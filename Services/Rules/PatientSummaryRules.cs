using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Paso 8.22: el resumen del expediente que sale de sus citas y consultas (<c>NextAppointmentDate</c>, <c>NextAppointmentTime</c>,
/// <c>NextAppointmentReason</c>, <c>NextAppointmentNotes</c> y <c>LastVisit</c>). Son columnas derivadas
/// (<c>TableMap.DerivedColumns</c>): cada equipo las calcula después de cada guardado y de cada bajada, suben como
/// información y nunca se aplican al bajar. Antes la próxima cita era la última que se guardaba, aunque fuera una ya atendida
/// o anterior a otra; ahora sigue el orden clínico. Vectores en <c>contracts/golden/patient-summary.json</c>.
/// </summary>
public static class PatientSummaryRules
{
    /// <summary>Una cita como la ve la regla.</summary>
    public sealed record Appointment(Guid SyncId, DateTime Date, string? Time, string? Status, string? Reason, string? Notes);

    /// <summary>La próxima cita del expediente (o ninguna: fecha nula y textos vacíos).</summary>
    public sealed record NextAppointment(DateTime? Date, string Time, string Reason, string Notes)
    {
        public static readonly NextAppointment None = new(null, string.Empty, string.Empty, string.Empty);
    }

    public const string Cancelled = "Cancelada";
    public const string Attended = "Atendida";

    /// <summary>La hora de una cita cuando no se puede leer (la misma que usa la agenda).</summary>
    public static readonly TimeSpan DefaultTime = new(10, 0, 0);

    /// <summary>
    /// La próxima cita: de las que no están "Cancelada" ni "Atendida" y caen <paramref name="today"/> (el día del consultorio)
    /// o después, la primera por fecha, luego por hora (<see cref="TimeOfDay"/>) y luego por SyncId en minúsculas (orden
    /// ordinal), así dos equipos eligen la misma. La fecha queda sin hora; la hora, el motivo y las notas, tal como están en
    /// la cita. Sin ninguna, no hay próxima cita.
    /// </summary>
    public static NextAppointment Next(IEnumerable<Appointment> appointments, DateTime today)
    {
        var next = appointments
            .Where(a => a.Date.Date >= today.Date && a.Status != Cancelled && a.Status != Attended)
            .OrderBy(a => a.Date.Date)
            .ThenBy(a => TimeOfDay(a.Time))
            .ThenBy(a => SyncIds.Format(a.SyncId), StringComparer.Ordinal)
            .FirstOrDefault();
        return next == null
            ? NextAppointment.None
            : new NextAppointment(next.Date.Date, next.Time ?? string.Empty, next.Reason ?? string.Empty, next.Notes ?? string.Empty);
    }

    /// <summary>
    /// La última visita: la fecha (con su hora, del consultorio) de la consulta más reciente. Sin consultas no hay una que
    /// calcular (null): se queda la que el expediente tenía (la del alta).
    /// </summary>
    public static DateTime? LastVisit(IEnumerable<DateTime> consultationDates)
    {
        DateTime? last = null;
        foreach (var date in consultationDates)
        {
            if (last == null || date > last) last = date;
        }
        return last;
    }

    /// <summary>
    /// La hora de una cita para ordenar: <c>H:mm</c> o <c>HH:mm</c>, con segundos opcionales, y opcionalmente seguida de
    /// AM o PM (también <c>a.m.</c>, <c>p. m.</c>, sin distinguir mayúsculas). Lo que no se puede leer cuenta como las 10:00.
    /// </summary>
    public static TimeSpan TimeOfDay(string? time) => TryTimeOfDay(time, out var value) ? value : DefaultTime;

    /// <summary>
    /// Paso 8.23: lo mismo que <see cref="TimeOfDay"/>, pero dice si la hora se pudo leer (false: vacía o en otro formato, que
    /// cuenta como las 10:00). Lo usa la normalización única de las horas escritas a mano en v1.7.0–v1.13.0.
    /// </summary>
    public static bool TryTimeOfDay(string? time, out TimeSpan value)
    {
        value = DefaultTime;
        if (string.IsNullOrWhiteSpace(time)) return false;
        var text = time.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace(".", string.Empty);
        bool? pm = null;
        if (text.EndsWith("am", StringComparison.Ordinal)) { pm = false; text = text[..^2]; }
        else if (text.EndsWith("pm", StringComparison.Ordinal)) { pm = true; text = text[..^2]; }

        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length is 0 or > 2 || !p.All(char.IsAsciiDigit))) return false;
        int hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
        int minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int seconds = parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
        if (parts[1].Length != 2 || (parts.Length == 3 && parts[2].Length != 2) || minutes > 59 || seconds > 59) return false;
        if (pm != null)
        {
            if (hours is < 1 or > 12) return false;
            hours = hours % 12 + (pm.Value ? 12 : 0);
        }
        else if (hours > 23)
        {
            return false;
        }
        value = new TimeSpan(hours, minutes, seconds);
        return true;
    }

    /// <summary>Las columnas del resumen, en el orden del contrato.</summary>
    public static readonly IReadOnlyList<string> Columns = new[]
    {
        nameof(PatientRecord.LastVisit),
        nameof(PatientRecord.NextAppointmentDate),
        nameof(PatientRecord.NextAppointmentNotes),
        nameof(PatientRecord.NextAppointmentReason),
        nameof(PatientRecord.NextAppointmentTime)
    };
}
