using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Paso 8.24 (A5): la cadencia de la PC con la nube activa (§3.3 del plan de la Fase 8). Sin WPF y con el reloj de la
/// sincronización (<see cref="TursoSyncService.Clock"/>), así las pruebas la mueven con tiempo simulado
/// (<see cref="TickAsync"/>), sin esperas reales.
/// <list type="bullet">
/// <item><b>Revisión</b> (<see cref="ConsultationHandoffService.PollAsync"/>), una sola petición: consultas recibidas, el estado
/// del envío propio, la marca de cambios (el LastPushUtc de los otros equipos), el latido cada 60 s, la lista de
/// compañeros cada 10 min y la limpieza cada 6 h (solo la PC). Cada 4 s en Pacientes si hay un compañero (un equipo que
/// puede recibir consultas, visto en los últimos 30 días) o un envío pendiente; si no, cada 30 s.</item>
/// <item><b>Marca de cambios.</b> Si la subida más reciente de otro equipo es posterior a la última bajada de este (las dos con
/// la hora de la nube), se sincroniza en ese momento. Cada valor de LastPushUtc dispara a lo más una sincronización que
/// termina: una marca rara (en el futuro) no hace un ciclo.</item>
/// <item><b>Descarga de seguridad</b> cada 10 min desde el último intento (atrapa lo que suba un equipo que no escribe
/// LastPushUtc, como una PC anterior a v1.27); al activar la ventana si la última sincronización buena fue hace más de
/// 30 s; al volver de la suspensión o de un corte de red.</item>
/// <item><b>Sin red</b>, reintentos a los 30 s, 1 min, 2 min y 5 min (y cada 5 min después); la revisión también se espacia
/// así. Cualquier sincronización que falle por la red (también la de después de guardar o la rápida) entra aquí.</item>
/// <item><b>Pausas.</b> Con la cuota agotada (BLOCKED, reintento cada hora como siempre), el freno de consumo, el token
/// rechazado u otra nube no se dispara nada; antes de la primera sincronización buena tampoco (la primera descarga la
/// empieza el arranque o el médico). Con la nube apagada no corre nada.</item>
/// </list>
/// Toda sincronización que pide comparte el candado de las demás (<see cref="TursoSyncService.ScheduledSyncAsync"/>): si
/// hay otra en curso no espera ni se repite.
/// </summary>
public sealed class SyncScheduler
{
    /// <summary>La revisión en Pacientes con un compañero o un envío pendiente.</summary>
    public static readonly TimeSpan FastCheckInterval = TimeSpan.FromSeconds(4);

    /// <summary>La revisión fuera de Pacientes, o sin compañeros ni envío pendiente.</summary>
    public static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>La descarga de seguridad, contada desde el último intento.</summary>
    public static readonly TimeSpan SafetyDownloadInterval = TimeSpan.FromMinutes(10);

    /// <summary>Al activar la ventana se sincroniza si la última buena fue hace más que esto.</summary>
    public static readonly TimeSpan ActivationSyncAge = TimeSpan.FromSeconds(30);

