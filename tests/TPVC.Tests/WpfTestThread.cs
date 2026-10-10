using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace TPVC.Tests;

/// <summary>
/// El hilo STA donde corren las pruebas que levantan la interfaz (humo y rendimiento). WPF admite una
/// sola Application por proceso y la ata al hilo que la crea: si cada prueba usara un hilo propio, la
/// que corre después heredaría una aplicación cuyo despachador ya terminó (lo que se envía a la
/// interfaz nunca corre y un Invoke síncrono se queda esperando para siempre). Este hilo vive lo que
/// dura el proceso y corre las sesiones una tras otra; la primera crea la aplicación en él, como el
/// hilo de la interfaz de la aplicación real. Entre sesiones solo espera la siguiente sin atender la
/// cola del despachador: lo que otras pruebas envían a la interfaz no corre en paralelo con ellas.
/// </summary>
internal static class WpfTestThread
{
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly Lazy<Thread> Worker = new(Start);

    public static void Run(Action session)
    {
        _ = Worker.Value;
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        Work.Add(() =>
        {
            try
            {
                if (System.Windows.Application.Current is { } app && !app.Dispatcher.CheckAccess())
                {
                    throw new InvalidOperationException(
                        "La aplicación de WPF se creó en otro hilo: las pruebas de interfaz deben correr con WpfTestThread.Run");
                }
                session();
            }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Thread Start()
    {
        var thread = new Thread(() =>
        {
            foreach (var session in Work.GetConsumingEnumerable()) session();
        })
        {
            IsBackground = true,
            Name = "Pruebas de interfaz (STA)"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }
}
