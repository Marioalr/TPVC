using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Consulta en curso (fase 6, paso 18): la nota médica (NOM-004-SSA3) por secciones, los signos vitales
/// con su alerta, el CIE-10, las medidas generales, y "Terminar consulta" con su cobro (Checkout). Lo que
/// cruza a otros dueños (sincronizar la consulta, la cuenta, recargar el catálogo, cerrar tras cobrar) se
/// lo pide al coordinador, en el mismo punto de siempre.
/// </summary>
public partial class ConsultationViewModel : ObservableObject
{
    private readonly InventoryService? _inventoryService;
    private readonly ConsultationService? _consultationService;
    private readonly ClinicProfileService? _clinicProfileService;
    private readonly PatientSession _session;
    private readonly ActiveConsultationSession _active;
    private readonly ConsultationRecordState _record;
    private readonly ConsultationAccount _account;
    private readonly CatalogStore _catalog;
    private readonly PatientsNavigation _nav;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly IConsultationGate _gate;
    private readonly IUserDialogs _dialogs;
    private readonly IUiThread _ui;
    private readonly IPatientsWorkflow _workflow;

    public ConsultationViewModel(
        InventoryService? inventoryService,
        ConsultationService? consultationService,
        ClinicProfileService? clinicProfileService,
        PatientSession session,
        ActiveConsultationSession active,
        ConsultationRecordState record,
        ConsultationAccount account,
        CatalogStore catalog,
        PatientsNavigation navigation,
        InfoDialogState info,
        PatientsFeedback feedback,
        IConsultationGate gate,
        IUserDialogs dialogs,
        IUiThread ui,
        IPatientsWorkflow workflow)
    {
        _inventoryService = inventoryService;
        _consultationService = consultationService;
        _clinicProfileService = clinicProfileService;
        _session = session;
        _active = active;
        _record = record;
        _account = account;
        _catalog = catalog;
        _nav = navigation;
        _info = info;
        _feedback = feedback;
        _gate = gate;
        _dialogs = dialogs;
        _ui = ui;
        _workflow = workflow;

        Checkout = new CheckoutViewModel(inventoryService, () =>
            _clinicProfileService?.CurrentProfile?.DoctorNameWithTitle is { Length: > 0 } doctor ? doctor : "Dr. Titular")
        {
            // Quitar todos los cargos termina una consulta de cortesía sin registrar una venta en ceros
            EmptyChargeText = "Terminar consulta (sin cargos)",
            ChargeSuffix = " y terminar",
            Notify = (message, ok) => { if (!ok) _feedback.ShowWarning(message); },
            Completed = _workflow.OnConsultationChargedAsync,
            Failed = () => _ = _workflow.LoadServicesAsync()
        };
    }

    public static string FormatTA(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var text = input.Trim().Replace('-', '/').Replace(' ', '/');

        if (text.Contains('/'))
        {
            var parts = text.Split(new[] { '/' }, 2);
            var leftDigits = new string(parts[0].Where(char.IsDigit).Take(3).ToArray());
            var rightDigits = parts.Length > 1 
                ? new string(parts[1].Where(char.IsDigit).Take(3).ToArray()) 
                : string.Empty;

            if (string.IsNullOrEmpty(leftDigits))
            {
                return rightDigits;
            }

            if (string.IsNullOrEmpty(rightDigits))
            {
                return $"{leftDigits}/";
            }

            return $"{leftDigits}/{rightDigits}";
        }

        var digits = new string(text.Where(char.IsDigit).Take(6).ToArray());

        if (digits.Length <= 3)
        {
            return digits;
        }

        if (digits.Length == 4)
        {
            if (digits.StartsWith("1") || digits.StartsWith("2"))
            {
                return $"{digits.Substring(0, 3)}/{digits.Substring(3)}";
            }
            return $"{digits.Substring(0, 2)}/{digits.Substring(2)}";
        }

        if (digits.Length == 5)
        {
            return $"{digits.Substring(0, 3)}/{digits.Substring(3)}";
        }

        return $"{digits.Substring(0, 3)}/{digits.Substring(3, 3)}";
    }

    // ==========================================
    // NOTA MÉDICA DE LA CONSULTA EN CURSO (NOM-004-SSA3)
    // ==========================================
    [ObservableProperty]
    private string _consultationFolio = string.Empty;

    [ObservableProperty]
    private string _consultationChiefComplaint = string.Empty;

    [ObservableProperty]
    private string _consultationTA = string.Empty;

