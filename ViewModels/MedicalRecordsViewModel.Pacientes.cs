using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
/// Apartado Pacientes (fase 2 del rediseño): inicio del consultorio, ficha, alta en pantalla y
/// agenda como panel. Un camino por tarea: el buscador abre la ficha, la ficha inicia la consulta.
/// </summary>
public partial class MedicalRecordsViewModel
{
    // Las páginas son de Navigation; los nombres siguen aquí para quien ya los usa
    public const string PageHome = PatientsNavigation.PageHome;
    public const string PageChart = PatientsNavigation.PageChart;
    public const string PageForm = PatientsNavigation.PageForm;
    public const string PageConsultation = PatientsNavigation.PageConsultation;

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private Action? _onAppointmentsChangedHandler;
    private int? _attendingAppointmentId;
    private ObservableCollection<PatientConsultation>? _observedHistory;
    private ObservableCollection<PatientAppointment>? _observedAppointments;

    private void InitializePacientes()
    {
        if (_inventoryService != null)
        {
            _onAppointmentsChangedHandler = () =>
            {
                try
                {
                    if (_ui.IsOffUiThread)
                    {
                        _ui.InvokeAsync(async () => await RefreshAppointmentViewsAsync());
                        return;
                    }
                }
                catch
                {
                    // Sin WPF (pruebas en Linux)
                }
                _ = RefreshAppointmentViewsAsync();
            };
            _inventoryService.OnAppointmentsChanged += _onAppointmentsChangedHandler;
        }
    }

    private void DisposePacientes()
    {
        if (_inventoryService != null && _onAppointmentsChangedHandler != null)
        {
            _inventoryService.OnAppointmentsChanged -= _onAppointmentsChangedHandler;
        }
    }

    // ================================================================== páginas

    /// <summary>Aviso "Consulta en curso · Volver" cuando se está fuera de la consulta.</summary>
    public bool ShowsConsultationBanner => Active.HasActiveConsultation && !Navigation.IsConsultationPage && !Navigation.IsFormPage;

    public string ConsultationBannerText => !Active.HasActiveConsultation
        ? string.Empty
        : IsConsultationHandedOff
            ? $"Consulta de {Active.ActiveConsultationPatientName} enviada a {HandoffTargetName}: esperando que la acepte"
            : $"Consulta en curso: {Active.ActiveConsultationPatientName} · {Active.ActiveConsultationElapsedTime}";

    public bool IsConsultationForSelectedPatient =>
        Active.HasActiveConsultation && Session.SelectedPatient != null && Active.ActiveConsultation?.PatientId == Session.SelectedPatient.Id;

    public string ChartPrimaryActionText => IsConsultationForSelectedPatient ? "Volver a la consulta" : "Iniciar consulta";

    private void NotifyPatientsPageState()
    {
        Navigation.RaisePageFlags();
        OnPropertyChanged(nameof(ShowsConsultationBanner));
        OnPropertyChanged(nameof(ConsultationBannerText));
        OnPropertyChanged(nameof(IsConsultationForSelectedPatient));
        OnPropertyChanged(nameof(ChartPrimaryActionText));
        OnPropertyChanged(nameof(CanAcceptIncomingHandoff));
        OnPropertyChanged(nameof(IncomingHandoffBlockedReason));
        OnPropertyChanged(nameof(ShowsIncomingHandoffNotice));
    }

    /// <summary>Al entrar desde el inicio de la app: a la consulta si hay una en curso, si no al inicio.</summary>
    public void EnterPatientsSection()
    {
        OnEnterPatientsSectionForHandoff();
        if (Active.HasActiveConsultation)
        {
            ShowConsultation();
        }
        else if (!Navigation.IsFormPage)
        {
            Navigation.GoToPatientsHome();
        }
    }

    [RelayCommand]
    public void OpenPatientChart(PatientRecord? patient)
    {
        if (patient == null) return;
        Home.HomeSearchText = string.Empty;
        Session.SelectPatient(patient);
        Navigation.PatientsPage = PageChart;
    }

