using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Paso 8.23 (H2): citas empalmadas. Dos citas se empalman si son del mismo día, ninguna está "Cancelada" y sus horarios
/// [hora, hora + duración) se cruzan: una que termina a las 10:30 y otra que empieza a las 10:30 no se empalman. La hora se
/// lee con la misma regla del resumen derivado (<see cref="PatientSummaryRules.TimeOfDay"/>: lo que no se puede leer cuenta
/// como las 10:00) y una cita sin duración (0 o negativa) dura 30 minutos, lo mismo que la agenda dibuja. La comparación es
/// dentro de la misma fecha: una cita de las 23:45 que dura 30 minutos se empalma con otra del mismo día a las 23:50, no con
/// una del día siguiente a las 00:00. Una cita "Atendida" sigue contando (ocupó ese horario). La misma cita (mismo SyncId, no
/// vacío: una cita nueva todavía no lo tiene) nunca se empalma consigo misma. Nunca se cancela una cita sola: la regla solo avisa. Vectores en
/// <c>contracts/golden/appointment-overlap.json</c>.
/// </summary>
public static class AppointmentRules
{
    /// <summary>Una cita como la ve la regla.</summary>
    public sealed record Appointment(Guid SyncId, DateTime Date, string? Time, int DurationMinutes, string? Status);

    /// <summary>La cita guardada como la ve la regla.</summary>
    public static Appointment Of(PatientAppointment appointment) =>
        new(appointment.SyncId, appointment.Date, appointment.Time, appointment.DurationMinutes, appointment.Status);

    /// <summary>Lo que dura una cita sin duración (la agenda la dibuja igual).</summary>
    public const int DefaultDurationMinutes = 30;

    /// <summary>La única cita que no ocupa su horario.</summary>
    public const string Cancelled = PatientSummaryRules.Cancelled;

    /// <summary>La hora de inicio (la regla de <see cref="PatientSummaryRules.TimeOfDay"/>).</summary>
    public static TimeSpan Start(string? time) => PatientSummaryRules.TimeOfDay(time);

    /// <summary>Los minutos que ocupa: los suyos, o 30 si no tiene (0 o negativa).</summary>
    public static int Minutes(int durationMinutes) => durationMinutes > 0 ? durationMinutes : DefaultDurationMinutes;

    /// <summary>Hasta cuándo ocupa (puede pasar de las 24:00: sigue contando en su mismo día).</summary>
    public static TimeSpan End(Appointment appointment) => Start(appointment.Time) + TimeSpan.FromMinutes(Minutes(appointment.DurationMinutes));

    /// <summary>¿Se empalman? Mismo día, ninguna cancelada y [inicio, fin) que se cruzan.</summary>
    public static bool Overlaps(Appointment a, Appointment b)
    {
        if (a.SyncId != Guid.Empty && a.SyncId == b.SyncId) return false;
        if (a.Status == Cancelled || b.Status == Cancelled) return false;
        if (a.Date.Date != b.Date.Date) return false;
        return Start(a.Time) < End(b) && Start(b.Time) < End(a);
    }

    /// <summary>Las citas de <paramref name="others"/> con que se empalma <paramref name="candidate"/>, por hora y SyncId.</summary>
    public static List<Appointment> OverlapsWith(Appointment candidate, IEnumerable<Appointment> others) =>
        others.Where(o => Overlaps(candidate, o))
            .OrderBy(o => Start(o.Time))
            .ThenBy(o => SyncIds.Format(o.SyncId), StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Los pares empalmados de una lista (cada par una vez, la primera por hora y SyncId), por fecha y hora. Con
    /// <paramref name="involving"/> solo los pares en que está alguna de esas citas (las que acaban de bajar).
    /// </summary>
    public static List<(Appointment First, Appointment Second)> Pairs(IEnumerable<Appointment> appointments, IReadOnlySet<Guid>? involving = null)
    {
        var pairs = new List<(Appointment, Appointment)>();
        foreach (var day in appointments.Where(a => a.Status != Cancelled).GroupBy(a => a.Date.Date).OrderBy(g => g.Key))
        {
            var list = day.OrderBy(a => Start(a.Time)).ThenBy(a => SyncIds.Format(a.SyncId), StringComparer.Ordinal).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (involving != null && !involving.Contains(list[i].SyncId) && !involving.Contains(list[j].SyncId)) continue;
                    if (Overlaps(list[i], list[j])) pairs.Add((list[i], list[j]));
                }
            }
        }
        return pairs;
    }

    /// <summary>"09:30": la hora de una cita en los avisos, como la escribe la agenda (24 horas, dos dígitos).</summary>
    public static string TimeText(string? time) => Start(time).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
