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

namespace TPVC.ViewModels;

/// <summary>
/// Apartado Pacientes (fase 2 del rediseño): inicio del consultorio, ficha, alta en pantalla y
/// agenda como panel. Un camino por tarea: el buscador abre la ficha, la ficha inicia la consulta.
/// </summary>
public partial class MedicalRecordsViewModel
{
    public const string PageHome = "Home";
    public const string PageChart = "Chart";
    public const string PageForm = "Form";
    public const string PageConsultation = "Consultation";

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private NotificationService? _notifications;
    private Action? _onAppointmentsChangedHandler;
    private string _pageBeforeForm = PageHome;
    private int? _attendingAppointmentId;
    private List<PatientAppointment> _agendaWeekAppointments = new();
    private ObservableCollection<PatientConsultation>? _observedHistory;
    private ObservableCollection<PatientAppointment>? _observedAppointments;

    /// <summary>La vista enfoca el buscador (al volver al inicio o al terminar una consulta).</summary>
    public event Action? FocusSearchRequested;

    /// <summary>Abre enlaces (WhatsApp, Google Calendar); las pruebas lo reemplazan.</summary>
    public Func<string, bool>? OpenUrl { get; set; }

    private void InitializePacientes(NotificationService? notifications)
    {
        _notifications = notifications;
        PropertyChanged += OnPropertyChangedForPacientes;

        if (_inventoryService != null)
        {
            _onAppointmentsChangedHandler = () =>
            {
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                    {
                        app.Dispatcher.InvokeAsync(async () => await RefreshAppointmentViewsAsync());
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

    /// <summary>Problema que no impide seguir (p. ej. falta de existencias al cobrar).</summary>
    private void ShowWarning(string message)
    {
        if (_notifications != null)
        {
            _notifications.Warning(message);
            return;
        }
        InfoModalMessage = message;
        IsInfoModalVisible = true;
    }

    /// <summary>Confirmación breve que se va sola; sin servicio de avisos (pruebas) usa la ventana.</summary>
    private void ShowSuccess(string message)
    {
        if (_notifications != null)
        {
            var firstLine = message.Split('\n')[0].Trim().TrimStart('¡').TrimEnd('!');
            _notifications.Success(firstLine);
            return;
        }
        InfoModalMessage = message;
        IsInfoModalVisible = true;
    }

    private void OnPropertyChangedForPacientes(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        if (name == null) return;

        // La barra lateral del alta refleja lo que se va capturando
        if (name.StartsWith("Form", StringComparison.Ordinal) && name != nameof(FormSections) && name != nameof(FormPageTitle))
        {
            OnPropertyChanged(nameof(FormSections));
            if (name is nameof(FormFirstName) or nameof(FormLastName) or nameof(FormMaternalLastName) or nameof(FormPatientId))
            {
                OnPropertyChanged(nameof(FormPageTitle));
            }
        }
    }

    // ================================================================== páginas

    [ObservableProperty]
    private string _patientsPage = PageHome;

    partial void OnPatientsPageChanged(string value) => NotifyPatientsPageState();

    public bool IsHomePage => PatientsPage == PageHome;
    public bool IsChartPage => PatientsPage == PageChart;
    public bool IsFormPage => PatientsPage == PageForm;
    public bool IsConsultationPage => PatientsPage == PageConsultation;

    /// <summary>Barra con buscador, agenda y nuevo paciente (inicio y ficha).</summary>
    public bool ShowsPatientsTopBar => IsHomePage || IsChartPage;

    /// <summary>Aviso "Consulta en curso · Volver" cuando se está fuera de la consulta.</summary>
    public bool ShowsConsultationBanner => HasActiveConsultation && !IsConsultationPage && !IsFormPage;

    public string ConsultationBannerText => !HasActiveConsultation
        ? string.Empty
        : IsConsultationHandedOff
            ? $"Consulta de {ActiveConsultationPatientName} enviada a {HandoffTargetName}: esperando que la acepte"
            : $"Consulta en curso: {ActiveConsultationPatientName} · {ActiveConsultationElapsedTime}";

    partial void OnActiveConsultationElapsedTimeChanged(string value) => OnPropertyChanged(nameof(ConsultationBannerText));

    public bool IsConsultationForSelectedPatient =>
        HasActiveConsultation && SelectedPatient != null && ActiveConsultation?.PatientId == SelectedPatient.Id;

    public string ChartPrimaryActionText => IsConsultationForSelectedPatient ? "Volver a la consulta" : "Iniciar consulta";

    private void NotifyPatientsPageState()
    {
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsChartPage));
        OnPropertyChanged(nameof(IsFormPage));
        OnPropertyChanged(nameof(IsConsultationPage));
        OnPropertyChanged(nameof(ShowsPatientsTopBar));
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
        if (HasActiveConsultation)
        {
            ShowConsultation();
        }
        else if (!IsFormPage)
        {
            GoToPatientsHome();
        }
    }

    [RelayCommand]
    public void GoToPatientsHome()
    {
        PatientsPage = PageHome;
        FocusSearchRequested?.Invoke();
    }

    [RelayCommand]
    public void OpenPatientChart(PatientRecord? patient)
    {
        if (patient == null) return;
        HomeSearchText = string.Empty;
        SelectPatient(patient);
        PatientsPage = PageChart;
    }

    [RelayCommand]
    public void ShowConsultation()
    {
        if (!HasActiveConsultation) return;
        var id = ActiveConsultation!.PatientId;
        if (SelectedPatient?.Id != id)
        {
            var patient = Patients.FirstOrDefault(p => p.Id == id);
            if (patient != null) SelectedPatient = patient;
        }
        PatientsPage = PageConsultation;
    }

    /// <summary>Botón "Expediente" de la consulta: la ficha completa sin cerrar la consulta.</summary>
    [RelayCommand]
    public void ShowChartFromConsultation()
    {
        if (SelectedPatient != null) PatientsPage = PageChart;
    }

    [RelayCommand]
    public void ChartPrimaryAction()
    {
        if (IsConsultationForSelectedPatient)
        {
            ShowConsultation();
        }
        else if (HasActiveConsultation)
        {
            InfoModalMessage = IsConsultationHandedOff
                ? $"La consulta de {ActiveConsultationPatientName} está enviada a {HandoffTargetName}. Recupérela o espere a que la acepte antes de iniciar otra."
                : $"Hay una consulta en curso con {ActiveConsultationPatientName}. Termínela o descártela antes de iniciar otra.";
            IsInfoModalVisible = true;
        }
        else
        {
            StartActiveConsultation();
        }
    }

    partial void OnIsPatientModalOpenChanged(bool value)
    {
        if (value)
        {
            if (!IsFormPage) _pageBeforeForm = PatientsPage;
            if (string.IsNullOrEmpty(FormOpenSection)) FormOpenSection = "Identificacion";
            PatientsPage = PageForm;
            OnPatientFormOpenedForDraft();
            return;
        }

        OnPatientFormClosedForDraft();
        if (!IsFormPage) return;
        // De vuelta a la consulta con su paciente (el formulario pudo ser de otro expediente)
        if (_pageBeforeForm == PageConsultation && HasActiveConsultation) ShowConsultation();
        else if (SelectedPatient != null) PatientsPage = PageChart;
        else GoToPatientsHome();
    }

    // ================================================================== inicio

    [ObservableProperty]
    private string _homeSearchText = string.Empty;

    partial void OnHomeSearchTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && IsChartPage) PatientsPage = PageHome;
        RefreshHomeResults();
    }

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _homeResults = new();

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _recentPatients = new();

