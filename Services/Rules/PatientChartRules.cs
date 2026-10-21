using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// La ficha del paciente (paso 8.26; antes en <c>PatientChartViewModel</c> y <c>VitalSignsChartViewModel</c>, sin cambios): los
/// antecedentes por sección, el historial (consultas y citas, con su filtro y su búsqueda), "Lo esencial" de los signos y
/// los últimos valores de la gráfica. Sin WPF ni base: vectores en <c>contracts/golden/chart-antecedents.json</c>,
/// <c>chart-timeline.json</c>, <c>latest-vitals.json</c> y <c>vitals-evolution.json</c>.
/// </summary>
public static class PatientChartRules
{
    /// <summary>
    /// Las secciones de antecedentes NOM-004 en su orden: heredofamiliares, patológicos, gineco-obstétricos (mujeres de 10
    /// años o más), desarrollo y vacunas (menores de 18), no patológicos y contacto. Cada una con su resumen (los dos
    /// primeros datos sin su etiqueta, o "Sin registrar") y su detalle (un dato por renglón). La edad sale de
    /// <see cref="ClinicClock"/>.
    /// </summary>
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

    /// <summary>
    /// Historial de la ficha, del más reciente al más antiguo: una tarjeta por consulta, con su fecha y el resumen de
    /// sus datos (motivo, signos, receta y plan), y las citas. El detalle por datos se ve al abrir la consulta.
    /// </summary>
    public static List<TimelineEntry> BuildTimeline(IEnumerable<PatientConsultation> consultations, IEnumerable<PatientAppointment> appointments)
    {
        var entries = new List<(TimelineEntry Entry, int Order)>();
        foreach (var c in consultations)
        {
            var title = !string.IsNullOrWhiteSpace(c.Diagnosis) ? c.Diagnosis.Trim() : (string.IsNullOrWhiteSpace(c.ChiefComplaint) ? "Consulta" : c.ChiefComplaint.Trim());
            var prescriptions = c.Prescriptions;
            var labOrder = c.LabOrder;
            var lines = new List<TimelineLine>();
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) lines.Add(new TimelineLine(label, FirstLine(value)));
            }
            // Con diagnóstico el título lo dice; el motivo va abajo
            if (!string.IsNullOrWhiteSpace(c.Diagnosis)) Add("Motivo", c.ChiefComplaint);
            Add("Signos", c.VitalSigns?.Replace(" | ", " · "));
            Add("Receta", string.Join(" · ", prescriptions.Select(r => r.MedicineName.Trim()).Where(n => n.Length > 0)));
            Add("Laboratorio", string.Join(" · ", labOrder.Studies.Select(s => s.Name)));
            Add("Plan", c.TreatmentPlan);

            // La búsqueda revisa todo lo de la consulta, también lo que no se ve en la tarjeta
            var searchText = string.Join("\n", new[]
            {
                c.ChiefComplaint, c.SystemsReview, c.PhysicalExamination, c.VitalSigns, c.TreatmentPlan, c.Notes,
                string.Join("\n", prescriptions.Select(r => $"{r.MedicineName} {r.Presentation} {r.Dosage}")),
                string.Join("\n", labOrder.Studies.Select(s => s.Name)), labOrder.Indications
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            entries.Add((new TimelineEntry(TimelineKind.Consultation, c.Date, title, searchText, c.Folio, c, lines), 0));
        }

        foreach (var a in appointments)
        {
            var when = a.Date.Date + PatientSummaryRules.TimeOfDay(a.Time);
            entries.Add((new TimelineEntry(TimelineKind.Appointment, when,
                string.IsNullOrWhiteSpace(a.Reason) ? "Cita" : a.Reason.Trim(),
                $"{a.Time} · {a.Status}"), 1));
        }

        return entries
            .OrderByDescending(x => x.Entry.Date.Date)
            .ThenBy(x => x.Order)
            .ThenByDescending(x => x.Entry.Date)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>El primer renglón de un texto, para la tarjeta (el resto se ve al abrir la consulta).</summary>
    public static string FirstLine(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length > 160 ? line[..157].TrimEnd() + "…" : line;
    }

    /// <summary>
    /// "Lo esencial" de la ficha con la serie de signos (de la más vieja a la más nueva): T/A (la última con las dos cifras y la
    /// anterior), peso, IMC y glucosa, cada uno con su último valor y la diferencia contra el anterior que lo tiene.
    /// </summary>
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

    /// <summary>
    /// El historial que se ve: el filtro ("Consultas", "Citas" o cualquier otro = todo) y la búsqueda sin acentos en el
    /// título y en todo el texto de la entrada.
    /// </summary>
    public static List<TimelineEntry> FilterTimeline(IEnumerable<TimelineEntry> entries, string? filter, string? search)
    {
        var kind = filter switch
        {
            "Consultas" => TimelineKind.Consultation,
            "Citas" => TimelineKind.Appointment,
            _ => (TimelineKind?)null
        };
        var query = TextSearch.Normalize(search);
        return entries
            .Where(e => kind == null || e.Kind == kind)
            .Where(e => TextSearch.Matches(query, e.Title, e.Detail))
            .ToList();
    }

    /// <summary>Los últimos valores de la gráfica de signos ("--" sin medición), con el formato de la cultura actual.</summary>
    public sealed record LatestVitalTexts(string Weight, string BloodPressure, string Glucose, string Bmi)
    {
        public static readonly LatestVitalTexts None = new("--", "--", "--", "--");
    }

    /// <summary>
    /// Los textos de arriba de la gráfica con el último punto de la serie: peso "F1 kg", T/A "sistólica/diastólica mmHg" (cada
    /// una como decimal tal cual), glucosa "F0 mg/dL" e IMC "F1"; cada uno "--" si el último punto no lo tiene.
    /// </summary>
    public static LatestVitalTexts LatestTexts(IReadOnlyList<VitalSignsEvolutionPoint> points)
    {
        var latest = points.LastOrDefault();
        if (latest == null) return LatestVitalTexts.None;
        return new LatestVitalTexts(
            latest.Weight.HasValue ? $"{latest.Weight.Value:F1} kg" : "--",
            (latest.Systolic.HasValue && latest.Diastolic.HasValue) ? $"{latest.Systolic.Value}/{latest.Diastolic.Value} mmHg" : "--",
            latest.Glucose.HasValue ? $"{latest.Glucose.Value:F0} mg/dL" : "--",
            latest.BMI.HasValue ? $"{latest.BMI.Value:F1}" : "--");
    }
}
