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
        AgendaFormPatient = _session.SelectedPatient;
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

    public static TimeSpan ParseAppointmentTime(string? time)
    {
        if (!string.IsNullOrWhiteSpace(time))
        {
            if (DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)) return parsed.TimeOfDay;
            if (DateTime.TryParse(time, Mexican, DateTimeStyles.NoCurrentDateDefault, out parsed)) return parsed.TimeOfDay;
        }
        return new TimeSpan(10, 0, 0);
    }

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