    partial void OnConsultationTAChanged(string value)
    {
        var formatted = FormatTA(value);
        if (formatted != value)
        {
            ConsultationTA = formatted;
        }
        NotifyVitalSignsWarningChanged();
    }

    [ObservableProperty]
    private string _consultationFC = string.Empty;

    [ObservableProperty]
    private string _consultationFR = string.Empty;

    [ObservableProperty]
    private string _consultationTemp = string.Empty;

    [ObservableProperty]
    private string _consultationWeight = string.Empty;

    [ObservableProperty]
    private string _consultationHeight = string.Empty;

    [ObservableProperty]
    private string _consultationIMC = "Valores no disponibles";

    [ObservableProperty]
    private string _consultationGlucose = string.Empty;

    [ObservableProperty]
    private string _consultationPhysicalExam = string.Empty;

    [ObservableProperty]
    private string _consultationSystemsReview = string.Empty;

    [ObservableProperty]
    private string _consultationDiagnosis = string.Empty;

    private bool _isSelectingCie10;

    partial void OnConsultationDiagnosisChanged(string value)
    {
        if (_isSelectingCie10) return;
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
        {
            Cie10Suggestions = new ObservableCollection<Cie10Diagnosis>();
            IsCie10DropdownOpen = false;
            return;
        }

        var matches = Cie10Diagnosis.Search(value.Trim(), 20);
        Cie10Suggestions = new ObservableCollection<Cie10Diagnosis>(matches);
        IsCie10DropdownOpen = Cie10Suggestions.Count > 0;
    }

    [ObservableProperty]
    private ObservableCollection<Cie10Diagnosis> _cie10Suggestions = new();

    [ObservableProperty]
    private bool _isCie10DropdownOpen;

    [RelayCommand]
    public void SelectCie10Suggestion(Cie10Diagnosis? item)
    {
        if (item == null) return;
        _isSelectingCie10 = true;
        try
        {
            ConsultationDiagnosis = item.ToString();
            IsCie10DropdownOpen = false;
            Cie10Suggestions.Clear();
        }
        finally
        {
            _isSelectingCie10 = false;
        }
    }

    [ObservableProperty]
    private string _consultationTreatmentPlan = string.Empty;

    [ObservableProperty]
    private string _consultationNotes = string.Empty;

    /// <summary>Cuenta de "Terminar consulta": el mismo cobro que el Punto de Venta, con su propia cuenta.</summary>
    public CheckoutViewModel Checkout { get; }

    /// <summary>Hoja "Terminar consulta" (cuenta y cobro de la consulta) abierta.</summary>
    [ObservableProperty]
    private bool _isFinishConsultationOpen;

    // ==========================================
    // INDICACIONES GENERALES EN CONSULTA ACTIVA
    // ==========================================
    [ObservableProperty]
    private string _consultationGeneralIndications = "Tomar abundantes líquidos, mantener reposo relativo y acudir a urgencias en caso de presentar datos de alarma.";

    // Las indicaciones de la receta (vista previa, impresión y PDF) son de Preview
    partial void OnConsultationTreatmentPlanChanged(string value) => _workflow.RaisePrescriptionIndications();

    // ==========================================
    // OPERACIONES DE CONSULTA MÉDICA (NOM-004-SSA3)
    // ==========================================

    partial void OnConsultationWeightChanged(string value) => RecalculateIMC();
    partial void OnConsultationHeightChanged(string value) => RecalculateIMC();

    public void RecalculateIMC()
    {
        if (decimal.TryParse(ConsultationWeight.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var weight) &&
            decimal.TryParse(ConsultationHeight.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var height) &&
            height > 0)
        {
            var imc = weight / (height * height);
            string category;
            if (imc < 18.5m) category = "Bajo peso";
            else if (imc < 25.0m) category = "Peso normal";
            else if (imc < 30.0m) category = "Sobrepeso";
            else if (imc < 35.0m) category = "Obesidad Grado I";
            else category = "Obesidad Grado II+";

            ConsultationIMC = $"{imc:F1} kg/m² ({category})";
        }
        else
        {
            ConsultationIMC = "Valores no disponibles";
        }
    }

    /// <summary>La nota de la consulta en curso ya está guardada en el expediente (con lo último capturado).</summary>
    public bool IsCurrentConsultationSaved { get; set; }

    /// <summary>Cita que se atiende con esta consulta: al cobrar queda "Atendida".</summary>
    public int? AttendingAppointmentId { get; set; }

