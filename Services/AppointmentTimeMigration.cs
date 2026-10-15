using System;
using System.Globalization;
using System.Linq;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

/// <summary>
/// Paso 8.23 (revisión): de v1.7.0 a v1.13.0 la hora de una cita se escribía a mano ("Ej. 10:00 AM o 16:30") y se guardaba
/// tal cual: hay citas con "5 pm", "12 PM", "4.30 pm", "16:30 PM" o "7:5". Hasta v1.39.0 la agenda las leía con
/// <c>DateTime.TryParse</c>; la regla de los empalmes y del resumen (<see cref="PatientSummaryRules.TimeOfDay"/>), que Kotlin
/// repite, no las lee y las pondría a las 10:00. Una sola vez por base (al abrirla, antes de su primera sincronización con
/// esta versión; también un respaldo anterior que se restaura), cada hora que la regla no lee y el lector de antes sí se
/// reescribe como "HH:mm" con el guardado de siempre (marca nueva y registro de cambios): sube como una edición y todos los
/// equipos quedan con la misma. Lo que ninguno de los dos lee se queda como está (ya se mostraba a las 10:00).
/// </summary>
public static class AppointmentTimeMigration
{
    /// <summary>La clave de <c>DbMeta</c> que dice que ya se hizo.</summary>
    public const string DoneKey = "AppointmentTimesNormalized";

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Normaliza las horas de las citas si no se ha hecho en esta base. Regresa cuántas cambió.</summary>
    public static int Run(AppDbContext db)
    {
        if (DbMeta.Get(db, DoneKey) == "1") return 0;

        int changed = 0;
        try
        {
            var candidates = db.Appointments.Where(a => a.Time != null && a.Time != "").ToList()
                .Where(a => !PatientSummaryRules.TryTimeOfDay(a.Time, out _))
                .ToList();
            foreach (var appointment in candidates)
            {
                if (LegacyTimeOfDay(appointment.Time) is not { } time) continue;
                appointment.Time = Format(time);
                changed++;
            }
            if (changed > 0) db.SaveChanges();
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
        DbMeta.Set(db, DoneKey, "1");
        if (changed > 0) AppLogger.Info($"Citas: {changed} horas escritas a mano (v1.7–v1.13) se guardaron como HH:mm.");
        return changed;
    }

    /// <summary>"17:00"; con segundos si los tiene ("16:30:15").</summary>
    public static string Format(TimeSpan time) =>
        time.ToString(time.Seconds == 0 ? @"hh\:mm" : @"hh\:mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// El lector de la agenda hasta v1.39.0: <c>DateTime.TryParse</c> con la cultura invariante y luego es-MX, sin fecha.
    /// null si tampoco la lee.
    /// </summary>
    public static TimeSpan? LegacyTimeOfDay(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        if (DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)) return parsed.TimeOfDay;
        if (DateTime.TryParse(time, Mexican, DateTimeStyles.NoCurrentDateDefault, out parsed)) return parsed.TimeOfDay;
        return null;
    }
}
