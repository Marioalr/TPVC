using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Paso 8.24 (A5): la cadencia de la PC con la nube activa (§3.3 del plan de la Fase 8). Sin WPF y con el reloj de la
/// sincronización (<see cref="TursoSyncService.Clock"/>), así las pruebas la mueven con tiempo simulado
/// (<see cref="TickAsync"/>), sin esperas reales. Los plazos se cuentan con el reloj monótono de ese mismo
/// <see cref="TimeProvider"/> (<c>GetTimestamp</c>): mover la hora de Windows (hacia atrás o adelante) no los cambia. La hora
/// de pared solo se compara con marcas de la nube (LastPushUtc contra la última bajada).
/// <list type="bullet">
/// <item><b>Revisión</b> (<see cref="ConsultationHandoffService.PollAsync"/>), una sola petición: consultas recibidas, el estado
/// del envío propio, la marca de cambios (el LastPushUtc de los otros equipos), el latido cada 60 s, la lista de
/// compañeros cada 10 min y la limpieza cada 6 h (solo la PC). Cada 4 s en Pacientes si hay un compañero (un equipo que
/// puede recibir consultas, visto en los últimos 30 días) o un envío pendiente; si no, cada 30 s.</item>
/// <item><b>Marca de cambios.</b> Si la última subida de otro equipo (su LastPushUtc) es posterior a la última bajada de este
/// (las dos con la hora de la nube), se sincroniza en ese momento. Cada valor de LastPushUtc de cada equipo dispara a lo más
/// una sincronización que corre: una marca rara (en el futuro) no hace un ciclo ni esconde las subidas de los demás.</item>
/// <item><b>Descarga de seguridad</b> cada 10 min desde el último intento (atrapa lo que suba un equipo que no escribe
/// LastPushUtc, como una PC anterior a v1.27); al activar la ventana si la última sincronización buena fue hace más de
/// 30 s; al volver de la suspensión o de un corte de red.</item>
/// <item><b>Sin red</b>, reintentos a los 30 s, 1 min, 2 min y 5 min (y cada 5 min después); la revisión también se espacia
/// así, salvo en Pacientes con un compañero o un envío pendiente, donde la revisión no pasa de 30 s. Cualquier sincronización
/// que falle por la red (también la de después de guardar o la rápida) entra aquí, y cualquier cosa que llegue a la nube
/// (una sincronización buena, enviar o aceptar una consulta) regresa la revisión a su paso enseguida.</item>
/// <item><b>Pausas.</b> Con la cuota agotada (BLOCKED, reintento cada hora como siempre), el freno de consumo, el token
/// rechazado u otra nube no se dispara nada; antes de la primera sincronización buena tampoco (la primera descarga la
/// empieza el arranque o el médico). Con la nube apagada no corre nada.</item>
/// </list>
/// Toda sincronización que pide comparte el candado de las demás (<see cref="TursoSyncService.ScheduledSyncAsync"/>): si
/// hay otra en curso no espera ni se repite. La pide sin esperarla: mientras corre (una bajada larga, otra generación, una red
/// que no contesta), la revisión y el latido siguen a su paso.
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
    private readonly long _origin;

    // Estado, siempre bajo _gate (lo tocan la vuelta, la revisión y el final de cada sincronización). Los plazos son
    // tiempo monótono desde que se creó (Mono), nunca la hora de pared
    private bool _begun;
    private TimeSpan _nextCheckAt;
    private int _checkFailures;
    private int _syncFailures;
    private TimeSpan? _retryAt;
    private bool _retryIsNetwork;
    private TimeSpan _lastGoodSyncAt;
    private TimeSpan _lastAttemptAt;
    private TimeSpan _startupGraceUntil;
    private string? _requested;
    private IReadOnlyDictionary<string, DateTime> _peerPushes = new Dictionary<string, DateTime>();
    private readonly Dictionary<string, DateTime> _handledPushes = new(StringComparer.OrdinalIgnoreCase);
    private int? _peerCount;
    private bool _pendingSendSeen;
    private long _completedSyncs;
    private Task? _syncRun;

    private volatile bool _inPatients;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource _wakeCts = new();

    public SyncScheduler(TursoSyncService sync, ConsultationHandoffService handoff, TursoSettingsService settings)
    {
        _sync = sync;
        _handoff = handoff;
        _settings = settings;
        _origin = sync.Clock.GetTimestamp();
        _sync.SyncCompleted += OnSyncCompleted;
    }

    /// <summary>Tiempo monótono (no se mueve con la hora de Windows) del mismo reloj que la sincronización.</summary>
    private TimeSpan Mono => _sync.Clock.GetElapsedTime(_origin);

    /// <summary>La revisión de este equipo (la que avisa a esta cadencia lo que encontró).</summary>
    internal ConsultationHandoffService Handoff => _handoff;

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
            lock (_gate) _nextCheckAt = Mono;
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

    /// <summary>Hay un reintento sin red esperando.</summary>
    public bool IsRetryPending
    {
        get { lock (_gate) return _retryAt != null; }
    }

    /// <summary>Cuánto falta para la siguiente revisión (las pruebas lo usan para ver que la hora de Windows no lo mueve).</summary>
    public TimeSpan TimeToNextCheck
    {
        get { lock (_gate) return _nextCheckAt - Mono; }
    }

    /// <summary>La sincronización que pidió la cadencia y sigue corriendo (o una ya terminada).</summary>
    internal Task PendingSync => Volatile.Read(ref _syncRun) ?? Task.CompletedTask;

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
                TimeSpan delay;
                try
                {
                    delay = await TickAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Falló la cadencia de la sincronización", ex);
                    delay = IdleCheckInterval;
                }

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
            var now = Mono;
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
    /// Una pasada: la revisión si ya toca y, si toca una sincronización, la empieza sin esperarla (sigue en
    /// <see cref="PendingSync"/>). Regresa cuánto falta para que vuelva a tocar algo. La vuelta en segundo plano la llama sin
    /// parar; las pruebas, adelantando el reloj.
    /// </summary>
    public async Task<TimeSpan> TickAsync(CancellationToken ct = default)
    {
        bool checkDue;
        lock (_gate)
        {
            if (!_begun) Begin();
            checkDue = Mono >= _nextCheckAt;
        }

        bool cloudOn = IsCloudOn;
        if (checkDue)
        {
            await RunCheckAsync(cloudOn, ct);
            lock (_gate) _nextCheckAt = Mono + (cloudOn ? CheckDelay() : IdleCheckInterval);
        }

        if (cloudOn && TakeDueSync() is { } reason) StartSync(reason);

        lock (_gate) return NextWake(cloudOn) - Mono;
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

    private bool FastMode()
    {
        bool pendingSend = _pendingSendSeen || (HasPendingSend?.Invoke() ?? false);
        return _inPatients && (_peerCount is not 0 || pendingSend);
    }

    private TimeSpan NormalCheckInterval() => FastMode() ? FastCheckInterval : IdleCheckInterval;

    private TimeSpan CheckDelay()
    {
        var normal = NormalCheckInterval();
        if (_checkFailures == 0) return normal;
        var backoff = RetryDelay(_checkFailures);
        // Con alguien a quien enviarle o de quien recibir una consulta, la revisión sin red no pasa de 30 s
        if (FastMode() && backoff > IdleCheckInterval) backoff = IdleCheckInterval;
        return backoff > normal ? backoff : normal;
    }

    private static TimeSpan RetryDelay(int failures) => RetryDelays[Math.Clamp(failures - 1, 0, RetryDelays.Count - 1)];

    private bool SyncInFlight => Volatile.Read(ref _syncRun) is { IsCompleted: false };

    /// <summary>La sincronización que toca ahora, si alguna (y la quita de pendientes).</summary>
    private string? TakeDueSync()
    {
        // La pausa manda (cuota agotada, freno, token rechazado, otra nube); con una en curso, la siguiente pasada decide
        if (_sync.IsAutomaticSyncBlocked || _sync.IsSyncRunning || SyncInFlight) return null;
        lock (_gate)
        {
            var now = Mono;
            if (_retryAt is { } retryAt) return now >= retryAt ? ReasonRetry : null;

            // La primera descarga es larga: la empieza el arranque o el médico, nunca la cadencia
            if (_settings.CurrentSettings.LastSyncUtc == null) return null;
            if (now < _startupGraceUntil) return null;

            if (_requested is { } requested)
            {
                _requested = null;
                return requested;
            }
            if (HasUnseenPush()) return ReasonPeerChanges;
            if (now - Latest(_lastGoodSyncAt, _lastAttemptAt) >= SafetyDownloadInterval) return ReasonSafety;
            return null;
        }
    }

    /// <summary>
    /// Algún equipo subió después de la última bajada de este (las dos marcas con la hora de la nube) y esa subida no la
    /// atendió ya una sincronización que corrió.
    /// </summary>
    private bool HasUnseenPush()
    {
        var pulled = _sync.LastPullServerUtc;
        foreach (var (deviceId, pushed) in _peerPushes)
        {
            if (pulled is { } p && pushed <= p) continue;
            if (_handledPushes.TryGetValue(deviceId, out var handled) && handled == pushed) continue;
            return true;
        }
        return false;
    }

    /// <summary>Empieza la sincronización sin esperarla: la revisión y el latido siguen mientras corre.</summary>
    private void StartSync(string reason)
    {
        IReadOnlyDictionary<string, DateTime> pushes;
        long before;
        lock (_gate)
        {
            pushes = _peerPushes;
            before = _completedSyncs;
        }
        SyncRequested?.Invoke(reason);
        var run = Task.Run(async () =>
        {
            try
            {
                await _sync.ScheduledSyncAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Turso: la sincronización por {reason} no pudo completarse: {ex.Message}");
            }

            // Una sincronización que corrió (buena o no) ya atendió esas marcas de cambios: las mismas no vuelven a disparar otra
            lock (_gate)
            {
                if (_completedSyncs != before)
                    foreach (var (deviceId, pushed) in pushes) _handledPushes[deviceId] = pushed;
            }
            // Lo que siga (un reintento, otra marca) se decide en la siguiente pasada
            Wake();
        });
        Volatile.Write(ref _syncRun, run);
    }

    private TimeSpan NextWake(bool cloudOn)
    {
        var next = _nextCheckAt;
        // Mientras corre una sincronización (o la pausa manda) solo la revisión: al terminar, la vuelta despierta
        if (!cloudOn || _sync.IsAutomaticSyncBlocked || SyncInFlight) return next;
        if (_retryAt is { } retryAt) return Earliest(next, retryAt);
        if (_settings.CurrentSettings.LastSyncUtc == null) return next;
        var safety = Latest(_lastGoodSyncAt, _lastAttemptAt) + SafetyDownloadInterval;
        if (_requested != null) safety = Mono;
        return Earliest(next, Latest(safety, _startupGraceUntil));
    }

    private static TimeSpan Earliest(TimeSpan a, TimeSpan b) => a <= b ? a : b;
    private static TimeSpan Latest(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    // ================================================================== lo que avisan los demás

    /// <summary>Una revisión que respondió: la marca de cambios, los compañeros (si los leyó) y si había un envío pendiente.</summary>
    internal void OnCheckSucceeded(HandoffPollResult result)
    {
        lock (_gate)
        {
            // Volvió la red después de revisiones o sincronizaciones fallidas por la red: se sincroniza ya, sin esperar el
            // reintento (un error que no es de la red sigue esperando el suyo: no se repite en cada revisión)
            if ((_checkFailures > 0 && (_retryAt != null || _syncFailures > 0)) || (_retryAt != null && _retryIsNetwork))
            {
                _retryAt = null;
                _requested ??= ReasonNetworkBack;
            }
            _checkFailures = 0;
            _peerPushes = result.PeerPushes;
            if (result.HandoffPeerCount is { } peers) _peerCount = peers;
            _pendingSendSeen = result.HadOutgoing;
        }
    }

    /// <summary>Una revisión que no llegó a la nube (sin red): la siguiente se espacia como los reintentos.</summary>
    internal void OnCheckFailed()
    {
        lock (_gate) _checkFailures++;
    }

    /// <summary>
    /// Algo llegó a la nube fuera de la revisión (enviar, aceptar o recuperar una consulta, la lista de equipos): si la
    /// revisión estaba espaciada por la red, vuelve a su paso ya.
    /// </summary>
    internal void OnCloudReachable()
    {
        bool wake;
        lock (_gate)
        {
            wake = _checkFailures > 0;
            if (!wake) return;
            _checkFailures = 0;
            _nextCheckAt = Mono;
        }
        Wake();
    }

    /// <summary>La lista de equipos se acaba de leer (al abrir «Enviar a…»).</summary>
    internal void OnPeersRefreshed(int peerCount)
    {
        lock (_gate) _peerCount = peerCount;
    }

    /// <summary>
    /// La ventana de TPVC se activó: revisa enseguida y sincroniza si la última buena fue hace más de 30 s (con un reintento
    /// por la red esperando, lo adelanta la revisión si responde).
    /// </summary>
    public void OnWindowActivated()
    {
        lock (_gate)
        {
            if (!_begun) return;
            var now = Mono;
            _nextCheckAt = now;
            if (now - _lastGoodSyncAt > ActivationSyncAge) _requested ??= ReasonActivated;
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
            _nextCheckAt = Mono;
        }
        Wake();
    }

    /// <summary>Al terminar cualquier sincronización (también la de después de guardar, la rápida o la del médico).</summary>
    private void OnSyncCompleted(TursoSyncResult result)
    {
        bool wake = false;
        lock (_gate)
        {
            _completedSyncs++;
            var now = Mono;
            _lastAttemptAt = now;
            if (result.Success)
            {
                _lastGoodSyncAt = now;
                _syncFailures = 0;
                _retryAt = null;
                // La nube responde: si la revisión estaba espaciada por la red, vuelve a su paso ya
                if (_checkFailures > 0)
                {
                    _checkFailures = 0;
                    _nextCheckAt = now;
                    wake = true;
                }
            }
            else if (_sync.Status is TursoSyncStatus.Offline or TursoSyncStatus.Error)
            {
                // Sin red (o un error de la nube): reintentos crecientes
                _syncFailures++;
                _retryAt = now + RetryDelay(_syncFailures);
                _retryIsNetwork = _sync.Status == TursoSyncStatus.Offline;
            }
            else
            {
                // En pausa (cuota, freno, token, otra nube, respaldo restaurado): la pausa decide cuándo volver
                _syncFailures = 0;
                _retryAt = null;
            }
        }
        if (wake) Wake();
    }
}
