using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Servidor Hrana v2 (/v2/pipeline) simulado sobre SQLite. Valida la codificación de valores
/// igual que Turso: un entero enviado como número JSON provoca HTTP 400 Bad Request.
/// </summary>
internal sealed class FakeHranaServer : HttpMessageHandler
{
    private readonly SqliteConnection _connection;
    public int RequestCount { get; private set; }
    public int StatementCount { get; private set; }

    public FakeHranaServer(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _connection.Open();
    }

    public SqliteConnection Connection => _connection;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var results = new JsonArray();

        foreach (var req in body["requests"]!.AsArray())
        {
            var type = req!["type"]!.GetValue<string>();
            if (type == "close")
            {
                results.Add(new JsonObject { ["type"] = "ok", ["response"] = new JsonObject { ["type"] = "close" } });
                continue;
            }

            var stmt = req["stmt"]!;
            var sql = stmt["sql"]!.GetValue<string>();
            var args = stmt["args"]?.AsArray() ?? new JsonArray();

            // Validación estricta del protocolo (como el servidor real)
            foreach (var arg in args)
            {
                var argType = arg!["type"]!.GetValue<string>();
                if (argType == "integer" && arg["value"] is JsonValue v && v.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("Failed to deserialize the JSON body: invalid type: integer, expected a string")
                    };
                }
            }

            StatementCount++;
            try
            {
                results.Add(new JsonObject { ["type"] = "ok", ["response"] = new JsonObject { ["type"] = "execute", ["result"] = Execute(sql, args) } });
            }
            catch (SqliteException ex)
            {
                results.Add(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = ex.Message, ["code"] = "SQLITE_ERROR" } });
            }
        }

        var payload = new JsonObject { ["baton"] = null, ["results"] = results };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private JsonObject Execute(string sql, JsonArray args)
    {
        using var cmd = _connection.CreateCommand();
        var sb = new StringBuilder();
        int index = 0;
        bool inString = false;
        foreach (var c in sql)
        {
            if (c == '\'') inString = !inString;
            if (c == '?' && !inString)
            {
                index++;
                sb.Append("$p").Append(index);
            }
            else
            {
                sb.Append(c);
            }
        }
        cmd.CommandText = sb.ToString();

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i]!;
            object value = arg["type"]!.GetValue<string>() switch
            {
                "null" => DBNull.Value,
                "integer" => long.Parse(arg["value"]!.GetValue<string>()),
                "float" => arg["value"]!.GetValue<double>(),
                "text" => arg["value"]!.GetValue<string>(),
                _ => DBNull.Value
            };
            cmd.Parameters.AddWithValue($"$p{i + 1}", value);
        }

        var trimmed = sql.TrimStart();
        if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = cmd.ExecuteReader();
            var cols = new JsonArray();
            for (int i = 0; i < reader.FieldCount; i++) cols.Add(new JsonObject { ["name"] = reader.GetName(i) });
            var rows = new JsonArray();
            while (reader.Read())
            {
                var row = new JsonArray();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row.Add(reader.IsDBNull(i) ? new JsonObject { ["type"] = "null" } : reader.GetValue(i) switch
                    {
                        long l => new JsonObject { ["type"] = "integer", ["value"] = l.ToString() },
                        double d => new JsonObject { ["type"] = "float", ["value"] = d },
                        var o => new JsonObject { ["type"] = "text", ["value"] = o.ToString() }
                    });
                }
                rows.Add(row);
            }
            return new JsonObject { ["cols"] = cols, ["rows"] = rows, ["affected_row_count"] = 0 };
        }

        var affected = cmd.ExecuteNonQuery();
        return new JsonObject { ["cols"] = new JsonArray(), ["rows"] = new JsonArray(), ["affected_row_count"] = affected };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }
}

[TestClass]
[DoNotParallelize]
public class TursoSyncEndToEndTests
{
    private const string Url = "https://tpvc-test.turso.io";
    private readonly List<string> _files = new();
    private FakeHranaServer _server = null!;

    private sealed class Device
    {
        public required IDbContextFactory<AppDbContext> Factory { get; init; }
        public required InventoryService Inventory { get; init; }
        public required TursoSettingsService Settings { get; init; }
        public required ClinicProfileService Profile { get; init; }
        public required TursoSyncService Sync { get; init; }
    }

    private string TempFile(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tpvc_sync_{Guid.NewGuid():N}{ext}");
        _files.Add(path);
        return path;
    }

