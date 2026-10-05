using System;

namespace TPVC.Services;

/// <summary>
/// Punto único para confirmaciones y avisos desde los ViewModels. La app conecta
/// MessageBox al iniciar; en pruebas (sin WPF) las confirmaciones se aceptan solas.
/// </summary>
public static class DialogService
{
    public static Func<string, string, bool> ConfirmHandler { get; set; } = (_, _) => true;

    public static Action<string, string> ErrorHandler { get; set; } = (_, _) => { };

    public static bool Confirm(string message, string title = "Confirmar acción") => ConfirmHandler(message, title);

    public static void ShowError(string message, string title = "TPVC") => ErrorHandler(message, title);
}
