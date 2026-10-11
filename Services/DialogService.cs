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

    /// <summary>Elegir varios archivos (resultados de laboratorio); null si se canceló.</summary>
    public static Func<string, string, string[]?> OpenFilesPickerHandler { get; set; } = (filter, title) =>
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filter,
            Title = title,
            Multiselect = true
        };
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    };

    /// <summary>Elegir una carpeta (el respaldo externo de laboratorio); null si se canceló.</summary>
    public static Func<string, string?, string?> OpenFolderPickerHandler { get; set; } = (title, initial) =>
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    };

    public static bool Confirm(string message, string title = "Confirmar acción") => ConfirmHandler(message, title);

    public static void ShowError(string message, string title = "TPVC") => ErrorHandler(message, title);

    public static string? ShowOpenFileDialog(string filter, string title = "Seleccionar archivo") =>
        OpenFilePickerHandler(filter, title);
}
