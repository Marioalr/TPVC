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

    private Device CreateDevice(string name, bool seed, TimeProvider? clock = null)
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
        if (clock != null) settings.Clock = clock;
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
        if (clock != null) sync.Clock = clock;

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
