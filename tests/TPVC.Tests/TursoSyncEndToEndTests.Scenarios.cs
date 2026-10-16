using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.25, kit de conformidad de escritura: contracts/scenarios/*.json son escenarios de varios equipos sobre el servidor
/// simulado. Cada uno es una lista de pasos (editar, sincronizar en el equipo A o B, desfase o salto de reloj, corte a media
/// subida…) y, después de cada paso, las filas esperadas en la nube y en cada equipo, por SyncId, en la forma de la nube
/// (las referencias como el SyncId de su principal). Los pasos no dependen del SQL: son operaciones del equipo (las que
/// repite el motor de Kotlin) o del arnés (la red, el reloj de la nube, esconder una fila). Esta prueba los ejecuta con el
/// motor real de la PC: al comparar, corre los pasos que dice el archivo (no los de aquí) y exige el mismo resultado; con
/// TPVC_WRITE_GOLDEN=1 escribe los archivos desde las definiciones de TursoSyncEndToEndTests.ScenarioDefinitions.cs.
/// Lo que cambia en cada corrida va como marcador: &lt;time&gt; (una hora), &lt;id:N&gt; (un GUID que generó el motor; los de los
/// pasos son fijos), &lt;today&gt; y &lt;year&gt; (el día y el año del consultorio dentro de un folio) y &lt;code:B&gt; (el código del
/// equipo en un folio con letra).
/// </summary>
public partial class TursoSyncEndToEndTests
{
    /// <summary>Un escenario: su nombre (el archivo), los pasos de la PC que cubre, notas, equipos, qué se mira y sus pasos.</summary>
    private sealed record ScenarioDefinition(string Name, string[] Covers, string[] Notes, string[] Devices, JsonObject Watch, JsonArray Steps);

    private static readonly Regex ScenarioTimestamp = new(@"\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(\.\d{1,7})?", RegexOptions.Compiled);
    private static readonly Regex ScenarioStamp = new(@"^now(?:\+(\d+)([smh]))?$", RegexOptions.Compiled);

    /// <summary>Lo que se recuerda mientras corre un escenario.</summary>
    private sealed class ScenarioRun
    {
        public required Dictionary<string, Device> Devices { get; init; }
        public required JsonObject Watch { get; init; }

        /// <summary>
        /// Marcas con nombre de rawWrite ("anchor"): la primera que la usa la calcula con la hora de ese paso y las demás repiten
        /// el mismo instante (dos equipos que guardan con la misma marca).
        /// </summary>
        public Dictionary<string, DateTime> Anchors { get; } = new(StringComparer.Ordinal);

        /// <summary>GUID que generó el motor → &lt;id:N&gt;, en el orden en que aparecen (estable: ver <see cref="NumberNewIds"/>).</summary>
        public Dictionary<string, string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Backups { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// El papel de cada equipo de los escenarios: A es siempre la PC principal (el motor de la PC, también en la CI de Kotlin) y
    /// B la tablet (el motor de Kotlin; en la PC, otra computadora que hace sus pasos).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, DeviceRole> ScenarioRoles = new Dictionary<string, DeviceRole>(StringComparer.Ordinal)
    {
        ["A"] = DeviceRole.WindowsDesktop,
        ["B"] = DeviceRole.AndroidTablet
    };

    /// <summary>
    /// Si un equipo con ese papel puede hacer ese paso, con los permisos de la app (<see cref="DevicePermissions"/>): una tablet que
    /// cambia un precio no se podría reproducir en Kotlin sin SQL directo. Los pasos del arnés (sin equipo) siempre.
    /// </summary>
    private static bool ScenarioStepAllowed(DeviceRole role, JsonObject step)
    {
        var op = step["op"]!.GetValue<string>();
        string? table = step["table"]?.GetValue<string>();
        bool Write(string t) => t switch
        {
            "Patients" or "PatientConsultations" or "LabResults" => DevicePermissions.CanManageClinicalRecords(role),
            "Appointments" => DevicePermissions.CanScheduleAppointments(role),
            "Sales" or "SaleItems" => DevicePermissions.CanProcessSales(role),
            "ProductBatches" or "InventoryMovements" => DevicePermissions.CanManageInventory(role)
                                                       || op == "create" && DevicePermissions.CanRegisterStockEntries(role),
            "Categories" or "Products" or "ClinicalServices" or "ServiceDefaultProducts" => DevicePermissions.CanManageInventory(role),
            "PediatricDoseRules" or "PrescriptionTemplates" => DevicePermissions.CanConfigureSystem(role),
            _ => false
        };
        return op switch
        {
            "create" or "edit" or "delete" or "rawWrite" => Write(table!),
            "sheetRow" or "newPatient" => DevicePermissions.CanManageClinicalRecords(role),
            "sell" or "cancelSale" => DevicePermissions.CanProcessSales(role),
            "closeBatch" => DevicePermissions.CanManageInventory(role),
            // Solo la PC principal
            "fullSync" or "regularizeFolios" or "backup" or "restoreBackup" or "replaceCloud" => DevicePermissions.CanConfigureSystem(role),
            "sync" or "join" or "reapplySaved" => true,
            _ => false
        };
    }

    private static bool IsFixedId(string text) => text.StartsWith("10000000-0000-4000-8000-", StringComparison.OrdinalIgnoreCase);

    // ================================================================== ejecutar y comparar

    /// <summary>Espera extra entre pasos (la prueba de un arnés lento: el resultado no depende de lo que tarde).</summary>
    private TimeSpan _scenarioStepDelay = TimeSpan.Zero;

    private async Task RunScenarioAsync(ScenarioDefinition definition)
    {
        var relative = $"contracts/scenarios/{definition.Name}.json";
        JsonArray steps = definition.Steps;
        if (!GoldenVectorTests.WriteGolden)
        {
            // Se ejecuta lo que dice el archivo, no la definición: Kotlin solo tiene el archivo
            var path = GoldenVectorTests.RepoPath(relative);
            Assert.IsTrue(File.Exists(path), $"Falta {relative}. Genérelo con TPVC_WRITE_GOLDEN=1 dotnet test --filter TursoSyncEndToEndTests.Scenario");
            var file = JsonNode.Parse(File.ReadAllText(path))!;
            steps = new JsonArray(file["steps"]!.AsArray().Select(s => (JsonNode?)WithoutExpect(s!.AsObject())).ToArray());
            Assert.AreEqual(StepsText(definition.Steps), StepsText(steps),
                $"Los pasos de {relative} no son los de la definición: regenere con TPVC_WRITE_GOLDEN=1");
        }

        var recorded = await ExecuteScenarioAsync(definition, steps);
        GoldenVectorTests.CheckOrWrite(relative, SyncContract.ToStableJson(new JsonObject
        {
            ["scenario"] = definition.Name,
            ["covers"] = new JsonArray(definition.Covers.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["contract"] = SyncContract.Revision,
            ["protocol"] = TursoSyncService.ClientProtocol,
            ["generatedBy"] = "tests/TPVC.Tests/TursoSyncEndToEndTests.Scenarios.cs (TPVC_WRITE_GOLDEN=1 lo regenera)",
            ["notes"] = new JsonArray(definition.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["devices"] = new JsonArray(definition.Devices.Select(d => (JsonNode?)new JsonObject
            {
                ["name"] = d,
                ["deviceId"] = $"device-{d.ToLowerInvariant()}",
                ["role"] = ScenarioRoles[d].ToString(),
                ["engine"] = ScenarioRoles[d] == DeviceRole.WindowsDesktop ? "pc" : "kotlin"
            }).ToArray()),
            ["watch"] = definition.Watch.DeepClone(),
            ["steps"] = recorded
        }));
    }

    private static JsonObject WithoutExpect(JsonObject step)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in step)
        {
            if (key is not ("n" or "result" or "expect")) copy[key] = value?.DeepClone();
        }
        return copy;
    }

    private static string StepsText(JsonArray steps) => new JsonArray(steps.Select(s => (JsonNode?)WithoutExpect(s!.AsObject())).ToArray()).ToJsonString();

    private async Task<JsonArray> ExecuteScenarioAsync(ScenarioDefinition definition, JsonArray steps)
    {
        var run = new ScenarioRun
        {
            Devices = definition.Devices.ToDictionary(d => d, d => CreateDevice(d.ToLowerInvariant(), seed: false), StringComparer.Ordinal),
            Watch = definition.Watch
        };

        var recorded = new JsonArray();
        int n = 0;
        foreach (var node in steps)
        {
            var step = node!.AsObject();
            var result = await ExecuteStepAsync(run, step);
            var output = new JsonObject { ["n"] = ++n };
            foreach (var (key, value) in step) output[key] = value?.DeepClone();
            if (result != null) output["result"] = result;
            output["expect"] = await SnapshotAsync(run);
            recorded.Add(output);
            // Cada paso con su propia hora: dos equipos nunca marcan en el mismo instante por casualidad
            await Task.Delay(TimeSpan.FromMilliseconds(5) + _scenarioStepDelay);
        }

        _server.IsOffline = false;
        _server.Clock = null;
        return recorded;
    }

    // ================================================================== los pasos

    private async Task<JsonObject?> ExecuteStepAsync(ScenarioRun run, JsonObject step)
    {
        var op = step["op"]!.GetValue<string>();
        Device D() => run.Devices[step["device"]!.GetValue<string>()];
        string S(string key) => step[key]!.GetValue<string>();

        switch (op)
        {
            // ---- la red, la nube y su reloj (el arnés)
            case "network":
                _server.IsOffline = !step["online"]!.GetValue<bool>();
                return null;
            case "serverClock":
                var offset = TimeSpan.FromSeconds(step["offsetSeconds"]!.GetValue<int>());
                _server.Clock = offset == TimeSpan.Zero ? null : () => DateTime.UtcNow + offset;
                return null;
            case "cloudHold":
                HoldCloudRow(S("table"), Guid.Parse(S("syncId")));
                return null;
            case "cloudRelease":
                ReleaseHeldRow(S("table"));
                return null;

            // ---- sincronizar
            case "fullSync":
            {
                var result = await D().Sync.InitialUploadToTursoAsync();
                return SyncResult(result);
            }
            case "join":
            {
                var (ok, message) = await D().Sync.JoinCloudAsync();
                Assert.IsTrue(ok, message);
                return new JsonObject { ["ok"] = ok };
            }
            case "sync":
                return await ScenarioSyncAsync(D(), step);

            // ---- escribir en un equipo
            case "create":
                await CreateRowAsync(D(), S("table"), step["row"]!.AsObject());
                return null;
            case "edit":
                await EditRowAsync(D(), S("table"), Guid.Parse(S("syncId")), step["set"]!.AsObject());
                return null;
            case "delete":
                await DeleteRowAsync(D(), S("table"), Guid.Parse(S("syncId")));
                return null;
            case "rawWrite":
                RawWrite(run, D(), S("table"), Guid.Parse(S("syncId")), step["set"]!.AsObject(), S("stamp"), step["anchor"]?.GetValue<string>());
                return null;
            case "sheetRow":
                await SheetRowAsync(D(), Guid.Parse(S("syncId")), S("action"), step["row"]!.AsObject());
                return null;
            case "newPatient":
            {
                var patient = await D().Inventory.SavePatientAsync(new PatientRecord
                {
                    SyncId = Guid.Parse(S("syncId")), FullName = S("fullName"), Age = 30, CreatedAt = FixtureDate
                });
                return new JsonObject { ["recordNumber"] = ScenarioText(run, patient.RecordNumber) };
            }
            case "sell":
            {
                var device = D();
                var product = (await device.Inventory.GetProductsAsync()).Single(p => p.SyncId == Guid.Parse(S("productSyncId")));
                var sale = await device.Inventory.RegisterSaleAsync(new[] { (product.Id, step["quantity"]!.GetValue<int>(), product.Price) }, "Público General", "Efectivo");
                Assert.IsNotNull(sale, "La venta se registró");
                return new JsonObject { ["folio"] = ScenarioText(run, sale.Folio) };
            }
            case "cancelSale":
            {
                var device = D();
                var sale = await device.Inventory.GetSaleByFolioAsync(FromScenarioText(run, S("folio")));
                Assert.IsNotNull(sale, $"El ticket {S("folio")} existe en {step["device"]}");
                return new JsonObject { ["ok"] = await device.Inventory.CancelSaleAsync(sale.Id) };
            }
            case "closeBatch":
            {
                var device = D();
                await device.Inventory.DeleteBatchAsync(LocalId(device, "ProductBatches", Guid.Parse(S("syncId"))));
                return null;
            }

            // ---- lo que solo hace la PC (en Kotlin, el arnés lo hace con un motor de la PC o lo emula; ver contracts/README.md)
            case "regularizeFolios":
            {
                var result = await D().Sync.FolioRegularization.RunIfDueAsync();
                return new JsonObject { ["outcome"] = result.Outcome.ToString(), ["patients"] = result.Patients, ["sales"] = result.Sales };
            }
            case "backup":
                run.Backups[step["device"]!.GetValue<string>()] = new BackupService(D().DbPath).CreateNamedBackup("escenario");
                return null;
            case "restoreBackup":
                await RestoreAsync(D(), run.Backups[step["device"]!.GetValue<string>()]);
                return null;
            case "replaceCloud":
            {
                var result = await D().Sync.ReplaceCloudWithLocalAsync();
                Assert.IsTrue(result.Ok, result.Message);
                return new JsonObject { ["ok"] = result.Ok };
            }
            case "reapplySaved":
            {
                var (ok, message) = await D().Sync.ReapplySavedChangesAsync();
                Assert.IsTrue(ok, message);
                return new JsonObject { ["ok"] = ok };
            }
            default:
                throw new AssertFailedException($"Paso desconocido: {op}");
        }
    }

    private static JsonObject SyncResult(TursoSyncResult result) => new() { ["ok"] = result.Success, ["parked"] = result.ParkedRows };

    /// <summary>
    /// Una petición de la subida: lleva el upsert de una fila o la lápida (solo para contar dónde cortar; el archivo dice
    /// "la n-ésima petición de la subida").
    /// </summary>
    private static bool IsUploadRequest(string sql) =>
        sql.Contains("ON CONFLICT(\"SyncId\") DO UPDATE", StringComparison.Ordinal)
        || sql.Contains($"INSERT INTO \"{SyncSchema.TombstonesTable}\"", StringComparison.Ordinal);

    private async Task<JsonObject> ScenarioSyncAsync(Device device, JsonObject step)
    {
        if (step["pushBatchSize"] is { } size) device.Sync.PushChunkSizeOverride = size.GetValue<int>();
        if (step["cut"] is JsonObject cut)
        {
            int at = cut["uploadRequest"]!.GetValue<int>();
            int seen = 0;
            switch (cut["mode"]!.GetValue<string>())
            {
                case "beforeApply":
                    _server.BeforeExecuteAsync = sql =>
                    {
                        if (IsUploadRequest(sql) && ++seen == at) throw new HttpRequestException("Se cortó la red a media subida (escenario)");
                        return Task.CompletedTask;
                    };
                    break;
                case "lostResponse":
                    _server.DropResponseAfterExecute = sql => IsUploadRequest(sql) && ++seen == at;
                    break;
                default:
                    throw new AssertFailedException($"Corte desconocido: {cut["mode"]}");
            }
        }

        try
        {
            return SyncResult(await device.Sync.SyncAsync());
        }
        finally
        {
            _server.BeforeExecuteAsync = null;
            _server.DropResponseAfterExecute = null;
            device.Sync.PushChunkSizeOverride = null;
        }
    }

    private static TableMap ScenarioMap(AppDbContext db, string table) =>
        SyncSchema.FromModel(db.Model).Tables.SingleOrDefault(m => m.Table == table)
        ?? throw new AssertFailedException($"Tabla sin sincronizar: {table}");

    private static int LocalId(Device device, string table, Guid syncId)
    {
        using var db = device.Factory.CreateDbContext();
        return LocalId(db, table, syncId);
    }

    private static int LocalId(AppDbContext db, string table, Guid syncId)
    {
        // La tabla sale del mapa de sincronización, nunca de un texto de afuera
        var sql = $"SELECT \"Id\" AS \"Value\" FROM \"{table}\" WHERE upper(\"SyncId\") = upper({{0}})";
        return db.Database.SqlQueryRaw<int>(sql, SyncIds.Format(syncId)).AsEnumerable().Single();
    }

    /// <summary>Un valor en la forma de la nube (texto) puesto en la entidad: una columna o una referencia por SyncId.</summary>
    private static void SetRemoteValue(AppDbContext db, TableMap map, object entity, string column, JsonNode? value)
    {
        var text = value?.GetValue<string>();
        if (map.Columns.FirstOrDefault(c => c.Name == column) is { } col)
        {
            col.Property.SetValue(entity, TursoSyncService.FromRemoteValue(text, col.ClrType, col.IsNullable));
            return;
        }
        var reference = map.References.SingleOrDefault(r => r.RemoteColumn == column)
                        ?? throw new AssertFailedException($"{map.Table} no tiene la columna {column}");
        var principal = SyncSchema.FromModel(db.Model).Tables.Single(m => m.ClrType == reference.PrincipalType);
        reference.Property.SetValue(entity, text == null ? null : LocalId(db, principal.Table, Guid.Parse(text)));
    }

    private static async Task CreateRowAsync(Device device, string table, JsonObject row)
    {
        using var db = device.Factory.CreateDbContext();
        var map = ScenarioMap(db, table);
        var entity = Activator.CreateInstance(map.ClrType)!;
        ((ISyncEntity)entity).SyncId = Guid.Parse(row["SyncId"]!.GetValue<string>());
        foreach (var (column, value) in row)
        {
            if (column != "SyncId") SetRemoteValue(db, map, entity, column, value);
        }
        db.Add(entity);
        await db.SaveChangesAsync();
    }

    private static async Task EditRowAsync(Device device, string table, Guid syncId, JsonObject set)
    {
        if (table == "Patients")
        {
            // El expediente por el guardado de siempre: la copia con su foto (paso 8.21) y las marcas por campo (8.22)
            var patient = (await device.Inventory.GetPatientsAsync()).Single(p => p.SyncId == syncId);
            using (var db = device.Factory.CreateDbContext())
            {
                var map = ScenarioMap(db, table);
                foreach (var (column, value) in set) SetRemoteValue(db, map, patient, column, value);
            }
            await device.Inventory.SavePatientAsync(patient);
            return;
        }

        using (var db = device.Factory.CreateDbContext())
        {
            var map = ScenarioMap(db, table);
            var entity = db.Find(map.ClrType, LocalId(db, table, syncId))!;
            foreach (var (column, value) in set) SetRemoteValue(db, map, entity, column, value);
            await db.SaveChangesAsync();
        }
    }

    private static async Task DeleteRowAsync(Device device, string table, Guid syncId)
    {
        using var db = device.Factory.CreateDbContext();
        var map = ScenarioMap(db, table);
        db.Remove(db.Find(map.ClrType, LocalId(db, table, syncId))!);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Una escritura que no pasa por el guardado: los valores y la marca dada, sin tocar FieldStampsJson (como una PC
    /// v1.19–v1.38, un reloj que saltó o dos equipos que guardan en el mismo instante). Kotlin nunca escribe así: es del arnés.
    /// La marca es relativa a la hora de este paso ("now+90s", "now+3h"); con "anchor", la primera vez se calcula y se recuerda
    /// con ese nombre, y las siguientes con el mismo nombre repiten ese instante. Así nada depende de lo que tarde el arnés.
    /// </summary>
    private static void RawWrite(ScenarioRun run, Device device, string table, Guid syncId, JsonObject set, string stamp, string? anchor)
    {
        var match = ScenarioStamp.Match(stamp);
        Assert.IsTrue(match.Success, $"Marca desconocida: {stamp}");
        DateTime at;
        if (anchor != null && run.Anchors.TryGetValue(anchor, out var remembered))
        {
            at = remembered;
        }
        else
        {
            at = DateTime.UtcNow;
            if (match.Groups[1].Success)
            {
                var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                at += match.Groups[2].Value switch { "s" => TimeSpan.FromSeconds(amount), "m" => TimeSpan.FromMinutes(amount), _ => TimeSpan.FromHours(amount) };
            }
            if (anchor != null) run.Anchors[anchor] = at;
        }

        using var db = device.Factory.CreateDbContext();
        var args = new List<object?>();
        var assignments = new List<string>();
        foreach (var (column, value) in set)
        {
            assignments.Add($"\"{column}\" = {{{args.Count}}}");
            args.Add(value?.GetValue<string>());
        }
        assignments.Add($"\"UpdatedAtUtc\" = {{{args.Count}}}");
        args.Add(TursoSyncService.FormatDate(at));
        args.Add(SyncIds.Format(syncId));
        var sql = $"UPDATE \"{table}\" SET {string.Join(", ", assignments)} WHERE upper(\"SyncId\") = upper({{{args.Count - 1}}})";
        Assert.AreEqual(1, db.Database.ExecuteSqlRaw(sql, args.ToArray()!));
    }

    /// <summary>Agregar, quitar o cambiar un renglón de la hoja de insumos, por el guardado de siempre.</summary>
    private static async Task SheetRowAsync(Device device, Guid patientSyncId, string action, JsonObject row)
    {
        var patient = (await device.Inventory.GetPatientsAsync()).Single(p => p.SyncId == patientSyncId);
        var rowId = Guid.Parse(row["RowId"]!.GetValue<string>());
        var supplies = new List<PatientSupplyItem>(patient.ActiveSupplies);
        switch (action)
        {
            case "add":
                supplies.Add(new PatientSupplyItem
                {
                    RowId = rowId, SupplyName = row["SupplyName"]!.GetValue<string>(), Quantity = row["Quantity"]!.GetValue<int>()
                });
                break;
            case "remove":
                supplies.RemoveAll(s => s.RowId == rowId);
                break;
            case "setQuantity":
                supplies.Single(s => s.RowId == rowId).Quantity = row["Quantity"]!.GetValue<int>();
                break;
            default:
                throw new AssertFailedException($"Acción desconocida en la hoja: {action}");
        }
        patient.ActiveSupplies = supplies;
        await device.Inventory.SavePatientAsync(patient);
    }

    // ================================================================== lo esperado

    /// <summary>
    /// La nube y cada equipo después de un paso: las filas de las tablas que mira el escenario, por SyncId, con las columnas
    /// que pide en la forma de la nube; las lápidas de la nube; y en cada equipo cuántos cambios tiene sin subir (pending),
    /// apartados (parked) y guardados por un cambio de generación (saved).
    /// </summary>
    private async Task<JsonObject> SnapshotAsync(ScenarioRun run)
    {
        var places = new List<(string Place, Dictionary<string, List<Dictionary<string, string?>>> Tables)>();
        AppDbContext? model = null;
        try
        {
            model = run.Devices.Values.First().Factory.CreateDbContext();
            var maps = SyncSchema.FromModel(model.Model).Tables;
            var watched = maps.Where(m => run.Watch.ContainsKey(m.Table)).ToList();

            places.Add(("cloud", CloudRows(watched, run.Watch)));
            foreach (var (name, device) in run.Devices) places.Add((name, LocalRows(device, watched, maps, run.Watch)));

            // Los GUID que generó el motor reciben su número en un orden que no depende de la corrida
            NumberNewIds(run, places, watched);

            var expect = new JsonObject();
            foreach (var (place, tables) in places)
            {
                var state = new JsonObject();
                foreach (var map in watched)
                {
                    var rows = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
                    foreach (var row in tables[map.Table])
                    {
                        var values = new JsonObject();
                        foreach (var column in run.Watch[map.Table]!.AsArray().Select(c => c!.GetValue<string>()))
                        {
                            values[column] = row[column] is { } text ? ScenarioText(run, text) : null;
                        }
                        rows[ScenarioText(run, row["SyncId"]!)] = values;
                    }
                    var table = new JsonObject();
                    foreach (var (id, values) in rows) table[id] = values;
                    state[map.Table] = table;
                }

                if (place == "cloud")
                {
                    state["tombstones"] = new JsonArray(CloudTombstones(watched)
                        .Select(t => ScenarioText(run, t)).OrderBy(t => t, StringComparer.Ordinal)
                        .Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                }
                else
                {
                    var device = run.Devices[place];
                    state["pending"] = (await device.Sync.GetPendingChangesAsync()).Count;
                    state["parked"] = device.Sync.RefreshParkedCount();
                    state["saved"] = device.Sync.RefreshSavedChangesCount();
                }
                expect[place] = state;
            }
            return expect;
        }
        finally
        {
            model?.Dispose();
        }
    }

    private Dictionary<string, List<Dictionary<string, string?>>> CloudRows(List<TableMap> watched, JsonObject watch)
    {
        var result = new Dictionary<string, List<Dictionary<string, string?>>>();
        using var held = _server.Lock();
        foreach (var map in watched)
        {
            var rows = new List<Dictionary<string, string?>>();
            using var cmd = _server.Connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{map.Table}\"";
            try
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = new Dictionary<string, string?> { ["SyncId"] = reader["SyncId"].ToString() };
                    foreach (var column in watch[map.Table]!.AsArray().Select(c => c!.GetValue<string>()))
                    {
                        var raw = reader[column] is DBNull ? null : reader[column];
                        row[column] = map.Columns.FirstOrDefault(c => c.Name == column) is { } col
                            ? CanonicalValue(map, column, TursoSyncService.FromRemoteValue(raw, col.ClrType, col.IsNullable), row["SyncId"])
                            : raw?.ToString()?.ToLowerInvariant();
                    }
                    rows.Add(row);
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // La nube todavía no tiene la tabla (antes de la primera subida)
            }
            result[map.Table] = rows;
        }
        return result;
    }

    private List<string> CloudTombstones(List<TableMap> watched)
    {
        var types = watched.Select(m => m.ClrType.Name).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = $"SELECT EntityType, SyncId FROM \"{SyncSchema.TombstonesTable}\"";
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (types.Contains(reader.GetString(0))) result.Add($"{reader.GetString(0)}/{reader.GetString(1).ToLowerInvariant()}");
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Sin tabla de lápidas todavía
        }
        return result;
    }

    private static Dictionary<string, List<Dictionary<string, string?>>> LocalRows(Device device, List<TableMap> watched, IReadOnlyList<TableMap> maps, JsonObject watch)
    {
        using var db = device.Factory.CreateDbContext();
        var result = new Dictionary<string, List<Dictionary<string, string?>>>();
        var setMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;
        IEnumerable<object> All(Type clr) => ((IEnumerable)setMethod.MakeGenericMethod(clr).Invoke(db, null)!).Cast<object>().ToList();

        foreach (var map in watched)
        {
            var rows = new List<Dictionary<string, string?>>();
            var principals = new Dictionary<SyncReference, Dictionary<int, Guid>>();
            foreach (var reference in map.References)
            {
                var principal = maps.Single(m => m.ClrType == reference.PrincipalType).ClrType;
                principals[reference] = All(principal).ToDictionary(e => (int)principal.GetProperty("Id")!.GetValue(e)!, e => ((ISyncEntity)e).SyncId);
            }

            foreach (var entity in All(map.ClrType))
            {
                var syncId = SyncIds.Format(((ISyncEntity)entity).SyncId);
                var row = new Dictionary<string, string?> { ["SyncId"] = syncId };
                foreach (var column in watch[map.Table]!.AsArray().Select(c => c!.GetValue<string>()))
                {
                    if (map.Columns.FirstOrDefault(c => c.Name == column) is { } col)
                    {
                        row[column] = CanonicalValue(map, column, col.Property.GetValue(entity), syncId);
                        continue;
                    }
                    var reference = map.References.Single(r => r.RemoteColumn == column);
                    row[column] = reference.Property.GetValue(entity) is int id && principals[reference].TryGetValue(id, out var guid) ? SyncIds.Format(guid) : null;
                }
                rows.Add(row);
            }
            result[map.Table] = rows;
        }
        return result;
    }

    /// <summary>
    /// El valor como texto, igual en la nube y en cada equipo: decimales sin ceros de más, booleanos 1/0, enums con su número,
    /// fechas de 27 caracteres; las hojas del paciente en su forma canónica (contracts/golden/canonical-json.json) y las
    /// marcas por campo en forma canónica (las horas pasan a &lt;time&gt; después).
    /// </summary>
    private static string? CanonicalValue(TableMap map, string column, object? value, string? syncId)
    {
        var text = value switch
        {
            null => null,
            string s => s,
            bool b => b ? "1" : "0",
            decimal d => d.ToString("G29", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            DateTime dt => TursoSyncService.FormatDate(dt),
            Guid g => SyncIds.Format(g),
            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        if (text == null || map.Table != "Patients") return text;
        if (PatientSheetCanonical.IsSheet(column)) return PatientSheetCanonical.CanonicalText(column, text, Guid.Parse(syncId!));
        if (column == nameof(PatientRecord.FieldStampsJson) && text.Length > 0) return CanonicalJson.Normalize(text);
        return text;
    }

    private static readonly Regex AnyGuid = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);

    /// <summary>
    /// Numera los GUID nuevos que generó el motor (una venta, sus renglones, sus movimientos): en el orden de los lugares
    /// (nube, A, B), de las tablas (orden de subida) y de las filas por su contenido con esos GUID tapados, que es el mismo en
    /// cada corrida. Kotlin numera igual al comparar.
    /// </summary>
    private static void NumberNewIds(ScenarioRun run, List<(string Place, Dictionary<string, List<Dictionary<string, string?>>> Tables)> places, List<TableMap> watched)
    {
        string Masked(Dictionary<string, string?> row) =>
            string.Join("\u0001", row.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + (kv.Value == null ? "∅" : AnyGuid.Replace(kv.Value, m => IsFixedId(m.Value) || run.Ids.ContainsKey(m.Value) ? m.Value : "?"))));

        foreach (var (_, tables) in places)
        {
            foreach (var map in watched)
            {
                foreach (var row in tables[map.Table].OrderBy(Masked, StringComparer.Ordinal))
                {
                    var columns = new[] { "SyncId" }.Concat(run.Watch[map.Table]!.AsArray().Select(c => c!.GetValue<string>()));
                    foreach (var column in columns)
                    {
                        if (row[column] is not { } value) continue;
                        foreach (Match m in AnyGuid.Matches(value))
                        {
                            if (!IsFixedId(m.Value) && !run.Ids.ContainsKey(m.Value)) run.Ids[m.Value] = $"<id:{run.Ids.Count + 1}>";
                        }
                    }
                }
            }
        }
    }

    /// <summary>Marcadores de lo que cambia en cada corrida (ver el resumen de la clase).</summary>
    private static string ScenarioText(ScenarioRun run, string text)
    {
        text = AnyGuid.Replace(text, m => IsFixedId(m.Value) ? m.Value.ToLowerInvariant()
            : run.Ids.TryGetValue(m.Value, out var id) ? id
            : m.Value);
        text = ScenarioTimestamp.Replace(text, m => m.Value == TursoSyncService.FormatDate(FixtureDate) || m.Value == TursoSyncService.FormatDate(FixtureBirth)
            ? m.Value
            : "<time>");
        foreach (var (name, device) in run.Devices)
        {
            if (device.Settings.CurrentSettings.DeviceCode is { Length: > 0 } code) text = text.Replace($"-{code}-", $"-<code:{name}>-", StringComparison.Ordinal);
        }
        var today = ClinicClock.Today;
        return text.Replace("TKT-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture), "TKT-<today>", StringComparison.Ordinal)
            .Replace("EXP-" + today.ToString("yyyy", CultureInfo.InvariantCulture), "EXP-<year>", StringComparison.Ordinal);
    }

    /// <summary>Lo contrario, para un folio que viene en un paso.</summary>
    private static string FromScenarioText(ScenarioRun run, string text)
    {
        foreach (var (name, device) in run.Devices)
        {
            if (device.Settings.CurrentSettings.DeviceCode is { Length: > 0 } code) text = text.Replace($"-<code:{name}>-", $"-{code}-", StringComparison.Ordinal);
        }
        var today = ClinicClock.Today;
        return text.Replace("TKT-<today>", "TKT-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("EXP-<year>", "EXP-" + today.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
