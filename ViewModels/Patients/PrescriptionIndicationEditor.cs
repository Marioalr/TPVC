using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// "Cambiar indicación" de un medicamento que ya está en la receta (pedido del consultorio del 2026-10-14): el renglón se
/// vuelve un campo con las indicaciones frecuentes; Enter o Guardar guardan solo ese renglón y Esc o Cancelar lo dejan
/// como estaba. Uno para la hoja de receta y otro para la lista de la sección 5 de la consulta, así cada pantalla tiene su
/// propio campo y su propio foco. Lo que se guarda (y la guarda de solo lectura) lo decide
/// <see cref="PrescriptionSheetViewModel"/>.
/// </summary>
public partial class PrescriptionIndicationEditor : ObservableObject
{
    private readonly Func<PatientPrescriptionItem, bool> _canStart;
    private readonly Func<PatientPrescriptionItem, string, Task<bool>> _save;

    /// <param name="templates">Las indicaciones frecuentes del consultorio.</param>
    /// <param name="canStart">Si el renglón se puede corregir ahora (es de la receta abierta y la consulta no está en otro equipo).</param>
    /// <param name="save">Guarda la indicación en el renglón; false si no se pudo (el campo sigue abierto).</param>
    public PrescriptionIndicationEditor(PrescriptionTemplateStore templates, Func<PatientPrescriptionItem, bool> canStart,
        Func<PatientPrescriptionItem, string, Task<bool>> save)
    {
        _canStart = canStart;
        _save = save;
        Picker = new FrequentIndicationsPicker(templates, () => Text, text => Text = text);
    }

    /// <summary>Las indicaciones frecuentes del campo.</summary>
    public FrequentIndicationsPicker Picker { get; }

    /// <summary>El renglón que se corrige (el mismo objeto de la receta); null sin corrección abierta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private PatientPrescriptionItem? _item;

    /// <summary>La indicación que se está escribiendo.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    public bool IsEditing => Item != null;

    partial void OnTextChanged(string value) => Picker.Filter(value);

    /// <summary>El lápiz del renglón: abre el campo con la indicación que tiene.</summary>
    [RelayCommand]
    public void Start(PatientPrescriptionItem? item)
    {
        if (item == null || !_canStart(item)) return;
        Picker.Close();
        Text = item.Dosage ?? string.Empty;
        Item = item;
    }

    /// <summary>Enter o Guardar: guarda la indicación en el renglón y cierra el campo.</summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        var item = Item;
        if (item == null) return;
        Picker.Close();
        if (await _save(item, Text ?? string.Empty) && ReferenceEquals(Item, item)) Cancel();
    }

    /// <summary>Esc o Cancelar: cierra el campo sin cambiar el renglón.</summary>
    [RelayCommand]
    public void Cancel()
    {
        Picker.Close();
        Item = null;
        Text = string.Empty;
    }
}