    /// <summary>
    /// Refleja en la instancia en memoria los datos que AddConsultationNoteAsync actualiza en la base.
    /// </summary>
    private static void ApplyConsultationSummary(PatientRecord patient, PatientConsultation consultation)
    {
        patient.LastVisit = consultation.Date;
        if (!string.IsNullOrWhiteSpace(consultation.Diagnosis)) patient.LastDiagnosis = consultation.Diagnosis;
        if (!string.IsNullOrWhiteSpace(consultation.VitalSigns)) patient.VitalSigns = consultation.VitalSigns;
        if (!string.IsNullOrWhiteSpace(consultation.Notes) || !string.IsNullOrWhiteSpace(consultation.TreatmentPlan))
        {
            patient.ClinicalNotes = $"{consultation.ChiefComplaint}\n{consultation.Notes}\nPlan: {consultation.TreatmentPlan}".Trim();
        }
        if (consultation.Prescriptions.Count > 0)
        {
            patient.ActivePrescription = new List<PatientPrescriptionItem>(consultation.Prescriptions);
        }
    }

    public async Task<PatientConsultation?> SaveActiveConsultationToDbAsync(DateTime? endedAt = null)
    {
        if (_session.SelectedPatient == null) return null;

        var end = endedAt ?? DateTime.Now;
        var active = _consultationService?.CurrentConsultation;
        var start = active?.StartedAt ?? DateTime.Now;

        var cleanConsultationTA = (ConsultationTA ?? string.Empty).Trim().TrimEnd('/');
        var vitalsParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(cleanConsultationTA)) vitalsParts.Add($"TA: {cleanConsultationTA} mmHg");
        if (!string.IsNullOrWhiteSpace(ConsultationFC)) vitalsParts.Add($"FC: {ConsultationFC.Trim()} lpm");
        if (!string.IsNullOrWhiteSpace(ConsultationFR)) vitalsParts.Add($"FR: {ConsultationFR.Trim()} rpm");
        if (!string.IsNullOrWhiteSpace(ConsultationTemp)) vitalsParts.Add($"Temp: {ConsultationTemp.Trim()} °C");
        if (!string.IsNullOrWhiteSpace(ConsultationWeight)) vitalsParts.Add($"Peso: {ConsultationWeight.Trim()} kg");
        if (!string.IsNullOrWhiteSpace(ConsultationHeight)) vitalsParts.Add($"Talla: {ConsultationHeight.Trim()} m");
        if (!string.IsNullOrWhiteSpace(ConsultationGlucose)) vitalsParts.Add($"Glucosa: {ConsultationGlucose.Trim()} mg/dL");
        if (!string.IsNullOrWhiteSpace(ConsultationSpO2)) vitalsParts.Add($"SpO2: {ConsultationSpO2.Trim().TrimEnd('%')}%");
        if (!string.IsNullOrWhiteSpace(ConsultationIMC) && !ConsultationIMC.Contains("no disponibles")) vitalsParts.Add($"IMC: {ConsultationIMC.Trim()}");

        // Sin signos capturados la nota queda sin signos: copiar los del expediente los haría
        // pasar por medidos en esta consulta (y duplicaría puntos en la gráfica de evolución)
        var vitalSignsFormatted = string.Join(" | ", vitalsParts);

        var chiefComplaint = !string.IsNullOrWhiteSpace(ConsultationChiefComplaint)
            ? ConsultationChiefComplaint.Trim()
            : (!string.IsNullOrWhiteSpace(active?.ChiefComplaint) ? active.ChiefComplaint : "Consulta Médica General");

        var diagnosis = !string.IsNullOrWhiteSpace(ConsultationDiagnosis)
            ? ConsultationDiagnosis.Trim()
            : "Consulta Médica General";

        var treatmentPlan = !string.IsNullOrWhiteSpace(ConsultationTreatmentPlan)
            ? ConsultationTreatmentPlan.Trim()
            : (!string.IsNullOrWhiteSpace(ConsultationGeneralIndications) ? ConsultationGeneralIndications.Trim() : PrescriptionPreviewViewModel.SavedTreatmentPlanPlaceholder);

        var systemsReview = !string.IsNullOrWhiteSpace(ConsultationSystemsReview)
            ? ConsultationSystemsReview.Trim()
            : string.Empty;

        var folio = !string.IsNullOrWhiteSpace(ConsultationFolio)
            ? ConsultationFolio.Trim()
            : (!string.IsNullOrWhiteSpace(active?.Folio)
                ? active.Folio
                : $"CNS-{DateTime.Today:yyyyMMdd}-{Random.Shared.Next(100, 999)}");
        ConsultationFolio = folio;
        if (active != null) active.Folio = folio;

