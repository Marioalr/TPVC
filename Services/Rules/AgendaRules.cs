using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// La agenda del consultorio (paso 8.26; antes en <c>AgendaPanelViewModel</c> y <c>PatientsHomeViewModel</c>, sin cambios):
/// los huecos del día, la marca de empalme, la semana, las citas de hoy y los textos que no dependen de nombres de meses
/// abreviados. Sin WPF ni base: vectores en <c>contracts/golden/agenda.json</c>. Las citas que se empalman son las de
/// <see cref="AppointmentRules.Overlaps"/> (<c>appointment-overlap.json</c>).
/// </summary>
public static class AgendaRules
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>El primer hueco del día.</summary>
    public static readonly TimeSpan DayStart = new(8, 0, 0);

    /// <summary>Después del último hueco (el último empieza 30 minutos antes).</summary>
    public static readonly TimeSpan DayEnd = new(20, 0, 0);

    /// <summary>Lo que dura un hueco.</summary>
    public static readonly TimeSpan SlotLength = TimeSpan.FromMinutes(30);

    /// <summary>El lunes de la semana de <paramref name="date"/> (la semana va de lunes a domingo).</summary>
    public static DateTime StartOfWeek(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }

    /// <summary>
    /// Citas del día y huecos libres de 30 minutos entre las 8:00 y las 20:00. Un hueco ocupado por
    /// una cita (según su duración) no se ofrece; en el día de hoy no se ofrecen horas pasadas. Las citas canceladas no
    /// cuentan; las que se empalman llevan su marca (<see cref="MarkOverlaps"/>). Orden: por hora, la cita antes que el hueco.
    /// </summary>
    public static List<AgendaSlot> BuildSlots(DateTime date, IEnumerable<PatientAppointment> appointments, DateTime now)
    {
        var dayAppointments = appointments
            .Where(a => a.Date.Date == date.Date && !a.IsCancelled)
            .Select(a => (Appointment: a, Start: PatientSummaryRules.TimeOfDay(a.Time), Minutes: AppointmentRules.Minutes(a.DurationMinutes)))
            .OrderBy(a => a.Start)
            .ToList();
        // Paso 8.23: las que se empalman llevan la marca "Empalme"
        MarkOverlaps(dayAppointments.Select(a => a.Appointment));

        var slots = dayAppointments.Select(a => new AgendaSlot(a.Start, a.Appointment)).ToList();

        for (var t = DayStart; t < DayEnd; t += SlotLength)
        {
            if (date.Date == now.Date && date.Date + t < now) continue;
            var slotEnd = t + SlotLength;
            bool busy = dayAppointments.Any(a => a.Start < slotEnd && t < a.Start + TimeSpan.FromMinutes(a.Minutes));
            if (!busy) slots.Add(new AgendaSlot(t, null));
        }

        return slots.OrderBy(s => s.Time).ThenBy(s => s.IsFree).ToList();
    }

    /// <summary>
    /// Paso 8.23: pone "Empalme con Ana López (10:30)" en cada cita de la lista que se empalma con otra de la misma lista
    /// (<see cref="AppointmentRules.Overlaps"/>), y lo quita en las demás. Con varias, la primera por hora (y por SyncId).
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

    /// <summary>"Ya hay una cita a las 10:30 con Ana López. ¿Agendar de todos modos?" (con varias, todas).</summary>
    public static string OverlapQuestion(IReadOnlyList<PatientAppointment> overlapping)
    {
        var parts = overlapping.Select(a => $"a las {AppointmentRules.TimeText(a.Time)} con {a.PatientName}").ToList();
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " y " + parts[^1];
        return (overlapping.Count == 1 ? "Ya hay una cita " : "Ya hay citas ") + list + ".\n\n¿Agendar de todos modos?";
    }

    /// <summary>Los siete días de la semana de <paramref name="date"/>, de lunes a domingo, con sus citas sin contar las canceladas.</summary>
    public static List<(DateTime Date, int Count)> Week(DateTime date, IEnumerable<PatientAppointment> appointments)
    {
        var monday = StartOfWeek(date);
        var list = appointments.ToList();
        return Enumerable.Range(0, 7).Select(i =>
        {
            var day = monday.AddDays(i);
            return (day, list.Count(a => a.Date.Date == day && !a.IsCancelled));
        }).ToList();
    }

    /// <summary>"libre", "1 cita" o "3 citas", debajo de cada día de la semana.</summary>
    public static string DayCountText(int count) => count == 0 ? "libre" : count == 1 ? "1 cita" : $"{count} citas";

    /// <summary>"2 citas · 21 huecos libres" con los huecos del día.</summary>
    public static string DaySummary(IEnumerable<AgendaSlot> slots)
    {
        var list = slots.ToList();
        int appointments = list.Count(s => s.HasAppointment);
        int free = list.Count(s => s.IsFree);
        return $"{appointments} {(appointments == 1 ? "cita" : "citas")} · {free} {(free == 1 ? "hueco libre" : "huecos libres")}";
    }

    /// <summary>"Lunes 12 de octubre": el día del panel con su primera letra en mayúscula.</summary>
    public static string PanelTitle(DateTime date)
    {
        var text = date.ToString("dddd d 'de' MMMM", Mexican);
        return char.ToUpper(text[0], Mexican) + text[1..];
    }

    /// <summary>
    /// La semana del panel: "12 – 18 de octubre 2026" si no cambia de mes; si cambia, "d MMM – d MMM yyyy" con los meses
    /// abreviados de es-MX (dependen de los datos de la cultura del sistema: no van en los vectores).
    /// </summary>
    public static string WeekTitle(DateTime date)
    {
        var monday = StartOfWeek(date);
        var sunday = monday.AddDays(6);
        return monday.Month == sunday.Month
            ? $"{monday.Day} – {sunday.Day} de {sunday.ToString("MMMM yyyy", Mexican)}"
            : $"{monday.ToString("d MMM", Mexican)} – {sunday.ToString("d MMM yyyy", Mexican)}";
    }

    /// <summary>Las citas de hoy en el inicio: sin las canceladas, por hora y con su marca de empalme.</summary>
    public static List<PatientAppointment> TodayAppointments(IEnumerable<PatientAppointment> dayAppointments)
    {
        var today = dayAppointments.Where(a => !a.IsCancelled).OrderBy(a => PatientSummaryRules.TimeOfDay(a.Time)).ToList();
        MarkOverlaps(today);
        return today;
    }

    /// <summary>
    /// La próxima cita de la ficha: la primera por fecha y hora de las que siguen pendientes <paramref name="today"/> o
    /// después (ni cancelada ni atendida); null si no hay.
    /// </summary>
    public static PatientAppointment? NextAppointment(IEnumerable<PatientAppointment> appointments, DateTime today) =>
        appointments
            .Where(a => a.IsUpcomingOn(today))
            .OrderBy(a => a.Date.Date + PatientSummaryRules.TimeOfDay(a.Time))
            .FirstOrDefault();
}
