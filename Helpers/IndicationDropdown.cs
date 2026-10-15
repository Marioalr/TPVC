using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TPVC.Models;
using TPVC.ViewModels.Patients;

namespace TPVC.Helpers;

/// <summary>
/// Teclado y foco del campo "Indicación" con sus indicaciones frecuentes (pedido del consultorio del 2026-10-14), igual en
/// la captura de la receta y en "Cambiar indicación" (la hoja y la sección 5 de la consulta). Qué hay en la lista y qué
/// se escribe lo decide <see cref="FrequentIndicationsPicker"/>; aquí solo las teclas y el foco:
/// <list type="bullet">
/// <item>↓ abre la lista (también la flecha del campo); abierta, ↑ ↓ se mueven.</item>
/// <item>Enter con la lista abierta escribe la marcada en el campo (se puede seguir editando); con la lista cerrada ejecuta
/// <c>EnterCommand</c> (agregar el medicamento o guardar el renglón).</item>
/// <item>Esc la cierra desde la escalera de Esc de Pacientes (<c>PatientsWorkspace.TryCloseTopmost</c>), antes que la hoja.</item>
/// <item>Tab o salir del campo la cierran.</item>
/// </list>
/// Uso: en el TextBox <c>helpers:IndicationDropdown.Picker</c> y <c>EnterCommand</c> (y <c>FocusWhenShown</c> en el campo de
/// un renglón); en la lista de la plantilla <c>helpers:IndicationDropdown.IsDropdownList="True"</c> (clic elige y la marcada
/// se ve).
/// </summary>
public static class IndicationDropdown
{
    private sealed class Subscription
    {
        public FrequentIndicationsPicker? Picker;
        public PropertyChangedEventHandler? OnPropertyChanged;
        public System.Action? OnPicked;
    }

    private static readonly ConditionalWeakTable<TextBox, Subscription> Subscriptions = new();

    public static readonly DependencyProperty PickerProperty = DependencyProperty.RegisterAttached(
        "Picker", typeof(FrequentIndicationsPicker), typeof(IndicationDropdown), new PropertyMetadata(null, OnPickerChanged));

    public static FrequentIndicationsPicker? GetPicker(DependencyObject obj) => (FrequentIndicationsPicker?)obj.GetValue(PickerProperty);

    public static void SetPicker(DependencyObject obj, FrequentIndicationsPicker? value) => obj.SetValue(PickerProperty, value);

    /// <summary>Lo que hace Enter con la lista cerrada.</summary>
    public static readonly DependencyProperty EnterCommandProperty = DependencyProperty.RegisterAttached(
        "EnterCommand", typeof(ICommand), typeof(IndicationDropdown), new PropertyMetadata(null));

    public static ICommand? GetEnterCommand(DependencyObject obj) => (ICommand?)obj.GetValue(EnterCommandProperty);

    public static void SetEnterCommand(DependencyObject obj, ICommand? value) => obj.SetValue(EnterCommandProperty, value);

    /// <summary>Al mostrarse (el renglón entra a "Cambiar indicación") el campo toma el foco con su texto seleccionado.</summary>
    public static readonly DependencyProperty FocusWhenShownProperty = DependencyProperty.RegisterAttached(
        "FocusWhenShown", typeof(bool), typeof(IndicationDropdown), new PropertyMetadata(false, OnFocusWhenShownChanged));

    public static bool GetFocusWhenShown(DependencyObject obj) => (bool)obj.GetValue(FocusWhenShownProperty);

    public static void SetFocusWhenShown(DependencyObject obj, bool value) => obj.SetValue(FocusWhenShownProperty, value);

    /// <summary>La lista de la plantilla: clic en una indicación la elige y la marcada siempre se ve.</summary>
    public static readonly DependencyProperty IsDropdownListProperty = DependencyProperty.RegisterAttached(
        "IsDropdownList", typeof(bool), typeof(IndicationDropdown), new PropertyMetadata(false, OnIsDropdownListChanged));

    public static bool GetIsDropdownList(DependencyObject obj) => (bool)obj.GetValue(IsDropdownListProperty);

    public static void SetIsDropdownList(DependencyObject obj, bool value) => obj.SetValue(IsDropdownListProperty, value);

    // ------------------------------------------------------------------ el campo

    private static void OnPickerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.LostKeyboardFocus -= OnLostKeyboardFocus;
        box.Loaded -= OnLoaded;
        box.Unloaded -= OnUnloaded;
        Detach(box);

