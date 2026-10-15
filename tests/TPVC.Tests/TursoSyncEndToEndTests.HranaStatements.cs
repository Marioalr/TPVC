using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.13: contracts/hrana/statements.json, cada instrucción que Kotlin repite para leer la nube, literal y con sus
/// argumentos, sacada de las mismas constantes que usa la PC. La prueba también las corre en el servidor simulado, que
/// rechaza un recorrido completo de una tabla sincronizada (SCAN): SQLite usa un índice solo si el WHERE lo permite, y
/// una consulta escrita de otra forma recorrería la tabla.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private static JsonArray Texts(params string[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static JsonObject Statement(string name, string sql, JsonArray args, string when) => new()
    {
        ["name"] = name,
        ["sql"] = sql,
        ["args"] = args,
        ["when"] = when
    };

    [TestMethod]
    public async Task Hrana_ReadStatements_MatchTheCatalog_AndNoneScansASyncedTable()
    {
        var (pc, _) = await CloudWithReadFixtureAsync();
        using var db = pc.Factory.CreateDbContext();
        var schema = SyncSchema.FromModel(db.Model);

        var statements = new JsonArray
        {
            Statement("meta.schema", $"SELECT \"Value\" FROM {SyncSchema.Q(SyncSchema.MetaTable)} WHERE \"Key\" = 'schema';", new JsonArray(),
                "Antes de sincronizar: sin huella, la PC todavía no creó el esquema («Sincronice primero la PC») y Kotlin no hace nada."),
            Statement("handshake", TursoSyncService.HandshakeSql, new JsonArray(),
                "Primera instrucción de cada sincronización: la hora del servidor, la marca de agua (5 s antes) y las claves de _tpvc_meta (identidad, generación y protocolo)."),
            Statement("generation", SyncSchema.GenerationSql, new JsonArray(),
                "Última instrucción de cada petición de la subida y de cada página de la bajada: si no es la generación del equipo, se detiene sin avanzar cursores."),
            Statement("profile", TursoSyncService.ClinicProfileSql, new JsonArray(),
                "El perfil del consultorio, en la misma petición que la bajada; gana el UpdatedAtUtc más nuevo. Si cambió el hash de una imagen, se baja con asset.*.")
        };

        foreach (var table in schema.Tables.Select(t => t.Table).Append(SyncSchema.TombstonesTable))
        {
            var withOrigin = TursoSyncService.PageQuery(table, TursoSyncService.PullCursor.Start, "device", skipOwnRows: true, TursoSyncService.DefaultPullPageSize(table));
            var allRows = TursoSyncService.PageQuery(table, TursoSyncService.PullCursor.Start, "device", skipOwnRows: false, TursoSyncService.DefaultPullPageSize(table));
            var page = Statement($"page.{table}", withOrigin.Sql,
                Texts("SyncedAtUtc del cursor", "SyncedAtUtc del cursor", "SyncedAtUtc del cursor", "SyncId del cursor", "DeviceId de este equipo"),
                "Una página de la bajada. El cursor empieza en '' y '' y es (SyncedAtUtc, SyncId) de la última fila aplicada, sin pasar de la marca de agua. Una página que no se llena termina la tabla.");
            page["sqlWithoutOwnFilter"] = allRows.Sql;
            page["whenWithoutOwnFilter"] = "La primera descarga después de unirse o de un cambio de generación: también baja lo que subió este equipo (sin el último argumento).";
            page["pageSize"] = TursoSyncService.DefaultPullPageSize(table);
            statements.Add(page);
        }

        var registry = TursoSyncService.RegistrationQueries("device");
        statements.Add(Statement("heartbeat.readNonce", "SELECT InstanceNonce FROM RegisteredDevices WHERE DeviceId = ?;", Texts("DeviceId"),
            "Antes de escribir el latido, en la misma petición: si trae un InstanceNonce ajeno después de haber escrito el suyo, otra instalación usa el mismo DeviceId."));
        statements.Add(Statement("heartbeat.write", TursoSyncService.HeartbeatSql(afterPush: false),
            Texts("DeviceId", "DeviceName", "DeviceRole", "AppVersion", "Platform ('Android')", "SyncProtocol", "HandoffProtocol", "ContractRevision",
                "PendingCount", "OldestPendingUtc", "InstanceNonce"),
            "El latido (cada 60 s con la app al frente); LastSeenUtc lo pone la nube."));
        statements.Add(Statement("heartbeat.writeAfterPush", TursoSyncService.HeartbeatSql(afterPush: true),
            Texts("DeviceId", "DeviceName", "DeviceRole", "AppVersion", "Platform ('Android')", "SyncProtocol", "HandoffProtocol", "ContractRevision",
                "PendingCount", "OldestPendingUtc", "InstanceNonce"),
            "Al final de una subida que mandó algo: también LastPushUtc con la hora de la nube."));
        statements.Add(Statement("registry.own", registry[0].Sql, Texts("DeviceId"),
            "El rol, el código y la revocación de este equipo. Sin fila (todavía no aprobado por la PC) o revocado, no baja datos."));
        statements.Add(Statement("registry.primary", registry[1].Sql, new JsonArray(),
            "La PC principal, con su nombre y su último latido."));
        statements.Add(Statement("asset.meta", TursoSyncService.AssetMetaSql, Texts("Hash"),
            "Una imagen del membrete cuyo hash cambió: sin fila, todavía no está completa."));
        statements.Add(Statement("asset.chunk", TursoSyncService.AssetChunkSql, Texts("Hash", "Part"),
            "Cada trozo, cuatro por petición; se revisan el tamaño y el SHA-256."));
        statements.Add(Statement("folio.take", TursoSyncService.TakeFolioSql, Texts("Series ('EXP-2026' o 'TKT-20261009')", "Piso (integer)", "Piso (integer)"),
            "Paso 8.16: al guardar un expediente o un ticket, con 2 s de espera; el piso es el mayor número de esa serie en la base del equipo (los folios con letra no cuentan). Sin respuesta, el folio lleva la letra del equipo (golden/folios.json)."));
        // Paso 8.24: la revisión (cadencia y marca de cambios), literal: SQLite usa el índice parcial solo con este WHERE
        statements.Add(Statement("check.incoming", ConsultationHandoffService.IncomingSql, Texts("DeviceId de este equipo"),
            "Paso 8.24: la revisión, cada 4 s en pacientes y consulta con un compañero o un envío pendiente (si no, 30 s): las consultas dirigidas a este equipo, enviadas o aceptadas aquí con su copia. Con este WHERE exacto SQLite usa el índice parcial IX_ConsultationHandoffs_Pending."));
        statements.Add(Statement("check.outgoing", ConsultationHandoffService.OutgoingStateSql, Texts("Id del envío pendiente"),
            "Paso 8.24: en la misma petición de la revisión, solo si este equipo tiene un envío pendiente: su estado."));
        statements.Add(Statement("check.peerPush", ConsultationHandoffService.PeerPushSql, Texts("DeviceId de este equipo"),
            "Paso 8.24: en la misma petición de la revisión, la marca de cambios: la última subida de cada uno de los otros equipos. Si alguna es posterior a la hora de la nube de la última bajada (marca de agua + 5 s), se sincroniza; cada valor dispara a lo más una sincronización."));
        var peers = ConsultationHandoffService.PeerListQueries().ToList();
        statements.Add(Statement("check.peers.serverNow", peers[0].Sql, new JsonArray(),
            "Paso 8.24: cada 10 min, en la misma petición de la revisión, la lista de compañeros: la hora de la nube…"));
        statements.Add(Statement("check.peers.devices", peers[1].Sql, new JsonArray(),
            "…los latidos (un compañero puede recibir consultas: HandoffProtocol >= 1, visto en los últimos 30 días)…"));
        statements.Add(Statement("check.peers.registry", peers[2].Sql, new JsonArray(),
            "…y DeviceRegistry (rol, aprobado y sin quitar). Sin compañeros ni envío pendiente, la revisión va cada 30 s también en pacientes."));
        statements.Add(Statement("folio.raise", TursoSyncService.RaiseFolioSql, Texts("Series", "Número (integer)"),
            "Paso 8.16: un folio tecleado a mano mayor que el contador lo sube. La PC principal además siembra el año y el día al empezar cada sesión; la tablet y el celular no."));

        var file = new JsonObject
        {
            ["contract"] = SyncContract.Revision,
            ["protocol"] = TursoSyncService.ClientProtocol,
            ["generatedBy"] = "tests/TPVC.Tests/TursoSyncEndToEndTests.HranaStatements.cs (TPVC_WRITE_GOLDEN=1 lo regenera)",
            ["notes"] = Texts(
                "Las instrucciones de lectura que repite Kotlin, literales: escribirlas de otra forma puede hacer que SQLite recorra la tabla completa (cada fila recorrida cuenta como leída en Turso).",
                "Los argumentos van con la codificación de golden/values.json; las fechas como texto 'yyyy-MM-dd HH:mm:ss.fffffff'.",
                "Los intercambios completos están en los .jsonl de esta carpeta. Lo de escribir (upsert, lápidas y cerca de generación) está en golden/lww.json; lo de los resultados de laboratorio, en golden/lab-files.json."),
            ["statements"] = statements
        };
        GoldenVectorTests.CheckOrWrite("contracts/hrana/statements.json", SyncContract.ToStableJson(file));

        // Cada una corre en el servidor simulado, que rechaza un SCAN de una tabla sincronizada
        using var client = new TursoClient(Url, "test-token", new HttpClient(_server, disposeHandler: false));
        foreach (var statement in statements.Select(s => s!.AsObject()).Where(s => s["sql"]!.GetValue<string>().StartsWith("SELECT", StringComparison.Ordinal)))
        {
            var sql = statement["sql"]!.GetValue<string>();
            var argCount = statement["args"]!.AsArray().Count;
            var args = Enumerable.Range(0, argCount).Select(i => (object?)(i < 4 && statement["name"]!.GetValue<string>().StartsWith("page.") ? string.Empty : "x")).ToArray();
            await client.QueryAsync(sql, args);
            if (statement["sqlWithoutOwnFilter"] is { } other)
                await client.QueryAsync(other.GetValue<string>(), args.Take(4).ToArray());
        }
        Assert.IsEmpty(_server.ScanWarnings);
    }
}
