using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.22 (H1b), la combinación por campo como función pura (Services/Rules/PatientMerge): por campo gana la marca mayor,
/// una escritura sin marcas equivale al LWW, las hojas se combinan por renglón, lo desconocido se conserva, y cualquier
/// intercalado de ediciones y sincronizaciones entre 2 y 3 equipos converge sin subidas repetidas.
/// </summary>
[TestClass]
public class PatientMergeTests
{
    internal static readonly Guid Patient = new("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    internal static readonly string[] Columns =
    {
        nameof(PatientRecord.Allergies), nameof(PatientRecord.Phone), nameof(PatientRecord.FullName), nameof(PatientRecord.Age),
        nameof(PatientRecord.IsActive), nameof(PatientRecord.ActiveSuppliesJson), nameof(PatientRecord.ActivePrescriptionJson)
    };

    internal static DateTime T(double seconds) => new DateTime(2026, 10, 14, 15, 0, 0).AddSeconds(seconds);

    internal static string S(double seconds) => PatientFieldStamps.Format(T(seconds));

    /// <summary>Una versión: valores por columna (las que faltan, vacías) y sus marcas.</summary>
    internal static PatientMerge.Version V(DateTime updated, string? stamps, params (string Column, object? Value)[] values)
    {
        var dict = Columns.ToDictionary(c => c, c => c switch
        {
            nameof(PatientRecord.Age) => (JsonNode?)JsonValue.Create(30),
            nameof(PatientRecord.IsActive) => JsonValue.Create(true),
            _ => JsonValue.Create(string.Empty)
        }, StringComparer.Ordinal);
        foreach (var (column, value) in values) dict[column] = CanonicalJson.FromValue(value);
        return new PatientMerge.Version { SyncId = Patient, UpdatedAtUtc = updated, FieldStampsJson = stamps, Values = dict };
    }

    internal static string Stamps(DateTime row, params (string Key, double Seconds)[] entries)
    {
        var stamps = new PatientFieldStamps();
        foreach (var (key, seconds) in entries) stamps[key] = S(seconds);
        stamps.RowStamp = row;
        return stamps.ToJson();
    }

    internal static string Supplies(params (Guid Id, string Name, int Quantity)[] rows) =>
        JsonSerializer.Serialize(rows.Select(r => new { RowId = r.Id, SupplyName = r.Name, Instructions = "", Quantity = r.Quantity, UnitPrice = 10 }));

    private static readonly Guid RowA = new("a0000000-0000-4000-8000-000000000001");
    private static readonly Guid RowB = new("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid RowC = new("a0000000-0000-4000-8000-000000000003");

    private static string Value(PatientMerge.Result result, string column) => CanonicalJson.AsString(result.Values[column]) ?? string.Empty;

    private static List<string> SupplyNames(PatientMerge.Result result) =>
        PatientSheetCanonical.ReadRows(PatientSheetRows.SuppliesColumn, Value(result, PatientSheetRows.SuppliesColumn), Patient)
            .Select(r => ((PatientSupplyItem)r.Value).SupplyName).ToList();

    // ================================================================== por campo

    [TestMethod]
    public void AnAllergyEditOnOneSide_AndAPhoneEditOnTheOther_BothSurvive_OnBothSides()
    {
        var pc = V(T(20), Stamps(T(20), ("Phone", 20)), ("Phone", "5511112222"));
        var tablet = V(T(10), Stamps(T(10), ("Allergies", 10)), ("Allergies", "Penicilina"));

        foreach (var (local, remote) in new[] { (pc, tablet), (tablet, pc) })
        {
            var merged = PatientMerge.Merge(local, remote, Columns);
            Assert.AreEqual("Penicilina", Value(merged, "Allergies"));
            Assert.AreEqual("5511112222", Value(merged, "Phone"));
            Assert.IsTrue(merged.DiffersFromRemote, "El resultado sube");
            Assert.AreEqual(T(20).AddMilliseconds(1), merged.UpdatedAtUtc, "max(L, R) + 1 ms");
            var stamps = PatientFieldStamps.Parse(merged.FieldStampsJson);
            Assert.AreEqual(S(10), stamps["Allergies"]);
            Assert.AreEqual(S(20), stamps["Phone"]);
            Assert.AreEqual(merged.UpdatedAtUtc, stamps.RowStamp, "Las marcas quedan al día con la fila");
        }
    }

    [TestMethod]
    public void TheHigherStampWins_AndAMissingStampCountsAsTheLowest()
    {
        var older = V(T(30), Stamps(T(30), ("Allergies", 5)), ("Allergies", "Sulfas"));
        var newer = V(T(10), Stamps(T(10), ("Allergies", 10)), ("Allergies", "Penicilina"));
        Assert.AreEqual("Penicilina", Value(PatientMerge.Merge(older, newer, Columns), "Allergies"), "La marca del campo, no la de la fila");

        var unstamped = V(T(30), Stamps(T(30), ("Phone", 30)), ("Allergies", "Sulfas"));
        Assert.AreEqual("Penicilina", Value(PatientMerge.Merge(unstamped, newer, Columns), "Allergies"), "Sin marca cuenta como la mínima");
    }

    [TestMethod]
    public void EqualStamps_DifferentValues_TheLargerCanonicalTextWins_OnBothSides()
    {
        // Orden ordinal, no el de es-MX: "Ácido" (U+00C1) va después de "Agua"
        var a = V(T(10), Stamps(T(10), ("Allergies", 10)), ("Allergies", "Agua"));
        var b = V(T(10), Stamps(T(10), ("Allergies", 10)), ("Allergies", "Ácido"));
        Assert.AreEqual("Ácido", Value(PatientMerge.Merge(a, b, Columns), "Allergies"));
        Assert.AreEqual("Ácido", Value(PatientMerge.Merge(b, a, Columns), "Allergies"));
    }

    [TestMethod]
    public void AWriteWithoutStamps_WinsWithItsUpdatedAtUtc_LikeTheRowLww()
    {
        // La nube tiene una fila de una PC v1.38: su UpdatedAtUtc es mayor que el de sus marcas (que no tocó)
        var oldPc = V(T(50), Stamps(T(10), ("Allergies", 10)), ("Allergies", "Ninguna"), ("Phone", "5500000000"));
        var here = V(T(40), Stamps(T(40), ("Allergies", 10), ("Phone", 30), ("FullName", 45)),
            ("Allergies", "Penicilina"), ("Phone", "5511111111"), ("FullName", "García, Ana"));

        var merged = PatientMerge.Merge(here, oldPc, Columns);
        Assert.AreEqual("Ninguna", Value(merged, "Allergies"), "Lo que difiere cuenta con el UpdatedAtUtc de la PC vieja (50 > 10)");
        Assert.AreEqual("5500000000", Value(merged, "Phone"), "50 > 30");
        Assert.AreEqual(string.Empty, Value(merged, "FullName"), "También el nombre: la PC vieja escribió la fila completa con el suyo (50 > 45)");

        // Una edición de aquí posterior a la escritura sin marcas se queda
        var later = V(T(60), Stamps(T(60), ("Phone", 60)), ("Allergies", "Penicilina"), ("Phone", "5511111111"));
        merged = PatientMerge.Merge(later, oldPc, Columns);
        Assert.AreEqual("5511111111", Value(merged, "Phone"));
        Assert.AreEqual("Ninguna", Value(merged, "Allergies"));

        // Dos filas sin marcas (antes de actualizar): exactamente el LWW por fila
        var lwwOld = V(T(5), null, ("Allergies", "A"), ("Phone", "1"));
        var lwwNew = V(T(6), null, ("Allergies", "B"), ("Phone", "1"));
        merged = PatientMerge.Merge(lwwOld, lwwNew, Columns);
        Assert.AreEqual("B", Value(merged, "Allergies"));
        Assert.AreEqual(S(6), merged.Stamps["Allergies"], "La marca queda escrita");
    }

    [TestMethod]
    public void AStampNeverCountsForMoreThanItsRowUpdatedAtUtc()
    {
        // Una marca reparada en la fila (paso 8.7) pero no en el campo: el campo vale lo que la fila
        var repaired = V(T(10), Stamps(T(3600), ("Allergies", 3600)), ("Allergies", "Futuro"));
        var other = V(T(20), Stamps(T(20), ("Allergies", 20)), ("Allergies", "Presente"));
        Assert.AreEqual("Presente", Value(PatientMerge.Merge(repaired, other, Columns), "Allergies"));
    }

    [TestMethod]
    public void UnknownStampKeys_AreKeptWithTheHigherStamp()
    {
        var newer = V(T(10), Stamps(T(10), ("Allergies", 10), ("ColumnaDeUnaVersionNueva", 8), ("Otra/llave", 9)), ("Allergies", "X"));
        var here = V(T(9), Stamps(T(9), ("ColumnaDeUnaVersionNueva", 4)));
        var merged = PatientMerge.Merge(here, newer, Columns);
        Assert.AreEqual(S(8), merged.Stamps["ColumnaDeUnaVersionNueva"]);
        Assert.AreEqual(S(9), merged.Stamps["Otra/llave"]);
        Assert.IsFalse(merged.DiffersFromRemote, "Nada que subir: la de la nube ya lo tiene todo");
        Assert.AreEqual(newer.FieldStampsJson, merged.FieldStampsJson, "Se queda el texto de la nube tal cual");
    }

    [TestMethod]
    public void TheSameMeaning_InAnotherText_IsNotADifference()
    {
        // La misma hoja escrita por otro equipo: llaves en otro orden y minúsculas, 10.0 contra 10, sin campos por omisión
        var pcText = Supplies((RowA, "Gasa", 1));
        var tabletText = $"[{{\"quantity\":1,\"supplyName\":\"Gasa\",\"rowId\":\"{RowA}\",\"unitPrice\":10.0,\"ChargedUnits\":0}}]";
        var pc = V(T(10), Stamps(T(10), ($"{PatientSheetRows.SuppliesColumn}/{RowA}", 10)), (PatientSheetRows.SuppliesColumn, pcText));
        var tablet = V(T(10), Stamps(T(10), ($"{PatientSheetRows.SuppliesColumn}/{RowA}", 10)), (PatientSheetRows.SuppliesColumn, tabletText));
        var merged = PatientMerge.Merge(pc, tablet, Columns);
        Assert.IsFalse(merged.DiffersFromRemote);
        Assert.IsFalse(merged.DiffersFromLocal);
    }

    // ================================================================== hojas

    [TestMethod]
    public void RowsAddedOnBothSides_ToTheSameSheet_BothSurvive_InTheSameOrderOnBothSides()
    {
        var col = PatientSheetRows.SuppliesColumn;
        var pc = V(T(20), Stamps(T(20), (col, 20), ($"{col}/{RowA}", 1), ($"{col}/{RowB}", 20)), (col, Supplies((RowA, "Gasa", 1), (RowB, "Venda", 1))));
        var tablet = V(T(10), Stamps(T(10), (col, 10), ($"{col}/{RowA}", 1), ($"{col}/{RowC}", 10)), (col, Supplies((RowA, "Gasa", 1), (RowC, "Alcohol", 1))));

        var one = PatientMerge.Merge(pc, tablet, Columns);
        var two = PatientMerge.Merge(tablet, pc, Columns);
        CollectionAssert.AreEqual(new[] { "Gasa", "Venda", "Alcohol" }, SupplyNames(one), "El orden del lado con la marca de orden mayor, y al final lo del otro");
        CollectionAssert.AreEqual(SupplyNames(one), SupplyNames(two));
        Assert.AreEqual(PatientMerge.Source.Merged, one.Sources[col]);
    }

    [TestMethod]
    public void ARemovalWinsOverAnOlderEdit_AndANewerEditWinsOverAnOlderRemoval()
    {
        var col = PatientSheetRows.SuppliesColumn;
        // La tablet quitó B a los 30; la PC le había cambiado la cantidad a los 20
        var removed = V(T(30), Stamps(T(30), (col, 30), ($"{col}/{RowA}", 1), ($"{col}/{RowB}", 1), ($"{col}/{RowB}/quitado", 30)), (col, Supplies((RowA, "Gasa", 1))));
        var edited = V(T(20), Stamps(T(20), ($"{col}/{RowA}", 1), ($"{col}/{RowB}", 20)), (col, Supplies((RowA, "Gasa", 1), (RowB, "Venda", 3))));
        CollectionAssert.AreEqual(new[] { "Gasa" }, SupplyNames(PatientMerge.Merge(edited, removed, Columns)));
        CollectionAssert.AreEqual(new[] { "Gasa" }, SupplyNames(PatientMerge.Merge(removed, edited, Columns)));

        // Al revés: la edición (a los 40) es posterior a quitarlo (a los 30)
        var editedLater = V(T(40), Stamps(T(40), ($"{col}/{RowA}", 1), ($"{col}/{RowB}", 40)), (col, Supplies((RowA, "Gasa", 1), (RowB, "Venda", 3))));
        var merged = PatientMerge.Merge(removed, editedLater, Columns);
        CollectionAssert.AreEqual(new[] { "Gasa", "Venda" }, SupplyNames(merged));
        Assert.AreEqual(S(30), merged.Stamps[$"{col}/{RowB}/quitado"], "La marca de quitado se queda (la edición la rebasa)");

        // Con marcas iguales gana quitado
        var sameTime = V(T(30), Stamps(T(30), ($"{col}/{RowA}", 1), ($"{col}/{RowB}", 30)), (col, Supplies((RowA, "Gasa", 1), (RowB, "Venda", 3))));
        CollectionAssert.AreEqual(new[] { "Gasa" }, SupplyNames(PatientMerge.Merge(sameTime, removed, Columns)));
    }

    [TestMethod]
    public void AnOldFormatReader_SeesOnlyTheLiveRows()
    {
        var col = PatientSheetRows.SuppliesColumn;
        var removed = V(T(30), Stamps(T(30), (col, 30), ($"{col}/{RowB}/quitado", 30)), (col, Supplies((RowA, "Gasa", 1), (RowC, "Alcohol", 2))));
        var edited = V(T(20), Stamps(T(20), ($"{col}/{RowB}", 20), ($"{col}/{RowC}", 20)), (col, Supplies((RowA, "Gasa", 1), (RowB, "Venda", 3), (RowC, "Alcohol", 2))));
        var merged = PatientMerge.Merge(edited, removed, Columns);
        var text = Value(merged, col);

        // Como lee una PC v1.36–v1.38 (y cualquier lector de la hoja): cada elemento del arreglo es un renglón vivo
        var plain = JsonSerializer.Deserialize<List<PatientSupplyItem>>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        CollectionAssert.AreEqual(new[] { "Gasa", "Alcohol" }, plain.Select(r => r.SupplyName).ToList());
        var record = new PatientRecord { SyncId = Patient, ActiveSuppliesJson = text };
        CollectionAssert.AreEqual(new[] { "Gasa", "Alcohol" }, record.ActiveSupplies.Select(r => r.SupplyName).ToList());
        Assert.IsFalse(text.Contains("quitado", StringComparison.OrdinalIgnoreCase), "Lo quitado vive en FieldStampsJson");
        Assert.AreEqual(S(30), merged.Stamps[$"{col}/{RowB}/quitado"]);
    }

    [TestMethod]
    public void RemovedRows_ArePurged90DaysAfterTheNewestStamp_AndOnlyThen()
    {
        var col = PatientSheetRows.SuppliesColumn;
        var stamps = PatientFieldStamps.Parse(Stamps(T(0), ($"{col}/{RowB}", 1), ($"{col}/{RowB}/quitado", 2), ($"{col}/{RowC}/quitado", 3), ("Phone", 4)));
        stamps.Purge(PatientSheetCanonical.Columns);
        Assert.IsNotNull(stamps[$"{col}/{RowB}/quitado"], "Sin una marca 90 días más nueva, se queda");

        var later = TimeSpan.FromDays(90).TotalSeconds;
        stamps["Allergies"] = S(later + 2.5);
        stamps.Purge(PatientSheetCanonical.Columns);
        Assert.IsNull(stamps[$"{col}/{RowB}/quitado"], "Quitado a los 2 s, 90 días antes de la marca más nueva");
        Assert.IsNull(stamps[$"{col}/{RowB}"], "Con la llave del renglón");
        Assert.IsNotNull(stamps[$"{col}/{RowC}/quitado"], "A los 3 s todavía no");
        Assert.AreEqual(S(4), stamps["Phone"], "Las de los campos nunca se purgan");

        // Una combinación purga lo mismo de los dos lados: la purga no se vuelve una diferencia que suba sin fin
        var a = V(T(later + 10), Stamps(T(later + 10), ("Allergies", later + 10)), ("Allergies", "Z"));
        var b = V(T(20), Stamps(T(20), ($"{col}/{RowB}", 1), ($"{col}/{RowB}/quitado", 2)));
        var merged = PatientMerge.Merge(b, a, Columns);
        Assert.IsNull(merged.Stamps[$"{col}/{RowB}/quitado"]);
        var again = PatientMerge.Merge(b, new PatientMerge.Version
        {
            SyncId = Patient, UpdatedAtUtc = merged.UpdatedAtUtc, FieldStampsJson = merged.FieldStampsJson, Values = merged.Values
        }, Columns);
        Assert.IsFalse(again.DiffersFromRemote, "Combinar otra vez con la versión vieja no la regresa");
    }

    // ================================================================== el sello

    [TestMethod]
    public void ALocalWrite_StampsTheChangedFields_AndEachSheetRow()
    {
        var col = PatientSheetRows.SuppliesColumn;
        var stamps = PatientFieldStamps.Parse("{\"Otra\":\"x\"}");
        stamps.ApplyLocalWrite(T(10), new[] { "Phone" },
            new[] { (col, (string?)Supplies((RowA, "Gasa", 1), (RowB, "Venda", 1)), (string?)Supplies((RowA, "Gasa", 2), (RowC, "Alcohol", 1))) }, Patient);
        Assert.AreEqual(S(10), stamps["Phone"]);
        Assert.AreEqual(S(10), stamps[$"{col}/{RowA}"], "Cambió la cantidad");
        Assert.AreEqual(S(10), stamps[$"{col}/{RowC}"], "Nuevo");
        Assert.AreEqual(S(10), stamps[$"{col}/{RowB}/quitado"], "Quitado");
        Assert.IsNull(stamps[$"{col}/{RowB}"]);
        Assert.AreEqual(S(10), stamps[col], "Cambió qué renglones hay");
        Assert.AreEqual(T(10), stamps.RowStamp);
        Assert.AreEqual("x", stamps["Otra"], "Lo desconocido se queda");

        // Solo cambia el contenido de un renglón: la hoja no marca su orden
        var only = new PatientFieldStamps();
        only.ApplyLocalWrite(T(11), Array.Empty<string>(), new[] { (col, (string?)Supplies((RowA, "Gasa", 1)), (string?)Supplies((RowA, "Gasa", 5))) }, Patient);
        Assert.IsNull(only[col]);
        Assert.AreEqual(S(11), only[$"{col}/{RowA}"]);
    }

    // ================================================================== el semirretículo

    private static PatientMerge.Version AsVersion(PatientMerge.Result r) =>
        new() { SyncId = Patient, UpdatedAtUtc = r.UpdatedAtUtc, FieldStampsJson = r.FieldStampsJson, Values = r.Values };

    private static string Meaning(PatientMerge.Version v) =>
        PatientMerge.CanonicalVersion(v.Values, PatientFieldStamps.Parse(v.FieldStampsJson), Columns, Patient);

    private static string Meaning(PatientMerge.Result r) => PatientMerge.CanonicalVersion(r.Values, r.Stamps, Columns, Patient);

    /// <summary>
    /// Con versiones al azar (con y sin marcas): conmutativa, idempotente y absorbe (combinar con una versión que ya incluye el
    /// resultado no cambia nada: así nadie vuelve a subir). Entre versiones con marcas, lo que se sabe (valores, marcas y qué
    /// renglones viven) es además asociativo; el orden de los renglones depende de qué versión ganó el orden, pero una vez que
    /// todos saben lo mismo, gana la lista de texto canónico mayor y deja de cambiar.
    /// </summary>
    [TestMethod]
    public void TheMerge_IsCommutativeIdempotentAndAbsorbing()
    {
        var random = new Random(822);
        for (int i = 0; i < 2000; i++)
        {
            var a = RandomVersion(random);
            var b = RandomVersion(random);
            var ab = PatientMerge.Merge(a, b, Columns);
            var ba = PatientMerge.Merge(b, a, Columns);
            Assert.AreEqual(Meaning(ab), Meaning(ba), $"Conmutativa (caso {i})");

            var m = AsVersion(ab);
            Assert.IsFalse(PatientMerge.Merge(m, m, Columns).DiffersFromRemote, $"Idempotente (caso {i})");
            Assert.IsFalse(PatientMerge.Merge(a, m, Columns).DiffersFromRemote, $"Absorbe: combinar con lo que ya la incluye (caso {i})");
            Assert.IsFalse(PatientMerge.Merge(b, m, Columns).DiffersFromRemote, $"Absorbe del otro lado (caso {i})");

            var c = RandomVersion(random);
            if (new[] { a, b, c }.Any(v => PatientFieldStamps.Parse(v.FieldStampsJson).IsStampless(v.UpdatedAtUtc))) continue;
            var left = PatientMerge.Merge(m, c, Columns);
            var right = PatientMerge.Merge(a, AsVersion(PatientMerge.Merge(b, c, Columns)), Columns);
            Assert.AreEqual(Knowledge(left), Knowledge(right), $"Asociativa en lo que se sabe (caso {i})");
        }
    }

    /// <summary>Lo que se sabe de una versión: su significado con los renglones de las hojas en orden de RowId.</summary>
    private static string Knowledge(PatientMerge.Result r)
    {
        var node = JsonNode.Parse(Meaning(r))!;
        foreach (var column in PatientSheetCanonical.Columns.Where(Columns.Contains))
        {
            var rows = node["values"]![column]!.AsArray().Select(n => CanonicalJson.Write(n)).OrderBy(t => t, StringComparer.Ordinal).ToList();
            node["values"]![column] = new JsonArray(rows.Select(t => JsonNode.Parse(t)).ToArray());
        }
        return CanonicalJson.Write(node);
    }

    private static readonly string[] Words = { "", "Penicilina", "Sulfas", "Ácido", "Agua", "5511112222" };
    private static readonly Guid[] RowIds = Enumerable.Range(1, 5).Select(i => new Guid($"b0000000-0000-4000-8000-00000000000{i}")).ToArray();

    private static PatientMerge.Version RandomVersion(Random random)
    {
        var col = PatientSheetRows.SuppliesColumn;
        var updated = T(random.Next(1, 50));
        bool stampless = random.Next(4) == 0;
        var stamps = new PatientFieldStamps();
        var values = new List<(string, object?)>
        {
            ("Allergies", Words[random.Next(Words.Length)]),
            ("Phone", Words[random.Next(Words.Length)]),
            ("Age", random.Next(30, 32))
        };
        foreach (var field in new[] { "Allergies", "Phone", "Age" })
        {
            if (random.Next(3) > 0) stamps[field] = S(random.Next(0, 50));
        }
        var rows = RowIds.Where(_ => random.Next(2) == 0).OrderBy(_ => random.Next()).Select(id => (id, $"Insumo {id.ToString()[^1]}", random.Next(1, 3))).ToArray();
        values.Add((col, Supplies(rows)));
        foreach (var id in RowIds)
        {
            if (random.Next(3) > 0) stamps[$"{col}/{id}"] = S(random.Next(0, 50));
            if (random.Next(4) == 0) stamps[$"{col}/{id}/quitado"] = S(random.Next(0, 50));
        }
        if (random.Next(2) == 0) stamps[col] = S(random.Next(0, 50));
        if (!stampless)
        {
            // Una versión con marcas es coherente con su hoja: lo que tiene vive (su marca rebasa la de quitado) y lo que no
            // tiene, si tiene marca, está quitado. Una sin marcas puede traer cualquier hoja (la escribió una PC anterior)
            var present = rows.Select(r => r.id).ToHashSet();
            foreach (var id in RowIds)
            {
                var edit = stamps[$"{col}/{id}"];
                var removed = stamps[$"{col}/{id}/quitado"];
                if (present.Contains(id))
                {
                    if (removed != null && PatientFieldStamps.Compare(edit, removed) <= 0)
                        stamps[$"{col}/{id}"] = PatientFieldStamps.Format(PatientFieldStamps.ParseStamp(removed)!.Value.AddSeconds(1));
                }
                else if (edit != null && PatientFieldStamps.Compare(edit, removed) > 0)
                {
                    stamps[$"{col}/{id}/quitado"] = edit;
                }
            }
        }
        // Una versión con marcas las tiene al día con su fila (todas menores o iguales); una sin marcas, más vieja
        var maxStamp = stamps.Newest() ?? T(0);
        if (updated < maxStamp) updated = maxStamp;
        stamps.RowStamp = stampless ? updated.AddSeconds(-1) : updated;
        return V(updated, stamps.ToJson(), values.ToArray());
    }

    // ================================================================== prueba de propiedades: equipos que editan y sincronizan

    /// <summary>Un equipo simulado: su versión, si tiene algo sin subir y su reloj.</summary>
    private sealed class SimDevice
    {
        public required string Name { get; init; }
        public PatientMerge.Version Local { get; set; } = null!;
        public bool Pending { get; set; }
    }

    private sealed class SimCloud
    {
        public PatientMerge.Version? Row { get; set; }
        public string Origin { get; set; } = string.Empty;
        public int Uploads { get; set; }

        /// <summary>LWW por fila, como la nube: acepta solo una versión estrictamente más nueva.</summary>
        public bool TryUpload(SimDevice device)
        {
            if (Row != null && device.Local.UpdatedAtUtc <= Row.UpdatedAtUtc) return false;
            Row = device.Local;
            Origin = device.Name;
            Uploads++;
            return true;
        }
    }

    /// <summary>Lo que hace la sincronización de la PC: subir lo pendiente, bajar, combinar y subir lo que la combinación dejó distinto.</summary>
    private static void Sync(SimDevice device, SimCloud cloud)
    {
        if (device.Pending)
        {
            if (cloud.TryUpload(device)) device.Pending = false;
            else Apply(device, cloud);
        }
        if (cloud.Row != null && cloud.Origin != device.Name) Apply(device, cloud);
        if (device.Pending && cloud.TryUpload(device)) device.Pending = false;
    }

    private static void Apply(SimDevice device, SimCloud cloud)
    {
        var merged = PatientMerge.Merge(device.Local, cloud.Row!, Columns);
        device.Local = AsVersion(merged);
        device.Pending = merged.DiffersFromRemote;
    }

    /// <summary>Un guardado local: los valores nuevos y el sello, con la marca monótona de siempre.</summary>
    private static void Edit(SimDevice device, DateTime now, Random random, ISet<string>? onlyFields = null)
    {
        var col = PatientSheetRows.SuppliesColumn;
        var before = device.Local;
        var values = new Dictionary<string, JsonNode?>(before.Values.ToDictionary(kv => kv.Key, kv => CanonicalJson.Clone(kv.Value)), StringComparer.Ordinal);
        var changed = new List<string>();
        var sheets = new List<(string, string?, string?)>();
        var choice = random.Next(onlyFields == null ? 4 : 3);
        if (choice < 2)
        {
            var field = onlyFields?.ElementAt(random.Next(onlyFields.Count)) ?? (choice == 0 ? "Allergies" : "Phone");
            var value = Words[random.Next(Words.Length)] + (onlyFields != null ? $" {device.Name} {now.Ticks}" : string.Empty);
            if (CanonicalJson.Write(values[field]) != CanonicalJson.Quote(value)) changed.Add(field);
            values[field] = JsonValue.Create(value);
        }
        else
        {
            var text = CanonicalJson.AsString(values[col]);
            var rows = PatientSheetCanonical.ReadRows(col, text, Patient).Select(r => (PatientSupplyItem)r.Value).ToList();
            int action = onlyFields != null ? 0 : random.Next(3);
            if (action == 0 || rows.Count == 0) rows.Add(new PatientSupplyItem { RowId = Guid.NewGuid(), SupplyName = $"Insumo {device.Name} {random.Next(1000)}", Quantity = 1 });
            else if (action == 1) rows.RemoveAt(random.Next(rows.Count));
            else rows[random.Next(rows.Count)].Quantity += 1;
            var after = PatientSheetCanonical.Write(col, rows);
            sheets.Add((col, text, after));
            values[col] = JsonValue.Create(after);
        }
        var stamp = SyncRules.NextLocalStamp(now, before.UpdatedAtUtc, TimeSpan.Zero);
        var stamps = PatientFieldStamps.Parse(before.FieldStampsJson);
        stamps.ApplyLocalWrite(stamp, changed, sheets, Patient);
        device.Local = new PatientMerge.Version { SyncId = Patient, UpdatedAtUtc = stamp, FieldStampsJson = stamps.ToJson(), Values = values };
        device.Pending = true;
    }

    private static (List<SimDevice> Devices, SimCloud Cloud) StartDevices(int count)
    {
        var start = V(T(0), Stamps(T(0)), ("Allergies", "Negadas"), ("Phone", "5500000000"),
            (PatientSheetRows.SuppliesColumn, Supplies((RowA, "Gasa", 1))));
        var devices = Enumerable.Range(0, count).Select(i => new SimDevice { Name = "equipo" + i, Local = start }).ToList();
        return (devices, new SimCloud { Row = start, Origin = "inicio" });
    }

    /// <summary>Sincroniza todos hasta que una vuelta completa no sube nada; regresa cuántas vueltas hicieron falta.</summary>
    private static int Quiesce(List<SimDevice> devices, SimCloud cloud)
    {
        for (int round = 1; round <= 10; round++)
        {
            int before = cloud.Uploads;
            foreach (var device in devices) Sync(device, cloud);
            if (cloud.Uploads == before && devices.All(d => !d.Pending)) return round;
        }
        Assert.Fail("No convergió en 10 vueltas: hay subidas que se repiten");
        return -1;
    }

    [TestMethod]
    public void AnyInterleavingOfEditsAndSyncs_Converges_WithoutRepeatedUploads()
    {
        for (int seed = 1; seed <= 200; seed++)
        {
            var random = new Random(seed);
            var (devices, cloud) = StartDevices(2 + seed % 2);
            var now = T(1);
            for (int step = 0; step < 40; step++)
            {
                // A veces dos equipos en el mismo instante (marcas iguales), a veces con relojes que se adelantan
                if (random.Next(4) > 0) now = now.AddMilliseconds(random.Next(0, 3));
                var device = devices[random.Next(devices.Count)];
                if (random.Next(3) == 0) Sync(device, cloud);
                else Edit(device, now.AddMilliseconds(random.Next(-2, 3)), random);
            }

            int rounds = Quiesce(devices, cloud);
            Assert.IsLessThanOrEqualTo(4, rounds, $"Semilla {seed}: demasiadas vueltas para converger");
            var meaning = Meaning(cloud.Row!);
            foreach (var device in devices) Assert.AreEqual(meaning, Meaning(device.Local), $"Semilla {seed}: {device.Name} no quedó igual a la nube");

            int uploads = cloud.Uploads;
            foreach (var device in devices) Sync(device, cloud);
            Assert.AreEqual(uploads, cloud.Uploads, $"Semilla {seed}: una vez iguales, nadie vuelve a subir");
        }
    }

    [TestMethod]
    public void DevicesThatEditDifferentFields_AndAddRows_NeverLoseAnEdit()
    {
        for (int seed = 1; seed <= 100; seed++)
        {
            var random = new Random(1000 + seed);
            var (devices, cloud) = StartDevices(3);
            var owned = new Dictionary<string, HashSet<string>>
            {
                ["equipo0"] = new() { "Allergies" },
                ["equipo1"] = new() { "Phone" },
                ["equipo2"] = new() { "FullName" }
            };
            var lastValue = new Dictionary<string, string>();
            var addedRows = new HashSet<Guid>();
            var now = T(1);
            for (int step = 0; step < 30; step++)
            {
                now = now.AddMilliseconds(random.Next(0, 3));
                var device = devices[random.Next(devices.Count)];
                if (random.Next(3) == 0)
                {
                    Sync(device, cloud);
                    continue;
                }
                Edit(device, now, random, owned[device.Name]);
                var field = owned[device.Name].Single();
                lastValue[field] = CanonicalJson.AsString(device.Local.Values[field])!;
                foreach (var row in PatientSheetCanonical.ReadRows(PatientSheetRows.SuppliesColumn,
                             CanonicalJson.AsString(device.Local.Values[PatientSheetRows.SuppliesColumn]), Patient))
                {
                    addedRows.Add(row.RowId);
                }
            }

            Quiesce(devices, cloud);
            foreach (var (field, value) in lastValue)
            {
                Assert.AreEqual(value, CanonicalJson.AsString(cloud.Row!.Values[field]), $"Semilla {seed}: se perdió la última edición de {field}");
            }
            var finalRows = PatientSheetCanonical.ReadRows(PatientSheetRows.SuppliesColumn,
                CanonicalJson.AsString(cloud.Row!.Values[PatientSheetRows.SuppliesColumn]), Patient).Select(r => r.RowId).ToHashSet();
            Assert.IsTrue(addedRows.IsSubsetOf(finalRows), $"Semilla {seed}: se perdió un renglón agregado");
        }
    }

    // ================================================================== consulta entre equipos

    private static ConsultationDraft Payload(DateTime patientVersion, string? stamps, Guid? note = null, DateTime? noteVersion = null)
    {
        var draft = new ConsultationDraft();
        HandoffPayloadCodec.PrepareForSending(draft, Array.Empty<CatalogRef>(), Patient, patientVersion, note, null, null, T(0), stamps, noteVersion);
        return draft;
    }

    [TestMethod]
    public void AcceptingAHandoff_RequiresEveryFieldStampOfTheCopy()
    {
        var col = PatientSheetRows.SuppliesColumn;
        var sent = Stamps(T(20), ("Allergies", 10), ($"{col}/{RowA}", 20), (col, 20));
        var payload = Payload(T(20), sent);
        Assert.HasCount(3, payload.PatientFieldStamps!, "Viajan las marcas sin la de la fila");

        // Aquí la fila es "más nueva" (otro campo) pero no tiene el renglón que agregó el otro equipo
        var newerRow = Stamps(T(30), ("Allergies", 10), ("Phone", 30));
        Assert.AreEqual(HandoffPayloadCodec.Arrival.PatientBehind, HandoffPayloadCodec.CheckArrival(payload, true, T(30), newerRow, false, null));
        var complete = Stamps(T(31), ("Allergies", 10), ("Phone", 30), ($"{col}/{RowA}", 20), (col, 31));
        Assert.AreEqual(HandoffPayloadCodec.Arrival.Ready, HandoffPayloadCodec.CheckArrival(payload, true, T(31), complete, false, null));

        // Una copia de antes del paso (sin marcas): solo la marca de la fila
        var old = Payload(T(20), null);
        Assert.IsNull(old.PatientFieldStamps);
        Assert.AreEqual(HandoffPayloadCodec.Arrival.Ready, HandoffPayloadCodec.CheckArrival(old, true, T(30), newerRow, false, null));
        Assert.AreEqual(HandoffPayloadCodec.Arrival.PatientBehind, HandoffPayloadCodec.CheckArrival(old, true, T(19), complete, false, null));
        Assert.AreEqual(HandoffPayloadCodec.Arrival.PatientMissing, HandoffPayloadCodec.CheckArrival(old, false, null, null, false, null));

        // La nota: debe existir y con su última versión
        var noteId = Guid.NewGuid();
        var withNote = Payload(T(20), sent, noteId, T(25));
        Assert.AreEqual(HandoffPayloadCodec.Arrival.NoteMissing, HandoffPayloadCodec.CheckArrival(withNote, true, T(31), complete, false, null));
        Assert.AreEqual(HandoffPayloadCodec.Arrival.NoteBehind, HandoffPayloadCodec.CheckArrival(withNote, true, T(31), complete, true, T(24)));
        Assert.AreEqual(HandoffPayloadCodec.Arrival.Ready, HandoffPayloadCodec.CheckArrival(withNote, true, T(31), complete, true, T(25)));
    }
}
