using System;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Interop;
using TPVC.Services;
using TPVC.Views;

namespace TPVC.Helpers;

/// <summary>
/// Confirmaciones y errores de DialogService con la ventana de la app (Views/ConfirmDialog), que sigue
/// el tema. Se usa cuando la ventana principal ya está cargada y visible y la llamada llega por su hilo;
/// si no (error al abrir la base de datos antes de que exista, al cerrar la app, desde otro hilo) o si la
/// ventana no se pudo mostrar, se pregunta con el MessageBox de Windows como antes, con "No" por defecto.
/// </summary>
public static class ThemedDialogs
{
    /// <summary>
    /// Muestra la ventana como modal (ShowDialog). Las pruebas lo reemplazan para contestar con los
    /// botones de la ventana sin mostrarla.
    /// </summary>
    public static Action<ConfirmDialog> ShowModal { get; set; } = dialog => dialog.ShowDialog();

    /// <summary>Conecta DialogService; owner da la ventana principal en el momento de preguntar (o null).</summary>
    public static void Install(Func<Window?> owner)
    {
        DialogService.ConfirmHandler = (message, title) => Confirm(owner(), message, title);
        DialogService.ErrorHandler = (message, title) => ShowError(owner(), message, title);
    }

    /// <summary>La ventana de la app puede abrirse sobre owner: existe, está cargada y visible, y es de este hilo.</summary>
    public static bool CanShowOver([NotNullWhen(true)] Window? owner) =>
        owner != null && owner.Dispatcher.CheckAccess() && owner.IsLoaded && owner.IsVisible;

    /// <summary>true solo si se eligió "Sí".</summary>
    public static bool Confirm(Window? owner, string message, string title)
    {
        if (CanShowOver(owner))
        {
            try
            {
                return Present(owner, message, title, ConfirmDialogKind.Confirm);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo mostrar la confirmación de la app; se pregunta con la de Windows", ex);
            }
        }
        return ShowMessageBox(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public static void ShowError(Window? owner, string message, string title)
    {
        if (CanShowOver(owner))
        {
            try
            {
                Present(owner, message, title, ConfirmDialogKind.Error);
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo mostrar el error con la ventana de la app; se muestra con la de Windows", ex);
            }
        }
        ShowMessageBox(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK);
    }

    private static bool Present(Window owner, string message, string title, ConfirmDialogKind kind)
    {
        var dialog = new ConfirmDialog(message, title, kind);
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            dialog.Owner = owner;
            ShowModal(dialog);
            return dialog.Confirmed;
        }
        finally
        {
            // Una ventana creada y sin cerrar evita que la app termine al cerrar la principal
            if (!closed)
            {
                try { dialog.Close(); } catch (InvalidOperationException) { }
            }
        }
    }

    private static MessageBoxResult ShowMessageBox(Window? owner, string message, string title,
        MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        // Sobre la ventana principal solo si ya existe en Windows, se ve y es de este hilo
        bool canOwn = owner != null && owner.Dispatcher.CheckAccess() && owner.IsVisible
                      && new WindowInteropHelper(owner).Handle != IntPtr.Zero;
        return canOwn
            ? MessageBox.Show(owner!, message, title, buttons, image, defaultResult)
            : MessageBox.Show(message, title, buttons, image, defaultResult);
    }
}
