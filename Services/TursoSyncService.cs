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
using Microsoft.Data.Sqlite;
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
/// * Qué subir lo dice el registro de cambios local (<see cref="SyncChangeLog"/>, paso 8.5), no el reloj del equipo.
/// </summary>
public class TursoSyncService
{
    public const int RemoteSchemaVersion = 2;

    /// <summary>Hora del servidor (UTC) en el formato de las marcas de sincronización.</summary>
    public const string ServerNowSql = "strftime('%Y-%m-%d %H:%M:%f','now')";
    private const string TombstonesTable = SyncSchema.TombstonesTable;
    private const string MetaTable = SyncSchema.MetaTable;
    private const int PushChunkSize = 80;

    /// <summary>Lo menos a lo que se reduce una página que no se pudo leer por tamaño o tiempo.</summary>
    private const int MinPullPageSize = 10;

    private const string WatermarkSql = "SELECT strftime('%Y-%m-%d %H:%M:%f','now','-5 seconds') AS wm;";
    private const string ClinicProfileSql = "SELECT * FROM ClinicProfiles WHERE Id = 1;";

    /// <summary>
    /// Filas por página al bajar (paso 8.6): 100 en las tablas con columnas grandes (las hojas del paciente y las recetas
    /// van en JSON), 500 en las demás. Turso no puso tope hasta 64 MB por respuesta (V12); el límite es por memoria y
    /// tiempo, sobre todo en Android.
    /// </summary>
    private static int DefaultPullPageSize(string table) => table is "Patients" or "PatientConsultations" ? 100 : 500;

    /// <summary>Para las pruebas: un mismo tamaño de página en todas las tablas.</summary>
    internal int? PullPageSizeOverride { get; set; }

    /// <summary>Para las pruebas: cuántas instrucciones van en cada transacción de la subida.</summary>
    internal int? PushChunkSizeOverride { get; set; }

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly TursoSettingsService _settingsService;
    private readonly ClinicProfileService _clinicProfileService;
    private readonly InventoryService? _inventoryService;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Debouncer _autoSyncDebouncer = new();

    private SyncSchema? _schema;
    private bool _changeLogVerified;
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

        if (!_changeLogVerified)
        {
            // La base ya lo trae desde que abre el programa (DatabaseInitializer); esto cubre una que llegue sin él
            using var db = _contextFactory.CreateDbContext();
            SyncChangeLog.EnsureInstalled(db, backupBeforeFirstInstall: false);
            _changeLogVerified = true;
        }

        // Una sincronización completa (o la primera, sin ningún cursor de bajada) sube y baja todo: el freno la
        // revisa antes de empezar
        var cursors = fullResync ? PullCursors.FromScratch : await LoadPullCursorsAsync(settings, ct);
        bool fullPull = fullResync || cursors.IsFirstDownload;
        if (fullPull) EnsureFullPullAllowed();

