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

[TestClass]
[DoNotParallelize]
public partial class TursoSyncEndToEndTests
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
        public required string DbPath { get; init; }

        /// <summary>Su turso_config.json (para volver a abrirlo con otro servicio, como al reiniciar TPVC).</summary>
        public string SettingsPath { get; init; } = string.Empty;

        /// <summary>Sus resultados de laboratorio, con su archivo junto a su base (paso 8.12b).</summary>
        public LabResultService Labs { get; init; } = null!;
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
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private readonly List<string> _dirs = new();

    /// <param name="demo">Con <paramref name="seed"/>: los datos demo además del catálogo (como las pruebas de siempre).
    /// Sin ellos es una instalación nueva tal como arranca el programa (paso 8.8).</param>
    private Device CreateDevice(string name, bool seed, TimeProvider? clock = null, bool demo = true)
    {
        // Cada base en su carpeta: sus respaldos (carpeta "backups" junto a ella) no se mezclan con los de otra prueba
        var dir = Path.Combine(Path.GetTempPath(), $"tpvc_sync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var dbPath = Path.Combine(dir, "consultorio.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={dbPath}");
        var factory = new AppDbContextFactory(builder.Options);
        using (var db = factory.CreateDbContext())
        {
            if (seed)
            {
                DatabaseInitializer.Initialize(db, seedDemoData: demo);
            }
            else
            {
                db.Database.EnsureCreated();
                DatabaseInitializer.EnsureSchema(db);
            }
        }

        var settingsPath = TempFile(".json");
        var settings = new TursoSettingsService(settingsPath);
        if (clock != null) settings.Clock = clock;
        var s = settings.CurrentSettings;
        s.IsSyncEnabled = true;
        s.AutoSyncOnChanges = false;
        s.DatabaseUrl = Url;
        s.AuthToken = "test-token";
        s.DeviceId = $"device-{name}";
        s.DeviceName = name;
        settings.SaveSettings(s);

        // Su perfil y su carpeta del membrete junto a su base (paso 8.12: cada equipo con sus imágenes)
        var profile = new ClinicProfileService(Path.Combine(dir, "clinic_profile.json"));
        var inventory = new InventoryService(factory);
        var labs = new LabResultService(factory, new TPVC.Services.LabFiles.LabFileStore(Path.Combine(dir, "laboratorio")),
            new LocalPreferencesStore(Path.Combine(dir, "preferencias.json")));
        var sync = new TursoSyncService(factory, settings, profile, inventory, labs)
        {
            ClientFactory = (url, token) => new TursoClient(url, token, new HttpClient(_server, disposeHandler: false)),
            // Las pruebas corren la vuelta de los archivos de laboratorio a mano (RunLabTransfersAsync)
            AutoLabTransfers = false
        };
        if (clock != null) sync.Clock = clock;

        return new Device
        {
            Factory = factory, Inventory = inventory, Settings = settings, Profile = profile, Sync = sync, DbPath = dbPath, SettingsPath = settingsPath,
            Labs = labs
        };
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

        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Products";
        Assert.IsGreaterThan(0L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM Products WHERE CategorySyncId IS NULL";
        Assert.AreEqual(0L, (long)cmd.ExecuteScalar()!, "Cada producto sube con el SyncId de su categoría");

        // Paso 8.3: la nube guarda la revisión del contrato (contracts/sync/schema.json) junto a la huella
        cmd.CommandText = "SELECT Value FROM _tpvc_meta WHERE Key = 'contract'";
        Assert.AreEqual(SyncContract.Revision.ToString(), cmd.ExecuteScalar());
        cmd.CommandText = "SELECT Value FROM _tpvc_meta WHERE Key = 'schema'";
        var schema = System.Text.Json.Nodes.JsonNode.Parse(SyncContract.BuildSchemaJson(pc.Factory.CreateDbContext().Model))!;
        Assert.AreEqual(schema["fingerprint"]!.GetValue<string>(), cmd.ExecuteScalar(), "La huella de la nube es la del contrato exportado");
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
        using (_server.Lock())
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

        using var held = _server.Lock();
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
    public async Task IdleSync_IsOneRequest_ReadAsOneSnapshot()
    {
        // Paso 8.6: marca de agua, la primera página de las 13 tablas y de las lápidas y el perfil del consultorio en
        // una sola transacción de lectura (hasta v1.20.3 eran 16 peticiones). El esquema y el registro del equipo, una
        // vez por sesión
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        _server.ClearRecording();
        var before = _server.RequestCount;
        var readsBefore = _server.BilledRowsRead;
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        Assert.AreEqual(1, _server.RequestCount - before, "Una sincronización sin cambios = 1 petición");
        var statements = _server.Requests.Single().Statements;
        Assert.AreEqual("BEGIN", statements[0].Sql);
        Assert.AreEqual("COMMIT", statements[^1].Sql, "Una sola foto: watermark, páginas y perfil en la misma transacción");
        Assert.AreEqual(0, _server.Requests.SelectMany(r => r.Statements).Sum(s => s.RowsWritten), "Sin cambios no se escribe nada");
        foreach (var request in _server.Requests)
        {
            foreach (var s in request.Statements) Console.WriteLine($"#{request.Number} lecturas={s.RowsRead} {s.Sql}");
        }
        Console.WriteLine($"Lecturas estimadas de la sincronización sin cambios: {_server.BilledRowsRead - readsBefore}");
    }

    [TestMethod]
    public async Task Blocked_PausesAutomaticSyncForAnHour_ThenRetries()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var pc = CreateDevice("pc", seed: true, clock);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        _server.Blocked = FakeBlockedMode.StatementError;
        var blocked = await pc.Sync.SyncAsync();
        Assert.IsFalse(blocked.Success);
        StringAssert.StartsWith(blocked.ErrorMessage, "Se agotó la cuota mensual de Turso");
        Assert.AreEqual(TursoSyncPauseReason.QuotaExhausted, pc.Sync.PauseReason);
        Assert.AreEqual(TursoSyncStatus.Paused, pc.Sync.Status);
        StringAssert.StartsWith(pc.Sync.StatusMessage, "Se agotó la cuota mensual de Turso · siguiente intento a las ", "En el estado, una sola línea");

        // Lo automático (sincronizar tras guardar, la revisión de consultas) espera sin llegar a Turso
        var requests = _server.RequestCount;
        Assert.IsFalse((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        var handoff = new ConsultationHandoffService(pc.Sync, pc.Settings);
        var poll = await Assert.ThrowsExactlyAsync<TursoException>(() => handoff.PollAsync(null));
        Assert.AreEqual(TursoException.QuotaExhaustedCode, poll.ErrorCode);
        Assert.AreEqual("Se agotó la cuota mensual de Turso (se reintenta cada hora)", ConsultationHandoffService.Describe(poll));
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.IsFalse((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        Assert.AreEqual(requests, _server.RequestCount, "Durante la hora de pausa no se pregunta a Turso");

        // Pasada la hora, el siguiente intento automático vuelve a sincronizar
        clock.Advance(TimeSpan.FromMinutes(2));
        _server.Blocked = FakeBlockedMode.None;
        var retried = await pc.Sync.SyncAsync(isBackgroundAutoSync: true);
        Assert.IsTrue(retried.Success, retried.ErrorMessage);
        Assert.AreEqual(TursoSyncPauseReason.None, pc.Sync.PauseReason);
        Assert.AreEqual(TursoSyncStatus.Success, pc.Sync.Status);
    }

    [TestMethod]
    public async Task RepeatedFullDownload_TripsTheConsumptionBrake_UntilResume()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success, "1.ª descarga completa: la primera sincronización");
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success, "2.ª: «Sincronización completa»");

        var requests = _server.RequestCount;
        var third = await pc.Sync.InitialUploadToTursoAsync();
        Assert.IsFalse(third.Success);
        StringAssert.StartsWith(third.ErrorMessage, "Sincronización pausada: consumo anormal");
        Assert.AreEqual(requests, _server.RequestCount, "La tercera no llega a subir ni a bajar nada");
        Assert.AreEqual(TursoSyncPauseReason.ConsumptionBrake, pc.Sync.PauseReason);
        Assert.AreEqual(TursoSyncStatus.Paused, pc.Sync.Status);
        Assert.AreEqual(TursoConsumptionBrake.PausedTitle, pc.Sync.StatusMessage);

        // Lo automático se detiene; lo que pide el médico sigue, y la pausa se queda hasta "Reanudar"
        Assert.IsFalse((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        Assert.AreEqual(requests, _server.RequestCount);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(TursoSyncStatus.Paused, pc.Sync.Status, "Una sincronización a mano no quita el freno");
        Assert.IsNotNull(pc.Settings.CurrentSettings.ConsumptionBrakeTrippedUtc);

        pc.Sync.ResumeAfterConsumptionBrake();
        Assert.AreEqual(TursoSyncPauseReason.None, pc.Sync.PauseReason);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success, "Después de «Reanudar» el día se cuenta desde cero");
    }

    [TestMethod]
    public async Task DailyEstimatedUsageOverTheCap_TripsTheConsumptionBrake()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Una sincronización que deja el día arriba del tope de lecturas activa el freno al terminar
        pc.Settings.RecordUsage(TursoConsumptionBrake.MaxRowsReadPerDay, 0);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(TursoSyncPauseReason.ConsumptionBrake, pc.Sync.PauseReason);
        Assert.AreEqual(TursoSyncStatus.Paused, pc.Sync.Status);

        // La revisión de consultas no pasa por la sincronización: su consumo también cuenta
        pc.Sync.ResumeAfterConsumptionBrake();
        pc.Sync.AddExternalUsage(rowsRead: 0, rowsWritten: TursoConsumptionBrake.MaxRowsWrittenPerDay + 1);
        Assert.AreEqual(TursoSyncPauseReason.ConsumptionBrake, pc.Sync.PauseReason);
        var handoff = new ConsultationHandoffService(pc.Sync, pc.Settings);
        var poll = await Assert.ThrowsExactlyAsync<TursoException>(() => handoff.PollAsync(null));
        Assert.AreEqual(TursoException.ConsumptionBrakeCode, poll.ErrorCode);

        // "Reanudar" anota lo pendiente en el mes: la siguiente revisión normal no vuelve a activar el freno
        var monthBefore = pc.Settings.GetQuotaSummary().RowsWrittenThisMonth;
        pc.Sync.ResumeAfterConsumptionBrake();
        pc.Sync.AddExternalUsage(rowsRead: 3, rowsWritten: 1);
        Assert.AreEqual(TursoSyncPauseReason.None, pc.Sync.PauseReason);
        Assert.AreEqual(monthBefore + TursoConsumptionBrake.MaxRowsWrittenPerDay + 1, pc.Settings.GetQuotaSummary().RowsWrittenThisMonth);
        Assert.IsTrue((await handoff.PollAsync(null)).Incoming.Count == 0);
    }

    // ================================================================== filas apartadas (paso 8.4)

    private void ExecuteCloud(string sql)
    {
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? CloudScalar(string sql)
    {
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>Saca una fila de la nube como si todavía no hubiera llegado; <see cref="ReleaseHeldRow"/> la regresa.</summary>
    private void HoldCloudRow(string table, Guid syncId) =>
        ExecuteCloud($"CREATE TABLE held_{table} AS SELECT * FROM \"{table}\" WHERE SyncId = '{TursoSyncService.FormatGuid(syncId)}'; " +
                     $"DELETE FROM \"{table}\" WHERE SyncId = '{TursoSyncService.FormatGuid(syncId)}';");

    private void ReleaseHeldRow(string table) =>
        ExecuteCloud($"INSERT INTO \"{table}\" SELECT * FROM held_{table}; " +
                     $"UPDATE \"{table}\" SET SyncedAtUtc = {TursoSyncService.ServerNowSql} WHERE SyncId IN (SELECT SyncId FROM held_{table}); " +
                     $"DROP TABLE held_{table};");

    [TestMethod]
    public async Task RowWithoutItsRequiredPrincipal_IsParked_AndAppliedWhenThePrincipalArrives()
    {
        var pc = CreateDevice("pc", seed: true);
        var patient = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Apartado", Age = 40 });
        await pc.Inventory.AddConsultationNoteAsync(patient.Id, new PatientConsultation { ChiefComplaint = "Cefalea tensional" });
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // La consulta llega a la tablet antes que su expediente
        HoldCloudRow("Patients", patient.SyncId);
        var tablet = CreateDevice("tablet", seed: false);
        var first = await tablet.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        Assert.AreEqual(1, first.ParkedRows, "La consulta se aparta en lugar de perderse");
        StringAssert.EndsWith(tablet.Sync.StatusMessage, "· 1 cambio sin sincronizar");

        ReleaseHeldRow("Patients");
        var second = await tablet.Sync.SyncAsync();
        Assert.IsTrue(second.Success, second.ErrorMessage);
        Assert.AreEqual(0, second.ParkedRows, "El contador vuelve a cero solo");
        Assert.AreEqual(0, tablet.Sync.ParkedCount);

        var onTablet = (await tablet.Inventory.GetPatientsAsync()).Single(p => p.SyncId == patient.SyncId);
        var full = (await tablet.Inventory.GetPatientByIdAsync(onTablet.Id))!;
        Assert.IsTrue(full.Consultations.Any(c => c.ChiefComplaint == "Cefalea tensional"), "La consulta apartada se aplicó al llegar su expediente");
    }

    /// <summary>Una venta deja un movimiento con el lote del que salió (FEFO).</summary>
    private static async Task SellOneFromABatchAsync(Device device)
    {
        var batch = (await device.Inventory.GetBatchesAsync()).First(b => !b.IsDepleted && b.Quantity > 2);
        var product = (await device.Inventory.GetProductsAsync()).Single(p => p.Id == batch.ProductId);
        await device.Inventory.RegisterSaleAsync(new[] { (product.Id, 1, product.Price) }, "Público General", "Efectivo");
    }

    [TestMethod]
    public async Task MovementThatArrivesBeforeItsBatch_EndsUpWithItsBatch()
    {
        var pc = CreateDevice("pc", seed: true);
        await SellOneFromABatchAsync(pc);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var batchSyncId = Guid.Parse((string)CloudScalar("SELECT BatchSyncId FROM InventoryMovements WHERE BatchSyncId IS NOT NULL LIMIT 1")!);
        var movements = (long)CloudScalar($"SELECT COUNT(*) FROM InventoryMovements WHERE BatchSyncId = '{TursoSyncService.FormatGuid(batchSyncId)}'")!;

        // Antes la referencia opcional se guardaba en nulo y el movimiento nunca volvía a aplicarse
        HoldCloudRow("ProductBatches", batchSyncId);
        var tablet = CreateDevice("tablet", seed: false);
        var first = await tablet.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        Assert.AreEqual((int)movements, first.ParkedRows);

        ReleaseHeldRow("ProductBatches");
        Assert.AreEqual(0, (await tablet.Sync.SyncAsync()).ParkedRows);

        using var db = tablet.Factory.CreateDbContext();
        var batchId = db.ProductBatches.Single(b => b.SyncId == batchSyncId).Id;
        Assert.AreEqual((int)movements, db.InventoryMovements.Count(m => m.BatchId == batchId), "Cada movimiento terminó con su lote");
    }

    [TestMethod]
    public async Task OptionalReferenceToADeletedPrincipal_IsAppliedEmpty()
    {
        var pc = CreateDevice("pc", seed: true);
        await SellOneFromABatchAsync(pc);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var batchSyncId = Guid.Parse((string)CloudScalar("SELECT BatchSyncId FROM InventoryMovements WHERE BatchSyncId IS NOT NULL LIMIT 1")!);

        // El lote se borró en la nube (con su lápida) y sus movimientos se quedaron: se aplican sin lote
        var id = TursoSyncService.FormatGuid(batchSyncId);
        ExecuteCloud($"DELETE FROM ProductBatches WHERE SyncId = '{id}'; INSERT INTO SyncTombstones VALUES ('ProductBatch', '{id}', " +
                     $"{TursoSyncService.ServerNowSql}, {TursoSyncService.ServerNowSql}, 'device-pc');");
        var tablet = CreateDevice("tablet", seed: false);
        var result = await tablet.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(0, result.ParkedRows, "En la misma sincronización: la lápida del principal deja aplicar la fila");

        using var db = tablet.Factory.CreateDbContext();
        var cloudMovements = (long)CloudScalar($"SELECT COUNT(*) FROM InventoryMovements WHERE BatchSyncId = '{id}'")!;
        Assert.IsGreaterThan(0L, cloudMovements);
        Assert.AreEqual(db.InventoryMovements.Count(), (int)(long)CloudScalar("SELECT COUNT(*) FROM InventoryMovements")!, "Ningún movimiento se perdió");
    }

    [TestMethod]
    public async Task TombstoneThatFails_IsParked_AndRetried()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Una categoría borrada en otro equipo que aquí todavía tiene productos: su borrado falla
        var categories = await tablet.Inventory.GetCategoriesAsync();
        var products = await tablet.Inventory.GetProductsAsync();
        var doomed = categories.First(c => products.Any(p => p.CategoryId == c.Id));
        var keep = categories.First(c => c.Id != doomed.Id);
        var id = TursoSyncService.FormatGuid(doomed.SyncId);
        ExecuteCloud($"INSERT INTO SyncTombstones VALUES ('Category', '{id}', {TursoSyncService.ServerNowSql}, {TursoSyncService.ServerNowSql}, 'device-pc');");

        var failed = await tablet.Sync.SyncAsync();
        Assert.IsTrue(failed.Success, failed.ErrorMessage);
        Assert.AreEqual(1, failed.ParkedRows, "La lápida se aparta en lugar de perderse");
        using (var db = tablet.Factory.CreateDbContext())
        {
            var parked = db.SyncParkedRows.Single();
            Assert.AreEqual(SyncParkedRow.Tombstone, parked.Direction);
            Assert.AreEqual(2, parked.Attempts, "Se intentó al bajarla y otra vez al final de la descarga");
        }

        // Aquí se mueven sus productos a otra categoría: el siguiente intento la borra
        foreach (var product in products.Where(p => p.CategoryId == doomed.Id))
        {
            product.CategoryId = keep.Id;
            await tablet.Inventory.SaveProductAsync(product);
        }
        var retried = await tablet.Sync.SyncAsync();
        Assert.AreEqual(0, retried.ParkedRows);
        Assert.IsFalse((await tablet.Inventory.GetCategoriesAsync()).Any(c => c.SyncId == doomed.SyncId));
    }

    [TestMethod]
    public async Task RowThatCannotBePushed_IsParked_AndRetriedAfterTheCursorPassedIt()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Un lote huérfano (su producto no existe): no puede subir sin el SyncId de su producto
        var orphanSyncId = Guid.NewGuid();
        using (var db = pc.Factory.CreateDbContext())
        {
            db.Database.OpenConnection();
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
            db.ProductBatches.Add(new ProductBatch { SyncId = orphanSyncId, ProductId = 999_999, BatchNumber = "HUERFANO", ExpirationDate = DateTime.Today.AddYears(1), Quantity = 3 });
            db.SaveChanges();
        }

        var first = await pc.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        Assert.AreEqual(1, first.ParkedRows);
        Assert.AreEqual(0L, (long)CloudScalar($"SELECT COUNT(*) FROM ProductBatches WHERE SyncId = '{TursoSyncService.FormatGuid(orphanSyncId)}'")!);

        // Aparece su producto: el lote sube aunque el cursor de subida ya pasó su marca
        using (var db = pc.Factory.CreateDbContext())
        {
            db.Products.Add(new Product { Id = 999_999, Code = "PRD-APARTADO", Name = "Producto que faltaba", CategoryId = db.Categories.First().Id });
            db.SaveChanges();
        }
        var second = await pc.Sync.SyncAsync();
        Assert.IsTrue(second.Success, second.ErrorMessage);
        Assert.AreEqual(0, second.ParkedRows);
        Assert.AreEqual(1L, (long)CloudScalar($"SELECT COUNT(*) FROM ProductBatches WHERE SyncId = '{TursoSyncService.FormatGuid(orphanSyncId)}' AND ProductSyncId IS NOT NULL")!);
    }

    [TestMethod]
    public async Task EditingAServiceKeepsItsDefaultProducts_WithoutTombstones()
    {
        var pc = CreateDevice("pc", seed: true);
        var service = (await pc.Inventory.GetClinicalServicesAsync()).First(s => s.DefaultProducts.Count > 0);
        var before = service.DefaultProducts.ToDictionary(d => d.ProductId, d => d.SyncId);

        var edited = service.DefaultProducts.Select(d => (d.ProductId, d.Quantity + 1, d.Instructions)).ToList();
        await pc.Inventory.SaveClinicalServiceAsync(service, edited);

        using var db = pc.Factory.CreateDbContext();
        var after = db.ServiceDefaultProducts.Where(d => d.ClinicalServiceId == service.Id).ToList();
        CollectionAssert.AreEquivalent(before.Values.ToList(), after.Select(d => d.SyncId).ToList(), "Cada renglón conserva su SyncId");
        Assert.IsTrue(after.All(d => d.Quantity == service.DefaultProducts.Single(o => o.ProductId == d.ProductId).Quantity + 1));
        Assert.IsFalse(db.SyncTombstones.Any(t => t.EntityType == nameof(ServiceDefaultProduct)), "Editar no deja lápidas");
    }

    // ================================================================== registro de cambios (paso 8.5)

    [TestMethod]
    public async Task RowStampedBeforeTheLastSync_IsPushedAnyway()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // El reloj de la PC se atrasó después de sincronizar: el expediente nuevo queda marcado antes de esa
        // sincronización. Con el cursor de reloj (LastPushedLocalUtc) nunca subía
        var patient = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Reloj Atrasado", Age = 50 });
        using (var db = pc.Factory.CreateDbContext())
        {
            db.Database.ExecuteSqlRaw("UPDATE \"Patients\" SET \"UpdatedAtUtc\" = {0} WHERE \"Id\" = {1}",
                TursoSyncService.FormatDate(DateTime.UtcNow.AddHours(-3)), patient.Id);
        }

        var result = await pc.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(1L, CloudScalar($"SELECT COUNT(*) FROM Patients WHERE SyncId = '{TursoSyncService.FormatGuid(patient.SyncId)}'"));
    }

    [TestMethod]
    public async Task EditingAPushedRow_GetsASeqAboveTheCursor_AndGoesUp()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        long cursor;
        using (var db = pc.Factory.CreateDbContext()) cursor = (await SyncChangeLog.ReadPushCursorAsync(db))!.Value;

        var product = (await pc.Inventory.GetProductsAsync()).First();
        product.Price = 321.5m;
        await pc.Inventory.SaveProductAsync(product);
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.IsGreaterThan(cursor, ChangeLogTestSupport.SeqOf(db, nameof(Product), product.SyncId)!.Value);
        }

        var result = await pc.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual("321.5", CloudScalar($"SELECT Price FROM Products WHERE SyncId = '{TursoSyncService.FormatGuid(product.SyncId)}'"));
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.IsGreaterThan(cursor, (await SyncChangeLog.ReadPushCursorAsync(db))!.Value, "El cursor avanzó");
        }
    }

    [TestMethod]
    public async Task DeletingASale_PushesTombstonesForItsItems_StampedAfterEachRow()
    {
        var pc = CreateDevice("pc", seed: true);
        var products = (await pc.Inventory.GetProductsAsync()).Where(p => p.CurrentStock > 2).Take(2).ToList();
        var sale = await pc.Inventory.RegisterSaleAsync(products.Select(p => (p.Id, 1, p.Price)).ToArray(), "Público General", "Efectivo");
        Assert.IsNotNull(sale);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        List<SaleItem> items;
        using (var db = pc.Factory.CreateDbContext())
        {
            items = db.SaleItems.AsNoTracking().Where(i => i.SaleId == sale.Id).ToList();
            // La cascada la hace SQLite, sin que EF cargue las partidas: antes sus lápidas nunca existían
            db.Database.ExecuteSqlRaw("DELETE FROM \"Sales\" WHERE \"Id\" = {0}", sale.Id);
        }
        Assert.HasCount(2, items);

        var result = await pc.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(1L, CloudScalar($"SELECT COUNT(*) FROM SyncTombstones WHERE EntityType = 'Sale' AND SyncId = '{TursoSyncService.FormatGuid(sale.SyncId)}'"));
        foreach (var item in items)
        {
            var id = TursoSyncService.FormatGuid(item.SyncId);
            var deletedAt = TursoSyncService.ParseDate(CloudScalar($"SELECT DeletedAtUtc FROM SyncTombstones WHERE EntityType = 'SaleItem' AND SyncId = '{id}'"));
            Assert.IsGreaterThan(item.UpdatedAtUtc, deletedAt, "La lápida queda después de la marca de la partida");
            Assert.AreEqual(0L, CloudScalar($"SELECT COUNT(*) FROM SaleItems WHERE SyncId = '{id}'"), "La partida también se borra en la nube");
        }
    }

    [TestMethod]
    public async Task WhatArrivesFromTheCloud_IsNotLogged_AndDoesNotGoBackUp()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        var tablet = CreateDevice("tablet", seed: false);
        var pulled = await tablet.Sync.SyncAsync();
        Assert.IsTrue(pulled.Success, pulled.ErrorMessage);
        Assert.IsGreaterThan(0, pulled.RowsPulled);
        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.AreEqual(0L, ChangeLogTestSupport.Count(db, "\"SyncChangeLog\""), "Lo recibido no se anota");
        }

        _server.ClearRecording();
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.AreEqual(0, _server.Requests.SelectMany(r => r.Statements).Sum(s => s.RowsWritten), "Nada vuelve a subir");
    }

    [TestMethod]
    public async Task UpperCaseLocalSyncIds_GoUpInLowerCase()
    {
        var pc = CreateDevice("pc", seed: true);
        var product = (await pc.Inventory.GetProductsAsync()).First();
        using (var db = pc.Factory.CreateDbContext())
        {
            var stored = db.Database.SqlQueryRaw<string>("SELECT \"SyncId\" AS Value FROM \"Products\" WHERE \"Id\" = {0}", product.Id).AsEnumerable().Single();
            Assert.AreEqual(stored.ToUpperInvariant(), stored, "Así guarda EF Core los Guid en SQLite");
        }

        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var id = TursoSyncService.FormatGuid(product.SyncId);
        Assert.AreEqual(id.ToLowerInvariant(), id);
        Assert.AreEqual(1L, CloudScalar($"SELECT COUNT(*) FROM Products WHERE SyncId = '{id}'"));
        Assert.AreEqual(0L, CloudScalar($"SELECT COUNT(*) FROM Products WHERE SyncId <> lower(SyncId)"));
        Assert.AreEqual(0L, CloudScalar($"SELECT COUNT(*) FROM Products WHERE CategorySyncId <> lower(CategorySyncId)"));
    }

    [TestMethod]
    public async Task FirstPushAfterUpdating_SendsEverythingOnce()
    {
        // Una base de la versión anterior, con datos y sin registro de cambios ni cursor
        var pc = CreateDevice("pc", seed: true);
        using (var db = pc.Factory.CreateDbContext())
        {
            ChangeLogTestSupport.RemoveChangeLog(db);
            SyncChangeLog.EnsureInstalled(db, backupBeforeFirstInstall: false);
            Assert.AreEqual(0L, ChangeLogTestSupport.Count(db, "\"SyncChangeLog\""), "Lo que ya existía no está anotado");
        }

        var first = await pc.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        using (var db = pc.Factory.CreateDbContext())
        {
            foreach (var map in SyncSchema.FromModel(db.Model).Tables)
            {
                Assert.AreEqual(ChangeLogTestSupport.Count(db, $"\"{map.Table}\""), CloudScalar($"SELECT COUNT(*) FROM \"{map.Table}\""), map.Table);
            }
        }

        var again = await pc.Sync.SyncAsync();
        Assert.AreEqual(0, again.RowsPushed, "Ya subió todo: solo una vez");
    }

    // ================================================================== descarga eficiente y desempates (paso 8.6)

    /// <summary>Filas que "escribió otro equipo" directo en la nube, con un SyncId válido y su marca de servidor.</summary>
    private void InsertCloudRows(string table, int count, string syncedAtSql, string extraColumns = "", string extraValues = "")
    {
        const string guid = "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-a' || " +
                            "substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)))";
        ExecuteCloud($"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {count}) " +
                     $"INSERT INTO \"{table}\" (SyncId, UpdatedAtUtc, SyncedAtUtc, OriginDeviceId{extraColumns}) " +
                     $"SELECT {guid}, '2026-10-10 15:00:00.0000000', {syncedAtSql}, 'device-otra'{extraValues} FROM n;");
    }

    [TestMethod]
    public async Task ManyRowsWithTheSameSyncedAtUtc_ArePaged_WithoutLosingAny()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        InsertCloudRows("Categories", 1200, "'2026-10-10 15:00:00.000'", ", Name", ", 'Categoría ' || i");
        var cloudCount = (long)CloudScalar("SELECT COUNT(*) FROM Categories")!;

        var tablet = CreateDevice("tablet", seed: false);
        tablet.Sync.PullPageSizeOverride = 100;
        var result = await tablet.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);

        using var db = tablet.Factory.CreateDbContext();
        Assert.AreEqual(cloudCount, ChangeLogTestSupport.Count(db, "\"Categories\""), "Con la misma marca, las páginas siguen por SyncId");
    }

    [TestMethod]
    public async Task TwoDevicesSavingWithTheSameStamp_EndUpWithTheSameRow()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Los dos guardan el mismo producto con la misma marca y distinto precio
        var product = (await pc.Inventory.GetProductsAsync()).First();
        var stamp = TursoSyncService.FormatDate(DateTime.UtcNow.AddMinutes(1));
        foreach (var (device, price) in new[] { (pc, "111.5"), (tablet, "222.5") })
        {
            using var db = device.Factory.CreateDbContext();
            db.Database.ExecuteSqlRaw("UPDATE \"Products\" SET \"Price\" = {0}, \"UpdatedAtUtc\" = {1} WHERE \"SyncId\" = {2}",
                price, stamp, product.SyncId.ToString().ToUpperInvariant());
        }

        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success, "La nube se queda con la de la tablet");
        var result = await pc.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);

        async Task<decimal> PriceOn(Device device) => (await device.Inventory.GetProductsAsync()).Single(p => p.SyncId == product.SyncId).Price;
        Assert.AreEqual(222.5m, await PriceOn(pc), "La nube no aceptó la de la PC (empate): la PC aplica la de la nube");
        Assert.AreEqual(222.5m, await PriceOn(tablet));
        Assert.AreEqual("222.5", CloudScalar($"SELECT Price FROM Products WHERE SyncId = '{TursoSyncService.FormatGuid(product.SyncId)}'"));
        Assert.AreEqual(0, (await pc.Sync.SyncAsync()).RowsPushed, "Lo aplicado en el desempate no vuelve a subir");
    }

    [TestMethod]
    public async Task FullDownload_WithA1MbResponseCap_HalvesThePage_AndReadsAboutEachRowOnce()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        // 300 expedientes de otro equipo con antecedentes largos (~12 KB): una página de 100 pasa de 1 MB
        InsertCloudRows("Patients", 300, "strftime('%Y-%m-%d %H:%M:%f', '2026-10-10', '+' || i || ' seconds')",
            ", RecordNumber, FullName, FamilyHistory", ", 'EXP-OTRO-' || i, 'Paciente ' || i, hex(zeroblob(6000))");
        long remoteRows = 0;
        using (var db = pc.Factory.CreateDbContext())
        {
            foreach (var map in SyncSchema.FromModel(db.Model).Tables) remoteRows += (long)CloudScalar($"SELECT COUNT(*) FROM \"{map.Table}\"")!;
        }

        _server.MaxResponseBytes = 1_000_000;
        var readsBefore = _server.BilledRowsRead;
        var tablet = CreateDevice("tablet", seed: false);
        var result = await tablet.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);

        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.AreEqual((long)CloudScalar("SELECT COUNT(*) FROM Patients")!, ChangeLogTestSupport.Count(db, "\"Patients\""));
        }
        Assert.IsTrue(_server.Requests.Any(r => r.Statements.Any(s => s.ErrorCode == "RESPONSE_TOO_LARGE")), "La página de 100 no cupo y se redujo");
        var read = _server.BilledRowsRead - readsBefore;
        Assert.IsLessThan(remoteRows * 2 + 100, read, $"~N lecturas: {read} para {remoteRows} filas");
    }

    [TestMethod]
    public async Task ADownloadCutHalfway_ContinuesWhereItWas()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        InsertCloudRows("Categories", 1000, "strftime('%Y-%m-%d %H:%M:%f', '2026-10-10', '+' || i || ' seconds')", ", Name", ", 'Categoría ' || i");
        var cloudCount = (long)CloudScalar("SELECT COUNT(*) FROM Categories")!;

        // Se cae la red al pedir la quinta página de categorías
        var tablet = CreateDevice("tablet", seed: false);
        tablet.Sync.PullPageSizeOverride = 100;
        int categoryPages = 0;
        _server.BeforeExecuteAsync = sql =>
        {
            if (sql.Contains("FROM \"Categories\"", StringComparison.Ordinal) && ++categoryPages == 5) throw new HttpRequestException("Se cortó la red (simulado)");
            return Task.CompletedTask;
        };
        Assert.IsFalse((await tablet.Sync.SyncAsync()).Success);
        long appliedBefore;
        using (var db = tablet.Factory.CreateDbContext()) appliedBefore = ChangeLogTestSupport.Count(db, "\"Categories\"");
        Assert.AreEqual(400L, appliedBefore, "Las cuatro páginas que llegaron quedaron aplicadas");

        _server.BeforeExecuteAsync = null;
        _server.ClearRecording();
        var resumed = await tablet.Sync.SyncAsync();
        Assert.IsTrue(resumed.Success, resumed.ErrorMessage);
        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.AreEqual(cloudCount, ChangeLogTestSupport.Count(db, "\"Categories\""));
        }
        var categoryReads = _server.Requests.SelectMany(r => r.Statements).Where(s => s.Sql.Contains("FROM \"Categories\"", StringComparison.Ordinal)).Sum(s => s.RowsRead);
        Assert.IsLessThanOrEqualTo(cloudCount - appliedBefore + 100, categoryReads, $"No se vuelve a leer lo ya aplicado ({categoryReads} lecturas)");
    }

    [TestMethod]
    public async Task PushIsAtomic_AFailingRowLeavesNothingOfItsTransaction()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var product = (await pc.Inventory.GetProductsAsync()).First(p => p.CurrentStock > 2);
        var sale = await pc.Inventory.RegisterSaleAsync(new[] { (product.Id, 1, product.Price) }, "Público General", "Efectivo");
        Assert.IsNotNull(sale);
        var id = TursoSyncService.FormatGuid(sale.SyncId);

        _server.FailWhen = sql => sql.StartsWith("INSERT INTO \"SaleItems\"", StringComparison.Ordinal);
        Assert.IsFalse((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(0L, CloudScalar($"SELECT COUNT(*) FROM Sales WHERE SyncId = '{id}'"), "La venta no queda arriba sin sus renglones");

        _server.FailWhen = null;
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(1L, CloudScalar($"SELECT COUNT(*) FROM Sales WHERE SyncId = '{id}'"));
        Assert.AreEqual(1L, CloudScalar($"SELECT COUNT(*) FROM SaleItems WHERE SaleSyncId = '{id}'"));
    }

    [TestMethod]
    public async Task ASaleGoesUpWithItsItems_InTheSameTransaction()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var products = (await pc.Inventory.GetProductsAsync()).Where(p => p.CurrentStock > 2).Take(2).ToList();
        Assert.IsNotNull(await pc.Inventory.RegisterSaleAsync(products.Select(p => (p.Id, 1, p.Price)).ToArray(), "Público General", "Efectivo"));

        pc.Sync.PushChunkSizeOverride = 2;
        _server.ClearRecording();
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        var request = _server.Requests.Single(r => r.Statements.Any(s => s.Sql.StartsWith("INSERT INTO \"Sales\"", StringComparison.Ordinal)));
        Assert.AreEqual("BEGIN", request.Statements[0].Sql);
        Assert.AreEqual(2, request.Statements.Count(s => s.Sql.StartsWith("INSERT INTO \"SaleItems\"", StringComparison.Ordinal)),
            "Los renglones viajan con su venta aunque el lote sea de 2");
    }

    [TestMethod]
    public async Task AfterUpdating_TheWatermarkInTheSettingsAvoidsAFullDownload()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Como una base de v1.20.3: sin cursores por tabla, con la marca de agua de la configuración
        using (var db = tablet.Factory.CreateDbContext()) DbMeta.RemoveByPrefix(db, DbMeta.PullCursorPrefix);
        var fullPulls = tablet.Settings.GetQuotaSummary().FullPullsToday;
        _server.ClearRecording();
        var result = await tablet.Sync.SyncAsync();

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(fullPulls, tablet.Settings.GetQuotaSummary().FullPullsToday, "No es otra descarga completa");
        Assert.AreEqual(1, _server.Requests.Count, "Una petición, como cualquier sincronización sin cambios");
        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.HasCount(SyncSchema.FromModel(db.Model).Tables.Count + 1, await DbMeta.GetByPrefixAsync(db, DbMeta.PullCursorPrefix), "Quedan los cursores por tabla");
        }
    }

    // ================================================================== hora e identidad de la nube (paso 8.7)

    private string? CloudMeta(string key) => CloudScalar($"SELECT Value FROM _tpvc_meta WHERE Key = '{key}'") as string;

    [TestMethod]
    [DataRow(10)]
    [DataRow(-10)]
    public async Task WithTheServerClockOff_LocalStampsFollowTheServer(int minutes)
    {
        _server.Clock = () => DateTime.UtcNow.AddMinutes(minutes);
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(minutes, (int)Math.Round(pc.Sync.ClockOffset!.Value.TotalMinutes));
        StringAssert.Contains(pc.Sync.ClockWarningText, $"difiere {Math.Abs(minutes)} min de la nube");

        // Una edición y un borrado aquí quedan marcados con la hora del servidor
        var product = (await pc.Inventory.GetProductsAsync()).First();
        product.Price += 1;
        await pc.Inventory.SaveProductAsync(product);
        var expected = DateTime.UtcNow.AddMinutes(minutes);
        var stamp = (await pc.Inventory.GetProductsAsync()).Single(p => p.Id == product.Id).UpdatedAtUtc;
        Assert.IsLessThan(TimeSpan.FromMinutes(1), (stamp - expected).Duration(), $"Marca {stamp:HH:mm:ss}, servidor {expected:HH:mm:ss}");

        using var db = pc.Factory.CreateDbContext();
        var category = new Category { Name = "Para borrar" };
        db.Categories.Add(category);
        db.SaveChanges();
        db.Database.ExecuteSqlRaw("DELETE FROM \"Categories\" WHERE \"Id\" = {0}", category.Id);
        var deletedAt = db.SyncTombstones.AsNoTracking().Single(t => t.SyncId == category.SyncId).DeletedAtUtc;
        Assert.IsLessThan(TimeSpan.FromMinutes(1), (deletedAt - expected).Duration(), "La lápida del trigger usa el mismo desfase");
    }

    [TestMethod]
    public async Task ARowStampedThreeHoursInTheFuture_IsRepairedBeforeGoingUp()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Un cambio guardado con el reloj adelantado 3 h: sin reparar ganaría todos los conflictos
        var patient = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Reloj Adelantado", Age = 40 });
        using (var db = pc.Factory.CreateDbContext())
        {
            db.Database.ExecuteSqlRaw("UPDATE \"Patients\" SET \"UpdatedAtUtc\" = {0} WHERE \"Id\" = {1}",
                TursoSyncService.FormatDate(DateTime.UtcNow.AddHours(3)), patient.Id);
        }

        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var limit = DateTime.UtcNow.AddMinutes(5);
        var cloudStamp = TursoSyncService.ParseDate(CloudScalar($"SELECT UpdatedAtUtc FROM Patients WHERE SyncId = '{TursoSyncService.FormatGuid(patient.SyncId)}'"));
        Assert.IsLessThan(limit, cloudStamp, "Subió con la hora del servidor");
        Assert.IsLessThan(limit, (await pc.Inventory.GetPatientsAsync()).Single(p => p.Id == patient.Id).UpdatedAtUtc, "Y aquí también quedó reparada");
    }

    [TestMethod]
    public async Task FutureStampsInTheCloud_AreRepairedOnce()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsNotNull(CloudMeta("stampsRepairedAtUtc"), "La primera sincronización ya revisó la nube");

        // Una nube con marcas en el futuro (de v1.19 o de un reloj adelantado) que todavía no se revisaba
        ExecuteCloud("DELETE FROM _tpvc_meta WHERE Key = 'stampsRepairedAtUtc'; " +
                     "UPDATE Products SET UpdatedAtUtc = '2099-01-01 00:00:00.0000000' WHERE rowid IN (SELECT rowid FROM Products LIMIT 3);");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(0L, CloudScalar("SELECT COUNT(*) FROM Products WHERE UpdatedAtUtc > '2090'"));
        Assert.IsNotNull(CloudMeta("stampsRepairedAtUtc"));

        _server.ClearRecording();
        var product = (await pc.Inventory.GetProductsAsync()).First();
        product.Price += 1;
        await pc.Inventory.SaveProductAsync(product);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsFalse(_server.Requests.SelectMany(r => r.Statements).Any(s => s.Sql.StartsWith(TursoSyncService.FullScanMarker + "UPDATE", StringComparison.Ordinal)),
            "Solo una vez por nube");
    }

    [TestMethod]
    public async Task TheFullSync_WritesTheCloudIdentity_AndItsManifestIsTheContract()
    {
        var pc = CreateDevice("pc", seed: true);
        var result = await pc.Sync.InitialUploadToTursoAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsNotNull(result.FullSyncReport);
        Assert.IsTrue(result.FullSyncReport.IsClean, result.FullSyncReport.Summary);

        var clinicId = CloudMeta("clinicId");
        Assert.IsNotNull(clinicId);
        Assert.AreEqual(clinicId, result.FullSyncReport.ClinicId);
        Assert.AreEqual("3", CloudMeta("protocol"), "La PC habla el protocolo 3 (cerca de generación, paso 8.9)");
        Assert.AreEqual("2", CloudMeta("minClientProtocol"), "Los equipos con el protocolo 2 siguen sincronizando hasta un reemplazo de la nube");
        Assert.IsNotNull(CloudMeta("generation"));
        Assert.AreEqual(CloudMeta("generation"), CloudMeta("generationReady"));
        Assert.IsNotNull(CloudMeta("fullUploadAtUtc"));
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.AreEqual(clinicId, DbMeta.Get(db, DbMeta.ClinicId), "La identidad queda también en la base");
            Assert.AreEqual(SyncContract.BuildSchemaJson(db.Model), CloudMeta("manifest"), "manifest = contracts/sync/schema.json");
        }

        // Una segunda sincronización completa no cambia la identidad
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        Assert.AreEqual(clinicId, CloudMeta("clinicId"));
    }

    [TestMethod]
    public async Task ARowWithoutItsPrincipal_KeepsTheFullSyncFromVerifyingTheCloud()
    {
        var pc = CreateDevice("pc", seed: true);
        using (var db = pc.Factory.CreateDbContext())
        {
            db.Database.OpenConnection();
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
            db.ProductBatches.Add(new ProductBatch { ProductId = 999_999, BatchNumber = "HUERFANO", ExpirationDate = DateTime.Today.AddYears(1), Quantity = 3 });
            db.SaveChanges();
        }

        var result = await pc.Sync.InitialUploadToTursoAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsFalse(result.FullSyncReport!.IsClean);
        Assert.AreEqual(1, result.FullSyncReport.ParkedRows);
        Assert.IsNull(CloudMeta("fullUploadAtUtc"), "Sin reporte limpio la nube no queda verificada");
        Assert.IsNull(CloudMeta("clinicId"));
        StringAssert.Contains(pc.Sync.StatusMessage, "con diferencias");
    }

    [TestMethod]
    public async Task ADatabaseWithItsOwnData_DoesNotSyncWithAnotherClinicsCloud()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        var cloudProducts = (long)CloudScalar("SELECT COUNT(*) FROM Products")!;

        // Otra instalación con sus propios datos y la URL y el token de esta nube
        var other = CreateDevice("otra", seed: true);
        var result = await other.Sync.SyncAsync();
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(other.Sync.IdentityConflict);
        StringAssert.Contains(result.ErrorMessage, "no se sincroniza para no mezclar datos");
        Assert.AreEqual(cloudProducts, (long)CloudScalar("SELECT COUNT(*) FROM Products")!, "No subió nada");

        // Lo automático ya no lo vuelve a intentar
        _server.ClearRecording();
        Assert.IsFalse((await other.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        Assert.IsEmpty(_server.Requests);

        // Y «Esta PC ya usaba esta nube» se niega: tiene registros que nunca llegaron a ella
        var adopt = await other.Sync.AdoptCloudIdentityAsync();
        Assert.IsFalse(adopt.Ok);
        StringAssert.Contains(adopt.Message, "no parece la misma");
        using var db = other.Factory.CreateDbContext();
        Assert.IsNull(DbMeta.Get(db, DbMeta.ClinicId));
    }

    [TestMethod]
    public async Task APcThatAlreadyUsedTheCloud_JoinsItsIdentity()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La PC verifica la nube después: la tablet no conoce todavía su identidad
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        Assert.IsFalse((await tablet.Sync.SyncAsync()).Success);

        var adopt = await tablet.Sync.AdoptCloudIdentityAsync();
        Assert.IsTrue(adopt.Ok, adopt.Message);
        Assert.IsNull(tablet.Sync.IdentityConflict);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        using var db = tablet.Factory.CreateDbContext();
        Assert.AreEqual(CloudMeta("clinicId"), DbMeta.Get(db, DbMeta.ClinicId));
    }

    [TestMethod]
    public async Task ACloudThatRequiresANewerProtocol_StopsTheSync()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        ExecuteCloud("INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('minClientProtocol', '4');");

        var product = (await pc.Inventory.GetProductsAsync()).First();
        product.Price = 777.5m;
        await pc.Inventory.SaveProductAsync(product);
        var result = await pc.Sync.SyncAsync();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "Actualice TPVC en esta computadora");
        Assert.AreNotEqual("777.5", CloudScalar($"SELECT Price FROM Products WHERE SyncId = '{TursoSyncService.FormatGuid(product.SyncId)}'"), "No subió nada");
    }

    [TestMethod]
    public async Task ANewColumn_DownloadsItsTable_AndDoesNotOverwriteTheCloudValue()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Un equipo más nuevo llenó en la nube una columna que esta versión de la tablet "no conocía"
        ExecuteCloud("UPDATE Patients SET Occupation = 'Docente';");
        using (var db = tablet.Factory.CreateDbContext())
        {
            // Como la dejó EnsureSchema al agregarla: con su valor por omisión, sin anotar nada para subir
            db.Database.ExecuteSqlRaw("INSERT INTO \"SyncApplyState\" (\"Applying\") VALUES (1); UPDATE \"Patients\" SET \"Occupation\" = ''; DELETE FROM \"SyncApplyState\";");
            var known = DbMeta.Get(db, DbMeta.PullColumnsPrefix + "Patients")!.Split(',').Where(c => c != "Occupation");
            DbMeta.Set(db, DbMeta.PullColumnsPrefix + "Patients", string.Join(",", known));
        }

        // Y aquí se edita un expediente (más nuevo aquí que en la nube)
        var patient = (await tablet.Inventory.GetPatientsAsync()).First();
        patient.Phone = "2221112233";
        await tablet.Inventory.SavePatientAsync(patient);

        var result = await tablet.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);

        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.IsTrue(db.Patients.AsNoTracking().All(p => p.Occupation == "Docente"), "La tabla se bajó completa con la columna nueva");
            StringAssert.Contains(DbMeta.Get(db, DbMeta.PullColumnsPrefix + "Patients"), "Occupation");
        }
        var id = TursoSyncService.FormatGuid(patient.SyncId);
        Assert.AreEqual("2221112233", CloudScalar($"SELECT Phone FROM Patients WHERE SyncId = '{id}'"), "La edición subió");
        Assert.AreEqual("Docente", CloudScalar($"SELECT Occupation FROM Patients WHERE SyncId = '{id}'"), "Sin pisar el valor de la nube");
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

    // ================================================================== siembra segura, unirse y recuperar (paso 8.8)

    /// <summary>Filas de cada tabla que sincroniza: aquí y en la nube.</summary>
    private Dictionary<string, (long Local, long Cloud)> CountsByTable(Device device)
    {
        using var db = device.Factory.CreateDbContext();
        return SyncSchema.FromModel(db.Model).Tables.ToDictionary(m => m.Table,
            m => (ChangeLogTestSupport.Count(db, $"\"{m.Table}\""), (long)CloudScalar($"SELECT COUNT(*) FROM \"{m.Table}\"")!));
    }

    private void AssertSameAsTheCloud(Device device)
    {
        foreach (var (table, (local, cloud)) in CountsByTable(device)) Assert.AreEqual(cloud, local, table);
        using var db = device.Factory.CreateDbContext();
        Assert.AreEqual(0, db.SyncParkedRows.Count(), "Nada quedó apartado");
    }

    private static string[] Backups(Device device, string label)
    {
        var dir = Path.Combine(Path.GetDirectoryName(device.DbPath)!, "backups");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, $"consultorio-*-{label}.db") : Array.Empty<string>();
    }

    /// <summary>Una nube con identidad (la «Sincronización completa» de la PC principal, con sus datos demo).</summary>
    private async Task<Device> CloudWithIdentityAsync()
    {
        var pc = CreateDevice("pc", seed: true);
        var upload = await pc.Sync.InitialUploadToTursoAsync();
        Assert.IsTrue(upload.Success, upload.ErrorMessage);
        Assert.IsNotNull(CloudMeta("clinicId"));
        return pc;
    }

    [TestMethod]
    public async Task TwoNewInstalls_SeedTheSameCatalog_AndSyncWithoutDuplicates()
    {
        var pc = CreateDevice("pc", seed: true, demo: false);
        var laptop = CreateDevice("laptop", seed: true, demo: false);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        Assert.AreEqual(5L, CloudScalar("SELECT COUNT(*) FROM Categories"));
        Assert.AreEqual(5L, CloudScalar("SELECT COUNT(*) FROM ClinicalServices"));
        foreach (var device in new[] { pc, laptop })
        {
            using var db = device.Factory.CreateDbContext();
            Assert.AreEqual(5, db.Categories.Count());
            Assert.AreEqual(5, db.ClinicalServices.Count());
            Assert.AreEqual(0, db.Patients.Count(), "Sin datos demo: esos solo salen del asistente");
            Assert.AreEqual(0, db.Products.Count());
            var category = db.Categories.AsNoTracking().Single(c => c.Name == "Material de Curación");
            Assert.AreEqual(SyncIds.Seeded("Category", "Material de Curación"), category.SyncId);
            Assert.AreEqual(DatabaseInitializer.SeedStampUtc, category.UpdatedAtUtc, "Sembrada con la marca del año 2000");
            Assert.AreEqual(SyncIds.Seeded("ClinicalService", "SERV-003"), db.ClinicalServices.AsNoTracking().Single(s => s.Code == "SERV-003").SyncId);
        }
    }

    [TestMethod]
    public void TheSeededCatalog_HasFixedIds_AlsoForTheDefaultProductsOfTheDemo()
    {
        var pc = CreateDevice("pc", seed: true);
        using var db = pc.Factory.CreateDbContext();
        var defaults = db.ServiceDefaultProducts.AsNoTracking().Include(d => d.ClinicalService).Include(d => d.Product).ToList();
        Assert.IsNotEmpty(defaults);
        foreach (var d in defaults)
        {
            Assert.AreEqual(SyncIds.Seeded("ServiceDefaultProduct", $"{d.ClinicalService!.Code}|{d.Product!.Code}"), d.SyncId);
            Assert.AreEqual(DatabaseInitializer.SeedStampUtc, d.UpdatedAtUtc);
        }
    }

    [TestMethod]
    public async Task ASeededCategoryThatWasDeleted_StaysDeleted_AfterAThirdInstallSyncs()
    {
        var pc = CreateDevice("pc", seed: true, demo: false);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var laptop = CreateDevice("laptop", seed: true, demo: false);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);

        // La PC borra una categoría sembrada; una tercera instalación la vuelve a sembrar con su marca del año 2000
        var deletedId = SyncIds.Seeded("Category", "Material de Curación");
        using (var db = pc.Factory.CreateDbContext())
        {
            db.Categories.Remove(db.Categories.Single(c => c.SyncId == deletedId));
            db.SaveChanges();
        }
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        var third = CreateDevice("tercera", seed: true, demo: false);
        Assert.IsTrue((await third.Sync.SyncAsync()).Success);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        Assert.AreEqual(0L, CloudScalar($"SELECT COUNT(*) FROM Categories WHERE SyncId = '{TursoSyncService.FormatGuid(deletedId)}'"), "La siembra repetida no la revive");
        Assert.AreEqual(4L, CloudScalar("SELECT COUNT(*) FROM Categories"));
        foreach (var device in new[] { pc, laptop, third })
        {
            using var db = device.Factory.CreateDbContext();
            Assert.AreEqual(4, db.Categories.Count(), device.Settings.CurrentSettings.DeviceName);
        }
    }

    [TestMethod]
    public void ASecondStartWithoutPatients_SeedsNothing()
    {
        var pc = CreateDevice("pc", seed: true, demo: false);
        using (var db = pc.Factory.CreateDbContext())
        {
            // El arranque del programa (App.xaml.cs), otra vez
            DatabaseInitializer.Initialize(db, seedDemoData: false);
        }

        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.AreEqual(0, db.Patients.Count(), "Antes, un arranque con la tabla vacía sembraba los pacientes demo");
            Assert.AreEqual(0, db.Products.Count());
            Assert.AreEqual(5, db.Categories.Count());
            Assert.AreEqual(5, db.ClinicalServices.Count());
        }
    }

    [TestMethod]
    public async Task JoiningACloud_CreatesNoDuplicates_AndUploadsNoTombstones()
    {
        var pc = await CloudWithIdentityAsync();
        var cloudBefore = CountsByTable(pc).ToDictionary(kv => kv.Key, kv => kv.Value.Cloud);
        var tombstonesBefore = (long)CloudScalar("SELECT COUNT(*) FROM SyncTombstones")!;

        // Una PC nueva (con su catálogo sembrado) y la URL y el token de esa nube: no sincroniza hasta unirse
        var tablet = CreateDevice("tablet", seed: true, demo: false);
        Assert.IsFalse((await tablet.Sync.SyncAsync()).Success);
        Assert.IsNotNull(tablet.Sync.IdentityConflict);

        var join = await tablet.Sync.JoinCloudAsync();
        Assert.IsTrue(join.Ok, join.Message);
        Assert.IsNull(tablet.Sync.IdentityConflict);
        Assert.HasCount(1, Backups(tablet, "antes-unirse-a-la-nube"), "Respalda antes de borrar");

        var sync = await tablet.Sync.SyncAsync();
        Assert.IsTrue(sync.Success, sync.ErrorMessage);
        Assert.AreEqual(0, sync.RowsPushed, "Unirse no sube nada");
        AssertSameAsTheCloud(tablet);
        foreach (var (table, (_, cloud)) in CountsByTable(tablet)) Assert.AreEqual(cloudBefore[table], cloud, $"Sin duplicados en {table}");
        Assert.AreEqual(tombstonesBefore, CloudScalar("SELECT COUNT(*) FROM SyncTombstones"), "Ni lápidas");

        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.AreEqual("1", DbMeta.Get(db, DbMeta.CloudJoined));
            Assert.AreEqual(CloudMeta("clinicId"), DbMeta.Get(db, DbMeta.ClinicId));
            Assert.IsNull(DbMeta.Get(db, DbMeta.CloudDownloadPending), "La descarga terminó");
            Assert.AreEqual(0, db.SyncTombstones.Count());
        }

        // Y de ahí en adelante sincroniza normal: lo nuevo sube
        var patient = await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente de la tablet", Age = 30 });
        var after = await tablet.Sync.SyncAsync();
        Assert.IsTrue(after.Success, after.ErrorMessage);
        Assert.AreEqual(1, after.RowsPushed);
        Assert.AreEqual(1L, CloudScalar($"SELECT COUNT(*) FROM Patients WHERE SyncId = '{TursoSyncService.FormatGuid(patient.SyncId)}'"));
    }

    [TestMethod]
    public async Task RecoveringFromTheCloud_GivesTheSameCounts_EvenWithTheSameDeviceId()
    {
        await CloudWithIdentityAsync();

        // La PC se descompuso: instalación nueva con la misma configuración (el mismo DeviceId que subió todo)
        var recovered = CreateDevice("pc", seed: true, demo: false);
        Assert.IsFalse((await recovered.Sync.SyncAsync()).Success);
        var join = await recovered.Sync.JoinCloudAsync();
        Assert.IsTrue(join.Ok, join.Message);
        var sync = await recovered.Sync.SyncAsync();
        Assert.IsTrue(sync.Success, sync.ErrorMessage);
        Assert.IsGreaterThan(0, sync.RowsPulled);

        AssertSameAsTheCloud(recovered);
        using var db = recovered.Factory.CreateDbContext();
        Assert.IsGreaterThan(0, db.Patients.Count(), "También lo que había subido este mismo equipo");
    }

    [TestMethod]
    public async Task ARestartHalfwayThroughTheDownload_DoesNotSeedAgain()
    {
        await CloudWithIdentityAsync();
        var cloudCategories = (long)CloudScalar("SELECT COUNT(*) FROM Categories")!;
        var tablet = CreateDevice("tablet", seed: true, demo: false);
        Assert.IsFalse((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.JoinCloudAsync()).Ok);

        // Se cae la red al pedir las categorías: la base queda vacía
        _server.BeforeExecuteAsync = sql => sql.Contains("FROM \"Categories\"", StringComparison.Ordinal)
            ? throw new HttpRequestException("Se cortó la red (simulado)")
            : Task.CompletedTask;
        Assert.IsFalse((await tablet.Sync.SyncAsync()).Success);
        _server.BeforeExecuteAsync = null;

        // Se cierra TPVC y vuelve a abrir: unida a la nube, ya no siembra nada
        using (var db = tablet.Factory.CreateDbContext())
        {
            DatabaseInitializer.Initialize(db, seedDemoData: false);
            DatabaseInitializer.SeedDemoData(db);
            Assert.AreEqual(0, db.Categories.Count());
            Assert.AreEqual(0, db.ClinicalServices.Count());
            Assert.AreEqual(0, db.Patients.Count(), "Ni los datos demo del asistente");
            Assert.AreEqual("1", DbMeta.Get(db, DbMeta.CloudDownloadPending), "La descarga sigue pendiente");
        }

        var resumed = await tablet.Sync.SyncAsync();
        Assert.IsTrue(resumed.Success, resumed.ErrorMessage);
        Assert.AreEqual(0, resumed.RowsPushed);
        AssertSameAsTheCloud(tablet);
        Assert.AreEqual(cloudCategories, CloudScalar("SELECT COUNT(*) FROM Categories"));
    }

    [TestMethod]
    public async Task ADatabaseWithRealData_IsRefusedToJoin()
    {
        await CloudWithIdentityAsync();
        var other = CreateDevice("otra", seed: true, demo: false);
        var patient = await other.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente de esta PC", Age = 52 });
        Assert.IsFalse((await other.Sync.SyncAsync()).Success);

        var join = await other.Sync.JoinCloudAsync();
        Assert.IsFalse(join.Ok);
        StringAssert.Contains(join.Message, "pacientes: 1");
        StringAssert.Contains(join.Message, "Reemplazar con la nube");
        Assert.IsEmpty(Backups(other, "antes-unirse-a-la-nube"));
        Assert.IsNotNull(other.Sync.IdentityConflict, "Sigue sin sincronizar");
        using var db = other.Factory.CreateDbContext();
        Assert.IsTrue(db.Patients.Any(p => p.Id == patient.Id), "No se borró nada");
        Assert.IsNull(DbMeta.Get(db, DbMeta.CloudJoined));
    }

    [TestMethod]
    public async Task ACustomCategory_AlsoKeepsTheDatabaseFromJoining()
    {
        await CloudWithIdentityAsync();
        var other = CreateDevice("otra", seed: true, demo: false);
        using (var db = other.Factory.CreateDbContext())
        {
            db.Categories.Add(new Category { Name = "Vacunas" });
            db.SaveChanges();
        }
        var join = await other.Sync.JoinCloudAsync();
        Assert.IsFalse(join.Ok);
        StringAssert.Contains(join.Message, "categorías propias: 1");
    }

    [TestMethod]
    public async Task ReplacingWithTheCloud_LeavesWhatTheCloudHas_WithABackupBefore()
    {
        await CloudWithIdentityAsync();
        var tombstonesBefore = (long)CloudScalar("SELECT COUNT(*) FROM SyncTombstones")!;

        // Otra base con sus propios datos (demo y un paciente que nunca subió)
        var other = CreateDevice("otra", seed: true);
        var patient = await other.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente que solo estaba aquí", Age = 61 });
        Assert.IsFalse((await other.Sync.SyncAsync()).Success);

        var replace = await other.Sync.ReplaceWithCloudAsync();
        Assert.IsTrue(replace.Ok, replace.Message);
        var sync = await other.Sync.SyncAsync();
        Assert.IsTrue(sync.Success, sync.ErrorMessage);
        Assert.AreEqual(0, sync.RowsPushed, "Lo de esta base no sube");

        AssertSameAsTheCloud(other);
        Assert.AreEqual(0L, CloudScalar($"SELECT COUNT(*) FROM Patients WHERE SyncId = '{TursoSyncService.FormatGuid(patient.SyncId)}'"));
        Assert.AreEqual(tombstonesBefore, CloudScalar("SELECT COUNT(*) FROM SyncTombstones"), "Borrar aquí no dejó lápidas");

        // El respaldo de antes conserva lo que había
        var backup = Backups(other, "antes-reemplazar-con-la-nube").Single();
        using var connection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"Patients\" WHERE upper(\"SyncId\") = upper($id)";
        command.Parameters.AddWithValue("$id", patient.SyncId.ToString());
        Assert.AreEqual(1L, (long)command.ExecuteScalar()!);
    }

    [TestMethod]
    public async Task JoiningACloudWithoutIdentity_IsRefused_WithoutChangingAnything()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: true, demo: false);

        var join = await tablet.Sync.JoinCloudAsync();
        Assert.IsFalse(join.Ok);
        StringAssert.Contains(join.Message, "todavía no tiene identidad");
        using var db = tablet.Factory.CreateDbContext();
        Assert.AreEqual(5, db.Categories.Count());
        Assert.IsNull(DbMeta.Get(db, DbMeta.CloudJoined));
    }

    // ================================================================== costo del producto (v1.21.0, contrato 2)

    [TestMethod]
    public async Task TheProductCost_TravelsBothWays()
    {
        var pc = CreateDevice("pc", seed: true);
        var product = (await pc.Inventory.GetProductsAsync()).First(p => p.CostExcludesVat);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var id = TursoSyncService.FormatGuid(product.SyncId);
        Assert.AreEqual(product.UnitCost.ToString(System.Globalization.CultureInfo.InvariantCulture), CloudScalar($"SELECT UnitCost FROM Products WHERE SyncId = '{id}'"));
        Assert.AreEqual(1L, CloudScalar($"SELECT CostExcludesVat FROM Products WHERE SyncId = '{id}'"));

        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var received = (await tablet.Inventory.GetProductsAsync()).Single(p => p.SyncId == product.SyncId);
        Assert.AreEqual(product.UnitCost, received.UnitCost);
        Assert.IsTrue(received.CostExcludesVat);

        received.UnitCost = 21.75m;
        received.CostExcludesVat = false;
        await tablet.Inventory.SaveProductAsync(received);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var back = (await pc.Inventory.GetProductsAsync()).Single(p => p.SyncId == product.SyncId);
        Assert.AreEqual(21.75m, back.UnitCost);
        Assert.IsFalse(back.CostExcludesVat);
    }

    [TestMethod]
    public async Task ACloudFromBeforeTheCost_GetsItsColumns_AndItsRowsArriveWithoutCost()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Como la dejó v1.20.x: sin las columnas del costo (las filas, sin costo) y con la huella de antes
        ExecuteCloud("ALTER TABLE Products DROP COLUMN UnitCost; ALTER TABLE Products DROP COLUMN CostExcludesVat; " +
                     "UPDATE _tpvc_meta SET Value = 'huella-v1.20' WHERE Key = 'schema'; UPDATE _tpvc_meta SET Value = '1' WHERE Key = 'contract';");

        var tablet = CreateDevice("tablet", seed: false);
        var result = await tablet.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);

        Assert.AreEqual(2L, CloudScalar("SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name IN ('UnitCost', 'CostExcludesVat')"), "La PC agrega las columnas");
        Assert.AreEqual(SyncContract.Revision.ToString(), CloudScalar("SELECT Value FROM _tpvc_meta WHERE Key = 'contract'"));
        var products = await tablet.Inventory.GetProductsAsync();
        Assert.IsNotEmpty(products);
        Assert.IsTrue(products.All(p => p.UnitCost == 0m && !p.CostExcludesVat), "Las filas de antes llegan sin costo");
    }
}