    [RelayCommand]
    public void ShowConsultation()
    {
        if (!Active.HasActiveConsultation) return;
        var id = Active.ActiveConsultation!.PatientId;
        if (Session.SelectedPatient?.Id != id)
        {
            var patient = Session.Patients.FirstOrDefault(p => p.Id == id);
            if (patient != null) Session.SelectedPatient = patient;
        }
        Navigation.PatientsPage = PageConsultation;
    }

    /// <summary>Botón "Expediente" de la consulta: la ficha completa sin cerrar la consulta.</summary>
    [RelayCommand]
    public void ShowChartFromConsultation()
    {
        if (Session.SelectedPatient != null) Navigation.PatientsPage = PageChart;
    }

    [RelayCommand]
    public void ChartPrimaryAction()
    {
        if (IsConsultationForSelectedPatient)
        {
            ShowConsultation();
        }
        else if (Active.HasActiveConsultation)
        {
            Info.InfoModalMessage = IsConsultationHandedOff
                ? $"La consulta de {Active.ActiveConsultationPatientName} está enviada a {HandoffTargetName}. Recupérela o espere a que la acepte antes de iniciar otra."
                : $"Hay una consulta en curso con {Active.ActiveConsultationPatientName}. Termínela o descártela antes de iniciar otra.";
            Info.IsInfoModalVisible = true;
        }
        else
        {
            StartActiveConsultation();
        }
    }

    // ================================================================== inicio: Home

    [RelayCommand]
    public async Task CreatePatientFromSearchAsync()
    {
        var typed = Home.HomeSearchText.Trim();
        await PatientForm.NewPatientAsync();
        // Si se escribió un nombre (no un folio o teléfono), el alta empieza con él
        if (typed.Length > 0 && !typed.Any(char.IsDigit))
        {
            PatientFormViewModel.ParseFullName(typed, out var first, out var last, out var maternal);
            PatientForm.FormFirstName = ToTitle(first);
            PatientForm.FormLastName = ToTitle(last);
            PatientForm.FormMaternalLastName = ToTitle(maternal);
        }
        Home.HomeSearchText = string.Empty;
    }