    /// <summary>Con «Sincronizar al abrir el programa», la cadencia no sincroniza en este tiempo: lo hace el arranque.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    /// <summary>Sin red: el primer reintento, el segundo, el tercero y de ahí en adelante.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = new[]
    {
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)
    };

    /// <summary>Por qué sincronizó la cadencia (para la bitácora y las pruebas).</summary>
    public const string ReasonPeerChanges = "cambios de otro equipo";
    public const string ReasonSafety = "descarga de seguridad";
    public const string ReasonRetry = "reintento";
    public const string ReasonActivated = "ventana activada";
    public const string ReasonNetworkBack = "volvió la red";
    public const string ReasonResumed = "volvió de la suspensión";

    /// <summary>Lo menos que espera la vuelta entre dos pasadas (nunca un ciclo apretado).</summary>
    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    private readonly TursoSyncService _sync;
    private readonly ConsultationHandoffService _handoff;
    private readonly TursoSettingsService _settings;
    private readonly object _gate = new();

    // Estado, siempre bajo _gate (lo tocan la vuelta, la revisión y el final de cada sincronización)
    private bool _begun;
    private DateTime _nextCheckAt;
    private int _checkFailures;
    private int _syncFailures;
    private DateTime? _retryAt;
    private DateTime _lastGoodSyncAt;
    private DateTime _lastAttemptAt;
    private DateTime _startupGraceUntil;
    private string? _requested;
    private DateTime? _peersLastPushUtc;
    private DateTime? _handledPeerPush;
    private int? _peerCount;
    private bool _pendingSendSeen;
    private long _completedSyncs;

    private volatile bool _inPatients;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource _wakeCts = new();

    public SyncScheduler(TursoSyncService sync, ConsultationHandoffService handoff, TursoSettingsService settings)
    {
        _sync = sync;
        _handoff = handoff;
        _settings = settings;
        _sync.SyncCompleted += OnSyncCompleted;
    }

    private DateTime Now => _sync.Clock.GetUtcNow().UtcDateTime;

    /// <summary>La nube está activada con URL y token. Si no, no se pregunta nada a Turso.</summary>
    public bool IsCloudOn
    {
        get
        {
            var s = _settings.CurrentSettings;
            return s.IsSyncEnabled && !string.IsNullOrWhiteSpace(s.DatabaseUrl) && !string.IsNullOrWhiteSpace(s.AuthToken);
        }
    }

    /// <summary>El médico está en Pacientes (la revisión puede ir cada 4 s). Al entrar se revisa enseguida.</summary>
    public bool InPatientsSection
    {
        get => _inPatients;
        set
        {
            bool entering = value && !_inPatients;
            _inPatients = value;
            if (!entering) return;
            lock (_gate) _nextCheckAt = Now;
            Wake();
        }
    }

    /// <summary>Hay una consulta enviada desde aquí que el otro equipo todavía no toma (la da la pantalla de Pacientes).</summary>
    public Func<bool>? HasPendingSend { get; set; }

    /// <summary>
    /// La revisión que corre la vuelta: la de la pantalla de Pacientes (avisos y estado del envío). Sin ella (pruebas, o
    /// antes de abrir la ventana), <see cref="ConsultationHandoffService.PollAsync"/> directamente.
    /// </summary>
    public Func<CancellationToken, Task>? CheckRunner { get; set; }

    /// <summary>Cada sincronización que pide la cadencia, con su motivo (antes de correrla).</summary>
    public event Action<string>? SyncRequested;

    /// <summary>Hay al menos un compañero que puede recibir consultas (sin saberlo todavía, se supone que sí).</summary>
    public bool HasPeer
    {
        get { lock (_gate) return _peerCount is not 0; }
    }

    /// <summary>Cada cuánto va la revisión ahora mismo (sin contar los reintentos).</summary>
    public TimeSpan CheckInterval
    {
        get { lock (_gate) return NormalCheckInterval(); }
    }

    /// <summary>El siguiente reintento sin red, si hay uno.</summary>
    public DateTime? RetryAtUtc
    {
        get { lock (_gate) return _retryAt; }
    }

    // ================================================================== la vuelta

    /// <summary>Empieza la vuelta en segundo plano (la abre la pantalla de Pacientes al arrancar).</summary>
    public void Start()
    {
        if (!_handoff.AutoPoll || _loopCts != null) return;
        Begin();
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                DateTime next;
                try
                {
                    next = await TickAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Falló la cadencia de la sincronización", ex);
                    next = Now + IdleCheckInterval;
                }

                var delay = next - Now;
                if (delay < MinimumWait) delay = MinimumWait;
                var wake = Volatile.Read(ref _wakeCts);
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wake.Token);
                    await Task.Delay(delay, _sync.Clock, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) break;
                }
            }
        }, ct);
    }

    public void Stop()
    {
        _loopCts?.Cancel();
        _loopCts = null;
    }

    /// <summary>Despierta la vuelta para que revise lo que ya toca.</summary>
    public void Wake()
    {
        var previous = Interlocked.Exchange(ref _wakeCts, new CancellationTokenSource());
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>El punto de partida: revisar enseguida y contar los 10 min de la descarga de seguridad desde ahora.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            var now = Now;
            _begun = true;
            _nextCheckAt = now;
            _lastGoodSyncAt = now;
            _lastAttemptAt = now;
            _startupGraceUntil = _settings.CurrentSettings.AutoSyncOnStartup ? now + StartupGrace : now;
            _checkFailures = 0;
            _syncFailures = 0;
            _retryAt = null;
            _requested = null;
        }
    }

    /// <summary>
    /// Una pasada: la revisión si ya toca y la sincronización que toque. Regresa cuándo vuelve a tocar algo. La vuelta en
    /// segundo plano la llama sin parar; las pruebas, adelantando el reloj.
    /// </summary>
    public async Task<DateTime> TickAsync(CancellationToken ct = default)
    {
        bool checkDue;
        lock (_gate)
        {
            if (!_begun) Begin();
            checkDue = Now >= _nextCheckAt;
        }

        bool cloudOn = IsCloudOn;
        if (checkDue)
        {
            await RunCheckAsync(cloudOn, ct);
            lock (_gate) _nextCheckAt = Now + (cloudOn ? CheckDelay() : IdleCheckInterval);
        }

        if (cloudOn && TakeDueSync() is { } reason) await RunSyncAsync(reason, ct);

        lock (_gate) return NextWake(cloudOn);
    }

    private async Task RunCheckAsync(bool cloudOn, CancellationToken ct)
    {
        try
        {
            if (CheckRunner is { } runner) await runner(ct);
            else if (cloudOn) await _handoff.PollAsync(outgoingId: null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ConsultationHandoffService.IsConnectivityFailure(ex) || ex is TursoException)
        {
            // Sin red, pausa o error de la nube: la revisión lo anota (OnCheckFailed) y la pantalla lo muestra
        }
    }

    private TimeSpan NormalCheckInterval()
    {
        bool pendingSend = _pendingSendSeen || (HasPendingSend?.Invoke() ?? false);
        return _inPatients && (_peerCount is not 0 || pendingSend) ? FastCheckInterval : IdleCheckInterval;
    }

    private TimeSpan CheckDelay()
    {
        var normal = NormalCheckInterval();
        if (_checkFailures == 0) return normal;
        var backoff = RetryDelay(_checkFailures);
        return backoff > normal ? backoff : normal;
    }

    private static TimeSpan RetryDelay(int failures) => RetryDelays[Math.Clamp(failures - 1, 0, RetryDelays.Count - 1)];

    /// <summary>La sincronización que toca ahora, si alguna (y la quita de pendientes).</summary>
    private string? TakeDueSync()
    {
        // La pausa manda (cuota agotada, freno, token rechazado, otra nube); con una en curso, la siguiente pasada decide
        if (_sync.IsAutomaticSyncBlocked || _sync.IsSyncRunning) return null;
        lock (_gate)
        {
            var now = Now;
            if (_retryAt is { } retryAt) return now >= retryAt ? ReasonRetry : null;

            // La primera descarga es larga: la empieza el arranque o el médico, nunca la cadencia
            if (_settings.CurrentSettings.LastSyncUtc == null) return null;
            if (now < _startupGraceUntil) return null;

            if (_requested is { } requested)
            {
                _requested = null;
                return requested;
            }
            if (_peersLastPushUtc is { } push && push != _handledPeerPush && (_sync.LastPullServerUtc is not { } pulled || push > pulled))
                return ReasonPeerChanges;
            if (now - Latest(_lastGoodSyncAt, _lastAttemptAt) >= SafetyDownloadInterval) return ReasonSafety;
            return null;
        }
    }

    private async Task RunSyncAsync(string reason, CancellationToken ct)
    {
        DateTime? push;
        long before;
        lock (_gate)
        {
            push = _peersLastPushUtc;
            before = _completedSyncs;
        }
        SyncRequested?.Invoke(reason);
        try
        {
            await _sync.ScheduledSyncAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Turso: la sincronización por {reason} no pudo completarse: {ex.Message}");
        }

        // Una sincronización que corrió (buena o no) ya atendió esa marca de cambios: la misma no vuelve a disparar otra
        lock (_gate)
        {
            if (_completedSyncs != before && push != null) _handledPeerPush = push;
        }
    }

    private DateTime NextWake(bool cloudOn)
    {
        var next = _nextCheckAt;
        if (!cloudOn || _sync.IsAutomaticSyncBlocked) return next;
        if (_retryAt is { } retryAt) return Earliest(next, retryAt);
        if (_settings.CurrentSettings.LastSyncUtc == null) return next;
        var safety = Latest(_lastGoodSyncAt, _lastAttemptAt) + SafetyDownloadInterval;
        if (_requested != null) safety = Now;
        return Earliest(next, Latest(safety, _startupGraceUntil));
    }

    private static DateTime Earliest(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Latest(DateTime a, DateTime b) => a >= b ? a : b;

    // ================================================================== lo que avisan los demás

    /// <summary>Una revisión que respondió: la marca de cambios, los compañeros (si los leyó) y si había un envío pendiente.</summary>
    internal void OnCheckSucceeded(HandoffPollResult result)
    {
        lock (_gate)
        {
            // Volvió la red después de revisiones fallidas: se sincroniza ya, sin esperar el reintento
            if (_checkFailures > 0 && (_retryAt != null || _syncFailures > 0))
            {
                _retryAt = null;
                _requested ??= ReasonNetworkBack;
            }
            _checkFailures = 0;
            _peersLastPushUtc = result.PeersLastPushUtc;
            if (result.HandoffPeerCount is { } peers) _peerCount = peers;
            _pendingSendSeen = result.HadOutgoing;
        }
    }

    /// <summary>Una revisión que no llegó a la nube (sin red): la siguiente se espacia como los reintentos.</summary>
    internal void OnCheckFailed()
    {
        lock (_gate) _checkFailures++;
    }

    /// <summary>La lista de equipos se acaba de leer (al abrir «Enviar a…»).</summary>
    internal void OnPeersRefreshed(int peerCount)
    {
        lock (_gate) _peerCount = peerCount;
    }

    /// <summary>La ventana de TPVC se activó: sincroniza si la última buena fue hace más de 30 s.</summary>
    public void OnWindowActivated()
    {
        lock (_gate)
        {
            if (!_begun || Now - _lastGoodSyncAt <= ActivationSyncAge) return;
            _requested ??= ReasonActivated;
        }
        Wake();
    }

    /// <summary>Volvió la red o el equipo salió de la suspensión: revisa y sincroniza enseguida, sin esperar el reintento.</summary>
    public void OnConnectivityRestored(bool resumed = false)
    {
        lock (_gate)
        {
            if (!_begun) return;
            _checkFailures = 0;
            _retryAt = null;
            _requested = resumed ? ReasonResumed : ReasonNetworkBack;
            _nextCheckAt = Now;
        }
        Wake();
    }

    /// <summary>Al terminar cualquier sincronización (también la de después de guardar, la rápida o la del médico).</summary>
    private void OnSyncCompleted(TursoSyncResult result)
    {
        lock (_gate)
        {
            _completedSyncs++;
            var now = Now;
            _lastAttemptAt = now;
            if (result.Success)
            {
                _lastGoodSyncAt = now;
                _syncFailures = 0;
                _retryAt = null;
            }
            else if (_sync.Status is TursoSyncStatus.Offline or TursoSyncStatus.Error)
            {
                // Sin red (o un error de la nube): reintentos crecientes
                _syncFailures++;
                _retryAt = now + RetryDelay(_syncFailures);
            }
            else
            {
                // En pausa (cuota, freno, token, otra nube, respaldo restaurado): la pausa decide cuándo volver
                _syncFailures = 0;
                _retryAt = null;
            }
        }
    }
}
