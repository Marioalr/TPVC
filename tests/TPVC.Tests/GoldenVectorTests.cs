using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// El contrato de sincronización y los vectores compartidos con la app de Kotlin (paso 8.3): cada archivo de
/// <c>contracts/</c> se genera aquí desde el código que usa la PC y se compara con el que está en el repositorio.
/// Un cambio en el modelo de EF o en una regla sin regenerar los archivos hace fallar la CI. Para regenerarlos:
/// <c>TPVC_WRITE_GOLDEN=1 dotnet test --filter GoldenVectorTests</c> (y revisar la diferencia antes de subirla).
/// </summary>
[TestClass]
public partial class GoldenVectorTests
{
    internal static bool WriteGolden => Environment.GetEnvironmentVariable("TPVC_WRITE_GOLDEN") == "1";

    private const string SampleSyncId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    // ================================================================== contracts/sync/schema.json

    [TestMethod]
    public void SyncSchema_MatchesTheEfModel()
    {
        var generated = SyncContract.BuildSchemaJson(Model());
        var path = RepoPath("contracts/sync/schema.json");
        if (WriteGolden && File.Exists(path)) RequireAdditiveChangeWithNewRevision(File.ReadAllText(path), generated);
        CheckOrWrite("contracts/sync/schema.json", generated);
    }

