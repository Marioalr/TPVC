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
/// <item><b>Cuándo.</b> Al abrir la base (<see cref="RunAtOpen"/>, desde el arranque y al restaurar un respaldo) solo si la base
/// nunca se ha ligado a una nube (<see cref="IsBoundToCloud"/>: sin URL configurada, sin <c>clinicId</c> y sin cursores de
/// bajada; no importa la casilla «Sincronización activada»). Si la base está o estuvo ligada, dentro de la sincronización, con su
/// candado, <b>después de bajar</b> (también la descarga completa de «Unirse» y «Reemplazar con la nube», que quitan la marca
/// de hecho): así solo se reescriben filas al día con la nube. Al abrir una base ligada no se toca nada, aunque la casilla
/// esté apagada: una copia vieja (otro equipo movió o canceló la cita mientras esta PC estaba cerrada o sin sincronizar) con
/// marca nueva subiría antes de bajar y desharía ese cambio en todos los equipos (las citas son LWW por fila).</item>
/// <item><b>La marca de hecho</b> (<c>DbMeta</c> "AppointmentTimesNormalized") solo se pone después de una pasada al abrir una
/// base nunca ligada o después de bajar.</item>
/// <item><b>Nunca detiene la sincronización.</b> Un texto que no se puede leer se deja como está. Si la pasada falla de todos
/// modos (la base), la sincronización sigue sin ella, no se pone la marca de hecho y esta versión no lo vuelve a intentar
/// (<c>DbMeta</c> "AppointmentTimesFailedVersion"); una versión nueva sí.</item>
/// </list>
/// </summary>
public static class AppointmentTimeMigration
{
    /// <summary>La clave de <c>DbMeta</c> que dice que ya se hizo.</summary>
    public const string DoneKey = "AppointmentTimesNormalized";

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>La versión de TPVC en que falló la pasada dentro de la sincronización (esa versión ya no la repite).</summary>
    public const string FailedVersionKey = "AppointmentTimesFailedVersion";

    /// <summary>
    /// "10.30", "9,30", "10 30", con am/pm opcional ("4.30 pm"): hora y minutos con otro separador. Solo dígitos ASCII
    /// (<c>\d</c> aceptaría "１０.３０" y la lectura fallaría).
    /// </summary>
    private static readonly Regex SeparatedTime = new(
        @"^\s*(?<h>[0-9]{1,2})\s*[.,\s]\s*(?<m>[0-9]{2})\s*(?<ampm>[ap]\.?\s*m\.?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// ¿La base está o estuvo ligada a una nube? Una URL configurada (aunque la casilla «Sincronización activada» esté apagada,
    /// y como lo toma la restauración), o en la base el <c>clinicId</c> de una nube o algún cursor de bajada. «Desconectar esta
    /// PC de la nube» quita los tres.
    /// </summary>
    public static bool IsBoundToCloud(AppDbContext db, TursoSettings? settings) =>
        !string.IsNullOrWhiteSpace(settings?.DatabaseUrl)
        || DbMeta.Get(db, DbMeta.ClinicId) != null
        || DbMeta.AnyWithPrefix(db, DbMeta.PullCursorPrefix);

    /// <summary>
    /// Al abrir la base (el arranque y al restaurar un respaldo): solo si nunca se ha ligado a una nube. Si lo está o lo estuvo
    /// no hace nada ni pone la marca de hecho: lo hace la sincronización después de bajar. Regresa cuántas cambió.
    /// </summary>
    public static int RunAtOpen(AppDbContext db, TursoSettings? settings) => IsBoundToCloud(db, settings) ? 0 : Run(db);

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
                TimeSpan? time;
                try
                {
                    time = LegacyTimeOfDay(row.Time);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                {
                    // Un texto raro nunca detiene la pasada: se deja como está
                    AppLogger.Warn($"Citas: la hora '{row.Time}' no se pudo leer y se deja como está ({ex.Message}).");
                    continue;
                }
                if (time is { } value) candidates[row.Id] = (row.Time, Format(value));
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
            if (!int.TryParse(match.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
                || !int.TryParse(match.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                || minutes > 59)
            {
                return null;
            }
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
