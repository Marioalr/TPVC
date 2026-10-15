using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Indicaciones frecuentes dentro del campo "Indicación" (pedido del consultorio del 2026-10-14). Las opciones son las
/// posologías del Excel del consultorio (<see cref="PrescriptionTemplateStore.PosologyTemplates"/>), que antes se elegían
/// en un selector aparte. La flecha del campo o ↓ abren la lista, ↑ ↓ se mueven, Enter o un clic escriben la elegida en
/// el campo (se puede seguir editando) y Esc solo cierra la lista (la escalera de Esc de
/// <see cref="PatientsWorkspace.TryCloseTopmost"/>). Con la lista abierta, lo que se escribe la filtra
/// (<see cref="TextSearch"/>: sin acentos ni mayúsculas); si ya no coincide ninguna, se cierra y Enter vuelve a ser del campo.
/// Una por campo: la captura de la receta y la corrección de un renglón (<see cref="PrescriptionIndicationEditor"/>).
/// </summary>
public partial class FrequentIndicationsPicker : ObservableObject
{
    private readonly PrescriptionTemplateStore _templates;
    private readonly Func<string> _currentText;
    private readonly Action<string> _write;

    /// <param name="templates">Las posologías del consultorio.</param>
    /// <param name="currentText">Lo que tiene escrito el campo (para filtrar al abrir).</param>
    /// <param name="write">Escribe en el campo la indicación elegida.</param>
    public FrequentIndicationsPicker(PrescriptionTemplateStore templates, Func<string> currentText, Action<string> write)
    {
        _templates = templates;
        _currentText = currentText;
        _write = write;
    }

    /// <summary>La lista está abierta debajo del campo.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Las indicaciones que se ven en la lista (todas o las que coinciden con lo escrito).</summary>
    [ObservableProperty]
    private ObservableCollection<PrescriptionTemplate> _suggestions = new();

    /// <summary>La marcada (la que toma Enter); -1 sin ninguna.</summary>
    [ObservableProperty]
    private int _selectedIndex = -1;

    /// <summary>Se eligió una indicación: la vista deja el cursor al final del campo.</summary>
    public event Action? Picked;

    private List<PrescriptionTemplate> All => _templates.PosologyTemplates.Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();

    /// <summary>
    /// Abre la lista. Sin texto, o con el texto de una de ellas (recién elegida), se ven todas con esa marcada; con otro
    /// texto, las que coinciden, y si ninguna coincide, todas. false si el consultorio no tiene indicaciones frecuentes.
    /// </summary>
    public bool Open()
    {
        var all = All;
        if (all.Count == 0)
        {
            Close();
            return false;
        }

        var q = TextSearch.Normalize(_currentText());
        var exact = q.Length == 0 ? -1 : all.FindIndex(t => TextSearch.Normalize(t.Text) == q);
        var shown = q.Length == 0 || exact >= 0 ? all : Matching(all, q);
        if (shown.Count == 0) shown = all;

        Suggestions = new ObservableCollection<PrescriptionTemplate>(shown);
        SelectedIndex = exact >= 0 && ReferenceEquals(shown, all) ? exact : 0;
        IsOpen = true;
        return true;
    }

    /// <summary>La flecha del campo: abre o cierra la lista.</summary>
    [RelayCommand]
    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Close()
    {
        IsOpen = false;
    }

    /// <summary>↓ con la lista abierta (de la última vuelve a la primera).</summary>
    public void MoveNext()
    {
        if (!IsOpen || Suggestions.Count == 0) return;
        SelectedIndex = SelectedIndex < 0 || SelectedIndex >= Suggestions.Count - 1 ? 0 : SelectedIndex + 1;
    }

    /// <summary>↑ con la lista abierta (de la primera pasa a la última).</summary>
    public void MovePrevious()
    {
        if (!IsOpen || Suggestions.Count == 0) return;
        SelectedIndex = SelectedIndex <= 0 || SelectedIndex >= Suggestions.Count ? Suggestions.Count - 1 : SelectedIndex - 1;
    }

    /// <summary>
    /// Con la lista abierta, lo escrito la filtra: sin texto se ven todas; si ninguna coincide se cierra (Enter vuelve a ser
    /// del campo). El dueño del campo la llama al cambiar el texto.
    /// </summary>
    public void Filter(string? text)
    {
        if (!IsOpen) return;
        var all = All;
        var q = TextSearch.Normalize(text);
        var shown = q.Length == 0 ? all : Matching(all, q);
        if (shown.Count == 0)
        {
            Close();
            return;
        }
        Suggestions = new ObservableCollection<PrescriptionTemplate>(shown);
        SelectedIndex = 0;
    }

    /// <summary>Enter con la lista abierta: escribe la marcada en el campo y cierra la lista. false si no había ninguna.</summary>
    public bool Accept()
    {
        if (!IsOpen || SelectedIndex < 0 || SelectedIndex >= Suggestions.Count) return false;
        Pick(Suggestions[SelectedIndex]);
        return true;
    }

    /// <summary>Clic en una indicación de la lista.</summary>
    [RelayCommand]
    public void Pick(PrescriptionTemplate? template)
    {
        if (template == null) return;
        // Primero se cierra: el texto nuevo ya no filtra la lista
        Close();
        _write(template.Text);
        Picked?.Invoke();
    }

    private static List<PrescriptionTemplate> Matching(List<PrescriptionTemplate> all, string normalizedQuery) =>
        all.Where(t => TextSearch.Matches(normalizedQuery, t.Text, t.Title)).ToList();
}
