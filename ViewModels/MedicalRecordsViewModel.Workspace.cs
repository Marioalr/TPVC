using System;
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
/// Pantalla de consulta en curso: secciones en el orden de la entrevista y una "cuenta de la consulta"
/// que se llena sola (receta que se surte aquí, insumos y servicios). Todo se cobra al terminar,
/// sin salir al Punto de Venta.
/// </summary>
public partial class MedicalRecordsViewModel
{
    public const int ConsultationSectionCount = 5;

    /// <summary>1 Motivo · 2 Signos vitales · 3 Exploración · 4 Diagnóstico · 5 Tratamiento.</summary>
    [ObservableProperty]
    private int _consultationSection = 1;

    [ObservableProperty]
    private string _consultationSpO2 = string.Empty;

    [RelayCommand]
    public void SetConsultationSection(string? section)
    {
        if (int.TryParse(section, out var n)) ConsultationSection = Math.Clamp(n, 1, ConsultationSectionCount);
    }

    [RelayCommand]
    public void NextConsultationSection() => ConsultationSection = Math.Min(ConsultationSectionCount, ConsultationSection + 1);

    [RelayCommand]
    public void PreviousConsultationSection() => ConsultationSection = Math.Max(1, ConsultationSection - 1);

    [RelayCommand]
    public void AppendChiefComplaintChip(string? text) => ConsultationChiefComplaint = AppendSentence(ConsultationChiefComplaint, text);

    [RelayCommand]
    public void AppendSystemsReviewLine(string? text) => ConsultationSystemsReview = AppendLine(ConsultationSystemsReview, text);

    [RelayCommand]
    public void AppendPhysicalExamLine(string? text) => ConsultationPhysicalExam = AppendLine(ConsultationPhysicalExam, text);

    private static string AppendSentence(string current, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return current;
        return string.IsNullOrWhiteSpace(current) ? text.Trim() : $"{current.TrimEnd().TrimEnd('.')}, {text.Trim().ToLowerInvariant()}";
    }

    private static string AppendLine(string current, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return current;
        return string.IsNullOrWhiteSpace(current) ? text.Trim() : current.TrimEnd() + Environment.NewLine + text.Trim();
    }

    // =========================================================================
    // ALERTAS DE SIGNOS VITALES (ayuda visual mientras se capturan)
    // =========================================================================

    partial void OnConsultationTempChanged(string value) => NotifyVitalSignsWarningChanged();
    partial void OnConsultationFCChanged(string value) => NotifyVitalSignsWarningChanged();
    partial void OnConsultationSpO2Changed(string value) => NotifyVitalSignsWarningChanged();
    partial void OnConsultationGlucoseChanged(string value) => NotifyVitalSignsWarningChanged();

    /// <summary>
    /// Todo lo que lee VitalSignsWarning avisa aquí: Temp, FC, SpO₂ y glucosa (arriba), la T/A
    /// (OnConsultationTAChanged, que además le da formato) y el paciente, por su edad (OnSelectedPatientChanged).
    /// </summary>
    private void NotifyVitalSignsWarningChanged()
    {
        OnPropertyChanged(nameof(VitalSignsWarning));
        OnPropertyChanged(nameof(HasVitalSignsWarning));
    }

    public bool HasVitalSignsWarning => !string.IsNullOrEmpty(VitalSignsWarning);

