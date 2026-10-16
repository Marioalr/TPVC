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

    /// <summary>Paso 8.24: "sigo en línea" cada 60 s (antes 30 s), dentro de la revisión.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>La limpieza de renglones terminados y de latidos viejos: cada 6 h, solo la PC.</summary>
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    /// <summary>Paso 8.24: la lista de compañeros (quién puede recibir consultas) se vuelve a leer en una revisión cada 10 min.</summary>
    public static readonly TimeSpan PeerRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>Un equipo que no se ve en este tiempo ya no se ofrece para enviarle la consulta (ni cuenta como compañero).</summary>
    public static readonly TimeSpan ForgottenDeviceAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Paso 8.22: una PC con Windows vista en este tiempo con una revisión del contrato anterior a la de las marcas por campo
    /// (<see cref="SyncContract.FieldStampsRevision"/>) recibe el aviso de que sus cambios se combinan como fila completa.
    /// </summary>
    public static readonly TimeSpan OldPcWarningWindow = TimeSpan.FromDays(30);
    private const string ServerNow = TursoSyncService.ServerNowSql;

    private const string Columns = "Id, FromDeviceId, FromDeviceName, ToDeviceId, ToDeviceName, PatientSyncId, PatientName, Folio, " +
                                   "Payload, PayloadVersion, State, AcceptToken, CreatedAtUtc";

    /// <summary>
    /// Paso 8.24 (F2): lo que todavía le importa a quien recibe, una enviada o una aceptada con su copia. Es a la vez el WHERE
    /// del índice parcial y parte del WHERE de la revisión: SQLite usa el índice parcial solo si la consulta repite este texto.
    /// </summary>
    public const string PendingCondition = "State = '" + StateSent + "' OR (State = '" + StateAccepted + "' AND Payload <> '')";

    /// <summary>El índice parcial de F2: la revisión lee ~0 filas en lugar de todas las que se conservan para el equipo.</summary>
    public const string PendingIndexName = "IX_ConsultationHandoffs_Pending";

    /// <summary>
    /// La revisión: las consultas dirigidas a este equipo (enviadas, o aceptadas aquí y sin guardar todavía). Literal en
    /// contracts/hrana/statements.json ("check.incoming"): escrita de otra forma, SQLite no usaría el índice parcial.
    /// </summary>
    public const string IncomingSql = "SELECT " + Columns + " FROM ConsultationHandoffs WHERE ToDeviceId = ? AND (" + PendingCondition + ") " +
                                      "ORDER BY CreatedAtUtc DESC LIMIT 10";

    /// <summary>El estado del envío propio ("check.outgoing"), solo con uno pendiente.</summary>
    public const string OutgoingStateSql = "SELECT State FROM ConsultationHandoffs WHERE Id = ?";

    /// <summary>
    /// Paso 8.24 (A5): la marca de cambios. La última subida de cada uno de los otros equipos (LastPushUtc, con la hora de la
    /// nube); si alguna es posterior a la última bajada de este, hay algo que bajar ("check.peerPush"). Por equipo y no el
    /// máximo: una marca rara (en el futuro) de uno no esconde las subidas de los demás.
    /// </summary>
    public const string PeerPushSql = "SELECT DeviceId, LastPushUtc FROM RegisteredDevices WHERE DeviceId <> ? AND LastPushUtc IS NOT NULL;";

    public static readonly string[] HandoffTableDdl =
    {
        "CREATE TABLE IF NOT EXISTS ConsultationHandoffs (Id TEXT PRIMARY KEY NOT NULL, FromDeviceId TEXT NOT NULL, FromDeviceName TEXT NOT NULL, " +
        "ToDeviceId TEXT NOT NULL, ToDeviceName TEXT NOT NULL, PatientSyncId TEXT NOT NULL, PatientName TEXT NOT NULL, Folio TEXT NOT NULL, " +
        "Payload TEXT NOT NULL, PayloadVersion INTEGER NOT NULL, State TEXT NOT NULL, AcceptToken TEXT, SenderAckAtUtc TEXT, " +
        "CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL)",
        "CREATE INDEX IF NOT EXISTS IX_ConsultationHandoffs_To ON ConsultationHandoffs (ToDeviceId, State)",
        // Paso 8.24 (F2): aditivo; el de arriba se queda para las PC anteriores
        $"CREATE INDEX IF NOT EXISTS {PendingIndexName} ON ConsultationHandoffs (ToDeviceId) WHERE {PendingCondition}"
    };

    private readonly TursoSyncService _sync;
    private readonly TursoSettingsService _settings;
    private string? _ensuredUrl;
    // Paso 8.24: los plazos del latido, la lista de compañeros y la limpieza con el reloj monótono (null = todavía no)
    private TimeSpan? _lastHeartbeat;
    private TimeSpan? _lastCleanup;
    private TimeSpan? _lastPeers;
    private readonly long _origin;
    private string? _peersUrl;

    public ConsultationHandoffService(TursoSyncService sync, TursoSettingsService settings)
    {
        _sync = sync;
        _settings = settings;
        _origin = sync.Clock.GetTimestamp();
        Scheduler = new SyncScheduler(sync, this, settings);
    }

    /// <summary>
    /// Revisar y sincronizar solos con la cadencia del paso 8.24 (<see cref="SyncScheduler"/>). Las pruebas lo apagan y
    /// revisan a mano (o llaman <see cref="SyncScheduler.TickAsync"/> con un reloj simulado).
    /// </summary>
    public bool AutoPoll { get; set; } = true;

    /// <summary>Paso 8.24: la cadencia de la revisión, el latido y las sincronizaciones en segundo plano.</summary>
    public SyncScheduler Scheduler { get; }

    /// <summary>Tiempo monótono del reloj de la sincronización: mover la hora de Windows no cambia los plazos.</summary>
    private TimeSpan Mono => _sync.Clock.GetElapsedTime(_origin);

    private static bool Due(TimeSpan? last, TimeSpan now, TimeSpan interval) => last is not { } at || now - at >= interval;

    public TursoSyncService Sync => _sync;

    private TursoSettings Settings => _settings.CurrentSettings;

    /// <summary>La nube está activada con URL y token: se puede enviar y recibir.</summary>
    public bool IsConfigured =>
        Settings.IsSyncEnabled && !string.IsNullOrWhiteSpace(Settings.DatabaseUrl) && !string.IsNullOrWhiteSpace(Settings.AuthToken);

    public string DeviceId => Settings.DeviceId;
    public string DeviceName => string.IsNullOrWhiteSpace(Settings.DeviceName) ? Environment.MachineName : Settings.DeviceName.Trim();
    public string DatabaseUrl => Settings.DatabaseUrl;

    private TursoClient CreateClient() => _sync.ClientFactory(Settings.DatabaseUrl, Settings.AuthToken);

    /// <summary>
    /// Las tablas pueden no existir todavía, o no tener las columnas nuevas del latido (paso 8.10): el esquema completo de
    /// la nube, que solo crea o migra la PC (una sola consulta por sesión si ya está al día).
    /// </summary>
    private async Task EnsureTablesAsync(TursoClient client, CancellationToken ct)
    {
        var url = Settings.DatabaseUrl;
        if (_ensuredUrl == url) return;
        await _sync.EnsureRemoteSchemaAsync(client, ct);
        _ensuredUrl = url;
    }

    // ================================================================== revisión periódica

    /// <summary>
    /// La revisión, en una sola petición (paso 8.24, §3.3 del plan): consultas dirigidas a este equipo (enviadas, o aceptadas
    /// aquí pero aún no guardadas), el estado del envío propio, la marca de cambios de los otros equipos (su LastPushUtc) y,
    /// cuando toca, "sigo en línea" (cada 60 s), la lista de compañeros (cada 10 min) y la limpieza (cada 6 h, solo la PC).
    /// Lo que sirve a la cadencia lo recibe <see cref="Scheduler"/>.
    /// </summary>
    public async Task<HandoffPollResult> PollAsync(string? outgoingId, CancellationToken ct = default)
    {
        // Es automática: con la cuota agotada o el freno de consumo activo no se pregunta a la nube
        _sync.ThrowIfAutomaticSyncPaused();

        try
        {
            var result = await PollCoreAsync(outgoingId, ct);
            Scheduler.OnCheckSucceeded(result);
            return result;
        }
        catch (Exception ex) when (IsConnectivityFailure(ex))
        {
            Scheduler.OnCheckFailed();
            throw;
        }
    }

    private async Task<HandoffPollResult> PollCoreAsync(string? outgoingId, CancellationToken ct)
    {
        using var client = CreateClient();
        await EnsureTablesAsync(client, ct);

        var now = Mono;
        var deviceId = DeviceId;
        var statements = new List<(string Sql, object?[]? Args)>
        {
            (IncomingSql, new object?[] { deviceId }),
            (PeerPushSql, new object?[] { deviceId })
        };

        int outgoingIndex = -1;
        if (!string.IsNullOrEmpty(outgoingId))
        {
            outgoingIndex = statements.Count;
            statements.Add((OutgoingStateSql, new object?[] { outgoingId }));
        }

        bool heartbeat = Due(_lastHeartbeat, now, HeartbeatInterval);
        int heartbeatIndex = statements.Count;
        if (heartbeat) statements.AddRange(await _sync.HeartbeatStatementsAsync(Settings, afterPush: false, ct));

        // La lista de compañeros: al empezar, al cambiar de nube y cada 10 min (son tablas chicas, se leen completas)
        var url = Settings.DatabaseUrl;
        bool peers = _peersUrl != url || Due(_lastPeers, now, PeerRefreshInterval);
        int peersIndex = statements.Count;
        if (peers) statements.AddRange(PeerListQueries());

        bool cleanup = Due(_lastCleanup, now, CleanupInterval);
        if (cleanup)
        {
            // Solo renglones terminados, sin copia y que quien envió ya vio (o muy viejos)
            statements.Add(($"DELETE FROM ConsultationHandoffs WHERE State <> '{StateSent}' AND Payload = '' AND " +
                            "((SenderAckAtUtc IS NOT NULL AND UpdatedAtUtc < strftime('%Y-%m-%d %H:%M:%f','now','-7 days')) " +
                            "OR UpdatedAtUtc < strftime('%Y-%m-%d %H:%M:%f','now','-365 days'))", null));
            // Paso 8.10: el latido de los equipos quitados y de los que no se ven hace 60 días (solo la PC lo limpia)
            statements.Add((TursoSyncService.CleanupDevicesSql, null));
        }

        var results = await client.QueryBatchAsync(statements, ct);
        if (heartbeat) _lastHeartbeat = now;
        if (cleanup) _lastCleanup = now;
        if (heartbeat) await _sync.AfterHeartbeatAsync(client, deviceId, results[heartbeatIndex], ct);

        int? peerCount = null;
        int peerRows = 0;
        if (peers)
        {
            _peersUrl = url;
            _lastPeers = now;
            peerCount = CountPeers(results[peersIndex], results[peersIndex + 1], results[peersIndex + 2], deviceId);
            peerRows = results[peersIndex + 1].Count + results[peersIndex + 2].Count;
        }

        var incoming = results[0].Select(ToRow).ToList();
        var peerPushes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in results[1])
        {
            if (row.GetValueOrDefault("DeviceId")?.ToString() is { Length: > 0 } id && ParseUtc(row.GetValueOrDefault("LastPushUtc")) is { } pushed)
                peerPushes[id] = pushed;
        }
        _sync.AddExternalUsage(rowsRead: 1 + Math.Max(1, peerPushes.Count) + incoming.Count + (outgoingIndex >= 0 ? 1 : 0) + (heartbeat ? 1 : 0) + peerRows,
            rowsWritten: heartbeat ? 1 : 0);

        return new HandoffPollResult
        {
            Incoming = incoming,
            OutgoingState = outgoingIndex >= 0 ? results[outgoingIndex].FirstOrDefault()?["State"]?.ToString() ?? string.Empty : null,
            PeerPushes = peerPushes,
            HandoffPeerCount = peerCount,
            HadOutgoing = outgoingIndex >= 0
        };
    }

    /// <summary>
    /// Paso 8.24: la lista de compañeros dentro de una revisión: la hora de la nube, los latidos y DeviceRegistry ("check.peers.*"
    /// en contracts/hrana/statements.json).
    /// </summary>
    internal static IEnumerable<(string Sql, object?[]? Args)> PeerListQueries() => TursoSyncService.DeviceListQueries().Take(3);

    /// <summary>
    /// Compañeros: otros equipos que pueden recibir una consulta (los mismos que ofrece «Enviar a…»): no este, ni el celular,
    /// ni los quitados o sin aprobar, con una versión que recibe consultas y vistos en los últimos 30 días.
    /// </summary>
    private static int CountPeers(IReadOnlyList<Dictionary<string, object?>> serverNowRows, IReadOnlyList<Dictionary<string, object?>> heartbeats,
        IReadOnlyList<Dictionary<string, object?>> registry, string deviceId)
    {
        var serverNow = ParseUtc(serverNowRows.FirstOrDefault()?.GetValueOrDefault("ServerNow")) ?? DateTime.UtcNow;
        return HandoffTargets(MergeDevices(heartbeats, registry, primaryDeviceId: null, serverNow, deviceId), serverNow).Count(d => d.IsAvailable);
    }

    /// <summary>A quién se le puede enviar la consulta (y quién cuenta como compañero para la cadencia).</summary>
    internal static IEnumerable<LinkedDevice> HandoffTargets(IEnumerable<LinkedDevice> devices, DateTime serverNow) =>
        devices
            .Where(d => !d.IsThisDevice && !d.IsRevoked && !d.IsPendingApproval)
            .Where(d => d.Role is not { } role || DevicePermissions.CanTakeConsultations(role))
            // Con latido reciente: uno registrado cuyo latido ya se limpió no se ve hace más de 60 días
            .Where(d => d.LastSeenUtc != null && serverNow - d.LastSeenUtc.Value < ForgottenDeviceAge);

    // ================================================================== equipos enlazados

    /// <summary>
    /// Otros equipos que pueden llevar la consulta: no el celular, no los quitados ni los que la PC todavía no aprueba,
    /// ni los que no se ven hace 30 días.
    /// </summary>
    public async Task<IReadOnlyList<LinkedDevice>> GetLinkedDevicesAsync(CancellationToken ct = default)
    {
        using var client = CreateClient();
        await EnsureTablesAsync(client, ct);
        var deviceId = DeviceId;
        var heartbeat = await _sync.HeartbeatStatementsAsync(Settings, afterPush: false, ct);
        var results = await client.QueryBatchAsync(heartbeat.Concat(TursoSyncService.DeviceListQueries()), ct);
        _lastHeartbeat = Mono;
        await _sync.AfterHeartbeatAsync(client, deviceId, results[0], ct);

        var serverNow = ParseUtc(results[2].FirstOrDefault()?["ServerNow"]) ?? DateTime.UtcNow;
        var devices = HandoffTargets(MergeDevices(results[3], results[4], results[5].FirstOrDefault()?["Value"]?.ToString(), serverNow, deviceId), serverNow)
            .OrderByDescending(d => d.IsOnline)
            .ThenByDescending(d => d.LastSeenUtc)
            .ToList();
        _sync.AddExternalUsage(rowsRead: 2 + results[3].Count + results[4].Count, rowsWritten: 1);
        // Paso 8.24: la lista recién leída también sirve a la cadencia (un equipo recién vinculado acelera la revisión)
        Scheduler.OnPeersRefreshed(devices.Count(d => d.IsAvailable));
        Scheduler.OnCloudReachable();
        return devices;
    }

    /// <summary>
    /// Cruza los latidos (RegisteredDevices) con DeviceRegistry (paso 8.10). Un equipo con fila en DeviceRegistry toma de
    /// ahí su rol, su código y si está quitado; uno Android sin fila espera que la PC lo apruebe; una PC sin fila (anterior
    /// a v1.27.0) conserva el rol de su latido. Un equipo con fila y sin latido aparece sin conexión.
    /// </summary>
    public static List<LinkedDevice> MergeDevices(IEnumerable<Dictionary<string, object?>> heartbeats, IEnumerable<Dictionary<string, object?>> registry,
        string? primaryDeviceId, DateTime serverNowUtc, string thisDeviceId, DateTime? tokensRotatedAtUtc = null)
    {
        var registryById = registry
            .Where(r => r.GetValueOrDefault("DeviceId")?.ToString() is { Length: > 0 })
            .GroupBy(r => r["DeviceId"]!.ToString()!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var devices = new List<LinkedDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in heartbeats)
        {
            var id = row.GetValueOrDefault("DeviceId")?.ToString() ?? string.Empty;
            if (id.Length == 0 || !seen.Add(id)) continue;
            devices.Add(Describe(ToDevice(row, serverNowUtc, registryById.GetValueOrDefault(id)), id, primaryDeviceId, thisDeviceId, tokensRotatedAtUtc));
        }

        // Registrados sin latido (se limpió después de 60 días sin verse)
        foreach (var (id, entry) in registryById.Where(kv => !seen.Contains(kv.Key)))
        {
            var row = new Dictionary<string, object?>
            {
                ["DeviceId"] = id,
                ["DeviceName"] = entry.GetValueOrDefault("DisplayName"),
                ["DeviceRole"] = entry.GetValueOrDefault("Role"),
                ["AppVersion"] = string.Empty
            };
            devices.Add(Describe(ToDevice(row, serverNowUtc, entry), id, primaryDeviceId, thisDeviceId, tokensRotatedAtUtc));
        }
        return devices;

        static LinkedDevice Describe(LinkedDevice device, string id, string? primaryDeviceId, string thisDeviceId, DateTime? rotatedAt)
        {
            bool thisDevice = string.Equals(thisDeviceId, id, StringComparison.OrdinalIgnoreCase);
            return device with
            {
                IsPrimary = primaryDeviceId != null && string.Equals(primaryDeviceId, id, StringComparison.OrdinalIgnoreCase),
                IsThisDevice = thisDevice,
                MergesAsWholeRow = device.MergesAsWholeRow && !thisDevice,
                IgnoresServiceSupplies = device.IgnoresServiceSupplies && !thisDevice,
                // Paso 8.15: no se ha conectado desde que se anularon los tokens (sin latido, tampoco)
                TokensRotatedAtUtc = !thisDevice && !device.IsRevoked && !device.IsPendingApproval
                                     && rotatedAt is { } at && (device.LastSeenUtc == null || device.LastSeenUtc < at)
                    ? rotatedAt
                    : null
            };
        }
    }

    /// <summary>
    /// Un latido (con su fila de DeviceRegistry, si tiene). Recibe consultas si su HandoffProtocol es 1 o más (así la app
    /// de Android numera sus versiones desde 1.0.0); si está vacío (PC v1.17–v1.26), si su AppVersion es 1.17.0 o más.
    /// </summary>
    public static LinkedDevice ToDevice(IReadOnlyDictionary<string, object?> row, DateTime serverNowUtc, IReadOnlyDictionary<string, object?>? registry = null)
    {
        var platform = row.GetValueOrDefault("Platform")?.ToString();
        var roleText = registry?.GetValueOrDefault("Role")?.ToString() ?? row.GetValueOrDefault("DeviceRole")?.ToString();
        DeviceRole? role = Enum.TryParse<DeviceRole>(roleText, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
        bool pendingApproval = registry == null && string.Equals(platform, TursoSyncService.AndroidPlatform, StringComparison.OrdinalIgnoreCase);
        var revokedAt = registry == null ? null : ParseUtc(registry.GetValueOrDefault("RevokedAtUtc"));
        var version = row.GetValueOrDefault("AppVersion")?.ToString() ?? string.Empty;
        var lastSeen = ParseUtc(row.GetValueOrDefault("LastSeenUtc"));
        bool online = lastSeen != null && serverNowUtc - lastSeen.Value < OnlineWindow;
        int? handoffProtocol = ParseInt(row.GetValueOrDefault("HandoffProtocol"));
        bool compatible = handoffProtocol is { } protocol
            ? protocol >= ProtocolVersion
            : Version.TryParse(version, out var v) && v >= MinimumAppVersion;

        string status;
        if (revokedAt != null)
        {
            status = "Quitado de los equipos vinculados";
        }
        else if (pendingApproval)
        {
            status = "Esperando que la PC lo vincule";
        }
        else if (!compatible)
        {
            status = handoffProtocol != null
                ? $"Su versión ({(string.IsNullOrWhiteSpace(version) ? "anterior" : version)}) no recibe consultas de otros equipos"
                : $"Necesita TPVC {MinimumAppVersion.ToString(3)} o posterior (tiene {(string.IsNullOrWhiteSpace(version) ? "una versión anterior" : version)})";
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

        var name = registry?.GetValueOrDefault("DisplayName")?.ToString() is { Length: > 0 } registered
            ? registered
            : row.GetValueOrDefault("DeviceName")?.ToString() is { Length: > 0 } heartbeatName ? heartbeatName : "Equipo sin nombre";
        // Paso 8.22: una PC (Windows, o sin plataforma: v1.19–v1.26) que todavía no marca cada campo del expediente
        var contractRevision = ParseInt(row.GetValueOrDefault("ContractRevision"));
        bool windows = platform == null
            ? role is not (DeviceRole.AndroidTablet or DeviceRole.AndroidMobileAdmin)
            : string.Equals(platform, TursoSyncService.WindowsPlatform, StringComparison.OrdinalIgnoreCase);
        bool mergesAsWholeRow = windows && revokedAt == null && lastSeen != null && serverNowUtc - lastSeen.Value < OldPcWarningWindow
                                && (contractRevision ?? 0) < SyncContract.FieldStampsRevision;
        // Pedido del 2026-10-14: un equipo al que se le puede enviar la consulta, con una versión que no conoce los insumos
        // ajustados de un servicio (los ignora y, al quitar el servicio, descuenta los de siempre)
        bool ignoresServiceSupplies = compatible && revokedAt == null && !pendingApproval
                                      && (contractRevision ?? 0) < SyncContract.ServiceSuppliesRevision;

        return new LinkedDevice(
            row.GetValueOrDefault("DeviceId")?.ToString() ?? string.Empty,
            name,
            role,
            version,
            lastSeen,
            online,
            compatible && revokedAt == null && !pendingApproval,
            status)
        {
            Platform = platform,
            ContractRevision = contractRevision,
            MergesAsWholeRow = mergesAsWholeRow,
            IgnoresServiceSupplies = ignoresServiceSupplies,
            DeviceCode = registry?.GetValueOrDefault("DeviceCode")?.ToString(),
            IsRevoked = revokedAt != null,
            IsPendingApproval = pendingApproval,
            PendingCount = ParseInt(row.GetValueOrDefault("PendingCount")),
            OldestPendingUtc = ParseUtc(row.GetValueOrDefault("OldestPendingUtc")),
            SeenText = online ? "En línea" : lastSeen == null ? "Sin latido todavía" : $"Visto {DescribeLongAgo(serverNowUtc - lastSeen.Value)}"
        };
    }

    private static int? ParseInt(object? raw) =>
        raw switch
        {
            null => null,
            long l => (int)l,
            int i => i,
            _ => int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null
        };

    private static string DescribeAgo(TimeSpan ago)
    {
        if (ago < TimeSpan.Zero) ago = TimeSpan.Zero;
        return ago.TotalSeconds < 60 ? $"hace {(int)ago.TotalSeconds} s" : $"hace {(int)ago.TotalMinutes} min";
    }

    /// <summary>"hace 5 min", "hace 3 h", "hace 2 días" (para la lista de Equipos vinculados).</summary>
    public static string DescribeLongAgo(TimeSpan ago)
    {
        if (ago < TimeSpan.Zero) ago = TimeSpan.Zero;
        if (ago.TotalMinutes < 1) return "hace un momento";
        if (ago.TotalHours < 1) return $"hace {(int)ago.TotalMinutes} min";
        if (ago.TotalDays < 1) return $"hace {(int)ago.TotalHours} h";
        return (int)ago.TotalDays == 1 ? "hace 1 día" : $"hace {(int)ago.TotalDays} días";
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
        Scheduler.OnCloudReachable(); // paso 8.24: la nube responde
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
        Scheduler.OnCloudReachable(); // paso 8.24: la nube responde

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
        Scheduler.OnCloudReachable(); // paso 8.24: la nube responde
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
        Scheduler.OnCloudReachable(); // paso 8.24: la nube responde
        return affected == 1;
    }

    /// <summary>Estado y ficha del que la tomó (para saber si un intento dudoso sí llegó).</summary>
    public async Task<(string? State, string? AcceptToken)> GetAcceptStateAsync(string handoffId, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var rows = await client.QueryAsync("SELECT State, AcceptToken FROM ConsultationHandoffs WHERE Id = ?", new object?[] { handoffId }, ct);
        _sync.AddExternalUsage(1, 0);
        Scheduler.OnCloudReachable(); // paso 8.24: la nube responde
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
        Scheduler.OnCloudReachable(); // paso 8.24: la nube responde
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
    public static string Describe(Exception ex) => ex switch
    {
        _ when IsConnectivityFailure(ex) => "Sin conexión con la nube",
        TursoException { ErrorCode: "HTTP_401" or "HTTP_403" } => "La nube rechazó el token (revise Configuración › Nube y dispositivos)",
        TursoException { ErrorCode: TursoException.QuotaExhaustedCode } => "Se agotó la cuota mensual de Turso (se reintenta cada hora)",
        TursoException { ErrorCode: TursoException.ConsumptionBrakeCode } => "Sincronización pausada: consumo anormal (Configuración › Nube y dispositivos)",
        TursoException { ErrorCode: TursoException.ArchivedCode } => "La base de Turso está archivada (Configuración › Nube y dispositivos)",
        _ => ex.Message
    };

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
