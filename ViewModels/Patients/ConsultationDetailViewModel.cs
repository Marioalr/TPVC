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

/// <summary>Una sección del detalle de la consulta: un dato y su texto.</summary>
public sealed record ConsultationDetailSection(string Title, string Text);

/// <summary>Un renglón de la receta mientras se corrige (todo editable).</summary>
public partial class EditablePrescriptionLine : ObservableObject
{
    [ObservableProperty]
    private string _medicineName = string.Empty;

    [ObservableProperty]
    private string _presentation = string.Empty;

    [ObservableProperty]
    private string _dosage = string.Empty;

    public int RecommendedQuantity { get; init; } = 1;

    /// <summary>Revisión 16: el principio activo entre paréntesis del renglón y si se muestra (ver <see cref="PatientPrescriptionItem.ActiveIngredient"/>).</summary>
    public string? ActiveIngredient { get; init; }

    public bool? ShowActiveIngredient { get; init; }

    public static EditablePrescriptionLine From(PatientPrescriptionItem item) => new()
    {
        MedicineName = item.MedicineName,
        Presentation = item.Presentation ?? string.Empty,
        Dosage = item.Dosage,
        RecommendedQuantity = item.RecommendedQuantity,
        ActiveIngredient = item.ActiveIngredient,
        ShowActiveIngredient = item.ShowActiveIngredient
    };

    public PatientPrescriptionItem ToItem() => new()
    {
        MedicineName = MedicineName.Trim(),
        Presentation = string.IsNullOrWhiteSpace(Presentation) ? null : Presentation.Trim(),
        Dosage = Dosage.Trim(),
        RecommendedQuantity = RecommendedQuantity,
        ActiveIngredient = ActiveIngredient,
        ShowActiveIngredient = ActiveIngredient == null ? null : ShowActiveIngredient
    };
}

