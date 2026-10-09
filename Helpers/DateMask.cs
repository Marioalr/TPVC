using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TPVC.Helpers;

/// <summary>
/// Fecha dd/mm/aaaa con las diagonales solas (fase 7, 7.1). Al escribir 08082008 se ve 08/08/2008:
/// la diagonal aparece al completar el día y el mes, y al borrar desde el final se va sola junto con
/// el dígito anterior. Solo cambia lo que se ve en el cuadro: el texto llega al ViewModel por su
/// binding, que lo valida como siempre. Lo que pone el ViewModel (un paciente, un borrador) no se toca.
/// Uso: &lt;TextBox helpers:DateMask.IsEnabled="True" ... /&gt;
/// Aquí solo se atienden las teclas, el pegado y el cursor; las reglas viven en DateMaskText (sin WPF,
/// se prueban en Linux).
/// </summary>
public static class DateMask
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DateMask), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        DataObject.RemovePastingHandler(box, OnPasting);

        if ((bool)e.NewValue)
        {
            box.PreviewTextInput += OnPreviewTextInput;
            box.PreviewKeyDown += OnPreviewKeyDown;
            DataObject.AddPastingHandler(box, OnPasting);
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox { IsReadOnly: false } box || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        e.Handled = true;
        Apply(box, DateMaskText.Insert(box.Text, box.SelectionStart, box.SelectionLength, e.Text));
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { IsReadOnly: false } box) return;
        // Ctrl+Retroceso (borrar palabra), Mayús+Supr (cortar) y demás atajos siguen como siempre
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;

        switch (e.Key)
        {
            case Key.Back:
                e.Handled = true;
                Apply(box, DateMaskText.Backspace(box.Text, box.SelectionStart, box.SelectionLength));
                break;
            case Key.Delete when modifiers == ModifierKeys.None:
                e.Handled = true;
                Apply(box, DateMaskText.Delete(box.Text, box.SelectionStart, box.SelectionLength));
                break;
            case Key.Space:
                // El espacio no llega a PreviewTextInput en un TextBox: aquí se descarta
                e.Handled = true;
                break;
        }
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (sender is not TextBox { IsReadOnly: false } box || e.IsDragDrop) return;
        var pasted = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string
                     ?? e.SourceDataObject.GetData(DataFormats.Text) as string;
        Apply(box, DateMaskText.Paste(box.Text, box.SelectionStart, box.SelectionLength, pasted));
    }

    private static void Apply(TextBox box, DateMaskText.Edit? edit)
    {
        if (edit is not { } result) return;
        if (!string.Equals(box.Text, result.Text, StringComparison.Ordinal)) box.Text = result.Text;
        box.CaretIndex = result.Caret;
    }
}
