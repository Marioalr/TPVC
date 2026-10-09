using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Plantillas del Excel del consultorio: medidas generales. La posología y la dosis pediátrica por peso
/// son de la hoja de receta (Prescription).
/// </summary>
public partial class MedicalRecordsViewModel
{
    [ObservableProperty]
    private PrescriptionTemplate? _selectedGeneralMeasureTemplate;

    partial void OnSelectedGeneralMeasureTemplateChanged(PrescriptionTemplate? value)
    {
        if (value == null) return;
        ApplyGeneralMeasureTemplate(value);
        _ui.ResetSelectionLater(() => SelectedGeneralMeasureTemplate = null);
    }

    public void ApplyGeneralMeasureTemplate(PrescriptionTemplate? template)
    {
        if (template == null) return;

        // Indicaciones generales de la receta impresa + plan visible en la nota de consulta
        ConsultationGeneralIndications = template.Text;
        ConsultationTreatmentPlan = string.IsNullOrWhiteSpace(ConsultationTreatmentPlan)
            ? template.Text
            : ConsultationTreatmentPlan.TrimEnd() + Environment.NewLine + template.Text;
    }
}
