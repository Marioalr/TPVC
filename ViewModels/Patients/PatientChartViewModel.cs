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
using TPVC.Services.Rules;

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
            var next = AgendaRules.NextAppointment(PatientAppointmentsHistory, ClinicClock.Today);
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
        Timeline = new ObservableCollection<TimelineEntry>(PatientChartRules.FilterTimeline(all, TimelineFilter, TimelineSearchText));
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

    /// <summary>Paso 8.26: la regla vive en <see cref="PatientChartRules.BuildAntecedentSections"/> (vectores <c>chart-antecedents.json</c>).</summary>
    public static List<AntecedentSection> BuildAntecedentSections(PatientRecord p) => PatientChartRules.BuildAntecedentSections(p);

    /// <summary>Paso 8.26: la regla vive en <see cref="PatientChartRules.BuildTimeline"/> (vectores <c>chart-timeline.json</c>).</summary>
    public static List<TimelineEntry> BuildTimeline(IEnumerable<PatientConsultation> consultations, IEnumerable<PatientAppointment> appointments) =>
        PatientChartRules.BuildTimeline(consultations, appointments);

    /// <summary>Paso 8.26: la regla vive en <see cref="PatientChartRules.BuildLatestVitals"/> (vectores <c>latest-vitals.json</c>).</summary>
    public static IReadOnlyList<VitalSummary> BuildLatestVitals(IEnumerable<VitalSignsEvolutionPoint>? points) => PatientChartRules.BuildLatestVitals(points);

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
