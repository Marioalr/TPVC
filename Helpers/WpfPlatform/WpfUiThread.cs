using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TPVC.ViewModels.Patients;

namespace TPVC.Helpers.WpfPlatform;

/// <summary>
/// Hilo de la interfaz de WPF para Pacientes. Cada miembro hace lo mismo que hacía el ViewModel en
/// su lugar: consulta Application.Current en cada llamada (sin aplicación, todo corre aquí mismo; si
/// WPF ni siquiera puede iniciarse, como en Linux, lanza la misma excepción que antes y cada lugar la
/// atiende con su propio try/catch) y toma el contexto de sincronización al crearse, que ocurre al
/// crear el ViewModel (en el mismo hilo y momento en que antes se tomaba).
/// </summary>
public sealed class WpfUiThread : IUiThread
{
    private readonly InlineUiThread _context = new(SynchronizationContext.Current);

    public bool IsOffUiThread => Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess();

    public Task InvokeAsync(Action action) => CurrentDispatcher().InvokeAsync(action).Task;

    public Task InvokeAsync(Func<Task> action) => CurrentDispatcher().InvokeAsync(action).Task;

    public bool TryPostBackground(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return false;
        dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        return true;
    }

    public void Post(Action action) => _context.Post(action);

    public Task RunAsync(Func<Task> action) => _context.RunAsync(action);

    /// <summary>Solo se llama después de IsOffUiThread == true: ya se sabe que hay aplicación.</summary>
    private static Dispatcher CurrentDispatcher() =>
        Application.Current?.Dispatcher ?? throw new InvalidOperationException("No hay hilo de interfaz de WPF.");
}
