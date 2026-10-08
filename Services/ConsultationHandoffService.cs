using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>No se llegó a mandar nada a la nube (falló antes de la instrucción).</summary>
public sealed class HandoffNotSentException : Exception
{
    public HandoffNotSentException(Exception inner) : base(inner.Message, inner) { }
}

/// <summary>
/// Consulta entre equipos (fase E): manda la consulta en curso de un equipo a otro por la tabla
/// ConsultationHandoffs de Turso. Un solo dueño a la vez:
/// <list type="bullet">
/// <item>Quien envía inserta el renglón "enviada" y deja su consulta en solo lectura.</item>
/// <item>Quien recibe la toma con un UPDATE condicionado a "enviada" y su ficha (AcceptToken);
/// solo uno puede ganar. La copia queda en la nube hasta que el que recibe la guarda en su equipo.</item>
/// <item>"Recuperar" (o resolver un envío dudoso) inserta o cancela el renglón en una sola instrucción:
/// un INSERT atrasado ya no puede revivirlo.</item>
/// <item>Un renglón terminado se borra solo cuando quien envió confirmó que lo vio (SenderAckAtUtc).</item>
/// </list>
/// La tabla no se replica: cada equipo la consulta directamente (una consulta pequeña).
/// </summary>
public sealed class ConsultationHandoffService
{
    public const int ProtocolVersion = 1;
    public const string StateSent = "enviada";
    public const string StateAccepted = "aceptada";
    public const string StateCancelled = "cancelada";

    /// <summary>Primera versión que entiende los envíos (un equipo más viejo no los vería).</summary>
    public static readonly Version MinimumAppVersion = new(1, 17, 0);

    /// <summary>Un equipo visto en este tiempo cuenta como "en línea".</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ForgottenDeviceAge = TimeSpan.FromDays(30);
    private const string ServerNow = TursoSyncService.ServerNowSql;

    private const string Columns = "Id, FromDeviceId, FromDeviceName, ToDeviceId, ToDeviceName, PatientSyncId, PatientName, Folio, " +
                                   "Payload, PayloadVersion, State, AcceptToken, CreatedAtUtc";

    public static readonly string[] HandoffTableDdl =
    {
        "CREATE TABLE IF NOT EXISTS ConsultationHandoffs (Id TEXT PRIMARY KEY NOT NULL, FromDeviceId TEXT NOT NULL, FromDeviceName TEXT NOT NULL, " +
        "ToDeviceId TEXT NOT NULL, ToDeviceName TEXT NOT NULL, PatientSyncId TEXT NOT NULL, PatientName TEXT NOT NULL, Folio TEXT NOT NULL, " +
        "Payload TEXT NOT NULL, PayloadVersion INTEGER NOT NULL, State TEXT NOT NULL, AcceptToken TEXT, SenderAckAtUtc TEXT, " +
        "CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL)",
        "CREATE INDEX IF NOT EXISTS IX_ConsultationHandoffs_To ON ConsultationHandoffs (ToDeviceId, State)"
    };

    private const string RegisteredDevicesDdl =
        "CREATE TABLE IF NOT EXISTS RegisteredDevices (DeviceId TEXT PRIMARY KEY, DeviceName TEXT NOT NULL, DeviceRole TEXT NOT NULL, " +
        "AppVersion TEXT NOT NULL, LastSeenUtc TEXT NOT NULL)";

    private readonly TursoSyncService _sync;
    private readonly TursoSettingsService _settings;
    private string? _ensuredUrl;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public ConsultationHandoffService(TursoSyncService sync, TursoSettingsService settings)
    {
        _sync = sync;
        _settings = settings;
    }

    /// <summary>Revisar la tabla sola cada pocos segundos (las pruebas la apagan y revisan a mano).</summary>
    public bool AutoPoll { get; set; } = true;

    public TursoSyncService Sync => _sync;

    private TursoSettings Settings => _settings.CurrentSettings;