    public bool HasHomeQuery => !string.IsNullOrWhiteSpace(HomeSearchText);
    public bool HasNoHomeResults => HasHomeQuery && HomeResults.Count == 0;
    public string HomeResultsSummary => HomeResults.Count == 1 ? "1 paciente" : $"{HomeResults.Count} pacientes";
    public string CreateFromSearchText => $"Crear paciente \"{HomeSearchText.Trim()}\"";

    partial void OnPatientsChanged(ObservableCollection<PatientRecord> value)
    {
        RefreshRecentPatients();
        RefreshHomeResults();
    }

    /// <summary>
    /// Pacientes que coinciden con lo escrito (sin acentos): primero los que empiezan así, luego
    /// los que tienen una palabra que empieza así y al final los que lo contienen en nombre, folio,
    /// teléfono o diagnóstico.
    /// </summary>
    public static List<PatientRecord> RankPatients(IEnumerable<PatientRecord> patients, string? query, int limit = 30)
    {
        var q = TextSearch.Normalize(query);
        if (q.Length == 0) return new List<PatientRecord>();
        var digits = new string(q.Where(char.IsDigit).ToArray());

        return patients
            .Select(p =>
            {
                var name = TextSearch.Normalize(p.FullName);
                var other = TextSearch.Normalize($"{p.RecordNumber} {p.LastDiagnosis}");
                int rank;
                if (name.StartsWith(q, StringComparison.Ordinal)) rank = 0;
                else if ((" " + name).Contains(" " + q, StringComparison.Ordinal)) rank = 1;
                else if (name.Contains(q, StringComparison.Ordinal) || other.Contains(q, StringComparison.Ordinal)) rank = 2;
                else if (digits.Length >= 4 && new string((p.Phone ?? string.Empty).Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)) rank = 2;
                else rank = -1;
                return (Patient: p, Rank: rank);
            })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Patient.FullName, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(x => x.Patient)
            .ToList();
    }

