using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TPVC.Services;

public enum NotificationKind
{
    Success,
    Info,
    Warning,
    Error
}

public sealed class Notification
{
    public Notification(string message, NotificationKind kind)
    {
        Message = message;
        Kind = kind;
    }

    public string Message { get; }
    public NotificationKind Kind { get; }
    public bool IsInfo => Kind == NotificationKind.Info;
    public bool IsWarning => Kind == NotificationKind.Warning;
    public bool IsError => Kind == NotificationKind.Error;
}

/// <summary>
/// Avisos breves en una esquina ("Expediente guardado") que se van solos: sustituyen a las
/// ventanas de "¡Guardado exitosamente!" que obligaban a pulsar Aceptar. Los errores duran más.
/// </summary>
public sealed class NotificationService
{
    public const int MaxVisible = 3;

    private readonly SynchronizationContext? _uiContext;
    private readonly object _gate = new();

    public NotificationService(SynchronizationContext? uiContext = null)
    {
        _uiContext = uiContext ?? SynchronizationContext.Current;
    }

    public ObservableCollection<Notification> Items { get; } = new();

    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(3.5);
    public TimeSpan ErrorDuration { get; set; } = TimeSpan.FromSeconds(7);

    public void Success(string message) => Show(message, NotificationKind.Success);
    public void Info(string message) => Show(message, NotificationKind.Info);
    public void Warning(string message) => Show(message, NotificationKind.Warning);

    public Notification Show(string message, NotificationKind kind = NotificationKind.Success)
    {
        var notification = new Notification(message, kind);
        OnUi(() =>
        {
            Items.Add(notification);
            while (Items.Count > MaxVisible) Items.RemoveAt(0);
        });
        _ = DismissLaterAsync(notification, kind == NotificationKind.Error ? ErrorDuration : Duration);
        return notification;
    }

    public void Dismiss(Notification? notification)
    {
        if (notification == null) return;
        OnUi(() => Items.Remove(notification));
    }

    private async Task DismissLaterAsync(Notification notification, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        Dismiss(notification);
    }

    private void OnUi(Action action)
    {
        if (_uiContext == null)
        {
            // Sin hilo de interfaz (pruebas): agregar y quitar pueden ocurrir en hilos distintos
            lock (_gate) action();
        }
        else if (SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }
}
