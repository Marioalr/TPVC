using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TPVC.Helpers;

/// <summary>
/// Una sola ventana de TPVC por base de datos y sesión de Windows. La primera instancia toma un mutex con nombre y
/// atiende los avisos de las demás; una segunda (doble clic en el escritorio, menú de inicio) no obtiene el mutex,
/// avisa a la primera para que traiga su ventana al frente, espera su respuesta y termina. Antes se abría otra
/// ventana sobre la misma base, lo que confundía.
/// </summary>
/// <remarks>
/// Los avisos y las respuestas son semáforos con nombre, no eventos: dos dobles clics seguidos son dos avisos y cada
/// segunda instancia recibe su respuesta (con un evento se juntarían en uno y la otra esperaría en vano).
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _mutexName;
    private readonly string _requestsName;
    private readonly string _answersName;
    private Mutex? _mutex;
    private bool _owned;
    private Semaphore? _requests;
    private Semaphore? _answers;
    private RegisteredWaitHandle? _listener;

    /// <param name="key">Lo que identifica la instancia: dos claves iguales no pueden estar abiertas a la vez.</param>
    public SingleInstanceGuard(string key)
    {
        // "Local\": por sesión de Windows; otro usuario de la misma PC abre la suya
        var prefix = $@"Local\TPVC-{key}";
        _mutexName = prefix + "-instancia";
        _requestsName = prefix + "-mostrar";
        _answersName = prefix + "-mostrada";
    }

    /// <summary>La clave de una base: su ruta sin distinguir mayúsculas (un compilado de prueba con la misma base también cuenta).</summary>
    public static string KeyFor(string databasePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(databasePath.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }

    /// <summary>¿Es la primera instancia? Debe llamarse en el hilo que después llama a <see cref="Dispose"/>.</summary>
    public bool TryAcquire()
    {
        _mutex ??= new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            _owned = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // La instancia anterior terminó sin soltarlo (se cerró a la fuerza): ahora es de esta
            _owned = true;
        }
        return _owned;
    }

    /// <summary>
    /// En la primera instancia: <paramref name="onActivate"/> corre (en un hilo del sistema) cada vez que otra
    /// instancia pide traer la ventana al frente. Un aviso que llegó antes de escuchar también cuenta.
    /// </summary>
    public void ListenForActivation(Action onActivate)
    {
        _requests ??= OpenSemaphore(_requestsName);
        _answers ??= OpenSemaphore(_answersName);
        var answers = _answers;
        _listener = ThreadPool.RegisterWaitForSingleObject(_requests, (_, _) =>
        {
            // Se responde después de pedir la ventana al frente, y también si eso falla: la otra no espera en vano
            try
            {
                onActivate();
            }
            finally
            {
                answers.Release();
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// En una segunda instancia: pide a la primera que traiga su ventana al frente y espera hasta
    /// <paramref name="timeout"/> a que responda. False si no respondió (por ejemplo, si quedó colgada).
    /// </summary>
    public bool SignalExistingInstance(TimeSpan timeout)
    {
        // Esta instancia la abrió el usuario: puede ceder el primer plano a la otra, que si no solo parpadearía
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AnyProcess);

        using var requests = OpenSemaphore(_requestsName);
        using var answers = OpenSemaphore(_answersName);
        requests.Release();
        return answers.WaitOne(timeout);
    }

    public void Dispose()
    {
        _listener?.Unregister(null);
        _listener = null;
        _requests?.Dispose();
        _answers?.Dispose();
        _requests = _answers = null;
        if (_owned)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Se llamó desde otro hilo: Windows lo suelta al terminar el proceso
            }
            _owned = false;
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>Lo abre si ya existe y si no lo crea (en cero): da igual cuál de las dos instancias llegue primero.</summary>
    private static Semaphore OpenSemaphore(string name) => new(0, int.MaxValue, name);

    private const int AnyProcess = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