    private void RefreshHomeResults()
    {
        HomeResults = new ObservableCollection<PatientRecord>(RankPatients(Patients, HomeSearchText));
        OnPropertyChanged(nameof(HasHomeQuery));
        OnPropertyChanged(nameof(HasNoHomeResults));
        OnPropertyChanged(nameof(HomeResultsSummary));
        OnPropertyChanged(nameof(CreateFromSearchText));
    }

    private void RefreshRecentPatients()
    {
        RecentPatients = new ObservableCollection<PatientRecord>(
            Patients.OrderByDescending(p => p.LastVisit).ThenBy(p => p.FullName, StringComparer.CurrentCulture).Take(12));
    }

    [RelayCommand]
    public void OpenFirstHomeResult() => OpenPatientChart(HomeResults.FirstOrDefault());

    [RelayCommand]
    public async Task CreatePatientFromSearchAsync()
    {
        var typed = HomeSearchText.Trim();
        await NewPatientAsync();
        // Si se escribió un nombre (no un folio o teléfono), el alta empieza con él
        if (typed.Length > 0 && !typed.Any(char.IsDigit))
        {
            ParseFullName(typed, out var first, out var last, out var maternal);
            FormFirstName = ToTitle(first);
            FormLastName = ToTitle(last);
            FormMaternalLastName = ToTitle(maternal);
        }
        HomeSearchText = string.Empty;
    }

    private static string ToTitle(string text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Mexican.TextInfo.ToTitleCase(text.Trim().ToLower(Mexican));

    [ObservableProperty]
    private ObservableCollection<PatientAppointment> _todayAppointments = new();

    public bool HasTodayAppointments => TodayAppointments.Count > 0;

    public string TodayTitle
    {
        get
        {
            var text = DateTime.Today.ToString("dddd d 'de' MMMM", Mexican);
            return "Hoy · " + text;
        }
    }

    partial void OnTodayAppointmentsChanged(ObservableCollection<PatientAppointment> value) =>
        OnPropertyChanged(nameof(HasTodayAppointments));

    public async Task LoadTodayAppointmentsAsync()
    {
        if (_inventoryService == null) return;
        try
        {
            var list = await _inventoryService.GetAppointmentsByDayAsync(DateTime.Today);
            TodayAppointments = new ObservableCollection<PatientAppointment>(
                list.Where(a => !a.IsCancelled).OrderBy(a => ParseAppointmentTime(a.Time)));
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron cargar las citas de hoy", ex);
        }
    }

    private async Task RefreshAppointmentViewsAsync()
    {
        await LoadTodayAppointmentsAsync();
        if (IsAgendaPanelOpen) await LoadAgendaPanelAsync();
        if (SelectedPatient != null) await LoadPatientAppointmentsAsync(SelectedPatient.Id);
    }

    public static TimeSpan ParseAppointmentTime(string? time)
    {
        if (!string.IsNullOrWhiteSpace(time))
        {
            if (DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)) return parsed.TimeOfDay;
            if (DateTime.TryParse(time, Mexican, DateTimeStyles.NoCurrentDateDefault, out parsed)) return parsed.TimeOfDay;
        }
        return new TimeSpan(10, 0, 0);
    }

