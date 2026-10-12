using System;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Services;
using TPVC.Services.Folios;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.16: contracts/golden/folios.json, cómo se arma cada folio (FolioFactory). Kotlin lo repite igual: la serie, el
/// número que cuenta, el folio con conexión, sin conexión (con la letra del equipo) y con la nube apagada, y CNS.
/// </summary>
public partial class GoldenVectorTests
{
    private static JsonArray Folios(params string[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    [TestMethod]
    public void Folios_MatchTheVectors()
    {
        var day = new DateTime(2026, 10, 9, 10, 30, 15);
        var cases = new JsonArray();

        foreach (var (kind, label) in new[] { (FolioKind.Patient, "expediente"), (FolioKind.Sale, "ticket") })
        {
            cases.Add(Case($"serie · {label}", new JsonObject { ["kind"] = kind.ToString(), ["date"] = "2026-10-09" }, FolioFactory.Series(kind, day)));
        }

        // Qué folios cuentan para el número de una serie
        foreach (var folio in new[] { "EXP-2026-007", "exp-2026-012", " EXP-2026-003 ", "EXP-2026-P1-004", "EXP-2026-005-R1", "EXP-2026-", "EXP-20261", "EXP-2025-099", "EXP-2026-1000" })
        {
            var n = FolioFactory.NumberIn("EXP-2026", folio);
            cases.Add(Case($"número en EXP-2026 · '{folio}'", new JsonObject { ["series"] = "EXP-2026", ["folio"] = folio }, n is int value ? JsonValue.Create(value) : null));
        }
        Assert.IsNull(FolioFactory.NumberIn("EXP-2026", "EXP-2026-P1-004"), "Un folio con letra no cuenta");
        Assert.AreEqual(4, FolioFactory.NumberIn("EXP-2026-P1", "EXP-2026-P1-004"), "Pero sí en su serie con letra");

        // Con la nube apagada o sin verificar: el mayor local + 1 (como en v1.19)
        var local = new[] { "EXP-2026-001", "EXP-2026-004", "EXP-2026-P1-009", "EXP-2026-002-R1" };
        cases.Add(Case("nube apagada · expediente", new JsonObject { ["kind"] = "Patient", ["date"] = "2026-10-09", ["existing"] = Folios(local) },
            FolioFactory.Local(FolioKind.Patient, day, local)));
        cases.Add(Case("nube apagada · primer ticket del día", new JsonObject { ["kind"] = "Sale", ["date"] = "2026-10-09", ["existing"] = Folios() },
            FolioFactory.Local(FolioKind.Sale, day, Array.Empty<string>())));

        // Con conexión: el número que regresó el contador
        cases.Add(Case("con conexión · expediente", new JsonObject { ["kind"] = "Patient", ["date"] = "2026-10-09", ["taken"] = 5 }, FolioFactory.Online(FolioKind.Patient, day, 5)));
        cases.Add(Case("con conexión · ticket", new JsonObject { ["kind"] = "Sale", ["date"] = "2026-10-09", ["taken"] = 12 }, FolioFactory.Online(FolioKind.Sale, day, 12)));
        cases.Add(Case("con conexión · más dígitos de los previstos", new JsonObject { ["kind"] = "Patient", ["date"] = "2026-10-09", ["taken"] = 1000 },
            FolioFactory.Online(FolioKind.Patient, day, 1000)));

        // Sin conexión: la letra del equipo; el número es el mayor entre el guardado fuera de la base y el local
        foreach (var (name, kind, code, existing, saved) in new[]
                 {
                     ("primero de la tablet", FolioKind.Patient, "T1", Array.Empty<string>(), 0),
                     ("el guardado fuera de la base manda (se restauró un respaldo)", FolioKind.Patient, "T1", new[] { "EXP-2026-T1-001" }, 3),
                     ("el local manda", FolioKind.Patient, "P1", new[] { "EXP-2026-P1-006", "EXP-2026-007" }, 2),
                     ("ticket del celular", FolioKind.Sale, "C2", new[] { "TKT-20261009-0040" }, 0)
                 })
        {
            var offline = FolioFactory.Offline(kind, day, code, existing, saved);
            cases.Add(Case($"sin conexión · {name}", new JsonObject
            {
                ["kind"] = kind.ToString(), ["date"] = "2026-10-09", ["deviceCode"] = code, ["existing"] = Folios(existing), ["savedLast"] = saved
            }, new JsonObject { ["folio"] = offline.Folio, ["letteredSeries"] = offline.LetteredSeries, ["number"] = offline.Number }));
        }

        foreach (var code in new[] { "P1", "T12", "C3", "X1", "p1", "T", "" })
        {
            cases.Add(Case($"código de equipo · '{code}'", new JsonObject { ["code"] = code }, FolioFactory.IsDeviceCode(code)));
        }

        cases.Add(Case("CNS · con conexión", new JsonObject { ["now"] = "2026-10-09 10:30:15", ["offlineDeviceCode"] = null }, FolioFactory.Consultation(day)));
        cases.Add(Case("CNS · sin conexión", new JsonObject { ["now"] = "2026-10-09 10:30:15", ["offlineDeviceCode"] = "T1" }, FolioFactory.Consultation(day, "T1")));

        CheckOrWrite("contracts/golden/folios.json", VectorFile("folios", cases,
            "Serie: EXP-yyyy (año del consultorio) con 3 dígitos; TKT-yyyyMMdd (día) con 4. Más dígitos si el número no cabe.",
            "Un folio cuenta para su serie solo si es la serie, un guion y dígitos (sin importar mayúsculas ni espacios en los extremos): uno con letra (EXP-2026-T1-004), con -R1 o con otro texto no cuenta.",
            "Con la nube activa y verificada (clinicId local), el número sale de FolioCounters con la instrucción 'folio.take' de hrana/statements.json, con 2 s de espera; el piso es el mayor número local de la serie, así nunca regresa uno que el equipo ya tiene.",
            "Sin conexión, sin respuesta en 2 s, con la cuota agotada o con el token rechazado: el folio lleva el código del equipo de DeviceRegistry (P, T o C y su número; X nunca numera) y el número siguiente de esa serie con letra, el mayor entre el último guardado fuera de la base ('savedLast', que no se borra al bajar todo otra vez) y el máximo local. Sin código, no se crea el folio.",
            "Con la nube apagada o sin verificar: el mayor número local de la serie + 1.",
            "CNS sale de la hora (yyyyMMdd-HHmmss); sin conexión lleva la letra (CNS-yyyyMMdd-T1-HHmmss) y nunca se regulariza.",
            "Un folio repetido (de antes del paso 8.16 o tecleado a mano) se conserva: ya no se renombra con -R1."));
    }
}