    [TestMethod]
    public void SyncSchema_DescribesWhatTheEngineUses()
    {
        var schema = JsonNode.Parse(SyncContract.BuildSchemaJson(Model()))!;
        var tables = schema["tables"]!.AsArray().Select(t => t!["table"]!.GetValue<string>()).ToList();

        // Principales antes que sus dependientes: la subida va en este orden
        Assert.IsLessThan(tables.IndexOf("Products"), tables.IndexOf("Categories"));
        Assert.IsLessThan(tables.IndexOf("ProductBatches"), tables.IndexOf("Products"));
        Assert.IsLessThan(tables.IndexOf("PatientConsultations"), tables.IndexOf("Patients"));
        Assert.IsLessThan(tables.IndexOf("SaleItems"), tables.IndexOf("Sales"));
        Assert.IsLessThan(tables.IndexOf("LabResults"), tables.IndexOf("PatientConsultations"));
        Assert.AreEqual(14, tables.Count);

        // Toda fecha dice cómo se interpreta, y una columna obligatoria dice qué guarda si llega nula
        foreach (var column in schema["tables"]!.AsArray().SelectMany(t => t!["columns"]!.AsArray()))
        {
            var name = column!["name"]!.GetValue<string>();
            if (column["clrType"]?.GetValue<string>() == "DateTime") Assert.IsNotNull(column["timeKind"], $"{name} sin timeKind");
            if (column["role"]!.GetValue<string>() == "data" && !column["nullable"]!.GetValue<bool>()) Assert.IsNotNull(column["nullValue"], $"{name} sin nullValue");
        }

        // Los alias de Sale y SaleItem (propiedades con get y set) son columnas aparte que se escriben iguales
        var sale = schema["tables"]!.AsArray().Single(t => t!["entityType"]!.GetValue<string>() == "Sale")!;
        Assert.AreEqual("CustomerName", sale["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "PatientName")!["aliasOf"]!.GetValue<string>());
    }

    // ================================================================== contracts/golden/values.json

    private static readonly (string Name, string ClrType, string? Value)[] EncodeCases =
    {
        ("entero", "int", "42"),
        ("entero negativo", "int", "-7"),
        ("long más grande que 2^53 (viaja como texto: un número JSON perdería precisión)", "long", "9007199254740993"),
        ("booleano verdadero", "bool", "true"),
        ("booleano falso", "bool", "false"),
        ("enum MovementType.SalidaVenta", "enum MovementType", "SalidaVenta"),
        ("double", "double", "1.5"),
        ("double sin representación binaria exacta", "double", "0.1"),
        ("decimal con su escala (12.50 no es 12.5)", "decimal", "12.50"),
        ("decimal entero", "decimal", "350"),
        ("decimal negativo", "decimal", "-0.75"),
        ("decimal con 9 decimales", "decimal", "1234567.123456789"),
        ("fecha con siete decimales", "DateTime", "2026-10-10 15:04:05.1234567"),
        ("fecha sin hora (dateOnly)", "DateTime", "1990-05-01 00:00:00.0000000"),
        ("fecha mínima: el nulo de una fecha obligatoria", "DateTime", "0001-01-01 00:00:00.0000000"),
        ("GUID en mayúsculas sale en minúsculas", "Guid", "3F2504E0-4F89-11D3-9A0C-0305E82C3301"),
        ("texto con acentos, ñ y símbolos", "string", "Señora María José Peña — 50 % ✓"),
        ("texto vacío", "string", ""),
        ("BLOB: sale con relleno '=' (Turso acepta con y sin él)", "byte[]", "89504e47"),
        ("BLOB vacío", "byte[]", ""),
        ("texto nulo", "string?", null),
        ("entero opcional nulo", "int?", null),
    };

    private static readonly (string Name, string ClrType, string Hrana)[] DecodeCases =
    {
        ("entero", "int", """{"type":"integer","value":"42"}"""),
        ("entero como número JSON (lectura tolerante)", "int", """{"type":"integer","value":42}"""),
        ("long más grande que 2^53", "long", """{"type":"integer","value":"9007199254740993"}"""),
        ("booleano desde entero 1", "bool", """{"type":"integer","value":"1"}"""),
        ("booleano desde entero 0", "bool", """{"type":"integer","value":"0"}"""),
        ("enum desde su número", "enum MovementType", """{"type":"integer","value":"6"}"""),
        ("double", "double", """{"type":"float","value":1.5}"""),
        ("decimal desde texto con su escala", "decimal", """{"type":"text","value":"12.50"}"""),
        ("decimal desde float (filas viejas)", "decimal", """{"type":"float","value":0.1}"""),
        ("decimal desde entero", "decimal", """{"type":"integer","value":"350"}"""),
        ("fecha del protocolo", "DateTime", """{"type":"text","value":"2026-10-10 15:04:05.1234567"}"""),
        ("fecha con T y sin fracción", "DateTime", """{"type":"text","value":"2026-10-10T15:04:05"}"""),
        ("fecha sin hora", "DateTime", """{"type":"text","value":"2026-10-10"}"""),
        ("GUID en mayúsculas", "Guid", """{"type":"text","value":"3F2504E0-4F89-11D3-9A0C-0305E82C3301"}"""),
        ("BLOB sin relleno, como lo manda Turso (faltan dos '=')", "byte[]", """{"type":"blob","base64":"iVBORw"}"""),
        ("BLOB sin relleno (falta un '=')", "byte[]", """{"type":"blob","base64":"iVBORw0"}"""),
        ("BLOB con relleno (lectura tolerante)", "byte[]", """{"type":"blob","base64":"iVBORw=="}"""),
        ("BLOB con + y / (base64 estándar, no el de URL)", "byte[]", """{"type":"blob","base64":"++//"}"""),
        ("BLOB vacío: no es nulo", "byte[]", """{"type":"blob","base64":""}"""),
        ("nulo en texto obligatorio", "string", """{"type":"null"}"""),
        ("nulo en entero obligatorio", "int", """{"type":"null"}"""),
        ("nulo en booleano obligatorio", "bool", """{"type":"null"}"""),
        ("nulo en decimal obligatorio", "decimal", """{"type":"null"}"""),
        ("nulo en fecha obligatoria", "DateTime", """{"type":"null"}"""),
        ("nulo en BLOB obligatorio", "byte[]", """{"type":"null"}"""),
        ("nulo en texto opcional", "string?", """{"type":"null"}"""),
        ("nulo en fecha opcional", "DateTime?", """{"type":"null"}"""),
        ("nulo en BLOB opcional", "byte[]?", """{"type":"null"}"""),
    };

    [TestMethod]
    public void HranaValues_MatchTheVectors()
    {
        var cases = new JsonArray();
        foreach (var (name, clrType, value) in EncodeCases)
        {
            var encoded = TursoClient.FormatArg(TursoSyncService.ToRemoteValue(ParseInput(clrType, value)));
            cases.Add(Case($"codificar · {name}", new JsonObject { ["direction"] = "encode", ["clrType"] = clrType, ["value"] = value }, encoded));
        }
        foreach (var (name, clrType, hrana) in DecodeCases)
        {
            var (type, nullable) = ResolveType(clrType);
            var decoded = TursoSyncService.FromRemoteValue(TursoClient.ExtractValue(JsonNode.Parse(hrana)), type, nullable);
            cases.Add(Case($"decodificar · {name}", new JsonObject { ["direction"] = "decode", ["clrType"] = clrType, ["hrana"] = JsonNode.Parse(hrana) },
                Canonical(decoded) is { } text ? JsonValue.Create(text) : null));
        }

        CheckOrWrite("contracts/golden/values.json", VectorFile("hrana-values", cases,
            "encode: el valor (en 'value', como texto invariante; un BLOB en hexadecimal; null = nulo) se manda a Turso como el argumento Hrana de 'expected'. Un entero, booleano o enum como número JSON hace que Turso rechace la petición con HTTP 400.",
            "decode: el valor Hrana de una fila se guarda en una columna de 'clrType' (con '?' acepta nulo); 'expected' es su forma canónica: entero en base 10, booleano true/false, decimal con su escala, fecha yyyy-MM-dd HH:mm:ss.fffffff, GUID en minúsculas, enum por su nombre, BLOB en hexadecimal en minúsculas ('' = BLOB vacío), null = nulo.",
            "BLOB: {\"type\":\"blob\",\"base64\":…}, sin 'value'. Turso manda el base64 estándar (con + y /) sin el relleno '=' y acepta los argumentos con o sin él (libSQL, libsql-hrana/src/proto.rs). Un decodificador que exige el relleno, como Convert.FromBase64String, rechaza lo que manda Turso.",
            "Los nombres de 'clrType' son los de contracts/sync/schema.json."));
    }

    // ================================================================== contracts/golden/lww.json

    private static string T(int seconds, int extraTicks = 0) =>
        TursoSyncService.FormatDate(new DateTime(2026, 10, 10, 15, 0, 0).AddSeconds(seconds).AddTicks(extraTicks));

    [TestMethod]
    public async Task LastWriteWins_MatchTheVectors()
    {
        // Anclas que no dependen de los archivos: con marcas iguales se conserva lo que ya está
        Assert.IsFalse(SyncRules.RemoteRowWins(new DateTime(2026, 1, 1), new DateTime(2026, 1, 1)));
        Assert.IsTrue(SyncRules.RemoteTombstoneDeletes(new DateTime(2026, 1, 1), new DateTime(2026, 1, 1)));

        var cases = new JsonArray();
        foreach (var (name, local, remote) in new (string, string?, string)[]
                 {
                     ("sin fila local: se aplica", null, T(0)),
                     ("la remota es más nueva", T(0), T(1)),
                     ("empate: se conserva la local", T(0), T(0)),
                     ("la local es más nueva", T(1), T(0)),
                     ("la remota gana por un tick (100 ns)", T(0), T(0, 1)),
                 })
        {
            cases.Add(Case($"bajar fila · {name}", new JsonObject { ["check"] = "pull-row", ["localUpdatedAtUtc"] = local, ["remoteUpdatedAtUtc"] = remote },
                new JsonObject { ["apply"] = SyncRules.RemoteRowWins(ParseDate(local), ParseDate(remote)!.Value) }));
        }

        foreach (var (name, local, deleted) in new (string, string?, string)[]
                 {
                     ("sin fila local: nada que borrar", null, T(1)),
                     ("la local es anterior al borrado", T(0), T(1)),
                     ("empate: se borra", T(1), T(1)),
                     ("la local se editó después del borrado: se conserva", T(2), T(1)),
                 })
        {
            cases.Add(Case($"bajar lápida · {name}", new JsonObject { ["check"] = "pull-tombstone", ["localUpdatedAtUtc"] = local, ["deletedAtUtc"] = deleted },
                new JsonObject { ["delete"] = SyncRules.RemoteTombstoneDeletes(ParseDate(local), ParseDate(deleted)!.Value) }));
        }

        foreach (var (name, row, tombstone, incoming) in new (string, string?, string?, string)[]
                 {
                     ("fila nueva", null, null, T(1)),
                     ("más nueva que la de la nube", T(1), null, T(2)),
                     ("empate: la nube no cambia", T(1), null, T(1)),
                     ("más vieja que la de la nube", T(2), null, T(1)),
                     ("borrada con una lápida más nueva: no revive", null, T(2), T(1)),
                     ("lápida con la misma marca: no revive", null, T(1), T(1)),
                     ("editada después del borrado: revive", null, T(1), T(2)),
                 })
        {
            var (affected, rowAfter, _) = await RunInCloudAsync(row, tombstone, "upsert", incoming);
            cases.Add(Case($"subir fila (nube) · {name}",
                new JsonObject { ["check"] = "server-upsert", ["serverUpdatedAtUtc"] = row, ["serverDeletedAtUtc"] = tombstone, ["incomingUpdatedAtUtc"] = incoming },
                new JsonObject { ["affectedRows"] = affected, ["serverUpdatedAtUtcAfter"] = rowAfter }));
        }

        foreach (var (name, row, tombstone, deleted) in new (string, string?, string?, string)[]
                 {
                     ("borra la fila anterior al borrado", T(1), null, T(2)),
                     ("empate: borra la fila", T(1), null, T(1)),
                     ("la fila se editó después del borrado: se conserva", T(2), null, T(1)),
                     ("la nube ya tenía un borrado más nuevo: se queda ese", null, T(3), T(2)),
                 })
        {
            var (_, rowAfter, tombstoneAfter) = await RunInCloudAsync(row, tombstone, "tombstone", deleted);
            cases.Add(Case($"subir lápida (nube) · {name}",
                new JsonObject { ["check"] = "server-tombstone", ["serverUpdatedAtUtc"] = row, ["serverDeletedAtUtc"] = tombstone, ["incomingDeletedAtUtc"] = deleted },
                new JsonObject { ["serverUpdatedAtUtcAfter"] = rowAfter, ["serverDeletedAtUtcAfter"] = tombstoneAfter }));
        }

        // Cerca de generación (paso 8.9): con otra generación en la nube, nada de lo que manda el equipo se escribe
        foreach (var (name, action, row, cloudGeneration, deviceGeneration) in new (string, string, string?, string?, string?)[]
                 {
                     ("subir fila · misma generación: se escribe", "upsert", null, "gen-a", "gen-a"),
                     ("subir fila · la nube sin generación y el equipo tampoco: se escribe", "upsert", null, null, null),
                     ("subir fila · otra generación: no se escribe", "upsert", null, "gen-nueva", "gen-vieja"),
                     ("subir fila · el equipo sin generación y la nube con una: no se escribe", "upsert", null, "gen-nueva", null),
                     ("subir lápida · otra generación: ni lápida ni borrado", "tombstone", T(1), "gen-nueva", "gen-vieja"),
                 })
        {
            var stamp = T(2);
            var (affected, rowAfter, tombstoneAfter) = await RunInCloudAsync(row, null, action, stamp, cloudGeneration, deviceGeneration);
            cases.Add(Case($"cerca de generación (nube) · {name}",
                new JsonObject
                {
                    ["check"] = action == "upsert" ? "server-upsert" : "server-tombstone", ["serverUpdatedAtUtc"] = row,
                    [action == "upsert" ? "incomingUpdatedAtUtc" : "incomingDeletedAtUtc"] = stamp,
                    ["cloudGeneration"] = cloudGeneration, ["deviceGeneration"] = deviceGeneration
                },
                new JsonObject { ["affectedRows"] = affected, ["serverUpdatedAtUtcAfter"] = rowAfter, ["serverDeletedAtUtcAfter"] = tombstoneAfter }));
        }

        foreach (var (name, rowExists, tombstoneExists) in new (string, bool, bool)[]
                 {
                     ("la nube tiene la fila (más nueva o con la misma marca): se aplica aunque empate", true, false),
                     ("la relectura encuentra lápida: se borra aquí", false, true),
                     ("la fila revivió después de su lápida: se aplica la fila", true, true),
                     ("ni fila ni lápida: se reintenta", false, false),
                 })
        {
            cases.Add(Case($"subida rechazada (desde 8.6) · {name}",
                new JsonObject { ["check"] = "rejected-upsert", ["serverRowExists"] = rowExists, ["serverTombstoneExists"] = tombstoneExists },
                new JsonObject { ["resolution"] = SyncRules.AfterRejectedUpsert(rowExists, tombstoneExists).ToString() }));
        }

        foreach (var (name, local, server) in new (string, string?, string)[]
                 {
                     ("empate: se aplica la de la nube", T(1), T(1)),
                     ("la de la nube es más nueva", T(1), T(2)),
                     ("la local se volvió a editar después de subir: se conserva", T(2), T(1)),
                     ("ya no existe aquí: se aplica", null, T(1)),
                 })
        {
            cases.Add(Case($"subida rechazada, aplicar la fila de la nube · {name}",
                new JsonObject { ["check"] = "rejected-upsert-row", ["localUpdatedAtUtc"] = local, ["serverUpdatedAtUtc"] = server },
                new JsonObject { ["apply"] = SyncRules.ServerRowAppliesAfterReject(ParseDate(local), ParseDate(server)!.Value) }));
        }

        CheckOrWrite("contracts/golden/lww.json", VectorFile("lww", cases,
            "Marcas en UTC con el formato del protocolo (yyyy-MM-dd HH:mm:ss.fffffff); null = no existe.",
            "pull-row y pull-tombstone: lo que decide el equipo al bajar (SyncRules.RemoteRowWins y RemoteTombstoneDeletes).",
            "server-upsert y server-tombstone: lo que hace la nube con el SQL que manda el equipo (TableMap.UpsertSql, SyncSchema.TombstoneUpsertSql y DeleteTombstonedRowSql), ejecutado de verdad sobre SQLite. 'affectedRows' es lo que Turso regresa en affected_row_count.",
            "Cerca de generación (paso 8.9): el último argumento de las tres instrucciones es la generación del equipo ('deviceGeneration'; null si la nube todavía no tenía). Sin 'cloudGeneration' la nube no tiene la clave. Si no coincide con _tpvc_meta.generation, la nube no escribe nada (0 filas) y el equipo, al leer la generación al final de la petición, se detiene.",
            "rejected-upsert: si un upsert afecta 0 filas, el equipo vuelve a leer la fila y la lápida de la nube (SyncRules.AfterRejectedUpsert). La PC lo hace desde el paso 8.6; la app de Kotlin desde su primera versión que escribe.",
            "rejected-upsert-row: con la fila de la nube en mano, se aplica si su marca es igual o más nueva que la local (SyncRules.ServerRowAppliesAfterReject). No se relee en la primera subida completa, donde casi todo afecta 0 filas."));
    }

    /// <summary>Estado inicial de la nube para una fila de Categories, y la instrucción real que manda la PC.</summary>
    /// <param name="cloudGeneration">_tpvc_meta.generation de la nube (null = no existe).</param>
    /// <param name="deviceGeneration">La generación que manda el equipo en la cerca (paso 8.9).</param>
    private static async Task<(int Affected, string? RowAfter, string? TombstoneAfter)> RunInCloudAsync(string? row, string? tombstone, string action, string stamp,
        string? cloudGeneration = null, string? deviceGeneration = null)
    {
        var schema = SyncSchema.FromModel(Model());
        var map = schema.Tables.Single(t => t.ClrType == typeof(Category));
        using var server = new FakeHranaServer(":memory:");
        using var client = new TursoClient("https://tpvc-golden.turso.io", "token", new HttpClient(server, disposeHandler: false));

        await client.ExecuteBatchAsync(new[] { SyncSchema.MetaStatement }.Concat(SyncSchema.TableStatements(map)).Concat(SyncSchema.TombstoneStatements)
            .Select(s => (s, (object?[]?)null)));
        if (cloudGeneration != null)
            await client.ExecuteAsync("INSERT INTO \"_tpvc_meta\" (\"Key\", \"Value\") VALUES ('generation', ?)", new object?[] { cloudGeneration });
        if (row != null)
            await client.ExecuteAsync($"INSERT INTO \"{map.Table}\" (\"SyncId\", \"UpdatedAtUtc\", \"SyncedAtUtc\") VALUES (?, ?, ?)", new object?[] { SampleSyncId, row, row });
        if (tombstone != null)
            await client.ExecuteAsync("INSERT INTO \"SyncTombstones\" (\"EntityType\", \"SyncId\", \"DeletedAtUtc\", \"SyncedAtUtc\") VALUES (?, ?, ?, ?)",
                new object?[] { nameof(Category), SampleSyncId, tombstone, tombstone });

        int affected;
        if (action == "upsert")
        {
            var args = new List<object?> { SampleSyncId, stamp, "device-golden" };
            args.AddRange(map.RemoteColumnNames.Select(_ => (object?)null));
            args.AddRange(new object?[] { nameof(Category), SampleSyncId, stamp, deviceGeneration });
            affected = await client.ExecuteAsync(map.UpsertSql, args.ToArray());
        }
        else
        {
            affected = await client.ExecuteAsync(SyncSchema.TombstoneUpsertSql, new object?[] { nameof(Category), SampleSyncId, stamp, "device-golden", deviceGeneration });
            await client.ExecuteAsync(SyncSchema.DeleteTombstonedRowSql(map.Table), new object?[] { SampleSyncId, stamp, deviceGeneration });
        }

        var rowAfter = (await client.QueryAsync($"SELECT \"UpdatedAtUtc\" FROM \"{map.Table}\" WHERE \"SyncId\" = ?", new object?[] { SampleSyncId }))
            .FirstOrDefault()?["UpdatedAtUtc"]?.ToString();
        var tombstoneAfter = (await client.QueryAsync("SELECT \"DeletedAtUtc\" FROM \"SyncTombstones\" WHERE \"EntityType\" = 'Category' AND \"SyncId\" = ?", new object?[] { SampleSyncId }))
            .FirstOrDefault()?["DeletedAtUtc"]?.ToString();
        return (affected, rowAfter, tombstoneAfter);
    }

    // ================================================================== contracts/golden/stamping.json

    [TestMethod]
    public void LocalStamps_MatchTheVectors()
    {
        var now = new DateTime(2026, 10, 10, 15, 0, 0).AddTicks(1234567);
        Assert.AreEqual(now.AddMilliseconds(1), SyncRules.NextLocalStamp(now, now, TimeSpan.Zero), "Con la marca igual a la hora, un milisegundo después");

        var cases = new JsonArray();
        foreach (var (name, stored, offsetMs) in new (string, DateTime?, int)[]
                 {
                     ("alta: sin marca guardada", null, 0),
                     ("edición: la marca guardada es anterior", now.AddSeconds(-1), 0),
                     ("la marca guardada es igual a la hora", now, 0),
                     ("la marca guardada está en el futuro (vino de un equipo con el reloj adelantado)", now.AddMinutes(5), 0),
                     ("la marca guardada es 1 ms posterior", now.AddMilliseconds(1), 0),
                     ("con desfase de +2 min con la nube (paso 8.7)", now, 120_000),
                     ("con desfase negativo que deja atrás la marca guardada", now.AddMinutes(-1), -30_000),
                     ("con desfase negativo que no alcanza la marca guardada", now, -60_000),
                 })
        {
            var stamp = SyncRules.NextLocalStamp(now, stored, TimeSpan.FromMilliseconds(offsetMs));
            cases.Add(Case(name,
                new JsonObject
                {
                    ["nowUtc"] = TursoSyncService.FormatDate(now),
                    ["storedUpdatedAtUtc"] = stored is { } s ? TursoSyncService.FormatDate(s) : null,
                    ["clockOffsetMs"] = offsetMs
                },
                TursoSyncService.FormatDate(stamp)));
        }

        CheckOrWrite("contracts/golden/stamping.json", VectorFile("local-stamp", cases,
            "Marca de un cambio local (alta, edición o borrado): max(nowUtc + clockOffsetMs, storedUpdatedAtUtc + 1 ms). Un borrado usa la marca guardada de la fila que se borra.",
            "clockOffsetMs es el desfase con la hora de la nube (paso 8.7); hasta entonces la PC usa 0.",
            "Todas las marcas en UTC con el formato del protocolo (yyyy-MM-dd HH:mm:ss.fffffff)."));
    }

    [TestMethod]
    public void TombstoneTriggerStamp_FollowsTheSameVectors()
    {
        // La lápida que pone el trigger de borrado (paso 8.5) aplica la misma regla, escrita en SQL de SQLite
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        string? Stamp(string? stored, string now, long offsetMs)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT " + SyncChangeLog.StampSql("$stored", "$now", "$offset");
            cmd.Parameters.AddWithValue("$stored", (object?)stored ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$offset", offsetMs);
            return cmd.ExecuteScalar() as string;
        }

        var vectors = JsonNode.Parse(File.ReadAllText(RepoPath("contracts/golden/stamping.json")))!;
        foreach (var c in vectors["cases"]!.AsArray())
        {
            var input = c!["input"]!;
            Assert.AreEqual(c["expected"]!.GetValue<string>(),
                Stamp(input["storedUpdatedAtUtc"]?.GetValue<string>(), input["nowUtc"]!.GetValue<string>(), input["clockOffsetMs"]!.GetValue<long>()),
                c["name"]!.GetValue<string>());
        }

        // Las marcas como las guarda EF Core (sin ceros a la derecha) y el cambio de segundo, de día y de año
        const string now = "2026-10-10 15:00:00.1234567";
        foreach (var stored in new[] { "2026-10-10 15:05:00.12", "2026-10-10 15:05:00", "2026-10-10 15:05:00.999", "2026-12-31 23:59:59.9999999" })
        {
            var expected = TursoSyncService.FormatDate(SyncRules.NextLocalStamp(
                TursoSyncService.ParseDate(now), TursoSyncService.ParseDate(stored), TimeSpan.Zero));
            Assert.AreEqual(expected, Stamp(stored, now, 0), stored);
        }
    }

    // ================================================================== contracts/golden/uuid5.json

    [TestMethod]
    public void Uuid5_MatchesRfc4122_AndTheVectors()
    {
        // RFC 4122: el ejemplo de Python, uuid.uuid5(uuid.NAMESPACE_DNS, "python.org")
        Assert.AreEqual(new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"), SyncIds.Uuid5(SyncIds.DnsNamespace, "python.org"));

        var namespaces = new Dictionary<string, Guid> { ["dns"] = SyncIds.DnsNamespace, ["url"] = SyncIds.UrlNamespace, ["tpvc"] = SyncIds.TpvcNamespace };
        var cases = new JsonArray();
        foreach (var (name, ns, value) in new (string, string, string)[]
                 {
                     ("RFC 4122 · DNS · python.org", "dns", "python.org"),
                     ("el espacio de nombres de TPVC", "url", "https://github.com/Marioalr/TPVC/sync"),
                     ("TPVC · categoría con acentos (UTF-8)", "tpvc", "Category:Analgésicos y antipiréticos"),
                     ("TPVC · servicio por código", "tpvc", "ClinicalService:SERV-001"),
                     ("TPVC · categoría sembrada", "tpvc", "Category:Material de Curación"),
                     ("TPVC · insumo por defecto de un servicio sembrado", "tpvc", "ServiceDefaultProduct:SERV-002|CUR-001"),
                     ("TPVC · nombre vacío", "tpvc", ""),
                 })
        {
            cases.Add(Case(name, new JsonObject { ["namespace"] = ns, ["namespaceId"] = SyncIds.Format(namespaces[ns]), ["name"] = value },
                SyncIds.Format(SyncIds.Uuid5(namespaces[ns], value))));
        }

        // El catálogo que siembra una base nueva (paso 8.8) usa estos mismos nombres
        Assert.AreEqual(SyncIds.Uuid5(SyncIds.TpvcNamespace, "Category:Material de Curación"), SyncIds.Seeded("Category", "Material de Curación"));
        Assert.AreEqual(SyncIds.Uuid5(SyncIds.TpvcNamespace, "ServiceDefaultProduct:SERV-002|CUR-001"),
            SyncIds.Seeded("ServiceDefaultProduct", "SERV-002|CUR-001"));

        const string other = "7C9E6679-7425-40DE-944B-E07FC1F90AE7";
        foreach (var (name, prefix, ids) in new (string, string, string[])[]
                 {
                     ("SyncId en minúsculas", "return:", new[] { SampleSyncId }),
                     ("el mismo SyncId en mayúsculas da lo mismo", "return:", new[] { SampleSyncId.ToUpperInvariant() }),
                     ("dos SyncId (uno en mayúsculas)", "return:", new[] { SampleSyncId, other }),
                     ("saldo inicial", "opening:v1:", new[] { other }),
                 })
        {
            var derived = SyncIds.Derived(prefix, ids.Select(Guid.Parse).ToArray());
            cases.Add(Case($"derivado · {name}",
                new JsonObject { ["namespace"] = "tpvc", ["prefix"] = prefix, ["syncIds"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) },
                SyncIds.Format(derived)));
        }

        // Paso 8.19: las devoluciones al cancelar un ticket, con las funciones que usa la PC (StockLedger)
        const string exitMovement = "5B6C1F0E-2A44-4D7B-9E31-8F0A6C2D9B47";
        const string sale = "a1d3c5e7-0b2f-4c6e-8a9d-1e3f5a7c9b0d";
        const string product = "c0ffee00-1234-4abc-9def-0123456789ab";
        var newRule = StockLedger.ReturnSyncId(Guid.Parse(exitMovement));
        var oldRule = StockLedger.LegacyReturnSyncId(Guid.Parse(sale), Guid.Parse(product));
        Assert.AreEqual(SyncIds.Derived("return:", Guid.Parse(exitMovement)), newRule);
        Assert.AreEqual(SyncIds.Derived("return:", Guid.Parse(sale), Guid.Parse(product)), oldRule);
        cases.Add(Case("devolución · venta con LedgerVersion 1: return:<SyncId de la salida> (en mayúsculas da lo mismo)",
            new JsonObject { ["namespace"] = "tpvc", ["prefix"] = StockLedger.ReturnPrefix, ["syncIds"] = new JsonArray(exitMovement) }, SyncIds.Format(newRule)));
        cases.Add(Case("devolución · venta con LedgerVersion 0: return:<SyncId de la venta>:<SyncId del producto>",
            new JsonObject { ["namespace"] = "tpvc", ["prefix"] = StockLedger.ReturnPrefix, ["syncIds"] = new JsonArray(sale, product) }, SyncIds.Format(oldRule)));

        // Paso 8.19: los dos movimientos que cierran un lote
        const string batch = "e8b1d2c4-6f7a-4b3c-9d0e-1f2a3b4c5d6e";
        Assert.AreEqual(SyncIds.Derived("close:", Guid.Parse(batch)), StockLedger.CloseSyncId(Guid.Parse(batch)));
        Assert.AreEqual(SyncIds.Derived("close-rest:", Guid.Parse(batch)), StockLedger.CloseRestSyncId(Guid.Parse(batch)));
        cases.Add(Case("cerrar un lote · la parte con su lote: close:<SyncId del lote>",
            new JsonObject { ["namespace"] = "tpvc", ["prefix"] = StockLedger.ClosePrefix, ["syncIds"] = new JsonArray(batch) },
            SyncIds.Format(StockLedger.CloseSyncId(Guid.Parse(batch)))));
        cases.Add(Case("cerrar un lote · la parte sin lote: close-rest:<SyncId del lote>",
            new JsonObject { ["namespace"] = "tpvc", ["prefix"] = StockLedger.CloseRestPrefix, ["syncIds"] = new JsonArray(batch) },
            SyncIds.Format(StockLedger.CloseRestSyncId(Guid.Parse(batch)))));

        CheckOrWrite("contracts/golden/uuid5.json", VectorFile("uuid5", cases,
            "UUID versión 5 de RFC 4122 (SHA-1 del espacio de nombres en orden de red + el nombre en UTF-8). 'expected' en minúsculas con guiones.",
            "Espacios de nombres: dns y url de RFC 4122; tpvc = uuid5(url, \"https://github.com/Marioalr/TPVC/sync\"), fijo para siempre.",
            "Catálogo sembrado por la PC (paso 8.8; Kotlin nunca siembra): 'Category:<nombre>', 'ClinicalService:<código>', 'ServiceDefaultProduct:<código del servicio>|<código del producto>'.",
            "Derivados: nombre = prefix + los syncIds en minúsculas unidos con ':' (un SyncId leído en mayúsculas da lo mismo).",
            "Devoluciones al cancelar un ticket (paso 8.19, regla «Existencias» de schema.json): con Sales.LedgerVersion 1, una por salida con 'return:' + SyncId de la salida; con 0 (venta anterior), una por producto con 'return:' + SyncId de la venta + ':' + SyncId del producto. Dos equipos que cancelan el mismo ticket escriben la misma fila.",
            "Cerrar un lote (paso 8.19): la parte con su lote con 'close:' + SyncId del lote y la parte sin lote con 'close-rest:' + SyncId del lote; dos equipos que cierran el mismo lote escriben las mismas filas."));
    }

    // ================================================================== utilidades

    private static Microsoft.EntityFrameworkCore.Metadata.IModel Model()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, "Data Source=:memory:");
        using var db = new AppDbContext(builder.Options);
        return db.Model;
    }

    private static JsonObject Case(string name, JsonObject input, JsonNode? expected) =>
        new() { ["name"] = name, ["input"] = input, ["expected"] = expected };

    private static string VectorFile(string rule, JsonArray cases, params string[] notes) =>
        SyncContract.ToStableJson(new JsonObject
        {
            ["rule"] = rule,
            ["culture"] = "es-MX",
            ["today"] = null,
            ["now"] = null,
            ["contract"] = SyncContract.Revision,
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["cases"] = cases
        });

    private static (Type Type, bool Nullable) ResolveType(string clrType)
    {
        bool nullable = clrType.EndsWith('?');
        var name = clrType.TrimEnd('?');
        Type type = name switch
        {
            "int" => typeof(int),
            "long" => typeof(long),
            "bool" => typeof(bool),
            "double" => typeof(double),
            "decimal" => typeof(decimal),
            "DateTime" => typeof(DateTime),
            "Guid" => typeof(Guid),
            "string" => typeof(string),
            "byte[]" => typeof(byte[]),
            "enum MovementType" => typeof(MovementType),
            _ => throw new ArgumentException($"Tipo sin resolver: {clrType}")
        };
        return (nullable && type.IsValueType ? typeof(Nullable<>).MakeGenericType(type) : type, nullable);
    }

    private static object? ParseInput(string clrType, string? text)
    {
        if (text == null) return null;
        var (type, _) = ResolveType(clrType);
        type = Nullable.GetUnderlyingType(type) ?? type;
        var c = CultureInfo.InvariantCulture;
        return type switch
        {
            _ when type == typeof(int) => int.Parse(text, c),
            _ when type == typeof(long) => long.Parse(text, c),
            _ when type == typeof(bool) => bool.Parse(text),
            _ when type == typeof(double) => double.Parse(text, c),
            _ when type == typeof(decimal) => decimal.Parse(text, c),
            _ when type == typeof(DateTime) => DateTime.ParseExact(text, TursoSyncFormats.DateTimeFormat, c),
            _ when type == typeof(Guid) => Guid.Parse(text),
            _ when type == typeof(byte[]) => Convert.FromHexString(text),
            _ when type.IsEnum => Enum.Parse(type, text),
            _ => text
        };
    }

    private static string? Canonical(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => TursoSyncService.FormatDate(d),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        Guid g => SyncIds.Format(g),
        byte[] bytes => Convert.ToHexStringLower(bytes),
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static DateTime? ParseDate(string? text) => text == null ? null : TursoSyncService.ParseDate(text);

    internal static string RepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "TPVC.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.IsNotNull(dir, "No se encontró la carpeta del proyecto (TPVC.sln)");
        return Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static void CheckOrWrite(string relative, string generated)
    {
        var path = RepoPath(relative);
        if (WriteGolden)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        Assert.IsTrue(File.Exists(path), $"Falta {relative}. Genérelo con TPVC_WRITE_GOLDEN=1 dotnet test --filter GoldenVectorTests");
        var current = File.ReadAllText(path).Replace("\r\n", "\n");
        if (current == generated) return;

        var a = current.Split('\n');
        var b = generated.Split('\n');
        int line = 0;
        while (line < a.Length && line < b.Length && a[line] == b[line]) line++;
        Assert.Fail($"{relative} ya no coincide con el código (línea {line + 1}).\n  archivo: {(line < a.Length ? a[line].Trim() : "(fin)")}\n" +
                    $"  código:  {(line < b.Length ? b[line].Trim() : "(fin)")}\n" +
                    "Si el cambio es a propósito, regenere con TPVC_WRITE_GOLDEN=1 dotnet test --filter GoldenVectorTests y revise la diferencia: la app de Kotlin lee estos archivos.");
    }

    /// <summary>
    /// Al regenerar schema.json: los cambios de tablas o columnas solo pueden agregar (la app de Kotlin y las PC viejas siguen
    /// leyendo lo que ya existía) y obligan a subir <see cref="SyncContract.Revision"/>. Desde el paso 8.23 la revisión 13
    /// (<see cref="SyncContract.FrozenRevision"/>) es la base de la tablet, y "aditivo" se revisa columna por columna
    /// (<see cref="NonAdditiveChanges"/>).
    /// </summary>
    private static void RequireAdditiveChangeWithNewRevision(string existing, string generated)
    {
        var problems = NonAdditiveChanges(existing, generated);
        if (problems.Count > 0)
        {
            Assert.Fail($"El contrato está congelado desde la revisión {SyncContract.FrozenRevision} (base de la tablet): solo se admiten cambios aditivos.\n  " +
                        string.Join("\n  ", problems));
        }

        var before = JsonNode.Parse(existing)!.AsObject();
        var after = JsonNode.Parse(generated)!.AsObject();
        static string Core(JsonObject o)
        {
            var copy = (JsonObject)o.DeepClone();
            foreach (var key in new[] { "contract", "fingerprint", "generatedBy", "rules" }) copy.Remove(key);
            return copy.ToJsonString();
        }

        if (Core(before) != Core(after) && before["contract"]!.GetValue<int>() == after["contract"]!.GetValue<int>())
        {
            Assert.Fail($"Cambiaron las tablas del contrato: suba SyncContract.Revision (hoy {SyncContract.Revision}) y vuelva a generar.");
        }
    }

    /// <summary>
    /// Paso 8.23, formato congelado: lo que <paramref name="generated"/> cambia de <paramref name="existing"/> sin ser aditivo.
    /// Se admite: tablas, columnas y referencias nuevas (en la nube todas las columnas aceptan nulo), valores nuevos de un enum,
    /// una columna que pasa a derivada (un equipo anterior la sigue aplicando como valor, que es lo que hacía; el paso 8.20 lo
    /// hace con las existencias), un índice local que deja de ser único y otro orden de subida. No se admite: quitar o
    /// renombrar una tabla, columna o referencia; cambiar en una columna su tipo remoto o de C#, si acepta nulo, su timeKind,
    /// su papel, su alias o el número de un valor de su enum, o quitarle uno; que una derivada deje de serlo; cambiar el
    /// principal de una referencia o si es obligatoria; ni agregar un índice único a una tabla que ya existía (un equipo
    /// anterior pudo guardar filas repetidas).
    /// </summary>
    private static List<string> NonAdditiveChanges(string existing, string generated)
    {
        var problems = new List<string>();
        var before = JsonNode.Parse(existing)!["tables"]!.AsArray();
        var after = JsonNode.Parse(generated)!["tables"]!.AsArray().ToDictionary(t => t!["table"]!.GetValue<string>(), t => t!.AsObject());

        static string? Text(JsonNode? node) => node?.ToJsonString();

        foreach (var oldTable in before.Select(t => t!.AsObject()))
        {
            var name = oldTable["table"]!.GetValue<string>();
            if (!after.TryGetValue(name, out var newTable))
            {
                problems.Add($"La tabla {name} desapareció.");
                continue;
            }
            if (Text(oldTable["entityType"]) != Text(newTable["entityType"])) problems.Add($"La tabla {name} cambió de entityType.");

            var newColumns = newTable["columns"]!.AsArray().ToDictionary(c => c!["name"]!.GetValue<string>(), c => c!.AsObject());
            foreach (var oldColumn in oldTable["columns"]!.AsArray().Select(c => c!.AsObject()))
            {
                var column = oldColumn["name"]!.GetValue<string>();
                if (!newColumns.TryGetValue(column, out var newColumn))
                {
                    problems.Add($"La columna {name}.{column} desapareció.");
                    continue;
                }
                foreach (var key in new[] { "role", "remoteType", "clrType", "nullable", "timeKind", "aliasOf", "enumType" })
                {
                    if (Text(oldColumn[key]) != Text(newColumn[key]))
                        problems.Add($"La columna {name}.{column} cambió '{key}' ({Text(oldColumn[key]) ?? "nada"} → {Text(newColumn[key]) ?? "nada"}).");
                }
                if (oldColumn["derived"]?.GetValue<bool>() == true && newColumn["derived"]?.GetValue<bool>() != true)
                    problems.Add($"La columna {name}.{column} dejó de ser derivada.");
                if (oldColumn["enumValues"] is JsonObject oldValues)
                {
                    var newValues = newColumn["enumValues"] as JsonObject;
                    foreach (var (value, number) in oldValues)
                    {
                        if (newValues == null || !newValues.TryGetPropertyValue(value, out var newNumber))
                            problems.Add($"El valor {value} del enum de {name}.{column} desapareció.");
                        else if (Text(newNumber) != Text(number))
                            problems.Add($"El valor {value} del enum de {name}.{column} cambió de número ({Text(number)} → {Text(newNumber)}).");
                    }
                }
            }

            var newReferences = newTable["references"]!.AsArray().ToDictionary(r => r!["column"]!.GetValue<string>(), r => r!.AsObject());
            foreach (var oldReference in oldTable["references"]!.AsArray().Select(r => r!.AsObject()))
            {
                var column = oldReference["column"]!.GetValue<string>();
                if (!newReferences.TryGetValue(column, out var newReference))
                {
                    problems.Add($"La referencia {name}.{column} desapareció.");
                    continue;
                }
                foreach (var key in new[] { "principal", "required" })
                {
                    if (Text(oldReference[key]) != Text(newReference[key]))
                        problems.Add($"La referencia {name}.{column} cambió '{key}' ({Text(oldReference[key])} → {Text(newReference[key])}).");
                }
            }

            static string UniqueKey(JsonNode? u) => string.Join(",", u!.AsArray().Select(c => c!.GetValue<string>()).Order(StringComparer.Ordinal));
            var oldUnique = oldTable["unique"]!.AsArray().Select(UniqueKey).ToHashSet(StringComparer.Ordinal);
            foreach (var unique in newTable["unique"]!.AsArray().Select(UniqueKey).Where(u => !oldUnique.Contains(u)))
            {
                problems.Add($"La tabla {name} ganó un índice único ({unique}).");
            }
        }
        return problems;
    }
}
