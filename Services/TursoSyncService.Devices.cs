using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services.Pairing;

namespace TPVC.Services;

/// <summary>
/// Paso 8.10 del plan de la Fase 8: el latido de este equipo en RegisteredDevices (con sus cambios pendientes y su
/// InstanceNonce), el registro de equipos (DeviceRegistry: rol, código y revocación), la PC principal
/// (_tpvc_meta.primaryDeviceId) y la detección de otra instalación con el mismo DeviceId.
/// </summary>
public partial class TursoSyncService
{
    public const string WindowsPlatform = "Windows";
    public const string AndroidPlatform = "Android";

    /// <summary>Motivo de la revocación al quitar un equipo desde la lista.</summary>
    public const string RevokedRemovedReason = "quitado";

    /// <summary>Motivo de la revocación de la PC principal anterior («Esta PC reemplaza a la principal»).</summary>
    public const string RevokedReplacedReason = "reemplazada";

    /// <summary>
    /// Motivo de un equipo nuevo que la PC no aprobó (paso 8.14). Su fila lleva un código X (X1, X2…): los códigos T y C son
    /// para los folios de los equipos vinculados.
    /// </summary>
    public const string RevokedRejectedReason = "rechazado";

    public const char RejectedCodeLetter = 'X';

    /// <summary>
    /// Motivo de «Perdí este equipo» (paso 8.15). El equipo Android que lo lee borra su base, su credencial y sus borradores
    /// sin subir nada; una PC no se borra. El corte de verdad es anular los tokens.
    /// </summary>
    public const string RevokedLostReason = "perdido";

    /// <summary>
    /// La clave de _tpvc_meta con la hora de la nube en que una PC sincronizó por primera vez con un token nuevo después de
    /// que se anularon (paso 8.15). Un equipo que no se ha conectado desde entonces necesita el token nuevo.
    /// </summary>
    public const string TokensRotatedKey = "tokensRotatedAtUtc";

    /// <summary>
    /// Anota la rotación, salvo que la nube ya tenga una marca posterior a la última sincronización buena con el token
    /// anulado (?): esa es la misma rotación, anotada por otro equipo, y moverla haría pedir el token a los que ya lo tienen.
    /// </summary>
    internal static readonly string TokensRotatedSql =
        $"INSERT INTO {Q(MetaTable)} (\"Key\", \"Value\") VALUES ('{TokensRotatedKey}', {ServerNowSql}) " +
        $"ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\" WHERE {Q(MetaTable)}.\"Value\" < ?;";

    /// <summary>La clave de _tpvc_meta con el DeviceId de la PC principal.</summary>
    public const string PrimaryDeviceKey = "primaryDeviceId";

    /// <summary>La limpieza de cada 6 h borra del latido a los equipos que no se ven en este tiempo.</summary>
    public const int ForgottenDeviceDays = 60;

    /// <summary>
    /// Borra del latido a los equipos quitados y a los que no se ven hace 60 días (cada revinculación o reinstalación
    /// agrega un DeviceId, y la revisión de consultas lee la tabla completa). Solo la PC, cada 6 h.
    /// </summary>
    public static readonly string CleanupDevicesSql =
        $"DELETE FROM RegisteredDevices WHERE LastSeenUtc < strftime('%Y-%m-%d %H:%M:%f','now','-{ForgottenDeviceDays} days') " +
        $"OR DeviceId IN (SELECT DeviceId FROM {SyncSchema.DeviceRegistryTable} WHERE RevokedAtUtc IS NOT NULL)";

    internal const string DuplicateDeviceNotice =
        "Otra computadora usaba la misma identidad que esta para la nube (se copió la carpeta de TPVC o la computadora completa), " +
        "así que cada una dejaba de recibir lo que subía la otra. Esta tomó una identidad nueva y vuelve a bajar todo una vez. " +
        "Si esta es la PC principal, pulse «Esta PC reemplaza a la principal» en Equipos vinculados.";

    /// <summary>Aleatorio en cada arranque: si el latido lee otro en su fila, otra instalación usa el mismo DeviceId.</summary>
    public string InstanceNonce { get; } = Guid.NewGuid().ToString("N");

    // La URL donde este proceso ya escribió su InstanceNonce, y donde ya revisó su fila de DeviceRegistry
    private string? _nonceWrittenUrl;
    private string? _registryCheckedUrl;

    /// <summary>Este equipo en DeviceRegistry y la PC principal, según la última consulta (null si no se ha consultado).</summary>
    public DeviceRegistration? Registration { get; private set; }

