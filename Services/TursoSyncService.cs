using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TPVC.Data;
using TPVC.Models;
using TPVC.ViewModels;

namespace TPVC.Services;

/// <summary>
/// Motor de sincronización bidireccional incremental entre SQLite local y Turso Cloud (libSQL).
///
/// Protocolo (versión 2, compartido con la futura app Android):
/// * Cada tabla remota replica TODAS las columnas del modelo local (ver <see cref="TableMap"/>),
///   identificada por "SyncId" (GUID en minúsculas). Los Id enteros locales nunca se suben:
///   las referencias viajan como "&lt;Referencia&gt;SyncId" (p. ej. ProductId → ProductSyncId).
/// * "UpdatedAtUtc" (reloj del dispositivo que escribió) resuelve conflictos: gana la versión más reciente.
/// * "SyncedAtUtc" lo asigna el servidor al escribir; las descargas usan esa marca, así un dispositivo
///   con el reloj atrasado no puede "esconder" cambios.
/// * "OriginDeviceId" evita descargar de vuelta lo que el propio dispositivo subió.
/// * Los borrados físicos viajan en "SyncTombstones".
/// </summary>
public class TursoSyncService
{
    public const int RemoteSchemaVersion = 2;

    /// <summary>Hora del servidor (UTC) en el formato de las marcas de sincronización.</summary>
    public const string ServerNowSql = "strftime('%Y-%m-%d %H:%M:%f','now')";
    private const string TombstonesTable = "SyncTombstones";
    private const string MetaTable = "_tpvc_meta";
    private const int PushChunkSize = 80;
    private const int PullPageSize = 500;

    /// <summary>
    /// Referencias enteras sin relación declarada en EF que también deben traducirse a SyncId.
    /// </summary>
    private static readonly (Type Entity, string Property, Type Principal)[] SoftReferences =
    {
        (typeof(SaleItem), nameof(SaleItem.ProductId), typeof(Product)),
        (typeof(SaleItem), nameof(SaleItem.ClinicalServiceId), typeof(ClinicalService)),
    };

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly TursoSettingsService _settingsService;
    private readonly ClinicProfileService _clinicProfileService;
    private readonly InventoryService? _inventoryService;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Debouncer _autoSyncDebouncer = new();

    private List<TableMap>? _tableMaps;
    private string? _verifiedSchemaUrl;
    private string? _registeredDeviceUrl;
    private int _suppressAutoSync;

    // Una sincronización automática que encontró otra en curso se repite al terminar esa (antes se
    // perdía y lo guardado después del corte de la otra no subía hasta el siguiente cambio)
    private int _autoSyncSkipped;

    // Lecturas y escrituras de otras partes (revisión de consultas enviadas) para el monitor de cuota
    private long _externalRowsRead;
    private long _externalRowsWritten;

    /// <summary>Crea el cliente HTTP de Turso (reemplazable en pruebas por un servidor simulado).</summary>
    public Func<string, string, TursoClient> ClientFactory { get; set; } = (url, token) => new TursoClient(url, token);

    public TursoSyncStatus Status { get; private set; } = TursoSyncStatus.NotConfigured;
    public string StatusMessage { get; private set; } = "Sin configurar";
    public TursoSyncResult? LastSyncResult { get; private set; }

    public event Action<TursoSyncStatus, string>? StatusChanged;
    public event Action<TursoSyncResult>? SyncCompleted;

    public TursoSyncService(
        IDbContextFactory<AppDbContext> contextFactory,
        TursoSettingsService settingsService,
        ClinicProfileService clinicProfileService,
        InventoryService? inventoryService = null)
    {
        _contextFactory = contextFactory;
        _settingsService = settingsService;
        _clinicProfileService = clinicProfileService;
        _inventoryService = inventoryService;

        UpdateInitialStatus();

        if (_inventoryService != null)
        {
            _inventoryService.OnInventoryChanged += OnLocalDataChanged;
            _inventoryService.OnPatientsChanged += OnLocalDataChanged;
            _inventoryService.OnSalesChanged += OnLocalDataChanged;
            _inventoryService.OnAppointmentsChanged += OnLocalDataChanged;
        }

        _settingsService.SettingsChanged += UpdateInitialStatus;
    }