    [TestInitialize]
    public void Setup()
    {
        _server = new FakeHranaServer(TempFile(".remote.db"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _server.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in _files)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(f + suffix); } catch { }
            }
        }
    }

    private Device CreateDevice(string name, bool seed)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={TempFile(".db")}");
        var factory = new AppDbContextFactory(builder.Options);
        using (var db = factory.CreateDbContext())
        {
            if (seed)
            {
                DatabaseInitializer.Initialize(db);
            }
            else
            {
                db.Database.EnsureCreated();
                DatabaseInitializer.EnsureSchema(db);
            }
        }

        var settings = new TursoSettingsService(TempFile(".json"));
        var s = settings.CurrentSettings;
        s.IsSyncEnabled = true;
        s.AutoSyncOnChanges = false;
        s.DatabaseUrl = Url;
        s.AuthToken = "test-token";
        s.DeviceId = $"device-{name}";
        s.DeviceName = name;
        settings.SaveSettings(s);

        var profile = new ClinicProfileService(TempFile(".profile.json"));
        var inventory = new InventoryService(factory);
        var sync = new TursoSyncService(factory, settings, profile, inventory)
        {
            ClientFactory = (url, token) => new TursoClient(url, token, new HttpClient(_server, disposeHandler: false))
        };

        return new Device { Factory = factory, Inventory = inventory, Settings = settings, Profile = profile, Sync = sync };
    }

    [TestMethod]
    public void FormatArg_EncodesIntegersAsStrings_PerHranaSpec()
    {
        Assert.AreEqual("{\"type\":\"integer\",\"value\":\"42\"}", TursoClient.FormatArg(42).ToJsonString());
        Assert.AreEqual("{\"type\":\"integer\",\"value\":\"1\"}", TursoClient.FormatArg(true).ToJsonString());
        Assert.AreEqual("{\"type\":\"integer\",\"value\":\"6\"}", TursoClient.FormatArg(MovementType.SalidaVenta).ToJsonString());
        Assert.AreEqual("{\"type\":\"null\"}", TursoClient.FormatArg(null).ToJsonString());
        Assert.AreEqual("float", TursoClient.FormatArg(1.5)["type"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task FirstSync_PushesEverything_WithoutHttp400()
    {
        var pc = CreateDevice("pc", seed: true);
        var result = await pc.Sync.SyncAsync();

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsGreaterThan(0, result.RowsPushed);

        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Products";
        Assert.IsGreaterThan(0L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM Products WHERE CategorySyncId IS NULL";
        Assert.AreEqual(0L, (long)cmd.ExecuteScalar()!, "Cada producto sube con el SyncId de su categoría");
    }

    [TestMethod]
    public async Task SecondDevice_ReceivesFullDataSet_IncludingConsultationsSalesAndBatches()
    {
        var pc = CreateDevice("pc", seed: true);
        var patient = (await pc.Inventory.GetPatientsAsync()).First();
        patient.Occupation = "Docente";
        patient.FamilyHistory = "Madre con DM2";
        await pc.Inventory.SavePatientAsync(patient);
        await pc.Inventory.AddConsultationNoteAsync(patient.Id, new PatientConsultation
        {
            ChiefComplaint = "Tos seca",
            Diagnosis = "Rinofaringitis (J00)",
            VitalSigns = "TA: 120/80 mmHg | Peso: 70 kg"
        });
        var product = (await pc.Inventory.GetProductsAsync()).First(p => p.CurrentStock > 2);
        var sale = await pc.Inventory.RegisterSaleAsync(new[] { (product.Id, 1, product.Price) }, patient.FullName, "Efectivo");

        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        var tablet = CreateDevice("tablet", seed: false);
        var pulled = await tablet.Sync.SyncAsync();
        Assert.IsTrue(pulled.Success, pulled.ErrorMessage);

        var tabletPatient = (await tablet.Inventory.GetPatientsAsync()).Single(p => p.SyncId == patient.SyncId);
        Assert.AreEqual("Docente", tabletPatient.Occupation, "Viajan todos los campos NOM-004, no solo un subconjunto");
        Assert.AreEqual("Madre con DM2", tabletPatient.FamilyHistory);

        var tabletFull = (await tablet.Inventory.GetPatientByIdAsync(tabletPatient.Id))!;
        Assert.IsTrue(tabletFull.Consultations.Any(c => c.ChiefComplaint == "Tos seca"), "Las consultas también se descargan");

        var tabletSale = await tablet.Inventory.GetSaleByFolioAsync(sale!.Folio);
        Assert.IsNotNull(tabletSale, "Las ventas también se descargan");
        var tabletProduct = (await tablet.Inventory.GetProductsAsync()).Single(p => p.SyncId == product.SyncId);
        Assert.AreEqual(tabletProduct.Id, tabletSale.Items.Single().ProductId, "La partida apunta al producto local correcto");
        Assert.AreEqual(product.CurrentStock - 1, tabletProduct.CurrentStock);

        using var db = tablet.Factory.CreateDbContext();
        Assert.AreEqual(
            await pc.Factory.CreateDbContext().ProductBatches.CountAsync(),
            await db.ProductBatches.CountAsync(), "Lotes completos en la tablet");
    }

    [TestMethod]
    public async Task PulledRows_KeepRemoteTimestamps_AndDoNotPingPong()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Ambos equipos ya están al día: otra ronda no debe mover nada
        var again = await tablet.Sync.SyncAsync();
        Assert.AreEqual(0, again.RowsPulled, "No deben volver a descargarse los mismos registros");
        var pcAgain = await pc.Sync.SyncAsync();
        Assert.AreEqual(0, pcAgain.RowsPulled, "La PC no debe recibir de vuelta sus propios registros");
        Assert.AreEqual(0, pcAgain.RowsPushed, "Nada nuevo que subir");

        var pcProduct = (await pc.Inventory.GetProductsAsync()).First();
        var tabletProduct = (await tablet.Inventory.GetProductsAsync()).Single(p => p.SyncId == pcProduct.SyncId);
        Assert.AreEqual(pcProduct.UpdatedAtUtc, tabletProduct.UpdatedAtUtc, "La marca remota se conserva tal cual (sin desfase de zona horaria)");
    }

    [TestMethod]
    public async Task EditOnTablet_ReachesPc_AndLastWriteWins()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        var tProduct = (await tablet.Inventory.GetProductsAsync()).First();
        tProduct.Price = 999.5m;
        await tablet.Inventory.SaveProductAsync(tProduct);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        var result = await pc.Sync.SyncAsync();
        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.RowsPulled);
        var pcProduct = (await pc.Inventory.GetProductsAsync()).Single(p => p.SyncId == tProduct.SyncId);
        Assert.AreEqual(999.5m, pcProduct.Price);
    }

    [TestMethod]
    public async Task Deletions_PropagateThroughTombstones()
    {
        var pc = CreateDevice("pc", seed: true);
        var batch = (await pc.Inventory.GetBatchesAsync()).First();
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        await pc.Inventory.DeleteBatchAsync(batch.Id);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        Assert.IsFalse((await tablet.Inventory.GetBatchesAsync()).Any(b => b.SyncId == batch.SyncId));
    }

    [TestMethod]
    public async Task DuplicateRecordNumbersFromTwoDevices_DoNotBlockSync()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Ambos equipos crean un expediente nuevo sin conexión: obtienen el mismo folio
        var onPc = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente PC", Age = 30 });
        var onTablet = await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Tablet", Age = 5 });
        Assert.AreEqual(onPc.RecordNumber, onTablet.RecordNumber);

        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var result = await pc.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);

        var pcPatients = await pc.Inventory.GetPatientsAsync();
        Assert.IsTrue(pcPatients.Any(p => p.FullName == "Paciente Tablet" && p.RecordNumber == onTablet.RecordNumber + "-R1"));
        Assert.IsTrue(pcPatients.Any(p => p.FullName == "Paciente PC" && p.RecordNumber == onPc.RecordNumber));
    }

    [TestMethod]
    public async Task LegacyV1RemoteTables_AreRenamed_NotDropped()
    {
        using (var cmd = _server.Connection.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE Products (Id INTEGER PRIMARY KEY AUTOINCREMENT, SyncId TEXT NOT NULL UNIQUE, Code TEXT NOT NULL, " +
                              "Name TEXT NOT NULL, Price REAL NOT NULL, UpdatedAtUtc TEXT NOT NULL); " +
                              "INSERT INTO Products (SyncId, Code, Name, Price, UpdatedAtUtc) VALUES ('x', 'OLD', 'Viejo', 1, '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        var pc = CreateDevice("pc", seed: true);
        var result = await pc.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);

        using var check = _server.Connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM legacy_v1_Products";
        Assert.AreEqual(1L, (long)check.ExecuteScalar()!, "Los datos heredados se conservan");
        check.CommandText = "SELECT COUNT(*) FROM Products";
        Assert.IsGreaterThan(1L, (long)check.ExecuteScalar()!);
    }

    [TestMethod]
    public async Task SchemaCheck_RunsOncePerSession()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var before = _server.StatementCount;
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var perIdleSync = _server.StatementCount - before;

        // Sin cambios: marca de agua + 1 lectura por tabla + tombstones + perfil (sin DDL)
        Assert.IsLessThan(25, perIdleSync, $"Una sincronización sin cambios no debe reenviar el esquema ({perIdleSync} sentencias)");
    }

    [TestMethod]
    public async Task ClinicProfile_LastWriteWinsAcrossDevices()
    {
        var pc = CreateDevice("pc", seed: true);
        var profile = pc.Profile.CurrentProfile;
        profile.DoctorName = "Dra. María del Cielo Alberto Galicia";
        profile.ProfessionalLicense = "15632243";
        pc.Profile.SaveProfile(profile);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.AreEqual("15632243", tablet.Profile.CurrentProfile.ProfessionalLicense);

        await Task.Delay(20);
        var tp = tablet.Profile.CurrentProfile;
        tp.Phone = "2481592448";
        tablet.Profile.SaveProfile(tp);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        Assert.AreEqual("2481592448", pc.Profile.CurrentProfile.Phone, "El cambio más reciente de la tablet llega a la PC");
    }
}