    private static string ToTitle(string text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Mexican.TextInfo.ToTitleCase(text.Trim().ToLower(Mexican));

    private async Task RefreshAppointmentViewsAsync()
    {
        await Home.LoadTodayAppointmentsAsync();
        if (Agenda.IsAgendaPanelOpen) await Agenda.LoadAgendaPanelAsync();
        if (Session.SelectedPatient != null) await LoadPatientAppointmentsAsync(Session.SelectedPatient.Id);
    }

    /// <summary>"Atender" en una cita de hoy: abre la consulta con el motivo de la cita.</summary>
    [RelayCommand]
    public void AttendAppointment(PatientAppointment? appointment)
    {
        if (appointment == null) return;

        var patient = appointment.PatientRecordId > 0 ? Session.Patients.FirstOrDefault(p => p.Id == appointment.PatientRecordId) : null;
        if (patient == null)
        {
            // Cita sin expediente: se busca por nombre para abrirlo o crearlo
            Agenda.IsAgendaPanelOpen = false;
            Navigation.GoToPatientsHome();
            Home.HomeSearchText = appointment.PatientName;
            return;
        }

        if (Active.HasActiveConsultation)
        {
            if (Active.ActiveConsultation?.PatientId == patient.Id)
            {
                ShowConsultation();
            }
            else
            {
                Info.InfoModalMessage = $"Hay una consulta en curso con {Active.ActiveConsultationPatientName}. Termínela o descártela antes de atender otra cita.";
                Info.IsInfoModalVisible = true;
            }
            return;
        }

        Agenda.IsAgendaPanelOpen = false;
        Session.SelectPatient(patient);
        StartActiveConsultation();
        if (!string.IsNullOrWhiteSpace(appointment.Reason)) ConsultationChiefComplaint = appointment.Reason.Trim();
        _attendingAppointmentId = appointment.Id;
        // La copia pendiente se tomó antes de saber la cita: si el programa se cierra antes de la
        // siguiente edición, la consulta recuperada debe seguir ligada a ella (y marcarla "Atendida")
        ScheduleAutosave();
    }

    /// <summary>Al cobrar la consulta, la cita que se estaba atendiendo queda como atendida.</summary>
    private async Task CompleteAttendedAppointmentAsync()
    {
        if (_attendingAppointmentId is not int id || _inventoryService == null) return;
        _attendingAppointmentId = null;
        try
        {
            await _inventoryService.UpdateAppointmentStatusAsync(id, "Atendida");
            await RefreshAppointmentViewsAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo marcar la cita como atendida", ex);
        }
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

    public IReadOnlyList<string> TimelineFilters { get; } = new[] { "Todo", "Consultas", "Signos", "Recetas", "Citas" };

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
            var p = Session.SelectedPatient;
            if (p == null) return string.Empty;
            var parts = new List<string> { p.AgeDisplay };
            if (!string.IsNullOrWhiteSpace(p.Gender)) parts.Add(p.Gender);
            if (!string.IsNullOrWhiteSpace(p.RecordNumber)) parts.Add(p.RecordNumber);
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

    private void ObserveConsultationsHistory(ObservableCollection<PatientConsultation> history)
    {
        if (_observedHistory != null) _observedHistory.CollectionChanged -= OnChartSourceCollectionChanged;
        _observedHistory = history;
        if (_observedHistory != null) _observedHistory.CollectionChanged += OnChartSourceCollectionChanged;
        RefreshTimeline();
    }

    private void OnChartSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshTimeline();

    /// <summary>Se llama al cambiar de paciente: antecedentes, historial y resumen de la ficha.</summary>
    private void RefreshChartData()
    {
        var patient = Session.SelectedPatient;
        AntecedentSections = new ObservableCollection<AntecedentSection>(
            patient == null ? Enumerable.Empty<AntecedentSection>() : BuildAntecedentSections(patient));
        TimelineSearchText = string.Empty;
        IsVitalsChartVisible = false;
        RefreshTimeline();
        OnPropertyChanged(nameof(SelectedPatientMeta));
        NotifyPrescriptionStateChanged();
        OnPropertyChanged(nameof(IsConsultationForSelectedPatient));
        OnPropertyChanged(nameof(ChartPrimaryActionText));
    }

    private void RefreshTimeline()
    {
        var all = BuildTimeline(Session.ConsultationsHistory, PatientAppointmentsHistory);
        var kind = TimelineFilter switch
        {
            "Consultas" => TimelineKind.Consultation,
            "Signos" => TimelineKind.Vitals,
            "Recetas" => TimelineKind.Prescription,
            "Citas" => TimelineKind.Appointment,
            _ => (TimelineKind?)null
        };
        var query = TextSearch.Normalize(TimelineSearchText);
        Timeline = new ObservableCollection<TimelineEntry>(all
            .Where(e => kind == null || e.Kind == kind)
            .Where(e => TextSearch.Matches(query, e.Title, e.Detail)));
        OnPropertyChanged(nameof(HasTimeline));
        Preview.RaisePrescriptionIndications();
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

    /// <summary>"Editar esta sección": el alta abierta justo en esa sección.</summary>
    [RelayCommand]
    public void EditAntecedent(AntecedentSection? section)
    {
        if (BlockIfPatientHandedOff(Session.SelectedPatient)) return;
        PatientForm.EditPatient();
        if (section != null) PatientForm.FormOpenSection = section.Key;
    }

    [RelayCommand]
    public void ToggleTimelineEntry(TimelineEntry? entry)
    {
        if (entry != null) entry.IsExpanded = !entry.IsExpanded;
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

    /// <summary>Historial único de la ficha, del más reciente al más antiguo.</summary>
    public static List<TimelineEntry> BuildTimeline(IEnumerable<PatientConsultation> consultations, IEnumerable<PatientAppointment> appointments)
    {
        var entries = new List<(TimelineEntry Entry, int Order)>();
        foreach (var c in consultations)
        {
            var title = !string.IsNullOrWhiteSpace(c.Diagnosis) ? c.Diagnosis.Trim() : (string.IsNullOrWhiteSpace(c.ChiefComplaint) ? "Consulta" : c.ChiefComplaint.Trim());
            var detail = string.Join("\n", new[]
            {
                string.IsNullOrWhiteSpace(c.ChiefComplaint) ? null : $"Motivo: {c.ChiefComplaint.Trim()}",
                string.IsNullOrWhiteSpace(c.PhysicalExamination) ? null : $"Exploración: {c.PhysicalExamination.Trim()}",
                string.IsNullOrWhiteSpace(c.TreatmentPlan) ? null : $"Plan: {c.TreatmentPlan.Trim()}"
            }.Where(x => x != null));
            entries.Add((new TimelineEntry(TimelineKind.Consultation, c.Date, title, detail, c.Folio), 0));

            if (!string.IsNullOrWhiteSpace(c.VitalSigns))
            {
                entries.Add((new TimelineEntry(TimelineKind.Vitals, c.Date, "Signos vitales", c.VitalSigns.Replace(" | ", " · ")), 1));
            }

            var prescriptions = c.Prescriptions;
            if (prescriptions.Count > 0)
            {
                entries.Add((new TimelineEntry(TimelineKind.Prescription, c.Date,
                    string.Join(" · ", prescriptions.Select(r => r.MedicineName)),
                    string.Join("\n", prescriptions.Select(r => string.IsNullOrWhiteSpace(r.Dosage) ? r.MedicineName : $"{r.MedicineName}: {r.Dosage}"))), 2));
            }
        }

        foreach (var a in appointments)
        {
            var when = a.Date.Date + AgendaPanelViewModel.ParseAppointmentTime(a.Time);
            entries.Add((new TimelineEntry(TimelineKind.Appointment, when,
                string.IsNullOrWhiteSpace(a.Reason) ? "Cita" : a.Reason.Trim(),
                $"{a.Time} · {a.Status}"), 3));
        }

        return entries
            .OrderByDescending(x => x.Entry.Date.Date)
            .ThenBy(x => x.Order)
            .ThenByDescending(x => x.Entry.Date)
            .Select(x => x.Entry)
            .ToList();
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

    // ================================================================== agenda (panel lateral): Agenda

    private async Task RefreshAgendaAfterChangeAsync(int patientId)
    {
        await Agenda.LoadAgendaPanelAsync();
        await Home.LoadTodayAppointmentsAsync();
        if (Session.SelectedPatient != null && Session.SelectedPatient.Id == patientId) await LoadPatientAppointmentsAsync(patientId);
    }

    /// <summary>Baja lógica: deja de aparecer en búsquedas; sus consultas y recetas se conservan.</summary>
    [RelayCommand]
    public async Task DeactivateSelectedPatientAsync()
    {
        var patient = Session.SelectedPatient;
        if (patient == null || BlockIfPatientHandedOff(patient)) return;
        if (Active.HasActiveConsultation && Active.ActiveConsultation?.PatientId == patient.Id)
        {
            Info.InfoModalMessage = "Termine o descarte la consulta en curso antes de dar de baja el expediente.";
            Info.IsInfoModalVisible = true;
            return;
        }
        if (!await _dialogs.ConfirmAsync($"¿Dar de baja el expediente de {patient.FullName}?\n\nDejará de aparecer en la búsqueda. Sus consultas y recetas se conservan.", "Dar de baja expediente")) return;

        try
        {
            if (_inventoryService != null) await _inventoryService.DeletePatientAsync(patient.Id);
        }
        catch (Exception ex)
        {
            Info.InfoModalMessage = $"No se pudo dar de baja el expediente: {ex.Message}";
            Info.IsInfoModalVisible = true;
            return;
        }

        Session.SelectedPatient = null;
        await LoadPatientsAsync();
        Feedback.ShowSuccess($"Expediente de {patient.FullName} dado de baja");
        Navigation.GoToPatientsHome();
    }
}
