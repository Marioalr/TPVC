using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

/// <summary>
/// Paso 8.23 (revisión): de v1.7.0 a v1.13.0 la hora de una cita se escribía a mano ("Ej. 10:00 AM o 16:30") y se guardaba
/// tal cual: hay citas con "5 pm", "12 PM", "4.30 pm", "16:30 PM", "7:5" o "10.30". Hasta v1.39.0 la agenda las leía con
/// <c>DateTime.TryParse</c>; la regla de los empalmes y del resumen (<see cref="PatientSummaryRules.TimeOfDay"/>), que Kotlin
/// repite, no las lee y las pondría a las 10:00. Una sola vez por base, cada hora que la regla no lee y
/// <see cref="LegacyTimeOfDay"/> sí se reescribe como "HH:mm" con el guardado de siempre (marca nueva y registro de cambios):
/// sube como una edición y todos los equipos quedan con la misma. Lo que ninguno de los dos lee se queda como está (se sigue
/// viendo el texto tal cual, y la agenda lo pone a las 10:00).
/// <list type="bullet">
/// <item><b>Cuándo.</b> Sin nube activa, al abrir la base (<see cref="RunWithoutCloud"/>, desde el arranque y al restaurar un
/// respaldo). Con la nube activa, dentro de la sincronización, con su candado, <b>después de bajar</b> (también la descarga
/// completa de «Unirse» y «Reemplazar con la nube», que quitan la marca de hecho): así solo se reescriben filas al día con la
/// nube. Al abrir no se toca nada con la nube activa: una copia vieja (otro equipo movió o canceló la cita mientras esta PC
/// estaba cerrada) con marca nueva subiría antes de bajar y desharía ese cambio en todos los equipos (las citas son LWW por
/// fila).</item>
/// <item><b>La marca de hecho</b> (<c>DbMeta</c> "AppointmentTimesNormalized") solo se pone después de una pasada sin nube o
/// después de bajar.</item>
/// </list>
/// </summary>
public static class AppointmentTimeMigration
{
    /// <summary>La clave de <c>DbMeta</c> que dice que ya se hizo.</summary>
    public const string DoneKey = "AppointmentTimesNormalized";

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>"10.30", "9,30", "10 30", con am/pm opcional ("4.30 pm"): hora y minutos con otro separador.</summary>
    private static readonly Regex SeparatedTime = new(
        @"^\s*(?<h>\d{1,2})\s*[.,\s]\s*(?<m>\d{2})\s*(?<ampm>[ap]\.?\s*m\.?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>¿Hay nube activa? (la sincronización encendida y con URL, como la pide <c>SyncAsync</c>).</summary>
    public static bool IsCloudActive(TursoSettings? settings) =>
        settings != null && settings.IsSyncEnabled && !string.IsNullOrWhiteSpace(settings.DatabaseUrl);

    /// <summary>
    /// Al abrir la base (el arranque y al restaurar un respaldo): solo sin nube activa. Con la nube activa no hace nada: lo
    /// hace la sincronización después de bajar. Regresa cuántas cambió.
    /// </summary>
    public static int RunWithoutCloud(AppDbContext db, bool cloudActive) => cloudActive ? 0 : Run(db);

    /// <summary>
    /// Normaliza las horas de las citas si no se ha hecho en esta base y pone la marca de hecho. Solo se llama sin nube activa
    /// o dentro de la sincronización, después de bajar. Regresa cuántas cambió.
    /// </summary>
    internal static int Run(AppDbContext db)
    {
        if (DbMeta.Get(db, DoneKey) == "1") return 0;

        int changed = 0;
        try
        {
            // Primero lo que hay que cambiar, sin seguimiento; después cada fila se vuelve a leer y se cambia solo si su hora
            // sigue igual (pudo cambiar mientras tanto)
            var candidates = new Dictionary<int, (string Before, string After)>();
            foreach (var row in db.Appointments.AsNoTracking().Where(a => a.Time != null && a.Time != "").Select(a => new { a.Id, a.Time }).ToList())
            {
                if (PatientSummaryRules.TryTimeOfDay(row.Time, out _)) continue;
                if (LegacyTimeOfDay(row.Time) is { } time) candidates[row.Id] = (row.Time, Format(time));
            }
            if (candidates.Count > 0)
            {
                var ids = candidates.Keys.ToList();
                foreach (var appointment in db.Appointments.Where(a => ids.Contains(a.Id)).ToList())
                {
                    var (before, after) = candidates[appointment.Id];
                    if (!string.Equals(appointment.Time, before, StringComparison.Ordinal)) continue;
                    appointment.Time = after;
                    changed++;
                }
                if (changed > 0) db.SaveChanges();
            }
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
    /// La hora de un texto escrito a mano en v1.7–v1.13, o null si no se puede leer sin adivinar.
    /// <list type="number">
    /// <item>"H.mm", "H,mm" y "H mm", con am/pm opcional ("10.30", "9,30", "10 30", "4.30 pm"), como H:mm.</item>
    /// <item>Si no, el lector de la agenda hasta v1.39.0: <c>DateTime.TryParse</c> con la cultura invariante y luego es-MX, sin
    /// fecha ("5 pm", "12 PM", "16:30 PM", "7:5"). Un texto que ese lector toma como fecha ("10/30", "10-30", "13.12" en es-MX)
    /// da medianoche: no es una hora y se deja como está (antes de esta regla se reescribía como "00:00").</item>
    /// </list>
    /// </summary>
    public static TimeSpan? LegacyTimeOfDay(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;

        var match = SeparatedTime.Match(time);
        if (match.Success)
        {
            int hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            int minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
            if (minutes > 59) return null;
            if (match.Groups["ampm"].Success)
            {
                if (hours is < 1 or > 12) return null;
                bool pm = char.ToLowerInvariant(match.Groups["ampm"].Value[0]) == 'p';
                hours = hours % 12 + (pm ? 12 : 0);
            }
            else if (hours > 23)
            {
                return null;
            }
            return new TimeSpan(hours, minutes, 0);
        }

        foreach (var culture in new[] { CultureInfo.InvariantCulture, Mexican })
        {
            // Con NoCurrentDateDefault una hora sola queda en 0001-01-01; cualquier otra fecha es que lo leyó como fecha
            if (DateTime.TryParse(time, culture, DateTimeStyles.NoCurrentDateDefault, out var parsed) && parsed.Date == DateTime.MinValue.Date)
                return parsed.TimeOfDay;
        }
        return null;
    }
}