    /// <summary>La nube está activada con URL y token: se puede enviar y recibir.</summary>
    public bool IsConfigured =>
        Settings.IsSyncEnabled && !string.IsNullOrWhiteSpace(Settings.DatabaseUrl) && !string.IsNullOrWhiteSpace(Settings.AuthToken);

    public string DeviceId => Settings.DeviceId;
    public string DeviceName => string.IsNullOrWhiteSpace(Settings.DeviceName) ? Environment.MachineName : Settings.DeviceName.Trim();
    public string DatabaseUrl => Settings.DatabaseUrl;

    private TursoClient CreateClient() => _sync.ClientFactory(Settings.DatabaseUrl, Settings.AuthToken);

    /// <summary>La tabla puede no existir todavía (ningún equipo ha sincronizado con esta versión).</summary>
    private async Task EnsureTablesAsync(TursoClient client, CancellationToken ct)
    {
        var url = Settings.DatabaseUrl;
        if (_ensuredUrl == url) return;
        await client.ExecuteBatchAsync(HandoffTableDdl.Append(RegisteredDevicesDdl).Select(sql => (sql, (object?[]?)null)), ct);
        _ensuredUrl = url;
    }

    // ================================================================== revisión periódica

    /// <summary>
    /// Una sola petición: hora del servidor, consultas dirigidas a este equipo (enviadas, o aceptadas
    /// aquí pero aún no guardadas), el estado del envío propio y, cada 30 s, "sigo en línea".
    /// </summary>
    public async Task<HandoffPollResult> PollAsync(string? outgoingId, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await EnsureTablesAsync(client, ct);

        var nowUtc = DateTime.UtcNow;
        var statements = new List<(string Sql, object?[]? Args)>
        {
            ($"SELECT {ServerNow} AS ServerNow", null),
            ($"SELECT {Columns} FROM ConsultationHandoffs WHERE ToDeviceId = ? AND (State = '{StateSent}' OR (State = '{StateAccepted}' AND Payload <> '')) " +
             "ORDER BY CreatedAtUtc DESC LIMIT 10", new object?[] { DeviceId })
        };

        int outgoingIndex = -1;
        if (!string.IsNullOrEmpty(outgoingId))
        {
            outgoingIndex = statements.Count;
            statements.Add(("SELECT State FROM ConsultationHandoffs WHERE Id = ?", new object?[] { outgoingId }));
        }

        bool heartbeat = nowUtc - _lastHeartbeatUtc >= HeartbeatInterval;
        if (heartbeat) statements.Add(HeartbeatStatement());

        bool cleanup = nowUtc - _lastCleanupUtc >= CleanupInterval;
        if (cleanup)
        {
            // Solo renglones terminados, sin copia y que quien envió ya vio (o muy viejos)
            statements.Add(($"DELETE FROM ConsultationHandoffs WHERE State <> '{StateSent}' AND Payload = '' AND " +
                            "((SenderAckAtUtc IS NOT NULL AND UpdatedAtUtc < strftime('%Y-%m-%d %H:%M:%f','now','-7 days')) " +
                            "OR UpdatedAtUtc < strftime('%Y-%m-%d %H:%M:%f','now','-365 days'))", null));
        }

        var results = await client.QueryBatchAsync(statements, ct);
        if (heartbeat) _lastHeartbeatUtc = nowUtc;
        if (cleanup) _lastCleanupUtc = nowUtc;

        var incoming = results[1].Select(ToRow).ToList();
        _sync.AddExternalUsage(rowsRead: 2 + incoming.Count + (outgoingIndex >= 0 ? 1 : 0), rowsWritten: heartbeat ? 1 : 0);

        return new HandoffPollResult
        {
            Success = true,
            ServerNowUtc = ParseUtc(results[0].FirstOrDefault()?["ServerNow"]),
            Incoming = incoming,
            OutgoingId = outgoingIndex >= 0 ? outgoingId : null,
            OutgoingState = outgoingIndex >= 0 ? results[outgoingIndex].FirstOrDefault()?["State"]?.ToString() ?? string.Empty : null
        };
    }

