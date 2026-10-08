using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// Plantillas del Excel del consultorio: posologías, medidas generales y dosis pediátrica por peso.
/// </summary>
public partial class MedicalRecordsViewModel
{
    [ObservableProperty]
    private ObservableCollection<PrescriptionTemplate> _posologyTemplates = new();

    [ObservableProperty]
    private ObservableCollection<PrescriptionTemplate> _generalMeasureTemplates = new();

    [ObservableProperty]
    private ObservableCollection<PediatricDoseRule> _pediatricDoseRules = new();

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

    public bool HasPosologyTemplates => PosologyTemplates.Count > 0;
    public bool HasGeneralMeasureTemplates => GeneralMeasureTemplates.Count > 0;
    public bool HasPediatricDoseRules => PediatricDoseRules.Count > 0;

    /// <summary>La dosis pediátrica por peso solo se ofrece con pacientes menores de edad.</summary>
    public bool ShowsPediatricDose => HasPediatricDoseRules && SelectedPatient?.IsPediatric == true;

    public async Task LoadPrescriptionTemplatesAsync()
    {
        if (_inventoryService == null) return;
        try
        {
            PosologyTemplates = new ObservableCollection<PrescriptionTemplate>(
                await _inventoryService.GetPrescriptionTemplatesAsync(PrescriptionTemplateKinds.Posology));
            GeneralMeasureTemplates = new ObservableCollection<PrescriptionTemplate>(
                await _inventoryService.GetPrescriptionTemplatesAsync(PrescriptionTemplateKinds.GeneralMeasures));
            PediatricDoseRules = new ObservableCollection<PediatricDoseRule>(
                await _inventoryService.GetPediatricDoseRulesAsync());
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron cargar las plantillas de receta", ex);
        }

        OnPropertyChanged(nameof(HasPosologyTemplates));
        OnPropertyChanged(nameof(HasGeneralMeasureTemplates));
        OnPropertyChanged(nameof(HasPediatricDoseRules));
        OnPropertyChanged(nameof(ShowsPediatricDose));
    }

    /// <summary>Elegir una posología la copia al campo de indicaciones del medicamento que se captura.</summary>
    partial void OnSelectedPosologyTemplateChanged(PrescriptionTemplate? value)
    {
        if (value == null) return;
        ApplyPosologyTemplate(value);
        ResetSelectionLater(() => SelectedPosologyTemplate = null);
    }

    partial void OnSelectedGeneralMeasureTemplateChanged(PrescriptionTemplate? value)
    {
        if (value == null) return;
        ApplyGeneralMeasureTemplate(value);
        ResetSelectionLater(() => SelectedGeneralMeasureTemplate = null);
    }

    /// <summary>
    /// El ComboBox vuelve a "sin selección" para poder aplicar la misma plantilla al siguiente
    /// medicamento; se difiere para no modificar SelectedItem dentro de su propio cambio.
    /// </summary>
    private static void ResetSelectionLater(Action reset)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.BeginInvoke(reset, System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
        }
        catch
        {
        }
        reset();
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
        if (HasActiveConsultation && TryParseNumber(ConsultationWeight, out var current) && current > 0 && current < 70)
        {
            return current.ToString("0.#", CultureInfo.InvariantCulture);
        }

        var last = SelectedPatient?.Consultations
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
