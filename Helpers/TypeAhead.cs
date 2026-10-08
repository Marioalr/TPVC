using System;
using System.Collections;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TextSearch = TPVC.Services.TextSearch;

namespace TPVC.Helpers;

/// <summary>
/// Lista desplegable que busca al escribir, sin cuadro de búsqueda: al teclear se abre (si estaba
/// cerrada) y el cursor salta a la mejor coincidencia. Ignora acentos y mayúsculas; primero las
/// opciones que empiezan con el texto, luego las que tienen una palabra que empieza así y al final
/// las que lo contienen. ↑ ↓ mueven, Enter elige, Esc cierra, Retroceso borra una letra.
/// Uso: &lt;ComboBox helpers:TypeAhead.IsEnabled="True" ... /&gt;
/// </summary>
public static class TypeAhead
{
    private const int ResetAfterMs = 1500;

    private sealed class State
    {
        public string Buffer = string.Empty;
        public DateTime LastKey = DateTime.MinValue;
        public bool SpaceAlreadyAdded;
    }

    private static readonly ConditionalWeakTable<ComboBox, State> States = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TypeAhead), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;

        combo.PreviewTextInput -= OnPreviewTextInput;
        combo.PreviewKeyDown -= OnPreviewKeyDown;
        combo.DropDownClosed -= OnDropDownClosed;

        if ((bool)e.NewValue)
        {
            // La búsqueda integrada de WPF solo compara el inicio, distingue acentos y cambia la
            // selección con la lista cerrada; se sustituye por este comportamiento
            combo.IsTextSearchEnabled = false;
            combo.PreviewTextInput += OnPreviewTextInput;
            combo.PreviewKeyDown += OnPreviewKeyDown;
            combo.DropDownClosed += OnDropDownClosed;
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsEditable || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;

        var state = States.GetOrCreateValue(combo);
        if (e.Text == " " && state.SpaceAlreadyAdded)
        {
            // El espacio ya se agregó en KeyDown (que además evita que abra/cierre la lista)
            state.SpaceAlreadyAdded = false;
            e.Handled = true;
            return;
        }
        var now = DateTime.UtcNow;
        if ((now - state.LastKey).TotalMilliseconds > ResetAfterMs) state.Buffer = string.Empty;
        state.LastKey = now;
        state.Buffer += e.Text;
        e.Handled = true;

        Search(combo, state);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var state = States.GetOrCreateValue(combo);

        switch (e.Key)
        {
            case Key.Back when state.Buffer.Length > 0:
                state.Buffer = state.Buffer[..^1];
                state.LastKey = DateTime.UtcNow;
                Search(combo, state);
                e.Handled = true;
                break;
            case Key.Space when state.Buffer.Length > 0:
                // El espacio forma parte de lo buscado ("cada 12"), no abre/cierra la lista
                state.Buffer += " ";
                state.SpaceAlreadyAdded = true;
                state.LastKey = DateTime.UtcNow;
                Search(combo, state);
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Up:
            case Key.Down:
                Reset(combo, state);
                break;
        }
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo) Reset(combo, States.GetOrCreateValue(combo));
    }

    private static void Reset(ComboBox combo, State state)
    {
        state.Buffer = string.Empty;
    }

    private static void Search(ComboBox combo, State state)
    {
        if (!combo.IsDropDownOpen) combo.IsDropDownOpen = true;

        int index = FindBestMatch(combo, state.Buffer);
        if (index < 0) return;

        // Con la lista abierta, "seleccionar" sin confirmar = dar foco al elemento (Enter lo elige)
        combo.Dispatcher.BeginInvoke(() =>
        {
            if (combo.ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem item)
            {
                item.BringIntoView();
                item.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    public static int FindBestMatch(ComboBox combo, string buffer) =>
        FindBestMatch(combo.Items, combo.DisplayMemberPath, buffer);

    /// <summary>Índice de la mejor coincidencia (inicio → palabra → contiene), -1 si no hay.</summary>
    public static int FindBestMatch(IEnumerable items, string? displayMemberPath, string buffer) =>
        TextSearch.FindBestMatchIndex(items.Cast<object?>().Select(i => DisplayText(i, displayMemberPath)), buffer);

    private static string DisplayText(object? item, string? displayMemberPath)
    {
        if (item == null) return string.Empty;
        if (!string.IsNullOrEmpty(displayMemberPath))
        {
            var value = item.GetType().GetProperty(displayMemberPath)?.GetValue(item);
            if (value != null) return value.ToString() ?? string.Empty;
        }
        return item.ToString() ?? string.Empty;
    }
}