    private (string Sql, object?[]? Args) HeartbeatStatement() =>
        ($"INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc) VALUES (?, ?, ?, ?, {ServerNow}) " +
         "ON CONFLICT(DeviceId) DO UPDATE SET DeviceName = excluded.DeviceName, DeviceRole = excluded.DeviceRole, " +
         "AppVersion = excluded.AppVersion, LastSeenUtc = excluded.LastSeenUtc",
         new object?[] { DeviceId, DeviceName, Settings.DeviceRole.ToString(), TursoSyncService.AppVersionText });

    // ================================================================== equipos enlazados

    /// <summary>Otros equipos que pueden llevar la consulta (no el celular de administración).</summary>
    public async Task<IReadOnlyList<LinkedDevice>> GetLinkedDevicesAsync(CancellationToken ct = default)
    {
        using var client = CreateClient();
        await EnsureTablesAsync(client, ct);
        var results = await client.QueryBatchAsync(new (string, object?[]?)[]
        {
            HeartbeatStatement(),
            ($"SELECT {ServerNow} AS ServerNow", null),
            ("SELECT DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc FROM RegisteredDevices WHERE DeviceId <> ? ORDER BY LastSeenUtc DESC",
             new object?[] { DeviceId })
        }, ct);
        _lastHeartbeatUtc = DateTime.UtcNow;

        var serverNow = ParseUtc(results[1].FirstOrDefault()?["ServerNow"]) ?? DateTime.UtcNow;
        var devices = results[2]
            .Select(r => ToDevice(r, serverNow))
            .Where(d => d.Role != DeviceRole.AndroidMobileAdmin)
            .Where(d => d.LastSeenUtc == null || serverNow - d.LastSeenUtc.Value < ForgottenDeviceAge)
            .OrderByDescending(d => d.IsOnline)
            .ThenByDescending(d => d.LastSeenUtc)
            .ToList();
        _sync.AddExternalUsage(rowsRead: 1 + devices.Count, rowsWritten: 1);
        return devices;
    }

    public static LinkedDevice ToDevice(IReadOnlyDictionary<string, object?> row, DateTime serverNowUtc)
    {
        var roleText = row.GetValueOrDefault("DeviceRole")?.ToString();
        DeviceRole? role = Enum.TryParse<DeviceRole>(roleText, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
        var version = row.GetValueOrDefault("AppVersion")?.ToString() ?? string.Empty;
        var lastSeen = ParseUtc(row.GetValueOrDefault("LastSeenUtc"));
        bool online = lastSeen != null && serverNowUtc - lastSeen.Value < OnlineWindow;
        bool compatible = Version.TryParse(version, out var v) && v >= MinimumAppVersion;

        string status;
        if (!compatible)
        {
            status = $"Necesita TPVC {MinimumAppVersion.ToString(3)} o posterior (tiene {(string.IsNullOrWhiteSpace(version) ? "una versión anterior" : version)})";
        }
        else if (online)
        {
            status = $"En línea · {DescribeAgo(serverNowUtc - lastSeen!.Value)}";
        }
        else
        {
            status = lastSeen == null
                ? "Sin conexión reciente · la verá al abrir Pacientes"
                : $"Sin conexión desde {DescribeLocal(lastSeen.Value)} · la verá al abrir Pacientes";
        }

        return new LinkedDevice(
            row.GetValueOrDefault("DeviceId")?.ToString() ?? string.Empty,
            row.GetValueOrDefault("DeviceName")?.ToString() is { Length: > 0 } name ? name : "Equipo sin nombre",
            role,
            version,
            lastSeen,
            online,
            compatible,
            status);
    }

    private static string DescribeAgo(TimeSpan ago)
    {
        if (ago < TimeSpan.Zero) ago = TimeSpan.Zero;
        return ago.TotalSeconds < 60 ? $"hace {(int)ago.TotalSeconds} s" : $"hace {(int)ago.TotalMinutes} min";
    }

    private static string DescribeLocal(DateTime utc)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        if (local.Date == DateTime.Today) return $"hoy {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"ayer {local:HH:mm}";
        return local.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    }

