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

// El SQL dinámico de este archivo solo usa nombres de tablas y columnas del modelo de EF;
// los datos van siempre como parámetros.
#pragma warning disable EF1002, EF1003

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
public partial class TursoSyncService
{
    public const int RemoteSchemaVersion = 2;

    /// <summary>Hora del servidor (UTC) en el formato de las marcas de sincronización.</summary>
    public const string ServerNowSql = "strftime('%Y-%m-%d %H:%M:%f','now')";
    private const string TombstonesTable = SyncSchema.TombstonesTable;
    private const string MetaTable = SyncSchema.MetaTable;
    private const int PushChunkSize = 80;

    /// <summary>Lo menos a lo que se reduce una página que no se pudo leer por tamaño o tiempo.</summary>
    private const int MinPullPageSize = 10;

    internal const string ClinicProfileSql = "SELECT * FROM ClinicProfiles WHERE Id = 1;";

    /// <summary>
    /// La versión del protocolo de sincronización que entiende este programa (la nube puede exigir una mínima). 3 (paso
    /// 8.9) = el 2 con la cerca de generación en cada escritura.
    /// </summary>
    public const int ClientProtocol = GenerationProtocol;

    /// <summary>El protocolo con la cerca de generación (paso 8.9). Reemplazar la nube lo exige a todos los equipos.</summary>
    public const int GenerationProtocol = 3;

    /// <summary>
    /// Lo mínimo que exige una nube verificada en la vida normal: los equipos con el protocolo 2 siguen sincronizando hasta
    /// que alguien reemplaza la nube con un respaldo (entonces sube a <see cref="GenerationProtocol"/>).
    /// </summary>
    public const int MinCompatibleProtocol = 2;

    /// <summary>La nube exige un protocolo más nuevo (_tpvc_meta.minClientProtocol): hay que actualizar TPVC.</summary>
    public const string ClientOutdatedCode = "CLIENT_OUTDATED";

    /// <summary>La nube pertenece a otra base (_tpvc_meta.clinicId distinto del de DbMeta): no se sincroniza.</summary>
    public const string CloudIdentityCode = "CLOUD_IDENTITY";

    /// <summary>
    /// Al inicio de las instrucciones que recorren una tabla completa a propósito (el reporte de la sincronización completa,
    /// la revisión de "Esta PC ya usaba esta nube" y la reparación única de marcas en la nube).
    /// </summary>
    public const string FullScanMarker = "/* recorrido completo a propósito */ ";

    /// <summary>Una marca más adelantada que esto respecto a la hora del servidor se vuelve a marcar con la hora del servidor.</summary>
    private static readonly TimeSpan FutureStampTolerance = TimeSpan.FromMinutes(2);

    /// <summary>Con un desfase mayor que esto se avisa en pantalla.</summary>
    private static readonly TimeSpan ClockWarningThreshold = TimeSpan.FromMinutes(2);

    /// <summary>Claves de _tpvc_meta que regresa la primera instrucción de cada sincronización (paso 8.7).</summary>
    private static readonly string[] HandshakeKeys =
        { "clinicId", "generation", "generationReady", "generationPrevious", "protocol", "minClientProtocol", "fullUploadAtUtc", "stampsRepairedAtUtc" };

    /// <summary>La hora del servidor, la marca de agua de la bajada (5 s antes) y las claves de _tpvc_meta, en una instrucción.</summary>
    internal static readonly string HandshakeSql =
        "SELECT strftime('%Y-%m-%d %H:%M:%f','now') AS now, strftime('%Y-%m-%d %H:%M:%f','now','-5 seconds') AS wm" +
        string.Concat(HandshakeKeys.Select(k => $", (SELECT \"Value\" FROM {SyncSchema.Q(SyncSchema.MetaTable)} WHERE \"Key\" = '{k}') AS \"{k}\"")) + ";";

    /// <summary>
    /// Filas por página al bajar (paso 8.6): 100 en las tablas con columnas grandes (las hojas del paciente y las recetas
    /// van en JSON), 500 en las demás. Turso no puso tope hasta 64 MB por respuesta (V12); el límite es por memoria y
    /// tiempo, sobre todo en Android.
    /// </summary>
    internal static int DefaultPullPageSize(string table) => table is "Patients" or "PatientConsultations" ? 100 : 500;

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