        if (e.NewValue is not FrequentIndicationsPicker) return;
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.LostKeyboardFocus += OnLostKeyboardFocus;
        box.Loaded += OnLoaded;
        box.Unloaded += OnUnloaded;
        if (box.IsLoaded) Attach(box);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((TextBox)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Detach((TextBox)sender);

    /// <summary>Los avisos de la lista solo mientras el campo está en pantalla (los renglones se crean y se desechan).</summary>
    private static void Attach(TextBox box)
    {
        Detach(box);
        if (GetPicker(box) is not { } picker) return;
        var subscription = new Subscription { Picker = picker };
        subscription.OnPropertyChanged = (_, args) =>
        {
            // La flecha del campo abre la lista: el cursor va al campo para seguir con ↑ ↓ y Enter
            if (args.PropertyName == nameof(FrequentIndicationsPicker.IsOpen) && picker.IsOpen && box.IsVisible && !box.IsKeyboardFocusWithin)
            {
                FocusAtEnd(box, selectAll: false);
            }
        };
        subscription.OnPicked = () =>
        {
            if (box.IsVisible) FocusAtEnd(box, selectAll: false);
        };
        picker.PropertyChanged += subscription.OnPropertyChanged;
        picker.Picked += subscription.OnPicked;
        Subscriptions.AddOrUpdate(box, subscription);
    }

    private static void Detach(TextBox box)
    {
        if (!Subscriptions.TryGetValue(box, out var subscription)) return;
        if (subscription.Picker != null)
        {
            subscription.Picker.PropertyChanged -= subscription.OnPropertyChanged;
            subscription.Picker.Picked -= subscription.OnPicked;
        }
        Subscriptions.Remove(box);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || GetPicker(box) is not { } picker) return;
        var modifiers = Keyboard.Modifiers & ~ModifierKeys.Shift;
        if (modifiers != ModifierKeys.None && !(modifiers == ModifierKeys.Alt && e.SystemKey == Key.Down)) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Down:
                if (picker.IsOpen) picker.MoveNext();
                else if (!picker.Open()) return;
                e.Handled = true;
                break;
            case Key.Up when picker.IsOpen:
                picker.MovePrevious();
                e.Handled = true;
                break;
            case Key.Enter:
                if (picker.IsOpen && picker.Accept())
                {
                    e.Handled = true;
                    break;
                }
                if (GetEnterCommand(box) is { } command)
                {
                    if (command.CanExecute(null)) command.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.Tab when picker.IsOpen:
                picker.Close();
                break;
        }
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && GetPicker(box) is { IsOpen: true } picker) picker.Close();
    }

    private static void OnFocusWhenShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.IsVisibleChanged -= OnIsVisibleChanged;
        if ((bool)e.NewValue) box.IsVisibleChanged += OnIsVisibleChanged;
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Solo cuando se abre la corrección en un renglón que ya estaba en pantalla. Un renglón que se vuelve a crear
        // (al agregar o quitar otro medicamento, o al repetir la receta) se muestra antes de su Loaded: ese no se lleva
        // el cursor, que vuelve al buscador; si se lo llevara, lo que se teclea pisaría la corrección pendiente
        if (sender is TextBox { IsLoaded: true } box && (bool)e.NewValue) FocusAtEnd(box, selectAll: true);
    }

    private static void FocusAtEnd(TextBox box, bool selectAll)
    {
        box.Dispatcher.InvokeAsync(() =>
        {
            if (!box.IsVisible) return;
            box.Focus();
            if (selectAll) box.SelectAll();
            else box.CaretIndex = box.Text.Length;
        }, DispatcherPriority.Input);
    }

    // ------------------------------------------------------------------ la lista

    private static void OnIsDropdownListChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;
        list.PreviewMouseLeftButtonUp -= OnListMouseUp;
        list.SelectionChanged -= OnListSelectionChanged;
        if (!(bool)e.NewValue) return;
        list.PreviewMouseLeftButtonUp += OnListMouseUp;
        list.SelectionChanged += OnListSelectionChanged;
    }

    private static void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && !ReferenceEquals(dep, sender))
        {
            dep = VisualTreeHelper.GetParent(dep);
        }
        if (dep is ListBoxItem { DataContext: PrescriptionTemplate template } && sender is ListBox { DataContext: FrequentIndicationsPicker picker })
        {
            picker.Pick(template);
            e.Handled = true;
        }
    }

    private static void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } list) list.ScrollIntoView(item);
    }
}
