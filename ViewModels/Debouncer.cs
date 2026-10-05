using System;
using System.Threading;
using System.Threading.Tasks;

namespace TPVC.ViewModels;

/// <summary>
/// Agrupa ráfagas de cambios (p.ej. cada tecla en un buscador) y ejecuta solo la última.
/// Sin aplicación WPF en ejecución (pruebas) el retraso es 0 para que el resultado sea inmediato.
/// </summary>
internal sealed class Debouncer
{
    private CancellationTokenSource? _cts;

    public static int DefaultDelayMs { get; } = ResolveDefaultDelay();

    private static int ResolveDefaultDelay()
    {
        try
        {
            return System.Windows.Application.Current != null ? 250 : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task RunAsync(Func<Task> action, int? delayMs = null)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        var delay = delayMs ?? DefaultDelayMs;
        if (delay > 0)
        {
            try
            {
                await Task.Delay(delay, cts.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        if (cts.IsCancellationRequested) return;
        await action();
    }
}