    // Paso 8.22: expedientes que una combinación dejó distintos de la nube en esta sincronización (suben al final de ella)
    private int _mergedForUpload;

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
            if (_settingsService.IsCurrentTokenRejected) return TursoSyncPauseReason.TokenRejected;
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
        TursoSyncPauseReason.TokenRejected => $"{TokenRejectedTitle}. {TokenRejectedDetail}",
        TursoSyncPauseReason.QuotaExhausted => $"{TursoException.QuotaExhaustedMessage} Siguiente intento: {RetryAtLocalText}.",
        _ => string.Empty
    };

    /// <summary>La pausa en una línea, para el estado (barra de estado y resumen de Configuración).</summary>
    private string PauseTitle => PauseReason switch
    {
        TursoSyncPauseReason.ConsumptionBrake => TursoConsumptionBrake.PausedTitle,
        TursoSyncPauseReason.TokenRejected => TokenRejectedTitle,
        TursoSyncPauseReason.QuotaExhausted => $"{TursoException.QuotaExhaustedTitle} · siguiente intento a las {RetryAtLocalText}",
        _ => string.Empty
    };

    public const string TokenRejectedTitle = "Sincronización detenida: Turso rechazó el token";

    /// <summary>Paso 8.15: el aviso de Nube y dispositivos cuando Turso rechazó el token de esta computadora.</summary>
    public string TokenRejectedDetail =>
        "Turso rechazó el token de esta computadora" +
        (_settingsService.CurrentSettings.TokenRejectedUtc is { } at ? $" el {at.ToLocalTime():dd/MM/yyyy HH:mm}" : string.Empty) +
        ": se anularon los tokens, o el que se pegó no es de esta base o está incompleto. Cree uno nuevo en el panel de Turso " +
        "(o con 'turso db tokens create'), péguelo en «Conexión con Turso» y pulse «Sincronizar ahora». No se borra nada: lo que " +
        "esta computadora no ha subido sube con el token nuevo.";

    private string RetryAtLocalText => (QuotaRetryAtUtc ?? Clock.GetUtcNow().UtcDateTime).ToLocalTime().ToString("HH:mm");

    public event Action<TursoSyncStatus, string>? StatusChanged;
    public event Action<TursoSyncResult>? SyncCompleted;

    public TursoSyncService(
        IDbContextFactory<AppDbContext> contextFactory,
        TursoSettingsService settingsService,
        ClinicProfileService clinicProfileService,
        InventoryService? inventoryService = null,
        LabResultService? labResults = null)
    {
        _contextFactory = contextFactory;
        _settingsService = settingsService;
        _clinicProfileService = clinicProfileService;
        _inventoryService = inventoryService;
        AttachLabResults(labResults);

        UpdateInitialStatus();

        if (_inventoryService != null)
        {
            // Paso 8.16: EXP y TKT toman su número del contador de la nube
            _inventoryService.Folios = this;
            _inventoryService.OnInventoryChanged += OnLocalDataChanged;
            _inventoryService.OnPatientsChanged += OnLocalDataChanged;
            _inventoryService.OnSalesChanged += OnLocalDataChanged;
            _inventoryService.OnAppointmentsChanged += OnLocalDataChanged;
        }

        _settingsService.SettingsChanged += UpdateInitialStatus;

        // Paso 8.17: la regularización diaria de folios (la app la enciende con AutoRun)
        FolioRegularization = new Folios.FolioRegularizationService(contextFactory, this);
    }

    /// <summary>Paso 8.17: la PC principal da el número normal a los folios hechos sin conexión, una vez al día.</summary>
    public Folios.FolioRegularizationService FolioRegularization { get; }

    private void UpdateInitialStatus()
    {
        if (Status == TursoSyncStatus.Syncing) return;

        var settings = _settingsService.CurrentSettings;
        // Otra URL: el conflicto de identidad era de la nube anterior
        if (IdentityConflict != null && _identityConflictUrl != settings.DatabaseUrl) IdentityConflict = null;

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

        // Una nube de otra base (paso 8.7): lo automático no vuelve a intentar hasta que se resuelva en Nube y dispositivos
        if (isBackgroundAutoSync && IdentityConflict is { } conflict && _identityConflictUrl == settings.DatabaseUrl)
        {
            return Task.FromResult(new TursoSyncResult { Success = false, ErrorMessage = IdentityConflictMessage(conflict) });
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
        throw new TursoException(PauseMessage, reason switch
        {
            TursoSyncPauseReason.QuotaExhausted => TursoException.QuotaExhaustedCode,
            TursoSyncPauseReason.TokenRejected => "HTTP_401",
            _ => TursoException.ConsumptionBrakeCode
        });
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
            var (pushed, pulled, report) = await Task.Run(() => SyncCoreAsync(fullResync, ct), ct);
            sw.Stop();

            var parkedRows = RefreshParkedCount();
            var result = new TursoSyncResult
            {
                Success = true,
                RowsPushed = pushed,
                RowsPulled = pulled,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                ParkedRows = parkedRows,
                FullSyncReport = report
            };

            // Turso volvió a aceptar instrucciones: termina la pausa por cuota agotada
            StoredQuotaRetryAtUtc = null;
            if (PauseReason == TursoSyncPauseReason.ConsumptionBrake)
                SetStatus(TursoSyncStatus.Paused, TursoConsumptionBrake.PausedTitle);
            else
                SetStatus(TursoSyncStatus.Success, $"✓ Sincronizado ({pushed} subidos, {pulled} recibidos • {sw.ElapsedMilliseconds} ms)" +
                    (parkedRows > 0 ? $" · {ParkedText(parkedRows)}" : string.Empty) +
                    (report is { IsClean: false } ? " · con diferencias (vea el reporte)" : string.Empty) +
                    (ClockWarningText is { } clock ? $" · {clock}" : string.Empty));
            SyncCompleted?.Invoke(result);

            if (pulled > 0)
            {
                NotifyLocalViews();
                _labResults?.NotifyChanged();
            }

            // Paso 8.12b: los archivos de laboratorio, fuera del candado de la sincronización
            ScheduleLabTransfers();

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
            else if (code is "HTTP_401")
            {
                // Paso 8.15: un token anulado (o pegado de otra base, o mal copiado). Lo automático deja de intentar hasta
                // que se pegue otro; no se borra ni se aparta nada
                _settingsService.RecordTokenRejected();
                AppLogger.Warn($"Turso: rechazó el token de esta computadora; la sincronización automática espera uno nuevo. {ex.Message}");
                SetStatus(TursoSyncStatus.Paused, TokenRejectedTitle);
            }
            else if (code is CloudIdentityCode or ClientOutdatedCode or GenerationPendingCode or GenerationChangedCode or RestorePendingCode)
            {
                // Pasos 8.7 y 8.9: no es un error de red ni de datos; el detalle va en el aviso de Nube y dispositivos
                AppLogger.Warn($"Turso: sincronización detenida. {ex.Message}");
                SetStatus(TursoSyncStatus.Paused, code switch
                {
                    CloudIdentityCode => "Sincronización detenida: esta nube es de otra base",
                    ClientOutdatedCode => "Sincronización detenida: actualice TPVC en esta computadora",
                    RestorePendingCode => "Sincronización detenida: se restauró un respaldo (elija qué hacer con la nube)",
                    _ => "Sincronización en espera: otra computadora está reemplazando la nube con un respaldo"
                });
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

    private async Task<(int Pushed, int Pulled, TursoFullSyncReport? Report)> SyncCoreAsync(bool fullResync, CancellationToken ct)
    {
        if (!_changeLogVerified)
        {
            // La base ya lo trae desde que abre el programa (DatabaseInitializer); esto cubre una que llegue sin él
            using var db = _contextFactory.CreateDbContext();
            SyncChangeLog.EnsureInstalled(db, backupBeforeFirstInstall: false);
            _changeLogVerified = true;
        }

        // Paso 8.9: con un respaldo restaurado no se sincroniza hasta elegir qué hacer con la nube; un reemplazo de la
        // nube que se cortó se termina antes que nada
        string? replacing;
        bool restored;
        using (var db = _contextFactory.CreateDbContext())
        {
            replacing = await DbMeta.GetAsync(db, DbMeta.ReplacingCloud, ct);
            restored = await DbMeta.GetAsync(db, DbMeta.RestoredWithCloud, ct) == "1";
        }
        if (replacing != null) await ContinueCloudReplacementAsync(replacing, ct);
        else if (restored) throw new TursoException(RestorePendingMessage, RestorePendingCode);

        try
        {
            return await SyncOnceAsync(fullResync, ct);
        }
        catch (GenerationReloadException reload)
        {
            // Otra PC reemplazó la nube con un respaldo y ya terminó: lo que esta no subió quedó guardado; se vacía y se
            // vuelve a bajar todo, una sola vez
            await ReloadNewGenerationAsync(reload.Handshake, ct);
            return await SyncOnceAsync(fullResync: false, ct);
        }
    }

    private async Task<(int Pushed, int Pulled, TursoFullSyncReport? Report)> SyncOnceAsync(bool fullResync, CancellationToken ct)
    {
        var settings = _settingsService.CurrentSettings;
        _syncGeneration = null;
        _generationChecked = false;
        Interlocked.Exchange(ref _mergedForUpload, 0);

        // Paso 8.10: si este equipo tomó un DeviceId nuevo porque otra instalación usaba el mismo, se baja todo una vez
        await ApplyRedownloadRequestAsync(settings, ct);

        // Una sincronización completa (o la primera, sin ningún cursor de bajada) sube y baja todo: el freno la
        // revisa antes de empezar
        var cursors = fullResync ? PullCursors.FromScratch : await LoadPullCursorsAsync(settings, ct);
        bool fullPull = fullResync || cursors.IsFirstDownload;
        if (fullPull) EnsureFullPullAllowed();

        // Después de «Unirse» o «Reemplazar con la nube» (paso 8.8) se baja todo, también lo que subió este equipo
        bool downloadPending;
        using (var db = _contextFactory.CreateDbContext())
        {
            downloadPending = await DbMeta.GetAsync(db, DbMeta.CloudDownloadPending, ct) == "1";
        }

        using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);

        await EnsureRemoteSchemaAsync(client, ct);

        if (_registeredDeviceUrl != settings.DatabaseUrl)
        {
            await RegisterDeviceAsync(client, settings, ct);
            _registeredDeviceUrl = settings.DatabaseUrl;
        }

        // 0. Antes de escribir nada: la hora del servidor, el protocolo y la identidad de la nube (paso 8.7). Si no hay
        //    nada que subir ni columnas nuevas, viene como primera instrucción de la misma petición de la bajada
        var columns = await LoadPulledColumnsAsync(ct);
        bool cloudChecked = false;
        int checkReads = 0;
        if (fullResync || columns.NewByTable.Count > 0 || await HasPendingPushAsync(ct))
        {
            var (handshake, sent, received) = await ReadHandshakeAsync(client, ct);
            await CheckCloudAsync(client, handshake, sent, received, ct);
            cloudChecked = true;
            checkReads = HandshakeKeys.Length;

            // Columnas que esta base no conocía la última vez que bajó la tabla: se baja completa antes de subir
            if (columns.NewByTable.Count > 0)
            {
                checkReads += await DownloadTablesWithNewColumnsAsync(client, settings.DeviceId, columns.NewByTable, handshake.Watermark, ct);
            }
        }

        // 1. PUSH: lo anotado en el registro de cambios desde la última subida (paso 8.5)
        var (pushed, pushReads) = await PushLocalChangesAsync(client, settings.DeviceId, fullResync, ct);

        // 2. PULL y perfil del consultorio: lo que otros equipos escribieron desde el cursor de cada tabla (la marca
        //    de agua se toma ANTES de leer, con 5 s de traslape; los duplicados se descartan por LWW)
        if (fullPull) _settingsService.RecordFullPull(); // se cuenta al empezar a bajar, cuando la red ya respondió
        var (pulled, rowsRead, watermark) = await PullRemoteChangesAsync(client, settings.DeviceId, cursors,
            skipOwnRows: !fullResync && !downloadPending, incremental: !fullPull, cloudChecked, ct);

        // 2b. Paso 8.22: lo que una combinación de expedientes dejó distinto de la nube sube en esta misma sincronización (una
        //     sola vez: lo que la nube rechace ahí se combina otra vez y sube en la siguiente)
        if (Interlocked.Exchange(ref _mergedForUpload, 0) > 0)
        {
            var (merged, mergedReads) = await PushLocalChangesAsync(client, settings.DeviceId, fullResync: false, ct);
            pushed += merged;
            pushReads += mergedReads;
        }
        if (downloadPending)
        {
            using var db = _contextFactory.CreateDbContext();
            DbMeta.Remove(db, DbMeta.CloudDownloadPending);
            // Paso 8.19: las existencias de la nube traen su propia historia; la foto del libro se toma con ellas
            await StockLedger.RetakeBaselineAsync(db, ct);
            AppLogger.Info("Turso: terminó la descarga completa de la nube.");
        }

        // Las columnas con que se bajó cada tabla (la primera vez, las de hoy, sin bajar nada)
        await SaveMetaAsync(columns.FirstTime, ct);

        // 3. «Sincronización completa»: reporte y, si sale limpio, la identidad de la nube (A6)
        var report = fullResync ? await BuildFullSyncReportAsync(client, ct) : null;

        // 4. Paso 8.10: una vez por sesión con la nube verificada, esta PC en DeviceRegistry y la PC principal
        var registryWrites = await EnsureDeviceRegistryAsync(client, settings, ct);

        // 5. Paso 8.15: el primer token que funciona después de anularlos se anota en la nube
        registryWrites += await RecordTokenRotationAsync(client, ct);

        // 6. Paso 8.16: los contadores de folios, hasta el mayor número de esta base (una vez por sesión y en cada
        //    «Sincronización completa»), y los folios repetidos para el aviso
        registryWrites += await SeedFolioCountersAsync(client, force: fullResync, ct);
        await RefreshDuplicateFoliosAsync(ct);

        settings.LastPulledServerWatermark = watermark;
        settings.LastSyncUtc = DateTime.UtcNow;
        _settingsService.RecordUsage(rowsRead + pushReads + checkReads + (report?.RowsRead ?? 0) + 3 + Interlocked.Exchange(ref _externalRowsRead, 0),
            pushed + 1 + registryWrites + Interlocked.Exchange(ref _externalRowsWritten, 0));
        _settingsService.SaveSettings(settings);
        CheckDailyConsumption();

        return (pushed, pulled, report);
    }

    /// <summary>¿Hay algo que subir? (sin cursor todavía, lo anotado después de él o filas apartadas al subir)</summary>
    private async Task<bool> HasPendingPushAsync(CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        var cursor = await SyncChangeLog.ReadPushCursorAsync(db, ct);
        return cursor == null
               || await SyncChangeLog.HasEntriesAfterAsync(db, cursor.Value, ct)
               || await db.SyncParkedRows.AnyAsync(p => p.Direction == SyncParkedRow.Push, ct);
    }

    // =========================================================================
    // COLUMNAS NUEVAS (paso 8.7)
    // =========================================================================

    /// <param name="NewByTable">Tablas con columnas que el modelo conoce y la última bajada no tenía.</param>
    /// <param name="FirstTime">Tablas sin columnas anotadas (al actualizar a esta versión): se anotan las de hoy.</param>
    private sealed record PulledColumns(Dictionary<string, HashSet<string>> NewByTable, Dictionary<string, string> FirstTime);

    private static List<string> ColumnsOf(TableMap map) => map.RemoteColumnNames.OrderBy(c => c, StringComparer.Ordinal).ToList();

    private async Task<PulledColumns> LoadPulledColumnsAsync(CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        var stored = await DbMeta.GetByPrefixAsync(db, DbMeta.PullColumnsPrefix, ct);
        var newByTable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var firstTime = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var map in GetTableMaps())
        {
            var key = DbMeta.PullColumnsPrefix + map.Table;
            var current = ColumnsOf(map);
            if (!stored.TryGetValue(key, out var known))
            {
                firstTime[key] = string.Join(",", current);
                continue;
            }
            var knownSet = known.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var added = current.Where(c => !knownSet.Contains(c)).ToHashSet(StringComparer.Ordinal);
            if (added.Count > 0) newByTable[map.Table] = added;
        }
        return new PulledColumns(newByTable, firstTime);
    }

    /// <summary>
    /// Una versión nueva que conoce columnas que esta base no tenía al bajar la tabla (EnsureSchema las agregó con su
    /// valor por omisión): la tabla se baja completa antes de subir. La fila con la misma marca es la misma versión y se
    /// aplica completa; la que aquí es más nueva recibe solo esas columnas. Así lo que sube no pisa en la nube el valor
    /// que escribió un equipo más nuevo.
    /// </summary>
    private async Task<int> DownloadTablesWithNewColumnsAsync(TursoClient client, string deviceId,
        Dictionary<string, HashSet<string>> newByTable, string watermark, CancellationToken ct)
    {
        int rowsRead = 0;
        var upTo = new PullCursor(watermark, string.Empty);
        var pageSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        var idleCursors = new Dictionary<string, string>(StringComparer.Ordinal);
        var syncIdMaps = new Dictionary<Type, Dictionary<Guid, int>>();
        foreach (var map in GetTableMaps().Where(m => newByTable.ContainsKey(m.Table)))
        {
            var columns = newByTable[map.Table];
            AppLogger.Info($"Turso: {map.Table} tiene columnas nuevas ({string.Join(", ", columns)}): se baja completa antes de subir.");
            var (_, read) = await PullTableAsync(client, map.Table, PullCursor.Start, null, upTo, deviceId, skipOwnRows: false, pageSizes, idleCursors,
                (page, cursor) => ApplyPageAsync(map, page, syncIdMaps, deadPrincipals: null, ct, cursor, newColumns: columns), ct);
            rowsRead += read;
            syncIdMaps.Remove(map.ClrType);
            await SaveMetaAsync(new Dictionary<string, string> { [DbMeta.PullColumnsPrefix + map.Table] = string.Join(",", ColumnsOf(map)) }, ct);
        }
        await SaveMetaAsync(idleCursors, ct);
        return rowsRead;
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
    // HORA E IDENTIDAD DE LA NUBE (paso 8.7)
    // =========================================================================

    /// <summary>Lo que regresa la primera instrucción de cada sincronización.</summary>
    internal sealed record CloudHandshake(DateTime ServerNowUtc, string Watermark, IReadOnlyDictionary<string, string?> Meta)
    {
        public string? ClinicId => Meta.GetValueOrDefault("clinicId");
        public string? Generation => Meta.GetValueOrDefault("generation");
        public string? GenerationReady => Meta.GetValueOrDefault("generationReady");

        /// <summary>La generación anterior a un reemplazo de la nube (paso 8.9); no existe si nunca se reemplazó.</summary>
        public string? GenerationPrevious => Meta.GetValueOrDefault("generationPrevious");
        public int? MinClientProtocol => int.TryParse(Meta.GetValueOrDefault("minClientProtocol"), out var p) ? p : null;
        public bool StampsRepaired => !string.IsNullOrEmpty(Meta.GetValueOrDefault("stampsRepairedAtUtc"));
    }

    /// <summary>La nube ya tiene su identidad (clinicId) y esta base no es de ella.</summary>
    public sealed record CloudIdentityConflict(string CloudClinicId, string? LocalClinicId);

    /// <summary>Desfase de este equipo con la nube medido en la última sincronización (hora del servidor − hora de aquí).</summary>
    public TimeSpan? ClockOffset { get; private set; }

    /// <summary>"La hora de esta computadora difiere N min de la nube" si el desfase pasa de 2 min; null si no.</summary>
    public string? ClockWarningText => ClockOffset is { } offset && offset.Duration() > ClockWarningThreshold
        ? $"La hora de esta computadora difiere {Math.Round(offset.Duration().TotalMinutes):0} min de la nube"
        : null;

    /// <summary>La sincronización está detenida porque la nube pertenece a otra base (se resuelve en Nube y dispositivos).</summary>
    public CloudIdentityConflict? IdentityConflict { get; private set; }
    private string? _identityConflictUrl;

    private static CloudHandshake ParseHandshake(Dictionary<string, object?>? row)
    {
        if (row == null) throw new TursoException("Turso no regresó su hora.");
        var meta = HandshakeKeys.ToDictionary(k => k, k => row.GetValueOrDefault(k)?.ToString(), StringComparer.Ordinal);
        return new CloudHandshake(ParseDate(row.GetValueOrDefault("now")), row.GetValueOrDefault("wm")?.ToString() ?? string.Empty, meta);
    }

    private async Task<(CloudHandshake Handshake, DateTime SentUtc, DateTime ReceivedUtc)> ReadHandshakeAsync(TursoClient client, CancellationToken ct)
    {
        var sent = DateTime.UtcNow;
        var rows = await client.QueryAsync(HandshakeSql, null, ct);
        return (ParseHandshake(rows.FirstOrDefault()), sent, DateTime.UtcNow);
    }

    /// <summary>
    /// Antes de escribir o aplicar nada: el protocolo que exige la nube, su identidad, el desfase del reloj (se guarda en
    /// DbMeta: lo usan las marcas y los triggers) y las marcas en el futuro, aquí siempre y en la nube una sola vez.
    /// </summary>
    private async Task CheckCloudAsync(TursoClient client, CloudHandshake handshake, DateTime sentUtc, DateTime receivedUtc, CancellationToken ct)
    {
        if (handshake.MinClientProtocol is { } minimum && minimum > ClientProtocol)
        {
            throw new TursoException(
                $"Actualice TPVC en esta computadora: la nube ya usa una versión más nueva de la sincronización (protocolo {minimum}). " +
                "Lo que se capture aquí se guarda y sube al actualizar.", ClientOutdatedCode);
        }

        // Desfase: la hora del servidor contra la de aquí a la mitad del viaje. Se mide aunque la nube resulte ajena: el
        // aviso de la hora sirve igual y guardarlo no escribe nada en la nube
        using var db = _contextFactory.CreateDbContext();
        var offset = handshake.ServerNowUtc - (sentUtc + (receivedUtc - sentUtc) / 2);
        offset = TimeSpan.FromMilliseconds(Math.Round(offset.TotalMilliseconds));
        if (ClockOffset is not { } previous || (previous - offset).Duration() > TimeSpan.FromMilliseconds(250))
        {
            DbMeta.Set(db, DbMeta.ClockOffsetMs, ((long)offset.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            SyncClock.Default.Remember(db, offset);
        }
        ClockOffset = offset;
        if (ClockWarningText is { } warning) AppLogger.Warn($"Turso: {warning} ({offset.TotalSeconds:0} s).");

        var localClinicId = DbMeta.Get(db, DbMeta.ClinicId);
        if (handshake.ClinicId is { } cloudClinicId && !string.Equals(localClinicId, cloudClinicId, StringComparison.OrdinalIgnoreCase))
        {
            IdentityConflict = new CloudIdentityConflict(cloudClinicId, localClinicId);
            _identityConflictUrl = _settingsService.CurrentSettings.DatabaseUrl;
            throw new TursoException(IdentityConflictMessage(IdentityConflict), CloudIdentityCode);
        }
        IdentityConflict = null;

        // Paso 8.9: la generación de la nube (si otra PC la reemplazó con un respaldo, aquí no se escribe nada)
        await CheckGenerationAsync(db, handshake, ct);

        await RepairLocalFutureStampsAsync(db, handshake.ServerNowUtc, ct);
        if (!handshake.StampsRepaired) await RepairCloudFutureStampsAsync(client, handshake.ServerNowUtc, ct);
    }

    /// <summary>
    /// Reporte de la «Sincronización completa» (A6): filas apartadas, COUNT(*) aquí y en la nube por tabla (recorre cada
    /// tabla una vez, ~20k lecturas) y las tablas legacy_v1_*. Si sale limpio, escribe la identidad de la nube.
    /// </summary>
    private async Task<TursoFullSyncReport> BuildFullSyncReportAsync(TursoClient client, CancellationToken ct)
    {
        var tables = GetTableMaps();
        var statements = tables
            .Select(m => ($"{FullScanMarker}SELECT COUNT(*) AS n FROM {Q(m.Table)};", (object?[]?)null))
            .Append(("SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'legacy_v1_%' ORDER BY name;", null))
            .ToList();
        var results = await client.QueryBatchAsync(statements, ct);

        using var db = _contextFactory.CreateDbContext();
        var counts = new List<TursoTableCount>();
        for (int i = 0; i < tables.Count; i++)
        {
            var local = (await db.Database.SqlQueryRaw<long>($"SELECT COUNT(*) AS Value FROM {Q(tables[i].Table)}").ToListAsync(ct)).Single();
            var remote = Convert.ToInt64(results[i].FirstOrDefault()?["n"] ?? 0L, CultureInfo.InvariantCulture);
            counts.Add(new TursoTableCount(tables[i].Table, local, remote));
        }

        var report = new TursoFullSyncReport
        {
            ParkedRows = RefreshParkedCount(),
            Tables = counts,
            LegacyTables = results[^1].Select(r => r["name"]?.ToString() ?? string.Empty).ToList(),
            RowsRead = counts.Sum(c => c.Remote) + 1
        };
        if (report.IsClean) report.ClinicId = await WriteCloudIdentityAsync(client, db, ct);
        AppLogger.Info($"Turso: reporte de la sincronización completa. {report.Summary.Replace('\n', ' ')}");
        return report;
    }

    /// <summary>
    /// Después de un reporte limpio, en una transacción: clinicId (una sola vez), protocol y minClientProtocol (2, sin
    /// bajarlos si ya son mayores), manifest (el mismo contracts/sync/schema.json), generation y generationReady (si
    /// faltan) y fullUploadAtUtc. El clinicId queda también en DbMeta.
    /// </summary>
    private async Task<string?> WriteCloudIdentityAsync(TursoClient client, AppDbContext db, CancellationToken ct)
    {
        var meta = Q(MetaTable);
        string Once(string key) => $"INSERT INTO {meta} (\"Key\", \"Value\") VALUES ('{key}', ?) ON CONFLICT(\"Key\") DO NOTHING;";
        string Always(string key) => $"INSERT INTO {meta} (\"Key\", \"Value\") VALUES ('{key}', ?) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";";
        string AtLeast(string key) => $"INSERT INTO {meta} (\"Key\", \"Value\") VALUES ('{key}', ?) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\" " +
                                      $"WHERE CAST({meta}.\"Value\" AS INTEGER) < CAST(excluded.\"Value\" AS INTEGER);";
        var protocol = ClientProtocol.ToString(CultureInfo.InvariantCulture);

        var results = await client.TransactionAsync(new (string, object?[]?)[]
        {
            (Once("clinicId"), new object?[] { Guid.NewGuid().ToString() }),
            (AtLeast("protocol"), new object?[] { protocol }),
            // Los equipos con el protocolo 2 siguen sincronizando; reemplazar la nube sube el mínimo (paso 8.9)
            (AtLeast("minClientProtocol"), new object?[] { MinCompatibleProtocol.ToString(CultureInfo.InvariantCulture) }),
            (Always("manifest"), new object?[] { SyncContract.BuildSchemaJson(db.Model) }),
            (Once("generation"), new object?[] { Guid.NewGuid().ToString() }),
            ($"INSERT INTO {meta} (\"Key\", \"Value\") SELECT 'generationReady', \"Value\" FROM {meta} WHERE \"Key\" = 'generation' ON CONFLICT(\"Key\") DO NOTHING;", null),
            ($"INSERT INTO {meta} (\"Key\", \"Value\") VALUES ('fullUploadAtUtc', {ServerNowSql}) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";", null),
            ($"SELECT \"Value\" FROM {meta} WHERE \"Key\" = 'clinicId';", null),
            (SyncSchema.GenerationSql, null)
        }, ct);

        var clinicId = results[^2].Rows.FirstOrDefault()?["Value"]?.ToString();
        if (!string.IsNullOrEmpty(clinicId)) DbMeta.Set(db, DbMeta.ClinicId, clinicId);
        // La generación que se acaba de escribir (o la que ya tenía la nube) es la de esta base
        if (results[^1].Rows.FirstOrDefault()?["generation"]?.ToString() is { Length: > 0 } generation) DbMeta.Set(db, DbMeta.Generation, generation);
        AppLogger.Info($"Turso: nube verificada; identidad {clinicId}.");
        return clinicId;
    }

    /// <summary>
    /// «Esta PC ya usaba esta nube»: si la nube ya tiene identidad y esta base no, se une a ella solo si todo lo que esta
    /// PC ya subió está en la nube (por SyncId; lo que todavía no sube no cuenta). Recorre los SyncId de cada tabla una
    /// vez. Si falta algo, no es la misma nube y no se toca nada.
    /// </summary>
    public async Task<(bool Ok, string Message)> AdoptCloudIdentityAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5), ct)) return (false, "Ya hay una sincronización en curso. Intente en un momento.");
        try
        {
            using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
            await EnsureRemoteSchemaAsync(client, ct);
            var (handshake, _, _) = await ReadHandshakeAsync(client, ct);
            if (handshake.ClinicId is not { } cloudClinicId)
            {
                IdentityConflict = null;
                return (true, "La nube todavía no tiene identidad: esta PC puede sincronizar con ella.");
            }

            using var db = _contextFactory.CreateDbContext();
            var localClinicId = DbMeta.Get(db, DbMeta.ClinicId);
            if (localClinicId != null && !string.Equals(localClinicId, cloudClinicId, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Esta base ya pertenece a otra nube: no puede unirse a esta.");
            }

            var tables = GetTableMaps();
            var remote = await client.QueryBatchAsync(tables.Select(m => ($"{FullScanMarker}SELECT \"SyncId\" FROM {Q(m.Table)};", (object?[]?)null)), ct);
            var cursor = await SyncChangeLog.ReadPushCursorAsync(db, ct);
            var missing = new List<string>();
            for (int i = 0; i < tables.Count; i++)
            {
                var inCloud = remote[i].Select(r => r["SyncId"]?.ToString() ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var pending = cursor is { } seq
                    ? await SyncChangeLog.PendingSyncIdsAsync(db, tables[i].ClrType.Name, seq, ct)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var local = await db.Database.SqlQueryRaw<string>($"SELECT \"SyncId\" AS Value FROM {Q(tables[i].Table)}").ToListAsync(ct);
                int notInCloud = local.Count(id => !pending.Contains(id) && !inCloud.Contains(id));
                if (notInCloud > 0) missing.Add($"{tables[i].Table}: {notInCloud}");
            }
            _settingsService.RecordUsage(remote.Sum(r => r.Count) + 1, 0);

            if (missing.Count > 0)
            {
                return (false, "Esta PC tiene registros que nunca llegaron a esta nube, así que no parece la misma " +
                               $"({string.Join(", ", missing)}). No se cambió nada.");
            }

            DbMeta.Set(db, DbMeta.ClinicId, cloudClinicId);
            if (handshake.Generation is { } generation && generation == handshake.GenerationReady) DbMeta.Set(db, DbMeta.Generation, generation);
            IdentityConflict = null;
            AppLogger.Info($"Turso: esta PC se unió a la nube {cloudClinicId} («Esta PC ya usaba esta nube»).");
            return (true, "✓ Esta PC ya usaba esta nube: la sincronización sigue normal.");
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

    private static string IdentityConflictMessage(CloudIdentityConflict conflict) => conflict.LocalClinicId == null
        ? "Esta nube ya tiene datos de otra computadora y esta base todavía no está unida a ella: no se sincroniza para no " +
          "mezclar datos. En Nube y dispositivos: «Unirse a esta nube», «Esta PC ya usaba esta nube» o «Reemplazar con la nube»."
        : "Esta nube pertenece a otra base de datos (otro consultorio o una nube reemplazada): no se sincroniza para no " +
          "mezclar datos. Revise la URL en Nube y dispositivos, o use «Reemplazar con la nube».";

    /// <summary>
    /// «Unirse a esta nube» (paso 8.8; en una PC nueva o descompuesta es «Recuperar desde la nube»): solo con una base
    /// intacta, sin más datos que el catálogo que siembra una base nueva. Respalda, borra ese catálogo sin lápidas, une
    /// la base a la nube para siempre (<see cref="DbMeta.CloudJoined"/>: nunca vuelve a sembrar) y deja lista la descarga
    /// completa, que hace la sincronización siguiente sin omitir lo que haya subido este equipo.
    /// </summary>
    public Task<(bool Ok, string Message)> JoinCloudAsync(CancellationToken ct = default) => PrepareCloudDownloadAsync(replace: false, ct);

    /// <summary>
    /// «Reemplazar con la nube» (paso 8.8): como <see cref="JoinCloudAsync"/>, pero con cualquier base. Respalda y borra
    /// todo lo local que sincroniza, también lo que no había subido; la interfaz pide teclear CONFIRMAR antes.
    /// </summary>
    public Task<(bool Ok, string Message)> ReplaceWithCloudAsync(CancellationToken ct = default) => PrepareCloudDownloadAsync(replace: true, ct);

    private async Task<(bool Ok, string Message)> PrepareCloudDownloadAsync(bool replace, CancellationToken ct)
    {
        var settings = _settingsService.CurrentSettings;
        if (string.IsNullOrWhiteSpace(settings.DatabaseUrl)) return (false, "URL de Turso no especificada.");
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5), ct)) return (false, "Ya hay una sincronización en curso. Intente en un momento.");
        try
        {
            // 1. La identidad de la nube: sin ella no hay a qué unirse
            using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
            await EnsureRemoteSchemaAsync(client, ct);
            var (handshake, _, _) = await ReadHandshakeAsync(client, ct);
            _settingsService.RecordUsage(HandshakeKeys.Length, 0);
            if (handshake.MinClientProtocol is { } minimum && minimum > ClientProtocol)
            {
                return (false, "Actualice TPVC en esta computadora: la nube ya usa una versión más nueva de la sincronización. No se cambió nada.");
            }
            if (handshake.ClinicId is not { } cloudClinicId)
            {
                return (false, "Esta nube todavía no tiene identidad. Primero haga la «Sincronización completa» en la computadora " +
                               "que ya la usa; si la nube está vacía, esta PC puede sincronizar con ella sin unirse. No se cambió nada.");
            }
            if (handshake.Generation != handshake.GenerationReady)
            {
                // Paso 8.9: nadie baja una nube a medio reemplazar
                return (false, "Otra computadora está reemplazando la nube con un respaldo. Intente cuando termine. No se cambió nada.");
            }

            // 2. Unirse exige una base intacta
            using var db = _contextFactory.CreateDbContext();
            if (!replace && await DescribeLocalDataAsync(db, ct) is { Count: > 0 } found)
            {
                return (false, $"Esta base ya tiene datos ({string.Join(", ", found)}), así que unirse los perdería. Si ya sincronizaba " +
                               "con esta nube, use «Esta PC ya usaba esta nube»; si no, «Reemplazar con la nube» (se respalda antes). " +
                               "No se cambió nada.");
            }

            // 3. Respaldo antes de borrar
            string backup;
            try
            {
                backup = new BackupService(db.Database.GetDbConnection().DataSource)
                    .CreateNamedBackup(replace ? "antes-reemplazar-con-la-nube" : "antes-unirse-a-la-nube");
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo respaldar la base antes de bajarla de la nube", ex);
                return (false, $"No se pudo respaldar la base, así que no se borró nada: {ex.Message}");
            }

            // 4–7. Borra sin lápidas, une la base y deja la bajada desde cero
            await ClearForCloudDownloadAsync(db, cloudClinicId, handshake.Generation, replace, ct);
            settings.LastPulledServerWatermark = null;
            _settingsService.SaveSettings(settings);
            IdentityConflict = null;
            NotifyLocalViews();

            AppLogger.Info(replace
                ? $"Turso: la base local se vació para reemplazarla con la nube {cloudClinicId}. Respaldo: {backup}"
                : $"Turso: esta PC se unió a la nube {cloudClinicId}. Respaldo: {backup}");
            return (true, (replace ? "✓ La base local se vació (con respaldo antes)" : "✓ Esta PC se unió a la nube") +
                          ": ahora se baja todo lo de la nube. Si se corta, la siguiente sincronización sigue donde iba.");
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
    /// Lo que impide unirse a una nube: cualquier fila de las tablas que sincronizan, salvo las categorías y los servicios
    /// que siembra una base nueva (por nombre y código: también los de antes del paso 8.8, con SyncId aleatorios).
    /// </summary>
    internal async Task<List<string>> DescribeLocalDataAsync(AppDbContext db, CancellationToken ct)
    {
        var seededNames = DatabaseInitializer.SeededCategories.Select(c => c.Name).ToList();
        var seededCodes = DatabaseInitializer.SeededServiceCodes.ToList();
        var found = new List<string>();
        foreach (var map in GetTableMaps())
        {
            int count = map.ClrType == typeof(Category)
                ? await db.Categories.CountAsync(c => !seededNames.Contains(c.Name), ct)
                : map.ClrType == typeof(ClinicalService)
                    ? await db.ClinicalServices.CountAsync(s => !seededCodes.Contains(s.Code), ct)
                    : (await db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {Q(map.Table)}").ToListAsync(ct)).First();
            if (count > 0) found.Add($"{LocalDataLabel(map.ClrType)}: {count:N0}");
        }
        return found;
    }

    private static string LocalDataLabel(Type type) => type.Name switch
    {
        nameof(Category) => "categorías propias",
        nameof(ClinicalService) => "servicios propios",
        nameof(Product) => "productos",
        nameof(ProductBatch) => "lotes",
        nameof(InventoryMovement) => "movimientos de inventario",
        nameof(ServiceDefaultProduct) => "insumos de servicios",
        nameof(PatientRecord) => "pacientes",
        nameof(PatientConsultation) => "consultas",
        nameof(PatientAppointment) => "citas",
        nameof(Sale) => "ventas",
        nameof(SaleItem) => "renglones de venta",
        nameof(PrescriptionTemplate) => "plantillas de receta",
        nameof(PediatricDoseRule) => "reglas de dosis",
        nameof(LabResult) => "resultados de laboratorio",
        _ => type.Name
    };

    /// <summary>
    /// Vacía lo que sincroniza (hijos primero) con los triggers apagados: sin lápidas ni anotaciones para subir. En una
    /// transacción: une la base (CloudJoined y clinicId), lleva el cursor de subida al Seq actual (lo que llegue de la nube
    /// nunca vuelve a subir), quita los cursores de bajada y pide la bajada completa sin omitir lo de este equipo.
    /// </summary>
    /// <param name="generation">La generación de la nube que se va a bajar (paso 8.9); queda como la de esta base.</param>
    private async Task ClearForCloudDownloadAsync(AppDbContext db, string? cloudClinicId, string? generation, bool replace, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"INSERT OR IGNORE INTO {Q(SyncChangeLog.ApplyStateTable)} (\"Applying\") VALUES (1);", ct);
        foreach (var map in GetTableMaps().Reverse())
        {
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM {Q(map.Table)};", ct);
        }
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM {Q(TombstonesTable)};", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SyncParkedRows\";", ct);
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM {Q(SyncChangeLog.Table)};", ct);
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM {Q(SyncChangeLog.ApplyStateTable)};", ct);

        // AUTOINCREMENT: el siguiente Seq siempre queda después de este
        var lastSeq = (await db.Database.SqlQueryRaw<long>(
                $"SELECT ifnull((SELECT \"seq\" FROM sqlite_sequence WHERE \"name\" = '{SyncChangeLog.Table}'), 0) AS Value")
            .ToListAsync(ct)).First();
        await SyncChangeLog.SavePushCursorAsync(db, lastSeq, ct);
        await DbMeta.SetAsync(db, DbMeta.CloudJoined, "1", ct);
        if (cloudClinicId != null) await DbMeta.SetAsync(db, DbMeta.ClinicId, cloudClinicId, ct);
        if (generation != null) await DbMeta.SetAsync(db, DbMeta.Generation, generation, ct);
        else DbMeta.Remove(db, DbMeta.Generation);
        await DbMeta.SetAsync(db, DbMeta.CloudDownloadPending, "1", ct);
        // Lo que había aquí ya no cuenta: tampoco un respaldo restaurado pendiente de decidir
        DbMeta.Remove(db, DbMeta.RestoredWithCloud);
        // La foto del libro de existencias era de lo que había aquí: se vuelve a tomar al terminar de bajar (paso 8.19)
        DbMeta.Remove(db, DbMeta.StockLedgerBaseline);
        DbMeta.RemoveByPrefix(db, DbMeta.PullCursorPrefix);
        DbMeta.RemoveByPrefix(db, DbMeta.PullColumnsPrefix);
        if (replace) await DbMeta.SetAsync(db, DbMeta.DbInstanceId, Guid.NewGuid().ToString(), ct);
        await tx.CommitAsync(ct);
        RefreshParkedCount();
    }

    /// <summary>
    /// Toda marca local más de 2 min en el futuro (de un equipo con el reloj adelantado, o de un reloj que cambió sin
    /// conexión) se vuelve a marcar con la hora del servidor; si no, ganaría todos los conflictos. Con los triggers
    /// apagados: lo pendiente ya está anotado y sube con la marca nueva; lo demás no tiene por qué volver a subir.
    /// </summary>
    private async Task RepairLocalFutureStampsAsync(AppDbContext db, DateTime serverNowUtc, CancellationToken ct)
    {
        var now = FormatDate(serverNowUtc);
        var limit = FormatDate(serverNowUtc + FutureStampTolerance);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"INSERT OR IGNORE INTO {Q(SyncChangeLog.ApplyStateTable)} (\"Applying\") VALUES (1);", ct);
        int repaired = 0;
        foreach (var map in GetTableMaps())
        {
            repaired += await db.Database.ExecuteSqlRawAsync(
                $"UPDATE {Q(map.Table)} SET \"UpdatedAtUtc\" = {{0}} WHERE \"UpdatedAtUtc\" > {{1}};", new object[] { now, limit }, ct);
        }
        repaired += await db.Database.ExecuteSqlRawAsync(
            $"UPDATE {Q(TombstonesTable)} SET \"DeletedAtUtc\" = {{0}} WHERE \"DeletedAtUtc\" > {{1}};", new object[] { now, limit }, ct);

        // Paso 8.22: también las marcas por campo de los expedientes (una marca nunca vale más que su fila, pero si se queda
        // en el futuro subiría con cada edición posterior de otro campo)
        var futureStamps = await db.Database.SqlQueryRaw<FutureFieldStamps>(
                "SELECT \"Id\", \"FieldStampsJson\" FROM \"Patients\" WHERE json_valid(\"FieldStampsJson\") " +
                "AND EXISTS (SELECT 1 FROM json_each(\"Patients\".\"FieldStampsJson\") WHERE json_each.value > {0})", limit)
            .ToListAsync(ct);
        foreach (var row in futureStamps)
        {
            var stamps = Rules.PatientFieldStamps.Parse(row.FieldStampsJson);
            foreach (var (key, value) in stamps.Entries.ToList())
            {
                if (string.CompareOrdinal(value, limit) > 0 && Rules.PatientFieldStamps.ParseStamp(value) != null) stamps[key] = now;
            }
            repaired += await db.Database.ExecuteSqlRawAsync("UPDATE \"Patients\" SET \"FieldStampsJson\" = {0} WHERE \"Id\" = {1};",
                new object[] { stamps.ToJson(), row.Id }, ct);
        }
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM {Q(SyncChangeLog.ApplyStateTable)};", ct);
        await tx.CommitAsync(ct);
        if (repaired > 0) AppLogger.Warn($"Turso: {repaired} marcas locales en el futuro se volvieron a marcar con la hora del servidor.");
    }

    /// <summary>Un expediente con marcas por campo en el futuro (para repararlas).</summary>
    private sealed class FutureFieldStamps
    {
        public int Id { get; set; }
        public string FieldStampsJson { get; set; } = string.Empty;
    }

    /// <summary>
    /// Una sola vez por nube (_tpvc_meta.stampsRepairedAtUtc): las marcas de la nube más de 2 min en el futuro, de v1.19 o
    /// de un reloj adelantado, pasan a la hora del servidor. Recorre cada tabla una vez (~20k lecturas).
    /// </summary>
    private async Task RepairCloudFutureStampsAsync(TursoClient client, DateTime serverNowUtc, CancellationToken ct)
    {
        var now = FormatDate(serverNowUtc);
        var limit = FormatDate(serverNowUtc + FutureStampTolerance);
        var statements = GetTableMaps()
            .Select(map => ($"{FullScanMarker}UPDATE {Q(map.Table)} SET \"UpdatedAtUtc\" = ? WHERE \"UpdatedAtUtc\" > ?;", (object?[]?)new object?[] { now, limit }))
            .Append(($"{FullScanMarker}UPDATE {Q(TombstonesTable)} SET \"DeletedAtUtc\" = ? WHERE \"DeletedAtUtc\" > ?;", new object?[] { now, limit }))
            .Append(($"INSERT INTO {Q(MetaTable)} (\"Key\", \"Value\") VALUES ('stampsRepairedAtUtc', {ServerNowSql}) ON CONFLICT(\"Key\") DO NOTHING;", null))
            .ToList();
        var results = await client.TransactionAsync(statements, ct);
        int repaired = results.Take(results.Count - 1).Sum(r => r.AffectedRows);
        if (repaired > 0) AppLogger.Warn($"Turso: {repaired} marcas en el futuro de la nube se volvieron a marcar con la hora del servidor (una sola vez).");
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

        // La sincronización y la revisión de consultas pueden llegar aquí a la vez: un ALTER repetido fallaría
        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_verifiedSchemaUrl == client.DatabaseUrl) return;
            await MigrateRemoteSchemaAsync(client, ct);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    /// <summary>
    /// Tablas que no salen del modelo de EF y ganaron columnas (con nulo): en una nube que ya las tenía llegan con ALTER,
    /// revisado con pragma_table_info.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Type)[]> OtherTablesWithAddedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RegisteredDevices"] = SyncSchema.RegisteredDevicesAddedColumns, // paso 8.10
        ["ClinicProfiles"] = SyncSchema.ClinicProfilesAddedColumns        // paso 8.12
    };

    private async Task MigrateRemoteSchemaAsync(TursoClient client, CancellationToken ct)
    {
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

        // Estructura actual de las tablas sincronizadas y de las que no salen del modelo pero ganaron columnas (el latido en
        // el paso 8.10, el perfil en el 8.12)
        var syncTables = GetTableMaps().Select(m => m.Table).Append(TombstonesTable).ToList();
        var existingTables = (await client.QueryAsync("SELECT name FROM sqlite_master WHERE type = 'table';", null, ct))
            .Select(r => r["name"]?.ToString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tablesToInspect = syncTables.Concat(OtherTablesWithAddedColumns.Keys).Where(existingTables.Contains).ToList();
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
        var otherColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in OtherTablesWithAddedColumns.Keys)
        {
            if (columnsByTable.Remove(table, out var found)) otherColumns[table] = found;
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

        // Las columnas nuevas fuera del modelo, todas con nulo (el upsert de una PC más vieja, con sus columnas explícitas,
        // sigue igual)
        foreach (var (table, cols) in otherColumns)
        {
            foreach (var (name, type) in OtherTablesWithAddedColumns[table].Where(c => !cols.Contains(c.Name)))
            {
                ddl.Add(($"ALTER TABLE {Q(table)} ADD COLUMN {Q(name)} {type};", null));
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
            // Cada lote termina leyendo la generación (paso 8.9): si otra PC reemplazó la nube, la cerca no dejó escribir
            // nada y aquí se detiene sin avanzar el cursor
            var results = await client.TransactionAsync(chunk.Select(s => (s.Sql, (object?[]?)s.Args)).Append((SyncSchema.GenerationSql, null)), ct);
            EnsureSameGeneration(results[^1].Rows);
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

        // Paso 8.10: la subida termina con LastPushUtc y lo que quedó pendiente en el latido (los demás equipos y «Quitar»)
        if (statementCount > 0) await HeartbeatAfterPushAsync(client, ct);

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
                args.Add(_syncGeneration); // cerca de generación (paso 8.9)

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
                new(SyncSchema.TombstoneUpsertSql, new object?[] { t.EntityType, FormatGuid(t.SyncId), FormatDate(t.DeletedAtUtc), deviceId, _syncGeneration }, null, t.SyncId)
            };
            if (tablesByType.TryGetValue(t.EntityType, out var table))
            {
                group.Add(new(SyncSchema.DeleteTombstonedRowSql(table), new object?[] { FormatGuid(t.SyncId), FormatDate(t.DeletedAtUtc), _syncGeneration }, null, t.SyncId));
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
    /// La primera instrucción es la hora del servidor con las claves de la nube (paso 8.7); si no se revisaron antes de
    /// subir (<paramref name="cloudChecked"/>), se revisan aquí antes de aplicar nada.
    /// </summary>
    private async Task<(int Applied, int RowsRead, string Watermark)> PullRemoteChangesAsync(
        TursoClient client, string deviceId, PullCursors cursors, bool skipOwnRows, bool incremental, bool cloudChecked, CancellationToken ct)
    {
        var tables = GetTableMaps();
        var pageSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        int applied = 0, rowsRead = 0;
        CloudHandshake? handshake = null;
        DateTime sent = DateTime.UtcNow, received = sent;
        Dictionary<string, object?>? profileRow = null;
        Dictionary<string, List<Dictionary<string, object?>>>? firstPages = null;

        if (incremental)
        {
            var statements = new List<(string Sql, object?[]? Args)> { (HandshakeSql, null) };
            foreach (var table in tables.Select(m => m.Table).Append(TombstonesTable))
            {
                statements.Add(PageQuery(table, cursors.For(table), deviceId, skipOwnRows, PageSize(pageSizes, table)));
            }
            statements.Add((ClinicProfileSql, null));
            try
            {
                sent = DateTime.UtcNow;
                var results = await client.TransactionAsync(statements, ct);
                received = DateTime.UtcNow;
                handshake = ParseHandshake(results[0].Rows.FirstOrDefault());
                firstPages = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
                int i = 1;
                foreach (var table in tables.Select(m => m.Table).Append(TombstonesTable)) firstPages[table] = results[i++].Rows;
                profileRow = results[^1].Rows.FirstOrDefault();
            }
            catch (TursoException ex) when (IsTooLargeOrSlow(ex))
            {
                // Demasiado para una sola respuesta: como la descarga completa, una tabla por petición
                AppLogger.Warn($"Turso: la bajada en una sola petición no se pudo leer ({ex.Message}); se baja tabla por tabla.");
                handshake = null;
                firstPages = null;
            }
        }

        if (handshake == null)
        {
            sent = DateTime.UtcNow;
            var results = await client.QueryBatchAsync(new (string, object?[]?)[] { (HandshakeSql, null), (ClinicProfileSql, null) }, ct);
            received = DateTime.UtcNow;
            handshake = ParseHandshake(results[0].FirstOrDefault());
            profileRow = results[1].FirstOrDefault();
        }
        var watermark = handshake.Watermark;
        if (string.IsNullOrWhiteSpace(watermark)) throw new TursoException("Turso no regresó su hora para la marca de agua.");
        if (!cloudChecked) await CheckCloudAsync(client, handshake, sent, received, ct);
        else EnsureSameGeneration(handshake.Generation); // otra PC pudo reemplazar la nube entre la subida y la bajada

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
                await SaveMetaAsync(cursor, ct);
                return n;
            }, ct);
        applied += tombstonesApplied;
        rowsRead += tombstonesRead;

        // Las tablas sin nada nuevo avanzan hasta la marca de agua, todas en una escritura
        await SaveMetaAsync(idleCursors, ct);

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
                if (!_generationChecked) return (await client.QueryAsync(sql, args, ct), size);

                // Cada página trae la generación (paso 8.9): si otra PC reemplazó la nube a media bajada, no se mezclan
                var results = await client.QueryBatchAsync(new (string, object?[]?)[] { (sql, args), (SyncSchema.GenerationSql, null) }, ct);
                EnsureSameGeneration(results[1]);
                return (results[0], size);
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

    private async Task SaveMetaAsync(IReadOnlyDictionary<string, string> cursors, CancellationToken ct)
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
        ISet<(Type, Guid)>? deadPrincipals, CancellationToken ct, IReadOnlyDictionary<string, string>? cursor = null, bool serverRowAfterReject = false,
        ISet<string>? newColumns = null, bool force = false)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(ApplyPageGenericAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(map.ClrType);
        return await (Task<int>)method.Invoke(this, new object?[] { map, rows, syncIdMaps, deadPrincipals, cursor, serverRowAfterReject, newColumns, force, ct })!;
    }

    /// <summary>
    /// Aplica una página de filas remotas. Una fila cuyo principal (obligatorio u opcional) todavía no existe aquí se
    /// aparta en la misma transacción y se reintenta al final de cada descarga (<see cref="RetryParkedPullAsync"/>).
    /// <paramref name="deadPrincipals"/> son los principales que se sabe que se borraron. Con <paramref name="newColumns"/>
    /// (columnas que esta base no conocía, paso 8.7), la fila con la misma marca se aplica completa y la que aquí es más
    /// nueva recibe solo esas columnas.
    /// </summary>
    private async Task<int> ApplyPageGenericAsync<T>(TableMap map, List<Dictionary<string, object?>> rows, Dictionary<Type, Dictionary<Guid, int>> syncIdMaps,
        ISet<(Type, Guid)>? deadPrincipals, IReadOnlyDictionary<string, string>? cursor, bool serverRowAfterReject, ISet<string>? newColumns,
        bool force, CancellationToken ct)
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
        // Paso 8.22: un expediente que ya existe aquí no sigue el LWW por fila: se combina por campo. Lo que queda distinto de la
        // versión de la nube se anota para subir en la misma transacción. «Volver a aplicar» (force) lo escribe tal cual
        bool mergePatients = typeof(T) == typeof(PatientRecord) && !force;
        var markForUpload = new List<(string EntityType, Guid SyncId)>();
        foreach (var (row, syncIdNullable) in incoming)
        {
            var syncId = syncIdNullable!.Value;
            var remoteUpdated = ParseDate(row.GetValueOrDefault("UpdatedAtUtc"));
            locals.TryGetValue(syncId, out var local);

            if (mergePatients && local is PatientRecord patient)
            {
                Unpark(db, parked, entityType, syncId);
                var merged = PatientFieldMerge.Merge(patient, row, map);
                if (!merged.DiffersFromLocal && merged.UpdatedAtUtc == patient.UpdatedAtUtc && merged.FieldStampsJson == patient.FieldStampsJson) continue;
                PatientFieldMerge.Apply(db.Entry(patient), merged, row, map);
                if (merged.DiffersFromRemote) markForUpload.Add((entityType, syncId));
                applied++;
                continue;
            }

            // Last-Write-Wins: la versión local igual o más nueva se conserva (en el desempate, solo la más nueva), y lo
            // apartado de esta fila ya no hace falta
            // «Volver a aplicar» (paso 8.9, force): la fila guardada se aplica sin importar la marca; después se le pone una nueva
            bool remoteWins = force || (serverRowAfterReject
                ? SyncRules.ServerRowAppliesAfterReject(local?.UpdatedAtUtc, remoteUpdated)
                : SyncRules.RemoteRowWins(local?.UpdatedAtUtc, remoteUpdated));
            // Una versión apartada con la misma marca que la local es la misma versión: la bajada de columnas nuevas la
            // apartó porque le faltaba un principal (paso 8.19). Se aplica al reintentar o al volver a leerla en el traslape
            // (o se vuelve a apartar); si no, se perdería con sus columnas nuevas
            if (!remoteWins && local != null && remoteUpdated == local.UpdatedAtUtc && parked.ContainsKey((entityType, syncId))) remoteWins = true;
            bool onlyNewColumns = false;
            if (!remoteWins && newColumns != null && local != null)
            {
                // Columnas nuevas: con la misma marca es la misma versión (la de la nube trae esas columnas); si aquí es
                // más nueva, solo se copian ellas y al subir no se pisa el valor de la nube
                if (remoteUpdated == local.UpdatedAtUtc) remoteWins = true;
                else onlyNewColumns = true;
            }
            if (!remoteWins && !onlyNewColumns)
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
                if (onlyNewColumns && !newColumns!.Contains(reference.RemoteColumn)) continue;
                var principalSyncId = ParseGuid(row.GetValueOrDefault(reference.RemoteColumn));
                // Nulo en una columna recién agregada: es lo que dejó el ALTER, no un valor; se queda el de aquí (más nuevo)
                if (onlyNewColumns && !principalSyncId.HasValue) continue;
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

                if (onlyNewColumns && principalSyncId.HasValue && localId == null) continue; // se queda como está
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
            bool created = entry.State == EntityState.Added;
            foreach (var column in map.Columns)
            {
                if (onlyNewColumns && !newColumns!.Contains(column.Name)) continue;
                // Una columna derivada (paso 8.22) nunca se aplica al bajar: solo da su primer valor a una fila nueva aquí, y
                // enseguida se recalcula
                if (map.IsDerived(column.Name) && !created) continue;
                if (!row.TryGetValue(column.Name, out var raw)) continue;
                // Todas las columnas nuevas aceptan nulo en la nube y el ALTER las llena con nulo: un nulo ahí no es un valor
                // que haya escrito otro equipo, y no pisa lo que se escribió aquí después de actualizar (paso 8.19)
                if (onlyNewColumns && raw == null) continue;
                entry.Property(column.Property.Name).CurrentValue = FromRemoteValue(raw, column.ClrType, column.IsNullable);
            }
            foreach (var (property, id) in resolved)
            {
                entry.Property(property.Name).CurrentValue = property.PropertyType == typeof(int) ? id ?? 0 : id;
            }
            if (!onlyNewColumns) local.UpdatedAtUtc = remoteUpdated;
            applied++;
        }

        await ResolveUniqueConflictsAsync(db, map, ct);
        await SyncChangeLog.SaveRemoteChangesAsync(db, ct, cursor, markForUpload);
        if (markForUpload.Count > 0)
        {
            Interlocked.Add(ref _mergedForUpload, markForUpload.Count);
            AppLogger.Info($"Turso: {markForUpload.Count} expedientes combinados por campo quedaron distintos de la nube y suben.");
        }
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
            // Paso 8.12: las imágenes que dice la nube (se bajan solo si cambió su hash)
            await ReconcileLetterheadAsync(client, RemoteLetterheadHashes(remote), ct);
            return;
        }

        if (localProfile.IsConfigured && (remote == null || localProfile.UpdatedAtUtc > remoteUpdated))
        {
            // Paso 8.12: primero las imágenes (completas en la nube), luego el perfil que las nombra
            var remoteHashes = RemoteLetterheadHashes(remote);
            var hashes = await PrepareLetterheadForPushAsync(client, remoteHashes, ct);

            const string sql = @"
INSERT INTO ClinicProfiles (Id, DoctorName, ProfessionalLicense, Specialty, University, ClinicName, Address, Phone, Email, TaxId, AdditionalNotes, UpdatedAtUtc, CrestImageHash, HeaderImageHash)
VALUES (1, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
ON CONFLICT(Id) DO UPDATE SET
    DoctorName = excluded.DoctorName, ProfessionalLicense = excluded.ProfessionalLicense, Specialty = excluded.Specialty,
    University = excluded.University, ClinicName = excluded.ClinicName, Address = excluded.Address, Phone = excluded.Phone,
    Email = excluded.Email, TaxId = excluded.TaxId, AdditionalNotes = excluded.AdditionalNotes, UpdatedAtUtc = excluded.UpdatedAtUtc,
    CrestImageHash = excluded.CrestImageHash, HeaderImageHash = excluded.HeaderImageHash;";

            await client.ExecuteAsync(sql, new object?[]
            {
                localProfile.DoctorName, localProfile.ProfessionalLicense, localProfile.Specialty, localProfile.University,
                localProfile.ClinicName, localProfile.Address, localProfile.Phone, localProfile.Email, localProfile.TaxId,
                localProfile.AdditionalNotes, FormatDate(localProfile.UpdatedAtUtc ?? DateTime.UtcNow), hashes.Crest, hashes.Header
            }, ct);
            AddExternalUsage(0, 1);

            if (hashes != remoteHashes) await DeleteUnusedClinicAssetsAsync(client, ct);
            // Una imagen que esta PC no tiene y la nube sí (no se eligió ni se quitó aquí) se baja
            await ReconcileLetterheadAsync(client, hashes, ct);
            return;
        }

        // Mismo perfil: solo las imágenes, si esta PC no tiene la que dice la nube (o la nube todavía no sabe de la suya)
        await ReconcileLetterheadAsync(client, RemoteLetterheadHashes(remote), ct);
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
    /// columna obligatoria se vuelve "", 0, false, 0001-01-01 o un BLOB vacío. Un BLOB llega como byte[]
    /// (<see cref="TursoClient.ExtractValue"/>); leído como texto, o un texto en una columna byte[], se
    /// convierte en UTF-8, como CAST en SQLite.
    /// </summary>
    public static object? FromRemoteValue(object? raw, Type targetType, bool isNullable)
    {
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (raw == null)
        {
            if (isNullable) return null;
            if (t == typeof(string)) return string.Empty;
            if (t == typeof(DateTime)) return DateTime.MinValue;
            if (t == typeof(byte[])) return Array.Empty<byte>();
            return Activator.CreateInstance(t);
        }

        var text = raw switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty
        };

        if (t == typeof(byte[])) return raw as byte[] ?? Encoding.UTF8.GetBytes(text);
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
