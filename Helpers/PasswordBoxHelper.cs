using System.Windows;
using System.Windows.Controls;

namespace TPVC.Helpers;

/// <summary>
/// Permite el enlace bidireccional (Two-Way Data Binding) seguro entre la propiedad
/// Password de un PasswordBox y una propiedad del ViewModel en WPF MVVM.
/// </summary>
public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty UpdatingPasswordProperty =
        DependencyProperty.RegisterAttached(
            "UpdatingPassword",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    public static bool GetBindPassword(DependencyObject d) => (bool)d.GetValue(BindPasswordProperty);
    public static void SetBindPassword(DependencyObject d, bool value) => d.SetValue(BindPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if ((bool)box.GetValue(UpdatingPasswordProperty)) return;

        var newPassword = (string)e.NewValue ?? string.Empty;
        if (box.Password != newPassword)
        {
            box.Password = newPassword;
        }
    }

    private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        if ((bool)e.OldValue)
        {
            box.PasswordChanged -= HandlePasswordChanged;
        }
        if ((bool)e.NewValue)
        {
            box.PasswordChanged += HandlePasswordChanged;
        }
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;

        box.SetValue(UpdatingPasswordProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(UpdatingPasswordProperty, false);
    }
}
