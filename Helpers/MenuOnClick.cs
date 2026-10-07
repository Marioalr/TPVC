using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TPVC.Helpers;

/// <summary>
/// Abre el ContextMenu de un botón con clic normal (botón "⋯"), debajo del botón. El menú recibe
/// el DataContext del botón; Tag del botón puede llevar el ViewModel para los comandos.
/// Uso: &lt;Button helpers:MenuOnClick.IsEnabled="True"&gt;&lt;Button.ContextMenu&gt;…
/// </summary>
public static class MenuOnClick
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MenuOnClick), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OpenMenu;
        if ((bool)e.NewValue) button.Click += OpenMenu;
    }

    private static void OpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu) return;
        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = element.DataContext;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