        // La receta de la nota es la que se ve, se imprime y se cobra (la del expediente en curso)
        var prescriptions = new List<PatientPrescriptionItem>(_session.SelectedPatient.ActivePrescription ?? new List<PatientPrescriptionItem>());

        var consultation = new PatientConsultation
        {
            PatientRecordId = _session.SelectedPatient.Id,
            Folio = folio,
            Date = DateTime.Now,
            StartedAt = start,
            EndedAt = end,
            ChiefComplaint = chiefComplaint,
            VitalSigns = vitalSignsFormatted,
            PhysicalExamination = ConsultationPhysicalExam?.Trim() ?? string.Empty,
            SystemsReview = systemsReview,
            Diagnosis = diagnosis,
            TreatmentPlan = treatmentPlan,
            Notes = ConsultationNotes?.Trim() ?? string.Empty,
            Prescriptions = prescriptions
        };

        consultation.EnsureNumericVitalSigns();

        if (_inventoryService != null)
        {
            try
            {
                await _inventoryService.AddConsultationNoteAsync(_session.SelectedPatient.Id, consultation);
                ApplyConsultationSummary(_session.SelectedPatient, consultation);
                IsCurrentConsultationSaved = true;
            }
            catch (Exception ex)
            {
                // No reportar como guardada una nota que no llegó a la base de datos
                AppLogger.Error("Error al guardar nota de consulta en BD", ex);
                _dialogs.ShowError($"No se pudo guardar la nota médica en el expediente:\n{ex.Message}");
                return null;
            }
        }
        else
        {
            _session.SelectedPatient.LastVisit = consultation.Date;
            _session.SelectedPatient.LastDiagnosis = consultation.Diagnosis;
            _session.SelectedPatient.VitalSigns = consultation.VitalSigns;
            _session.SelectedPatient.ClinicalNotes = $"{consultation.ChiefComplaint}\n{consultation.Notes}\nPlan: {consultation.TreatmentPlan}".Trim();
            _session.SelectedPatient.ActivePrescription = new List<PatientPrescriptionItem>(consultation.Prescriptions);
            IsCurrentConsultationSaved = true;
        }

        var existingInPatient = _session.SelectedPatient.Consultations.FirstOrDefault(c => c.Folio == consultation.Folio);
        if (existingInPatient != null)
        {
            var idx = _session.SelectedPatient.Consultations.IndexOf(existingInPatient);
            _session.SelectedPatient.Consultations[idx] = consultation;
        }
        else
        {
            _session.SelectedPatient.Consultations.Insert(0, consultation);
        }

        var existingInHistory = _session.ConsultationsHistory.FirstOrDefault(c => c.Folio == consultation.Folio);
        if (existingInHistory != null)
        {
            var idx = _session.ConsultationsHistory.IndexOf(existingInHistory);
            _session.ConsultationsHistory[idx] = consultation;
        }
        else
        {
            _session.ConsultationsHistory.Insert(0, consultation);
        }

        _workflow.RefreshVitalSignsEvolution(_session.SelectedPatient);

