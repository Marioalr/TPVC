using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>Paso 8.26, ficha y agenda: chart-antecedents, chart-timeline, latest-vitals, vitals-evolution y agenda.</summary>
public partial class GoldenVectorTests
{
    // ================================================================== contracts/golden/chart-antecedents.json

    /// <summary>Los campos del expediente que lee la ficha, con sus nombres de PatientRecord (en camelCase).</summary>
    private static readonly string[] AntecedentFields =
    {
        "fullName", "gender", "birthDate", "storedAge", "familyHistory", "allergies", "chronicDiseases", "surgeries", "hospitalizations",
        "transfusions", "immunizationSchedule", "pregnanciesCount", "cesareansCount", "abortionsCount", "menarcheAge", "periodDuration",
        "papSmear", "sexualOnsetAge", "isCurrentlyPregnant", "gestationalWeeks", "developmentalMilestones", "occupation", "diet",
        "housingConditions", "zoonosis", "religion", "phone", "email", "address", "emergencyContactName", "emergencyContactPhone"
    };

    private static PatientRecord ChartPatient(JsonNode n) => new()
    {
        FullName = Str(n, "fullName"), Gender = S(n, "gender") ?? "No especificado", BirthDate = DateOrNull(n, "birthDate"), Age = Int(n, "storedAge"),
        FamilyHistory = Str(n, "familyHistory"), Allergies = Str(n, "allergies"), ChronicDiseases = Str(n, "chronicDiseases"), Surgeries = Str(n, "surgeries"),
        Hospitalizations = Str(n, "hospitalizations"), Transfusions = Str(n, "transfusions"), ImmunizationSchedule = Str(n, "immunizationSchedule"),
        PregnanciesCount = Int(n, "pregnanciesCount"), CesareansCount = Int(n, "cesareansCount"), AbortionsCount = Int(n, "abortionsCount"),
        MenarcheAge = Str(n, "menarcheAge"), PeriodDuration = Str(n, "periodDuration"), PapSmear = Str(n, "papSmear"), SexualOnsetAge = Str(n, "sexualOnsetAge"),
        IsCurrentlyPregnant = Bool(n, "isCurrentlyPregnant"), GestationalWeeks = Str(n, "gestationalWeeks"), DevelopmentalMilestones = Str(n, "developmentalMilestones"),
        Occupation = Str(n, "occupation"), Diet = Str(n, "diet"), HousingConditions = Str(n, "housingConditions"), Zoonosis = Str(n, "zoonosis"),
        Religion = Str(n, "religion"), Phone = Str(n, "phone"), Email = Str(n, "email"), Address = Str(n, "address"),
        EmergencyContactName = Str(n, "emergencyContactName"), EmergencyContactPhone = Str(n, "emergencyContactPhone")
    };

