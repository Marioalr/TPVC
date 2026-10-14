using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.22: los vectores de la combinación por campo en pacientes, que Kotlin repite igual: la forma canónica
/// (canonical-json.json), el sello de las marcas (field-stamps.json), la combinación (patient-merge.json), el resumen
/// derivado (patient-summary.json) y la revisión al aceptar una consulta (handoff-accept.json). Salen del mismo código que
/// usa la PC (Services/Rules).
/// </summary>
public partial class GoldenVectorTests
{
    private static readonly Guid MergePatient = new("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
    private static readonly Guid MergeRowA = new("a0000000-0000-4000-8000-000000000001");
    private static readonly Guid MergeRowB = new("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid MergeRowC = new("a0000000-0000-4000-8000-000000000003");

    private static string Stamp(int seconds) => PatientFieldStamps.Format(new DateTime(2026, 10, 14, 15, 0, 0).AddSeconds(seconds));

    private static DateTime At(int seconds) => new DateTime(2026, 10, 14, 15, 0, 0).AddSeconds(seconds);

    private static string Rk(Guid row) => PatientFieldStamps.RowKey(PatientSheetRows.SuppliesColumn, row);

    private static string Qk(Guid row) => PatientFieldStamps.RemovedKey(PatientSheetRows.SuppliesColumn, row);

    /// <summary>Un FieldStampsJson con la llave de la fila y estas marcas.</summary>
    private static string StampsJson(int row, params (string Key, int Seconds)[] entries)
    {
        var stamps = new PatientFieldStamps();
        foreach (var (key, seconds) in entries) stamps[key] = Stamp(seconds);
        stamps.RowStamp = At(row);
        return stamps.ToJson();
    }

    private static string SupplySheet(params (Guid Id, string Name, int Quantity)[] rows) =>
        JsonSerializer.Serialize(rows.Select(r => new { RowId = SyncIds.Format(r.Id), SupplyName = r.Name, Instructions = "", Quantity = r.Quantity, UnitPrice = 12.5m }),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // ================================================================== contracts/golden/canonical-json.json

    [TestMethod]
    public void CanonicalJson_MatchesTheVectors()
    {
        var cases = new JsonArray();
        foreach (var (name, json) in new[]
                 {
                     ("llaves ordenadas por unidades UTF-16 (mayúsculas antes que minúsculas; acentos al final)", """{"b":1,"a":2,"B":3,"á":4,"Z":5}"""),
                     ("sin espacios, también en objetos y arreglos anidados", """{ "x" : [ 1 , { "z" : true , "y" : null } ] }"""),
                     ("números: sin ceros de más, sin exponente, sin signo en el cero", """[35.0, 12.50, 1e3, -0, -0.0, 0.10, 100, 2.5E-2]"""),
                     ("texto: acentos, ñ y símbolos tal cual; solo se escapan comillas, diagonal invertida y control", """["Peña — 50 % ✓ <b> & / ü", "comilla \" y diagonal \\", "salto\nretorno\rtab\tcampana\u0007", "á escrita con escape"]"""),
                 })
        {
            cases.Add(Case($"json · {name}", new JsonObject { ["check"] = "json", ["json"] = json }, CanonicalJson.Normalize(json)));
        }

        foreach (var (name, clrType, value) in new (string, string, string?)[]
                 {
                     ("texto", "string", "García López, Ana"),
                     ("texto vacío", "string", ""),
                     ("nulo", "string?", null),
                     ("entero", "int", "42"),
                     ("booleano (en la nube viaja como 0/1)", "bool", "true"),
                     ("fecha: 27 caracteres", "DateTime", "1990-05-01 00:00:00.0000000"),
                     ("fecha opcional nula", "DateTime?", null),
                     ("GUID en mayúsculas sale en minúsculas", "Guid", "3F2504E0-4F89-11D3-9A0C-0305E82C3301"),
                     ("decimal con su escala: 12.50 vale lo mismo que 12.5", "decimal", "12.50"),
                 })
        {
            var canonical = CanonicalJson.Write(CanonicalJson.FromValue(ParseInput(clrType, value)));
            cases.Add(Case($"valor de columna · {name}", new JsonObject { ["check"] = "value", ["clrType"] = clrType, ["value"] = value }, canonical));
        }

        var patient = SyncIds.Format(MergePatient);
        foreach (var (name, column, json) in new[]
                 {
                     ("insumos: con todos sus campos conocidos (los omitidos con su valor por omisión), sin ChargedLineId",
                         PatientSheetRows.SuppliesColumn,
                         $$"""[{"RowId":"{{MergeRowA}}","SupplyName":"Gasa","Quantity":2,"UnitPrice":10.0,"ChargedConsultationFolio":"CNS-20261014-090000","ChargedUnits":2,"ChargedLineId":5,"ChargedLineKind":"producto","ChargedLineSyncId":"{{SyncIds.Format(GauzeSyncId).ToUpperInvariant()}}"}]"""),
                     ("insumos: nombres sin distinguir mayúsculas y lo que falta por omisión (Quantity 1)",
                         PatientSheetRows.SuppliesColumn, $$"""[{"rowid":"{{MergeRowA}}","supplyname":"Gasa"}]"""),
                     ("insumos: el Id local de otro equipo no cambia el significado",
                         PatientSheetRows.SuppliesColumn, $$"""[{"RowId":"{{MergeRowA}}","SupplyName":"Gasa","ChargedLineId":-1234567,"ChargedLineKind":"libre"}]"""),
                     ("receta: un renglón sin RowId recibe el determinista de su posición; los campos desconocidos van tal cual",
                         PatientSheetRows.PrescriptionColumn, """[{"MedicineName":"Paracetamol 500 mg","Dosage":"1 cada 8 horas","Plantilla":{"Id":"fiebre","V":2.0}}]"""),
                     ("servicios: sin ServiceId (el Id local), con ServiceSyncId",
                         PatientSheetRows.ServicesColumn, $$"""[{"RowId":"{{MergeRowB}}","ServiceId":21,"ServiceSyncId":"{{CuracionSyncId}}","ServiceCode":"SERV-002","ServiceName":"Curación Menor y Asepsia","Price":250,"Quantity":1}]"""),
                     ("hoja vacía o que no se puede leer: arreglo vacío", PatientSheetRows.SuppliesColumn, "no es JSON"),
                     ("texto vacío", PatientSheetRows.SuppliesColumn, ""),
                 })
        {
            cases.Add(Case($"hoja · {name}", new JsonObject { ["check"] = "sheet", ["column"] = column, ["patientSyncId"] = patient, ["json"] = json },
                PatientSheetCanonical.CanonicalText(column, json, MergePatient)));
        }

        CheckOrWrite("contracts/golden/canonical-json.json", VectorFile("canonical-json", cases,
            "Forma canónica (Services/Rules/CanonicalJson y PatientSheetCanonical, paso 8.22): para saber si dos versiones de un expediente dicen lo mismo sin depender de cómo las escribió cada equipo. 'expected' es el texto canónico.",
            "json: sin espacios; llaves de objeto ordenadas por unidades UTF-16 (String.compareTo de Kotlin); texto con solo \\\" \\\\ y los caracteres de control escapados (\\b \\f \\n \\r \\t; los demás \\u00xx en minúsculas), lo demás tal cual; números en decimal sin exponente, sin ceros de más en la fracción ni punto final, y 0 sin signo (BigDecimal.stripTrailingZeros().toPlainString(), con 0 para el cero); true, false y null tal cual.",
            "value: el valor de una columna sincronizada como valor canónico: texto como texto, entero y enum como número, booleano como true/false (en la nube es 0/1), fecha como texto de 27 caracteres, GUID en minúsculas, decimal como número canónico, nulo como null. Los nombres de 'clrType' son los de contracts/sync/schema.json.",
            "sheet: una hoja del paciente (Active*Json) como arreglo de renglones canónicos, en su orden. Se lee como la PC (nombres sin distinguir mayúsculas, lo que falta con su valor por omisión: Quantity y RecommendedQuantity 1, los demás números 0, textos '' salvo Presentation, ChargedConsultationFolio, ChargedLineKind y los SyncId, que son null; un texto que no se puede leer es una hoja vacía; RowId determinista de contracts/golden/synced-json-columns.json para un renglón sin él) y cada renglón lleva TODOS sus campos conocidos menos los Id locales (ChargedLineId en insumos, ServiceId en servicios), más los desconocidos tal cual. Campos: receta RowId, MedicineName, Dosage, RecommendedQuantity, Presentation, DispenseInClinic; insumos RowId, SupplyName, Instructions, Quantity, UnitPrice, ChargedConsultationFolio, ChargedUnits, ChargedLineKind, ChargedLineSyncId; servicios RowId, ServiceSyncId, ServiceCode, ServiceName, Category, Notes, Price, Quantity, ChargedConsultationFolio, ChargedUnits."));
    }

    // ================================================================== contracts/golden/field-stamps.json

    [TestMethod]
    public void FieldStamps_MatchTheVectors()
    {
        var cases = new JsonArray();
        var col = PatientSheetRows.SuppliesColumn;
        var patient = SyncIds.Format(MergePatient);

        foreach (var (name, before, fields, sheetBefore, sheetAfter) in new (string, string, string[], string?, string?)[]
                 {
                     ("un campo que cambió toma la marca nueva; la llave de la fila también", StampsJson(10, ("Allergies", 10)), new[] { "Phone" }, null, null),
                     ("alta: sin campos, solo la llave de la fila", "", Array.Empty<string>(), null, null),
                     ("hoja: renglón agregado, cambiado y quitado; cambió qué renglones hay, así que la hoja marca su orden",
                         StampsJson(10, (Rk(MergeRowA), 5), (Rk(MergeRowB), 5)), Array.Empty<string>(),
                         SupplySheet((MergeRowA, "Gasa", 1), (MergeRowB, "Venda", 1)), SupplySheet((MergeRowA, "Gasa", 2), (MergeRowC, "Alcohol", 1))),
                     ("hoja: solo cambia la cantidad de un renglón; el orden no se marca", StampsJson(10), Array.Empty<string>(),
                         SupplySheet((MergeRowA, "Gasa", 1)), SupplySheet((MergeRowA, "Gasa", 5))),
                     ("hoja: mismo significado con otro texto (sin RowId, otro orden de llaves): nada que marcar", StampsJson(10), Array.Empty<string>(),
                         """[{"SupplyName":"Gasa","Quantity":1}]""", $$"""[{"Quantity":1,"RowId":"{{PatientSheetRows.LegacyRowId(MergePatient, PatientSheetRows.SuppliesColumn, 0, "Gasa")}}","SupplyName":"Gasa"}]"""),
                     ("hoja: un renglón que se había quitado vuelve con el mismo RowId (descartar la consulta): su marca rebasa la de quitado",
                         StampsJson(10, (Qk(MergeRowB), 10)), Array.Empty<string>(), SupplySheet((MergeRowA, "Gasa", 1)), SupplySheet((MergeRowA, "Gasa", 1), (MergeRowB, "Venda", 1))),
                     ("llaves desconocidas: se conservan", """{"ColumnaNueva":"2026-01-01 00:00:00.0000000","UpdatedAtUtc":"2026-10-14 15:00:10.0000000"}""", new[] { "Phone" }, null, null),
                 })
        {
            var stamps = PatientFieldStamps.Parse(before);
            var sheets = sheetBefore == null ? Array.Empty<(string, string?, string?)>() : new[] { (col, (string?)sheetBefore, (string?)sheetAfter) };
            stamps.ApplyLocalWrite(At(20), fields, sheets, MergePatient);
            cases.Add(Case($"guardar · {name}", new JsonObject
            {
                ["check"] = "local-write",
                ["patientSyncId"] = patient,
                ["fieldStampsJson"] = before,
                ["stamp"] = Stamp(20),
                ["changedFields"] = new JsonArray(fields.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
                ["sheets"] = sheetBefore == null ? new JsonArray() : new JsonArray(new JsonObject { ["column"] = col, ["before"] = sheetBefore, ["after"] = sheetAfter })
            }, stamps.ToJson()));
        }

        // La purga: 90 días después de la marca más nueva
        var day90 = (int)TimeSpan.FromDays(90).TotalSeconds;
        foreach (var (name, json) in new[]
                 {
                     ("quitado hace más de 90 días de la marca más nueva: se purgan su llave y la de quitado", StampsJson(day90 + 10, (Rk(MergeRowB), 1), (Qk(MergeRowB), 2), ("Phone", day90 + 10))),
                     ("quitado justo 90 días antes: se queda", StampsJson(day90 + 10, (Qk(MergeRowB), 10), ("Phone", day90 + 10))),
                     ("una edición posterior a quitarlo: vive, no se purga", StampsJson(day90 + 10, (Rk(MergeRowB), 3), (Qk(MergeRowB), 2), ("Phone", day90 + 10))),
                     ("las de los campos nunca se purgan", StampsJson(day90 + 10, ("Allergies", 1), ("Phone", day90 + 10))),
                 })
        {
            var stamps = PatientFieldStamps.Parse(json);
            stamps.Purge(PatientSheetCanonical.Columns);
            cases.Add(Case($"purgar · {name}", new JsonObject { ["check"] = "purge", ["fieldStampsJson"] = json }, stamps.ToJson()));
        }

        foreach (var (name, json, row) in new[]
                 {
                     ("con la llave de la fila igual a UpdatedAtUtc: con marcas", StampsJson(10), 10),
                     ("UpdatedAtUtc mayor que la llave de la fila: escritura sin marcas", StampsJson(10), 11),
                     ("sin llave de la fila: sin marcas", """{"Phone":"2026-10-14 15:00:05.0000000"}""", 10),
                     ("vacío: sin marcas", "", 10),
                     ("dañado: sin marcas", "{no es json", 10),
                 })
        {
            cases.Add(Case($"sin marcas · {name}", new JsonObject { ["check"] = "stampless", ["fieldStampsJson"] = json, ["rowUpdatedAtUtc"] = Stamp(row) },
                new JsonObject { ["stampless"] = PatientFieldStamps.Parse(json).IsStampless(At(row)) }));
        }

        foreach (var (name, json, row, key) in new[]
                 {
                     ("una marca menor que la fila cuenta tal cual", StampsJson(10, ("Phone", 5)), 10, "Phone"),
                     ("una marca mayor que la fila (la fila se reparó por estar en el futuro) cuenta como la fila", StampsJson(3600, ("Phone", 3600)), 10, "Phone"),
                     ("sin marca: la mínima (null)", StampsJson(10), 10, "Phone"),
                 })
        {
            cases.Add(Case($"marca efectiva · {name}", new JsonObject { ["check"] = "effective", ["fieldStampsJson"] = json, ["rowUpdatedAtUtc"] = Stamp(row), ["key"] = key },
                PatientFieldStamps.Parse(json).Effective(key, At(row)) is { } value ? JsonValue.Create(value) : null));
        }

        CheckOrWrite("contracts/golden/field-stamps.json", VectorFile("field-stamps", cases,
            "Marcas por campo de Patients.FieldStampsJson (Services/Rules/PatientFieldStamps, paso 8.22): objeto JSON plano en forma canónica (contracts/golden/canonical-json.json), marcas de 27 caracteres en UTC. Llaves: '<columna>' (último cambio del campo; en una hoja, de su orden: qué renglones tiene y en qué orden), '<hoja>/<RowId>' (renglón agregado o cambiado), '<hoja>/<RowId>/quitado' (renglón quitado) y 'UpdatedAtUtc' (el UpdatedAtUtc de la fila con que se mantuvieron). Cualquier otra llave se conserva.",
            "local-write: lo que deja un guardado local con la marca 'stamp' (la misma que su UpdatedAtUtc): cada campo de 'changedFields' (los que cambiaron por su significado) toma la marca; en cada hoja de 'sheets' (texto de antes y de después) cada renglón agregado o cuya forma canónica cambió toma la marca en su llave, cada renglón que ya no está la toma en su llave de quitado, y si cambió la lista de RowId (qué renglones y en qué orden), la hoja la toma en su propia llave. Al final 'UpdatedAtUtc' = la marca y se purga. 'expected' es el texto que se guarda ('' sin marcas).",
            "purge: un renglón quitado (su marca de quitado es mayor o igual que la del renglón) cuya marca de quitado es anterior a (la marca más nueva de todas las llaves salvo 'UpdatedAtUtc') − 90 días pierde sus dos llaves. Depende solo de las marcas: dos equipos purgan lo mismo.",
            "stampless: una escritura sin marcas (una PC v1.19–v1.38 sube la fila sin tocar esta columna): el UpdatedAtUtc de la fila es mayor que la llave 'UpdatedAtUtc', o no la hay (vacío o dañado cuenta como sin marcas).",
            "effective: una marca nunca vale más que el UpdatedAtUtc de su fila; null si no hay."));
    }

    // ================================================================== contracts/golden/patient-merge.json

    private static readonly string[] MergeVectorColumns =
    {
        nameof(PatientRecord.ActivePrescriptionJson), nameof(PatientRecord.ActiveSuppliesJson), nameof(PatientRecord.Allergies),
        nameof(PatientRecord.FullName), nameof(PatientRecord.IsActive), nameof(PatientRecord.Phone)
    };

    private static PatientMerge.Version MergeVersion(int updated, string? stamps, params (string Column, object? Value)[] values)
    {
        var dict = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [nameof(PatientRecord.ActivePrescriptionJson)] = JsonValue.Create(string.Empty),
            [nameof(PatientRecord.ActiveSuppliesJson)] = JsonValue.Create(string.Empty),
            [nameof(PatientRecord.Allergies)] = JsonValue.Create("Negadas"),
            [nameof(PatientRecord.FullName)] = JsonValue.Create("Rodríguez Silva, María Elena"),
            [nameof(PatientRecord.IsActive)] = JsonValue.Create(true),
            [nameof(PatientRecord.Phone)] = JsonValue.Create("5598761234")
        };
        foreach (var (column, value) in values) dict[column] = CanonicalJson.FromValue(value);
        return new PatientMerge.Version { SyncId = MergePatient, UpdatedAtUtc = At(updated), FieldStampsJson = stamps, Values = dict };
    }

    private static JsonObject VersionNode(PatientMerge.Version v) => new()
    {
        ["updatedAtUtc"] = PatientFieldStamps.Format(v.UpdatedAtUtc),
        ["fieldStampsJson"] = v.FieldStampsJson,
        ["values"] = new JsonObject(v.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, CanonicalJson.Clone(kv.Value))))
    };

    [TestMethod]
    public void PatientMerge_MatchesTheVectors()
    {
        var cases = new JsonArray();
        var col = PatientSheetRows.SuppliesColumn;
        var day90 = (int)TimeSpan.FromDays(90).TotalSeconds;

        var scenarios = new (string Name, PatientMerge.Version Local, PatientMerge.Version Remote)[]
        {
            ("alergias en la tablet y teléfono en la PC: sobreviven los dos y el resultado sube",
                MergeVersion(20, StampsJson(20, ("Phone", 20)), ("Phone", "5511112222")),
                MergeVersion(10, StampsJson(10, ("Allergies", 10)), ("Allergies", "Penicilina"))),
            ("gana la marca del campo, no la de la fila",
                MergeVersion(30, StampsJson(30, ("Allergies", 5), ("Phone", 30)), ("Allergies", "Sulfas"), ("Phone", "5511112222")),
                MergeVersion(10, StampsJson(10, ("Allergies", 10)), ("Allergies", "Penicilina"))),
            ("una marca que falta cuenta como la mínima",
                MergeVersion(30, StampsJson(30, ("Phone", 30)), ("Allergies", "Sulfas"), ("Phone", "5511112222")),
                MergeVersion(10, StampsJson(10, ("Allergies", 10)), ("Allergies", "Penicilina"))),
            ("marcas iguales y valores distintos: gana el texto canónico mayor en orden ordinal ('Ácido' va después de 'Agua')",
                MergeVersion(10, StampsJson(10, ("Allergies", 10)), ("Allergies", "Agua")),
                MergeVersion(10, StampsJson(10, ("Allergies", 10)), ("Allergies", "Ácido"))),
            ("escritura sin marcas de una PC v1.38: lo que difiere cuenta con su UpdatedAtUtc (como el LWW por fila)",
                MergeVersion(40, StampsJson(40, ("Allergies", 10), ("Phone", 30), ("FullName", 45)), ("Allergies", "Penicilina"), ("Phone", "5511111111"), ("FullName", "Rodríguez Silva, María")),
                MergeVersion(50, StampsJson(10, ("Allergies", 10)), ("Allergies", "Ninguna"), ("Phone", "5500000000"))),
            ("una edición de aquí posterior a la escritura sin marcas se queda",
                MergeVersion(60, StampsJson(60, ("Phone", 60)), ("Allergies", "Penicilina"), ("Phone", "5511111111")),
                MergeVersion(50, StampsJson(10, ("Allergies", 10)), ("Allergies", "Ninguna"), ("Phone", "5500000000"))),
            ("dos filas sin marcas (antes de actualizar): el LWW por fila, y las marcas quedan escritas",
                MergeVersion(5, null, ("Allergies", "A")),
                MergeVersion(6, "", ("Allergies", "B"))),
            ("una marca mayor que su fila (reparada por estar en el futuro) cuenta como la fila",
                MergeVersion(10, StampsJson(3600, ("Allergies", 3600)), ("Allergies", "Futuro")),
                MergeVersion(20, StampsJson(20, ("Allergies", 20)), ("Allergies", "Presente"))),
            ("la misma hoja escrita por otro equipo (otro orden de llaves, minúsculas, 10.0, sin lo que vale por omisión): no hay nada que subir",
                MergeVersion(10, StampsJson(10, (Rk(MergeRowA), 10)), (col, $$"""[{"RowId":"{{MergeRowA}}","SupplyName":"Gasa","Instructions":"","Quantity":1,"UnitPrice":10,"ChargedLineId":7}]""")),
                MergeVersion(10, StampsJson(10, (Rk(MergeRowA), 10)), (col, $$"""[{"quantity":1,"supplyName":"Gasa","rowId":"{{MergeRowA}}","unitPrice":10.0,"ChargedUnits":0}]"""))),
            ("un renglón agregado en cada equipo a la misma hoja: los dos sobreviven, en el orden del lado con la marca de orden mayor",
                MergeVersion(20, StampsJson(20, (col, 20), (Rk(MergeRowA), 1), (Rk(MergeRowB), 20)), (col, SupplySheet((MergeRowA, "Gasa", 1), (MergeRowB, "Venda", 1)))),
                MergeVersion(10, StampsJson(10, (col, 10), (Rk(MergeRowA), 1), (Rk(MergeRowC), 10)), (col, SupplySheet((MergeRowA, "Gasa", 1), (MergeRowC, "Alcohol", 1))))),
            ("quitar le gana a una edición anterior",
                MergeVersion(20, StampsJson(20, (Rk(MergeRowA), 1), (Rk(MergeRowB), 20)), (col, SupplySheet((MergeRowA, "Gasa", 1), (MergeRowB, "Venda", 3)))),
                MergeVersion(30, StampsJson(30, (col, 30), (Rk(MergeRowA), 1), (Rk(MergeRowB), 1), (Qk(MergeRowB), 30)), (col, SupplySheet((MergeRowA, "Gasa", 1))))),
            ("una edición posterior le gana a quitar",
                MergeVersion(40, StampsJson(40, (Rk(MergeRowA), 1), (Rk(MergeRowB), 40)), (col, SupplySheet((MergeRowA, "Gasa", 1), (MergeRowB, "Venda", 3)))),
                MergeVersion(30, StampsJson(30, (col, 30), (Rk(MergeRowA), 1), (Rk(MergeRowB), 1), (Qk(MergeRowB), 30)), (col, SupplySheet((MergeRowA, "Gasa", 1))))),
            ("con la misma marca, gana quitar",
                MergeVersion(30, StampsJson(30, (Rk(MergeRowA), 1), (Rk(MergeRowB), 30)), (col, SupplySheet((MergeRowA, "Gasa", 1), (MergeRowB, "Venda", 3)))),
                MergeVersion(30, StampsJson(30, (col, 30), (Rk(MergeRowA), 1), (Qk(MergeRowB), 30)), (col, SupplySheet((MergeRowA, "Gasa", 1))))),
            ("una hoja sin marcas (PC v1.38) que no tiene un renglón lo quitó con su UpdatedAtUtc; lo que tiene, lo tiene desde entonces",
                MergeVersion(20, StampsJson(20, (col, 20), (Rk(MergeRowA), 1), (Rk(MergeRowB), 20)), (col, SupplySheet((MergeRowA, "Gasa", 1), (MergeRowB, "Venda", 1)))),
                MergeVersion(25, StampsJson(10), (col, SupplySheet((MergeRowA, "Gasa", 4))))),
            ("marcas de orden iguales: gana la lista de RowId vivos con el texto canónico mayor; lo del otro lado va al final",
                MergeVersion(10, StampsJson(10, (col, 10)), (col, SupplySheet((MergeRowA, "Gasa", 1), (MergeRowC, "Alcohol", 1)))),
                MergeVersion(10, StampsJson(10, (col, 10)), (col, SupplySheet((MergeRowB, "Venda", 1), (MergeRowA, "Gasa", 1))))),
            ("llaves desconocidas: se conservan con la marca mayor, y si la nube ya lo tiene todo se queda su texto tal cual",
                MergeVersion(9, StampsJson(9, ("ColumnaDeUnaVersionNueva", 4))),
                MergeVersion(10, StampsJson(10, ("Allergies", 10), ("ColumnaDeUnaVersionNueva", 8), ("Otra/llave", 9)), ("Allergies", "Látex"))),
            ("un renglón quitado más de 90 días antes de la marca más nueva se purga",
                MergeVersion(20, StampsJson(20, (Rk(MergeRowB), 1), (Qk(MergeRowB), 2))),
                MergeVersion(day90 + 10, StampsJson(day90 + 10, ("Allergies", day90 + 10)), ("Allergies", "Látex"))),
            ("la versión de aquí ya incluye la de la nube: el resultado es la de aquí y sube",
                MergeVersion(31, StampsJson(31, ("Allergies", 10), ("Phone", 31)), ("Allergies", "Penicilina"), ("Phone", "5511112222")),
                MergeVersion(10, StampsJson(10, ("Allergies", 10)), ("Allergies", "Penicilina"))),
            ("nada nuevo: se queda la versión de la nube tal cual (marca y texto de FieldStampsJson)",
                MergeVersion(10, StampsJson(10, ("Allergies", 10)), ("Allergies", "Penicilina")),
                MergeVersion(12, StampsJson(12, ("Allergies", 10)), ("Allergies", "Penicilina"))),
        };

        foreach (var (name, local, remote) in scenarios)
        {
            var result = PatientMerge.Merge(local, remote, MergeVectorColumns);
            var values = new JsonObject();
            foreach (var column in MergeVectorColumns)
            {
                values[column] = PatientSheetCanonical.IsSheet(column)
                    ? PatientSheetCanonical.Canonical(column, CanonicalJson.AsString(result.Values[column]), MergePatient)
                    : CanonicalJson.Clone(result.Values[column]);
            }
            var stamps = new JsonObject();
            foreach (var (key, value) in result.Stamps.Entries) stamps[key] = value;
            cases.Add(Case(name, new JsonObject
            {
                ["patientSyncId"] = SyncIds.Format(MergePatient),
                ["columns"] = new JsonArray(MergeVectorColumns.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
                ["local"] = VersionNode(local),
                ["remote"] = VersionNode(remote)
            }, new JsonObject
            {
                ["values"] = values,
                ["sources"] = new JsonObject(MergeVectorColumns.Select(c => KeyValuePair.Create(c, (JsonNode?)JsonValue.Create(result.Sources[c].ToString().ToLowerInvariant())))),
                ["stamps"] = stamps,
                ["differsFromRemote"] = result.DiffersFromRemote,
                ["differsFromLocal"] = result.DiffersFromLocal,
                ["updatedAtUtc"] = PatientFieldStamps.Format(result.UpdatedAtUtc),
                ["fieldStampsJson"] = result.FieldStampsJson
            }));
        }

        CheckOrWrite("contracts/golden/patient-merge.json", VectorFile("patient-merge", cases,
            "Combinación por campo al bajar una fila de Patients que ya existe aquí (Services/Rules/PatientMerge, paso 8.22; regla «Pacientes por campo» de contracts/sync/schema.json). 'local' y 'remote': UpdatedAtUtc, el texto de FieldStampsJson y los valores de las columnas que se combinan ('columns': las de Patients que viajan, menos las derivadas y FieldStampsJson; aquí unas cuantas), cada uno como valor canónico (contracts/golden/canonical-json.json); en una hoja, el texto guardado.",
            "Por campo gana la marca mayor (field-stamps.json; una marca nunca vale más que el UpdatedAtUtc de su fila; una que falta cuenta como la mínima). Una versión sin marcas (UpdatedAtUtc mayor que su llave 'UpdatedAtUtc', o sin ella) cuenta con su UpdatedAtUtc en cada campo en que difiere, en cada renglón que tiene, en la marca de orden de cada hoja, y como quitados con ella los renglones que no tiene y la otra sí. Marcas iguales y valores distintos: gana el texto canónico mayor en orden ordinal (unidades UTF-16).",
            "Hojas por RowId: la marca de un renglón y la de quitado son la mayor de los dos lados; vive si está en alguna de las dos hojas y su marca es mayor que la de quitado (iguales: quitado; sin marca de quitado: vive). Su contenido es el del lado con la marca mayor (iguales: el texto canónico mayor). Orden: con los renglones vivos, la lista del lado con la marca de orden ('<hoja>') mayor (iguales: la de texto canónico mayor, como arreglo JSON de los RowId) y al final los que solo tiene el otro lado, en su orden. Los arreglos solo llevan renglones vivos.",
            "Después: las llaves desconocidas, con la marca mayor; se purgan los quitados viejos; y se compara por significado ({\"stamps\":…,\"values\":…} canónico, sin la llave 'UpdatedAtUtc'; las hojas como arreglos canónicos). differsFromRemote: el resultado sube con UpdatedAtUtc = max(local, remote) + 1 ms y FieldStampsJson con 'UpdatedAtUtc' igual. Si no, se queda la fila de la nube tal cual: su UpdatedAtUtc y su texto de FieldStampsJson.",
            "'values' en 'expected' es canónico (las hojas como arreglos de renglones canónicos): el texto que escribe cada equipo puede ser otro. 'sources': de dónde sale el valor que se guarda: local (se queda el de aquí; también si los dos dicen lo mismo), remote (el de la nube, tal como llegó; en una hoja que dice lo mismo que la de la nube pero con otro texto, el de la nube) o merged (una hoja con renglones de los dos lados, escrita de nuevo). 'stamps' son las marcas combinadas sin 'UpdatedAtUtc'.",
            "Las columnas derivadas (contracts/sync/schema.json, 'derived') no se combinan ni se tocan. En la PC, la edad (Age) con fecha de nacimiento se compara al día en los dos lados (se calcula con BirthDate), no por el número guardado."));
    }

    // ================================================================== contracts/golden/patient-summary.json

    [TestMethod]
    public void PatientSummary_MatchesTheVectors()
    {
        var cases = new JsonArray();
        static JsonObject AppointmentNode(PatientSummaryRules.Appointment a) => new()
        {
            ["syncId"] = SyncIds.Format(a.SyncId), ["date"] = Day(a.Date), ["time"] = a.Time, ["status"] = a.Status, ["reason"] = a.Reason, ["notes"] = a.Notes
        };
        static PatientSummaryRules.Appointment A(string date, string? time, string status, string reason, int id) =>
            new(new Guid($"c0000000-0000-4000-8000-0000000000{id:00}"), DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture), time, status, reason, $"Nota {id}");

        foreach (var (zoneName, zone) in VectorZones)
        {
            var today = TodayIn(zone);
            foreach (var (name, appointments) in new (string, PatientSummaryRules.Appointment[])[]
                     {
                         ("la primera pendiente por fecha y hora, no la última que se guardó", new[]
                         {
                             A("2026-10-21", "09:00", "Programada", "Control", 1), A("2026-10-14", "12:00", "Confirmada", "Estudios", 2),
                             A("2026-10-14", "10:30", "Programada", "Revisión", 3)
                         }),
                         ("las atendidas y las canceladas no cuentan", new[]
                         {
                             A("2026-10-12", "08:00", "Atendida", "Curación", 4), A("2026-10-13", "08:00", "Cancelada", "Cancelada", 5),
                             A("2026-10-20", "08:00", "Programada", "Control", 6)
                         }),
                         ("hoy cuenta; ayer no (11/10 en la Ciudad de México y 12/10 en UTC)", new[]
                         {
                             A("2026-10-11", "18:00", "Programada", "Del 11", 7), A("2026-10-12", "07:00", "Programada", "Del 12", 8),
                             A("2026-10-30", "07:00", "Programada", "Después", 9)
                         }),
                         ("misma fecha y hora: por SyncId en minúsculas", new[]
                         {
                             A("2026-10-15", "09:00", "Programada", "Segunda", 11), A("2026-10-15", "09:00", "Programada", "Primera", 10)
                         }),
                         ("hora con AM/PM y una que no se puede leer (cuenta como las 10:00)", new[]
                         {
                             A("2026-10-16", "9:30 p. m.", "Programada", "Noche", 12), A("2026-10-16", "mediodía", "Programada", "Sin hora", 13),
                             A("2026-10-16", "09:15 AM", "Programada", "Mañana", 14)
                         }),
                         ("sin citas pendientes: sin próxima cita", new[] { A("2026-10-01", "09:00", "Programada", "Pasada", 15) }),
                     })
            {
                var next = PatientSummaryRules.Next(appointments, today);
                cases.Add(Case($"{zoneName} · próxima cita · {name}", new JsonObject
                {
                    ["check"] = "next-appointment", ["zone"] = zoneName, ["today"] = Day(today),
                    ["appointments"] = new JsonArray(appointments.Select(a => (JsonNode)AppointmentNode(a)).ToArray())
                }, new JsonObject
                {
                    ["NextAppointmentDate"] = next.Date is { } d ? TursoSyncService.FormatDate(d) : null,
                    ["NextAppointmentTime"] = next.Time, ["NextAppointmentReason"] = next.Reason, ["NextAppointmentNotes"] = next.Notes
                }));
            }
        }

        foreach (var (name, dates) in new[]
                 {
                     ("la consulta más reciente, con su hora", new[] { "2026-10-01 09:00:00.0000000", "2026-10-09 18:30:00.0000000", "2026-09-01 00:00:00.0000000" }),
                     ("sin consultas: nada que calcular (se queda la que tenía, la del alta)", Array.Empty<string>()),
                 })
        {
            var last = PatientSummaryRules.LastVisit(dates.Select(d => TursoSyncService.ParseDate(d)));
            cases.Add(Case($"última visita · {name}", new JsonObject
            {
                ["check"] = "last-visit", ["consultationDates"] = new JsonArray(dates.Select(d => (JsonNode)JsonValue.Create(d)).ToArray())
            }, last is { } l ? JsonValue.Create(TursoSyncService.FormatDate(l)) : null));
        }

        foreach (var time in new[] { "9:05", "16:00", "16:00:30", "09:00 AM", "9:15 p. m.", "12:30 am", "12:30 PM", "1:05pm", " 07:45 ", "mediodía", "25:00", "9:5", "" })
        {
            cases.Add(Case($"hora · '{time}'", new JsonObject { ["check"] = "time", ["time"] = time },
                PatientSummaryRules.TimeOfDay(time).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)));
        }

        CheckOrWrite("contracts/golden/patient-summary.json", DatedVectorFile("patient-summary", Day(TodayIn(VectorZones[1].Zone)), VectorNow.ToString("O", CultureInfo.InvariantCulture), cases,
            "El resumen derivado del expediente (Services/Rules/PatientSummaryRules, paso 8.22): NextAppointmentDate, NextAppointmentTime, NextAppointmentReason, NextAppointmentNotes y LastVisit son columnas derivadas. Cada equipo las calcula después de cada guardado y de cada bajada (y la PC, completo al abrir), suben como información y nunca se aplican al bajar, salvo al crear la fila. Agendar ya no escribe el expediente.",
            "next-appointment: de las citas del expediente que no están 'Cancelada' ni 'Atendida' y caen 'today' (el día del consultorio) o después, la primera por fecha, luego por hora ('time', abajo) y luego por SyncId en minúsculas (orden ordinal). La fecha va sin hora (dateOnly) y la hora, el motivo y las notas tal como están en la cita. Sin ninguna: fecha nula y textos ''. 'today' es el día de VectorNow en cada zona: 11/10 en la Ciudad de México, 12/10 en UTC.",
            "last-visit: la fecha (clinicLocal, con hora) de la consulta más reciente del expediente; sin consultas no hay nada que calcular (null) y se queda la que tenía.",
            "time: H:mm o HH:mm, con :ss opcional, y opcionalmente AM o PM (también a.m., p. m., sin distinguir mayúsculas ni espacios); lo que no se puede leer cuenta como las 10:00. Solo sirve para ordenar."));
    }