    /// <summary>"Atender" en una cita de hoy: abre la consulta con el motivo de la cita.</summary>
    [RelayCommand]
    public void AttendAppointment(PatientAppointment? appointment)
    {
        if (appointment == null) return;

        var patient = appointment.PatientRecordId > 0 ? Patients.FirstOrDefault(p => p.Id == appointment.PatientRecordId) : null;
        if (patient == null)
        {
            // Cita sin expediente: se busca por nombre para abrirlo o crearlo
            IsAgendaPanelOpen = false;
            GoToPatientsHome();
            HomeSearchText = appointment.PatientName;
            return;
        }

        if (HasActiveConsultation)
        {
            if (ActiveConsultation?.PatientId == patient.Id)
            {
                ShowConsultation();
            }
            else
            {
                InfoModalMessage = $"Hay una consulta en curso con {ActiveConsultationPatientName}. Termínela o descártela antes de atender otra cita.";
                IsInfoModalVisible = true;
            }
            return;
        }

        IsAgendaPanelOpen = false;
        SelectPatient(patient);
        StartActiveConsultation();
        if (!string.IsNullOrWhiteSpace(appointment.Reason)) ConsultationChiefComplaint = appointment.Reason.Trim();
        _attendingAppointmentId = appointment.Id;
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
                .OrderBy(a => a.Date.Date + ParseAppointmentTime(a.Time))
                .FirstOrDefault();
            return next == null ? string.Empty : $"Próxima cita: {next.Date.ToString("ddd d MMM", Mexican)}, {next.Time}";
        }
    }

    public bool HasNextAppointment => !string.IsNullOrEmpty(NextAppointmentText);

    public string SelectedPatientMeta
    {
        get
        {
            var p = SelectedPatient;
            if (p == null) return string.Empty;
            var parts = new List<string> { p.AgeDisplay };
            if (!string.IsNullOrWhiteSpace(p.Gender)) parts.Add(p.Gender);
            if (!string.IsNullOrWhiteSpace(p.RecordNumber)) parts.Add(p.RecordNumber);
            if (!string.IsNullOrWhiteSpace(p.BloodType) && p.BloodType != BloodTypeUnknown) parts.Add(p.BloodType);
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

    partial void OnVitalSignsEvolutionPointsChanged(ObservableCollection<VitalSignsEvolutionPoint> value)
    {
        LatestVitals = BuildLatestVitals(value);
        OnPropertyChanged(nameof(HasLatestVitals));
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
        var patient = SelectedPatient;
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
        var all = BuildTimeline(ConsultationsHistory, PatientAppointmentsHistory);
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
        if (BlockIfPatientHandedOff(SelectedPatient)) return;
        EditPatient();
        if (section != null) FormOpenSection = section.Key;
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
            var when = a.Date.Date + ParseAppointmentTime(a.Time);
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
            bool up = false, down = false;
            if (withValue.Count > 1)
            {
                var previous = value(withValue[^2])!.Value;
                var diff = latest - previous;
                if (diff > 0) { up = true; trend = $"↑ {format(diff)}"; }
                else if (diff < 0) { down = true; trend = $"↓ {format(-diff)}"; }
                else trend = "sin cambio";
            }
            return new VitalSummary($"{label}{unit}", format(latest), trend, up, down);
        }

        var result = new List<VitalSummary>();
        var last = list.LastOrDefault(p => p.Systolic.HasValue && p.Diastolic.HasValue);
        if (last != null)
        {
            var prev = list.Where(p => p.Systolic.HasValue && p.Diastolic.HasValue && p != last).LastOrDefault();
            bool up = prev != null && last.Systolic > prev.Systolic, down = prev != null && last.Systolic < prev.Systolic;
            // La flecha dice si subió o bajó sin depender del color (en oscuro el ámbar se confunde con el texto)
            var trend = prev == null ? string.Empty : $"{(up ? "↑ " : down ? "↓ " : string.Empty)}antes {prev.BloodPressureText}";
            result.Add(new VitalSummary("T/A (mmHg)", last.BloodPressureText, trend, up, down));
        }
        if (Summary("Peso", p => p.Weight, v => v.ToString("0.0", CultureInfo.InvariantCulture), " (kg)") is { } weight) result.Add(weight);
        if (Summary("IMC", p => p.BMI, v => v.ToString("0.0", CultureInfo.InvariantCulture), string.Empty) is { } bmi) result.Add(bmi);
        if (Summary("Glucosa", p => p.Glucose, v => v.ToString("0", CultureInfo.InvariantCulture), " (mg/dL)") is { } glucose) result.Add(glucose);
        return result;
    }

    // ================================================================== alta y edición

    [ObservableProperty]
    private string _formOpenSection = "Identificacion";

    [RelayCommand]
    public void OpenFormSection(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) FormOpenSection = key;
    }

    public bool IsFormNew => FormPatientId == 0;

    public string FormPageTitle
    {
        get
        {
            if (IsFormNew) return "Nuevo paciente";
            var name = string.Join(" ", new[] { FormFirstName, FormLastName, FormMaternalLastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return string.IsNullOrWhiteSpace(name) ? "Editar expediente" : $"Editar: {name}";
        }
    }

    public IReadOnlyList<PatientFormSection> FormSections
    {
        get
        {
            var list = new List<PatientFormSection>();
            int number = 1;
            void Add(string key, string title, bool required, bool complete, bool applicable = true)
            {
                if (!applicable) return;
                var status = required ? (complete ? "Completa" : "Obligatoria") : (complete ? "Con datos" : "Opcional");
                list.Add(new PatientFormSection(key, (number++).ToString(CultureInfo.InvariantCulture), title, status, complete, required, FormOpenSection == key));
            }

            static bool Any(params string?[] values) => values.Any(v => !string.IsNullOrWhiteSpace(v));

            Add("Identificacion", "Identificación", true,
                Any(FormFirstName) && Any(FormLastName) && FormHasAge && Any(FormGender));
            Add("Alergias", "Alergias y crónicos", true, Any(FormAllergies));
            Add("Contacto", "Contacto y emergencia", false, Any(FormPhone, FormEmail, FormAddress, FormEmergencyContactName));
            Add("Heredofamiliares", "Heredofamiliares", false, Any(FormFamilyHistory));
            Add("Patologicos", "Personales patológicos", false, Any(FormSurgeries, FormHospitalizations, FormTransfusions, FormImmunizationSchedule));
            Add("Gineco", "Gineco-obstétricos", false,
                Any(FormMenarcheAge, FormSexualOnsetAge, FormPeriodDuration, FormPapSmear) || FormPregnanciesCount > 0, FormShowsGyneco);
            Add("Desarrollo", "Desarrollo", false, Any(FormDevelopmentalMilestones), FormIsPediatric);
            Add("NoPatologicos", "No patológicos", false, Any(FormDiet, FormHousingConditions, FormZoonosis, FormOccupation));
            Add("Notas", "Notas de ingreso", false, Any(FormClinicalNotes));
            return list;
        }
    }

    public string FormProgressText
    {
        get
        {
            var sections = FormSections;
            var required = sections.Where(s => s.IsRequired).ToList();
            var missing = required.Count(s => !s.IsComplete);
            return missing == 0
                ? $"{sections.Count(s => s.IsComplete)} de {sections.Count} secciones · lo obligatorio está completo"
                : $"Faltan {missing} sección(es) obligatoria(s)";
        }
    }

    partial void OnFormOpenSectionChanged(string value) => OnPropertyChanged(nameof(FormSections));

    /// <summary>Un error de validación abre la sección donde está el dato que falta.</summary>
    partial void OnFormErrorMessageChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = value.ToLowerInvariant();
        if (text.Contains("alergia")) FormOpenSection = "Alergias";
        else if (text.Contains("correo")) FormOpenSection = "Contacto";
        else FormOpenSection = "Identificacion";
    }

    [RelayCommand]
    public async Task SavePatientAndStartConsultationAsync()
    {
        await SavePatientModalAsync();
        if (!IsPatientModalOpen && FormErrorMessage == null && SelectedPatient != null && !HasActiveConsultation)
        {
            StartActiveConsultation();
        }
    }

    [RelayCommand]
    public void CancelPatientForm()
    {
        if (IsFormNew && (!string.IsNullOrWhiteSpace(FormFirstName) || !string.IsNullOrWhiteSpace(FormLastName))
            && !DialogService.Confirm("¿Salir sin guardar el paciente nuevo?\n\nLo capturado se perderá.", "Salir del alta"))
        {
            return;
        }
        ClosePatientModal();
    }

    // ================================================================== agenda (panel lateral)

    public static readonly TimeSpan AgendaDayStart = new(8, 0, 0);
    public static readonly TimeSpan AgendaDayEnd = new(20, 0, 0);
    public static readonly TimeSpan AgendaSlotLength = TimeSpan.FromMinutes(30);

    [ObservableProperty]
    private bool _isAgendaPanelOpen;

    [ObservableProperty]
    private DateTime _agendaPanelDate = DateTime.Today;

    [ObservableProperty]
    private ObservableCollection<AgendaDayItem> _agendaWeek = new();

    [ObservableProperty]
    private ObservableCollection<AgendaSlot> _agendaSlots = new();

    [ObservableProperty]
    private PatientRecord? _agendaFormPatient;

    [ObservableProperty]
    private string _agendaFormReason = string.Empty;

    [ObservableProperty]
    private int _agendaFormDuration = 30;

    [ObservableProperty]
    private string? _agendaFormError;

    /// <summary>Cita que se está cambiando de horario: el siguiente hueco elegido la recibe.</summary>
    [ObservableProperty]
    private PatientAppointment? _movingAppointment;

    public IReadOnlyList<int> AgendaDurations { get; } = new[] { 15, 30, 45, 60 };

    public IReadOnlyList<string> AgendaReasonPresets { get; } = new[]
    {
        "Consulta de seguimiento", "Primera vez", "Control", "Resultados de laboratorio", "Revisión de niño sano", "Aplicación de vacuna"
    };

    public string AgendaPanelTitle
    {
        get
        {
            var text = AgendaPanelDate.ToString("dddd d 'de' MMMM", Mexican);
            return char.ToUpper(text[0], Mexican) + text[1..];
        }
    }

    public string AgendaWeekTitle
    {
        get
        {
            var monday = StartOfWeek(AgendaPanelDate);
            var sunday = monday.AddDays(6);
            return monday.Month == sunday.Month
                ? $"{monday.Day} – {sunday.Day} de {sunday.ToString("MMMM yyyy", Mexican)}"
                : $"{monday.ToString("d MMM", Mexican)} – {sunday.ToString("d MMM yyyy", Mexican)}";
        }
    }

    public string AgendaDaySummary
    {
        get
        {
            int appointments = AgendaSlots.Count(s => s.HasAppointment);
            int free = AgendaSlots.Count(s => s.IsFree);
            return $"{appointments} {(appointments == 1 ? "cita" : "citas")} · {free} {(free == 1 ? "hueco libre" : "huecos libres")}";
        }
    }

    public bool IsMovingAppointment => MovingAppointment != null;
    public string MovingAppointmentText => MovingAppointment == null ? string.Empty : $"Elija el nuevo horario para {MovingAppointment.PatientName}";

    partial void OnAgendaPanelDateChanged(DateTime value)
    {
        OnPropertyChanged(nameof(AgendaPanelTitle));
        OnPropertyChanged(nameof(AgendaWeekTitle));
    }

    partial void OnMovingAppointmentChanged(PatientAppointment? value)
    {
        OnPropertyChanged(nameof(IsMovingAppointment));
        OnPropertyChanged(nameof(MovingAppointmentText));
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }

    /// <summary>El panel siempre abre en hoy (la aplicación puede quedar abierta de un día a otro).</summary>
    [RelayCommand]
    public Task OpenAgendaPanelAsync() => ShowAgendaPanelAsync(DateTime.Today);

    private async Task ShowAgendaPanelAsync(DateTime date)
    {
        AgendaPanelDate = date.Date;
        IsAgendaPanelOpen = true;
        await LoadAgendaPanelAsync();
    }

    [RelayCommand]
    public async Task ToggleAgendaPanelAsync()
    {
        if (IsAgendaPanelOpen) CloseAgendaPanel();
        else await OpenAgendaPanelAsync();
    }

    [RelayCommand]
    public void CloseAgendaPanel()
    {
        IsAgendaPanelOpen = false;
        MovingAppointment = null;
        CloseAgendaForm();
    }

    [RelayCommand]
    public async Task SelectAgendaDayAsync(AgendaDayItem? day)
    {
        if (day == null) return;
        AgendaPanelDate = day.Date;
        foreach (var d in AgendaWeek) d.IsSelected = d.Date == day.Date;
        BuildAgendaSlots();
        await Task.CompletedTask;
    }

    [RelayCommand]
    public async Task AgendaPanelPreviousWeekAsync()
    {
        AgendaPanelDate = AgendaPanelDate.AddDays(-7);
        await LoadAgendaPanelAsync();
    }

    [RelayCommand]
    public async Task AgendaPanelNextWeekAsync()
    {
        AgendaPanelDate = AgendaPanelDate.AddDays(7);
        await LoadAgendaPanelAsync();
    }

    [RelayCommand]
    public async Task AgendaPanelTodayAsync()
    {
        AgendaPanelDate = DateTime.Today;
        await LoadAgendaPanelAsync();
    }

    public async Task LoadAgendaPanelAsync()
    {
        var monday = StartOfWeek(AgendaPanelDate);
        try
        {
            _agendaWeekAppointments = _inventoryService == null
                ? new List<PatientAppointment>()
                : await _inventoryService.GetAppointmentsByWeekAsync(monday);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo cargar la agenda", ex);
            _agendaWeekAppointments = new List<PatientAppointment>();
        }

        AgendaWeek = new ObservableCollection<AgendaDayItem>(Enumerable.Range(0, 7).Select(i =>
        {
            var date = monday.AddDays(i);
            var count = _agendaWeekAppointments.Count(a => a.Date.Date == date && !a.IsCancelled);
            return new AgendaDayItem(date, count) { IsSelected = date == AgendaPanelDate.Date };
        }));
        BuildAgendaSlots();
    }

    private void BuildAgendaSlots()
    {
        CloseAgendaForm();
        AgendaSlots = new ObservableCollection<AgendaSlot>(BuildSlots(AgendaPanelDate, _agendaWeekAppointments, DateTime.Now));
        OnPropertyChanged(nameof(AgendaDaySummary));
    }

    /// <summary>
    /// Citas del día y huecos libres de 30 minutos entre las 8:00 y las 20:00. Un hueco ocupado por
    /// una cita (según su duración) no se ofrece; en el día de hoy no se ofrecen horas pasadas.
    /// </summary>
    public static List<AgendaSlot> BuildSlots(DateTime date, IEnumerable<PatientAppointment> appointments, DateTime now)
    {
        var dayAppointments = appointments
            .Where(a => a.Date.Date == date.Date && !a.IsCancelled)
            .Select(a => (Appointment: a, Start: ParseAppointmentTime(a.Time), Minutes: a.DurationMinutes > 0 ? a.DurationMinutes : 30))
            .OrderBy(a => a.Start)
            .ToList();

        var slots = dayAppointments.Select(a => new AgendaSlot(a.Start, a.Appointment)).ToList();

        for (var t = AgendaDayStart; t < AgendaDayEnd; t += AgendaSlotLength)
        {
            if (date.Date == now.Date && date.Date + t < now) continue;
            var slotEnd = t + AgendaSlotLength;
            bool busy = dayAppointments.Any(a => a.Start < slotEnd && t < a.Start + TimeSpan.FromMinutes(a.Minutes));
            if (!busy) slots.Add(new AgendaSlot(t, null));
        }

        return slots.OrderBy(s => s.Time).ThenBy(s => s.IsFree).ToList();
    }

    [RelayCommand]
    public async Task OpenAgendaSlotAsync(AgendaSlot? slot)
    {
        if (slot == null || slot.HasAppointment) return;

        if (MovingAppointment is { } moving)
        {
            await MoveAppointmentAsync(moving, AgendaPanelDate.Date, slot.TimeText);
            return;
        }

        CloseAgendaForm();
        slot.IsFormOpen = true;
        AgendaFormPatient = SelectedPatient;
        AgendaFormReason = string.Empty;
        AgendaFormDuration = 30;
        AgendaFormError = null;
    }

    [RelayCommand]
    public void SetAgendaReason(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)) AgendaFormReason = reason;
    }

    [RelayCommand]
    public void SetAgendaDuration(int minutes)
    {
        if (minutes > 0) AgendaFormDuration = minutes;
    }

    [RelayCommand]
    public void CloseAgendaForm()
    {
        foreach (var s in AgendaSlots) s.IsFormOpen = false;
        AgendaFormError = null;
    }

    [RelayCommand]
    public async Task SaveAgendaFormAsync()
    {
        var slot = AgendaSlots.FirstOrDefault(s => s.IsFormOpen);
        if (slot == null) return;

        var patient = AgendaFormPatient;
        if (patient == null)
        {
            AgendaFormError = "Elija el paciente (escriba su nombre en la lista).";
            return;
        }
        if (string.IsNullOrWhiteSpace(AgendaFormReason))
        {
            AgendaFormError = "Escriba el motivo de la cita o elija uno frecuente.";
            return;
        }

        var appointment = new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            PatientRecordNumber = patient.RecordNumber,
            PatientPhone = patient.Phone ?? string.Empty,
            Date = AgendaPanelDate.Date,
            Time = slot.TimeText,
            DurationMinutes = AgendaFormDuration > 0 ? AgendaFormDuration : 30,
            Reason = AgendaFormReason.Trim(),
            Status = "Programada"
        };

        try
        {
            if (_inventoryService != null) await _inventoryService.SaveAppointmentAsync(appointment);
            else _agendaWeekAppointments.Add(appointment);
        }
        catch (Exception ex)
        {
            AgendaFormError = $"No se pudo guardar la cita: {ex.Message}";
            return;
        }

        ShowSuccess($"Cita agendada: {patient.FullName}, {appointment.Date.ToString("ddd d MMM", Mexican)} a las {appointment.Time}");
        await RefreshAgendaAfterChangeAsync(patient.Id);
    }

    private async Task MoveAppointmentAsync(PatientAppointment appointment, DateTime date, string time)
    {
        var moved = new PatientAppointment
        {
            Id = appointment.Id,
            SyncId = appointment.SyncId,
            PatientRecordId = appointment.PatientRecordId,
            PatientName = appointment.PatientName,
            PatientRecordNumber = appointment.PatientRecordNumber,
            PatientPhone = appointment.PatientPhone,
            Date = date,
            Time = time,
            DurationMinutes = appointment.DurationMinutes,
            Reason = appointment.Reason,
            Notes = appointment.Notes,
            Status = "Programada",
            CreatedAt = appointment.CreatedAt
        };

        try
        {
            if (_inventoryService != null) await _inventoryService.SaveAppointmentAsync(moved);
        }
        catch (Exception ex)
        {
            InfoModalMessage = $"No se pudo cambiar la cita: {ex.Message}";
            IsInfoModalVisible = true;
            return;
        }

        MovingAppointment = null;
        ShowSuccess($"Cita de {moved.PatientName} cambiada al {date.ToString("ddd d MMM", Mexican)} a las {time}");
        await RefreshAgendaAfterChangeAsync(moved.PatientRecordId);
    }

    private async Task RefreshAgendaAfterChangeAsync(int patientId)
    {
        await LoadAgendaPanelAsync();
        await LoadTodayAppointmentsAsync();
        if (SelectedPatient != null && SelectedPatient.Id == patientId) await LoadPatientAppointmentsAsync(patientId);
    }

    [RelayCommand]
    public void StartMovingAppointment(PatientAppointment? appointment)
    {
        if (appointment == null) return;
        CloseAgendaForm();
        MovingAppointment = appointment;
    }

    [RelayCommand]
    public void CancelMovingAppointment() => MovingAppointment = null;

    [RelayCommand]
    public async Task ConfirmAppointmentAsync(PatientAppointment? appointment)
    {
        if (appointment == null) return;
        await UpdateAppointmentStatusAsync(appointment, "Confirmada");
        await RefreshAgendaAfterChangeAsync(appointment.PatientRecordId);
    }

    [RelayCommand]
    public async Task CancelAppointmentAsync(PatientAppointment? appointment)
    {
        if (appointment == null) return;
        if (!DialogService.Confirm($"¿Cancelar la cita de {appointment.PatientName} ({appointment.Date:dd/MM} {appointment.Time})?", "Cancelar cita")) return;
        await UpdateAppointmentStatusAsync(appointment, "Cancelada");
        ShowSuccess($"Cita de {appointment.PatientName} cancelada");
        await RefreshAgendaAfterChangeAsync(appointment.PatientRecordId);
    }

    [RelayCommand]
    public void RemindByWhatsApp(PatientAppointment? appointment)
    {
        if (appointment == null) return;
        var phone = new string((appointment.PatientPhone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (phone.Length < 10)
        {
            InfoModalMessage = $"La cita de {appointment.PatientName} no tiene un teléfono de 10 dígitos. Agréguelo en su expediente.";
            IsInfoModalVisible = true;
            return;
        }
        if (phone.Length == 10) phone = "52" + phone;

        var message = $"Hola {appointment.PatientName}, le recordamos su cita en {ClinicName} el {appointment.Date.ToString("dddd d 'de' MMMM", Mexican)} a las {appointment.Time}. Por favor confirme su asistencia respondiendo a este mensaje.";
        LaunchUrl($"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}");
    }

    [RelayCommand]
    public void AddToGoogleCalendar(PatientAppointment? appointment)
    {
        if (appointment == null) return;
        var start = appointment.Date.Date + ParseAppointmentTime(appointment.Time);
        var end = start.AddMinutes(appointment.DurationMinutes > 0 ? appointment.DurationMinutes : 30);
        var title = Uri.EscapeDataString($"Cita: {appointment.PatientName}");
        var details = Uri.EscapeDataString($"Motivo: {appointment.Reason}\nTeléfono: {appointment.PatientPhone}");
        var location = Uri.EscapeDataString(string.IsNullOrWhiteSpace(ClinicAddress) ? ClinicName : ClinicAddress);
        LaunchUrl($"https://calendar.google.com/calendar/render?action=TEMPLATE&text={title}&dates={start:yyyyMMdd'T'HHmmss}/{end:yyyyMMdd'T'HHmmss}&details={details}&location={location}");
    }

    private void LaunchUrl(string url)
    {
        if (OpenUrl != null)
        {
            OpenUrl(url);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo abrir el enlace", ex);
            InfoModalMessage = "No se pudo abrir el navegador.";
            IsInfoModalVisible = true;
        }
    }

    /// <summary>Baja lógica: deja de aparecer en búsquedas; sus consultas y recetas se conservan.</summary>
    [RelayCommand]
    public async Task DeactivateSelectedPatientAsync()
    {
        var patient = SelectedPatient;
        if (patient == null || BlockIfPatientHandedOff(patient)) return;
        if (HasActiveConsultation && ActiveConsultation?.PatientId == patient.Id)
        {
            InfoModalMessage = "Termine o descarte la consulta en curso antes de dar de baja el expediente.";
            IsInfoModalVisible = true;
            return;
        }
        if (!DialogService.Confirm($"¿Dar de baja el expediente de {patient.FullName}?\n\nDejará de aparecer en la búsqueda. Sus consultas y recetas se conservan.", "Dar de baja expediente")) return;

        try
        {
            if (_inventoryService != null) await _inventoryService.DeletePatientAsync(patient.Id);
        }
        catch (Exception ex)
        {
            InfoModalMessage = $"No se pudo dar de baja el expediente: {ex.Message}";
            IsInfoModalVisible = true;
            return;
        }

        SelectedPatient = null;
        await LoadPatientsAsync();
        ShowSuccess($"Expediente de {patient.FullName} dado de baja");
        GoToPatientsHome();
    }

    /// <summary>"Agendar cita" desde la ficha: el panel abre en mañana y al elegir un hueco el paciente ya viene puesto.</summary>
    [RelayCommand]
    public Task ScheduleForSelectedPatientAsync() => ShowAgendaPanelAsync(DateTime.Today.AddDays(1));
}