    private void UpdateInitialStatus()
    {
        if (Status == TursoSyncStatus.Syncing) return;

        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl))
        {
            SetStatus(TursoSyncStatus.NotConfigured, "Sincronización en la nube desactivada (Modo Local SQLite)");
        }
        else if (Status != TursoSyncStatus.Error)
        {
            SetStatus(TursoSyncStatus.Idle, settings.LastSyncUtc.HasValue
                ? $"Conectado a Turso • Último sync: {settings.LastSyncUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}"
                : "Conectado a Turso • Pendiente primera sincronización");
        }
    }

    private void SetStatus(TursoSyncStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
        StatusChanged?.Invoke(status, message);
    }

    private void OnLocalDataChanged()
    {
        // Cambios que llegaron de la nube no deben disparar otra sincronización
        if (Volatile.Read(ref _suppressAutoSync) > 0) return;

        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || !settings.AutoSyncOnChanges || string.IsNullOrWhiteSpace(settings.DatabaseUrl))
            return;

        // Debounce de 4 segundos: varios guardados seguidos se agrupan en una sola sincronización
        _ = _autoSyncDebouncer.RunAsync(async () =>
        {
            try
            {
                await SyncAsync(isBackgroundAutoSync: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Auto-sincronización con Turso no pudo completarse: {ex.Message}");
            }
        }, delayMs: 4000);
    }

    /// <summary>
    /// Sincronización incremental (sube cambios locales y descarga los remotos). <paramref name="lockWait"/>
    /// es cuánto esperar si ya hay otra en curso (por defecto 5 s, o nada si es automática).
    /// </summary>
    public Task<TursoSyncResult> SyncAsync(bool isBackgroundAutoSync = false, CancellationToken ct = default, TimeSpan? lockWait = null)
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl))
        {
            return Task.FromResult(new TursoSyncResult
            {
                Success = false,
                ErrorMessage = "La sincronización no está habilitada o falta configurar la URL de Turso."
            });
        }

        return RunExclusiveAsync(isBackgroundAutoSync, fullResync: false, ct, lockWait);
    }

    /// <summary>Suma al monitor de cuota lo leído y escrito fuera de la sincronización.</summary>
    public void AddExternalUsage(long rowsRead, long rowsWritten)
    {
        Interlocked.Add(ref _externalRowsRead, rowsRead);
        Interlocked.Add(ref _externalRowsWritten, rowsWritten);
    }

    /// <summary>
    /// Sincronización completa: sube todos los datos locales y descarga todos los remotos,
    /// ignorando las marcas de la última sincronización (útil al conectar por primera vez
    /// o para recuperar un equipo).
    /// </summary>
    public Task<TursoSyncResult> InitialUploadToTursoAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settingsService.CurrentSettings.DatabaseUrl))
        {
            return Task.FromResult(new TursoSyncResult { Success = false, ErrorMessage = "URL de Turso no especificada." });
        }

        return RunExclusiveAsync(isBackgroundAutoSync: false, fullResync: true, ct);
    }

    private async Task<TursoSyncResult> RunExclusiveAsync(bool isBackgroundAutoSync, bool fullResync, CancellationToken ct, TimeSpan? lockWait = null)
    {
        var wait = lockWait ?? (isBackgroundAutoSync ? TimeSpan.Zero : TimeSpan.FromSeconds(5));
        if (!await _syncLock.WaitAsync(wait, ct))
        {
            if (isBackgroundAutoSync) Interlocked.Exchange(ref _autoSyncSkipped, 1);
            return new TursoSyncResult
            {
                Success = false,
                ErrorMessage = "Ya hay una sincronización en curso. Por favor espere un momento."
            };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            SetStatus(TursoSyncStatus.Syncing, fullResync ? "Sincronización completa con Turso Cloud..." : "Sincronizando con Turso Cloud...");

            // Fuera del hilo de la interfaz: las consultas SQLite y el armado de lotes no congelan la ventana
            var (pushed, pulled) = await Task.Run(() => SyncCoreAsync(fullResync, ct), ct);
            sw.Stop();

            var result = new TursoSyncResult
            {
                Success = true,
                RowsPushed = pushed,
                RowsPulled = pulled,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                TimestampUtc = DateTime.UtcNow
            };

            SetStatus(TursoSyncStatus.Success, $"✓ Sincronizado ({pushed} subidos, {pulled} recibidos • {sw.ElapsedMilliseconds} ms)");
            LastSyncResult = result;
            SyncCompleted?.Invoke(result);

            if (pulled > 0)
            {
                NotifyLocalViews();
            }

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Error("Falló la sincronización con Turso", ex);

            var offline = ex is TursoException { ErrorCode: "NETWORK_ERROR" or "TIMEOUT" };
            SetStatus(offline ? TursoSyncStatus.Offline : TursoSyncStatus.Error, $"Error de sincronización: {ex.Message}");

            var result = new TursoSyncResult
            {
                Success = false,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                TimestampUtc = DateTime.UtcNow
            };
            LastSyncResult = result;
            SyncCompleted?.Invoke(result);
            return result;
        }
        finally
        {
            _syncLock.Release();
            if (Interlocked.Exchange(ref _autoSyncSkipped, 0) == 1) OnLocalDataChanged();
        }
    }

    private async Task<(int Pushed, int Pulled)> SyncCoreAsync(bool fullResync, CancellationToken ct)
    {
        var settings = _settingsService.CurrentSettings;
        using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);

        await EnsureRemoteSchemaAsync(client, ct);

        if (_registeredDeviceUrl != settings.DatabaseUrl)
        {
            await RegisterDeviceAsync(client, settings, ct);
            _registeredDeviceUrl = settings.DatabaseUrl;
        }

        // 1. PUSH: lo modificado localmente desde la última subida
        var cutoffUtc = DateTime.UtcNow;
        var lastPushed = fullResync ? DateTime.MinValue : settings.LastPushedLocalUtc ?? DateTime.MinValue;
        int pushed = await PushLocalChangesAsync(client, settings.DeviceId, lastPushed, cutoffUtc, ct);

        // 2. PULL: lo que otros dispositivos escribieron en el servidor desde la última descarga
        //    (la nueva marca se toma ANTES de leer, con 5 s de traslape; los duplicados se descartan por LWW)
        var watermarkRows = await client.QueryAsync($"SELECT strftime('%Y-%m-%d %H:%M:%f','now','-5 seconds') AS wm;", null, ct);
        var newWatermark = watermarkRows.FirstOrDefault()?["wm"]?.ToString();
        var lastPulled = fullResync ? null : settings.LastPulledServerWatermark;
        var (pulled, rowsRead) = await PullRemoteChangesAsync(client, settings.DeviceId, lastPulled, skipOwnRows: !fullResync, ct);

        // 3. Perfil del consultorio (último en modificarse gana)
        await SyncClinicProfileAsync(client, ct);

        settings.LastPushedLocalUtc = cutoffUtc;
        if (!string.IsNullOrWhiteSpace(newWatermark)) settings.LastPulledServerWatermark = newWatermark;
        settings.LastSyncUtc = DateTime.UtcNow;
        _settingsService.RecordUsage(rowsRead + 3 + Interlocked.Exchange(ref _externalRowsRead, 0),
            pushed + 1 + Interlocked.Exchange(ref _externalRowsWritten, 0));
        _settingsService.SaveSettings(settings);

        return (pushed, pulled);
    }

    // =========================================================================
    // MAPEO GENÉRICO DEL MODELO
    // =========================================================================

    private sealed class SyncColumn
    {
        public required string Name { get; init; }
        public required Type ClrType { get; init; }
        public required bool IsNullable { get; init; }
        public required PropertyInfo Property { get; init; }
        public required string RemoteType { get; init; }
    }

    private sealed class SyncReference
    {
        public required PropertyInfo Property { get; init; }
        public required Type PrincipalType { get; init; }
        public required string RemoteColumn { get; init; }
        public required bool IsRequired { get; init; }
    }

    private sealed class TableMap
    {
        public required Type ClrType { get; init; }
        public required string Table { get; init; }
        public required List<SyncColumn> Columns { get; init; }
        public required List<SyncReference> References { get; init; }
        public required List<PropertyInfo> UniqueProperties { get; init; }

        public IEnumerable<string> RemoteColumnNames =>
            Columns.Select(c => c.Name).Concat(References.Select(r => r.RemoteColumn));

        public string UpsertSql
        {
            get
            {
                var cols = RemoteColumnNames.ToList();
                var insertCols = string.Join(", ", new[] { "\"SyncId\"", "\"UpdatedAtUtc\"", "\"SyncedAtUtc\"", "\"OriginDeviceId\"" }
                    .Concat(cols.Select(Q)));
                var values = string.Join(", ", new[] { "?", "?", ServerNowSql, "?" }.Concat(cols.Select(_ => "?")));
                var updates = string.Join(", ", new[] { "UpdatedAtUtc", "SyncedAtUtc", "OriginDeviceId" }.Concat(cols)
                    .Select(c => $"{Q(c)} = excluded.{Q(c)}"));
                // Un registro que otro equipo ya borró (y su borrado es igual o más nuevo) no se vuelve
                // a crear: la subida reenvía filas con marca adelantada que pudieron borrarse en la nube
                return $"INSERT INTO {Q(Table)} ({insertCols}) SELECT {values} " +
                       $"WHERE NOT EXISTS (SELECT 1 FROM {Q(TombstonesTable)} WHERE \"EntityType\" = ? AND \"SyncId\" = ? AND \"DeletedAtUtc\" >= ?) " +
                       $"ON CONFLICT(\"SyncId\") DO UPDATE SET {updates} " +
                       $"WHERE excluded.\"UpdatedAtUtc\" > {Q(Table)}.\"UpdatedAtUtc\";";
            }
        }
    }

    private static string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private List<TableMap> GetTableMaps()
    {
        if (_tableMaps != null) return _tableMaps;

        using var db = _contextFactory.CreateDbContext();
        var maps = new List<TableMap>();

        foreach (var entityType in db.Model.GetEntityTypes().Where(t => typeof(ISyncEntity).IsAssignableFrom(t.ClrType)))
        {
            var table = entityType.GetTableName();
            if (string.IsNullOrEmpty(table)) continue;
            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());

            var references = new List<SyncReference>();
            var referenceProps = new HashSet<string>();

            foreach (var fk in entityType.GetForeignKeys())
            {
                if (fk.Properties.Count != 1 || !typeof(ISyncEntity).IsAssignableFrom(fk.PrincipalEntityType.ClrType)) continue;
                var prop = fk.Properties[0];
                if (prop.PropertyInfo == null) continue;
                references.Add(new SyncReference
                {
                    Property = prop.PropertyInfo,
                    PrincipalType = fk.PrincipalEntityType.ClrType,
                    RemoteColumn = ReferenceColumnName(prop.Name),
                    IsRequired = !prop.IsNullable
                });
                referenceProps.Add(prop.Name);
            }

            foreach (var (entity, propertyName, principal) in SoftReferences.Where(r => r.Entity == entityType.ClrType))
            {
                var prop = entityType.FindProperty(propertyName);
                if (prop?.PropertyInfo == null || referenceProps.Contains(propertyName)) continue;
                references.Add(new SyncReference
                {
                    Property = prop.PropertyInfo,
                    PrincipalType = principal,
                    RemoteColumn = ReferenceColumnName(propertyName),
                    IsRequired = false
                });
                referenceProps.Add(propertyName);
            }

            var columns = entityType.GetProperties()
                .Where(p => !p.IsPrimaryKey()
                            && p.PropertyInfo != null
                            && p.Name != nameof(ISyncEntity.SyncId)
                            && p.Name != nameof(ISyncEntity.UpdatedAtUtc)
                            && !referenceProps.Contains(p.Name))
                .Select(p => new SyncColumn
                {
                    Name = p.GetColumnName(storeObject) ?? p.Name,
                    ClrType = p.ClrType,
                    IsNullable = p.IsNullable,
                    Property = p.PropertyInfo!,
                    RemoteType = RemoteTypeFor(p.ClrType)
                })
                .ToList();

            var uniqueProps = entityType.GetIndexes()
                .Where(i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name != nameof(ISyncEntity.SyncId)
                            && i.Properties[0].ClrType == typeof(string) && i.Properties[0].PropertyInfo != null)
                .Select(i => i.Properties[0].PropertyInfo!)
                .ToList();

            maps.Add(new TableMap
            {
                ClrType = entityType.ClrType,
                Table = table,
                Columns = columns,
                References = references,
                UniqueProperties = uniqueProps
            });
        }

        _tableMaps = OrderByDependencies(maps);
        return _tableMaps;
    }

    /// <summary>ProductId → ProductSyncId, PatientRecordId → PatientRecordSyncId...</summary>
    private static string ReferenceColumnName(string propertyName) =>
        (propertyName.EndsWith("Id", StringComparison.Ordinal) ? propertyName[..^2] : propertyName) + "SyncId";

    private static string RemoteTypeFor(Type clrType)
    {
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t.IsEnum) return "INTEGER";
        if (t == typeof(double) || t == typeof(float)) return "REAL";
        return "TEXT"; // string, decimal (exacto), DateTime, Guid
    }

    /// <summary>Principales antes que dependientes (Categoría → Producto → Lote → Movimiento...).</summary>
    private static List<TableMap> OrderByDependencies(List<TableMap> maps)
    {
        var ordered = new List<TableMap>();
        var pending = maps.OrderBy(m => m.Table, StringComparer.Ordinal).ToList();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(m => m.References.All(r =>
                r.PrincipalType == m.ClrType || ordered.Any(o => o.ClrType == r.PrincipalType) || pending.All(p => p.ClrType != r.PrincipalType)))
                ?? pending[0];
            ordered.Add(next);
            pending.Remove(next);
        }
        return ordered;
    }

    // =========================================================================
    // ESQUEMA REMOTO
    // =========================================================================

    private IEnumerable<string> BuildSchemaStatements()
    {
        yield return $"CREATE TABLE IF NOT EXISTS {Q(MetaTable)} (\"Key\" TEXT PRIMARY KEY NOT NULL, \"Value\" TEXT)";

        foreach (var map in GetTableMaps())
        {
            var cols = map.Columns.Select(c => $"{Q(c.Name)} {c.RemoteType}")
                .Concat(map.References.Select(r => $"{Q(r.RemoteColumn)} TEXT"));
            yield return $"CREATE TABLE IF NOT EXISTS {Q(map.Table)} (\"SyncId\" TEXT PRIMARY KEY NOT NULL, \"UpdatedAtUtc\" TEXT NOT NULL, " +
                         $"\"SyncedAtUtc\" TEXT NOT NULL, \"OriginDeviceId\" TEXT, {string.Join(", ", cols)})";
            yield return $"CREATE INDEX IF NOT EXISTS {Q($"IX_{map.Table}_SyncedAtUtc")} ON {Q(map.Table)} (\"SyncedAtUtc\")";
        }

        yield return $"CREATE TABLE IF NOT EXISTS {Q(TombstonesTable)} (\"EntityType\" TEXT NOT NULL, \"SyncId\" TEXT NOT NULL, " +
                     "\"DeletedAtUtc\" TEXT NOT NULL, \"SyncedAtUtc\" TEXT NOT NULL, \"OriginDeviceId\" TEXT, PRIMARY KEY (\"EntityType\", \"SyncId\"))";
        yield return $"CREATE INDEX IF NOT EXISTS \"IX_SyncTombstones_SyncedAtUtc\" ON {Q(TombstonesTable)} (\"SyncedAtUtc\")";

        yield return "CREATE TABLE IF NOT EXISTS ClinicProfiles (Id INTEGER PRIMARY KEY, DoctorName TEXT, ProfessionalLicense TEXT, Specialty TEXT, " +
                     "University TEXT, ClinicName TEXT, Address TEXT, Phone TEXT, Email TEXT, TaxId TEXT, AdditionalNotes TEXT, UpdatedAtUtc TEXT NOT NULL)";
        yield return "CREATE TABLE IF NOT EXISTS RegisteredDevices (DeviceId TEXT PRIMARY KEY, DeviceName TEXT NOT NULL, DeviceRole TEXT NOT NULL, " +
                     "AppVersion TEXT NOT NULL, LastSeenUtc TEXT NOT NULL)";

        // Consultas enviadas entre equipos (no se replica: cada equipo la consulta directamente)
        foreach (var ddl in ConsultationHandoffService.HandoffTableDdl) yield return ddl;
    }

    /// <summary>
    /// Crea o actualiza el esquema remoto. Las tablas creadas por versiones 1.8.x (con Id entero y sin
    /// SyncedAtUtc) se renombran a "legacy_v1_*" sin borrar nada, y se crean las tablas nuevas.
    /// Solo consulta la estructura cuando cambia la huella del esquema (ahorra cuota del plan gratuito).
    /// </summary>
    public async Task EnsureRemoteSchemaAsync(TursoClient client, CancellationToken ct = default)
    {
        if (_verifiedSchemaUrl == client.DatabaseUrl) return;

        var statements = BuildSchemaStatements().ToList();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"v{RemoteSchemaVersion}|" + string.Join("|", statements))))[..16];

        var probe = await client.QueryBatchAsync(new (string, object?[]?)[]
        {
            (statements[0], null),
            ($"SELECT \"Value\" FROM {Q(MetaTable)} WHERE \"Key\" = 'schema';", null)
        }, ct);

        if (probe.Count > 1 && probe[1].FirstOrDefault()?["Value"]?.ToString() == fingerprint)
        {
            _verifiedSchemaUrl = client.DatabaseUrl;
            return;
        }

        // Estructura actual de las tablas sincronizadas
        var syncTables = GetTableMaps().Select(m => m.Table).Append(TombstonesTable).ToList();
        var existingTables = (await client.QueryAsync("SELECT name FROM sqlite_master WHERE type = 'table';", null, ct))
            .Select(r => r["name"]?.ToString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tablesToInspect = syncTables.Where(existingTables.Contains).ToList();
        var columnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (tablesToInspect.Count > 0)
        {
            var info = await client.QueryBatchAsync(
                tablesToInspect.Select(t => ($"SELECT name FROM pragma_table_info('{t}');", (object?[]?)null)), ct);
            for (int i = 0; i < tablesToInspect.Count && i < info.Count; i++)
            {
                columnsByTable[tablesToInspect[i]] = info[i]
                    .Select(r => r["name"]?.ToString() ?? string.Empty)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        var ddl = new List<(string Sql, object?[]? Args)>();
        foreach (var (table, columns) in columnsByTable)
        {
            if (!columns.Contains("SyncedAtUtc") || columns.Contains("Id"))
            {
                var legacyName = $"legacy_v1_{table}";
                if (existingTables.Contains(legacyName)) legacyName += $"_{DateTime.UtcNow:yyyyMMddHHmmss}";
                ddl.Add(($"ALTER TABLE {Q(table)} RENAME TO {Q(legacyName)};", null));
                AppLogger.Warn($"Turso: tabla heredada {table} renombrada a {legacyName} (esquema v{RemoteSchemaVersion}).");
            }
        }

        ddl.AddRange(statements.Select(s => (s + ";", (object?[]?)null)));

        // Columnas nuevas del modelo en tablas v2 existentes
        foreach (var map in GetTableMaps())
        {
            if (!columnsByTable.TryGetValue(map.Table, out var cols) || !cols.Contains("SyncedAtUtc") || cols.Contains("Id")) continue;
            foreach (var c in map.Columns.Where(c => !cols.Contains(c.Name)))
            {
                ddl.Add(($"ALTER TABLE {Q(map.Table)} ADD COLUMN {Q(c.Name)} {c.RemoteType};", null));
            }
            foreach (var r in map.References.Where(r => !cols.Contains(r.RemoteColumn)))
            {
                ddl.Add(($"ALTER TABLE {Q(map.Table)} ADD COLUMN {Q(r.RemoteColumn)} TEXT;", null));
            }
        }

        ddl.Add(($"INSERT INTO {Q(MetaTable)} (\"Key\", \"Value\") VALUES ('schema', ?) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";",
            new object?[] { fingerprint }));

        await client.ExecuteBatchAsync(ddl, ct);
        _verifiedSchemaUrl = client.DatabaseUrl;
    }

    /// <summary>Versión del programa (x.y.z) que se registra para cada equipo.</summary>
    public static string AppVersionText => typeof(TursoSyncService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static async Task RegisterDeviceAsync(TursoClient client, TursoSettings settings, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc)
VALUES (?, ?, ?, ?, ?)
ON CONFLICT(DeviceId) DO UPDATE SET
    DeviceName = excluded.DeviceName,
    DeviceRole = excluded.DeviceRole,
    AppVersion = excluded.AppVersion,
    LastSeenUtc = excluded.LastSeenUtc;";

        // Hora del servidor: "última conexión" no depende del reloj de cada equipo
        await client.ExecuteAsync(sql.Replace("VALUES (?, ?, ?, ?, ?)", $"VALUES (?, ?, ?, ?, {ServerNowSql})"), new object?[]
        {
            settings.DeviceId,
            settings.DeviceName,
            settings.DeviceRole.ToString(),
            AppVersionText
        }, ct);
    }

    // =========================================================================
    // PUSH
    // =========================================================================

    private async Task<int> PushLocalChangesAsync(TursoClient client, string deviceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        var statements = new List<(string Sql, object?[]? Args)>();
        var idMaps = new Dictionary<Type, Dictionary<int, Guid>>();

        foreach (var map in GetTableMaps())
        {
            var changed = await LoadChangedAsync(db, map.ClrType, fromUtc, toUtc, ct);
            if (changed.Count == 0) continue;

            var sql = map.UpsertSql;
            foreach (var entity in changed)
            {
                var args = new List<object?>
                {
                    FormatGuid(entity.SyncId),
                    FormatDate(entity.UpdatedAtUtc),
                    deviceId
                };
                args.AddRange(map.Columns.Select(c => ToRemoteValue(c.Property.GetValue(entity))));

                bool skip = false;
                foreach (var reference in map.References)
                {
                    var localId = reference.Property.GetValue(entity) as int?;
                    Guid? principalSyncId = null;
                    if (localId is > 0)
                    {
                        if (!idMaps.TryGetValue(reference.PrincipalType, out var lookup))
                        {
                            lookup = await LoadIdToSyncIdAsync(db, reference.PrincipalType, ct);
                            idMaps[reference.PrincipalType] = lookup;
                        }
                        if (lookup.TryGetValue(localId.Value, out var g)) principalSyncId = g;
                    }

                    if (principalSyncId == null && reference.IsRequired)
                    {
                        skip = true;
                        break;
                    }
                    args.Add(principalSyncId.HasValue ? FormatGuid(principalSyncId.Value) : null);
                }

                if (skip)
                {
                    AppLogger.Warn($"Turso: {map.Table} {entity.SyncId} omitido (referencia requerida inexistente).");
                    continue;
                }
                args.Add(map.ClrType.Name);
                args.Add(FormatGuid(entity.SyncId));
                args.Add(FormatDate(entity.UpdatedAtUtc));
                statements.Add((sql, args.ToArray()));
            }
        }

        // Borrados físicos: se registran y se aplican en la nube salvo que allá exista una versión más nueva
        // Sin tope superior: un borrado con marca adelantada al reloj (ver AppDbContext) también sube
        var tombstones = await db.SyncTombstones.AsNoTracking()
            .Where(t => t.DeletedAtUtc > fromUtc)
            .ToListAsync(ct);
        var tablesByType = GetTableMaps().ToDictionary(m => m.ClrType.Name, m => m.Table);
        foreach (var t in tombstones)
        {
            statements.Add((
                $"INSERT INTO {Q(TombstonesTable)} (\"EntityType\", \"SyncId\", \"DeletedAtUtc\", \"SyncedAtUtc\", \"OriginDeviceId\") " +
                $"VALUES (?, ?, ?, {ServerNowSql}, ?) ON CONFLICT(\"EntityType\", \"SyncId\") DO UPDATE SET " +
                "\"DeletedAtUtc\" = excluded.\"DeletedAtUtc\", \"SyncedAtUtc\" = excluded.\"SyncedAtUtc\", \"OriginDeviceId\" = excluded.\"OriginDeviceId\" " +
                $"WHERE excluded.\"DeletedAtUtc\" > {Q(TombstonesTable)}.\"DeletedAtUtc\";",
                new object?[] { t.EntityType, FormatGuid(t.SyncId), FormatDate(t.DeletedAtUtc), deviceId }));

            if (tablesByType.TryGetValue(t.EntityType, out var table))
            {
                statements.Add(($"DELETE FROM {Q(table)} WHERE \"SyncId\" = ? AND \"UpdatedAtUtc\" <= ?;",
                    new object?[] { FormatGuid(t.SyncId), FormatDate(t.DeletedAtUtc) }));
            }
        }

        // Se cuentan las filas que la nube sí cambió: reenviar algo que ya tenía no cuenta como subido
        int applied = 0;
        for (int i = 0; i < statements.Count; i += PushChunkSize)
        {
            applied += await client.ExecuteBatchAsync(statements.Skip(i).Take(PushChunkSize), ct);
        }

        return applied;
    }

    private static async Task<List<ISyncEntity>> LoadChangedAsync(AppDbContext db, Type clrType, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(LoadChangedGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(clrType);
        return await (Task<List<ISyncEntity>>)method.Invoke(null, new object[] { db, fromUtc, toUtc, ct })!;
    }

    private static async Task<List<ISyncEntity>> LoadChangedGenericAsync<T>(AppDbContext db, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        where T : class, ISyncEntity
    {
        // Sin tope superior: un registro con marca posterior al corte (guardado durante la subida o
        // con marca adelantada para ganarle a un equipo con el reloj adelantado) sube igual; la nube
        // descarta lo que no sea más nuevo, así que volver a subirlo no cambia nada
        var list = await db.Set<T>().AsNoTracking()
            .Where(e => EF.Property<DateTime>(e, nameof(ISyncEntity.UpdatedAtUtc)) > fromUtc)
            .ToListAsync(ct);
        return list.Cast<ISyncEntity>().ToList();
    }

    private static async Task<Dictionary<int, Guid>> LoadIdToSyncIdAsync(AppDbContext db, Type clrType, CancellationToken ct)
    {
        var pairs = await LoadIdPairsAsync(db, clrType, ct);
        return pairs.ToDictionary(p => p.Id, p => p.SyncId);
    }

    private static async Task<Dictionary<Guid, int>> LoadSyncIdToIdAsync(AppDbContext db, Type clrType, CancellationToken ct)
    {
        var pairs = await LoadIdPairsAsync(db, clrType, ct);
        var map = new Dictionary<Guid, int>();
        foreach (var p in pairs) map[p.SyncId] = p.Id;
        return map;
    }

    private static async Task<List<(int Id, Guid SyncId)>> LoadIdPairsAsync(AppDbContext db, Type clrType, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(LoadIdPairsGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(clrType);
        return await (Task<List<(int, Guid)>>)method.Invoke(null, new object[] { db, ct })!;
    }

    private static async Task<List<(int, Guid)>> LoadIdPairsGenericAsync<T>(AppDbContext db, CancellationToken ct) where T : class
    {
        var rows = await db.Set<T>().AsNoTracking()
            .Select(e => new { Id = EF.Property<int>(e, "Id"), SyncId = EF.Property<Guid>(e, nameof(ISyncEntity.SyncId)) })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, r.SyncId)).ToList();
    }

    // =========================================================================
    // PULL
    // =========================================================================

    private async Task<(int Applied, int RowsRead)> PullRemoteChangesAsync(
        TursoClient client, string deviceId, string? fromWatermark, bool skipOwnRows, CancellationToken ct)
    {
        int applied = 0;
        int rowsRead = 0;
        var syncIdMaps = new Dictionary<Type, Dictionary<Guid, int>>();

        foreach (var map in GetTableMaps())
        {
            await foreach (var page in ReadPagesAsync(client, map.Table, deviceId, fromWatermark, skipOwnRows, ct))
            {
                rowsRead += page.Count;
                applied += await ApplyPageAsync(map, page, syncIdMaps, ct);
            }
            // Esta tabla pudo recibir filas nuevas: refrescar su mapa para las tablas dependientes
            syncIdMaps.Remove(map.ClrType);
        }

        await foreach (var page in ReadPagesAsync(client, TombstonesTable, deviceId, fromWatermark, skipOwnRows, ct, keyColumn: "SyncId"))
        {
            rowsRead += page.Count;
            applied += await ApplyTombstonesAsync(page, ct);
        }

        return (applied, rowsRead);
    }

    private static async IAsyncEnumerable<List<Dictionary<string, object?>>> ReadPagesAsync(
        TursoClient client, string table, string deviceId, string? fromWatermark, bool skipOwnRows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, string keyColumn = "SyncId")
    {
        string lastSynced = fromWatermark ?? string.Empty;
        string lastKey = string.Empty;
        var originFilter = skipOwnRows ? " AND (\"OriginDeviceId\" IS NULL OR \"OriginDeviceId\" <> ?)" : string.Empty;

        while (true)
        {
            var sql = $"SELECT * FROM {Q(table)} WHERE (\"SyncedAtUtc\" > ? OR (\"SyncedAtUtc\" = ? AND {Q(keyColumn)} > ?)){originFilter} " +
                      $"ORDER BY \"SyncedAtUtc\", {Q(keyColumn)} LIMIT {PullPageSize};";
            var args = skipOwnRows
                ? new object?[] { lastSynced, lastSynced, lastKey, deviceId }
                : new object?[] { lastSynced, lastSynced, lastKey };

            var rows = await client.QueryAsync(sql, args, ct);
            if (rows.Count == 0) yield break;
            yield return rows;
            if (rows.Count < PullPageSize) yield break;

            var last = rows[^1];
            lastSynced = last["SyncedAtUtc"]?.ToString() ?? lastSynced;
            lastKey = last[keyColumn]?.ToString() ?? string.Empty;
        }
    }

    private async Task<int> ApplyPageAsync(TableMap map, List<Dictionary<string, object?>> rows, Dictionary<Type, Dictionary<Guid, int>> syncIdMaps, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(ApplyPageGenericAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(map.ClrType);
        return await (Task<int>)method.Invoke(this, new object[] { map, rows, syncIdMaps, ct })!;
    }

    private async Task<int> ApplyPageGenericAsync<T>(TableMap map, List<Dictionary<string, object?>> rows, Dictionary<Type, Dictionary<Guid, int>> syncIdMaps, CancellationToken ct)
        where T : class, ISyncEntity, new()
    {
        using var db = _contextFactory.CreateDbContext();
        db.ApplyingRemoteChanges = true;

        var incoming = rows
            .Select(r => (Row: r, SyncId: ParseGuid(r.GetValueOrDefault("SyncId"))))
            .Where(x => x.SyncId.HasValue)
            .ToList();
        var ids = incoming.Select(x => x.SyncId!.Value).ToList();
        var locals = (await db.Set<T>()
                .Where(e => ids.Contains(EF.Property<Guid>(e, nameof(ISyncEntity.SyncId))))
                .ToListAsync(ct))
            .ToDictionary(e => e.SyncId);

        int applied = 0;
        foreach (var (row, syncIdNullable) in incoming)
        {
            var syncId = syncIdNullable!.Value;
            var remoteUpdated = ParseDate(row.GetValueOrDefault("UpdatedAtUtc"));
            locals.TryGetValue(syncId, out var local);

            // Last-Write-Wins: la versión local igual o más nueva se conserva
            if (local != null && local.UpdatedAtUtc >= remoteUpdated) continue;

            // Resolver referencias (SyncId remoto → Id local)
            var resolved = new List<(PropertyInfo Property, int? Id)>();
            bool missingRequired = false;
            foreach (var reference in map.References)
            {
                var principalSyncId = ParseGuid(row.GetValueOrDefault(reference.RemoteColumn));
                int? localId = null;
                if (principalSyncId.HasValue)
                {
                    if (!syncIdMaps.TryGetValue(reference.PrincipalType, out var lookup))
                    {
                        lookup = await LoadSyncIdToIdAsync(db, reference.PrincipalType, ct);
                        syncIdMaps[reference.PrincipalType] = lookup;
                    }
                    if (lookup.TryGetValue(principalSyncId.Value, out var id)) localId = id;
                }

                if (localId == null && reference.IsRequired)
                {
                    missingRequired = true;
                    break;
                }
                resolved.Add((reference.Property, localId));
            }

            if (missingRequired)
            {
                AppLogger.Warn($"Turso: {map.Table} {syncId} no se aplicó (falta su registro relacionado en este equipo).");
                continue;
            }

            if (local == null)
            {
                local = new T { SyncId = syncId };
                db.Set<T>().Add(local);
                locals[syncId] = local;
            }

            var entry = db.Entry(local);
            foreach (var column in map.Columns)
            {
                if (!row.TryGetValue(column.Name, out var raw)) continue;
                entry.Property(column.Property.Name).CurrentValue = FromRemoteValue(raw, column.ClrType, column.IsNullable);
            }
            foreach (var (property, id) in resolved)
            {
                entry.Property(property.Name).CurrentValue = property.PropertyType == typeof(int) ? id ?? 0 : id;
            }
            local.UpdatedAtUtc = remoteUpdated;
            applied++;
        }

        await ResolveUniqueConflictsAsync(db, map, ct);
        await db.SaveChangesAsync(ct);
        return applied;
    }

    /// <summary>
    /// Dos equipos pueden generar el mismo folio (EXP-2026-005, TKT-...) para registros distintos.
    /// Para no bloquear la sincronización, el registro recibido conserva sus datos y su folio
    /// se marca con un sufijo visible ("-R1", "-R2"...).
    /// </summary>
    private static async Task ResolveUniqueConflictsAsync(AppDbContext db, TableMap map, CancellationToken ct)
    {
        if (map.UniqueProperties.Count == 0) return;

        var pending = db.ChangeTracker.Entries()
            .Where(e => e.Entity.GetType() == map.ClrType && e.State is EntityState.Added or EntityState.Modified)
            .ToList();

        foreach (var property in map.UniqueProperties)
        {
            var batchValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in pending)
            {
                var value = entry.Property(property.Name).CurrentValue as string;
                if (string.IsNullOrEmpty(value)) continue;

                var syncId = ((ISyncEntity)entry.Entity).SyncId;
                var candidate = value;
                int attempt = 0;
                while (batchValues.Contains(candidate) || await ExistsWithOtherSyncIdAsync(db, map.ClrType, property.Name, candidate, syncId, ct))
                {
                    attempt++;
                    candidate = $"{value}-R{attempt}";
                }

                if (candidate != value)
                {
                    AppLogger.Warn($"Turso: {map.Table}.{property.Name} '{value}' ya existía en este equipo; el registro recibido se guardó como '{candidate}'.");
                    entry.Property(property.Name).CurrentValue = candidate;
                }
                batchValues.Add(candidate);
            }
        }
    }

    private static async Task<bool> ExistsWithOtherSyncIdAsync(AppDbContext db, Type clrType, string propertyName, string value, Guid syncId, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(ExistsWithOtherSyncIdGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(clrType);
        return await (Task<bool>)method.Invoke(null, new object[] { db, propertyName, value, syncId, ct })!;
    }

    private static Task<bool> ExistsWithOtherSyncIdGenericAsync<T>(AppDbContext db, string propertyName, string value, Guid syncId, CancellationToken ct)
        where T : class
    {
        return db.Set<T>().AsNoTracking().AnyAsync(e =>
            EF.Property<string>(e, propertyName) == value && EF.Property<Guid>(e, nameof(ISyncEntity.SyncId)) != syncId, ct);
    }

    private async Task<int> ApplyTombstonesAsync(List<Dictionary<string, object?>> rows, CancellationToken ct)
    {
        int applied = 0;
        var mapsByType = GetTableMaps().ToDictionary(m => m.ClrType.Name, m => m);

        foreach (var row in rows)
        {
            var syncId = ParseGuid(row.GetValueOrDefault("SyncId"));
            var entityType = row.GetValueOrDefault("EntityType")?.ToString() ?? string.Empty;
            if (syncId == null || !mapsByType.TryGetValue(entityType, out var map)) continue;

            var deletedAt = ParseDate(row.GetValueOrDefault("DeletedAtUtc"));
            try
            {
                using var db = _contextFactory.CreateDbContext();
                db.ApplyingRemoteChanges = true;
                var method = typeof(TursoSyncService).GetMethod(nameof(DeleteLocalGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(map.ClrType);
                if (await (Task<bool>)method.Invoke(null, new object[] { db, syncId.Value, deletedAt, ct })!) applied++;
            }
            catch (Exception ex)
            {
                // P. ej. una categoría que todavía tiene productos en este equipo
                AppLogger.Warn($"Turso: no se pudo aplicar el borrado de {entityType} {syncId}: {ex.Message}");
            }
        }
        return applied;
    }

    private static async Task<bool> DeleteLocalGenericAsync<T>(AppDbContext db, Guid syncId, DateTime deletedAtUtc, CancellationToken ct)
        where T : class, ISyncEntity
    {
        var local = await db.Set<T>().FirstOrDefaultAsync(e => EF.Property<Guid>(e, nameof(ISyncEntity.SyncId)) == syncId, ct);
        // Si aquí se modificó después del borrado remoto, se conserva (LWW)
        if (local == null || local.UpdatedAtUtc > deletedAtUtc) return false;
        db.Set<T>().Remove(local);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // =========================================================================
    // PERFIL DEL CONSULTORIO
    // =========================================================================

    private async Task SyncClinicProfileAsync(TursoClient client, CancellationToken ct)
    {
        var localProfile = _clinicProfileService.CurrentProfile;
        var rows = await client.QueryAsync("SELECT * FROM ClinicProfiles WHERE Id = 1;", null, ct);
        var remote = rows.FirstOrDefault();
        DateTime? remoteUpdated = remote != null ? ParseDate(remote.GetValueOrDefault("UpdatedAtUtc")) : null;

        bool remoteHasData = remote != null && (!string.IsNullOrWhiteSpace(remote.GetValueOrDefault("DoctorName")?.ToString())
                                                || !string.IsNullOrWhiteSpace(remote.GetValueOrDefault("ClinicName")?.ToString()));

        if (remoteHasData && (localProfile.UpdatedAtUtc == null || remoteUpdated > localProfile.UpdatedAtUtc || !localProfile.IsConfigured))
        {
            var r = remote!;
            var updated = new ClinicProfile
            {
                DoctorName = r.GetValueOrDefault("DoctorName")?.ToString() ?? string.Empty,
                ProfessionalLicense = r.GetValueOrDefault("ProfessionalLicense")?.ToString() ?? string.Empty,
                Specialty = r.GetValueOrDefault("Specialty")?.ToString() ?? string.Empty,
                University = r.GetValueOrDefault("University")?.ToString() ?? string.Empty,
                ClinicName = r.GetValueOrDefault("ClinicName")?.ToString() ?? string.Empty,
                Address = r.GetValueOrDefault("Address")?.ToString() ?? string.Empty,
                Phone = r.GetValueOrDefault("Phone")?.ToString() ?? string.Empty,
                Email = r.GetValueOrDefault("Email")?.ToString() ?? string.Empty,
                TaxId = r.GetValueOrDefault("TaxId")?.ToString() ?? string.Empty,
                AdditionalNotes = r.GetValueOrDefault("AdditionalNotes")?.ToString() ?? string.Empty,
                IsConfigured = true,
                IsInitialSetupCompleted = true,
                UpdatedAtUtc = remoteUpdated
            };
            // ProfileChanged actualiza vistas: se aplica en el hilo de la interfaz
            RunOnUiThread(() => _clinicProfileService.SaveProfile(updated, preserveTimestamp: true));
            return;
        }

        if (localProfile.IsConfigured && (remote == null || localProfile.UpdatedAtUtc > remoteUpdated))
        {
            const string sql = @"
INSERT INTO ClinicProfiles (Id, DoctorName, ProfessionalLicense, Specialty, University, ClinicName, Address, Phone, Email, TaxId, AdditionalNotes, UpdatedAtUtc)
VALUES (1, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
ON CONFLICT(Id) DO UPDATE SET
    DoctorName = excluded.DoctorName, ProfessionalLicense = excluded.ProfessionalLicense, Specialty = excluded.Specialty,
    University = excluded.University, ClinicName = excluded.ClinicName, Address = excluded.Address, Phone = excluded.Phone,
    Email = excluded.Email, TaxId = excluded.TaxId, AdditionalNotes = excluded.AdditionalNotes, UpdatedAtUtc = excluded.UpdatedAtUtc;";

            await client.ExecuteAsync(sql, new object?[]
            {
                localProfile.DoctorName, localProfile.ProfessionalLicense, localProfile.Specialty, localProfile.University,
                localProfile.ClinicName, localProfile.Address, localProfile.Phone, localProfile.Email, localProfile.TaxId,
                localProfile.AdditionalNotes, FormatDate(localProfile.UpdatedAtUtc ?? DateTime.UtcNow)
            }, ct);
        }
    }

    // =========================================================================
    // UTILIDADES
    // =========================================================================

    private void NotifyLocalViews()
    {
        RunOnUiThread(() =>
        {
            Interlocked.Increment(ref _suppressAutoSync);
            try
            {
                _inventoryService?.NotifyAllChanged();
            }
            finally
            {
                Interlocked.Decrement(ref _suppressAutoSync);
            }
        });
    }

    private static void RunOnUiThread(Action action)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(action);
                return;
            }
        }
        catch
        {
            // Sin WPF (pruebas): ejecutar directamente
        }
        action();
    }

    /// <summary>GUID canónico del protocolo: minúsculas con guiones (igual en Windows y Android).</summary>
    public static string FormatGuid(Guid g) => g.ToString("D");

    /// <summary>
    /// Fechas sin conversión de zona horaria: UpdatedAtUtc ya está en UTC y las fechas de negocio
    /// (consulta, caducidad, cita) son hora local del consultorio. Convertirlas desfasaba 6 h en México.
    /// </summary>
    public static string FormatDate(DateTime dt) =>
        dt.ToString(TursoSyncFormats.DateTimeFormat, CultureInfo.InvariantCulture);

    private static readonly string[] DateFormats =
    {
        TursoSyncFormats.DateTimeFormat,
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd"
    };

    public static DateTime ParseDate(object? raw)
    {
        var s = raw?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose)) return loose;
        return DateTime.MinValue;
    }

    private static Guid? ParseGuid(object? raw) =>
        raw != null && Guid.TryParse(raw.ToString(), out var g) ? g : null;

    private static object? ToRemoteValue(object? value) => value switch
    {
        null => null,
        Guid g => FormatGuid(g),
        DateTime dt => FormatDate(dt),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
        _ => value
    };

    private static object? FromRemoteValue(object? raw, Type targetType, bool isNullable)
    {
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (raw == null)
        {
            if (isNullable) return null;
            if (t == typeof(string)) return string.Empty;
            if (t == typeof(DateTime)) return DateTime.MinValue;
            return Activator.CreateInstance(t);
        }

        var text = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;

        if (t == typeof(string)) return text;
        if (t == typeof(bool)) return raw is long l ? l != 0 : text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (t == typeof(int)) return raw is long li ? (int)li : (int)ParseDouble(text);
        if (t == typeof(long)) return raw is long ll ? ll : (long)ParseDouble(text);
        if (t == typeof(short)) return (short)ParseDouble(text);
        if (t.IsEnum) return Enum.ToObject(t, raw is long le ? le : (long)ParseDouble(text));
        if (t == typeof(double)) return raw is double d ? d : ParseDouble(text);
        if (t == typeof(float)) return (float)(raw is double df ? df : ParseDouble(text));
        if (t == typeof(decimal))
            return raw is double dd ? (decimal)dd
                : decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec) ? dec : 0m;
        if (t == typeof(DateTime)) return ParseDate(text);
        if (t == typeof(Guid)) return Guid.TryParse(text, out var g) ? g : Guid.Empty;

        return Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0d;
}
