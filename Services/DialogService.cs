using System;

namespace TPVC.Services;

/// <summary>
/// Punto único para confirmaciones y avisos desde los ViewModels. La app conecta al iniciar su
/// ventana de diálogo, que sigue el tema (Helpers/ThemedDialogs, con el MessageBox de Windows de
/// respaldo); en pruebas (sin WPF) las confirmaciones se aceptan solas.
/// </summary>
public static class DialogService
{
    public static Func<string, string, bool> ConfirmHandler { get; set; } = (_, _) => true;

    public static Action<string, string> ErrorHandler { get; set; } = (_, _) => { };

    public static Func<string, string, string?> OpenFilePickerHandler { get; set; } = (filter, title) =>
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filter,
            Title = title
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    };

    public static bool Confirm(string message, string title = "Confirmar acción") => ConfirmHandler(message, title);

    public static void ShowError(string message, string title = "TPVC") => ErrorHandler(message, title);

    public static string? ShowOpenFileDialog(string filter, string title = "Seleccionar archivo") =>
        OpenFilePickerHandler(filter, title);
}
