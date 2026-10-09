using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace TPVC.Views;

/// <summary>Qué muestra la ventana: una confirmación (Sí / No) o un error (Aceptar).</summary>
public enum ConfirmDialogKind
{
    Confirm,
    Error
}

/// <summary>
/// Confirmaciones y errores de DialogService con el aspecto de la app: sigue el tema (claro, oscuro,
/// acento y personalizado), a diferencia del MessageBox de Windows, que se queda blanco en modo oscuro.
/// Igual que el MessageBox con "No" por defecto, lo seguro es lo predeterminado: "No" (o "Aceptar" en un
/// error) tiene el foco al abrir y lo tocan Enter (IsDefault) y Esc (IsCancel). "Sí" se elige a propósito:
/// con clic, o con Tab y luego Enter o Espacio. Cerrar la ventana (Alt+F4) también es "No".
/// Se muestra con ShowDialog (Helpers/ThemedDialogs) y la respuesta queda en <see cref="Confirmed"/>.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string message, string title, ConfirmDialogKind kind)
    {
        try
        {
            InitializeComponent();
        }
        catch
        {
            // Una ventana creada y sin cerrar evita que la app termine al cerrar la principal
            try { Close(); } catch (InvalidOperationException) { }
            throw;
        }

        Kind = kind;
        var heading = string.IsNullOrWhiteSpace(title) ? "TPVC" : title;
        Title = heading;
        TitleText.Text = heading;
        MessageText.Text = message ?? string.Empty;

        if (kind == ConfirmDialogKind.Error)
        {
            WarningBadge.Visibility = Visibility.Collapsed;
            ErrorBadge.Visibility = Visibility.Visible;
            ConfirmButton.Visibility = Visibility.Collapsed;
            // Un solo botón, el principal; sigue siendo el de Enter y Esc
            SafeButton.Content = "Aceptar";
            SafeButton.SetResourceReference(StyleProperty, "ButtonPrimary");
        }

        // Foco inicial en lo seguro: Enter con el foco ahí contesta "No", como el MessageBox
        FocusManager.SetFocusedElement(this, SafeButton);
        ContentRendered += (_, _) => SafeButton.Focus();

        // Ctrl+C copia el título y el mensaje, como en el MessageBox (útil para reportar un error)
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopyText()));
    }

    public ConfirmDialogKind Kind { get; }

    /// <summary>true solo si se eligió "Sí". Esc, Enter con el foco inicial, "No" o cerrar la ventana: false.</summary>
    public bool Confirmed { get; private set; }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    /// <summary>Sin barra de título: la ventana se mueve arrastrando la tarjeta.</summary>
    private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // El botón se soltó antes de empezar a arrastrar
        }
    }

    private void CopyText()
    {
        try
        {
            Clipboard.SetText($"{TitleText.Text}{Environment.NewLine}{Environment.NewLine}{MessageText.Text}");
        }
        catch (ExternalException)
        {
            // Otro programa tiene abierto el portapapeles: no se copia, sin más
        }
    }
}