        return consultation;
    }

    [RelayCommand]
    public void StartActiveConsultation()
    {
        if (_gate.BlockIfReceivingHandoff()) return;
        if (_session.SelectedPatient == null)
        {
            _nav.GoToPatientsHome();
            return;
        }

        IsCurrentConsultationSaved = false;

        if (_consultationService != null)
        {
            _consultationService.StartConsultation(_session.SelectedPatient);
            _record.BeginConsultationOnRecord(_session.SelectedPatient);
            _workflow.SyncActiveConsultation();

            ConsultationFolio = _consultationService.CurrentConsultation?.Folio ?? $"CNS-{DateTime.Now:yyyyMMdd-HHmmss}";
            ClearConsultationFields();
            _workflow.NotifyAccountChanged();
            // La pantalla de consulta ya indica que está en curso: sin ventana emergente que interrumpa
            _nav.PatientsPage = PatientsNavigation.PageConsultation;
        }
    }

    [RelayCommand]
    public async Task FinalizeConsultationAsync()
    {
        if (_gate.BlockIfConsultationReadOnly()) return;
        if (_consultationService == null || !_consultationService.HasActiveConsultation)
        {
            _info.InfoModalMessage = "No hay ninguna consulta médica activa para terminar.";
            _info.IsInfoModalVisible = true;
            return;
        }

        if (_inventoryService == null)
        {
            _info.InfoModalMessage = "El servicio de inventario no está disponible.";
            _info.IsInfoModalVisible = true;
            return;
        }

        // Guardar (o actualizar) la nota con la hora de término; si ya había un avance guardado,
        // se actualiza el mismo registro (mismo folio)
        var endedAt = DateTime.Now;
        IsCurrentConsultationSaved = false;
        await SaveActiveConsultationToDbAsync(endedAt);

        // Existencias al día para la cuenta (lo surtido se descuenta al cobrar)
        await _workflow.LoadServicesAsync();
        Checkout.PatientName = _session.SelectedPatient?.FullName ?? _active.ActiveConsultationPatientName ?? CheckoutViewModel.DefaultClient;

        // Honorario + todo lo cargado durante la consulta. La consulta sigue activa hasta cobrar:
        // "Volver a la consulta" permite agregar algo que faltó.
        BuildCheckoutCart();
        IsFinishConsultationOpen = true;
    }

    /// <summary>"Volver a la consulta" (o Esc) desde Terminar consulta: no cobra ni la cierra.</summary>
    [RelayCommand]
    public void CloseFinishConsultation()
    {
        IsFinishConsultationOpen = false;

        // El cobro se rearma al terminar de nuevo y la nota se actualizará entonces
        if (_active.HasActiveConsultation)
        {
            Checkout.Reset();
            IsCurrentConsultationSaved = false;
        }
        _workflow.SyncActiveConsultation();
    }

    [RelayCommand]
    public void AppendGeneralIndicationPreset(string text)
    {
        if (string.IsNullOrWhiteSpace(ConsultationGeneralIndications))
            ConsultationGeneralIndications = text;
        else
            ConsultationGeneralIndications = ConsultationGeneralIndications.Trim() + " " + text;

        // Los botones rápidos están junto al "Plan Terapéutico": el texto debe verse ahí
        // (antes solo se guardaba en un campo que no aparecía en pantalla)
        ConsultationTreatmentPlan = string.IsNullOrWhiteSpace(ConsultationTreatmentPlan)
            ? text
            : ConsultationTreatmentPlan.TrimEnd() + " " + text;
    }

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
    public void NotifyVitalSignsWarningChanged()
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
                bool child = _session.SelectedPatient is { Age: < 12 };
                if (!child && fc > 100) warnings.Add($"FC {fc:0}: taquicardia");
                else if (child && fc > 140) warnings.Add($"FC {fc:0}: alta para la edad");
                else if (fc < 50) warnings.Add($"FC {fc:0}: bradicardia");
            }

            return string.Join(" · ", warnings);
        }
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Replace(',', '.').Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    /// <summary>Última consulta anterior (sin contar el avance guardado de la consulta en curso).</summary>
    public PatientConsultation? PreviousConsultation =>
        _session.ConsultationsHistory.FirstOrDefault(c => !string.Equals(c.Folio, _active.ActiveConsultationFolio, StringComparison.Ordinal));

    /// <summary>Cambió el historial o la consulta en curso (NotifyAccountChanged y el historial del coordinador).</summary>
    public void RaisePreviousConsultation() => OnPropertyChanged(nameof(PreviousConsultation));

    /// <summary>
    /// Arma el cobro de la consulta: honorario + cargos acumulados. Se reconstruye cada vez para
    /// que volver a la consulta y terminar de nuevo no duplique cantidades.
    /// </summary>
    private void BuildCheckoutCart()
    {
        Checkout.Reset();
        Checkout.PatientName = _session.SelectedPatient?.FullName ?? _active.ActiveConsultationPatientName ?? CheckoutViewModel.DefaultClient;
        var fee = _account.ConsultationFeeService;
        Checkout.Cart.Add(new CartItem
        {
            ProductId = fee != null ? -fee.Id : -1,
            Code = fee?.Code ?? "SERV-001",
            Name = _account.ConsultationFeeName,
            GenericName = $"Servicio Clínico • {fee?.Category ?? "Consulta"}",
            Presentation = "Honorario",
            Unit = "Servicio",
            AvailableStock = 999,
            UnitPrice = _account.ConsultationFeeAmount,
            Quantity = 1,
            IsService = true
        });

        foreach (var item in _consultationService?.CurrentConsultation?.Items ?? Enumerable.Empty<ConsultationProductItem>())
        {
            var product = item.ProductId > 0 ? _catalog.AvailableProducts.FirstOrDefault(p => p.Id == item.ProductId) : null;
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

    public void ClearConsultationFields()
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
