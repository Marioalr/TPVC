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
/// Plantillas del Excel del consultorio: posologías, medidas generales y dosis pediátrica por peso.
/// </summary>
public partial class MedicalRecordsViewModel
{
    [ObservableProperty]
    private PrescriptionTemplate? _selectedPosologyTemplate;

    [ObservableProperty]
    private PrescriptionTemplate? _selectedGeneralMeasureTemplate;

    [ObservableProperty]
    private PediatricDoseRule? _selectedPediatricRule;

    [ObservableProperty]
    private string _pediatricWeightText = string.Empty;

    [ObservableProperty]
    private string _pediatricDoseResult = string.Empty;

    /// <summary>La dosis pediátrica por peso solo se ofrece con pacientes menores de edad.</summary>
    public bool ShowsPediatricDose => Templates.HasPediatricDoseRules && Session.SelectedPatient?.IsPediatric == true;

    /// <summary>Elegir una posología la copia al campo de indicaciones del medicamento que se captura.</summary>
    partial void OnSelectedPosologyTemplateChanged(PrescriptionTemplate? value)
    {
        if (value == null) return;
        ApplyPosologyTemplate(value);
        _ui.ResetSelectionLater(() => SelectedPosologyTemplate = null);
    }

    partial void OnSelectedGeneralMeasureTemplateChanged(PrescriptionTemplate? value)
    {
        if (value == null) return;
        ApplyGeneralMeasureTemplate(value);
        _ui.ResetSelectionLater(() => SelectedGeneralMeasureTemplate = null);
    }

    public void ApplyPosologyTemplate(PrescriptionTemplate? template)
    {
        if (template == null) return;
        PrescriptionDosage = template.Text;
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

    partial void OnSelectedPediatricRuleChanged(PediatricDoseRule? value)
    {
        if (value != null && string.IsNullOrWhiteSpace(PediatricWeightText))
        {
            PediatricWeightText = SuggestPatientWeight();
        }
        RecalculatePediatricDose();
    }

    partial void OnPediatricWeightTextChanged(string value) => RecalculatePediatricDose();

    private string SuggestPatientWeight()
    {
        if (Active.HasActiveConsultation && TryParseNumber(ConsultationWeight, out var current) && current > 0 && current < 70)
        {
            return current.ToString("0.#", CultureInfo.InvariantCulture);
        }

        var last = Session.SelectedPatient?.Consultations
            .Where(c => c.Weight.HasValue && c.Weight > 0)
            .OrderByDescending(c => c.Date)
            .FirstOrDefault();
        return last?.Weight?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void RecalculatePediatricDose()
    {
        var rule = SelectedPediatricRule;
        if (rule == null)
        {
            PediatricDoseResult = string.Empty;
            return;
        }

        if (!rule.CanCalculate)
        {
            PediatricDoseResult = "Esta regla no tiene fórmula de ml por kg en el Excel.";
            return;
        }

        if (!TryParseNumber(PediatricWeightText, out var weight) || weight <= 0)
        {
            PediatricDoseResult = "Capture el peso del paciente (kg).";
            return;
        }

        var ml = rule.CalculateMl(weight)!.Value;
        var drops = rule.CalculateDrops(weight);
        PediatricDoseResult = drops.HasValue
            ? $"{ml.ToString("0.#", CultureInfo.InvariantCulture)} ml ({drops.Value:0} gotas) por dosis"
            : $"{ml.ToString("0.#", CultureInfo.InvariantCulture)} ml por dosis";
    }

    /// <summary>Copia la dosis calculada a las indicaciones del medicamento ("Dar 7 ml vía oral ...").</summary>
    [RelayCommand]
    public void ApplyPediatricDose()
    {
        var rule = SelectedPediatricRule;
        if (rule == null || !TryParseNumber(PediatricWeightText, out var weight)) return;

        var ml = rule.CalculateMl(weight);
        if (ml == null) return;

        var drops = rule.CalculateDrops(weight);
        var amount = drops.HasValue
            ? $"{ml.Value.ToString("0.#", CultureInfo.InvariantCulture)} ml ({drops.Value:0} gotas)"
            : $"{ml.Value.ToString("0.#", CultureInfo.InvariantCulture)} ml";
        var text = $"Dar {amount} vía oral cada ___ hrs por ___ días";

        PrescriptionDosage = text;
        if (string.IsNullOrWhiteSpace(PrescriptionSearchText) && string.IsNullOrWhiteSpace(PrescriptionMedicineName))
        {
            PrescriptionMedicineName = rule.Medication;
        }
    }

    private static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Replace(',', '.').Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
}