    // ================================================================== contracts/golden/handoff-accept.json

    [TestMethod]
    public void HandoffAccept_MatchesTheVectors()
    {
        var cases = new JsonArray();
        var col = PatientSheetRows.SuppliesColumn;
        var noteId = new Guid("d0000000-0000-4000-8000-000000000001");
        var sent = StampsJson(20, ("Allergies", 10), (Rk(MergeRowA), 20), (col, 20));

        ConsultationDraft Payload(int? version, string? stamps, Guid? note = null, int? noteVersion = null)
        {
            var draft = new ConsultationDraft();
            HandoffPayloadCodec.PrepareForSending(draft, Array.Empty<CatalogRef>(), MergePatient, version is { } v ? At(v) : null, note, null, null,
                new DateTime(2026, 10, 14, 9, 0, 0), stamps, noteVersion is { } n ? At(n) : null);
            return draft;
        }

        var day90 = (int)TimeSpan.FromDays(90).TotalSeconds;
        foreach (var (name, payload, exists, updated, stamps, noteExists, noteUpdated) in new (string, ConsultationDraft, bool, int?, string?, bool, int?)[]
                 {
                     ("con todas las marcas: se toma", Payload(20, sent), true, 31, StampsJson(31, ("Allergies", 10), ("Phone", 30), (Rk(MergeRowA), 20), (col, 31)), false, null),
                     ("la fila de aquí es más nueva por otro campo pero no tiene el renglón que agregó el otro equipo: esperar",
                         Payload(20, sent), true, 30, StampsJson(30, ("Allergies", 10), ("Phone", 30)), false, null),
                     ("la fila de aquí es más vieja que la copia: esperar", Payload(20, sent), true, 19, StampsJson(19, ("Allergies", 10), (Rk(MergeRowA), 19)), false, null),
                     ("aquí la fila la escribió una PC sin marcas después: cada marca cuenta al menos como su UpdatedAtUtc",
                         Payload(20, sent), true, 40, StampsJson(15, ("Allergies", 10)), false, null),
                     ("una marca de quitado que aquí ya se purgaría no se exige",
                         Payload(20, StampsJson(20, (Qk(MergeRowB), 1))), true, day90 + 10, StampsJson(day90 + 10, ("Phone", day90 + 10)), false, null),
                     ("todavía no llega el expediente", Payload(20, sent), false, null, null, false, null),
                     ("copia de antes del paso (sin marcas): solo la marca de la fila", Payload(20, null), true, 30, StampsJson(30, ("Phone", 30)), false, null),
                     ("con la nota: todavía no llega", Payload(20, sent, noteId, 25), true, 31, StampsJson(31, ("Allergies", 10), (Rk(MergeRowA), 20), (col, 31)), false, null),
                     ("con la nota: llegó una versión anterior", Payload(20, sent, noteId, 25), true, 31, StampsJson(31, ("Allergies", 10), (Rk(MergeRowA), 20), (col, 31)), true, 24),
                     ("con la nota: llegó su versión", Payload(20, sent, noteId, 25), true, 31, StampsJson(31, ("Allergies", 10), (Rk(MergeRowA), 20), (col, 31)), true, 25),
                 })
        {
            var arrival = HandoffPayloadCodec.CheckArrival(payload, exists, updated is { } u ? At(u) : null, stamps, noteExists, noteUpdated is { } n ? At(n) : null);
            var payloadNode = new JsonObject
            {
                ["PatientUpdatedAtUtc"] = payload.PatientUpdatedAtUtc is { } pu ? PatientFieldStamps.Format(pu) : null,
                ["PatientFieldStamps"] = payload.PatientFieldStamps == null
                    ? null
                    : new JsonObject(payload.PatientFieldStamps.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(PatientFieldStamps.Format(kv.Value))))),
                ["ProgressNoteSyncId"] = payload.ProgressNoteSyncId is { } ns ? SyncIds.Format(ns) : null,
                ["ProgressNoteUpdatedAtUtc"] = payload.ProgressNoteUpdatedAtUtc is { } nu ? PatientFieldStamps.Format(nu) : null
            };
            cases.Add(Case(name, new JsonObject
            {
                ["payload"] = payloadNode,
                ["local"] = new JsonObject
                {
                    ["patientExists"] = exists,
                    ["updatedAtUtc"] = updated is { } lu ? Stamp(lu) : null,
                    ["fieldStampsJson"] = stamps,
                    ["noteExists"] = noteExists,
                    ["noteUpdatedAtUtc"] = noteUpdated is { } lnu ? Stamp(lnu) : null
                }
            }, arrival.ToString()));
        }

        CheckOrWrite("contracts/golden/handoff-accept.json", VectorFile("handoff-accept", cases,
            "Aceptar una consulta recibida (Services/Rules/HandoffPayloadCodec.CheckArrival, paso 8.22), después de sincronizar. La copia (ConsultationDraft, PayloadVersion 1) trae, además de PatientUpdatedAtUtc, PatientFieldStamps (las marcas de FieldStampsJson del expediente en el equipo que envía, sin 'UpdatedAtUtc', cada una como cuenta en su fila; en la copia van como fecha ISO 8601 sin zona) y ProgressNoteUpdatedAtUtc (la marca del avance de la nota). Son campos aditivos: una copia de antes no los trae.",
            "Orden: sin expediente aquí → PatientMissing. UpdatedAtUtc de aquí menor que PatientUpdatedAtUtc → PatientBehind. Con PatientFieldStamps: cada marca de la copia debe ser menor o igual a la de aquí (la de aquí nunca vale más que su UpdatedAtUtc; si la fila de aquí es una escritura sin marcas, cuenta al menos como su UpdatedAtUtc); una llave de quitado cuya marca ya se purgaría aquí (anterior a la marca más nueva de aquí − 90 días) no se exige; si falta alguna → PatientBehind. Con ProgressNoteSyncId: sin la nota → NoteMissing; con ProgressNoteUpdatedAtUtc y la nota de aquí más vieja → NoteBehind. Si no, Ready.",
            "En pantalla: PatientMissing 'Todavía no llega el expediente de … a este equipo', PatientBehind 'Todavía no llegan los últimos cambios del expediente de …', NoteMissing 'Todavía no llega el avance de la nota de esta consulta', NoteBehind 'Todavía no llega la última versión del avance de la nota de esta consulta', todos con 'Intente de nuevo en unos segundos.'; la consulta sigue ofrecida."));
    }
}
