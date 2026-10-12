using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Ficha del paciente (fase 6, paso 17): antecedentes, historial único (consultas, signos, recetas y
/// citas), "Lo esencial" de los signos, la última receta y las citas del expediente. El coordinador la
/// rehace al cambiar de paciente; iniciar o volver a la consulta y editar una sección del alta son
/// flujos del coordinador. Lo que cambia la receta o sus indicaciones se lo pide al coordinador.
/// </summary>
public partial class PatientChartViewModel : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private readonly InventoryService? _inventoryService;
    private readonly PatientSession _session;
    private readonly ActiveConsultationSession _active;
    private readonly ConsultationRecordState _record;
    private readonly PatientsNavigation _nav;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly IConsultationGate _gate;
    private readonly IUserDialogs _dialogs;
    private readonly IPatientsWorkflow _workflow;

    public PatientChartViewModel(
        InventoryService? inventoryService,
        PatientSession session,
        ActiveConsultationSession active,
        ConsultationRecordState record,
        PatientsNavigation navigation,
        InfoDialogState info,
        PatientsFeedback feedback,
        IConsultationGate gate,
        IUserDialogs dialogs,
        IPatientsWorkflow workflow)
    {
        _inventoryService = inventoryService;
        _session = session;
        _active = active;
        _record = record;
        _nav = navigation;
        _info = info;
        _feedback = feedback;
        _gate = gate;
        _dialogs = dialogs;
        _workflow = workflow;
    }

    private ObservableCollection<PatientConsultation>? _observedHistory;
    private ObservableCollection<PatientAppointment>? _observedAppointments;

    // ==========================================
    // CITAS DEL PACIENTE
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<PatientAppointment> _patientAppointmentsHistory = new();

    public bool IsConsultationForSelectedPatient =>
        _active.HasActiveConsultation && _session.SelectedPatient != null && _active.ActiveConsultation?.PatientId == _session.SelectedPatient.Id;

    public string ChartPrimaryActionText => IsConsultationForSelectedPatient ? "Volver a la consulta" : "Iniciar consulta";

    /// <summary>La consulta en curso es (o deja de ser) la del expediente abierto (NotifyPatientsPageState del coordinador).</summary>
    public void RaiseConsultationLink()
    {
        OnPropertyChanged(nameof(IsConsultationForSelectedPatient));
        OnPropertyChanged(nameof(ChartPrimaryActionText));
    }

    /// <summary>"Última receta" de la ficha: durante la consulta, la emitida antes de ella.</summary>
    public IReadOnlyList<PatientPrescriptionItem> ChartPrescription =>
        IsConsultationForSelectedPatient && _record.PreviousPrescriptionKnown
            ? _record.PreviousPrescription
            : (IReadOnlyList<PatientPrescriptionItem>?)_session.SelectedPatient?.ActivePrescription ?? Array.Empty<PatientPrescriptionItem>();

    /// <summary>Cambió la receta de la consulta o la anterior (NotifyPrescriptionStateChanged del coordinador).</summary>
    public void RaisePrescriptionState()
    {
        OnPropertyChanged(nameof(ChartPrescription));
        OnPropertyChanged(nameof(HasActivePrescription));
    }

    // ================================================================== ficha

    [ObservableProperty]
    private ObservableCollection<AntecedentSection> _antecedentSections = new();

    [ObservableProperty]
    private ObservableCollection<TimelineEntry> _timeline = new();

    [ObservableProperty]
    private string _timelineFilter = "Todo";

    [ObservableProperty]
    private string _timelineSearchText = string.Empty;

    [ObservableProperty]
    private bool _isVitalsChartVisible;

    [ObservableProperty]
    private IReadOnlyList<VitalSummary> _latestVitals = Array.Empty<VitalSummary>();

    public IReadOnlyList<string> TimelineFilters { get; } = new[] { "Todo", "Consultas", "Citas" };

    public bool HasTimeline => Timeline.Count > 0;
    public bool HasLatestVitals => LatestVitals.Count > 0;
    public bool HasActivePrescription => ChartPrescription.Count > 0;

    public string NextAppointmentText
    {
        get
        {
            var next = PatientAppointmentsHistory
                .Where(a => a.IsUpcoming)
                .OrderBy(a => a.Date.Date + AgendaPanelViewModel.ParseAppointmentTime(a.Time))
                .FirstOrDefault();
            return next == null ? string.Empty : $"Próxima cita: {next.Date.ToString("ddd d MMM", Mexican)}, {next.Time}";
        }
    }

    public bool HasNextAppointment => !string.IsNullOrEmpty(NextAppointmentText);

    public string SelectedPatientMeta
    {
        get
        {
            var p = _session.SelectedPatient;
            if (p == null) return string.Empty;
            var parts = new List<string> { p.AgeDisplay };
            if (!string.IsNullOrWhiteSpace(p.Gender)) parts.Add(p.Gender);
            if (!string.IsNullOrWhiteSpace(p.RecordNumber))
            {
                // Paso 8.17: el folio con letra con que se creó sin conexión, si la PC principal ya le dio el normal
                parts.Add(string.IsNullOrWhiteSpace(p.OriginalFolio) ? p.RecordNumber : $"{p.RecordNumber} (sin conexión: {p.OriginalFolio})");
            }
            if (!string.IsNullOrWhiteSpace(p.BloodType) && p.BloodType != PatientFormViewModel.BloodTypeUnknown) parts.Add(p.BloodType);
            if (!string.IsNullOrWhiteSpace(p.Phone)) parts.Add(p.FormattedPhone);
            return string.Join(" · ", parts);
        }
    }

    partial void OnTimelineFilterChanged(string value) => RefreshTimeline();
    partial void OnTimelineSearchTextChanged(string value) => RefreshTimeline();

    partial void OnPatientAppointmentsHistoryChanged(ObservableCollection<PatientAppointment> value)
    {
        if (_observedAppointments != null) _observedAppointments.CollectionChanged -= OnChartSourceCollectionChanged;
        _observedAppointments = value;
        if (_observedAppointments != null) _observedAppointments.CollectionChanged += OnChartSourceCollectionChanged;
        RefreshTimeline();
    }

    public void ObserveConsultationsHistory(ObservableCollection<PatientConsultation> history)
    {
        if (_observedHistory != null) _observedHistory.CollectionChanged -= OnChartSourceCollectionChanged;
        _observedHistory = history;
        if (_observedHistory != null) _observedHistory.CollectionChanged += OnChartSourceCollectionChanged;
        RefreshTimeline();
    }

    private void OnChartSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshTimeline();

    /// <summary>Se llama al cambiar de paciente: antecedentes, historial y resumen de la ficha.</summary>
    public void RefreshChartData()
    {
        var patient = _session.SelectedPatient;
        AntecedentSections = new ObservableCollection<AntecedentSection>(
            patient == null ? Enumerable.Empty<AntecedentSection>() : BuildAntecedentSections(patient));
        TimelineSearchText = string.Empty;
        IsVitalsChartVisible = false;
        RefreshTimeline();
        OnPropertyChanged(nameof(SelectedPatientMeta));
        _workflow.NotifyPrescriptionStateChanged();
        OnPropertyChanged(nameof(IsConsultationForSelectedPatient));
        OnPropertyChanged(nameof(ChartPrimaryActionText));
    }

    private void RefreshTimeline()
    {
        var all = BuildTimeline(_session.ConsultationsHistory, PatientAppointmentsHistory);
        var kind = TimelineFilter switch
        {
            "Consultas" => TimelineKind.Consultation,
            "Citas" => TimelineKind.Appointment,
            _ => (TimelineKind?)null
        };
        var query = TextSearch.Normalize(TimelineSearchText);
        Timeline = new ObservableCollection<TimelineEntry>(all
            .Where(e => kind == null || e.Kind == kind)
            .Where(e => TextSearch.Matches(query, e.Title, e.Detail)));
        OnPropertyChanged(nameof(HasTimeline));
        _workflow.RaisePrescriptionIndications();
        OnPropertyChanged(nameof(NextAppointmentText));
        OnPropertyChanged(nameof(HasNextAppointment));
    }

    [RelayCommand]
    public void ToggleAntecedent(AntecedentSection? section)
    {
        if (section == null) return;
        var open = !section.IsOpen;
        foreach (var s in AntecedentSections) s.IsOpen = false;
        section.IsOpen = open;
    }

    public static List<AntecedentSection> BuildAntecedentSections(PatientRecord p)
    {
        static string Join(IEnumerable<string?> parts) => string.Join(" · ", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
        static string? Item(string label, string? value) => string.IsNullOrWhiteSpace(value) ? null : $"{label}: {value.Trim()}";

        AntecedentSection Make(string key, string title, params string?[] items)
        {
            var filled = items.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var detail = string.Join("\n", filled);
            var summary = filled.Count == 0 ? "Sin registrar" : Join(filled.Take(2).Select(x => x!.Contains(": ") ? x.Split(": ", 2)[1] : x));
            return new AntecedentSection(key, title, summary, detail);
        }

        var sections = new List<AntecedentSection>
        {
            Make("Heredofamiliares", "Heredofamiliares", p.FamilyHistory),
            Make("Patologicos", "Personales patológicos",
                Item("Alergias", p.AllergySummary),
                Item("Enfermedades crónicas", p.ChronicDiseases),
                Item("Cirugías", p.Surgeries),
                Item("Hospitalizaciones", p.Hospitalizations),
                Item("Transfusiones", p.Transfusions),
                p.IsPediatric ? null : Item("Vacunación", p.ImmunizationSchedule))
        };

        if (p.ShowsGynecoObstetric)
        {
            var formula = p.PregnanciesCount + p.CesareansCount + p.AbortionsCount > 0
                ? $"G{p.PregnanciesCount} C{p.CesareansCount} A{p.AbortionsCount}"
                : null;
            sections.Add(Make("Gineco", "Gineco-obstétricos",
                Item("Gestas / cesáreas / abortos", formula),
                Item("Menarca", string.IsNullOrWhiteSpace(p.MenarcheAge) ? null : $"{p.MenarcheAge} años"),
                Item("Ciclo", p.PeriodDuration),
                Item("Papanicolaou", p.PapSmear),
                Item("Inicio de vida sexual", p.SexualOnsetAge),
                p.IsCurrentlyPregnant ? $"Embarazo actual{(string.IsNullOrWhiteSpace(p.GestationalWeeks) ? string.Empty : $": {p.GestationalWeeks} semanas")}" : null));
        }

        if (p.IsPediatric)
        {
            sections.Add(Make("Desarrollo", "Desarrollo y vacunas",
                Item("Desarrollo", p.DevelopmentalMilestones),
                Item("Vacunación", p.ImmunizationSchedule)));
        }

        sections.Add(Make("NoPatologicos", "No patológicos",
            Item("Ocupación", p.Occupation),
            Item("Alimentación", p.Diet),
            Item("Vivienda", p.HousingConditions),
            Item("Animales", p.Zoonosis),
            Item("Religión", p.Religion)));

        sections.Add(Make("Contacto", "Contacto y emergencia",
            Item("Teléfono", p.FormattedPhone),
            Item("Correo", p.Email),
            Item("Domicilio", p.Address),
            string.IsNullOrWhiteSpace(p.EmergencyContactName) ? null : $"Emergencia: {p.EmergencyContactName.Trim()} {p.FormattedEmergencyContactPhone}".Trim()));

        return sections;
    }

    /// <summary>
    /// Historial de la ficha, del más reciente al más antiguo: una tarjeta por consulta, con su fecha y el resumen de
    /// sus datos (motivo, signos, receta y plan), y las citas. El detalle por datos se ve al abrir la consulta.
    /// </summary>
    public static List<TimelineEntry> BuildTimeline(IEnumerable<PatientConsultation> consultations, IEnumerable<PatientAppointment> appointments)
    {
        var entries = new List<(TimelineEntry Entry, int Order)>();
        foreach (var c in consultations)
        {
            var title = !string.IsNullOrWhiteSpace(c.Diagnosis) ? c.Diagnosis.Trim() : (string.IsNullOrWhiteSpace(c.ChiefComplaint) ? "Consulta" : c.ChiefComplaint.Trim());
            var prescriptions = c.Prescriptions;
            var labOrder = c.LabOrder;
            var lines = new List<TimelineLine>();
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) lines.Add(new TimelineLine(label, FirstLine(value)));
            }
            // Con diagnóstico el título lo dice; el motivo va abajo
            if (!string.IsNullOrWhiteSpace(c.Diagnosis)) Add("Motivo", c.ChiefComplaint);
            Add("Signos", c.VitalSigns?.Replace(" | ", " · "));
            Add("Receta", string.Join(" · ", prescriptions.Select(r => r.MedicineName.Trim()).Where(n => n.Length > 0)));
            Add("Laboratorio", string.Join(" · ", labOrder.Studies.Select(s => s.Name)));
            Add("Plan", c.TreatmentPlan);

            // La búsqueda revisa todo lo de la consulta, también lo que no se ve en la tarjeta
            var searchText = string.Join("\n", new[]
            {
                c.ChiefComplaint, c.SystemsReview, c.PhysicalExamination, c.VitalSigns, c.TreatmentPlan, c.Notes,
                string.Join("\n", prescriptions.Select(r => $"{r.MedicineName} {r.Presentation} {r.Dosage}")),
                string.Join("\n", labOrder.Studies.Select(s => s.Name)), labOrder.Indications
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            entries.Add((new TimelineEntry(TimelineKind.Consultation, c.Date, title, searchText, c.Folio, c, lines), 0));
        }

        foreach (var a in appointments)
        {
            var when = a.Date.Date + AgendaPanelViewModel.ParseAppointmentTime(a.Time);
            entries.Add((new TimelineEntry(TimelineKind.Appointment, when,
                string.IsNullOrWhiteSpace(a.Reason) ? "Cita" : a.Reason.Trim(),
                $"{a.Time} · {a.Status}"), 1));
        }

        return entries
            .OrderByDescending(x => x.Entry.Date.Date)
            .ThenBy(x => x.Order)
            .ThenByDescending(x => x.Entry.Date)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>El primer renglón de un texto, para la tarjeta (el resto se ve al abrir la consulta).</summary>
    private static string FirstLine(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length > 160 ? line[..157].TrimEnd() + "…" : line;
    }

    public static IReadOnlyList<VitalSummary> BuildLatestVitals(IEnumerable<VitalSignsEvolutionPoint>? points)
    {
        var list = points?.ToList() ?? new List<VitalSignsEvolutionPoint>();
        if (list.Count == 0) return Array.Empty<VitalSummary>();

        VitalSummary? Summary(string label, Func<VitalSignsEvolutionPoint, decimal?> value, Func<decimal, string> format, string unit)
        {
            var withValue = list.Where(p => value(p).HasValue).ToList();
            if (withValue.Count == 0) return null;
            var latest = value(withValue[^1])!.Value;
            string trend = string.Empty;
            bool up = false;
            if (withValue.Count > 1)
            {
                var previous = value(withValue[^2])!.Value;
                var diff = latest - previous;
                if (diff > 0) { up = true; trend = $"↑ {format(diff)}"; }
                else if (diff < 0) trend = $"↓ {format(-diff)}";
                else trend = "sin cambio";
            }
            return new VitalSummary($"{label}{unit}", format(latest), trend, up);
        }

        var result = new List<VitalSummary>();
        var last = list.LastOrDefault(p => p.Systolic.HasValue && p.Diastolic.HasValue);
        if (last != null)
        {
            var prev = list.Where(p => p.Systolic.HasValue && p.Diastolic.HasValue && p != last).LastOrDefault();
            bool up = prev != null && last.Systolic > prev.Systolic, down = prev != null && last.Systolic < prev.Systolic;
            // La flecha dice si subió o bajó sin depender del color (en oscuro el ámbar se confunde con el texto)
            var trend = prev == null ? string.Empty : $"{(up ? "↑ " : down ? "↓ " : string.Empty)}antes {prev.BloodPressureText}";
            result.Add(new VitalSummary("T/A (mmHg)", last.BloodPressureText, trend, up));
        }
        if (Summary("Peso", p => p.Weight, v => v.ToString("0.0", CultureInfo.InvariantCulture), " (kg)") is { } weight) result.Add(weight);
        if (Summary("IMC", p => p.BMI, v => v.ToString("0.0", CultureInfo.InvariantCulture), string.Empty) is { } bmi) result.Add(bmi);
        if (Summary("Glucosa", p => p.Glucose, v => v.ToString("0", CultureInfo.InvariantCulture), " (mg/dL)") is { } glucose) result.Add(glucose);
        return result;
    }

    /// <summary>Cambió la serie de signos de la gráfica: "Lo esencial" se arma con ella (antes del aviso de la serie).</summary>
    public void OnVitalSignsEvolutionPointsChanged(ObservableCollection<VitalSignsEvolutionPoint> value)
    {
        LatestVitals = BuildLatestVitals(value);
        OnPropertyChanged(nameof(HasLatestVitals));
    }

    /// <summary>
    /// Cambia la ficha abierta por la versión de la base sin perder lo que el médico tenía a la vista
    /// (búsqueda del historial, gráfica de signos y secciones abiertas).
    /// </summary>
    public void ReplaceSelectedPatientKeepingView(PatientRecord fresh)
    {
        var timelineSearch = TimelineSearchText;
        var vitalsVisible = IsVitalsChartVisible;
        var openSections = AntecedentSections.Where(a => a.IsOpen).Select(a => a.Key).ToHashSet();

        _session.SelectedPatient = fresh;

        TimelineSearchText = timelineSearch;
        IsVitalsChartVisible = vitalsVisible;
        foreach (var section in AntecedentSections) section.IsOpen = openSections.Contains(section.Key);
    }

    // ==========================================
    // CITAS: HISTORIAL DEL PACIENTE (el panel de agenda y el estado de las citas viven en Agenda)
    // ==========================================

    public async Task LoadPatientAppointmentsAsync(int? patientId)
    {
        if (_inventoryService == null || !patientId.HasValue || patientId.Value <= 0)
        {
            PatientAppointmentsHistory.Clear();
            return;
        }

        try
        {
            var all = await _inventoryService.GetAppointmentsAsync(DateTime.Today.AddMonths(-6), DateTime.Today.AddMonths(6));
            var patientList = all.Where(a => a.PatientRecordId == patientId.Value).OrderByDescending(a => a.Date).ToList();
            PatientAppointmentsHistory = new ObservableCollection<PatientAppointment>(patientList);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al cargar historial de citas del paciente", ex);
        }
    }

    /// <summary>Baja lógica: deja de aparecer en búsquedas; sus consultas y recetas se conservan.</summary>
    [RelayCommand]
    public async Task DeactivateSelectedPatientAsync()
    {
        var patient = _session.SelectedPatient;
        if (patient == null || _gate.BlockIfPatientHandedOff(patient)) return;
        if (_active.HasActiveConsultation && _active.ActiveConsultation?.PatientId == patient.Id)
        {
            _info.InfoModalMessage = "Termine o descarte la consulta en curso antes de dar de baja el expediente.";
            _info.IsInfoModalVisible = true;
            return;
        }
        if (!await _dialogs.ConfirmAsync($"¿Dar de baja el expediente de {patient.FullName}?\n\nDejará de aparecer en la búsqueda. Sus consultas y recetas se conservan.", "Dar de baja expediente")) return;

        try
        {
            if (_inventoryService != null) await _inventoryService.DeletePatientAsync(patient.Id);
        }
        catch (Exception ex)
        {
            _info.InfoModalMessage = $"No se pudo dar de baja el expediente: {ex.Message}";
            _info.IsInfoModalVisible = true;
            return;
        }

        _session.SelectedPatient = null;
        await _workflow.LoadPatientsAsync();
        _feedback.ShowSuccess($"Expediente de {patient.FullName} dado de baja");
        _nav.GoToPatientsHome();
    }
}