/// <summary>
/// La hoja de una consulta del historial (desde v1.24.0): el detalle por datos (motivo, interrogatorio, exploración,
/// signos, diagnóstico, plan, notas y receta), y la receta se puede corregir e imprimir después de terminar la
/// consulta, con la fecha y los signos de esa consulta.
/// </summary>
public partial class ConsultationDetailViewModel : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private readonly InventoryService? _inventoryService;
    private readonly PatientSession _session;
    private readonly ActiveConsultationSession _active;
    private readonly CatalogStore _catalog;
    private readonly ClinicInfo _clinic;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly ClinicProfileService? _clinicProfileService;
    private readonly IPrescriptionOutput? _printing;

    public ConsultationDetailViewModel(
        InventoryService? inventoryService,
        PatientSession session,
        ActiveConsultationSession active,
        CatalogStore catalog,
        ClinicInfo clinic,
        InfoDialogState info,
        PatientsFeedback feedback,
        ClinicProfileService? clinicProfileService,
        IPrescriptionOutput? printing)
    {
        _inventoryService = inventoryService;
        _session = session;
        _active = active;
        _catalog = catalog;
        _clinic = clinic;
        _info = info;
        _feedback = feedback;
        _clinicProfileService = clinicProfileService;
        _printing = printing;
    }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private PatientConsultation? _consultation;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ConsultationDetailSection> _sections = Array.Empty<ConsultationDetailSection>();

    [ObservableProperty]
    private IReadOnlyList<VitalSummary> _vitals = Array.Empty<VitalSummary>();

    [ObservableProperty]
    private IReadOnlyList<PatientPrescriptionItem> _prescription = Array.Empty<PatientPrescriptionItem>();

    /// <summary>La orden de laboratorio de la consulta (desde v1.25.0); vacía si no se pidieron estudios.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLabOrder), nameof(HasLabIndications))]
    private LabOrder _labOrder = new();

    public bool HasVitals => Vitals.Count > 0;
    public bool HasPrescription => Prescription.Count > 0;
    public bool HasLabOrder => !LabOrder.IsEmpty;
    public bool HasLabIndications => HasLabOrder && !string.IsNullOrWhiteSpace(LabOrder.Indications);

    // ================================================================== abrir

    /// <summary>Abre la hoja de una consulta (clic en su tarjeta del historial).</summary>
    [RelayCommand]
    public void Open(PatientConsultation? consultation)
    {
        if (consultation == null) return;
        Consultation = consultation;
        Title = $"Consulta del {consultation.Date.ToString("dddd d 'de' MMMM 'de' yyyy", Mexican)}";
        var times = consultation.StartedAt is { } start
            ? $"{start:HH:mm}{(consultation.EndedAt is { } end ? $" a {end:HH:mm}" : string.Empty)}"
            : consultation.Date.ToString("HH:mm", Mexican);
        Subtitle = string.Join(" · ", new[] { times, consultation.Folio }.Where(x => !string.IsNullOrWhiteSpace(x)));
        Sections = BuildSections(consultation);
        Vitals = BuildVitals(consultation);
        Prescription = consultation.Prescriptions.ToList();
        LabOrder = consultation.LabOrder;
        IsEditingPrescription = false;
        OnPropertyChanged(nameof(HasVitals));
        OnPropertyChanged(nameof(HasPrescription));
        OnPropertyChanged(nameof(CanEditPrescription));
        IsOpen = true;
    }

    [RelayCommand]
    public void Close()
    {
        IsEditingPrescription = false;
        IsOpen = false;
    }

    /// <summary>Los datos de la consulta en el orden de la nota médica; solo los que tienen algo.</summary>
    public static IReadOnlyList<ConsultationDetailSection> BuildSections(PatientConsultation c)
    {
        var sections = new List<ConsultationDetailSection>();
        void Add(string title, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) sections.Add(new ConsultationDetailSection(title, text.Trim()));
        }
        Add("Motivo de consulta", c.ChiefComplaint);
        Add("Interrogatorio por aparatos y sistemas", c.SystemsReview);
        Add("Exploración física", c.PhysicalExamination);
        Add("Diagnóstico", c.Diagnosis);
        Add("Plan y tratamiento", c.TreatmentPlan);
        Add("Notas", c.Notes);
        return sections;
    }

    /// <summary>Los signos de la consulta, cada uno con su unidad (o el texto que se guardó, en una consulta vieja).</summary>
    public static IReadOnlyList<VitalSummary> BuildVitals(PatientConsultation c)
    {
        static string N(decimal value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
        var list = new List<VitalSummary>();
        if (c.BloodPressureSystolic is { } sys && c.BloodPressureDiastolic is { } dia) list.Add(new VitalSummary("T/A (mmHg)", $"{N(sys, "0")}/{N(dia, "0")}", string.Empty, false));
        if (c.HeartRate is { } hr) list.Add(new VitalSummary("FC (lpm)", N(hr, "0"), string.Empty, false));
        if (c.RespiratoryRate is { } rr) list.Add(new VitalSummary("FR (rpm)", N(rr, "0"), string.Empty, false));
        if (c.Temperature is { } t) list.Add(new VitalSummary("Temp (°C)", N(t, "0.0"), string.Empty, false));
        if (c.Weight is { } w) list.Add(new VitalSummary("Peso (kg)", N(w, "0.0"), string.Empty, false));
        if (c.Height is { } h) list.Add(new VitalSummary("Talla (m)", N(h, "0.00"), string.Empty, false));
        if (c.BMI is { } bmi) list.Add(new VitalSummary("IMC", N(bmi, "0.0"), string.Empty, false));
        if (c.OxygenSaturation is { } spo2) list.Add(new VitalSummary("SpO2 (%)", N(spo2, "0"), string.Empty, false));
        if (c.Glucose is { } glucose) list.Add(new VitalSummary("Glucosa (mg/dL)", N(glucose, "0"), string.Empty, false));
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(c.VitalSigns))
        {
            // Consultas de antes de los signos por separado: el texto guardado, un dato por renglón
            foreach (var part in c.VitalSigns.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split(':', 2, StringSplitOptions.TrimEntries);
                list.Add(pieces.Length == 2 ? new VitalSummary(pieces[0], pieces[1], string.Empty, false) : new VitalSummary("Signos", part, string.Empty, false));
            }
        }
        return list;
    }

    // ================================================================== corregir la receta

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadingPrescription))]
    private bool _isEditingPrescription;

    public bool IsReadingPrescription => !IsEditingPrescription;

    public ObservableCollection<EditablePrescriptionLine> EditLines { get; } = new();

    /// <summary>La consulta en curso de este paciente se corrige en la consulta, no aquí.</summary>
    public bool CanEditPrescription => Consultation != null && !IsConsultationInProgressForPatient;

    private bool IsConsultationInProgressForPatient =>
        _active.HasActiveConsultation && _session.SelectedPatient != null && _active.ActiveConsultation?.PatientId == _session.SelectedPatient.Id;

    /// <summary>Lo que se escribe para agregar un medicamento (sugiere del catálogo, como en la consulta).</summary>
    [ObservableProperty]
    private string _newMedicineText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<Product> _medicineSuggestions = Array.Empty<Product>();

    partial void OnNewMedicineTextChanged(string value)
    {
        var query = TextSearch.Normalize(value);
        MedicineSuggestions = query.Length == 0
            ? Array.Empty<Product>()
            : _catalog.AvailableProducts
                .Where(CatalogStore.IsProductMedication)
                .Where(p => TextSearch.Matches(query, p.Name, p.GenericName, p.Code))
                .Take(8)
                .ToList();
    }

    [RelayCommand]
    public void StartEditPrescription()
    {
        if (!CanEditPrescription) return;
        EditLines.Clear();
        foreach (var item in Prescription) EditLines.Add(EditablePrescriptionLine.From(item));
        NewMedicineText = string.Empty;
        IsEditingPrescription = true;
    }

    [RelayCommand]
    public void CancelEditPrescription()
    {
        IsEditingPrescription = false;
        EditLines.Clear();
    }

    /// <summary>
    /// Agrega un medicamento del catálogo con su presentación: la marca con su concentración y forma y el principio activo
    /// entre paréntesis (aquí siempre se muestra; el renglón lo guarda por si después se oculta).
    /// </summary>
    [RelayCommand]
    public void AddSuggestedMedicine(Product? product)
    {
        if (product == null) return;
        var parts = PrescriptionNaming.Parts(product);
        EditLines.Add(new EditablePrescriptionLine
        {
            MedicineName = parts.Compose(true),
            Presentation = PrescriptionNaming.Presentation(product),
            ActiveIngredient = parts.ActiveIngredient,
            ShowActiveIngredient = parts.ActiveIngredient == null ? null : true
        });
        NewMedicineText = string.Empty;
    }

    /// <summary>Agrega lo escrito tal cual (un medicamento que no está en el catálogo).</summary>
    [RelayCommand]
    public void AddTypedMedicine()
    {
        if (string.IsNullOrWhiteSpace(NewMedicineText)) return;
        EditLines.Add(new EditablePrescriptionLine { MedicineName = NewMedicineText.Trim() });
        NewMedicineText = string.Empty;
    }

    [RelayCommand]
    public void RemoveLine(EditablePrescriptionLine? line)
    {
        if (line != null) EditLines.Remove(line);
    }

    /// <summary>
    /// Guarda la receta corregida en la consulta. Si es la última consulta del paciente, también queda como su receta
    /// vigente. Lo cobrado en esa consulta no cambia.
    /// </summary>
    [RelayCommand]
    public async Task SavePrescriptionAsync()
    {
        var consultation = Consultation;
        var patient = _session.SelectedPatient;
        if (consultation == null || patient == null || !CanEditPrescription) return;

        var items = EditLines.Where(l => !string.IsNullOrWhiteSpace(l.MedicineName)).Select(l => l.ToItem()).ToList();
        bool isLatest = _session.ConsultationsHistory.Count > 0
                        && _session.ConsultationsHistory.OrderByDescending(c => c.Date).First() is { } latest
                        && (ReferenceEquals(latest, consultation) || (latest.Id > 0 && latest.Id == consultation.Id));
        List<PatientPrescriptionItem>? stored = null;
        try
        {
            if (_inventoryService != null && consultation.Id > 0)
            {
                stored = await _inventoryService.UpdateConsultationPrescriptionAsync(consultation.Id, items, updateActivePrescription: isLatest);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo guardar la receta corregida", ex);
            _info.InfoModalMessage = $"No se pudo guardar la receta: {ex.Message}";
            _info.IsInfoModalVisible = true;
            return;
        }

        // Lo que se ve: la consulta del historial (su tarjeta se rehace) y, si es la última, la receta del expediente
        consultation.Prescriptions = items;
        if (isLatest)
        {
            if (stored != null)
            {
                // Paso 8.21: los mismos renglones que quedaron en la base (con sus RowId), y la foto de la instancia los toma
                patient.ActivePrescription = stored;
                PatientFields.MarkStored(patient, new[] { nameof(PatientRecord.ActivePrescriptionJson) });
            }
            else
            {
                patient.ActivePrescription = items.Select(i => new PatientPrescriptionItem
                {
                    MedicineName = i.MedicineName, Dosage = i.Dosage, RecommendedQuantity = i.RecommendedQuantity, Presentation = i.Presentation
                }).ToList();
            }
            _session.NotifySelectedPatientMutated();
        }
        var history = _session.ConsultationsHistory;
        int index = history.IndexOf(consultation);
        if (index >= 0) history[index] = consultation;

        Prescription = items;
        OnPropertyChanged(nameof(HasPrescription));
        IsEditingPrescription = false;
        EditLines.Clear();
        _feedback.ShowSuccess(isLatest ? "Receta corregida; es la vigente del expediente" : "Receta de la consulta corregida");
    }

    // ================================================================== imprimir

    private ClinicProfile CurrentProfile() => _clinicProfileService?.CurrentProfile ?? new ClinicProfile
    {
        DoctorName = _clinic.DoctorName,
        DoctorLicense = _clinic.DoctorLicense,
        DoctorSpecialty = _clinic.DoctorSpecialty,
        ClinicName = _clinic.ClinicName,
        ClinicAddress = _clinic.ClinicAddress,
        ClinicPhone = _clinic.ClinicPhone
    };

    /// <summary>Indicaciones de la receta de esta consulta: su plan, o el texto general si no tiene.</summary>
    private string Indications(PatientConsultation c) =>
        string.IsNullOrWhiteSpace(c.TreatmentPlan) || c.TreatmentPlan.Trim() == PrescriptionPreviewViewModel.SavedTreatmentPlanPlaceholder
            ? PrescriptionDefaults.Indications
            : c.TreatmentPlan.Trim();

    /// <summary>Imprime la receta de esta consulta, con su fecha y sus signos.</summary>
    [RelayCommand]
    public void PrintPrescription()
    {
        if (Consultation is not { } c || _session.SelectedPatient is not { } patient || Prescription.Count == 0) return;
        if (_printing == null) return;
        bool printed = _printing.PrintPrescription(patient, Prescription, CurrentProfile(), Indications(c), c);
        if (printed) _feedback.ShowSuccess("Receta enviada a la impresora");
    }

    /// <summary>Guarda la receta de esta consulta como documento (para PDF), con su fecha y sus signos.</summary>
    [RelayCommand]
    public void ExportPrescription()
    {
        if (Consultation is not { } c || _session.SelectedPatient is not { } patient || Prescription.Count == 0) return;
        if (_printing?.ExportPrescriptionDocument(patient, Prescription, CurrentProfile(), Indications(c), null, c) is { } path)
        {
            _info.InfoModalMessage = $"Receta guardada en:\n{path}";
            _info.IsInfoModalVisible = true;
        }
    }

    /// <summary>Imprime la solicitud de laboratorio de esta consulta, con su fecha y su diagnóstico.</summary>
    [RelayCommand]
    public void PrintLabOrder()
    {
        if (Consultation is not { } c || _session.SelectedPatient is not { } patient || !HasLabOrder || _printing == null) return;
        if (_printing.PrintLabOrder(patient, LabOrder, CurrentProfile(), c.Diagnosis, c)) _feedback.ShowSuccess("Solicitud de laboratorio enviada a la impresora");
    }

    /// <summary>Guarda la solicitud de laboratorio de esta consulta como documento (para PDF).</summary>
    [RelayCommand]
    public void ExportLabOrder()
    {
        if (Consultation is not { } c || _session.SelectedPatient is not { } patient || !HasLabOrder) return;
        if (_printing?.ExportLabOrderDocument(patient, LabOrder, CurrentProfile(), c.Diagnosis, null, c) is { } path)
        {
            _info.InfoModalMessage = $"Solicitud de laboratorio guardada en:\n{path}";
            _info.IsInfoModalVisible = true;
        }
    }
}
