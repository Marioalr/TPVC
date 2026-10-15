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
using TPVC.Services.Rules;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Agenda como panel lateral (fase 6, paso 9): la semana, los huecos del día, agendar, cambiar de
/// horario, confirmar, cancelar y recordar citas. Después de cada cambio le pide al coordinador que
/// actualice la agenda, las citas de hoy y las del expediente abierto.
/// </summary>
public partial class AgendaPanelViewModel : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private readonly InventoryService? _inventoryService;
    private readonly PatientSession _session;
    private readonly InfoDialogState _info;
    private readonly PatientsFeedback _feedback;
    private readonly ClinicInfo _clinic;
    private readonly IUserDialogs _dialogs;
    private readonly IUrlLauncher _urls;
    private readonly IPatientsWorkflow _workflow;

    private List<PatientAppointment> _agendaWeekAppointments = new();

    public AgendaPanelViewModel(
        InventoryService? inventoryService,
        PatientSession session,
        InfoDialogState info,
        PatientsFeedback feedback,
        ClinicInfo clinic,
        IUserDialogs dialogs,
        IUrlLauncher urls,
        IPatientsWorkflow workflow)
    {
        _inventoryService = inventoryService;
        _session = session;
        _info = info;
        _feedback = feedback;
        _clinic = clinic;
        _dialogs = dialogs;
        _urls = urls;
        _workflow = workflow;
    }

    /// <summary>Abre enlaces (WhatsApp, Google Calendar); las pruebas lo reemplazan.</summary>
    public Func<string, bool>? OpenUrl { get; set; }

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

    /// <summary>Paso 8.23: la agenda en un día (el de un empalme que llegó de otro equipo).</summary>
    public Task OpenAgendaOnDateAsync(DateTime date) => ShowAgendaPanelAsync(date);

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

    /// <summary>
    /// Arma los huecos del día. Paso 8.23: si había un formulario abierto (una recarga porque bajó algo, también la de la
    /// sincronización rápida antes de agendar), se vuelve a abrir en el mismo hueco si sigue libre en el mismo día, con lo que
    /// ya se había escrito. Si lo ocupó lo que bajó, se cierra y el siguiente hueco que se abra conserva paciente, motivo y
    /// duración. Cambiar de día lo cierra, como siempre.
    /// </summary>
    private void BuildAgendaSlots()
    {
        var open = AgendaSlots.FirstOrDefault(s => s.IsFormOpen);
        var sameDay = open != null && _formDate == AgendaPanelDate.Date;
        CloseFormSlots();
        AgendaSlots = new ObservableCollection<AgendaSlot>(BuildSlots(AgendaPanelDate, _agendaWeekAppointments, DateTime.Now));
        if (open != null && sameDay)
        {
            if (AgendaSlots.FirstOrDefault(s => s.IsFree && s.Time == open.Time) is { } again)
            {
                again.IsFormOpen = true;
            }
            else
            {
                _keepFormValues = true;
                _feedback.ShowWarning($"El hueco de las {open.TimeText} ya no está libre (llegó una cita de otro equipo). Elija otro: se conservan el paciente, el motivo y la duración.");
            }
        }
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
            .Select(a => (Appointment: a, Start: ParseAppointmentTime(a.Time), Minutes: AppointmentRules.Minutes(a.DurationMinutes)))
            .OrderBy(a => a.Start)
            .ToList();
        // Paso 8.23: las que se empalman llevan la marca "Empalme"
        MarkOverlaps(dayAppointments.Select(a => a.Appointment));

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

        CloseFormSlots();
        slot.IsFormOpen = true;
        _formDate = AgendaPanelDate.Date;
        _formRequest++;
        // Paso 8.23: si el hueco que tenía abierto lo ocupó lo que bajó, lo escrito pasa a este
        if (_keepFormValues)
        {
            _keepFormValues = false;
        }
        else
        {
            AgendaFormPatient = _session.SelectedPatient;
            AgendaFormReason = string.Empty;
            AgendaFormDuration = 30;
        }
        AgendaFormError = null;
    }

    // Paso 8.23: cambia cuando el médico abre o cierra el formulario (no cuando la agenda se recarga): un guardado que
    // esperó a la nube y lo encuentra distinto no guarda nada
    private int _formRequest;

    // El día del hueco donde está abierto el formulario
    private DateTime _formDate;

    // El hueco que tenía abierto lo ocupó lo que bajó: el siguiente que se abra conserva paciente, motivo y duración
    private bool _keepFormValues;

    /// <summary>Cierra los formularios de los huecos sin cambiar lo escrito (las recargas).</summary>
    private void CloseFormSlots()
    {
        foreach (var s in AgendaSlots) s.IsFormOpen = false;
    }

    [RelayCommand]
    public void SetAgendaReason(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)) AgendaFormReason = reason;
    }

    [RelayCommand]
    public void CloseAgendaForm()
    {
        // El médico cierra (o cambia a otra cosa): un guardado que esperaba a la nube ya no guarda
        _formRequest++;
        _keepFormValues = false;
        CloseFormSlots();
        AgendaFormError = null;
    }

    // Mientras se revisa la agenda en la nube (hasta 3 s) un segundo clic en "Agendar" no agenda dos veces
    private bool _savingAppointment;

    [RelayCommand]
    public async Task SaveAgendaFormAsync()
    {
        if (_savingAppointment) return;
        _savingAppointment = true;
        try
        {
            await SaveAgendaFormCoreAsync();
        }
        finally
        {
            _savingAppointment = false;
        }
    }

    private async Task SaveAgendaFormCoreAsync()
    {
        var slot = AgendaSlots.FirstOrDefault(s => s.IsFormOpen);
        if (slot == null) return;
        var request = _formRequest;

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

        // Paso 8.23: si mientras tanto el médico cerró el formulario o la agenda (o abrió otro hueco), no se agenda
        if (!await ConfirmNoOverlapAsync(appointment, () => request == _formRequest)) return;

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

        // Agendada: el formulario se cierra (la recarga no lo vuelve a abrir sobre la cita nueva)
        _keepFormValues = false;
        CloseFormSlots();
        _feedback.ShowSuccess($"Cita agendada: {patient.FullName}, {appointment.Date.ToString("ddd d MMM", Mexican)} a las {appointment.Time}");
        await _workflow.RefreshAgendaAfterChangeAsync(patient.Id);
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

        if (_savingAppointment) return;
        _savingAppointment = true;
        try
        {
            // Si no se confirma, sigue eligiendo otro horario. Paso 8.23: si mientras se esperaba a la nube el médico canceló
            // el cambio (o cerró la agenda), no se mueve
            if (!await ConfirmNoOverlapAsync(moved, () => ReferenceEquals(MovingAppointment, appointment))) return;
            try
            {
                if (_inventoryService != null) await _inventoryService.SaveAppointmentAsync(moved);
            }
            catch (Exception ex)
            {
                _info.InfoModalMessage = $"No se pudo cambiar la cita: {ex.Message}";
                _info.IsInfoModalVisible = true;
                return;
            }

            MovingAppointment = null;
            _feedback.ShowSuccess($"Cita de {moved.PatientName} cambiada al {date.ToString("ddd d MMM", Mexican)} a las {time}");
            await _workflow.RefreshAgendaAfterChangeAsync(moved.PatientRecordId);
        }
        finally
        {
            _savingAppointment = false;
        }
    }

    /// <summary>
    /// Paso 8.23 (H2): antes de guardar una cita, con la nube activa, la sincronización rápida (3 s como máximo; sin red no
    /// espera) y luego la revisión con las citas guardadas: si se empalma, "Ya hay una cita a las 10:30 con … ¿Agendar de
    /// todos modos?". Nunca cambia ni cancela la otra cita.
    /// </summary>
    private async Task<bool> ConfirmNoOverlapAsync(PatientAppointment appointment, Func<bool> stillWanted)
    {
        List<PatientAppointment> overlapping;
        if (_inventoryService != null)
        {
            await _inventoryService.QuickSyncAsync();
            // Lo canceló mientras se esperaba: ni siquiera se pregunta
            if (!stillWanted()) return false;
            try
            {
                overlapping = await _inventoryService.FindOverlappingAppointmentsAsync(appointment);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo revisar si la cita se empalma", ex);
                return true;
            }
        }
        else
        {
            var candidate = AppointmentRules.Of(appointment);
            overlapping = _agendaWeekAppointments
                .Where(a => !ReferenceEquals(a, appointment) && (appointment.Id == 0 || a.Id != appointment.Id) && AppointmentRules.Overlaps(candidate, AppointmentRules.Of(a)))
                .OrderBy(a => AppointmentRules.Start(a.Time))
                .ToList();
        }
        if (overlapping.Count > 0 && !await _dialogs.ConfirmAsync(OverlapQuestion(overlapping), "Cita empalmada")) return false;
        return stillWanted();
    }

    /// <summary>"Ya hay una cita a las 10:30 con Ana López. ¿Agendar de todos modos?" (con varias, todas).</summary>
    public static string OverlapQuestion(IReadOnlyList<PatientAppointment> overlapping)
    {
        var parts = overlapping.Select(a => $"a las {AppointmentRules.TimeText(a.Time)} con {a.PatientName}").ToList();
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " y " + parts[^1];
        return (overlapping.Count == 1 ? "Ya hay una cita " : "Ya hay citas ") + list + ".\n\n¿Agendar de todos modos?";
    }

    /// <summary>
    /// Paso 8.23: pone "Empalme con Ana López (10:30)" en cada cita de la lista que se empalma con otra de la misma lista
    /// (<see cref="AppointmentRules.Overlaps"/>), y lo quita en las demás. Con varias, la primera por hora.
    /// </summary>
    public static void MarkOverlaps(IEnumerable<PatientAppointment> appointments)
    {
        var list = appointments.ToList();
        foreach (var appointment in list) appointment.OverlapText = string.Empty;
        var rules = list.Select(a => (Appointment: a, Rule: AppointmentRules.Of(a))).ToList();
        foreach (var (appointment, rule) in rules)
        {
            var other = rules
                .Where(o => !ReferenceEquals(o.Appointment, appointment) && AppointmentRules.Overlaps(rule, o.Rule))
                .OrderBy(o => AppointmentRules.Start(o.Appointment.Time))
                .ThenBy(o => SyncIds.Format(o.Appointment.SyncId), StringComparer.Ordinal)
                .Select(o => o.Appointment)
                .ToList();
            if (other.Count == 0) continue;
            appointment.OverlapText = $"Empalme con {other[0].PatientName} ({AppointmentRules.TimeText(other[0].Time)})" +
                                      (other.Count > 1 ? $" y {other.Count - 1} más" : string.Empty);
        }
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
        await _workflow.RefreshAgendaAfterChangeAsync(appointment.PatientRecordId);
    }

    [RelayCommand]
    public async Task CancelAppointmentAsync(PatientAppointment? appointment)
    {
        if (appointment == null) return;
        if (!await _dialogs.ConfirmAsync($"¿Cancelar la cita de {appointment.PatientName} ({appointment.Date:dd/MM} {appointment.Time})?", "Cancelar cita")) return;
        await UpdateAppointmentStatusAsync(appointment, "Cancelada");
        _feedback.ShowSuccess($"Cita de {appointment.PatientName} cancelada");
        await _workflow.RefreshAgendaAfterChangeAsync(appointment.PatientRecordId);
    }

    [RelayCommand]
    public void RemindByWhatsApp(PatientAppointment? appointment)
    {
        if (appointment == null) return;
        var phone = new string((appointment.PatientPhone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (phone.Length < 10)
        {
            _info.InfoModalMessage = $"La cita de {appointment.PatientName} no tiene un teléfono de 10 dígitos. Agréguelo en su expediente.";
            _info.IsInfoModalVisible = true;
            return;
        }
        if (phone.Length == 10) phone = "52" + phone;

        var message = $"Hola {appointment.PatientName}, le recordamos su cita en {_clinic.ClinicName} el {appointment.Date.ToString("dddd d 'de' MMMM", Mexican)} a las {appointment.Time}. Por favor confirme su asistencia respondiendo a este mensaje.";
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
        var location = Uri.EscapeDataString(string.IsNullOrWhiteSpace(_clinic.ClinicAddress) ? _clinic.ClinicName : _clinic.ClinicAddress);
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
            _urls.Open(url);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo abrir el enlace", ex);
            _info.InfoModalMessage = "No se pudo abrir el navegador.";
            _info.IsInfoModalVisible = true;
        }
    }

    /// <summary>"Agendar cita" desde la ficha: el panel abre en mañana y al elegir un hueco el paciente ya viene puesto.</summary>
    [RelayCommand]
    public Task ScheduleForSelectedPatientAsync() => ShowAgendaPanelAsync(DateTime.Today.AddDays(1));

    // ================================================================== citas

    /// <summary>
    /// La hora de una cita. Paso 8.23: la misma regla que el resumen derivado y los empalmes
    /// (<see cref="PatientSummaryRules.TimeOfDay"/>: <c>H:mm</c>, segundos y AM/PM opcionales; lo demás, las 10:00), así la agenda
    /// dibuja la cita donde la regla la empalma. Antes era <c>DateTime.TryParse</c>, que también leía textos como "3 PM" o "7:5".
    /// Desde v1.14 la agenda escribe "HH:mm"; las horas escritas a mano en v1.7–v1.13 ("5 pm", "4.30 pm", "10.30") se pasan a
    /// "HH:mm" una sola vez (<see cref="AppointmentTimeMigration"/>): al abrir la base si no hay una nube configurada y, si la
    /// hay, en la sincronización después de bajar.
    /// </summary>
    public static TimeSpan ParseAppointmentTime(string? time) => PatientSummaryRules.TimeOfDay(time);

    public async Task UpdateAppointmentStatusAsync(PatientAppointment? appt, string status)
    {
        if (appt == null || _inventoryService == null) return;

        try
        {
            await _inventoryService.UpdateAppointmentStatusAsync(appt.Id, status);
            appt.Status = status;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al actualizar estado de cita", ex);
        }
    }
}