    /// <summary>Cambios que este equipo todavía no sube: lo anotado después del cursor y lo apartado al subir.</summary>
    public sealed record PendingChanges(int Count, DateTime? OldestUtc);

    internal sealed class PendingEntry
    {
        public string EntityType { get; set; } = string.Empty;
        public string SyncId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lo que va en PendingCount y OldestPendingUtc del latido (para «Quitar» y, en el paso 8.20, la barrera de los
    /// saldos). La fecha es la marca más vieja de esas filas o lápidas, o cuándo se apartó una fila.
    /// </summary>
    public async Task<PendingChanges> GetPendingChangesAsync(CancellationToken ct = default)
    {
        using var db = _contextFactory.CreateDbContext();
        var cursor = await SyncChangeLog.ReadPushCursorAsync(db, ct) ?? 0;
        var logged = await db.Database.SqlQueryRaw<PendingEntry>(
                $"SELECT \"EntityType\", \"SyncId\" FROM {Q(SyncChangeLog.Table)} WHERE \"Seq\" > {{0}}", cursor)
            .ToListAsync(ct);
        var parked = await db.SyncParkedRows.AsNoTracking()
            .Where(p => p.Direction == SyncParkedRow.Push)
            .Select(p => new { p.EntityType, p.SyncId, p.FirstParkedAtUtc })
            .ToListAsync(ct);

        var keys = logged.Select(e => (e.EntityType, e.SyncId.ToUpperInvariant())).ToHashSet();
        int count = keys.Count + parked.Count(p => !keys.Contains((p.EntityType, FormatGuid(p.SyncId).ToUpperInvariant())));
        if (count == 0) return new PendingChanges(0, null);

        var stamps = new List<DateTime>(parked.Select(p => DateTime.SpecifyKind(p.FirstParkedAtUtc, DateTimeKind.Utc)));
        foreach (var type in logged.Select(e => e.EntityType).Distinct(StringComparer.Ordinal))
        {
            if (GetTableMaps().FirstOrDefault(m => m.ClrType.Name == type) is not { } map) continue;
            stamps.AddRange(await MinStampAsync(db,
                $"SELECT min(\"UpdatedAtUtc\") AS Value FROM {Q(map.Table)} WHERE upper(\"SyncId\") IN " +
                $"(SELECT \"SyncId\" FROM {Q(SyncChangeLog.Table)} WHERE \"EntityType\" = {{0}} AND \"Seq\" > {{1}})", ct, type, cursor));
        }
        stamps.AddRange(await MinStampAsync(db,
            $"SELECT min(t.\"DeletedAtUtc\") AS Value FROM {Q(TombstonesTable)} AS t WHERE upper(t.\"SyncId\") IN " +
            $"(SELECT c.\"SyncId\" FROM {Q(SyncChangeLog.Table)} AS c WHERE c.\"EntityType\" = t.\"EntityType\" AND c.\"Seq\" > {{0}})", ct, cursor));
        return new PendingChanges(count, stamps.Count > 0 ? stamps.Min() : null);
    }

    private static async Task<IEnumerable<DateTime>> MinStampAsync(AppDbContext db, string sql, CancellationToken ct, params object[] args)
    {
        var value = (await db.Database.SqlQueryRaw<string?>(sql, args).ToListAsync(ct)).FirstOrDefault();
        var parsed = ParseDate(value);
        return parsed == DateTime.MinValue ? Array.Empty<DateTime>() : new[] { DateTime.SpecifyKind(parsed, DateTimeKind.Utc) };
    }

    // =========================================================================
    // LATIDO
    // =========================================================================

    private static readonly string[] HeartbeatColumns =
    {
        "DeviceId", "DeviceName", "DeviceRole", "AppVersion", "Platform", "SyncProtocol", "HandoffProtocol", "ContractRevision",
        "PendingCount", "OldestPendingUtc", "InstanceNonce"
    };

    /// <summary>
    /// El upsert del latido, con columnas explícitas (nunca INSERT OR REPLACE: no borra las que agregue otra versión) y la
    /// hora de la nube en LastSeenUtc. Al final de una subida escribe además LastPushUtc en la misma instrucción.
    /// </summary>
    internal static string HeartbeatSql(bool afterPush)
    {
        var timed = afterPush ? new[] { "LastSeenUtc", "LastPushUtc" } : new[] { "LastSeenUtc" };
        var columns = HeartbeatColumns.Concat(timed).ToList();
        var values = HeartbeatColumns.Select(_ => "?").Concat(timed.Select(_ => ServerNowSql));
        return $"INSERT INTO RegisteredDevices ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)}) " +
               "ON CONFLICT(DeviceId) DO UPDATE SET " +
               string.Join(", ", columns.Skip(1).Select(c => $"{c} = excluded.{c}")) + ";";
    }

    internal static string DeviceNameOf(TursoSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DeviceName) ? Environment.MachineName : settings.DeviceName.Trim();

    /// <summary>
    /// El latido de este equipo en dos instrucciones: primero lee el InstanceNonce que tiene su fila y luego la escribe.
    /// Después de ejecutarlas, <see cref="AfterHeartbeatAsync"/> revisa la primera.
    /// </summary>
    internal async Task<(string Sql, object?[]? Args)[]> HeartbeatStatementsAsync(TursoSettings settings, bool afterPush, CancellationToken ct)
    {
        var pending = await GetPendingChangesAsync(ct);
        return new (string, object?[]?)[]
        {
            ("SELECT InstanceNonce FROM RegisteredDevices WHERE DeviceId = ?;", new object?[] { settings.DeviceId }),
            (HeartbeatSql(afterPush), new object?[]
            {
                settings.DeviceId, DeviceNameOf(settings), settings.DeviceRole.ToString(), AppVersionText, WindowsPlatform,
                ClientProtocol, ConsultationHandoffService.ProtocolVersion, SyncContract.Revision,
                pending.Count, pending.OldestUtc is { } oldest ? FormatDate(oldest) : null, InstanceNonce
            })
        };
    }

    /// <summary>
    /// Si este proceso ya había escrito su InstanceNonce en esa nube y la fila trae otro, otra instalación escribió con el
    /// mismo DeviceId: se le regresa su nonce (se queda con el DeviceId) y este equipo toma uno nuevo. El primer latido de
    /// cada arranque no revisa: su fila trae el nonce del arranque anterior.
    /// </summary>
    internal async Task AfterHeartbeatAsync(TursoClient client, string deviceId, IReadOnlyList<Dictionary<string, object?>> before, CancellationToken ct)
    {
        var url = client.DatabaseUrl;
        var previous = before.FirstOrDefault()?.GetValueOrDefault("InstanceNonce")?.ToString();
        bool duplicate = _nonceWrittenUrl == url && !string.IsNullOrEmpty(previous) && previous != InstanceNonce
                         && string.Equals(deviceId, _settingsService.CurrentSettings.DeviceId, StringComparison.Ordinal);
        _nonceWrittenUrl = url;
        if (!duplicate) return;

        AppLogger.Warn($"Turso: otra instalación escribe con el DeviceId {deviceId} (su InstanceNonce aparece en el latido).");
        try
        {
            await client.ExecuteAsync("UPDATE RegisteredDevices SET InstanceNonce = ? WHERE DeviceId = ? AND InstanceNonce = ?;",
                new object?[] { previous, deviceId, InstanceNonce }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Si no se pudo, la otra también tomará uno nuevo: tampoco se mezclan
            AppLogger.Warn($"Turso: no se pudo regresar el InstanceNonce a la otra instalación: {ex.Message}");
        }
        TakeNewDeviceId(DuplicateDeviceNotice);
    }

    /// <summary>Un DeviceId nuevo: se registra de nuevo y la siguiente sincronización baja todo una vez.</summary>
    private void TakeNewDeviceId(string notice)
    {
        using (var db = _contextFactory.CreateDbContext()) DbMeta.Set(db, DbMeta.RedownloadAll, "1");
        _registeredDeviceUrl = null;
        _registryCheckedUrl = null;
        _nonceWrittenUrl = null;
        Registration = null;
        _settingsService.RegenerateDeviceId(notice);
    }

    /// <summary>Al empezar una sincronización: si este equipo cambió de DeviceId por un duplicado, baja todo desde cero.</summary>
    private async Task ApplyRedownloadRequestAsync(TursoSettings settings, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        if (await DbMeta.GetAsync(db, DbMeta.RedownloadAll, ct) != "1") return;
        DbMeta.RemoveByPrefix(db, DbMeta.PullCursorPrefix);
        settings.LastPulledServerWatermark = null;
        DbMeta.Remove(db, DbMeta.RedownloadAll);
        AppLogger.Info("Turso: este equipo cambió de DeviceId; se baja todo una vez.");
    }

    /// <summary>El latido al empezar la primera sincronización de cada sesión.</summary>
    private async Task RegisterDeviceAsync(TursoClient client, TursoSettings settings, CancellationToken ct)
    {
        var statements = await HeartbeatStatementsAsync(settings, afterPush: false, ct);
        var results = await client.QueryBatchAsync(statements, ct);
        await AfterHeartbeatAsync(client, settings.DeviceId, results[0], ct);
    }

    /// <summary>Al final de cada subida que mandó algo: LastPushUtc y lo que quedó pendiente, con la hora de la nube.</summary>
    private async Task<int> HeartbeatAfterPushAsync(TursoClient client, CancellationToken ct)
    {
        var settings = _settingsService.CurrentSettings;
        var deviceId = settings.DeviceId;
        var results = await client.QueryBatchAsync(await HeartbeatStatementsAsync(settings, afterPush: true, ct), ct);
        await AfterHeartbeatAsync(client, deviceId, results[0], ct);
        return 1;
    }

    // =========================================================================
    // REGISTRO DE EQUIPOS Y PC PRINCIPAL
    // =========================================================================

    /// <summary>El siguiente código de una letra: el mayor registrado + 1 (nunca se reutiliza, ni el de un quitado).</summary>
    private static string NextCodeSql(char letter) =>
        $"'{letter}' || (ifnull(max(CAST(substr(DeviceCode, 2) AS INTEGER)), 0) + 1)";

    /// <summary>Esta PC en DeviceRegistry: la primera vez recibe el siguiente código P; después solo se actualiza su nombre.</summary>
    private static (string Sql, object?[]? Args) RegisterThisPcStatement(TursoSettings settings) =>
        ($"INSERT INTO {SyncSchema.DeviceRegistryTable} (DeviceId, DeviceCode, Role, DisplayName, Model, PairedAtUtc) " +
         $"SELECT ?, {NextCodeSql('P')}, '{DeviceRole.WindowsDesktop}', ?, ?, {ServerNowSql} FROM {SyncSchema.DeviceRegistryTable} WHERE DeviceCode LIKE 'P%' " +
         "ON CONFLICT(DeviceId) DO UPDATE SET DisplayName = excluded.DisplayName, Model = excluded.Model " +
         $"WHERE {SyncSchema.DeviceRegistryTable}.RevokedAtUtc IS NULL;",
         new object?[] { settings.DeviceId, DeviceNameOf(settings), RuntimeInformation.OSDescription });

    internal static (string Sql, object?[]? Args)[] RegistrationQueries(string deviceId) => new (string, object?[]?)[]
    {
        ($"SELECT DeviceCode, RevokedAtUtc, RevokedReason FROM {SyncSchema.DeviceRegistryTable} WHERE DeviceId = ?;", new object?[] { deviceId }),
        ($"SELECT p.\"Value\" AS PrimaryDeviceId, g.DisplayName AS PrimaryName, d.DeviceName AS PrimaryHeartbeatName, d.LastSeenUtc AS PrimaryLastSeenUtc " +
         $"FROM {Q(MetaTable)} AS p LEFT JOIN {SyncSchema.DeviceRegistryTable} AS g ON g.DeviceId = p.\"Value\" " +
         $"LEFT JOIN RegisteredDevices AS d ON d.DeviceId = p.\"Value\" WHERE p.\"Key\" = '{PrimaryDeviceKey}';", null)
    };

    private DeviceRegistration ReadRegistration(string deviceId, List<Dictionary<string, object?>> own, List<Dictionary<string, object?>> primary)
    {
        var row = own.FirstOrDefault();
        var p = primary.FirstOrDefault();
        var registration = new DeviceRegistration(
            deviceId,
            row?.GetValueOrDefault("DeviceCode")?.ToString(),
            ParseUtcOrNull(row?.GetValueOrDefault("RevokedAtUtc")),
            row?.GetValueOrDefault("RevokedReason")?.ToString(),
            p?.GetValueOrDefault("PrimaryDeviceId")?.ToString(),
            (p?.GetValueOrDefault("PrimaryName") ?? p?.GetValueOrDefault("PrimaryHeartbeatName"))?.ToString(),
            ParseUtcOrNull(p?.GetValueOrDefault("PrimaryLastSeenUtc")));

        var settings = _settingsService.CurrentSettings;
        if (settings.DeviceId == deviceId && settings.DeviceCode != registration.DeviceCode)
        {
            settings.DeviceCode = registration.DeviceCode;
            _settingsService.SaveSettings(settings);
        }
        return registration;
    }

    private static DateTime? ParseUtcOrNull(object? raw)
    {
        var value = ParseDate(raw);
        return value == DateTime.MinValue ? null : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    /// <summary>
    /// Una vez por sesión, al final de una sincronización con la nube ya verificada (esta base tiene su clinicId): reclama
    /// la PC principal si nadie la tiene (INSERT … DO NOTHING) y registra esta PC con el siguiente código P. Una PC sin
    /// identidad de la nube todavía no escribe nada aquí.
    /// </summary>
    private async Task<int> EnsureDeviceRegistryAsync(TursoClient client, TursoSettings settings, CancellationToken ct)
    {
        if (_registryCheckedUrl == client.DatabaseUrl) return 0;
        using (var db = _contextFactory.CreateDbContext())
        {
            if (await DbMeta.GetAsync(db, DbMeta.ClinicId, ct) == null) return 0;
        }

        var deviceId = settings.DeviceId;
        var statements = new List<(string, object?[]?)>
        {
            ($"INSERT INTO {Q(MetaTable)} (\"Key\", \"Value\") VALUES ('{PrimaryDeviceKey}', ?) ON CONFLICT(\"Key\") DO NOTHING;", new object?[] { deviceId }),
            RegisterThisPcStatement(settings)
        };
        statements.AddRange(RegistrationQueries(deviceId));
        var results = await client.TransactionAsync(statements, ct);
        Registration = ReadRegistration(deviceId, results[^2].Rows, results[^1].Rows);
        _registryCheckedUrl = client.DatabaseUrl;
        if (Registration.IsRevoked) AppLogger.Warn($"Turso: esta PC está quitada de los equipos vinculados ({Registration.RevokedReason}).");
        return results.Take(2).Sum(r => r.AffectedRows);
    }

    /// <summary>
    /// «Esta PC reemplaza a la principal»: para una PC nueva que recuperó la nube (o la que se quedó con otra identidad).
    /// En una transacción: se registra, quita a la principal anterior (motivo "reemplazada") y toma la clave.
    /// </summary>
    public async Task<(bool Ok, string Message)> MakeThisThePrimaryAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5), ct)) return (false, "Ya hay una sincronización en curso. Intente en un momento.");
        try
        {
            var (client, problem) = await OpenVerifiedCloudAsync(settings, ct);
            if (client == null) return (false, problem!);
            using (client)
            {
                var deviceId = settings.DeviceId;
                var statements = new List<(string, object?[]?)>
                {
                    RegisterThisPcStatement(settings),
                    // Una PC que se había quitado y vuelve a ser la principal deja de estar quitada
                    ($"UPDATE {SyncSchema.DeviceRegistryTable} SET RevokedAtUtc = NULL, RevokedReason = NULL WHERE DeviceId = ? AND RevokedAtUtc IS NOT NULL;",
                        new object?[] { deviceId }),
                    ($"UPDATE {SyncSchema.DeviceRegistryTable} SET RevokedAtUtc = {ServerNowSql}, RevokedReason = '{RevokedReplacedReason}' " +
                     $"WHERE DeviceId = (SELECT \"Value\" FROM {Q(MetaTable)} WHERE \"Key\" = '{PrimaryDeviceKey}') AND DeviceId <> ? AND RevokedAtUtc IS NULL;",
                        new object?[] { deviceId }),
                    ($"INSERT INTO {Q(MetaTable)} (\"Key\", \"Value\") VALUES ('{PrimaryDeviceKey}', ?) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";",
                        new object?[] { deviceId })
                };
                statements.AddRange(RegistrationQueries(deviceId));
                var results = await client.TransactionAsync(statements, ct);
                Registration = ReadRegistration(deviceId, results[^2].Rows, results[^1].Rows);
                _registryCheckedUrl = client.DatabaseUrl;
                _settingsService.RecordUsage(4, results.Take(4).Sum(r => r.AffectedRows));
                AppLogger.Info($"Turso: esta PC ({Registration.DeviceCode}) es ahora la principal.");
                return (true, results[2].AffectedRows > 0
                    ? "✓ Esta PC es ahora la principal; la anterior quedó quitada de los equipos vinculados."
                    : "✓ Esta PC es ahora la principal.");
            }
        }
        catch (TursoException ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// «Quitar» un equipo de la lista: pone RevokedAtUtc y el motivo "quitado" (la fila nunca se borra). Un equipo sin
    /// fila (una PC anterior a v1.27.0) la recibe ya quitada, con el siguiente código de su letra. No se quita a esta PC
    /// ni a la principal.
    /// </summary>
    public Task<(bool Ok, string Message)> RemoveDeviceAsync(LinkedDevice device, CancellationToken ct = default) =>
        RevokeDeviceAsync(device, RevokedRemovedReason, $"✓ Se quitó «{device.DeviceName}» de los equipos vinculados.", ct);

    /// <summary>
    /// «Perdí este equipo» (paso 8.15): lo revoca con el motivo "perdido" y espera la rotación de los tokens; el primer token
    /// nuevo que funcione en esta PC se anota en la nube. Revocar no corta el acceso: el equipo perdido tiene el token.
    /// </summary>
    public async Task<(bool Ok, string Message)> ReportLostDeviceAsync(LinkedDevice device, CancellationToken ct = default)
    {
        var result = await RevokeDeviceAsync(device, RevokedLostReason,
            $"✓ «{device.DeviceName}» quedó como perdido. Falta anular los tokens para cortarle el acceso (vea los pasos).", ct);
        if (result.Ok) _settingsService.ExpectTokenRotation();
        return result;
    }

    private async Task<(bool Ok, string Message)> RevokeDeviceAsync(LinkedDevice device, string reason, string doneMessage, CancellationToken ct)
    {
        var settings = _settingsService.CurrentSettings;
        if (string.Equals(device.DeviceId, settings.DeviceId, StringComparison.Ordinal)) return (false, "Este equipo no se puede quitar desde sí mismo.");
        if (device.IsPrimary) return (false, "La PC principal no se quita: en la PC que la reemplaza, use «Esta PC reemplaza a la principal».");
        try
        {
            var (client, problem) = await OpenVerifiedCloudAsync(settings, ct);
            if (client == null) return (false, problem!);
            using (client)
            {
                var role = device.Role ?? DeviceRole.WindowsDesktop;
                var letter = DevicePermissions.CodeLetter(role);
                var affected = await client.ExecuteAsync(
                    $"INSERT INTO {SyncSchema.DeviceRegistryTable} (DeviceId, DeviceCode, Role, DisplayName, Model, PairedAtUtc, RevokedAtUtc, RevokedReason) " +
                    $"SELECT ?, {NextCodeSql(letter)}, ?, ?, NULL, {ServerNowSql}, {ServerNowSql}, ? " +
                    $"FROM {SyncSchema.DeviceRegistryTable} WHERE DeviceCode LIKE '{letter}%' " +
                    "ON CONFLICT(DeviceId) DO UPDATE SET RevokedAtUtc = excluded.RevokedAtUtc, RevokedReason = excluded.RevokedReason " +
                    $"WHERE {SyncSchema.DeviceRegistryTable}.RevokedAtUtc IS NULL " +
                    $"AND {SyncSchema.DeviceRegistryTable}.DeviceId <> ifnull((SELECT \"Value\" FROM {Q(MetaTable)} WHERE \"Key\" = '{PrimaryDeviceKey}'), '');",
                    new object?[] { device.DeviceId, role.ToString(), device.DeviceName, reason }, ct);
                _settingsService.RecordUsage(2, affected);
                AppLogger.Info($"Turso: se revocó el equipo {device.DeviceName} ({device.DeviceId}), motivo '{reason}'.");
                return (true, doneMessage);
            }
        }
        catch (TursoException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Al final de una sincronización: si es el primer token que funciona después de anularlos (un 401 al que funcionaba, o
    /// «Perdí este equipo»), anota la hora en _tpvc_meta. Con una nube sin verificar se queda para la siguiente.
    /// </summary>
    private async Task<int> RecordTokenRotationAsync(TursoClient client, CancellationToken ct)
    {
        if (!_settingsService.RecordTokenWorked()) return 0;
        using (var db = _contextFactory.CreateDbContext())
        {
            if (await DbMeta.GetAsync(db, DbMeta.ClinicId, ct) == null) return 0;
        }
        var after = _settingsService.CurrentSettings.TokenRotationAfterUtc ?? DateTime.UtcNow;
        var affected = await client.ExecuteAsync(TokensRotatedSql, new object?[] { FormatDate(after) }, ct);
        _settingsService.ClearTokenRotation();
        AppLogger.Info("Turso: se anotó en la nube que se anularon los tokens; los equipos que no se conectan desde entonces piden el nuevo.");
        return affected;
    }

    // =========================================================================
    // VINCULAR LA TABLET Y EL CELULAR (paso 8.14)
    // =========================================================================

    /// <summary>
    /// Aprueba un equipo Android que pegó la URL y el token (o los leyó del QR) y escribió su latido: su fila de
    /// DeviceRegistry con el rol que eligió el médico y el siguiente código de esa letra (T1, C1…). Hasta entonces el equipo
    /// no baja datos. Si ya tenía fila (otra PC lo aprobó), no cambia nada.
    /// </summary>
    public async Task<(bool Ok, string Message)> ApproveDeviceAsync(LinkedDevice device, DeviceRole role, CancellationToken ct = default)
    {
        if (role is not (DeviceRole.AndroidTablet or DeviceRole.AndroidMobileAdmin)) return (false, "Un equipo Android se vincula como tablet o como celular.");
        if (!device.IsPendingApproval) return (false, $"«{device.DeviceName}» no está esperando que lo vinculen.");
        var settings = _settingsService.CurrentSettings;
        try
        {
            var (client, problem) = await OpenVerifiedCloudAsync(settings, ct);
            if (client == null) return (false, problem!);
            using (client)
            {
                var letter = DevicePermissions.CodeLetter(role);
                var results = await client.TransactionAsync(new (string, object?[]?)[]
                {
                    ($"INSERT INTO {SyncSchema.DeviceRegistryTable} (DeviceId, DeviceCode, Role, DisplayName, Model, PairedAtUtc) " +
                     $"SELECT ?, {NextCodeSql(letter)}, ?, ?, NULL, {ServerNowSql} FROM {SyncSchema.DeviceRegistryTable} WHERE DeviceCode LIKE '{letter}%' " +
                     "ON CONFLICT(DeviceId) DO NOTHING;",
                        new object?[] { device.DeviceId, role.ToString(), device.DeviceName }),
                    ($"SELECT DeviceCode, Role, RevokedAtUtc FROM {SyncSchema.DeviceRegistryTable} WHERE DeviceId = ?;", new object?[] { device.DeviceId })
                }, ct);
                _settingsService.RecordUsage(2, results[0].AffectedRows);
                var row = results[1].Rows.FirstOrDefault();
                var code = row?.GetValueOrDefault("DeviceCode")?.ToString();
                if (results[0].AffectedRows == 0)
                {
                    return (false, row?.GetValueOrDefault("RevokedAtUtc") is string { Length: > 0 }
                        ? $"«{device.DeviceName}» ya estaba quitado o rechazado."
                        : $"«{device.DeviceName}» ya estaba vinculado ({code}).");
                }
                var kind = role == DeviceRole.AndroidTablet ? "tablet" : "celular";
                AppLogger.Info($"Turso: se vinculó {device.DeviceName} ({device.DeviceId}) como {kind}, código {code}.");
                return (true, $"✓ «{device.DeviceName}» quedó vinculado como {kind} (código {code}). Empieza a bajar los datos en su siguiente sincronización.");
            }
        }
        catch (TursoException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Rechaza un equipo nuevo: su fila de DeviceRegistry ya revocada (motivo "rechazado", código X…). El equipo lo lee y
    /// no baja nada; sale de la lista. Para cortarle el acceso a la nube hay que anular los tokens (paso 8.15).
    /// </summary>
    public async Task<(bool Ok, string Message)> RejectDeviceAsync(LinkedDevice device, CancellationToken ct = default)
    {
        if (!device.IsPendingApproval) return (false, $"«{device.DeviceName}» no está esperando que lo vinculen.");
        var settings = _settingsService.CurrentSettings;
        try
        {
            var (client, problem) = await OpenVerifiedCloudAsync(settings, ct);
            if (client == null) return (false, problem!);
            using (client)
            {
                var affected = await client.ExecuteAsync(
                    $"INSERT INTO {SyncSchema.DeviceRegistryTable} (DeviceId, DeviceCode, Role, DisplayName, Model, PairedAtUtc, RevokedAtUtc, RevokedReason) " +
                    $"SELECT ?, {NextCodeSql(RejectedCodeLetter)}, ?, ?, NULL, {ServerNowSql}, {ServerNowSql}, '{RevokedRejectedReason}' " +
                    $"FROM {SyncSchema.DeviceRegistryTable} WHERE DeviceCode LIKE '{RejectedCodeLetter}%' ON CONFLICT(DeviceId) DO NOTHING;",
                    new object?[] { device.DeviceId, (device.Role ?? DeviceRole.AndroidTablet).ToString(), device.DeviceName }, ct);
                _settingsService.RecordUsage(2, affected);
                if (affected == 0) return (false, $"«{device.DeviceName}» ya tenía su lugar en los equipos vinculados.");
                AppLogger.Info($"Turso: se rechazó el equipo {device.DeviceName} ({device.DeviceId}).");
                return (true, $"✓ Se rechazó «{device.DeviceName}»: no bajará ningún dato. Si no reconoce ese equipo, anule los tokens en el panel de Turso.");
            }
        }
        catch (TursoException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Lo que va en el QR para la tablet o el celular: la URL, el token de esta PC y el nombre del consultorio. null (con el
    /// motivo) si la nube no está lista: sin sincronización, sin token o sin la «Sincronización completa».
    /// </summary>
    public async Task<(DeviceLinkData? Data, string? Problem)> GetLinkDataAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl) || string.IsNullOrWhiteSpace(settings.AuthToken))
            return (null, "Primero active la sincronización con la URL y el token de Turso.");
        using (var db = _contextFactory.CreateDbContext())
        {
            if (await DbMeta.GetAsync(db, DbMeta.ClinicId, ct) == null)
                return (null, "Primero haga la «Sincronización completa»: los equipos se vinculan a una nube verificada.");
        }
        return (new DeviceLinkData(settings.DatabaseUrl, settings.AuthToken, _clinicProfileService.CurrentProfile.ClinicName ?? string.Empty), null);
    }

    /// <summary>Para las acciones de Equipos vinculados: la nube con el esquema al día y con la identidad de esta base.</summary>
    private async Task<(TursoClient? Client, string? Problem)> OpenVerifiedCloudAsync(TursoSettings settings, CancellationToken ct)
    {
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl)) return (null, "La sincronización con la nube no está activada.");
        string? localClinicId;
        using (var db = _contextFactory.CreateDbContext()) localClinicId = await DbMeta.GetAsync(db, DbMeta.ClinicId, ct);
        if (localClinicId == null)
        {
            return (null, "Primero haga la «Sincronización completa» (o únase a la nube): los equipos se registran en una nube verificada.");
        }

        var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
        try
        {
            await EnsureRemoteSchemaAsync(client, ct);
            var (handshake, _, _) = await ReadHandshakeAsync(client, ct);
            if (!string.Equals(handshake.ClinicId, localClinicId, StringComparison.OrdinalIgnoreCase))
            {
                client.Dispose();
                return (null, "Esta nube pertenece a otra base de datos: no se cambió nada.");
            }
            return (client, null);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // =========================================================================
    // LISTA DE EQUIPOS
    // =========================================================================

    /// <summary>
    /// Las instrucciones de la lista de equipos (después del latido): la hora de la nube, los latidos, DeviceRegistry y la
    /// PC principal. Son tablas chicas: se leen completas y se cruzan aquí.
    /// </summary>
    internal static (string Sql, object?[]? Args)[] DeviceListQueries() => new (string, object?[]?)[]
    {
        ($"SELECT {ServerNowSql} AS ServerNow;", null),
        ("SELECT DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc, Platform, HandoffProtocol, ContractRevision, PendingCount, OldestPendingUtc FROM RegisteredDevices;", null),
        ($"SELECT DeviceId, DeviceCode, Role, DisplayName, RevokedAtUtc, RevokedReason FROM {SyncSchema.DeviceRegistryTable};", null),
        ($"SELECT \"Value\" FROM {Q(MetaTable)} WHERE \"Key\" = '{PrimaryDeviceKey}';", null),
        ($"SELECT \"Value\" FROM {Q(MetaTable)} WHERE \"Key\" = '{TokensRotatedKey}';", null)
    };

    /// <summary>
    /// «Equipos vinculados»: todos los equipos con latido o con fila en DeviceRegistry, menos los quitados. Escribe antes
    /// el latido de este equipo (así aparece con sus cambios pendientes de este momento).
    /// </summary>
    public async Task<IReadOnlyList<LinkedDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
        await EnsureRemoteSchemaAsync(client, ct);
        var heartbeat = await HeartbeatStatementsAsync(settings, afterPush: false, ct);
        var deviceId = settings.DeviceId;
        var results = await client.QueryBatchAsync(heartbeat.Concat(DeviceListQueries()), ct);
        await AfterHeartbeatAsync(client, deviceId, results[0], ct);

        var devices = ConsultationHandoffService.MergeDevices(results[3], results[4], results[5].FirstOrDefault()?["Value"]?.ToString(),
                ParseUtcOrNull(results[2].FirstOrDefault()?["ServerNow"]) ?? DateTime.UtcNow, settings.DeviceId,
                tokensRotatedAtUtc: ParseUtcOrNull(results[6].FirstOrDefault()?["Value"]))
            .Where(d => !d.IsRevoked)
            .OrderByDescending(d => d.IsThisDevice)
            .ThenByDescending(d => d.IsPrimary)
            .ThenByDescending(d => d.IsOnline)
            .ThenByDescending(d => d.LastSeenUtc)
            .ToList();
        AddExternalUsage(rowsRead: 2 + results.Skip(3).Sum(r => r.Count), rowsWritten: 1);
        return devices;
    }
}
