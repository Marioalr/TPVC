using System.Windows;

namespace TPVC.Helpers;

/// <summary>
/// Propiedades adjuntas de los campos del lenguaje visual común.
/// Placeholder: texto de ejemplo dentro de un cuadro vacío (lo dibuja la plantilla FieldTextBox).
/// </summary>
public static class Field
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Field), new FrameworkPropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject obj) => (string)obj.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject obj, string value) => obj.SetValue(PlaceholderProperty, value);
}