        using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);

        await EnsureRemoteSchemaAsync(client, ct);

        if (_registeredDeviceUrl != settings.DatabaseUrl)
        {
            await RegisterDeviceAsync(client, settings, ct);
            _registeredDeviceUrl = settings.DatabaseUrl;
        }

        // 1. PUSH: lo anotado en el registro de cambios desde la última subida (paso 8.5)
        var (pushed, pushReads) = await PushLocalChangesAsync(client, settings.DeviceId, fullResync, ct);

        // 2. PULL y perfil del consultorio: lo que otros equipos escribieron desde el cursor de cada tabla (la marca
        //    de agua se toma ANTES de leer, con 5 s de traslape; los duplicados se descartan por LWW)
        if (fullPull) _settingsService.RecordFullPull(); // se cuenta al empezar a bajar, cuando la red ya respondió
        var (pulled, rowsRead, watermark) = await PullRemoteChangesAsync(client, settings.DeviceId, cursors,
            skipOwnRows: !fullResync, incremental: !fullPull, ct);

        settings.LastPulledServerWatermark = watermark;
        settings.LastSyncUtc = DateTime.UtcNow;
        _settingsService.RecordUsage(rowsRead + pushReads + 3 + Interlocked.Exchange(ref _externalRowsRead, 0),
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

    /// <summary>Una instrucción de la subida: una fila completa o una lápida (con su DELETE).</summary>
    private sealed record PushStatement(string Sql, object?[] Args, TableMap? RowMap, Guid SyncId);

    /// <summary>
    /// Sube lo anotado en <see cref="SyncChangeLog"/> después del cursor (<see cref="DbMeta.LastPushedSeq"/>), tabla por
    /// tabla en el orden del contrato: cada fila que todavía existe aquí sube completa y la que ya no, como su lápida
    /// más reciente. Cada lote va en una transacción (paso 8.6, V14) sin partir una operación: la lápida con su DELETE y
    /// una fila con las que le pertenecen (una venta con sus renglones). El cursor avanza solo si todo subió.
    /// Devuelve las filas que la nube cambió y las que se leyeron para el desempate.
    /// </summary>
    private async Task<(int Applied, int RowsRead)> PushLocalChangesAsync(TursoClient client, string deviceId, bool fullResync, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        var tables = GetTableMaps();

        // Primera subida con el registro (al actualizar, o con una base restaurada) o "Sincronización completa":
        // todo se anota y sube una vez; la nube descarta lo que ya tenía
        long? cursor = fullResync ? null : await SyncChangeLog.ReadPushCursorAsync(db, ct);
        if (cursor == null) await SyncChangeLog.BackfillAsync(db, tables, ct);
        long fromSeq = cursor ?? 0;

        var groups = new List<List<PushStatement>>();
        var parkNow = new Dictionary<(string EntityType, Guid SyncId), string>();
        var included = new HashSet<(string EntityType, Guid SyncId)>();
        List<SyncParkedRow> parkedBefore;
        long toSeq;

        // Una sola foto del registro, sus filas y sus lápidas (solo lectura). Lo que se guarde mientras tanto recibe
        // un Seq mayor que toSeq y sube en la siguiente
        await db.Database.OpenConnectionAsync(ct);
        using (var snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true))
        {
            await db.Database.UseTransactionAsync(snapshot, ct);
            var logged = await SyncChangeLog.ReadSinceAsync(db, fromSeq, ct);
            toSeq = logged.Count > 0 ? logged[^1].Seq : fromSeq;
            var loggedTypes = logged.Select(e => e.EntityType).ToHashSet(StringComparer.Ordinal);

            // Lo apartado en subidas anteriores se vuelve a intentar aunque el cursor ya lo haya pasado
            parkedBefore = await db.SyncParkedRows.AsNoTracking()
                .Where(p => p.Direction == SyncParkedRow.Push)
                .ToListAsync(ct);

            await BuildPushStatementsAsync(db, tables, deviceId, fromSeq, toSeq, loggedTypes, parkedBefore, groups, parkNow, included, ct);
            await db.Database.UseTransactionAsync(null, ct);
        }

        // Se cuentan las filas que la nube sí cambió: reenviar algo que ya tenía no cuenta como subido
        int applied = 0, statementCount = 0;
        var rejected = new List<(TableMap Map, Guid SyncId)>();
        foreach (var chunk in Chunk(groups, PushChunkSizeOverride ?? PushChunkSize))
        {
            var results = await client.TransactionAsync(chunk.Select(s => (s.Sql, (object?[]?)s.Args)), ct);
            for (int i = 0; i < chunk.Count; i++)
            {
                applied += results[i].AffectedRows;
                if (chunk[i].RowMap is { } map && results[i].AffectedRows == 0) rejected.Add((map, chunk[i].SyncId));
            }
            statementCount += chunk.Count;
        }

        // Ya subió todo: lo que se pudo subir sale de las apartadas y lo que no, se aparta (o suma un intento).
        // Una apartada cuya fila ya no existe aquí (se borró) también sale: su lápida sube por su lado.
        await UpdateParkedPushAsync(db, parkedBefore, parkNow, included, ct);
        await SyncChangeLog.SavePushCursorAsync(db, toSeq, ct);
        if (cursor == null)
        {
            AppLogger.Info($"Turso: subida completa con el registro de cambios hasta el Seq {toSeq} ({statementCount} instrucciones, {applied} filas cambiaron en la nube).");
            return (applied, 0);
        }

        // Desempate: lo que la nube no aceptó se vuelve a leer y se aplica aquí. No en la primera subida, en la que casi
        // todo afecta 0 filas (la nube ya lo tenía) y costaría una lectura por fila
        int rowsRead = rejected.Count > 0 ? await ApplyRejectedUpsertsAsync(client, rejected, ct) : 0;
        return (applied, rowsRead);
    }

    /// <summary>Lotes de la subida sin partir un grupo (uno más grande que el lote va solo).</summary>
    private static IEnumerable<List<PushStatement>> Chunk(List<List<PushStatement>> groups, int size)
    {
        var chunk = new List<PushStatement>();
        foreach (var group in groups)
        {
            if (chunk.Count > 0 && chunk.Count + group.Count > size)
            {
                yield return chunk;
                chunk = new List<PushStatement>();
            }
            chunk.AddRange(group);
        }
        if (chunk.Count > 0) yield return chunk;
    }

    /// <summary>
    /// Desempate (paso 8.6, <see cref="SyncRules.AfterRejectedUpsert"/>): si la nube no aceptó una fila (tenía una igual
    /// o más nueva, o una lápida), se vuelve a leer de la nube y se aplica aquí aunque las marcas empaten; si ya no la
    /// tiene, se aplica su lápida. Así dos equipos que guardaron con la misma marca terminan iguales. Una sola petición.
    /// </summary>
    private async Task<int> ApplyRejectedUpsertsAsync(TursoClient client, List<(TableMap Map, Guid SyncId)> rejected, CancellationToken ct)
    {
        var batches = rejected
            .GroupBy(r => r.Map)
            .SelectMany(g => g.Select(r => r.SyncId).Distinct().Chunk(200).Select(ids => (Map: g.Key, Ids: ids)))
            .ToList();

        var statements = new List<(string Sql, object?[]? Args)>();
        foreach (var (map, ids) in batches)
        {
            var args = ids.Select(id => (object?)FormatGuid(id)).ToArray();
            var placeholders = string.Join(", ", args.Select(_ => "?"));
            statements.Add(($"SELECT * FROM {Q(map.Table)} WHERE \"SyncId\" IN ({placeholders});", args));
            statements.Add(($"SELECT \"EntityType\", \"SyncId\", \"DeletedAtUtc\" FROM {Q(TombstonesTable)} WHERE \"EntityType\" = ? AND \"SyncId\" IN ({placeholders});",
                new object?[] { map.ClrType.Name }.Concat(args).ToArray()));
        }
        var results = await client.QueryBatchAsync(statements, ct);

        int rowsRead = 0;
        var syncIdMaps = new Dictionary<Type, Dictionary<Guid, int>>();
        for (int i = 0; i < batches.Count; i++)
        {
            var map = batches[i].Map;
            var rows = results[2 * i];
            var tombstones = results[2 * i + 1];
            rowsRead += Math.Max(1, rows.Count) + Math.Max(1, tombstones.Count);

            if (rows.Count > 0)
            {
                await ApplyPageAsync(map, rows, syncIdMaps, null, ct, serverRowAfterReject: true);
                syncIdMaps.Remove(map.ClrType);
            }
            var present = rows.Select(r => ParseGuid(r.GetValueOrDefault("SyncId"))).ToHashSet();
            var deleted = tombstones.Where(t => !present.Contains(ParseGuid(t.GetValueOrDefault("SyncId")))).ToList();
            if (deleted.Count > 0) await ApplyTombstonesAsync(deleted, ct);
        }
        AppLogger.Info($"Turso: {rejected.Count} filas que la nube no aceptó se releyeron y se aplicaron aquí.");
        return rowsRead;
    }

    private async Task BuildPushStatementsAsync(AppDbContext db, IReadOnlyList<TableMap> tables, string deviceId, long fromSeq, long toSeq,
        HashSet<string> loggedTypes, List<SyncParkedRow> parkedBefore, List<List<PushStatement>> groups,
        Dictionary<(string EntityType, Guid SyncId), string> parkNow, HashSet<(string EntityType, Guid SyncId)> included, CancellationToken ct)
    {
        var idMaps = new Dictionary<Type, Dictionary<int, Guid>>();
        var found = new HashSet<(string EntityType, Guid SyncId)>();

        // El grupo (la operación) de cada fila que sube: una fila con referencia obligatoria y borrado en cascada a otra
        // que también sube va en el grupo de esa (la venta con sus renglones, el producto con sus lotes)
        var groupOf = new Dictionary<(Type, Guid), List<PushStatement>>();

        foreach (var map in tables)
        {
            var parkedIds = parkedBefore.Where(p => p.EntityType == map.ClrType.Name).Select(p => p.SyncId).ToList();
            if (parkedIds.Count == 0 && !loggedTypes.Contains(map.ClrType.Name)) continue;
            var changed = await LoadLoggedAsync(db, map, fromSeq, toSeq, parkedIds, ct);
            if (changed.Count == 0) continue;

            var sql = map.UpsertSql;
            foreach (var entity in changed)
            {
                found.Add((map.ClrType.Name, entity.SyncId));
                var args = new List<object?>
                {
                    FormatGuid(entity.SyncId),
                    FormatDate(entity.UpdatedAtUtc),
                    deviceId
                };
                args.AddRange(map.Columns.Select(c => ToRemoteValue(c.Property.GetValue(entity))));

                string? missing = null;
                List<PushStatement>? owner = null;
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
                    if (owner == null && principalSyncId is { } principal && reference.IsRequired && reference.DeleteBehavior == DeleteBehavior.Cascade)
                    {
                        groupOf.TryGetValue((reference.PrincipalType, principal), out owner);
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

                if (owner == null)
                {
                    owner = new List<PushStatement>();
                    groups.Add(owner);
                }
                owner.Add(new PushStatement(sql, args.ToArray(), map, entity.SyncId));
                groupOf[(map.ClrType, entity.SyncId)] = owner;
            }
        }

        // Borrados: lo anotado que ya no existe aquí sube como su lápida más reciente (también los borrados en
        // cascada, que anotó el trigger de cada tabla hija). La nube lo aplica salvo que tenga una versión más nueva
        var tombstones = (await db.SyncTombstones.FromSqlRaw(SyncChangeLog.LoggedTombstonesSql, fromSeq, toSeq).AsNoTracking().ToListAsync(ct))
            .Where(t => !found.Contains((t.EntityType, t.SyncId)))
            .GroupBy(t => (t.EntityType, t.SyncId))
            .Select(g => g.MaxBy(t => t.DeletedAtUtc)!);
        var tablesByType = tables.ToDictionary(m => m.ClrType.Name, m => m.Table);
        foreach (var t in tombstones)
        {
            // La lápida y su DELETE, en el mismo lote
            var group = new List<PushStatement>
            {
                new(SyncSchema.TombstoneUpsertSql, new object?[] { t.EntityType, FormatGuid(t.SyncId), FormatDate(t.DeletedAtUtc), deviceId }, null, t.SyncId)
            };
            if (tablesByType.TryGetValue(t.EntityType, out var table))
            {
                group.Add(new(SyncSchema.DeleteTombstonedRowSql(table), new object?[] { FormatGuid(t.SyncId), FormatDate(t.DeletedAtUtc) }, null, t.SyncId));
            }
            groups.Add(group);
        }
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

    private static async Task<List<ISyncEntity>> LoadLoggedAsync(AppDbContext db, TableMap map, long fromSeq, long toSeq,
        List<Guid> parkedIds, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(LoadLoggedGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(map.ClrType);
        return await (Task<List<ISyncEntity>>)method.Invoke(null, new object[] { db, map, fromSeq, toSeq, parkedIds, ct })!;
    }

    /// <summary>Las filas de la tabla anotadas en (fromSeq, toSeq], más las apartadas al subir.</summary>
    private static async Task<List<ISyncEntity>> LoadLoggedGenericAsync<T>(AppDbContext db, TableMap map, long fromSeq, long toSeq,
        List<Guid> parkedIds, CancellationToken ct)
        where T : class, ISyncEntity
    {
        var list = await db.Set<T>()
            .FromSqlRaw(SyncChangeLog.LoggedRowsSql(map.Table), map.ClrType.Name, fromSeq, toSeq)
            .AsNoTracking()
            .ToListAsync(ct);

        var parkedMissing = parkedIds.Except(list.Select(e => e.SyncId)).ToList();
        if (parkedMissing.Count > 0)
        {
            list.AddRange(await db.Set<T>().AsNoTracking()
                .Where(e => parkedMissing.Contains(EF.Property<Guid>(e, nameof(ISyncEntity.SyncId))))
                .ToListAsync(ct));
        }
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

    /// <summary>
    /// Posición en una tabla remota, por (SyncedAtUtc, SyncId): lo que va antes ya se aplicó aquí. Se compara como texto,
    /// igual que en SQLite (las marcas tienen el formato del protocolo).
    /// </summary>
    internal readonly record struct PullCursor(string SyncedAtUtc, string Key)
    {
        public static readonly PullCursor Start = new(string.Empty, string.Empty);

        public string Serialize() => $"{SyncedAtUtc}|{Key}";

        public static PullCursor? Parse(string? text)
        {
            int bar = text?.IndexOf('|') ?? -1;
            return bar < 0 ? null : new PullCursor(text![..bar], text[(bar + 1)..]);
        }

        public static PullCursor Min(PullCursor a, PullCursor b)
        {
            int c = string.CompareOrdinal(a.SyncedAtUtc, b.SyncedAtUtc);
            if (c == 0) c = string.CompareOrdinal(a.Key, b.Key);
            return c <= 0 ? a : b;
        }
    }

    /// <summary>
    /// Los cursores de bajada guardados en <see cref="DbMeta"/>. Al actualizar desde v1.20.3 o antes no hay ninguno: la
    /// marca de agua de la configuración vale para todas las tablas, así nadie vuelve a bajar todo. Una tabla nueva
    /// (sin cursor, cuando las demás sí tienen) empieza desde el principio.
    /// </summary>
    private sealed class PullCursors(Dictionary<string, PullCursor> saved, PullCursor? fromSettings)
    {
        public static PullCursors FromScratch => new(new Dictionary<string, PullCursor>(), null);

        public bool IsFirstDownload => saved.Count == 0 && fromSettings == null;

        public PullCursor For(string table) =>
            saved.TryGetValue(table, out var cursor) ? cursor : saved.Count == 0 ? fromSettings ?? PullCursor.Start : PullCursor.Start;
    }

    private async Task<PullCursors> LoadPullCursorsAsync(TursoSettings settings, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        var saved = new Dictionary<string, PullCursor>(StringComparer.Ordinal);
        foreach (var (key, value) in await DbMeta.GetByPrefixAsync(db, DbMeta.PullCursorPrefix, ct))
        {
            if (PullCursor.Parse(value) is { } cursor) saved[key[DbMeta.PullCursorPrefix.Length..]] = cursor;
        }
        var fromSettings = string.IsNullOrWhiteSpace(settings.LastPulledServerWatermark)
            ? (PullCursor?)null
            : new PullCursor(settings.LastPulledServerWatermark, string.Empty);
        return new PullCursors(saved, fromSettings);
    }

    /// <summary>
    /// Baja lo que otros equipos escribieron, tabla por tabla en el orden del contrato y por páginas (paso 8.6).
    /// La bajada incremental empieza con una sola petición, una transacción de lectura (una sola foto, V14): la marca de
    /// agua, la primera página de las 13 tablas y de las lápidas y el perfil del consultorio. Solo se piden más páginas
    /// de las tablas que llenaron la suya. La descarga completa va una tabla por petición. El cursor de cada tabla se
    /// guarda con cada página aplicada, sin pasar de la marca de agua: un corte a media descarga sigue donde iba.
    /// </summary>
    private async Task<(int Applied, int RowsRead, string Watermark)> PullRemoteChangesAsync(
        TursoClient client, string deviceId, PullCursors cursors, bool skipOwnRows, bool incremental, CancellationToken ct)
    {
        var tables = GetTableMaps();
        var pageSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        int applied = 0, rowsRead = 0;
        string? watermark = null;
        Dictionary<string, object?>? profileRow = null;
        Dictionary<string, List<Dictionary<string, object?>>>? firstPages = null;

        if (incremental)
        {
            var statements = new List<(string Sql, object?[]? Args)> { (WatermarkSql, null) };
            foreach (var table in tables.Select(m => m.Table).Append(TombstonesTable))
            {
                statements.Add(PageQuery(table, cursors.For(table), deviceId, skipOwnRows, PageSize(pageSizes, table)));
            }
            statements.Add((ClinicProfileSql, null));
            try
            {
                var results = await client.TransactionAsync(statements, ct);
                watermark = results[0].Rows.FirstOrDefault()?["wm"]?.ToString();
                firstPages = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
                int i = 1;
                foreach (var table in tables.Select(m => m.Table).Append(TombstonesTable)) firstPages[table] = results[i++].Rows;
                profileRow = results[^1].Rows.FirstOrDefault();
            }
            catch (TursoException ex) when (IsTooLargeOrSlow(ex))
            {
                // Demasiado para una sola respuesta: como la descarga completa, una tabla por petición
                AppLogger.Warn($"Turso: la bajada en una sola petición no se pudo leer ({ex.Message}); se baja tabla por tabla.");
                watermark = null;
                firstPages = null;
            }
        }

        if (watermark == null)
        {
            var results = await client.QueryBatchAsync(new (string, object?[]?)[] { (WatermarkSql, null), (ClinicProfileSql, null) }, ct);
            watermark = results[0].FirstOrDefault()?["wm"]?.ToString();
            profileRow = results[1].FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(watermark)) throw new TursoException("Turso no regresó su hora para la marca de agua.");

        var upTo = new PullCursor(watermark, string.Empty);
        var idleCursors = new Dictionary<string, string>(StringComparer.Ordinal);
        var syncIdMaps = new Dictionary<Type, Dictionary<Guid, int>>();
        foreach (var map in tables)
        {
            var (a, r) = await PullTableAsync(client, map.Table, cursors.For(map.Table), firstPages?[map.Table], upTo, deviceId, skipOwnRows,
                pageSizes, idleCursors, (page, cursor) => ApplyPageAsync(map, page, syncIdMaps, deadPrincipals: null, ct, cursor), ct);
            applied += a;
            rowsRead += r;
            // Esta tabla pudo recibir filas nuevas: refrescar su mapa para las tablas dependientes
            syncIdMaps.Remove(map.ClrType);
        }

        var (tombstonesApplied, tombstonesRead) = await PullTableAsync(client, TombstonesTable, cursors.For(TombstonesTable), firstPages?[TombstonesTable],
            upTo, deviceId, skipOwnRows, pageSizes, idleCursors, async (page, cursor) =>
            {
                int n = await ApplyTombstonesAsync(page, ct);
                await SavePullCursorsAsync(cursor, ct);
                return n;
            }, ct);
        applied += tombstonesApplied;
        rowsRead += tombstonesRead;

        // Las tablas sin nada nuevo avanzan hasta la marca de agua, todas en una escritura
        await SavePullCursorsAsync(idleCursors, ct);

        // Lo apartado en esta y en otras sincronizaciones se vuelve a intentar con lo que ya llegó
        var (retried, retryReads) = await RetryParkedPullAsync(client, ct);

        // Perfil del consultorio (último en modificarse gana)
        await SyncClinicProfileAsync(client, profileRow, ct);

        return (applied + retried, rowsRead + retryReads, watermark);
    }

    /// <summary>
    /// Las páginas de una tabla desde <paramref name="from"/>: la primera puede venir ya leída (la foto de la bajada
    /// incremental) y se piden más mientras llegue llena. Cada página se aplica junto con su cursor.
    /// </summary>
    private async Task<(int Applied, int RowsRead)> PullTableAsync(TursoClient client, string table, PullCursor from,
        List<Dictionary<string, object?>>? firstPage, PullCursor upTo, string deviceId, bool skipOwnRows, Dictionary<string, int> pageSizes,
        Dictionary<string, string> idleCursors, Func<List<Dictionary<string, object?>>, IReadOnlyDictionary<string, string>, Task<int>> apply,
        CancellationToken ct)
    {
        int applied = 0, rowsRead = 0;
        var position = from;
        var page = firstPage;
        int size = PageSize(pageSizes, table);
        var key = DbMeta.PullCursorPrefix + table;

        while (true)
        {
            if (page == null) (page, size) = await ReadPageAsync(client, table, position, deviceId, skipOwnRows, pageSizes, ct);
            rowsRead += page.Count;
            bool full = page.Count >= size;
            if (page.Count > 0)
            {
                var last = page[^1];
                position = new PullCursor(last.GetValueOrDefault("SyncedAtUtc")?.ToString() ?? string.Empty, last.GetValueOrDefault("SyncId")?.ToString() ?? string.Empty);
            }

            // Lo aplicado, sin pasar de la marca de agua: algo escrito justo antes de ella puede llegar todavía. Al acabar
            // la tabla, la marca de agua
            var cursor = full ? PullCursor.Min(position, upTo) : upTo;
            var meta = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = cursor.Serialize() };
            if (page.Count > 0)
            {
                applied += await apply(page, meta);
                idleCursors.Remove(key);
            }
            else
            {
                idleCursors[key] = cursor.Serialize();
            }

            if (!full) return (applied, rowsRead);
            page = null;
        }
    }

    private int PageSize(Dictionary<string, int> pageSizes, string table) =>
        pageSizes.TryGetValue(table, out var size) ? size : PullPageSizeOverride ?? DefaultPullPageSize(table);

    /// <summary>
    /// Una página desde la posición dada. Si la respuesta es demasiado grande o tarda demasiado, la página de esa tabla
    /// se reduce a la mitad (hasta <see cref="MinPullPageSize"/>) y se vuelve a pedir.
    /// </summary>
    private async Task<(List<Dictionary<string, object?>> Rows, int Size)> ReadPageAsync(TursoClient client, string table, PullCursor from,
        string deviceId, bool skipOwnRows, Dictionary<string, int> pageSizes, CancellationToken ct)
    {
        while (true)
        {
            int size = PageSize(pageSizes, table);
            try
            {
                var (sql, args) = PageQuery(table, from, deviceId, skipOwnRows, size);
                return (await client.QueryAsync(sql, args, ct), size);
            }
            catch (TursoException ex) when (IsTooLargeOrSlow(ex) && size > MinPullPageSize)
            {
                pageSizes[table] = Math.Max(MinPullPageSize, size / 2);
                AppLogger.Warn($"Turso: la página de {table} con {size} filas no se pudo leer ({ex.Message}); se pide con {pageSizes[table]}.");
            }
        }
    }

    private static bool IsTooLargeOrSlow(TursoException ex) =>
        ex.ErrorCode is "TIMEOUT" or "RESPONSE_TOO_LARGE" or "HTTP_413"
        || ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Consulta de una página. "SyncedAtUtc" >= ? va adelante para que SQLite recorra el índice en orden desde el
    /// cursor (comprobado en Turso, V15): sin él usaba "MULTI-INDEX OR" y ordenaba todo lo que queda de la tabla en
    /// cada página (la primera de 20k filas leía 26,666; ahora ~2,000).
    /// </summary>
    internal static (string Sql, object?[] Args) PageQuery(string table, PullCursor from, string deviceId, bool skipOwnRows, int size)
    {
        var origin = skipOwnRows ? " AND (\"OriginDeviceId\" IS NULL OR \"OriginDeviceId\" <> ?)" : string.Empty;
        var sql = $"SELECT * FROM {Q(table)} WHERE \"SyncedAtUtc\" >= ? AND (\"SyncedAtUtc\" > ? OR (\"SyncedAtUtc\" = ? AND \"SyncId\" > ?)){origin} " +
                  $"ORDER BY \"SyncedAtUtc\", \"SyncId\" LIMIT {size};";
        var args = new List<object?> { from.SyncedAtUtc, from.SyncedAtUtc, from.SyncedAtUtc, from.Key };
        if (skipOwnRows) args.Add(deviceId);
        return (sql, args.ToArray());
    }

    private async Task SavePullCursorsAsync(IReadOnlyDictionary<string, string> cursors, CancellationToken ct)
    {
        if (cursors.Count == 0) return;
        using var db = _contextFactory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var (key, value) in cursors) await DbMeta.SetAsync(db, key, value, ct);
        await tx.CommitAsync(ct);
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

    /// <param name="cursor">El cursor de bajada que se guarda junto con la página (en la misma transacción).</param>
    /// <param name="serverRowAfterReject">Desempate: la fila de la nube se aplica también con la misma marca
    /// (<see cref="SyncRules.ServerRowAppliesAfterReject"/>).</param>
    private async Task<int> ApplyPageAsync(TableMap map, List<Dictionary<string, object?>> rows, Dictionary<Type, Dictionary<Guid, int>> syncIdMaps,
        ISet<(Type, Guid)>? deadPrincipals, CancellationToken ct, IReadOnlyDictionary<string, string>? cursor = null, bool serverRowAfterReject = false)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(ApplyPageGenericAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(map.ClrType);
        return await (Task<int>)method.Invoke(this, new object?[] { map, rows, syncIdMaps, deadPrincipals, cursor, serverRowAfterReject, ct })!;
    }

    /// <summary>
    /// Aplica una página de filas remotas. Una fila cuyo principal (obligatorio u opcional) todavía no existe aquí se
    /// aparta en la misma transacción y se reintenta al final de cada descarga (<see cref="RetryParkedPullAsync"/>).
    /// <paramref name="deadPrincipals"/> son los principales que se sabe que se borraron.
    /// </summary>
    private async Task<int> ApplyPageGenericAsync<T>(TableMap map, List<Dictionary<string, object?>> rows, Dictionary<Type, Dictionary<Guid, int>> syncIdMaps,
        ISet<(Type, Guid)>? deadPrincipals, IReadOnlyDictionary<string, string>? cursor, bool serverRowAfterReject, CancellationToken ct)
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

            // Last-Write-Wins: la versión local igual o más nueva se conserva (en el desempate, solo la más nueva), y lo
            // apartado de esta fila ya no hace falta
            if (serverRowAfterReject ? !SyncRules.ServerRowAppliesAfterReject(local?.UpdatedAtUtc, remoteUpdated) : !SyncRules.RemoteRowWins(local?.UpdatedAtUtc, remoteUpdated))
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
        await SyncChangeLog.SaveRemoteChangesAsync(db, ct, cursor);
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
        // Sin lápida aquí ni anotación para subir, tampoco de lo que se borre en cascada: el borrado ya viene de la nube
        await SyncChangeLog.SaveRemoteChangesAsync(db, ct);
        return true;
    }

    // =========================================================================
    // PERFIL DEL CONSULTORIO
    // =========================================================================

    /// <param name="remote">La fila del perfil en la nube, ya leída con la bajada (null si no existe).</param>
    private async Task SyncClinicProfileAsync(TursoClient client, Dictionary<string, object?>? remote, CancellationToken ct)
    {
        var localProfile = _clinicProfileService.CurrentProfile;
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
