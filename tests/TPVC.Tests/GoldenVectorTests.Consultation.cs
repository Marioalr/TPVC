using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.26, consulta: vitals, pediatric-dose, consultation-account, sheet-charges, checkout e inventory-value. Las hojas y
/// el cobro se corren con la cuenta real (<see cref="ConsultationAccount"/> y <see cref="CheckoutViewModel"/>), sin base.
/// </summary>
public partial class GoldenVectorTests
{
    // ================================================================== contracts/golden/vitals.json

    [TestMethod]
    public void Vitals_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, weight, height) in new (string, string?, string?)[]
                 {
                     ("peso normal", "70", "1.75"),
                     ("coma decimal", "70,5", "1,70"),
                     ("punto medio del F1 hacia arriba (22.25 → 22.3)", "89", "2"),
                     ("bajo peso", "45", "1.60"),
                     ("sobrepeso", "80", "1.70"),
                     ("obesidad grado I", "95", "1.70"),
                     ("obesidad grado II+", "120", "1.70"),
                     ("talla en centímetros (revisión 20): 170 → 1.70 m, como al leer la nota", "70", "170"),
                     ("talla en centímetros con decimales", "15,5", "95,5"),
                     ("3 todavía son metros", "70", "3"),
                     ("3.01 ya son centímetros (0.0301 m)", "0.1", "3.01"),
                     ("sin peso", "", "1.70"),
                     ("talla 0", "70", "0"),
                     ("texto", "setenta", "1.70"),
                 })
        {
            cases.Add(new RuleCase($"IMC · {name}", new JsonObject { ["check"] = "bmi", ["weight"] = weight, ["height"] = height }));
        }
        foreach (var bmi in new[] { "18.49", "18.5", "24.99", "25", "29.99", "30", "34.99", "35" })
        {
            cases.Add(new RuleCase($"clasificación · {bmi}", new JsonObject { ["check"] = "bmi-category", ["bmi"] = bmi }));
        }

        static JsonObject W(string? temp = null, string? ta = null, string? sat = null, string? glu = null, string? fc = null, int? age = null) =>
            new() { ["check"] = "warning", ["temperature"] = temp, ["bloodPressure"] = ta, ["oxygenSaturation"] = sat, ["glucose"] = glu, ["heartRate"] = fc, ["patientAge"] = age };
        foreach (var (name, input) in new (string, JsonObject)[]
                 {
                     ("todo normal", W("36.5", "120/80", "98", "95", "76", 40)),
                     ("fiebre desde 38", W("38")),
                     ("37.9 no", W("37.9")),
                     ("coma decimal; el punto medio exacto del formato 0.# va hacia arriba: 38,25 → 38.3", W("38,25")),
                     ("hipotermia bajo 35.5", W("35.4")),
                     ("0 no cuenta", W("0", "0/0", "0", "0", "0")),
                     ("T/A elevada por la sistólica", W(ta: "140/85")),
                     ("T/A elevada por la diastólica", W(ta: "130/90")),
                     ("T/A baja", W(ta: "85/60")),
                     ("T/A sin diastólica no se lee", W(ta: "150/")),
                     ("SpO2 como fracción", W(sat: "0.91")),
                     ("SpO2 92 está bien", W(sat: "92")),
                     ("glucosa elevada", W(glu: "200")),
                     ("hipoglucemia", W(glu: "69.5")),
                     ("taquicardia de adulto", W(fc: "101", age: 12)),
                     ("un niño de 11 con 130 está bien", W(fc: "130", age: 11)),
                     ("un niño con 141", W(fc: "141", age: 5)),
                     ("bradicardia", W(fc: "49", age: 5)),
                     ("sin edad cuenta como adulto", W(fc: "120")),
                     ("varios en su orden", W("39.2", "160/100", "0.88", "250", "120", 40)),
                 })
        {
            cases.Add(new RuleCase($"aviso · {name}", input));
        }

        static JsonObject F(string? ta = null, string? fc = null, string? fr = null, string? temp = null, string? weight = null, string? height = null,
            string? glucose = null, string? sat = null, string? bmi = null) =>
            new()
            {
                ["check"] = "format", ["bloodPressure"] = ta, ["heartRate"] = fc, ["respiratoryRate"] = fr, ["temperature"] = temp, ["weight"] = weight,
                ["height"] = height, ["glucose"] = glucose, ["oxygenSaturation"] = sat, ["bmiText"] = bmi
            };
        foreach (var (name, input) in new (string, JsonObject)[]
                 {
                     ("todo", F("120/80", "76", "18", "36.5", "70", "1.75", "95", "98", "22.9 kg/m² (Peso normal)")),
                     ("la T/A sin la diagonal del final y la SpO2 sin su %", F(" 120/ ", sat: " 97 % ".Trim())),
                     ("espacios a los lados", F(fc: " 80 ", weight: " 15,5 ")),
                     ("el IMC no disponible no va", F(weight: "70", bmi: VitalSignsRules.BmiUnavailable)),
                     ("nada capturado", F()),
                     ("solo espacios", F("  ", "  ")),
                 })
        {
            cases.Add(new RuleCase($"texto de la nota · {name}", input));
        }

        CheckRuleVectors("vitals.json", "vitals", cases, input => S(input, "check") switch
            {
                "bmi" => VitalSignsRules.BmiText(S(input, "weight"), S(input, "height")),
                "bmi-category" => VitalSignsRules.BmiCategory(Dec(input, "bmi")),
                "warning" => VitalSignsRules.Warning(S(input, "temperature"), S(input, "bloodPressure"), S(input, "oxygenSaturation"), S(input, "glucose"),
                    S(input, "heartRate"), IntOrNull(input, "patientAge")),
                "format" => VitalSignsRules.Format(S(input, "bloodPressure"), S(input, "heartRate"), S(input, "respiratoryRate"), S(input, "temperature"),
                    S(input, "weight"), S(input, "height"), S(input, "glucose"), S(input, "oxygenSaturation"), S(input, "bmiText")),
                _ => throw new ArgumentException(S(input, "check"))
            },
            "Los signos de la consulta (Services/Rules/VitalSignsRules.cs): el IMC mientras se captura, el aviso de valores fuera de rango y el texto que se guarda en la nota. Lo que se captura llega como texto; cómo se lee de vuelta ese texto está en vitals-evolution.json.",
            "bmi: peso y talla con coma o punto decimal, cultura invariante; con los dos y talla mayor que 0, la talla en metros (revisión 20, VitalSignsRules.HeightInMeters: más de 3 son centímetros y se divide entre 100 en decimal; es la misma regla con que vitals-evolution.json lee la nota; antes la captura no la convertía y 170 daba '0.0 kg/m² (Bajo peso)'), imc = peso / talla² en decimal y 'F1 kg/m² (clasificación)' (F1 de decimal: el punto medio hacia arriba); si no, 'Valores no disponibles'. bmi-category (OMS): < 18.5 'Bajo peso', < 25 'Peso normal', < 30 'Sobrepeso', < 35 'Obesidad Grado I', si no 'Obesidad Grado II+'.",
            "warning: cada valor como double (coma = punto, sin espacios); en este orden y unidos por ' · ': Temp ≥ 38 'Temp {0.#} °C: fiebre', o 0 < Temp < 35.5 'hipotermia'; T/A 'sis/dia' ≥ 140 o ≥ 90 'T/A {0}/{0}: elevada', o 0 < sis < 90 'baja'; SpO₂ > 0 (≤ 1 se multiplica por 100) < 92 'SpO₂ {0} %: baja'; glucosa > 0, ≥ 200 'elevada' o < 70 'hipoglucemia'; FC > 0: adulto (sin edad o 12 años o más) > 100 'taquicardia', niño (menos de 12) > 140 'alta para la edad', y si no < 50 'bradicardia'. Los números con '0.#' o '0' de double con la cultura es-MX: .NET formatea el valor binario más corto y redondea el punto medio hacia arriba (38.25 → 38.3; 69.5 → 70, así que 'Glucosa 70 mg/dL: hipoglucemia' sale con 69.5).",
            "format (lo que se guarda en PatientConsultation.VitalSigns al terminar): cada dato capturado sin espacios a los lados y unidos por ' | ': 'TA: {sin la / del final} mmHg', 'FC: … lpm', 'FR: … rpm', 'Temp: … °C', 'Peso: … kg', 'Talla: … m', 'Glucosa: … mg/dL', 'SpO2: {sin % al final}%', 'IMC: {el texto de bmi}' (no va si contiene 'no disponibles'). Lo vacío o solo espacios no va; sin nada, ''.");
    }

    // ================================================================== contracts/golden/pediatric-dose.json

    private static PediatricDoseRule ToDoseRule(JsonNode? n) => new()
    {
        Medication = Str(n, "medication"), DoseText = Str(n, "doseText"), MlPerKg = DblOrNull(n, "mlPerKg"), DropsPerMl = IntOrNull(n, "dropsPerMl")
    };

    [TestMethod]
    public void PediatricDose_MatchesTheVectors()
    {
        var paracetamol = new JsonObject { ["medication"] = "Paracetamol gotas 100 mg/ml", ["doseText"] = "15 mg/kg/dosis", ["mlPerKg"] = 0.15, ["dropsPerMl"] = 20 };
        var syrup = new JsonObject { ["medication"] = "Paracetamol suspensión 3.2 g/100 ml", ["doseText"] = "15 mg/kg/dosis", ["mlPerKg"] = 0.46875 };
        var half = new JsonObject { ["medication"] = "Ibuprofeno 100 mg/5 ml", ["doseText"] = "", ["mlPerKg"] = 0.5, ["dropsPerMl"] = 15 };
        var noFormula = new JsonObject { ["medication"] = "Amoxicilina", ["doseText"] = "Ver tabla" };

        var cases = new List<RuleCase>();
        foreach (var (name, rule, weight) in new (string, JsonObject?, string?)[]
                 {
                     ("gotas: ml redondeado a un decimal y gotas de esos ml", paracetamol, "12.5"),
                     ("coma decimal", syrup, "15,5"),
                     ("Math.Round al par: 5.25 ml → 5.2", half, "10.5"),
                     ("gotas al par: 2.5 ml × 15 = 37.5 → 38", half, "5"),
                     ("2.25 ml → 2.2", half, "4.5"),
                     ("sin gotas por ml", syrup, "10"),
                     ("sin fórmula", noFormula, "10"),
                     ("sin peso", paracetamol, ""),
                     ("peso 0", paracetamol, "0"),
                     ("peso negativo", paracetamol, "-3"),
                     ("sin regla", null, "10"),
                 })
        {
            cases.Add(new RuleCase($"calculadora · {name}", new JsonObject { ["check"] = "dose", ["rule"] = rule?.DeepClone(), ["weight"] = weight }));
        }

        static JsonObject Weights(bool active, string? current, params (string Date, string? Weight)[] consultations) => new()
        {
            ["check"] = "suggest-weight", ["hasActiveConsultation"] = active, ["currentWeight"] = current,
            ["consultations"] = new JsonArray(consultations.Select(c => (JsonNode)new JsonObject { ["date"] = c.Date, ["weight"] = c.Weight }).ToArray())
        };
        foreach (var (name, input) in new (string, JsonObject)[]
                 {
                     ("el de la consulta en curso", Weights(true, "18,25", ("2026-09-01 10:00", "17"))),
                     ("70 o más en la consulta: el último de las consultas", Weights(true, "70", ("2026-09-01 10:00", "17"), ("2026-10-01 10:00", "17.25"), ("2026-08-01 10:00", "16"))),
                     ("sin consulta en curso no se mira lo capturado", Weights(false, "18", ("2026-09-01 10:00", "17.04"))),
                     ("las consultas sin peso o con 0 no cuentan", Weights(false, null, ("2026-09-01 10:00", "16.5"), ("2026-10-01 10:00", "0"), ("2026-10-02 10:00", null))),
                     ("nada", Weights(true, "", Array.Empty<(string, string?)>())),
                 })
        {
            cases.Add(new RuleCase($"peso sugerido · {name}", input));
        }

        CheckRuleVectors("pediatric-dose.json", "pediatric-dose", cases, input =>
            {
                if (S(input, "check") == "suggest-weight")
                {
                    var consultations = Items(input, "consultations").Select(c => new PatientConsultation { Date = ParseLocal(Str(c, "date")), Weight = DecOrNull(c, "weight") }).ToList();
                    return PediatricDoseRules.SuggestWeight(Bool(input, "hasActiveConsultation"), S(input, "currentWeight"), consultations);
                }
                var rule = input["rule"] is { } r ? ToDoseRule(r) : null;
                var weightText = S(input, "weight");
                PediatricDoseRules.TryParseNumber(weightText, out var weight);
                return new JsonObject
                {
                    ["displayName"] = rule?.DisplayName,
                    ["ml"] = rule?.CalculateMl(weight),
                    ["drops"] = rule?.CalculateDrops(weight),
                    ["result"] = PediatricDoseRules.ResultText(rule, weightText),
                    ["dosage"] = PediatricDoseRules.DosageText(rule, weightText)
                };
            },
            "La calculadora de dosis pediátrica de la receta (Services/Rules/PediatricDoseRules.cs y Models/PediatricDoseRule, la hoja DOSIS PEDIA del Excel). La regla: medication, doseText, mlPerKg y dropsPerMl (opcionales). El peso es el texto capturado (coma o punto decimal, sin espacios a los lados, cultura invariante; lo que no se lee vale 0).",
            "ml = Math.Round(peso × mlPerKg, 1) si mlPerKg > 0 y peso > 0 (si no, null); drops = Math.Round(ml × dropsPerMl, 0) si hay ml y dropsPerMl > 0. Math.Round de double de .NET: multiplica por 10^d en double, redondea al par y divide (en Kotlin Math.rint(x * 10.0) / 10.0); así 5.25 → 5.2 y 37.5 → 38.",
            "result (lo que muestra): '' sin regla; 'Esta regla no tiene fórmula de ml por kg en el Excel.' sin mlPerKg > 0; 'Capture el peso del paciente (kg).' si el peso no se lee o no es mayor que 0; si no '{ml 0.#} ml ({gotas 0} gotas) por dosis' o '{ml 0.#} ml por dosis' (0.# invariante). dosage (lo que copia «Aplicar» a la indicación; si el nombre del medicamento está vacío también lo llena con 'medication'): null si no hay regla, el peso no se lee o no hay ml; si no 'Dar {ml} ml ({gotas} gotas) vía oral cada ___ hrs por ___ días' o sin las gotas. displayName: 'medication • doseText' o solo medication.",
            "suggest-weight (el peso con que se llena al elegir una regla): con una consulta en curso, lo capturado si es mayor que 0 y menor que 70; si no, el último peso mayor que 0 de las consultas del paciente por fecha; '' si no hay. Con '0.#' invariante (double lo capturado, decimal lo guardado: el punto medio hacia arriba).");
    }

    // ================================================================== contracts/golden/consultation-account.json

    private static JsonObject Svc(int id, string code, string name, string category, string price, bool active = true, int minutes = 20, string? syncId = null)
    {
        var o = new JsonObject { ["id"] = id, ["code"] = code, ["name"] = name, ["category"] = category, ["price"] = price, ["minutes"] = minutes };
        if (!active) o["active"] = false;
        if (syncId != null) o["syncId"] = syncId;
        return o;
    }

    private static ClinicalService ToService(JsonNode n) => new()
    {
        Id = Int(n, "id"), SyncId = S(n, "syncId") is { } sync ? Guid.Parse(sync) : Guid.Empty, Code = Str(n, "code"), Name = Str(n, "name"),
        Category = Str(n, "category"), Price = Dec(n, "price"), IsActive = Bool(n, "active", true), EstimatedDurationMinutes = Int(n, "minutes", 20)
    };

    [TestMethod]
    public void ConsultationAccount_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, services, overrideId) in new (string, JsonObject[], int?)[]
                 {
                     ("el código SERV-001 gana a la categoría", new[] { Svc(2, "SERV-010", "Consulta de especialidad", "Consulta", "500.00"), Svc(1, "serv-001", "Consulta general", "Consultas", "350.00") }, null),
                     ("el que usaba el equipo que envió la consulta", new[] { Svc(1, "SERV-001", "Consulta general", "Consulta", "350.00"), Svc(7, "SERV-007", "Consulta nocturna", "Consulta", "600.00") }, 7),
                     ("el que envió ya no está activo: SERV-001", new[] { Svc(1, "SERV-001", "Consulta general", "Consulta", "350.00"), Svc(7, "SERV-007", "Consulta nocturna", "Consulta", "600.00", active: false) }, 7),
                     ("la categoría Consulta sin distinguir mayúsculas", new[] { Svc(3, "SERV-003", "Sutura", "Procedimientos", "450.00"), Svc(4, "C-1", "Valoración", "CONSULTA", "280.00") }, null),
                     ("el nombre que contiene Consulta", new[] { Svc(3, "SERV-003", "Sutura", "Procedimientos", "450.00"), Svc(5, "X-5", "Primera consulta", "Otros", "320.50") }, null),
                     ("SERV-001 inactivo no cuenta", new[] { Svc(1, "SERV-001", "Consulta general", "Consulta", "350.00", active: false), Svc(5, "X-5", "Primera consulta", "Otros", "320.50") }, null),
                     ("sin servicio de consulta: 300 y su nombre", new[] { Svc(3, "SERV-003", "Sutura", "Procedimientos", "450.00") }, null),
                     ("sin catálogo", Array.Empty<JsonObject>(), null),
                 })
        {
            cases.Add(new RuleCase($"honorario · {name}", new JsonObject
            {
                ["check"] = "fee", ["services"] = new JsonArray(services.Select(s => (JsonNode)s).ToArray()), ["feeOverrideId"] = overrideId
            }));
        }

        static JsonObject AddOp(int lineId, string name, string price, int quantity, string code = "X") =>
            new() { ["op"] = "add", ["lineId"] = lineId, ["code"] = code, ["name"] = name, ["unitPrice"] = price, ["quantity"] = quantity };
        static JsonObject RemoveOp(int lineId, int quantity) => new() { ["op"] = "remove", ["lineId"] = lineId, ["quantity"] = quantity };
        foreach (var (name, fee, ops) in new (string, string, JsonObject[])[]
                 {
                     ("el mismo renglón suma piezas y conserva su nombre y precio", "350.00", new[] { AddOp(10, "Gasa", "18.00", 2), AddOp(-3, "Sutura", "450.00", 1), AddOp(10, "Gasa (otro nombre)", "20.00", 3) }),
                     ("cantidad 0 o negativa no agrega", "300", new[] { AddOp(10, "Gasa", "18.00", 0), AddOp(11, "Venda", "25.00", -1) }),
                     ("quitar menos de lo que hay reduce; igual o más lo quita", "300", new[] { AddOp(10, "Gasa", "18.50", 5), AddOp(11, "Venda", "25.00", 1), RemoveOp(10, 2), RemoveOp(11, 3) }),
                     ("quitar lo que no está o 0 no hace nada", "300", new[] { AddOp(10, "Gasa", "18.00", 1), RemoveOp(99, 1), RemoveOp(10, 0) }),
                     ("centavos", "0.01", new[] { AddOp(10, "A", "0.10", 3), AddOp(11, "B", "33.33", 3) }),
                 })
        {
            cases.Add(new RuleCase($"cuenta · {name}", new JsonObject { ["check"] = "account", ["feeAmount"] = fee, ["ops"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()) }));
        }

        foreach (var (name, recommended, stock, hasProduct) in new (string, int, int, bool)[]
                 {
                     ("alcanzan las existencias", 2, 5, true),
                     ("existencias justas", 2, 2, true),
                     ("no alcanzan: se receta sin cobrar", 3, 2, true),
                     ("cantidad 0 cuenta como 1", 0, 1, true),
                     ("sin producto", 1, 0, false),
                 })
        {
            cases.Add(new RuleCase($"se surte aquí · {name}", new JsonObject
            {
                ["check"] = "prescription-quantity", ["recommendedQuantity"] = recommended, ["product"] = hasProduct ? Prod(1, "MED-001", "Paracetamol 500 mg", stock: stock) : null
            }));
        }

        foreach (var (name, code, category, minutes) in new (string, string?, string, int)[] { ("servicio", "SERV-002", "Curaciones", 20), ("cargo libre sin código", "  ", "Otros", 0) })
        {
            cases.Add(new RuleCase($"textos del renglón · {name}", new JsonObject { ["check"] = "line-texts", ["code"] = code, ["category"] = category, ["minutes"] = minutes }));
        }

        CheckRuleVectors("consultation-account.json", "consultation-account", cases, input =>
            {
                switch (S(input, "check"))
                {
                    case "fee":
                    {
                        var fee = ConsultationAccountRules.FeeService(Items(input, "services").Select(ToService).ToList(), IntOrNull(input, "feeOverrideId"));
                        return new JsonObject
                        {
                            ["serviceId"] = fee?.Id, ["name"] = ConsultationAccountRules.FeeName(fee), ["amount"] = Money(ConsultationAccountRules.FeeAmount(fee))
                        };
                    }
                    case "account":
                    {
                        var items = new List<ConsultationProductItem>();
                        var results = new JsonArray();
                        foreach (var op in Items(input, "ops"))
                        {
                            results.Add(S(op, "op") == "add"
                                ? ConsultationAccountRules.AddLine(items, Int(op, "lineId"), Str(op, "code"), Str(op, "name"), null, null, "Piezas", Dec(op, "unitPrice"), Int(op, "quantity"), new DateTime(2026, 10, 15))
                                : ConsultationAccountRules.RemoveLine(items, Int(op, "lineId"), Int(op, "quantity")));
                        }
                        return new JsonObject
                        {
                            ["results"] = results,
                            ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
                            {
                                ["lineId"] = i.ProductId, ["name"] = i.Name, ["unitPrice"] = Money(i.UnitPrice), ["quantity"] = i.Quantity, ["subtotal"] = Money(i.Subtotal)
                            }).ToArray()),
                            ["total"] = Money(ConsultationAccountRules.Total(Dec(input, "feeAmount"), items))
                        };
                    }
                    case "prescription-quantity":
                        return ConsultationAccountRules.PrescriptionChargeQuantity(new PatientPrescriptionItem { RecommendedQuantity = Int(input, "recommendedQuantity") },
                            input["product"] is { } p ? ToProduct(p) : null);
                    case "line-texts":
                        return new JsonObject
                        {
                            ["code"] = ConsultationAccountRules.FreeChargeCode(S(input, "code")),
                            ["generic"] = ConsultationAccountRules.ServiceLineGeneric(S(input, "category")),
                            ["presentation"] = ConsultationAccountRules.ServiceLinePresentation(Int(input, "minutes"))
                        };
                    default:
                        throw new ArgumentException(S(input, "check"));
                }
            },
            "La cuenta de la consulta (Services/Rules/ConsultationAccountRules.cs, antes en ConsultationAccount y ConsultationService): el honorario, el total y cómo se suman y se quitan renglones. Importes como texto decimal invariante. Lo que cada hoja carga está en sheet-charges.json.",
            "fee: entre los servicios activos, el de Id feeOverrideId (la consulta recibida de otro equipo trae el honorario que usaba aquel, por su SyncId: handoff-payload.json), si no el de código SERV-001, si no el primero de categoría 'Consulta', si no el primero cuyo nombre contiene 'Consulta' (las tres sin distinguir mayúsculas; los acentos cuentan); name y amount son los suyos o 'Consulta Médica General' y 300.00. En la lista del catálogo, en su orden.",
            "account: add suma 'quantity' piezas al renglón con el mismo lineId (solo cambia su cantidad) o agrega uno al final; con cantidad 0 o negativa no hace nada (false). remove quita piezas del renglón con ese lineId: si tiene más se reduce, si no sale; false sin renglón o con cantidad 0 o negativa. lineId es el Id local del renglón (el producto, -Id del servicio o el de un cargo libre); en Kotlin cada renglón se reconoce con la regla de synced-json-columns.json. total = honorario + Σ cantidad × precio.",
            "prescription-quantity («Se surte aquí»): las piezas que carga un medicamento recetado, su cantidad recomendada (1 como mínimo), o null si no hay producto o sus existencias son menores (se receta igual, sin cobrar).",
            "line-texts: el código de un cargo libre (el suyo o 'CARGO' si está vacío), el genérico del renglón de un servicio 'Servicio Clínico • {categoría}' y su presentación 'Duración: N min'.");
    }

    // ================================================================== contracts/golden/sheet-charges.json

    /// <summary>Lo que la cuenta necesita de la pantalla de Pacientes: no hace nada (las tareas terminan en el momento).</summary>
    public class NullPort : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod?.ReturnType;
            if (type == null || type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type == typeof(Task<bool>)) return Task.FromResult(true);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class AccountHarness
    {
        public readonly ConsultationService Consultations = new(seedDemoPatients: false);
        public readonly CatalogStore Catalog;
        public readonly ConsultationAccount Account;

        public AccountHarness(IEnumerable<Product> products, IEnumerable<ClinicalService> services)
        {
            var hubs = DispatchProxy.Create<IPatientsHubs, NullPort>();
            var ui = new InlineUiThread();
            Catalog = new CatalogStore(null, hubs)
            {
                AvailableProducts = new(products),
                Services = new(services.Where(s => s.IsActive))
            };
            Account = new ConsultationAccount(Consultations, new ActiveConsultationSession(Consultations, ui, hubs), Catalog, new InfoDialogState(ui),
                DispatchProxy.Create<IConsultationGate, NullPort>(), DispatchProxy.Create<IPatientsWorkflow, NullPort>());
            Consultations.StartConsultation(new PatientRecord { Id = 1, FullName = "Paciente de prueba" });
        }
    }

    private static JsonObject AccountNode(AccountHarness h, IReadOnlyList<Product> products, IReadOnlyList<ClinicalService> services)
    {
        JsonObject Line(ConsultationProductItem i)
        {
            var product = i.ProductId > 0 ? products.FirstOrDefault(p => p.Id == i.ProductId) : null;
            var service = i.ProductId < 0 ? services.FirstOrDefault(s => -s.Id == i.ProductId) : null;
            return new JsonObject
            {
                ["kind"] = product != null ? "producto" : service != null ? "servicio" : "libre",
                ["syncId"] = product != null ? SyncIds.Format(product.SyncId) : service != null ? SyncIds.Format(service.SyncId) : null,
                ["code"] = i.Code, ["name"] = i.Name, ["genericName"] = i.GenericName, ["presentation"] = i.Presentation, ["unit"] = i.Unit,
                ["unitPrice"] = Money(i.UnitPrice), ["quantity"] = i.Quantity
            };
        }
        var items = h.Consultations.CurrentConsultation!.Items;
        return new JsonObject
        {
            ["items"] = new JsonArray(items.Select(i => (JsonNode)Line(i)).ToArray()),
            ["total"] = Money(h.Account.ConsultationAccountTotal)
        };
    }

    private static JsonObject SupplyListNode(IEnumerable<PatientServiceSupply>? supplies) => new()
    {
        ["supplies"] = supplies == null ? null : new JsonArray(supplies.Select(s => (JsonNode)new JsonObject
        {
            ["name"] = s.SupplyName, ["quantity"] = s.Quantity, ["unitPrice"] = Money(s.UnitPrice), ["kind"] = s.ChargedLineKind,
            ["syncId"] = s.ChargedLineSyncId is { } g ? SyncIds.Format(g) : null
        }).ToArray()),
        ["summary"] = ServiceRowSupplies.Summary(supplies)
    };

    private static JsonArray SheetProductsFixture() => new(
        WithSync(Prod(11, "CUR-001", "Gasa Estéril 10x10 cm", "Gasa de algodón", "Sobre con 5 piezas", 10, "Material de curación", price: "18.00"), "a1b2c3d4-0000-4000-8000-000000000001"),
        WithSync(Prod(12, "ANT-001", "Alcohol Etílico 70%", "Etanol", "Botella de 1000 ml", 3, "Antisépticos", price: "65.50"), "a1b2c3d4-0000-4000-8000-000000000002"),
        WithSync(Prod(14, "MED-001", "Paracetamol 500 mg", "Paracetamol", "Caja con 20 tabletas", 2, "Medicamentos", price: "45.00"), "a1b2c3d4-0000-4000-8000-000000000004"),
        WithSync(Prod(15, "MED-002", "Tempra", "Paracetamol", "Caja con 20 tabletas 500 mg", 9, "Medicamentos", price: "89.90"), "a1b2c3d4-0000-4000-8000-000000000005"));

    private static JsonArray SheetServicesFixture() => new(
        WithSync(Svc(21, "SERV-002", "Curación Menor y Asepsia", "Curaciones", "250.00", minutes: 20), "b1b2c3d4-0000-4000-8000-000000000001", ("11", 2), ("12", 1)),
        WithSync(Svc(22, "SERV-004", "Glucemia capilar", "Diagnóstico", "80.00", minutes: 10), "b1b2c3d4-0000-4000-8000-000000000002"),
        WithSync(Svc(1, "SERV-001", "Consulta Médica General", "Consulta", "350.00", minutes: 25), "b1b2c3d4-0000-4000-8000-000000000003"));

    private static JsonObject WithSync(JsonObject o, string syncId, params (string ProductId, int Quantity)[] defaults)
    {
        o["syncId"] = syncId;
        if (defaults.Length > 0) o["defaultProducts"] = new JsonArray(defaults.Select(d => (JsonNode)new JsonObject { ["productId"] = int.Parse(d.ProductId, CultureInfo.InvariantCulture), ["quantity"] = d.Quantity }).ToArray());
        return o;
    }

    [TestMethod]
    public void SheetCharges_MatchesTheVectors()
    {
        static JsonObject Prescribe(string name, int quantity, int? knownProductId = null) =>
            new() { ["op"] = "prescribe", ["medicineName"] = name, ["recommendedQuantity"] = quantity, ["knownProductId"] = knownProductId };
        static JsonObject Supply(string name, int quantity, string unitPrice = "0", int? productId = null) =>
            new() { ["op"] = "add-supply", ["supplyName"] = name, ["quantity"] = quantity, ["unitPrice"] = unitPrice, ["productId"] = productId };
        static JsonObject Service(string name, int quantity, string? syncId = null, string code = "", string category = "", string price = "0", int serviceId = 0) =>
            new()
            {
                ["op"] = "add-service", ["serviceName"] = name, ["serviceSyncId"] = syncId, ["serviceId"] = serviceId, ["serviceCode"] = code, ["category"] = category,
                ["price"] = price, ["quantity"] = quantity
            };
        static JsonObject Row(string op, int row, int value = 0) => new() { ["op"] = op, ["row"] = row, ["value"] = value };

        var curacion = "b1b2c3d4-0000-4000-8000-000000000001";
        var cases = new List<RuleCase>();
        foreach (var (name, ops) in new (string, JsonObject[])[]
                 {
                     ("receta que se surte aquí: el producto elegido, por nombre o por el renglón normado; sin existencias suficientes no se carga (cada renglón se compara solo con las existencias, no con lo ya cargado)", new[]
                     {
                         Prescribe("Paracetamol 500 mg", 2, 14), Prescribe("Paracetamol 500 mg tabletas (Tempra)", 1), Prescribe("Paracetamol 500 mg", 1),
                         Prescribe("Ibuprofeno 400 mg", 1), Prescribe("Tempra", 10, 15)
                     }),
                     ("quitar un medicamento de la receta descuenta sus piezas", new[] { Prescribe("Tempra", 3, 15), Row("unprescribe", 0) }),
                     ("insumos: del inventario con su nombre, por nombre, cargo libre con precio y sin precio (se anota pero no se cobra)", new[]
                     {
                         Supply("Gasa Estéril 10x10 cm", 2, productId: 11), Supply("alcohol etílico 70%", 1), Supply("Guantes de nitrilo", 4, "3.50"), Supply("Cubrebocas", 2),
                         Supply("Guantes de nitrilo", 1, "3.50")
                     }),
                     ("quitar un insumo descuenta lo que cargó", new[] { Supply("Gasa Estéril 10x10 cm", 3, productId: 11), Supply("Guantes de nitrilo", 2, "3.50"), Row("remove-supply", 0), Row("remove-supply", 1) }),
                     ("servicio del catálogo: el servicio por sus unidades y sus insumos por defecto, que forman su lista", new[] { Service("Curación Menor y Asepsia", 2, curacion) }),
                     ("servicio con un SyncId que aquí no existe: por su nombre normalizado", new[] { Service("curación menor y asepsia", 1, "b1b2c3d4-0000-4000-8000-0000000000ff") }),
                     ("renglón viejo con el Id local y el mismo nombre", new[] { Service("Curación Menor y Asepsia", 1, serviceId: 21) }),
                     ("sin SyncId ni Id es un cargo libre aunque el nombre exista: sin precio no se cobra", new[] { Service("Curación Menor y Asepsia", 1, price: "0") }),
                     ("servicio fuera del catálogo: cargo libre con precio, o nada sin precio", new[]
                     {
                         Service("Lavado ótico", 1, code: "", category: "Procedimientos", price: "200.00"), Service("Retiro de puntos", 1, category: "Procedimientos")
                     }),
                     ("una unidad más y una menos ajustan el servicio y su lista; lo ajustado a mano se queda", new[]
                     {
                         Service("Curación Menor y Asepsia", 1, curacion), Row("service-units", 0, 1), Row("service-supply-add", 0, 12), Row("service-units", 0, -1)
                     }),
                     ("quitar el renglón descuenta el servicio y su lista como quedó", new[]
                     {
                         Service("Curación Menor y Asepsia", 1, curacion), Row("service-supply-remove", 0, 11), Prescribe("Tempra", 1, 15), Row("remove-service", 0)
                     }),
                     ("las existencias no frenan los insumos por defecto de un servicio (el alcohol tiene 3)", new[] { Service("Curación Menor y Asepsia", 4, curacion) }),
                 })
        {
            cases.Add(new RuleCase(name, new JsonObject { ["products"] = SheetProductsFixture(), ["services"] = SheetServicesFixture(), ["feeOverrideId"] = null, ["ops"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()) }));
        }

        CheckRuleVectors("sheet-charges.json", "sheet-charges", cases, input =>
            {
                var products = ProductsIn(input);
                var services = Items(input, "services").Select(n =>
                {
                    var s = ToService(n);
                    s.DefaultProducts = Items(n, "defaultProducts").Select(d => new ServiceDefaultProduct { Product = products.First(p => p.Id == Int(d, "productId")), Quantity = Int(d, "quantity") }).ToList();
                    return s;
                }).ToList();
                var h = new AccountHarness(products, services);
                var rows = new List<object>();
                var steps = new JsonArray();
                foreach (var op in Items(input, "ops"))
                {
                    JsonObject step = new() { ["op"] = S(op, "op") };
                    switch (S(op, "op"))
                    {
                        case "prescribe":
                        {
                            var item = new PatientPrescriptionItem { MedicineName = Str(op, "medicineName"), RecommendedQuantity = Int(op, "recommendedQuantity") };
                            var known = IntOrNull(op, "knownProductId") is int id ? products.First(p => p.Id == id) : null;
                            item.DispenseInClinic = h.Account.ChargePrescription(item, known);
                            rows.Add(item);
                            step["charged"] = item.DispenseInClinic;
                            break;
                        }
                        case "unprescribe":
                            h.Account.UnchargePrescription((PatientPrescriptionItem)rows[Int(op, "row")]);
                            break;
                        case "add-supply":
                        {
                            var item = new PatientSupplyItem { SupplyName = Str(op, "supplyName"), Quantity = Int(op, "quantity"), UnitPrice = Dec(op, "unitPrice") };
                            var product = IntOrNull(op, "productId") is int id ? products.First(p => p.Id == id) : null;
                            var charged = h.Account.ChargeSupply(item, product ?? h.Catalog.FindCatalogProduct(item.SupplyName));
                            if (charged) item.ChargedUnits = item.Quantity;
                            rows.Add(item);
                            step["charged"] = charged;
                            step["line"] = new JsonObject
                            {
                                ["kind"] = item.ChargedLineKind, ["syncId"] = item.ChargedLineSyncId is { } g ? SyncIds.Format(g) : null
                            };
                            break;
                        }
                        case "remove-supply":
                        {
                            var item = (PatientSupplyItem)rows[Int(op, "row")];
                            if (item.ChargedUnits > 0) h.Account.UnchargeSupplyUnits(item, item.ChargedUnits);
                            break;
                        }
                        case "add-service":
                        {
                            var item = new PatientServiceItem
                            {
                                ServiceName = Str(op, "serviceName"), ServiceSyncId = S(op, "serviceSyncId") is { } sync ? Guid.Parse(sync) : null, ServiceId = Int(op, "serviceId"),
                                ServiceCode = Str(op, "serviceCode"), Category = Str(op, "category"), Price = Dec(op, "price"), Quantity = Int(op, "quantity")
                            };
                            var charged = h.Account.ChargeService(item);
                            if (charged) item.ChargedUnits = item.Quantity;
                            rows.Add(item);
                            step["charged"] = charged;
                            step.Add("row", SupplyListNode(item.Supplies));
                            break;
                        }
                        case "service-units":
                        {
                            var item = (PatientServiceItem)rows[Int(op, "row")];
                            var delta = Int(op, "value");
                            var ok = h.Account.ChangeServiceUnits(item, delta);
                            if (ok)
                            {
                                item.Quantity += delta;
                                item.ChargedUnits = Math.Max(0, item.ChargedUnits + delta);
                            }
                            step["charged"] = ok;
                            step.Add("row", SupplyListNode(item.Supplies));
                            break;
                        }
                        case "service-supply-add":
                        {
                            // Agregar del inventario una pieza de un insumo a la lista del renglón (como la búsqueda de insumos del servicio)
                            var item = (PatientServiceItem)rows[Int(op, "row")];
                            var product = products.First(p => p.Id == Int(op, "value"));
                            var list = item.Supplies ?? new List<PatientServiceSupply>();
                            var existing = ServiceRowSupplies.Find(list, product);
                            bool ok;
                            if (existing != null)
                            {
                                ok = h.Account.ChargeServiceSupply(existing, product, h.Account.ServiceSupplyLine(existing), 1);
                            }
                            else
                            {
                                var entry = ServiceRowSupplies.NewEntry(product, 0);
                                ok = h.Account.ChargeServiceSupply(entry, product, PatientSheetRows.SupplyLine.ForProduct(product), 1);
                                if (ok) list = list.Append(entry).ToList();
                            }
                            item.Supplies = list.Where(e => e.Quantity > 0).ToList();
                            step["charged"] = ok;
                            step.Add("row", SupplyListNode(item.Supplies));
                            break;
                        }
                        case "service-supply-remove":
                        {
                            // La X de un insumo de la lista (a 0 piezas)
                            var item = (PatientServiceItem)rows[Int(op, "row")];
                            var entry = item.Supplies!.First(e => e.ChargedLineSyncId == products.First(p => p.Id == Int(op, "value")).SyncId);
                            h.Account.UnchargeServiceSupply(entry, entry.Quantity);
                            item.Supplies = item.Supplies!.Where(e => e.Quantity > 0).ToList();
                            step.Add("row", SupplyListNode(item.Supplies));
                            break;
                        }
                        case "remove-service":
                        {
                            var item = (PatientServiceItem)rows[Int(op, "row")];
                            h.Account.UnchargeServiceRow(item, Math.Max(0, item.ChargedUnits));
                            break;
                        }
                        default:
                            throw new ArgumentException(S(op, "op"));
                    }
                    step.Add("account", AccountNode(h, products, services));
                    steps.Add(step);
                }
                return steps;
            },
            "Lo que las hojas de la consulta cargan a la cuenta (ViewModels/Patients/State/ConsultationAccount.cs, corrido tal cual con una consulta abierta y sin base; las reglas puras están en ConsultationAccountRules, PatientSheetRows y ServiceRowSupplies). Los productos y servicios son el catálogo de este equipo (defaultProducts: los insumos por defecto de un servicio, por Id del producto y piezas por unidad). Cada paso lleva la cuenta después de él: cada renglón como 'producto' o 'servicio' (por su SyncId) o 'libre' (por su nombre), con el código, nombre, genérico, presentación, unidad, precio y piezas que escribe la PC, y el total con el honorario (consultation-account.json).",
            "prescribe («Se surte aquí»): el producto elegido si el renglón sigue siendo ese producto (catalog-matching.json, product-matches-name), si no el del nombre (find-by-name); se carga con el nombre del renglón y sus piezas (consultation-account.json, prescription-quantity) solo si sus existencias alcanzan para ese renglón (no se resta lo que ya está en la cuenta: dos renglones pueden cargar más de lo que hay); 'charged' es lo que queda en DispenseInClinic. unprescribe descuenta las piezas del producto que encuentra por nombre.",
            "add-supply: el producto elegido o el del nombre; con producto se carga con el nombre del renglón; sin producto, un cargo libre ('INSUMO', 'Material clínico', 'Piezas', su precio) solo si el precio es mayor que 0 (si no, no se carga y la PC avisa). 'line' es lo que anota el renglón (ChargedLineKind y ChargedLineSyncId: synced-json-columns.json). remove-supply descuenta las piezas que cargó, del mismo renglón de la cuenta.",
            "add-service: el servicio del catálogo del renglón (PatientSheetRows.ResolveService: el activo con su SyncId; sin SyncId ni Id local, cargo libre; si no, el del Id viejo con el mismo nombre normalizado o el primero con ese nombre normalizado) se carga por sus unidades ('Servicio Clínico • categoría', 'Duración: N min', 'Servicio') y cada insumo por defecto por sus piezas × unidades, que forman la lista del renglón (revisión 14, ServiceRowSupplies.AddUnits: synced-json-columns.json); sin servicio, un cargo libre con su precio (si es mayor que 0) y la lista vacía. Las existencias no frenan los insumos por defecto (el tope solo aplica al agregar a mano). summary es el resumen de la lista que muestra la hoja.",
            "service-units (+ / −): con lista, una unidad más carga el servicio y los insumos por defecto de esa unidad (se suman a la lista); una menos descuenta el servicio y las piezas por defecto de esa unidad, cada insumo hasta lo que tenga la lista (lo agregado a mano se queda). service-supply-add: una pieza más de un producto en la lista (o uno nuevo). service-supply-remove: la X de un insumo de la lista. remove-service (la X del renglón): descuenta el servicio por sus unidades cargadas y cada insumo de la lista como quedó.");
    }

    // ================================================================== contracts/golden/checkout.json

    [TestMethod]
    public void Checkout_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var text in new[]
                 {
                     "500", "$1,000", "250.5", "250.", "99.999", "1,5", "abc", "", "0", "1.2.3",
                     // Revisión 20: la coma decimal
                     "150,50", "1,50", "$ 1,5", "1,500", "1,500.50", "1,000,000", "1,5,0", "1,", ",5", "1.500,50", "١٥٠"
                 })
        {
            cases.Add(new RuleCase($"recibido escrito · «{text}»", new JsonObject { ["check"] = "parse-amount", ["text"] = text }));
        }
        foreach (var total in new[] { "0", "35.50", "50", "180", "260", "999.99", "1200" })
        {
            cases.Add(new RuleCase($"billetes sugeridos · {total}", new JsonObject { ["check"] = "suggest-cash", ["total"] = total }));
        }

        static JsonObject AddProduct(int id, string name, string price, int stock, int quantity) =>
            new() { ["op"] = "add-product", ["product"] = Prod(id, $"P-{id}", name, stock: stock, price: price), ["quantity"] = quantity };
        static JsonObject AddLine(int id, string code, string name, string price, int quantity, bool service) =>
            new() { ["op"] = "add-line", ["productId"] = id, ["code"] = code, ["name"] = name, ["unitPrice"] = price, ["quantity"] = quantity, ["isService"] = service };
        static JsonObject Op(string op, JsonNode? value = null) => new() { ["op"] = op, ["value"] = value };

        foreach (var (name, emptyText, suffix, ops) in new (string, string?, string, JsonObject[])[]
                 {
                     ("venta en efectivo con cambio", null, "", new[] { AddProduct(1, "Paracetamol 500 mg", "45.00", 10, 2), AddProduct(2, "Gasa", "18.50", 50, 1), Op("cash-text", "$200") }),
                     ("efectivo de menos: falta y no se puede cobrar", null, "", new[] { AddProduct(1, "Paracetamol 500 mg", "45.00", 10, 3), Op("cash-text", "100") }),
                     ("sin recibido escrito paga exacto", null, "", new[] { AddProduct(1, "Paracetamol 500 mg", "45.00", 10, 1) }),
                     ("con tarjeta no hay cambio aunque se escriba", null, "", new[] { AddProduct(1, "Paracetamol 500 mg", "45.00", 10, 1), Op("cash-text", "500"), Op("payment", "Tarjeta Débito/Crédito") }),
                     ("las existencias topan lo que se agrega", null, "", new[] { AddProduct(1, "Tempra", "89.90", 3, 5), AddProduct(1, "Tempra", "89.90", 3, 1) }),
                     ("sin existencias no se agrega", null, "", new[] { AddProduct(1, "Tempra", "89.90", 0, 1) }),
                     ("+ y − con el tope; − en 1 quita el renglón", null, "", new[] { AddProduct(1, "Tempra", "89.90", 2, 1), Op("increase", 0), Op("increase", 0), AddProduct(2, "Gasa", "18.00", 5, 1), Op("decrease", 1) }),
                     ("la cantidad escrita: mínimo 1 y no más de lo que hay; un servicio no tiene tope", null, "", new[]
                     {
                         AddProduct(1, "Tempra", "89.90", 4, 1), Op("set-quantity-0", 9), AddLine(-21, "SERV-002", "Curación", "250.00", 1, true), Op("set-quantity-1", 0), Op("set-quantity-1", 7)
                     }),
                     ("terminar la consulta: el honorario y los cargos como renglones; el mismo renglón suma", null, " y terminar", new[]
                     {
                         AddLine(-1, "SERV-001", "Consulta Médica General", "350.00", 1, true), AddLine(11, "CUR-001", "Gasa", "18.00", 2, false), AddLine(11, "CUR-001", "Gasa", "18.00", 1, false)
                     }),
                     ("cuenta vacía al terminar la consulta: se permite con su texto", "Terminar sin cargos", " y terminar", Array.Empty<JsonObject>()),
                     ("cuenta vacía en el mostrador: no se cobra", null, "", Array.Empty<JsonObject>()),
                     ("el botón de un billete sugerido y el texto del recibido", null, "", new[] { AddProduct(1, "Tempra", "89.90", 5, 2), Op("set-cash", "200.5") }),
                 })
        {
            cases.Add(new RuleCase($"cobro · {name}", new JsonObject
            {
                ["check"] = "checkout", ["emptyChargeText"] = emptyText, ["chargeSuffix"] = suffix, ["ops"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray())
            }));
        }

        CheckRuleVectors("checkout.json", "checkout", cases, input =>
            {
                switch (S(input, "check"))
                {
                    case "parse-amount":
                        return Money(CheckoutRules.ParseAmount(S(input, "text")));
                    case "suggest-cash":
                        return new JsonArray(CheckoutRules.SuggestCashAmounts(Dec(input, "total")).Select(a => (JsonNode)JsonValue.Create(Money(a))!).ToArray());
                    case "checkout":
                    {
                        var notices = new JsonArray();
                        var vm = new CheckoutViewModel(null) { EmptyChargeText = S(input, "emptyChargeText"), ChargeSuffix = Str(input, "chargeSuffix") };
                        vm.Notify = (text, ok) => notices.Add(new JsonObject { ["text"] = text, ["ok"] = ok });
                        foreach (var op in Items(input, "ops"))
                        {
                            var kind = S(op, "op")!;
                            if (kind == "add-product") vm.AddProduct(ToProduct(op["product"]!), Int(op, "quantity"));
                            else if (kind == "add-line") vm.AddLine(new CartItem { ProductId = Int(op, "productId"), Code = Str(op, "code"), Name = Str(op, "name"), UnitPrice = Dec(op, "unitPrice"), Quantity = Int(op, "quantity"), IsService = Bool(op, "isService") });
                            else if (kind == "increase") vm.IncreaseQuantity(vm.Cart[Int(op, "value")]);
                            else if (kind == "decrease") vm.DecreaseQuantity(vm.Cart[Int(op, "value")]);
                            else if (kind.StartsWith("set-quantity-", StringComparison.Ordinal)) vm.Cart[int.Parse(kind["set-quantity-".Length..], CultureInfo.InvariantCulture)].Quantity = Int(op, "value");
                            else if (kind == "cash-text") vm.CashReceivedText = Str(op, "value");
                            else if (kind == "set-cash") vm.SetCashReceived(Dec(op, "value"));
                            else if (kind == "payment") vm.SelectedPaymentMethod = Str(op, "value");
                            else throw new ArgumentException(kind);
                        }
                        var (received, change) = CheckoutRules.ReceiptCash(vm.IsCash, vm.CashReceived, vm.TotalAmount);
                        return new JsonObject
                        {
                            ["cart"] = new JsonArray(vm.Cart.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["quantity"] = c.Quantity, ["subtotal"] = Money(c.Subtotal), ["stockText"] = c.StockText }).ToArray()),
                            ["totalItems"] = vm.TotalItems,
                            ["total"] = Money(vm.TotalAmount),
                            ["isCash"] = vm.IsCash,
                            ["cashReceived"] = Money(vm.CashReceived),
                            ["cashReceivedText"] = vm.CashReceivedText,
                            ["changeDue"] = Money(vm.ChangeDue),
                            ["hasChange"] = vm.HasChange,
                            ["missingAmount"] = Money(vm.MissingAmount),
                            ["canCharge"] = vm.CanCharge,
                            ["chargeText"] = vm.ChargeText,
                            ["quickCash"] = new JsonArray(vm.QuickCashAmounts.Select(a => (JsonNode)JsonValue.Create(Money(a))!).ToArray()),
                            ["receipt"] = new JsonObject { ["cashReceived"] = received is { } r ? Money(r) : null, ["change"] = change is { } c ? Money(c) : null },
                            ["missingText"] = vm.MissingAmount > 0 ? CheckoutRules.MissingText(vm.MissingAmount) : null,
                            ["notices"] = notices
                        };
                    }
                    default:
                        throw new ArgumentException(S(input, "check"));
                }
            },
            "El cobro del Punto de venta y de «Terminar consulta» (Services/Rules/CheckoutRules.cs y ViewModels/CheckoutViewModel.cs corrido tal cual, sin base). Importes como texto decimal invariante.",
            "parse-amount (lo que se escribe en Recibido): solo cuentan los dígitos ASCII 0-9, el punto y la coma (lo demás se quita, también los dígitos de otros alfabetos). Revisión 20: si no hay punto, hay una sola coma y después de ella quedan 1 o 2 dígitos al final, la coma es el punto decimal ('1,5' = 1.50, '150,50' = 150.50); en cualquier otro caso las comas se quitan (separan miles: '1,500' = 1500, '1,500.50' = 1500.50, '1,000,000'). Antes la coma siempre se quitaba y '1,5' era 15. Luego decimal invariante (solo punto decimal, sin signo ni miles); Math.Round a 2 (al par); 0 si no se lee o no es mayor que 0 (vacío = paga exacto). suggest-cash: con total > 0, el total redondeado hacia arriba a múltiplos de 50, 100, 200, 500 y 1000, solo los mayores que el total, sin repetir, de menor a mayor, 3 como mucho.",
            "checkout: add-product agrega un producto con existencias (si ya está suma, sin pasar de sus existencias; los avisos son los de la PC); add-line agrega un renglón armado (servicio o cargo de la consulta; si ya hay uno con el mismo Id, código y si es servicio, suma sus piezas sin tope); increase (+) no pasa de las existencias de un producto; decrease (−) en 1 quita el renglón; set-quantity-N escribe la cantidad del renglón N (mínimo 1; un producto con existencias no pasa de ellas); cash-text es lo que se escribe en Recibido (parse-amount); set-cash elige un billete (el texto queda '0.##' invariante); payment cambia la forma de pago (efectivo es exactamente 'Efectivo').",
            "Cuentas: total = Σ cantidad × precio; changeDue = max(0, recibido − total) con cualquier forma de pago; hasChange = efectivo, recibido ≥ total y total > 0; missingAmount = total − recibido si es efectivo y 0 < recibido < total; canCharge = (hay renglones o se permite la cuenta vacía) y no falta efectivo; chargeText = 'Cobrar {total con moneda de es-MX, $1,234.50}{complemento}' o el texto de la cuenta vacía. El recibo en efectivo lleva lo recibido (el total si no se escribió) y el cambio; con otra forma, nada.");
    }

    // ================================================================== contracts/golden/inventory-value.json

    [TestMethod]
    public void InventoryValue_MatchesTheVectors()
    {
        static JsonObject Costed(int id, int stock, string cost, bool excludesVat, bool active = true)
        {
            var o = Prod(id, $"P-{id}", $"Producto {id}", stock: stock, active: active);
            o["unitCost"] = cost;
            o["costExcludesVat"] = excludesVat;
            return o;
        }

        var cases = new List<RuleCase>();
        foreach (var (name, product) in new (string, JsonObject)[]
                 {
                     ("costo con IVA incluido", Costed(1, 10, "12.50", false)),
                     ("costo sin IVA: se suma 16 % redondeado a centavos", Costed(2, 3, "100", true)),
                     ("el IVA con punto medio hacia arriba: 0.125 × 1.16 = 0.145 → 0.15", Costed(3, 7, "0.125", true)),
                     ("sin existencias no vale nada", Costed(4, 0, "50", false)),
                     ("existencias negativas tampoco", Costed(5, -2, "50", false)),
                     ("sin costo", Costed(6, 5, "0", true)),
                 })
        {
            cases.Add(new RuleCase($"producto · {name}", new JsonObject { ["check"] = "product", ["product"] = product }));
        }
        cases.Add(new RuleCase("panel · solo activos con existencias; los que no tienen costo se cuentan aparte", new JsonObject
        {
            ["check"] = "dashboard",
            ["products"] = new JsonArray(Costed(1, 10, "12.50", false), Costed(2, 3, "100", true), Costed(3, 7, "0.125", true), Costed(4, 0, "50", false),
                Costed(6, 5, "0", true), Costed(7, 4, "0", false), Costed(8, 9, "10", false, active: false))
        }));
        cases.Add(new RuleCase("panel · todo con costo", new JsonObject { ["check"] = "dashboard", ["products"] = new JsonArray(Costed(1, 1, "1", false)) }));

        CheckRuleVectors("inventory-value.json", "inventory-value", cases, input =>
            {
                if (S(input, "check") == "product")
                {
                    var p = ToProduct(input["product"]!);
                    return new JsonObject { ["effectiveUnitCost"] = Money(p.EffectiveUnitCost), ["inventoryValue"] = Money(p.InventoryValue), ["hasCost"] = p.HasCost };
                }
                var (value, withoutCost) = InventoryValueRules.Summarize(ProductsIn(input));
                return new JsonObject { ["inventoryValue"] = Money(value), ["stockedWithoutCost"] = withoutCost, ["detail"] = InventoryValueRules.Detail(withoutCost) };
            },
            "El valor del inventario (Models/Product.InventoryValue y Services/Rules/InventoryValueRules.cs, el panel y el celular C7). Los productos llevan además unitCost (decimal en texto) y costExcludesVat. Importes como texto decimal invariante con su escala.",
            "product: effectiveUnitCost = el costo capturado, o si no incluye IVA Math.Round(costo × 1.16, 2, AwayFromZero); inventoryValue = existencias × effectiveUnitCost si hay existencias (> 0), si no 0; hasCost = costo > 0.",
            "dashboard: de los productos activos con existencias, la suma de inventoryValue y cuántos no tienen costo; detail 'al costo' o 'N sin costo'.");
    }
}
