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
    private const string TombstonesTable = SyncSchema.TombstonesTable;
    private const string MetaTable = SyncSchema.MetaTable;
    private const int PushChunkSize = 80;
    private const int PullPageSize = 500;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly TursoSettingsService _settingsService;
    private readonly ClinicProfileService _clinicProfileService;
    private readonly InventoryService? _inventoryService;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Debouncer _autoSyncDebouncer = new();

    private SyncSchema? _schema;
    private string? _verifiedSchemaUrl;
    private string? _registeredDeviceUrl;
    private int _suppressAutoSync;

    // Una sincronización automática que encontró otra en curso se repite al terminar esa (antes se
    // perdía y lo guardado después del corte de la otra no subía hasta el siguiente cambio)
    private int _autoSyncSkipped;

    // Lecturas y escrituras de otras partes (revisión de consultas enviadas) para el monitor de cuota
    private long _externalRowsRead;
    private long _externalRowsWritten;

    // Turso respondió BLOCKED (cuota agotada): la sincronización automática espera hasta esta hora (ticks UTC;
    // 0 = sin pausa). Se lee desde la interfaz y se escribe desde la sincronización: entero de 64 bits atómico
    private long _quotaRetryAtTicks;
    private int _quotaRetryScheduled;

    private DateTime? StoredQuotaRetryAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _quotaRetryAtTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set => Interlocked.Exchange(ref _quotaRetryAtTicks, value?.Ticks ?? 0);
    }

    /// <summary>Crea el cliente HTTP de Turso (reemplazable en pruebas por un servidor simulado).</summary>
    public Func<string, string, TursoClient> ClientFactory { get; set; } = (url, token) => new TursoClient(url, token);

    /// <summary>Reloj de la pausa por cuota agotada (reemplazable en pruebas).</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public TursoSyncStatus Status { get; private set; } = TursoSyncStatus.NotConfigured;
    public string StatusMessage { get; private set; } = "Sin configurar";

    /// <summary>Por qué está detenida la sincronización automática (el freno manda sobre la cuota agotada).</summary>
    public TursoSyncPauseReason PauseReason
    {
        get
        {
            if (_settingsService.CurrentSettings.ConsumptionBrakeTrippedUtc != null) return TursoSyncPauseReason.ConsumptionBrake;
            return QuotaRetryAtUtc != null ? TursoSyncPauseReason.QuotaExhausted : TursoSyncPauseReason.None;
        }
    }

    /// <summary>Hora (UTC) del siguiente intento automático tras BLOCKED; null si la cuota no está agotada (o ya pasó la hora).</summary>
    public DateTime? QuotaRetryAtUtc
    {
        get
        {
            var retryAt = StoredQuotaRetryAtUtc;
            return retryAt != null && Clock.GetUtcNow().UtcDateTime < retryAt.Value ? retryAt : null;
        }
    }

    /// <summary>Detalle de la pausa, para el aviso de Nube y dispositivos y los errores.</summary>
    private string PauseMessage => PauseReason switch
    {
        TursoSyncPauseReason.ConsumptionBrake => TursoConsumptionBrake.PausedMessage,
        TursoSyncPauseReason.QuotaExhausted => $"{TursoException.QuotaExhaustedMessage} Siguiente intento: {RetryAtLocalText}.",
        _ => string.Empty
    };

    /// <summary>La pausa en una línea, para el estado (barra de estado y resumen de Configuración).</summary>
    private string PauseTitle => PauseReason switch
    {
        TursoSyncPauseReason.ConsumptionBrake => TursoConsumptionBrake.PausedTitle,
        TursoSyncPauseReason.QuotaExhausted => $"{TursoException.QuotaExhaustedTitle} · siguiente intento a las {RetryAtLocalText}",
        _ => string.Empty
    };

    private string RetryAtLocalText => (QuotaRetryAtUtc ?? Clock.GetUtcNow().UtcDateTime).ToLocalTime().ToString("HH:mm");

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
        else if (PauseReason != TursoSyncPauseReason.None)
        {
            SetStatus(TursoSyncStatus.Paused, PauseTitle);
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

        // Cuota agotada o freno de consumo: solo lo que pide el médico (o la consulta entre equipos) sigue
        if (isBackgroundAutoSync && PauseReason != TursoSyncPauseReason.None)
        {
            return Task.FromResult(new TursoSyncResult { Success = false, ErrorMessage = PauseMessage });
        }

        return RunExclusiveAsync(isBackgroundAutoSync, fullResync: false, ct, lockWait);
    }

    /// <summary>Suma al monitor de cuota lo leído y escrito fuera de la sincronización.</summary>
    public void AddExternalUsage(long rowsRead, long rowsWritten)
    {
        var pendingRead = Interlocked.Add(ref _externalRowsRead, rowsRead);
        var pendingWritten = Interlocked.Add(ref _externalRowsWritten, rowsWritten);

        // La revisión de consultas no pasa por la sincronización: un ciclo descontrolado ahí también frena
        if (_settingsService.CurrentSettings.ConsumptionBrakeTrippedUtc != null) return;
        var today = _settingsService.GetQuotaSummary();
        if (today.RowsReadToday + pendingRead > TursoConsumptionBrake.MaxRowsReadPerDay
            || today.RowsWrittenToday + pendingWritten > TursoConsumptionBrake.MaxRowsWrittenPerDay)
        {
            TripConsumptionBrake("la revisión de consultas entre equipos pasó el tope diario");
        }
    }

    /// <summary>
    /// Para lo automático que no es la sincronización (la revisión de consultas entre equipos): lanza si la
    /// sincronización automática está detenida, sin llegar a Turso.
    /// </summary>
    public void ThrowIfAutomaticSyncPaused()
    {
        var reason = PauseReason;
        if (reason == TursoSyncPauseReason.None) return;
        throw new TursoException(PauseMessage,
            reason == TursoSyncPauseReason.QuotaExhausted ? TursoException.QuotaExhaustedCode : TursoException.ConsumptionBrakeCode);
    }

    /// <summary>"Reanudar" después del freno de consumo: el día se vuelve a contar desde cero.</summary>
    public void ResumeAfterConsumptionBrake()
    {
        // Lo que la revisión de consultas leyó y todavía no se anotaba cuenta para el mes, no para el día nuevo
        // (si no, el freno se volvería a activar en la siguiente revisión)
        _settingsService.RecordUsage(Interlocked.Exchange(ref _externalRowsRead, 0), Interlocked.Exchange(ref _externalRowsWritten, 0));
        _settingsService.ResumeAfterConsumptionBrake();
        AppLogger.Warn("Turso: el médico reanudó la sincronización después del freno de consumo.");
        if (Status == TursoSyncStatus.Syncing) return;
        if (PauseReason == TursoSyncPauseReason.QuotaExhausted)
            SetStatus(TursoSyncStatus.Paused, PauseTitle);
        else
            SetStatus(TursoSyncStatus.Idle, "Sincronización reanudada");
    }

    private void TripConsumptionBrake(string why)
    {
        if (_settingsService.CurrentSettings.ConsumptionBrakeTrippedUtc != null) return;
        _settingsService.TripConsumptionBrake();
        var q = _settingsService.GetQuotaSummary();
        AppLogger.Warn($"Turso: freno de consumo activado ({why}). Hoy: {q.RowsReadToday:N0} lecturas, {q.RowsWrittenToday:N0} escrituras, " +
                       $"{q.FullPullsToday} descargas completas.");
        if (Status != TursoSyncStatus.Syncing) SetStatus(TursoSyncStatus.Paused, TursoConsumptionBrake.PausedTitle);
    }

    /// <summary>Activa el freno si el consumo estimado de hoy pasó un tope.</summary>
    private void CheckDailyConsumption()
    {
        var q = _settingsService.GetQuotaSummary();
        if (q.RowsReadToday > TursoConsumptionBrake.MaxRowsReadPerDay)
            TripConsumptionBrake($"más de {TursoConsumptionBrake.MaxRowsReadPerDay:N0} lecturas en el día");
        else if (q.RowsWrittenToday > TursoConsumptionBrake.MaxRowsWrittenPerDay)
            TripConsumptionBrake($"más de {TursoConsumptionBrake.MaxRowsWrittenPerDay:N0} escrituras en el día");
    }

    /// <summary>Tras BLOCKED: sin sincronización automática durante una hora, y un intento solo al terminar.</summary>
    private void PauseForExhaustedQuota()
    {
        StoredQuotaRetryAtUtc = Clock.GetUtcNow().UtcDateTime + TursoConsumptionBrake.QuotaRetryInterval;
        AppLogger.Warn($"Turso respondió BLOCKED (cuota mensual agotada): sincronización automática en pausa hasta {StoredQuotaRetryAtUtc:HH:mm} UTC.");

        if (Interlocked.Exchange(ref _quotaRetryScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TursoConsumptionBrake.QuotaRetryInterval, Clock);
                Interlocked.Exchange(ref _quotaRetryScheduled, 0);
                await SyncAsync(isBackgroundAutoSync: true);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _quotaRetryScheduled, 0);
                AppLogger.Warn($"Turso: el reintento tras la cuota agotada no pudo completarse: {ex.Message}");
            }
        });
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

            var parkedRows = RefreshParkedCount();
            var result = new TursoSyncResult
            {
                Success = true,
                RowsPushed = pushed,
                RowsPulled = pulled,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                ParkedRows = parkedRows
            };

            // Turso volvió a aceptar instrucciones: termina la pausa por cuota agotada
            StoredQuotaRetryAtUtc = null;
            if (PauseReason == TursoSyncPauseReason.ConsumptionBrake)
                SetStatus(TursoSyncStatus.Paused, TursoConsumptionBrake.PausedTitle);
            else
                SetStatus(TursoSyncStatus.Success, $"✓ Sincronizado ({pushed} subidos, {pulled} recibidos • {sw.ElapsedMilliseconds} ms)" +
                    (parkedRows > 0 ? $" · {ParkedText(parkedRows)}" : string.Empty));
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
            var code = (ex as TursoException)?.ErrorCode;
            if (code is TursoException.QuotaExhaustedCode)
            {
                PauseForExhaustedQuota();
                SetStatus(TursoSyncStatus.Paused, PauseTitle);
            }
            else if (code is TursoException.ConsumptionBrakeCode)
            {
                SetStatus(TursoSyncStatus.Paused, TursoConsumptionBrake.PausedTitle);
            }
            else
            {
                AppLogger.Error("Falló la sincronización con Turso", ex);
                var offline = code is "NETWORK_ERROR" or "TIMEOUT";
                SetStatus(offline ? TursoSyncStatus.Offline : TursoSyncStatus.Error,
                    code is TursoException.ArchivedCode ? ex.Message : $"Error de sincronización: {ex.Message}");
            }

            var result = new TursoSyncResult
            {
                Success = false,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message
            };
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

        // Una sincronización completa (o la primera) sube y baja todo: el freno la revisa antes de empezar
        bool fullPull = fullResync || settings.LastPulledServerWatermark == null;
        if (fullPull) EnsureFullPullAllowed();

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
        var lastPulled = fullResync ? null : settings.LastPulledServerWatermark;
        if (fullPull) _settingsService.RecordFullPull(); // se cuenta al empezar a bajar, cuando la red ya respondió
        var watermarkRows = await client.QueryAsync($"SELECT strftime('%Y-%m-%d %H:%M:%f','now','-5 seconds') AS wm;", null, ct);
        var newWatermark = watermarkRows.FirstOrDefault()?["wm"]?.ToString();
        var (pulled, rowsRead) = await PullRemoteChangesAsync(client, settings.DeviceId, lastPulled, skipOwnRows: !fullResync, ct);

        // 3. Perfil del consultorio (último en modificarse gana)
        await SyncClinicProfileAsync(client, ct);

        settings.LastPushedLocalUtc = cutoffUtc;
        if (!string.IsNullOrWhiteSpace(newWatermark)) settings.LastPulledServerWatermark = newWatermark;
        settings.LastSyncUtc = DateTime.UtcNow;
        _settingsService.RecordUsage(rowsRead + 3 + Interlocked.Exchange(ref _externalRowsRead, 0),
            pushed + 1 + Interlocked.Exchange(ref _externalRowsWritten, 0));
        _settingsService.SaveSettings(settings);
        CheckDailyConsumption();

        return (pushed, pulled);
    }

    /// <summary>
    /// Una descarga completa lee toda la nube (~20–30k filas): como máximo <see cref="TursoConsumptionBrake.MaxFullPullsPerDay"/>
    /// al día. La siguiente activa el freno y no se hace (un error que borre la marca de agua en cada vuelta
    /// gastaría la cuota del mes en horas).
    /// </summary>
    private void EnsureFullPullAllowed()
    {
        if (_settingsService.CurrentSettings.ConsumptionBrakeTrippedUtc != null
            || _settingsService.GetQuotaSummary().FullPullsToday >= TursoConsumptionBrake.MaxFullPullsPerDay)
        {
            TripConsumptionBrake($"se pidió una descarga completa más después de {TursoConsumptionBrake.MaxFullPullsPerDay} en el día");
            throw new TursoException(TursoConsumptionBrake.PausedMessage, TursoException.ConsumptionBrakeCode);
        }
    }

    // =========================================================================
    // MAPEO DEL MODELO Y ESQUEMA REMOTO (Services/SyncSchema.cs)
    // =========================================================================

    private SyncSchema GetSchema()
    {
        if (_schema != null) return _schema;
        using var db = _contextFactory.CreateDbContext();
        return _schema = SyncSchema.FromModel(db.Model);
    }

    private IReadOnlyList<TableMap> GetTableMaps() => GetSchema().Tables;

    private static string Q(string identifier) => SyncSchema.Q(identifier);

    /// <summary>
    /// Crea o actualiza el esquema remoto. Las tablas creadas por versiones 1.8.x (con Id entero y sin
    /// SyncedAtUtc) se renombran a "legacy_v1_*" sin borrar nada, y se crean las tablas nuevas.
    /// Solo consulta la estructura cuando cambia la huella del esquema (ahorra cuota del plan gratuito).
    /// </summary>
    public async Task EnsureRemoteSchemaAsync(TursoClient client, CancellationToken ct = default)
    {
        if (_verifiedSchemaUrl == client.DatabaseUrl) return;

        var schema = GetSchema();
        var statements = schema.Statements;
        var fingerprint = schema.Fingerprint;

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

        // La revisión del contrato (contracts/sync/schema.json) va antes que la huella: si la petición se
        // corta a la mitad, la siguiente sincronización vuelve a escribir las dos
        ddl.Add(($"INSERT INTO {Q(MetaTable)} (\"Key\", \"Value\") VALUES ('contract', ?) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";",
            new object?[] { SyncContract.Revision.ToString(CultureInfo.InvariantCulture) }));
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

        // Lo apartado en subidas anteriores se vuelve a intentar aunque el cursor ya lo haya pasado
        var parkedBefore = await db.SyncParkedRows.AsNoTracking()
            .Where(p => p.Direction == SyncParkedRow.Push)
            .ToListAsync(ct);
        var parkNow = new Dictionary<(string EntityType, Guid SyncId), string>();
        var included = new HashSet<(string EntityType, Guid SyncId)>();

        foreach (var map in GetTableMaps())
        {
            var parkedIds = parkedBefore.Where(p => p.EntityType == map.ClrType.Name).Select(p => p.SyncId).ToList();
            var changed = await LoadChangedAsync(db, map.ClrType, fromUtc, toUtc, parkedIds, ct);
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

                string? missing = null;
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
                        missing = $"le falta {reference.PrincipalType.Name} (Id local {localId})";
                        break;
                    }
                    args.Add(principalSyncId.HasValue ? FormatGuid(principalSyncId.Value) : null);
                }

                if (missing != null)
                {
                    // Se aparta: se reintenta en cada subida hasta que exista su principal
                    parkNow[(map.ClrType.Name, entity.SyncId)] = $"No se pudo subir: {missing}.";
                    continue;
                }
                included.Add((map.ClrType.Name, entity.SyncId));
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
            statements.Add((SyncSchema.TombstoneUpsertSql,
                new object?[] { t.EntityType, FormatGuid(t.SyncId), FormatDate(t.DeletedAtUtc), deviceId }));

            if (tablesByType.TryGetValue(t.EntityType, out var table))
            {
                statements.Add((SyncSchema.DeleteTombstonedRowSql(table),
                    new object?[] { FormatGuid(t.SyncId), FormatDate(t.DeletedAtUtc) }));
            }
        }

        // Se cuentan las filas que la nube sí cambió: reenviar algo que ya tenía no cuenta como subido
        int applied = 0;
        for (int i = 0; i < statements.Count; i += PushChunkSize)
        {
            applied += await client.ExecuteBatchAsync(statements.Skip(i).Take(PushChunkSize), ct);
        }

        // Ya subió todo: lo que se pudo subir sale de las apartadas y lo que no, se aparta (o suma un intento).
        // Una apartada cuya fila ya no existe aquí (se borró) también sale: su lápida sube por su lado.
        await UpdateParkedPushAsync(db, parkedBefore, parkNow, included, ct);
        return applied;
    }

    private async Task UpdateParkedPushAsync(AppDbContext db, List<SyncParkedRow> parkedBefore,
        Dictionary<(string EntityType, Guid SyncId), string> parkNow, HashSet<(string EntityType, Guid SyncId)> included, CancellationToken ct)
    {
        if (parkedBefore.Count == 0 && parkNow.Count == 0) return;

        var nowUtc = DateTime.UtcNow;
        var tracked = await db.SyncParkedRows.Where(p => p.Direction == SyncParkedRow.Push).ToListAsync(ct);
        foreach (var row in tracked)
        {
            var key = (row.EntityType, row.SyncId);
            if (parkNow.TryGetValue(key, out var reason))
            {
                row.Reason = reason;
                row.Attempts++;
                row.LastAttemptUtc = nowUtc;
                parkNow.Remove(key);
            }
            else
            {
                if (included.Contains(key)) AppLogger.Info($"Turso: {row.EntityType} {row.SyncId} apartado subió.");
                db.SyncParkedRows.Remove(row);
            }
        }

        foreach (var ((entityType, syncId), reason) in parkNow)
        {
            AppLogger.Warn($"Turso: {entityType} {syncId} apartado. {reason}");
            db.SyncParkedRows.Add(new SyncParkedRow
            {
                Direction = SyncParkedRow.Push,
                EntityType = entityType,
                SyncId = syncId,
                Reason = reason,
                FirstParkedAtUtc = nowUtc,
                LastAttemptUtc = nowUtc
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<List<ISyncEntity>> LoadChangedAsync(AppDbContext db, Type clrType, DateTime fromUtc, DateTime toUtc,
        List<Guid> parkedIds, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(LoadChangedGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(clrType);
        return await (Task<List<ISyncEntity>>)method.Invoke(null, new object[] { db, fromUtc, toUtc, parkedIds, ct })!;
    }

    private static async Task<List<ISyncEntity>> LoadChangedGenericAsync<T>(AppDbContext db, DateTime fromUtc, DateTime toUtc,
        List<Guid> parkedIds, CancellationToken ct)
        where T : class, ISyncEntity
    {
        // Sin tope superior: un registro con marca posterior al corte (guardado durante la subida o
        // con marca adelantada para ganarle a un equipo con el reloj adelantado) sube igual; la nube
        // descarta lo que no sea más nuevo, así que volver a subirlo no cambia nada
        var list = await db.Set<T>().AsNoTracking()
            .Where(e => EF.Property<DateTime>(e, nameof(ISyncEntity.UpdatedAtUtc)) > fromUtc
                        || parkedIds.Contains(EF.Property<Guid>(e, nameof(ISyncEntity.SyncId))))
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
                applied += await ApplyPageAsync(map, page, syncIdMaps, deadPrincipals: null, ct);
            }
            // Esta tabla pudo recibir filas nuevas: refrescar su mapa para las tablas dependientes
            syncIdMaps.Remove(map.ClrType);
        }

        await foreach (var page in ReadPagesAsync(client, TombstonesTable, deviceId, fromWatermark, skipOwnRows, ct, keyColumn: "SyncId"))
        {
            rowsRead += page.Count;
            applied += await ApplyTombstonesAsync(page, ct);
        }

        // Lo apartado en esta y en otras sincronizaciones se vuelve a intentar con lo que ya llegó
        var (retried, retryReads) = await RetryParkedPullAsync(client, ct);
        return (applied + retried, rowsRead + retryReads);
    }

    // =========================================================================
    // FILAS APARTADAS (paso 8.4)
    // =========================================================================

    /// <summary>Cuántas filas están apartadas (en pantalla: "N cambios sin sincronizar").</summary>
    public int ParkedCount { get; private set; }

    /// <summary>"1 cambio sin sincronizar" / "N cambios sin sincronizar".</summary>
    public static string ParkedText(int count) => count == 1 ? "1 cambio sin sincronizar" : $"{count} cambios sin sincronizar";

    /// <summary>Vuelve a contar las filas apartadas (0 si la base todavía no tiene la tabla).</summary>
    public int RefreshParkedCount()
    {
        try
        {
            using var db = _contextFactory.CreateDbContext();
            ParkedCount = db.SyncParkedRows.Count();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Turso: no se pudieron contar las filas apartadas: {ex.Message}");
        }
        return ParkedCount;
    }

    /// <summary>Fila remota → texto para guardarla apartada (cada valor como argumento Hrana, sin perder su tipo).</summary>
    private static string SerializeRemoteRow(Dictionary<string, object?> row)
    {
        var json = new System.Text.Json.Nodes.JsonObject();
        foreach (var (column, value) in row) json[column] = TursoClient.FormatArg(value);
        return json.ToJsonString();
    }

    private static Dictionary<string, object?> DeserializeRemoteRow(string payload)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject()) row[column] = TursoClient.ExtractValue(value);
        return row;
    }

    /// <summary>
    /// Reintenta lo apartado al bajar. Antes pregunta (aquí y en la nube) qué principales que faltan tienen lápida:
    /// con lápida, una referencia opcional se aplica en nulo y una fila con su principal obligatorio borrado se descarta.
    /// </summary>
    private async Task<(int Applied, int RowsRead)> RetryParkedPullAsync(TursoClient client, CancellationToken ct)
    {
        List<SyncParkedRow> parked;
        using (var db = _contextFactory.CreateDbContext())
        {
            parked = await db.SyncParkedRows.AsNoTracking().Where(p => p.Direction != SyncParkedRow.Push).ToListAsync(ct);
        }
        if (parked.Count == 0) return (0, 0);

        int applied = 0, rowsRead = 0;
        var maps = GetTableMaps();
        var syncIdMaps = new Dictionary<Type, Dictionary<Guid, int>>();
        var rowsByType = parked.Where(p => p.Direction == SyncParkedRow.Pull && p.Payload != null)
            .GroupBy(p => p.EntityType)
            .ToDictionary(g => g.Key, g => g.Select(p => DeserializeRemoteRow(p.Payload!)).ToList());

        // Principales que faltan aquí: ¿se borraron?
        var missing = new Dictionary<Type, HashSet<Guid>>();
        using (var db = _contextFactory.CreateDbContext())
        {
            foreach (var map in maps.Where(m => rowsByType.ContainsKey(m.ClrType.Name)))
            {
                foreach (var reference in map.References)
                {
                    if (!syncIdMaps.TryGetValue(reference.PrincipalType, out var lookup))
                    {
                        lookup = await LoadSyncIdToIdAsync(db, reference.PrincipalType, ct);
                        syncIdMaps[reference.PrincipalType] = lookup;
                    }
                    foreach (var row in rowsByType[map.ClrType.Name])
                    {
                        if (ParseGuid(row.GetValueOrDefault(reference.RemoteColumn)) is { } principal && !lookup.ContainsKey(principal))
                        {
                            if (!missing.TryGetValue(reference.PrincipalType, out var set)) missing[reference.PrincipalType] = set = new HashSet<Guid>();
                            set.Add(principal);
                        }
                    }
                }
            }
        }

        var dead = new HashSet<(Type, Guid)>();
        if (missing.Count > 0)
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var (type, ids) in missing)
            {
                var idList = ids.ToList();
                foreach (var local in await db.SyncTombstones.AsNoTracking()
                             .Where(t => t.EntityType == type.Name && idList.Contains(t.SyncId)).Select(t => t.SyncId).ToListAsync(ct))
                {
                    dead.Add((type, local));
                }

                var placeholders = string.Join(", ", idList.Select(_ => "?"));
                var remote = await client.QueryAsync(
                    $"SELECT \"SyncId\" FROM {Q(TombstonesTable)} WHERE \"EntityType\" = ? AND \"SyncId\" IN ({placeholders});",
                    new object?[] { type.Name }.Concat(idList.Select(g => (object?)FormatGuid(g))).ToArray(), ct);
                rowsRead += Math.Max(1, remote.Count);
                foreach (var r in remote)
                {
                    if (ParseGuid(r.GetValueOrDefault("SyncId")) is { } g) dead.Add((type, g));
                }
            }
        }

        // Filas, en el orden de dependencias (un principal apartado se aplica antes que sus dependientes)
        syncIdMaps.Clear();
        foreach (var map in maps)
        {
            if (!rowsByType.TryGetValue(map.ClrType.Name, out var rows)) continue;
            applied += await ApplyPageAsync(map, rows, syncIdMaps, dead, ct);
            syncIdMaps.Remove(map.ClrType);
        }

        // Lápidas
        var tombstones = parked.Where(p => p.Direction == SyncParkedRow.Tombstone).Select(p => new Dictionary<string, object?>
        {
            ["EntityType"] = p.EntityType,
            ["SyncId"] = FormatGuid(p.SyncId),
            ["DeletedAtUtc"] = p.Payload
        }).ToList();
        if (tombstones.Count > 0) applied += await ApplyTombstonesAsync(tombstones, ct);

        return (applied, rowsRead);
    }

    /// <summary>Aparta (o actualiza) una fila en el contexto de la página: se guarda junto con lo que sí se aplicó.</summary>
    private static void Park(AppDbContext db, Dictionary<(string, Guid), SyncParkedRow> parked, string direction, string entityType,
        Guid syncId, string? payload, string reason)
    {
        var nowUtc = DateTime.UtcNow;
        if (parked.TryGetValue((entityType, syncId), out var existing))
        {
            existing.Payload = payload;
            existing.Reason = reason;
            existing.Attempts++;
            existing.LastAttemptUtc = nowUtc;
            return;
        }

        AppLogger.Warn($"Turso: {entityType} {syncId} apartado. {reason}");
        var row = new SyncParkedRow
        {
            Direction = direction,
            EntityType = entityType,
            SyncId = syncId,
            Payload = payload,
            Reason = reason,
            FirstParkedAtUtc = nowUtc,
            LastAttemptUtc = nowUtc
        };
        db.SyncParkedRows.Add(row);
        parked[(entityType, syncId)] = row;
    }

    private static void Unpark(AppDbContext db, Dictionary<(string, Guid), SyncParkedRow> parked, string entityType, Guid syncId)
    {
        if (!parked.Remove((entityType, syncId), out var row)) return;
        if (db.Entry(row).State == EntityState.Added) db.Entry(row).State = EntityState.Detached;
        else db.SyncParkedRows.Remove(row);
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

    private async Task<int> ApplyPageAsync(TableMap map, List<Dictionary<string, object?>> rows, Dictionary<Type, Dictionary<Guid, int>> syncIdMaps,
        ISet<(Type, Guid)>? deadPrincipals, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(ApplyPageGenericAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(map.ClrType);
        return await (Task<int>)method.Invoke(this, new object?[] { map, rows, syncIdMaps, deadPrincipals, ct })!;
    }

    /// <summary>
    /// Aplica una página de filas remotas. Una fila cuyo principal (obligatorio u opcional) todavía no existe aquí se
    /// aparta en la misma transacción y se reintenta al final de cada descarga (<see cref="RetryParkedPullAsync"/>).
    /// <paramref name="deadPrincipals"/> son los principales que se sabe que se borraron.
    /// </summary>
    private async Task<int> ApplyPageGenericAsync<T>(TableMap map, List<Dictionary<string, object?>> rows, Dictionary<Type, Dictionary<Guid, int>> syncIdMaps,
        ISet<(Type, Guid)>? deadPrincipals, CancellationToken ct)
        where T : class, ISyncEntity, new()
    {
        using var db = _contextFactory.CreateDbContext();
        db.ApplyingRemoteChanges = true;

        var entityType = map.ClrType.Name;
        var incoming = rows
            .Select(r => (Row: r, SyncId: ParseGuid(r.GetValueOrDefault("SyncId"))))
            .Where(x => x.SyncId.HasValue)
            .ToList();
        var ids = incoming.Select(x => x.SyncId!.Value).ToList();
        var locals = (await db.Set<T>()
                .Where(e => ids.Contains(EF.Property<Guid>(e, nameof(ISyncEntity.SyncId))))
                .ToListAsync(ct))
            .ToDictionary(e => e.SyncId);
        var parked = (await db.SyncParkedRows
                .Where(p => p.Direction == SyncParkedRow.Pull && p.EntityType == entityType && ids.Contains(p.SyncId))
                .ToListAsync(ct))
            .ToDictionary(p => (p.EntityType, p.SyncId));

        int applied = 0;
        foreach (var (row, syncIdNullable) in incoming)
        {
            var syncId = syncIdNullable!.Value;
            var remoteUpdated = ParseDate(row.GetValueOrDefault("UpdatedAtUtc"));
            locals.TryGetValue(syncId, out var local);

            // Last-Write-Wins: la versión local igual o más nueva se conserva (y lo apartado de esta fila ya no hace falta)
            if (!SyncRules.RemoteRowWins(local?.UpdatedAtUtc, remoteUpdated))
            {
                Unpark(db, parked, entityType, syncId);
                continue;
            }

            // Resolver referencias (SyncId remoto → Id local)
            var resolved = new List<(PropertyInfo Property, int? Id)>();
            string? parkReason = null;
            string? dropReason = null;
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

                if (principalSyncId.HasValue && localId == null)
                {
                    if (deadPrincipals?.Contains((reference.PrincipalType, principalSyncId.Value)) == true)
                    {
                        // El principal se borró: una referencia opcional queda en nulo; sin el obligatorio la fila no puede existir
                        if (reference.IsRequired)
                        {
                            dropReason = $"su {reference.PrincipalType.Name} {principalSyncId} se borró";
                            break;
                        }
                    }
                    else
                    {
                        parkReason = $"Todavía no llega a este equipo su {reference.PrincipalType.Name} {FormatGuid(principalSyncId.Value)}.";
                        break;
                    }
                }
                else if (localId == null && reference.IsRequired)
                {
                    dropReason = $"llegó sin {reference.RemoteColumn}";
                    break;
                }
                resolved.Add((reference.Property, localId));
            }

            if (parkReason != null)
            {
                Park(db, parked, SyncParkedRow.Pull, entityType, syncId, SerializeRemoteRow(row), parkReason);
                continue;
            }
            if (dropReason != null)
            {
                AppLogger.Warn($"Turso: {map.Table} {syncId} no se aplicó: {dropReason}.");
                Unpark(db, parked, entityType, syncId);
                continue;
            }

            Unpark(db, parked, entityType, syncId);
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

        Dictionary<(string, Guid), SyncParkedRow> parked;
        using (var db = _contextFactory.CreateDbContext())
        {
            parked = (await db.SyncParkedRows.AsNoTracking().Where(p => p.Direction == SyncParkedRow.Tombstone).ToListAsync(ct))
                .ToDictionary(p => (p.EntityType, p.SyncId));
        }

        foreach (var row in rows)
        {
            var syncId = ParseGuid(row.GetValueOrDefault("SyncId"));
            var entityType = row.GetValueOrDefault("EntityType")?.ToString() ?? string.Empty;
            if (syncId == null || !mapsByType.TryGetValue(entityType, out var map)) continue;

            var deletedAt = ParseDate(row.GetValueOrDefault("DeletedAtUtc"));
            string? failure = null;
            try
            {
                using var db = _contextFactory.CreateDbContext();
                db.ApplyingRemoteChanges = true;
                var method = typeof(TursoSyncService).GetMethod(nameof(DeleteLocalGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(map.ClrType);
                if (await (Task<bool>)method.Invoke(null, new object[] { db, syncId.Value, deletedAt, ct })!) applied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // P. ej. una categoría que todavía tiene productos en este equipo: se aparta y se reintenta
                failure = (ex.InnerException ?? ex).Message;
            }

            // Se borró, ya no estaba o aquí es más nueva: sale de las apartadas. Si falló, se aparta (o suma un intento)
            if (failure == null && !parked.ContainsKey((entityType, syncId.Value))) continue;
            using var parkDb = _contextFactory.CreateDbContext();
            var tracked = parked.TryGetValue((entityType, syncId.Value), out var existing)
                ? new Dictionary<(string, Guid), SyncParkedRow> { [(entityType, syncId.Value)] = parkDb.SyncParkedRows.Attach(existing).Entity }
                : new Dictionary<(string, Guid), SyncParkedRow>();
            if (failure != null)
                Park(parkDb, tracked, SyncParkedRow.Tombstone, entityType, syncId.Value, FormatDate(deletedAt), $"No se pudo aplicar su borrado: {failure}");
            else
                Unpark(parkDb, tracked, entityType, syncId.Value);
            await parkDb.SaveChangesAsync(ct);

            if (failure == null) parked.Remove((entityType, syncId.Value));
            else if (existing == null) parked[(entityType, syncId.Value)] = tracked[(entityType, syncId.Value)];
        }
        return applied;
    }

    private static async Task<bool> DeleteLocalGenericAsync<T>(AppDbContext db, Guid syncId, DateTime deletedAtUtc, CancellationToken ct)
        where T : class, ISyncEntity
    {
        var local = await db.Set<T>().FirstOrDefaultAsync(e => EF.Property<Guid>(e, nameof(ISyncEntity.SyncId)) == syncId, ct);
        // Si aquí se modificó después del borrado remoto, se conserva (LWW)
        if (local == null || !SyncRules.RemoteTombstoneDeletes(local.UpdatedAtUtc, deletedAtUtc)) return false;
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
    public static string FormatGuid(Guid g) => SyncIds.Format(g);

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

    /// <summary>Valor del modelo → valor que se manda a Turso (contracts/golden/values.json, "encode").</summary>
    public static object? ToRemoteValue(object? value) => value switch
    {
        null => null,
        Guid g => FormatGuid(g),
        DateTime dt => FormatDate(dt),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
        _ => value
    };

    /// <summary>
    /// Valor leído de Turso → valor del modelo (contracts/golden/values.json, "decode"). Un nulo en una
    /// columna obligatoria se vuelve "", 0, false o 0001-01-01.
    /// </summary>
    public static object? FromRemoteValue(object? raw, Type targetType, bool isNullable)
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