    // ================================================================== quien envía

    /// <summary>Inserta el renglón "enviada". Lanza si falla (el que llama decide si fue definitivo).</summary>
    public async Task SendAsync(ConsultationHandoffMarker marker, string payloadJson, CancellationToken ct = default)
    {
        using var client = CreateClient();
        try
        {
            await EnsureTablesAsync(client, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HandoffNotSentException(ex);
        }

        await client.ExecuteAsync(
            "INSERT INTO ConsultationHandoffs (Id, FromDeviceId, FromDeviceName, ToDeviceId, ToDeviceName, PatientSyncId, PatientName, Folio, " +
            $"Payload, PayloadVersion, State, CreatedAtUtc, UpdatedAtUtc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, '{StateSent}', {ServerNow}, {ServerNow})",
            new object?[]
            {
                marker.Id, marker.FromDeviceId, marker.FromDeviceName, marker.ToDeviceId, marker.ToDeviceName,
                marker.PatientSyncId, marker.PatientName, marker.Folio, payloadJson, ProtocolVersion
            }, ct);
        _sync.AddExternalUsage(0, 1);
    }

    /// <summary>
    /// "Recuperar" y envíos dudosos: en una sola petición se inserta el renglón ya cancelado (si no
    /// existe, un INSERT atrasado fallará) o se cancela si sigue "enviada", y se lee cómo quedó.
    /// </summary>
    public async Task<HandoffResolution> ResolveOutgoingAsync(ConsultationHandoffMarker marker, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await EnsureTablesAsync(client, ct);
        var results = await client.QueryBatchAsync(new (string, object?[]?)[]
        {
            ("INSERT INTO ConsultationHandoffs (Id, FromDeviceId, FromDeviceName, ToDeviceId, ToDeviceName, PatientSyncId, PatientName, Folio, " +
             $"Payload, PayloadVersion, State, CreatedAtUtc, UpdatedAtUtc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, '', ?, '{StateCancelled}', {ServerNow}, {ServerNow}) " +
             $"ON CONFLICT(Id) DO UPDATE SET State = '{StateCancelled}', Payload = '', UpdatedAtUtc = {ServerNow} " +
             $"WHERE ConsultationHandoffs.State = '{StateSent}'",
             new object?[]
             {
                 marker.Id, marker.FromDeviceId, marker.FromDeviceName, marker.ToDeviceId, marker.ToDeviceName,
                 marker.PatientSyncId, marker.PatientName, marker.Folio, ProtocolVersion
             }),
            ("SELECT State FROM ConsultationHandoffs WHERE Id = ?", new object?[] { marker.Id })
        }, ct);
        _sync.AddExternalUsage(1, 1);

        return results[1].FirstOrDefault()?["State"]?.ToString() == StateAccepted
            ? HandoffResolution.Accepted
            : HandoffResolution.Cancelled;
    }

    /// <summary>Quien envió ya vio el final (el renglón se podrá borrar después de unos días).</summary>
    public async Task AcknowledgeAsync(string handoffId, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.ExecuteAsync($"UPDATE ConsultationHandoffs SET SenderAckAtUtc = {ServerNow} WHERE Id = ?", new object?[] { handoffId }, ct);
        _sync.AddExternalUsage(0, 1);
    }

    // ================================================================== quien recibe

    /// <summary>true si este intento (su ficha) se quedó con la consulta.</summary>
    public async Task<bool> TryAcceptAsync(string handoffId, string acceptToken, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var affected = await client.ExecuteAsync(
            $"UPDATE ConsultationHandoffs SET State = '{StateAccepted}', AcceptToken = ?, UpdatedAtUtc = {ServerNow} " +
            $"WHERE Id = ? AND ToDeviceId = ? AND State = '{StateSent}'",
            new object?[] { acceptToken, handoffId, DeviceId }, ct);
        _sync.AddExternalUsage(1, affected);
        return affected == 1;
    }

    /// <summary>Estado y ficha del que la tomó (para saber si un intento dudoso sí llegó).</summary>
    public async Task<(string? State, string? AcceptToken)> GetAcceptStateAsync(string handoffId, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var rows = await client.QueryAsync("SELECT State, AcceptToken FROM ConsultationHandoffs WHERE Id = ?", new object?[] { handoffId }, ct);
        _sync.AddExternalUsage(1, 0);
        var row = rows.FirstOrDefault();
        return (row?["State"]?.ToString(), row?.GetValueOrDefault("AcceptToken")?.ToString());
    }

    /// <summary>La consulta ya quedó guardada aquí: se borra la copia de la nube.</summary>
    public async Task ClearPayloadAsync(string handoffId, string acceptToken, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.ExecuteAsync(
            $"UPDATE ConsultationHandoffs SET Payload = '', UpdatedAtUtc = {ServerNow} WHERE Id = ? AND AcceptToken = ? AND State = '{StateAccepted}'",
            new object?[] { handoffId, acceptToken }, ct);
        _sync.AddExternalUsage(0, 1);
    }

    // ================================================================== errores

    /// <summary>La nube rechazó la instrucción o no se llegó a mandar: es seguro que no se aplicó.</summary>
    public static bool IsDefinitiveRejection(Exception ex)
    {
        if (ex is HandoffNotSentException or ArgumentException) return true;
        if (ex is not TursoException turso || turso.ErrorCode == null) return false;
        if (turso.ErrorCode is "HTTP_400" or "HTTP_401" or "HTTP_403" or "HTTP_404") return true;
        if (turso.ErrorCode.StartsWith("HTTP_", StringComparison.Ordinal) || turso.ErrorCode is "NETWORK_ERROR" or "TIMEOUT") return false;
        // Error de la instrucción (Hrana): no se aplicó, salvo "ya existe" (un intento anterior sí llegó)
        return !turso.ErrorCode.Contains("CONSTRAINT", StringComparison.OrdinalIgnoreCase)
               && !turso.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               && !turso.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Falla de red o de tiempo (no hay conexión con la nube en este momento).</summary>
    public static bool IsConnectivityFailure(Exception ex) =>
        ex is TursoException { ErrorCode: "NETWORK_ERROR" or "TIMEOUT" }
        || ex is HttpRequestException or IOException or OperationCanceledException or JsonException
        || ex is TursoException { ErrorCode: { } code } && code.StartsWith("HTTP_5", StringComparison.Ordinal)
        || ex is HandoffNotSentException { InnerException: { } inner } && IsConnectivityFailure(inner);

    /// <summary>Texto breve para el médico.</summary>
    public static string Describe(Exception ex) => IsConnectivityFailure(ex)
        ? "Sin conexión con la nube"
        : ex is TursoException { ErrorCode: "HTTP_401" or "HTTP_403" }
            ? "La nube rechazó el token (revise Configuración › Nube y dispositivos)"
            : ex.Message;

    // ================================================================== utilidades

    private static HandoffRow ToRow(Dictionary<string, object?> r) => new(
        r["Id"]?.ToString() ?? string.Empty,
        r["FromDeviceId"]?.ToString() ?? string.Empty,
        r["FromDeviceName"]?.ToString() ?? string.Empty,
        r["ToDeviceId"]?.ToString() ?? string.Empty,
        r["ToDeviceName"]?.ToString() ?? string.Empty,
        r["PatientSyncId"]?.ToString() ?? string.Empty,
        r["PatientName"]?.ToString() ?? string.Empty,
        r["Folio"]?.ToString() ?? string.Empty,
        r["Payload"]?.ToString() ?? string.Empty,
        int.TryParse(r["PayloadVersion"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0,
        r["State"]?.ToString() ?? string.Empty,
        r.GetValueOrDefault("AcceptToken")?.ToString(),
        ParseUtc(r["CreatedAtUtc"]) ?? DateTime.MinValue);

    private static DateTime? ParseUtc(object? raw)
    {
        var value = TursoSyncService.ParseDate(raw);
        return value == DateTime.MinValue ? null : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