    [TestMethod]
    public void ChartAntecedents_MatchesTheVectors()
    {
        var cases = new List<RuleCase>
        {
            new("hombre adulto con todo", new JsonObject
            {
                ["today"] = "2026-10-15", ["fullName"] = "José Núñez", ["gender"] = "Masculino", ["birthDate"] = "1980-05-20",
                ["familyHistory"] = "Madre con diabetes tipo 2\nPadre hipertenso", ["allergies"] = "Penicilina", ["chronicDiseases"] = " Hipertensión arterial ",
                ["surgeries"] = "Apendicectomía (2001)", ["hospitalizations"] = "", ["transfusions"] = "Negadas", ["immunizationSchedule"] = "Influenza 2025",
                ["occupation"] = "Contador", ["diet"] = "Alta en sodio", ["housingConditions"] = "Casa con todos los servicios", ["zoonosis"] = "Un perro",
                ["religion"] = "Católica", ["phone"] = "+52 55 1234 5678", ["email"] = "jose@correo.mx", ["address"] = "Av. Juárez 10, CDMX",
                ["emergencyContactName"] = " Ana Núñez ", ["emergencyContactPhone"] = "5598765432"
            }),
            new("mujer con antecedentes gineco-obstétricos y embarazo actual", new JsonObject
            {
                ["today"] = "2026-10-15", ["fullName"] = "Ana López", ["gender"] = "Femenino", ["birthDate"] = "1994-01-10", ["allergies"] = "Negadas",
                ["pregnanciesCount"] = 2, ["cesareansCount"] = 1, ["abortionsCount"] = 0, ["menarcheAge"] = "12", ["periodDuration"] = "28 x 5 días",
                ["papSmear"] = "2025, normal", ["sexualOnsetAge"] = "19", ["isCurrentlyPregnant"] = true, ["gestationalWeeks"] = "20",
                ["emergencyContactName"] = "Luis López"
            }),
            new("embarazo actual sin semanas y sin gestas: la fórmula no va", new JsonObject
            {
                ["today"] = "2026-10-15", ["fullName"] = "Eva Ruiz", ["gender"] = "Femenino", ["birthDate"] = "2000-01-10", ["allergies"] = "",
                ["isCurrentlyPregnant"] = true
            }),
            new("niña de 9 años: desarrollo y vacunas, sin gineco-obstétricos; la vacunación sale de patológicos", new JsonObject
            {
                ["today"] = "2026-10-15", ["fullName"] = "Sofía Peña", ["gender"] = "Femenino", ["birthDate"] = "2017-02-01", ["allergies"] = "Ninguna",
                ["immunizationSchedule"] = "Completo para la edad", ["developmentalMilestones"] = "Caminó al año\nHabla bien", ["menarcheAge"] = "8"
            }),
            new("niña de 10 años: ya lleva la sección gineco-obstétrica (vacía) y la de desarrollo", new JsonObject
            {
                ["today"] = "2026-10-15", ["fullName"] = "Lucía", ["gender"] = "femenino", ["birthDate"] = "2016-10-15"
            }),
            new("expediente vacío sin fecha: las alergias siempre dicen algo", new JsonObject
            {
                ["today"] = "2026-10-15", ["fullName"] = "Paciente", ["storedAge"] = 40
            }),
            new("contacto de emergencia sin teléfono", new JsonObject
            {
                ["today"] = "2026-10-15", ["fullName"] = "Mario", ["gender"] = "Masculino", ["storedAge"] = 30, ["emergencyContactName"] = "Rosa", ["phone"] = "123"
            }),
        };

        CheckRuleVectors("chart-antecedents.json", "chart-antecedents", cases, input =>
            OnDay(ParseLocal(Str(input, "today")), () => (JsonNode)new JsonArray(PatientChartRules.BuildAntecedentSections(ChartPatient(input))
                .Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["title"] = s.Title, ["summary"] = s.Summary, ["detail"] = s.Detail, ["isEmpty"] = s.IsEmpty })
                .ToArray())),
            "Las secciones de antecedentes NOM-004 de la ficha (Services/Rules/PatientChartRules.BuildAntecedentSections, antes en PatientChartViewModel). La entrada son los campos de PatientRecord en camelCase ('" + string.Join("', '", AntecedentFields) + "'); 'storedAge' es la edad capturada sin fecha; 'today', el día del consultorio para la edad (patient-identity.json).",
            "Orden: Heredofamiliares; Patologicos (Alergias con su resumen de allergy-check.json, Enfermedades crónicas, Cirugías, Hospitalizaciones, Transfusiones y, solo si NO es pediátrico, Vacunación); Gineco (solo showsGynecoObstetric: 'Gestas / cesáreas / abortos: G2 C1 A0' si la suma es mayor que 0, 'Menarca: N años', Ciclo, Papanicolaou, 'Inicio de vida sexual' y 'Embarazo actual' o 'Embarazo actual: N semanas'); Desarrollo (solo pediátrico: Desarrollo y Vacunación); NoPatologicos (Ocupación, Alimentación, Vivienda, Animales, Religión); Contacto (Teléfono con el formato de patient-identity.json, Correo, Domicilio y 'Emergencia: nombre teléfono' sin espacios de más).",
            "Cada dato es 'Etiqueta: valor sin espacios a los lados' y no va si está vacío. detail: los datos unidos por \\n. summary: 'Sin registrar' sin datos; si no, los dos primeros datos sin su etiqueta (lo que sigue al primer ': ', así que 'Embarazo actual: 20 semanas' queda '20 semanas') unidos por ' · '. isEmpty: detail vacío.");
    }

    // ================================================================== contracts/golden/chart-timeline.json

    private static JsonObject Visit(string date, string folio, string diagnosis = "", string complaint = "", string vitals = "", string plan = "",
        (string Name, string? Presentation, string Dosage)[]? prescriptions = null, string[]? studies = null, string notes = "", string review = "", string exam = "",
        string labIndications = "")
    {
        var o = new JsonObject { ["date"] = date, ["folio"] = folio, ["diagnosis"] = diagnosis, ["chiefComplaint"] = complaint, ["vitalSigns"] = vitals, ["treatmentPlan"] = plan };
        if (notes.Length > 0) o["notes"] = notes;
        if (review.Length > 0) o["systemsReview"] = review;
        if (exam.Length > 0) o["physicalExamination"] = exam;
        if (prescriptions != null)
        {
            o["prescriptions"] = new JsonArray(prescriptions.Select(p => (JsonNode)new JsonObject { ["medicineName"] = p.Name, ["presentation"] = p.Presentation, ["dosage"] = p.Dosage }).ToArray());
        }
        if (studies != null) o["labStudies"] = Strings(studies);
        if (labIndications.Length > 0) o["labIndications"] = labIndications;
        return o;
    }

    private static JsonObject Appt(string date, string time, string reason, string status) =>
        new() { ["date"] = date, ["time"] = time, ["reason"] = reason, ["status"] = status };

    private static PatientConsultation ToConsultation(JsonNode n)
    {
        var c = new PatientConsultation
        {
            Date = ParseLocal(Str(n, "date")), Folio = Str(n, "folio"), Diagnosis = Str(n, "diagnosis"), ChiefComplaint = Str(n, "chiefComplaint"),
            VitalSigns = Str(n, "vitalSigns"), TreatmentPlan = Str(n, "treatmentPlan"), Notes = Str(n, "notes"), SystemsReview = Str(n, "systemsReview"),
            PhysicalExamination = Str(n, "physicalExamination"),
            BloodPressureSystolic = DecOrNull(n, "systolic"), BloodPressureDiastolic = DecOrNull(n, "diastolic"), HeartRate = DecOrNull(n, "heartRate"),
            Temperature = DecOrNull(n, "temperature"), Weight = DecOrNull(n, "weight"), Height = DecOrNull(n, "height"), BMI = DecOrNull(n, "bmi"),
            Glucose = DecOrNull(n, "glucose"), OxygenSaturation = DecOrNull(n, "oxygenSaturation")
        };
        if (n["prescriptions"] is JsonArray prescriptions)
        {
            c.Prescriptions = prescriptions.Select(p => new PatientPrescriptionItem { MedicineName = Str(p, "medicineName"), Presentation = S(p, "presentation"), Dosage = Str(p, "dosage") }).ToList();
        }
        if (n["labStudies"] is JsonArray studies)
        {
            c.LabOrder = new LabOrder { Studies = studies.Select(s => new LabOrderStudy { Name = s!.GetValue<string>(), Area = "Otros" }).ToList(), Indications = Str(n, "labIndications") };
        }
        return c;
    }

    private static PatientAppointment ToAppointment(JsonNode n) => new()
    {
        SyncId = S(n, "syncId") is { } sync ? Guid.Parse(sync) : Guid.Empty,
        PatientName = Str(n, "patientName"), Date = ParseLocal(Str(n, "date")), Time = Str(n, "time"),
        DurationMinutes = Int(n, "durationMinutes", 30), Reason = Str(n, "reason"), Status = S(n, "status") ?? "Programada"
    };

    private static JsonObject TimelineNode(TimelineEntry e) => new()
    {
        ["kind"] = e.Kind == TimelineKind.Consultation ? "consulta" : "cita",
        ["date"] = LocalText(e.Date),
        ["title"] = e.Title,
        ["side"] = e.Side,
        ["lines"] = new JsonArray(e.Lines.Select(l => (JsonNode)new JsonObject { ["label"] = l.Label, ["value"] = l.Value }).ToArray()),
        ["detail"] = e.Detail
    };

    [TestMethod]
    public void ChartTimeline_MatchesTheVectors()
    {
        var longPlan = "Reposo relativo. " + string.Concat(Enumerable.Repeat("Hidratación abundante y vigilar datos de alarma. ", 4)).TrimEnd() + "\nSegundo renglón";
        var consultations = new JsonArray(
            Visit("2026-10-12 10:30", "CNS-20261012-103000", "Faringitis aguda", "Dolor de garganta", "TA: 120/80 mmHg | FC: 76 lpm | Temp: 38.2 °C",
                "Reposo y líquidos", new[] { ("Paracetamol 500 mg tabletas", (string?)"Caja con 20 tabletas", "1 tableta cada 8 horas por 3 días"), ("  ", null, "") },
                new[] { "Biometría hemática", "Exudado faríngeo" }, notes: "Evolución favorable", review: "Sin datos de alarma", exam: "Faringe hiperémica", labIndications: "En ayuno"),
            Visit("2026-10-12 08:00", "CNS-20261012-080000", complaint: "  Control de peso  ", vitals: "Peso: 80 kg", plan: longPlan),
            Visit("2026-09-01 09:00", "CNS-20260901-090000", diagnosis: "", complaint: ""),
            Visit("2026-10-14 18:00", "CNS-20261014-180000", diagnosis: " Lumbalgia mecánica ", complaint: "Dolor lumbar\r\ndesde ayer", plan: "\n\n  Ibuprofeno  \nCompresas"));
        var appointments = new JsonArray(
            Appt("2026-10-12", "09:15", "Primera vez", "Atendida"),
            Appt("2026-10-12", "17:00", "", "Programada"),
            Appt("2026-10-20", "3 PM", "Revisión de resultados", "Confirmada"),
            Appt("2026-09-01", "07:30", "Control", "Cancelada"));

        var cases = new List<RuleCase>
        {
            new("historial completo: por día de la más nueva a la más vieja; en un día, las consultas antes que las citas y cada grupo de la hora más tarde a la más temprana",
                new JsonObject { ["consultations"] = consultations.DeepClone(), ["appointments"] = appointments.DeepClone() }),
            new("sin nada", new JsonObject { ["consultations"] = new JsonArray(), ["appointments"] = new JsonArray() }),
        };
        foreach (var (name, filter, search) in new (string, string, string)[]
                 {
                     ("solo consultas", "Consultas", ""),
                     ("solo citas", "Citas", ""),
                     ("cualquier otro filtro es todo; buscar sin acentos en lo que no se ve en la tarjeta", "Todo", "hiperemica"),
                     ("buscar en la receta (presentación e indicación)", "Todo", "caja con 20"),
                     ("buscar en las citas: su hora y su estado", "Citas", "confirmada"),
                     ("buscar en el título", "Consultas", "LUMBALGIA"),
                     ("nada coincide", "Todo", "xyz"),
                 })
        {
            cases.Add(new RuleCase($"filtro · {name}", new JsonObject
            {
                ["consultations"] = consultations.DeepClone(), ["appointments"] = appointments.DeepClone(), ["filter"] = filter, ["search"] = search
            }));
        }

        CheckRuleVectors("chart-timeline.json", "chart-timeline", cases, input =>
            {
                var entries = PatientChartRules.BuildTimeline(Items(input, "consultations").Select(ToConsultation).ToList(), Items(input, "appointments").Select(ToAppointment).ToList());
                if (input.ContainsKey("filter")) entries = PatientChartRules.FilterTimeline(entries, S(input, "filter"), S(input, "search"));
                return new JsonArray(entries.Select(e => (JsonNode)TimelineNode(e)).ToArray());
            },
            "El historial de la ficha (Services/Rules/PatientChartRules.BuildTimeline y FilterTimeline, antes en PatientChartViewModel). Fechas en hora local del consultorio (yyyy-MM-dd HH:mm). Una consulta: date, folio, diagnosis, chiefComplaint, vitalSigns, treatmentPlan, notes, systemsReview, physicalExamination, prescriptions (medicineName, presentation, dosage) y labStudies/labIndications (LabOrderJson). Una cita: date, time, reason y status (todas las del paciente, también las canceladas).",
            "Consulta: title = el diagnóstico sin espacios a los lados, si no el motivo, si no 'Consulta'; side = el folio; lines (en este orden y solo las que tienen texto): 'Motivo' (solo si hay diagnóstico), 'Signos' (con ' | ' cambiado por ' · '), 'Receta' (los nombres sin espacios a los lados, sin vacíos, unidos por ' · '), 'Laboratorio' (los estudios unidos por ' · ') y 'Plan'. El valor de cada línea es su primer renglón no vacío sin espacios a los lados (\\r\\n cuenta como \\n); con más de 160 caracteres, los primeros 157 sin espacios al final y '…'. detail (lo que revisa la búsqueda) une con \\n los que tienen texto de: motivo, interrogatorio, exploración, signos, plan, notas, la receta (un renglón por medicamento: 'nombre presentación indicación'), los estudios (uno por renglón) y las indicaciones del laboratorio.",
            "Cita: date = su día más la hora de 'time' leída como en patient-summary.json (lo que no se lee, las 10:00); title = el motivo sin espacios a los lados o 'Cita'; detail = 'time · status' (la hora tal cual); side y lines vacíos.",
            "Orden: por el día (sin la hora) de más nuevo a más viejo; en el mismo día, las consultas antes que las citas; dentro de cada grupo, de la hora más tarde a la más temprana; iguales, en el orden de entrada (las consultas primero en el orden de la lista).",
            "filter: 'Consultas' o 'Citas' (exacto) deja solo esas, cualquier otro deja todo; search: text-search.json 'matches' con el título y el detail (vacío = todo). La ficha muestra la fecha como 'd MMM' y el año aparte con la cultura es-MX; esos textos dependen de los datos de la cultura del sistema y no van en el vector.");
    }

    // ================================================================== contracts/golden/latest-vitals.json

    private static JsonObject Point(string date, string? weight = null, string? systolic = null, string? diastolic = null, string? glucose = null, string? bmi = null)
    {
        var o = new JsonObject { ["date"] = date };
        if (weight != null) o["weight"] = weight;
        if (systolic != null) o["systolic"] = systolic;
        if (diastolic != null) o["diastolic"] = diastolic;
        if (glucose != null) o["glucose"] = glucose;
        if (bmi != null) o["bmi"] = bmi;
        return o;
    }

    private static VitalSignsEvolutionPoint ToPoint(JsonNode n) => new()
    {
        Date = ParseLocal(Str(n, "date")), Weight = DecOrNull(n, "weight"), Systolic = DecOrNull(n, "systolic"), Diastolic = DecOrNull(n, "diastolic"),
        Glucose = DecOrNull(n, "glucose"), BMI = DecOrNull(n, "bmi"), HeartRate = DecOrNull(n, "heartRate"), Temperature = DecOrNull(n, "temperature")
    };

    [TestMethod]
    public void LatestVitals_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, points) in new (string, JsonObject[])[]
                 {
                     ("sin mediciones", Array.Empty<JsonObject>()),
                     ("una medición: sin tendencia", new[] { Point("2026-10-01", "70.0", "120", "80", "95", "24.2") }),
                     ("subió, bajó y sin cambio", new[] { Point("2026-09-01", "72.40", "130", "85", "110", "25.0"), Point("2026-10-01", "71.2", "120", "80", "126", "25.0") }),
                     ("la T/A sube con la sistólica aunque baje la diastólica", new[] { Point("2026-09-01", systolic: "118", diastolic: "82"), Point("2026-10-01", systolic: "121", diastolic: "79") }),
                     ("la T/A igual: 'antes' sin flecha", new[] { Point("2026-09-01", systolic: "120", diastolic: "80"), Point("2026-10-01", systolic: "120", diastolic: "75") }),
                     ("cada signo con su última medición, aunque sean de días distintos", new[]
                     {
                         Point("2026-08-01", "68", "125", "85", "101"), Point("2026-09-01", weight: "69.5", systolic: "140"), Point("2026-10-01", glucose: "98")
                     }),
                     ("redondeo de presentación hacia arriba (no al par): 72.25 → 72.3; glucosa 94.5 → 95; diferencias 0.05 → 0.1", new[]
                     {
                         Point("2026-09-01", "72.20", glucose: "90", bmi: "24.15"), Point("2026-10-01", "72.25", glucose: "94.5", bmi: "24.25")
                     }),
                 })
        {
            cases.Add(new RuleCase(name, new JsonObject { ["points"] = new JsonArray(points.Select(p => (JsonNode)p).ToArray()) }));
        }

        CheckRuleVectors("latest-vitals.json", "latest-vitals", cases, input =>
            new JsonArray(PatientChartRules.BuildLatestVitals(Items(input, "points").Select(ToPoint).ToList())
                .Select(v => (JsonNode)new JsonObject { ["label"] = v.Label, ["value"] = v.Value, ["trend"] = v.Trend, ["isUp"] = v.IsUp }).ToArray()),
            "«Lo esencial» de la ficha (Services/Rules/PatientChartRules.BuildLatestVitals, antes en PatientChartViewModel) a partir de la serie de signos de vitals-evolution.json (de la más vieja a la más nueva). Los valores de entrada son decimales en texto.",
            "En este orden y solo si alguna medición lo tiene: 'T/A (mmHg)' con la última que tiene sistólica y diastólica (value = BloodPressureText de vitals-evolution.json) y la anterior que las tiene: trend 'antes 120/80' con '↑ ' delante si la sistólica subió o '↓ ' si bajó; isUp si subió. 'Peso (kg)', 'IMC' y 'Glucosa (mg/dL)': el último valor que hay y la diferencia con el anterior que lo tiene ('↑ 1.2', '↓ 0.5' o 'sin cambio'; vacío con una sola medición); isUp si subió.",
            "Formato invariante: peso e IMC '0.0', glucosa '0' (decimal, redondeo de presentación con el punto medio hacia arriba: 72.25 → 72.3, a diferencia de Math.Round). La diferencia se formatea igual, sin signo.");
    }

    // ================================================================== contracts/golden/vitals-evolution.json

    [TestMethod]
    public void VitalsEvolution_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, consultations) in new (string, JsonObject[])[]
                 {
                     ("los signos se leen del texto de la nota, en orden de fecha", new[]
                     {
                         Visit("2026-10-12 10:00", "B", vitals: "TA: 120/80 mmHg | FC: 76 lpm | FR: 18 rpm | Temp: 37.8 °C | Peso: 78 kg | Talla: 1.74 m"),
                         Visit("2026-09-01 10:00", "A", vitals: "TA: 135/85 mmHg | FC: 68 lpm | Temp: 36.4 °C | Glucosa: 134 mg/dL"),
                     }),
                     ("otras formas: T/A, Presión, coma decimal, talla en cm, SpO2 como fracción y el IMC escrito", new[]
                     {
                         Visit("2026-01-10 09:00", "A", vitals: "T/A: 110/70 | Temp: 36,5 | Peso: 15,5 kg | Talla: 95 cm | SpO2: 0.94"),
                         Visit("2026-02-10 09:00", "B", vitals: "Presión: 130 / 90 | Peso: 80 kg | Talla: 1.70 m | IMC: 24.2 kg/m² (Peso normal)"),
                         Visit("2026-03-10 09:00", "C", vitals: "SatO2 = 97 | glucosa: 99.5"),
                     }),
                     ("sin peso, T/A, glucosa, temperatura ni SpO2 no hay punto (la FC sola no cuenta)", new[]
                     {
                         Visit("2026-05-01 09:00", "A", vitals: "FC: 80 lpm | FR: 18 rpm"),
                         Visit("2026-05-02 09:00", "B", vitals: ""),
                         Visit("2026-05-03 09:00", "C", vitals: "Talla: 1.60 m"),
                     }),
                     ("los valores ya guardados en sus columnas ganan al texto", new[]
                     {
                         new JsonObject { ["date"] = "2026-06-01 09:00", ["folio"] = "A", ["vitalSigns"] = "Peso: 99 kg", ["weight"] = "70.5", ["systolic"] = "118", ["diastolic"] = "76" },
                     }),
                     ("el IMC calculado redondea a 2 decimales al par (25.125 → 25.12) y el peso F1 hacia arriba (64.25 → 64.3)", new[]
                     {
                         Visit("2026-07-01 09:00", "A", vitals: "Peso: 64.32 kg | Talla: 1.60 m"),
                         Visit("2026-07-02 09:00", "B", vitals: "Peso: 64.25 kg"),
                     }),
                     ("la misma fecha conserva el orden de la lista", new[]
                     {
                         Visit("2026-08-01 09:00", "A", vitals: "Peso: 70 kg"),
                         Visit("2026-08-01 09:00", "B", vitals: "Peso: 71 kg"),
                     }),
                 })
        {
            cases.Add(new RuleCase(name, new JsonObject { ["consultations"] = new JsonArray(consultations.Select(c => (JsonNode)c).ToArray()) }));
        }

        CheckRuleVectors("vitals-evolution.json", "vitals-evolution", cases, input =>
            {
                var patient = new PatientRecord { Consultations = Items(input, "consultations").Select(ToConsultation).ToList() };
                var points = patient.GetVitalSignsEvolution();
                var latest = PatientChartRules.LatestTexts(points);
                static string? D(decimal? v) => v?.ToString(CultureInfo.InvariantCulture);
                return new JsonObject
                {
                    ["points"] = new JsonArray(points.Select(p => (JsonNode)new JsonObject
                    {
                        ["date"] = LocalText(p.Date), ["weight"] = D(p.Weight), ["systolic"] = D(p.Systolic), ["diastolic"] = D(p.Diastolic),
                        ["glucose"] = D(p.Glucose), ["heartRate"] = D(p.HeartRate), ["temperature"] = D(p.Temperature), ["bmi"] = D(p.BMI),
                        ["dateText"] = p.FormattedDate, ["weightText"] = p.WeightText, ["bloodPressureText"] = p.BloodPressureText,
                        ["glucoseText"] = p.GlucoseText, ["temperatureText"] = p.TempText
                    }).ToArray()),
                    ["latest"] = new JsonObject { ["weight"] = latest.Weight, ["bloodPressure"] = latest.BloodPressure, ["glucose"] = latest.Glucose, ["bmi"] = latest.Bmi }
                };
            },
            "La serie de signos de la ficha (Models/PatientRecord.GetVitalSignsEvolution con PatientConsultation.ParseAndSetVitalSigns, y los textos de arriba de la gráfica, Services/Rules/PatientChartRules.LatestTexts). Cada consulta: date, folio, vitalSigns (el texto de la nota, vitals.json 'format') y, si ya están en sus columnas, systolic, diastolic, heartRate, temperature, weight, height, bmi, glucose y oxygenSaturation (decimales en texto). Decimales de salida en texto invariante con su escala.",
            "Por consulta, de la fecha más vieja a la más nueva (iguales en el orden de la lista): si no tiene sistólica, peso, glucosa ni SpO2 en sus columnas, se leen del texto (sin distinguir mayúsculas): T/A con '(T/?A|TA|PA|Presi[oó]n):' y 'dd/dd' (2 o 3 dígitos cada uno), 'FC:' 2-3 dígitos, 'FR:' 1-2 dígitos, '(Temp|Temperatura):', '(Peso|Weight):', '(Talla|Height|Estatura):' (más de 3 se toma en cm y se divide entre 100), '(SpO2%?|SatO2|SPO2)' con ':' o '=' opcional (de 0 a 1 se multiplica por 100), 'IMC:' y 'Glucosa:', cada número con dígitos, punto o coma (la coma vale punto). Sin IMC escrito y con peso y talla, IMC = Math.Round(peso / talla², 2) (decimal, al par).",
            "Hay punto si la consulta tiene peso, sistólica, glucosa, temperatura o SpO2 (la SpO2 no viaja en el punto). Textos con la cultura es-MX: dateText 'dd/MM/yy', weightText 'F1 kg', bloodPressureText 'F0/F0' (con las dos), glucoseText 'F0 mg/dL', temperatureText 'F1 °C'; '--' sin el dato. El formato F de decimal redondea el punto medio hacia arriba.",
            "latest (los textos de arriba de la gráfica, con el último punto): weight 'F1 kg', bloodPressure 'sistólica/diastólica mmHg' con cada decimal tal cual (con su escala), glucose 'F0 mg/dL', bmi 'F1'; '--' sin el dato o sin puntos.");
    }

    // ================================================================== contracts/golden/agenda.json

    private static JsonObject Cita(string id, string patient, string date, string time, int minutes = 30, string status = "Programada", string reason = "Consulta de seguimiento") =>
        new()
        {
            ["syncId"] = $"10000000-0000-4000-8000-{id.PadLeft(12, '0')}", ["patientName"] = patient, ["date"] = date, ["time"] = time,
            ["durationMinutes"] = minutes, ["status"] = status, ["reason"] = reason
        };

    private static JsonObject SlotNode(AgendaSlot s) => new()
    {
        ["time"] = s.TimeText,
        ["patientName"] = s.Appointment?.PatientName,
        ["title"] = s.Title,
        ["subtitle"] = s.Subtitle,
        ["overlap"] = s.OverlapText
    };

    [TestMethod]
    public void Agenda_MatchesTheVectors()
    {
        var week = new JsonArray(
            Cita("1", "Ana López", "2026-10-15", "10:15"),
            Cita("2", "Luis Ruiz", "2026-10-15", "10:30", 45),
            Cita("3", "Ángel Díaz", "2026-10-15", "10:30", 15),
            Cita("4", "Eva Peña", "2026-10-15", "12:00", 0, "Confirmada", "Resultados de laboratorio"),
            Cita("5", "Cancelada Sí", "2026-10-15", "13:00", 60, "Cancelada"),
            Cita("6", "Temprano Pérez", "2026-10-15", "07:30", 60, "Atendida"),
            Cita("7", "Sin Hora", "2026-10-15", "3 PM"),
            Cita("8", "Noche Gómez", "2026-10-15", "19:45", 30),
            Cita("9", "Otro Día", "2026-10-16", "09:00"),
            Cita("10", "Domingo Ruiz", "2026-10-18", "11:00"),
            Cita("11", "Semana Pasada", "2026-10-11", "11:00"));

        var cases = new List<RuleCase>
        {
            new("huecos · un día sin citas en el futuro: 24 huecos de 8:00 a 19:30", new JsonObject { ["check"] = "slots", ["date"] = "2026-10-16", ["now"] = "2026-10-14 09:00", ["appointments"] = new JsonArray() }),
            new("huecos · hoy no ofrece horas pasadas (el hueco que ya empezó tampoco)", new JsonObject { ["check"] = "slots", ["date"] = "2026-10-16", ["now"] = "2026-10-16 12:10", ["appointments"] = new JsonArray() }),
            new("huecos · citas que ocupan huecos por su duración, empalmes, canceladas fuera, antes de las 8, hora que no se lee (10:00) y la que pasa de las 20:00",
                new JsonObject { ["check"] = "slots", ["date"] = "2026-10-15", ["now"] = "2026-10-01 08:00", ["appointments"] = week.DeepClone() }),
            new("huecos · hoy a las 11:00: las citas pasadas se quedan, los huecos pasados no",
                new JsonObject { ["check"] = "slots", ["date"] = "2026-10-15", ["now"] = "2026-10-15 11:00", ["appointments"] = week.DeepClone() }),
            new("semana · de lunes a domingo, sin canceladas (un domingo es el último día de su semana)", new JsonObject { ["check"] = "week", ["date"] = "2026-10-18", ["appointments"] = week.DeepClone() }),
            new("semana · el lunes es el inicio", new JsonObject { ["check"] = "week", ["date"] = "2026-10-12", ["appointments"] = week.DeepClone() }),
            new("hoy en el inicio · sin canceladas, por hora (la que no se lee cuenta como 10:00) y con empalmes", new JsonObject
            {
                ["check"] = "today", ["appointments"] = new JsonArray(week.Where(a => a!["date"]!.GetValue<string>() == "2026-10-15").Select(a => a!.DeepClone()).ToArray())
            }),
            new("próxima cita · ni cancelada ni atendida, desde hoy, por fecha y hora", new JsonObject
            {
                ["check"] = "next", ["today"] = "2026-10-15",
                ["appointments"] = new JsonArray(Cita("1", "P", "2026-10-14", "09:00"), Cita("2", "P", "2026-10-20", "09:00"), Cita("3", "P", "2026-10-15", "18:00", status: "Atendida"),
                    Cita("4", "P", "2026-10-15", "3 PM"), Cita("5", "P", "2026-10-15", "09:30", status: "Cancelada"))
            }),
            new("próxima cita · ninguna", new JsonObject { ["check"] = "next", ["today"] = "2026-10-15", ["appointments"] = new JsonArray(Cita("1", "P", "2026-10-14", "09:00")) }),
        };
        foreach (var (name, overlapping) in new (string, JsonObject[])[]
                 {
                     ("una", new[] { Cita("1", "Ana López", "2026-10-15", "10:30") }),
                     ("dos", new[] { Cita("1", "Ana López", "2026-10-15", "9:05"), Cita("2", "Luis Ruiz", "2026-10-15", "10:30") }),
                     ("tres", new[] { Cita("1", "Ana", "2026-10-15", "10:00"), Cita("2", "Luis", "2026-10-15", "10:15"), Cita("3", "Eva", "2026-10-15", "10:20") }),
                 })
        {
            cases.Add(new RuleCase($"pregunta de empalme · {name}", new JsonObject { ["check"] = "overlap-question", ["appointments"] = new JsonArray(overlapping.Select(o => (JsonNode)o).ToArray()) }));
        }
        foreach (var (name, date) in new (string, string)[] { ("título del día", "2026-10-12"), ("título de un miércoles", "2026-10-14") })
        {
            cases.Add(new RuleCase($"textos · {name}", new JsonObject { ["check"] = "titles", ["date"] = date }));
        }

        CheckRuleVectors("agenda.json", "agenda", cases, input =>
            {
                var appointments = Items(input, "appointments").Select(ToAppointment).ToList();
                switch (S(input, "check"))
                {
                    case "slots":
                    {
                        var slots = AgendaRules.BuildSlots(ParseLocal(Str(input, "date")), appointments, ParseLocal(Str(input, "now")));
                        return new JsonObject
                        {
                            ["slots"] = new JsonArray(slots.Select(s => (JsonNode)SlotNode(s)).ToArray()),
                            ["summary"] = AgendaRules.DaySummary(slots)
                        };
                    }
                    case "week":
                        return new JsonArray(AgendaRules.Week(ParseLocal(Str(input, "date")), appointments)
                            .Select(d => (JsonNode)new JsonObject { ["date"] = LocalText(d.Date), ["count"] = d.Count, ["countText"] = AgendaRules.DayCountText(d.Count) }).ToArray());
                    case "today":
                        return new JsonArray(AgendaRules.TodayAppointments(appointments)
                            .Select(a => (JsonNode)new JsonObject { ["patientName"] = a.PatientName, ["time"] = a.Time, ["overlap"] = a.OverlapText }).ToArray());
                    case "next":
                        return AgendaRules.NextAppointment(appointments, ParseLocal(Str(input, "today"))) is { } next
                            ? new JsonObject { ["syncId"] = SyncIds.Format(next.SyncId), ["date"] = LocalText(next.Date), ["time"] = next.Time }
                            : null;
                    case "overlap-question":
                        return AgendaRules.OverlapQuestion(appointments);
                    case "titles":
                    {
                        var date = ParseLocal(Str(input, "date"));
                        return new JsonObject { ["panelTitle"] = AgendaRules.PanelTitle(date), ["weekTitle"] = AgendaRules.WeekTitle(date), ["weekStart"] = LocalText(AgendaRules.StartOfWeek(date)) };
                    }
                    default:
                        throw new ArgumentException(S(input, "check"));
                }
            },
            "La agenda (Services/Rules/AgendaRules.cs, antes en AgendaPanelViewModel y PatientsHomeViewModel). Cada cita: syncId, patientName, date (yyyy-MM-dd), time (texto; se lee como en patient-summary.json, lo que no se lee son las 10:00), durationMinutes, status y reason. 'now' es la hora local del consultorio.",
            "slots (BuildSlots): las citas del día que no están 'Cancelada', cada una en su hora, más los huecos libres de 30 minutos que empiezan de las 08:00 a las 19:30. Un hueco [t, t+30) no se ofrece si alguna cita del día [inicio, inicio + duración) se cruza con él (duración 0 o negativa = 30); el día de hoy (la fecha de 'now') no se ofrece un hueco cuyo inicio ya pasó. Orden por hora, la cita antes que el hueco libre a la misma hora; citas a la misma hora en su orden de entrada. title: el paciente o 'Libre'; subtitle: 'motivo · N min' (la duración guardada, aunque sea 0) o '+ Agendar aquí'. overlap: la marca 'Empalme con {paciente} ({HH:mm})' y ' y N más' si hay varias (la primera por hora y luego por SyncId), con AppointmentRules.Overlaps (appointment-overlap.json). summary: 'N citas · M huecos libres' (singular con 1).",
            "week: los 7 días desde el lunes de la semana de 'date', con sus citas sin las canceladas; countText 'libre', '1 cita' o 'N citas'. today (las citas de hoy del inicio; la entrada ya son las del día): sin canceladas, por hora y con la marca de empalme. next (la próxima cita de la ficha): la primera por fecha y hora de las que no están 'Cancelada' ni 'Atendida' con fecha de hoy en adelante; null si no hay. overlap-question: la pregunta al agendar encima de otras ('a las HH:mm con nombre', unidas con ', ' y ' y ' antes de la última).",
            "titles: panelTitle 'dddd d de MMMM' de es-MX con la primera letra en mayúscula; weekTitle 'd – d de MMMM yyyy' si la semana no cambia de mes (si cambia usa meses abreviados, que dependen de los datos de la cultura del sistema: no van en el vector); weekStart, el lunes.");
    }
}
