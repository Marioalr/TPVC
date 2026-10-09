using System;
using System.Threading;
using System.Threading.Tasks;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Sin hilo de interfaz que esperar (pruebas en Linux, más adelante MAUI): nunca se está "fuera" del
/// hilo y no se difiere nada. Post y RunAsync usan el contexto recibido exactamente como lo hacía el
/// ViewModel con el contexto que tomaba al crearse (WpfUiThread los reutiliza tal cual).
/// </summary>
public sealed class InlineUiThread : IUiThread
{
    private readonly SynchronizationContext? _context;

    public InlineUiThread(SynchronizationContext? context = null)
    {
        _context = context;
    }

    public bool IsOffUiThread => false;

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task InvokeAsync(Func<Task> action) => action();

    public bool TryPostBackground(Action action) => false;

    public void Post(Action action)
    {
        if (_context != null) _context.Post(_ => action(), null);
        else action();
    }

    public Task RunAsync(Func<Task> action)
    {
        var context = _context;
        if (context == null || SynchronizationContext.Current == context) return action();

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(async _ =>
        {
            try
            {
                await action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }
}