    /// <summary>Valores fuera de rango para adulto/niño en una sola línea (vacío si todo es normal).</summary>
    public string VitalSignsWarning
    {
        get
        {
            var warnings = new System.Collections.Generic.List<string>();
            if (TryParse(ConsultationTemp, out var temp))
            {
                if (temp >= 38) warnings.Add($"Temp {temp:0.#} °C: fiebre");
                else if (temp < 35.5 && temp > 0) warnings.Add($"Temp {temp:0.#} °C: hipotermia");
            }

            var ta = (ConsultationTA ?? string.Empty).Split('/');
            if (ta.Length == 2 && TryParse(ta[0], out var sys) && TryParse(ta[1], out var dia))
            {
                if (sys >= 140 || dia >= 90) warnings.Add($"T/A {sys:0}/{dia:0}: elevada");
                else if (sys > 0 && sys < 90) warnings.Add($"T/A {sys:0}/{dia:0}: baja");
            }

            if (TryParse(ConsultationSpO2, out var sat) && sat > 0)
            {
                if (sat <= 1) sat *= 100;
                if (sat < 92) warnings.Add($"SpO₂ {sat:0} %: baja");
            }

            if (TryParse(ConsultationGlucose, out var glu) && glu > 0)
            {
                if (glu >= 200) warnings.Add($"Glucosa {glu:0} mg/dL: elevada");
                else if (glu < 70) warnings.Add($"Glucosa {glu:0} mg/dL: hipoglucemia");
            }

            if (TryParse(ConsultationFC, out var fc) && fc > 0)
            {
                bool child = Session.SelectedPatient is { Age: < 12 };
                if (!child && fc > 100) warnings.Add($"FC {fc:0}: taquicardia");
                else if (child && fc > 140) warnings.Add($"FC {fc:0}: alta para la edad");
                else if (fc < 50) warnings.Add($"FC {fc:0}: bradicardia");
            }

            return string.Join(" · ", warnings);
        }
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Replace(',', '.').Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    // =========================================================================
    // CUENTA DE LA CONSULTA (el honorario y los cargos viven en Account)
    // =========================================================================

    /// <summary>Última consulta anterior (sin contar el avance guardado de la consulta en curso).</summary>
    public PatientConsultation? PreviousConsultation =>
        Session.ConsultationsHistory.FirstOrDefault(c => !string.Equals(c.Folio, Active.ActiveConsultationFolio, StringComparison.Ordinal));

    private void NotifyAccountChanged()
    {
        OnPropertyChanged(nameof(PreviousConsultation));
        Account.RaiseAccountChanged();
        Active.RaiseTotalAmount();
    }

    /// <summary>
    /// Arma el cobro de la consulta: honorario + cargos acumulados. Se reconstruye cada vez para
    /// que volver a la consulta y terminar de nuevo no duplique cantidades.
    /// </summary>
    private void BuildCheckoutCart()
    {
        Checkout.Reset();
        Checkout.PatientName = Session.SelectedPatient?.FullName ?? Active.ActiveConsultationPatientName ?? CheckoutViewModel.DefaultClient;
        var fee = Account.ConsultationFeeService;
        Checkout.Cart.Add(new CartItem
        {
            ProductId = fee != null ? -fee.Id : -1,
            Code = fee?.Code ?? "SERV-001",
            Name = Account.ConsultationFeeName,
            GenericName = $"Servicio Clínico • {fee?.Category ?? "Consulta"}",
            Presentation = "Honorario",
            Unit = "Servicio",
            AvailableStock = 999,
            UnitPrice = Account.ConsultationFeeAmount,
            Quantity = 1,
            IsService = true
        });

        foreach (var item in _consultationService?.CurrentConsultation?.Items ?? Enumerable.Empty<ConsultationProductItem>())
        {
            var product = item.ProductId > 0 ? Catalog.AvailableProducts.FirstOrDefault(p => p.Id == item.ProductId) : null;
            Checkout.Cart.Add(new CartItem
            {
                ProductId = item.ProductId,
                Code = item.Code,
                Name = item.Name,
                GenericName = item.GenericName,
                Presentation = item.Presentation,
                Unit = item.Unit,
                AvailableStock = product?.CurrentStock ?? 999,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                RequiresRefrigeration = product?.RequiresRefrigeration ?? false,
                IsService = item.ProductId <= 0
            });
        }

        Checkout.SelectedItem = null;
    }

    /// <summary>
    /// Descarta la consulta en curso (paciente equivocado, se retiró...): no guarda la nota ni cobra.
    /// Si ya se había guardado un avance, se elimina del expediente.
    /// </summary>
    [RelayCommand]
    public async Task DiscardActiveConsultationAsync()
    {
        if (!Active.HasActiveConsultation || BlockIfConsultationReadOnly()) return;

        if (!await _dialogs.ConfirmAsync(
                "¿Descartar la consulta en curso?\n\nNo se guardará la nota médica ni se cobrará nada de la cuenta.",
                "Descartar consulta"))
        {
            return;
        }

        var folio = _consultationService?.CurrentConsultation?.Folio;
        if (_inventoryService != null && Session.SelectedPatient != null && !string.IsNullOrWhiteSpace(folio))
        {
            try
            {
                await _inventoryService.DeleteConsultationNoteAsync(Session.SelectedPatient.Id, folio);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo eliminar el avance de la consulta descartada", ex);
            }
        }

        // El avance que se hubiera guardado deja de verse en la ficha (historial, signos, resumen)
        var patient = Session.SelectedPatient;
        if (patient != null && !string.IsNullOrWhiteSpace(folio))
        {
            foreach (var note in patient.Consultations.Where(c => c.Folio == folio).ToList()) patient.Consultations.Remove(note);
            foreach (var note in Session.ConsultationsHistory.Where(c => c.Folio == folio).ToList()) Session.ConsultationsHistory.Remove(note);
            RefreshVitalSignsEvolution(patient);
        }

        await Record.EndConsultationOnRecordAsync(discarded: true);
        _consultationService?.CloseActiveConsultation();
        _isCurrentConsultationSaved = false;
        _attendingAppointmentId = null;
        SyncActiveConsultation();
        Active.StopConsultationTimer();
        ClearConsultationFields();
        Navigation.PatientsPage = Session.SelectedPatient != null ? PageChart : PageHome;
    }

    private void ClearConsultationFields()
    {
        ConsultationChiefComplaint = string.Empty;
        ConsultationSystemsReview = string.Empty;
        ConsultationPhysicalExam = string.Empty;
        ConsultationDiagnosis = string.Empty;
        ConsultationTreatmentPlan = string.Empty;
        ConsultationNotes = string.Empty;
        ConsultationGeneralIndications = string.Empty;
        ConsultationGlucose = string.Empty;
        ConsultationTA = string.Empty;
        ConsultationFC = string.Empty;
        ConsultationFR = string.Empty;
        ConsultationTemp = string.Empty;
        ConsultationWeight = string.Empty;
        ConsultationHeight = string.Empty;
        ConsultationSpO2 = string.Empty;
        ConsultationIMC = "Valores no disponibles";
        ConsultationSection = 1;
    }
}
