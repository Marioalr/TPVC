using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Fase E: la consulta en curso pasa de un equipo a otro por la nube (servidor Turso simulado),
/// con un solo dueño a la vez, incluso si se pierden respuestas o dos equipos actúan al mismo tiempo.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConsultationHandoffTests
{
    private const string Url = "https://tpvc-handoff.turso.io";
    private readonly List<string> _files = new();
    private readonly List<string> _folders = new();
    private FakeHranaServer _server = null!;

    private sealed class Device
    {
        public required string Name { get; init; }
        public required IDbContextFactory<AppDbContext> Factory { get; init; }
        public required InventoryService Inventory { get; init; }
        public required TursoSettingsService Settings { get; init; }
        public required TursoSyncService Sync { get; init; }
        public required ConsultationHandoffService Handoff { get; init; }
        public required string DraftPath { get; init; }
    }

    private sealed class Station
    {
        public required Device Device { get; init; }
        public required MedicalRecordsViewModel Records { get; init; }
        public required ConsultationService Consultations { get; init; }
    }

    private string TempFile(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tpvc_handoff_{Guid.NewGuid():N}{ext}");
        _files.Add(path);
        return path;
    }

    [TestInitialize]
    public void Setup()
    {
        _server = new FakeHranaServer(TempFile(".remote.db"));
        DialogService.ConfirmHandler = (_, _) => true;
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
        foreach (var d in _folders)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
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
        s.AutoSyncOnStartup = false;
        s.DatabaseUrl = Url;
        s.AuthToken = "test-token";
        s.DeviceId = $"device-{name}";
        s.DeviceName = name == "pc" ? "PC del consultorio" : "Tablet del consultorio";
        settings.SaveSettings(s);

        var profile = new ClinicProfileService(TempFile(".profile.json"));
        var inventory = new InventoryService(factory);
        var sync = new TursoSyncService(factory, settings, profile, inventory)
        {
            ClientFactory = (url, token) => new TursoClient(url, token, new HttpClient(_server, disposeHandler: false))
        };
        var folder = Path.Combine(Path.GetTempPath(), $"tpvc_handoff_{name}_{Guid.NewGuid():N}");
        _folders.Add(folder);

        return new Device
        {
            Name = name,
            Factory = factory,
            Inventory = inventory,
            Settings = settings,
            Sync = sync,
            Handoff = new ConsultationHandoffService(sync, settings) { AutoPoll = false },
            DraftPath = Path.Combine(folder, "consulta-en-curso.json")
        };
    }

    private static async Task<Station> OpenAsync(Device device)
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(device.Inventory, consultations,
            consultationDraftStore: new ConsultationDraftStore(device.DraftPath),
            consultationHandoffService: device.Handoff)
        {
            AutosaveDelay = TimeSpan.FromMilliseconds(30),
            HandoffSyncLockWait = TimeSpan.FromSeconds(5)
        };
        await records.InitialLoad;
        return new Station { Device = device, Records = records, Consultations = consultations };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 20) await Task.Delay(20);
        Assert.IsTrue(condition());
    }

    /// <summary>PC con datos y tablet sincronizada; los dos registrados en la nube.</summary>
    private async Task<(Station Pc, Station Tablet)> TwoDevicesAsync()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        return (await OpenAsync(pc), await OpenAsync(tablet));
    }

    private async Task<(PatientRecord Patient, Product Product, ClinicalService Service)> StartConsultationAsync(Station a)
    {
        var patient = a.Records.Patients.First();
        var product = (await a.Device.Inventory.GetProductsAsync()).First(p => p.CurrentStock >= 5 && p.Price > 0);
        var service = (await a.Device.Inventory.GetClinicalServicesAsync()).First(s => s.Code != "SERV-001");

        a.Records.SelectPatient(patient);
        a.Records.StartActiveConsultation();
        a.Records.ConsultationChiefComplaint = "Fiebre de 3 días";
        a.Records.ConsultationTA = "130/85";
        a.Records.ConsultationDiagnosis = "J00 Rinofaringitis aguda";
        a.Records.ConsultationSection = 4;
        a.Consultations.AddCartItemToActiveConsultation(product.Id, product.Code, product.Name, product.GenericName, product.Presentation, product.Unit, product.Price, 2);
        a.Consultations.AddCartItemToActiveConsultation(-service.Id, service.Code, service.Name, null, null, "Servicio", service.Price, 1);
        a.Consultations.AddCartItemToActiveConsultation(-1_000_777, "LIBRE", "Curación especial", null, null, "Servicio", 150m, 1);
        return (patient, product, service);
    }

    private static async Task SendToAsync(Station from, Station to)
    {
        await from.Records.OpenSendConsultationSheetAsync();
        Assert.IsTrue(from.Records.IsSendConsultationSheetOpen);
        var target = from.Records.HandoffDevices.Single(d => d.DeviceId == to.Device.Settings.CurrentSettings.DeviceId);
        Assert.IsTrue(target.IsAvailable, target.StatusText);
        from.Records.SelectedHandoffDevice = target;
        await from.Records.SendConsultationToSelectedDeviceAsync();
    }

    private string? ServerState(string? id = null)
    {
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = id == null
            ? "SELECT State FROM ConsultationHandoffs ORDER BY CreatedAtUtc DESC LIMIT 1"
            : "SELECT State FROM ConsultationHandoffs WHERE Id = $id";
        if (id != null) cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar()?.ToString();
    }

    private string ServerScalar(string sql)
    {
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static int Owners(params Station[] stations) =>
        stations.Count(s => s.Consultations.HasActiveConsultation && s.Records.IsConsultationEditable);

    // ================================================================== camino feliz

    [TestMethod]
    public async Task Send_Accept_ReleasesSender_AndReceiverFinishesWithMappedIds()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        var (patient, product, service) = await StartConsultationAsync(pc);
        var folio = pc.Consultations.CurrentConsultation!.Folio;
        var startedAt = pc.Consultations.CurrentConsultation.StartedAt;

        await SendToAsync(pc, tablet);

        Assert.IsTrue(pc.Records.IsConsultationHandedOff, "En la PC queda en solo lectura");
        Assert.IsFalse(pc.Records.IsConsultationEditable);
        Assert.IsFalse(pc.Records.IsSendConsultationSheetOpen);
        Assert.AreEqual("enviada", ServerState());
        StringAssert.Contains(File.ReadAllText(pc.Device.DraftPath), "\"Handoff\"", "La marca de envío queda en el borrador local");
        Assert.IsFalse(pc.Consultations.AddCartItemToActiveConsultation(product.Id, product.Code, product.Name, null, null, "Piezas", 1m, 1),
            "Una consulta enviada no acepta cargos (tampoco desde el POS)");

        // La tablet la ve en su revisión y la toma
        await tablet.Records.PollHandoffsOnceAsync();
        Assert.IsNotNull(tablet.Records.IncomingHandoff);
        StringAssert.Contains(tablet.Records.IncomingHandoffTitle, "PC del consultorio");
        StringAssert.Contains(tablet.Records.IncomingHandoffDetail, patient.FullName);
        Assert.IsTrue(tablet.Records.CanAcceptIncomingHandoff);

        await tablet.Records.AcceptIncomingHandoffAsync();
        var received = tablet.Consultations.CurrentConsultation;
        Assert.IsNotNull(received, tablet.Records.Info.InfoModalMessage);
        Assert.IsTrue(tablet.Records.IsConsultationEditable);
        Assert.AreEqual(folio, received.Folio, "Mismo folio");
        Assert.AreEqual(startedAt, received.StartedAt, "Misma hora de inicio");
        Assert.AreEqual("Fiebre de 3 días", tablet.Records.ConsultationChiefComplaint);
        Assert.AreEqual("130/85", tablet.Records.ConsultationTA);
        Assert.AreEqual(4, tablet.Records.ConsultationSection);

        var tabletProduct = (await tablet.Device.Inventory.GetProductsAsync()).Single(p => p.SyncId == product.SyncId);
        var tabletService = (await tablet.Device.Inventory.GetClinicalServicesAsync()).Single(s => s.SyncId == service.SyncId);
        Assert.AreEqual(2, received.Items.Single(i => i.ProductId == tabletProduct.Id).Quantity, "El producto se traduce al Id de la tablet");
        Assert.IsTrue(received.Items.Any(i => i.ProductId == -tabletService.Id), "El servicio se traduce al Id de la tablet");
        Assert.IsTrue(received.Items.Any(i => i.Name == "Curación especial" && i.ProductId <= -1_000_000), "El cargo libre sigue libre");
        Assert.AreEqual(tablet.Records.Patients.Single(p => p.SyncId == patient.SyncId).Id, received.PatientId);
        Assert.IsTrue(File.Exists(tablet.Device.DraftPath), "La tablet ya tiene su copia local");
        Assert.AreEqual("aceptada", ServerState());
        await WaitUntilAsync(() => ServerScalar("SELECT Payload FROM ConsultationHandoffs") == string.Empty);

        // La PC se entera en su revisión y se cierra SIN descartar
        await pc.Records.PollHandoffsOnceAsync();
        Assert.IsFalse(pc.Consultations.HasActiveConsultation, pc.Records.Info.InfoModalMessage);
        Assert.IsFalse(pc.Records.IsConsultationHandedOff);
        Assert.IsFalse(File.Exists(pc.Device.DraftPath), "La PC ya no la recupera al abrir");
        using (var db = pc.Device.Factory.CreateDbContext())
        {
            Assert.AreEqual(0, db.SyncTombstones.Count(t => t.EntityType == nameof(PatientConsultation)), "Liberar no borra notas");
        }
        await WaitUntilAsync(() => ServerScalar("SELECT COUNT(*) FROM ConsultationHandoffs WHERE SenderAckAtUtc IS NOT NULL") == "1");

        // La tablet la termina y la cobra
        await tablet.Records.FinalizeConsultationAsync();
        tablet.Records.Checkout.SetExactCash();
        await tablet.Records.Checkout.ChargeAsync();
        await WaitUntilAsync(() => !tablet.Consultations.HasActiveConsultation);

        Assert.IsTrue((await tablet.Device.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Device.Sync.SyncAsync()).Success);
        var notes = await pc.Device.Inventory.GetPatientConsultationsAsync(patient.Id);
        Assert.AreEqual(1, notes.Count(n => n.Folio == folio), "Una sola nota con ese folio en la PC");
        Assert.AreEqual("J00 Rinofaringitis aguda", notes.Single(n => n.Folio == folio).Diagnosis);
        var pcProduct = (await pc.Device.Inventory.GetProductsAsync()).Single(p => p.Id == product.Id);
        Assert.AreEqual(product.CurrentStock - 2, pcProduct.CurrentStock, "El cobro de la tablet descontó existencias que llegan a la PC");
    }

    // ================================================================== recuperar

    [TestMethod]
    public async Task Recover_BeforeAccept_MakesItEditableAgain_AndAStaleAcceptLoses()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);
        await tablet.Records.PollHandoffsOnceAsync();
        Assert.IsNotNull(tablet.Records.IncomingHandoff);

        await pc.Records.RecoverHandedOffConsultationAsync();
        Assert.IsTrue(pc.Records.IsConsultationEditable, "Vuelve a editarse en la PC");
        Assert.IsFalse(pc.Records.IsConsultationHandedOff);
        Assert.AreEqual("cancelada", ServerState());
        Assert.IsFalse(File.ReadAllText(pc.Device.DraftPath).Contains("\"Handoff\":{"), "El borrador ya no lleva la marca");
        Assert.IsTrue(pc.Consultations.AddCartItemToActiveConsultation(-1_000_001, "X", "Otro cargo", null, null, "Servicio", 10m, 1));

        // La tablet todavía muestra el aviso (no ha revisado): tomarla ya no funciona
        await tablet.Records.AcceptIncomingHandoffAsync();
        Assert.IsFalse(tablet.Consultations.HasActiveConsultation);
        StringAssert.Contains(tablet.Records.Info.InfoModalMessage, "recuperó");
        Assert.AreEqual(1, Owners(pc, tablet));

        await tablet.Records.PollHandoffsOnceAsync();
        Assert.IsNull(tablet.Records.IncomingHandoff, "El aviso desaparece");
    }

    [TestMethod]
    public async Task AcceptAndRecoverAtTheSameTime_ExactlyOneOwner()
    {
        foreach (var acceptFirst in new[] { true, false })
        {
            var (pc, tablet) = await TwoDevicesAsync();
            await StartConsultationAsync(pc);
            await SendToAsync(pc, tablet);
            await tablet.Records.PollHandoffsOnceAsync();

            // Se detiene la instrucción del que debe llegar segundo hasta que el otro termine
            var hold = new TaskCompletionSource();
            var holdMarker = acceptFirst ? "State = 'cancelada', Payload = ''" : "AcceptToken = ?, UpdatedAtUtc";
            _server.BeforeExecuteAsync = async body =>
            {
                if (body.Contains(holdMarker)) await hold.Task;
            };

            var accept = tablet.Records.AcceptIncomingHandoffAsync();
            var recover = pc.Records.RecoverHandedOffConsultationAsync();
            var first = acceptFirst ? accept : recover;
            await first;
            hold.SetResult();
            await Task.WhenAll(accept, recover);
            _server.BeforeExecuteAsync = null;
            await pc.Records.PollHandoffsOnceAsync();

            Assert.AreEqual(1, Owners(pc, tablet), $"Un solo dueño (acepta primero: {acceptFirst})");
            if (acceptFirst)
            {
                Assert.IsTrue(tablet.Consultations.HasActiveConsultation);
                Assert.IsFalse(pc.Consultations.HasActiveConsultation, "La PC se cierra: la tablet ganó");
                Assert.AreEqual("aceptada", ServerState());
            }
            else
            {
                Assert.IsTrue(pc.Records.IsConsultationEditable);
                Assert.IsFalse(tablet.Consultations.HasActiveConsultation);
                Assert.AreEqual("cancelada", ServerState());
            }
            Cleanup();
            Setup();
        }
    }

    // ================================================================== respuestas perdidas

    [TestMethod]
    public async Task AcceptResponseLost_TheReceiverStillOpensIt()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);
        await tablet.Records.PollHandoffsOnceAsync();

        // La nube aplica "aceptada" pero la respuesta no llega
        _server.DropResponseAfterExecute = body => body.Contains("AcceptToken = ?, UpdatedAtUtc");
        await tablet.Records.AcceptIncomingHandoffAsync();
        _server.DropResponseAfterExecute = null;

        Assert.IsTrue(tablet.Consultations.HasActiveConsultation, "Pregunta a la nube con su ficha y la abre");
        await pc.Records.PollHandoffsOnceAsync();
        Assert.IsFalse(pc.Consultations.HasActiveConsultation);
        Assert.AreEqual(1, Owners(pc, tablet));
    }

    [TestMethod]
    public async Task AcceptLostAndOffline_IsOfferedAgainWhenTheConnectionReturns()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);
        await tablet.Records.PollHandoffsOnceAsync();

        // Se aplica, se pierde la respuesta y además ya no hay red para preguntar
        _server.DropResponseAfterExecute = body =>
        {
            if (!body.Contains("AcceptToken = ?, UpdatedAtUtc")) return false;
            _server.IsOffline = true;
            return true;
        };
        await tablet.Records.AcceptIncomingHandoffAsync();
        _server.DropResponseAfterExecute = null;
        Assert.IsFalse(tablet.Consultations.HasActiveConsultation);
        StringAssert.Contains(tablet.Records.Info.InfoModalMessage, "Sin conexión");

        // La PC ve "aceptada" y se libera; la consulta no se pierde: la copia sigue en la nube
        _server.IsOffline = false;
        await pc.Records.PollHandoffsOnceAsync();
        Assert.IsFalse(pc.Consultations.HasActiveConsultation);
        Assert.AreNotEqual(string.Empty, ServerScalar("SELECT Payload FROM ConsultationHandoffs"));

        await tablet.Records.PollHandoffsOnceAsync();
        Assert.IsTrue(tablet.Records.IsIncomingHandoffAccepted, "Se ofrece para abrirla");
        StringAssert.Contains(tablet.Records.IncomingHandoffTitle, "pendiente de abrir");
        await tablet.Records.AcceptIncomingHandoffAsync();
        Assert.IsTrue(tablet.Consultations.HasActiveConsultation);
        Assert.AreEqual("Fiebre de 3 días", tablet.Records.ConsultationChiefComplaint);
    }

    [TestMethod]
    public async Task RetryingAfterALostAcceptResponse_KeepsTheFirstTokenAndOpensIt()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);
        await tablet.Records.PollHandoffsOnceAsync();

        // Se toma, se pierde la respuesta y tampoco se puede preguntar
        _server.DropResponseAfterExecute = sql =>
        {
            if (!sql.Contains("AcceptToken = ?, UpdatedAtUtc")) return false;
            _server.IsOffline = true;
            return true;
        };
        await tablet.Records.AcceptIncomingHandoffAsync();
        _server.DropResponseAfterExecute = null;
        _server.IsOffline = false;
        Assert.IsFalse(tablet.Consultations.HasActiveConsultation);

        // El médico vuelve a pulsar "Continuar aquí" con el aviso de antes (sin revisar otra vez):
        // la ficha del primer intento no se reemplaza y la consulta se abre aquí
        Assert.IsNotNull(tablet.Records.IncomingHandoff);
        await tablet.Records.AcceptIncomingHandoffAsync();
        Assert.IsTrue(tablet.Consultations.HasActiveConsultation, tablet.Records.Info.InfoModalMessage);
        await pc.Records.PollHandoffsOnceAsync();
        Assert.IsFalse(pc.Consultations.HasActiveConsultation);
        Assert.AreEqual(1, Owners(pc, tablet));
    }

    [TestMethod]
    public async Task ConfirmedSendWhoseRowDisappears_IsNeverUnlockedAutomatically()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);

        // P. ej. se restauró la base de la nube: no se sabe dónde siguió la consulta
        using (_server.Lock())
        using (var cmd = _server.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM ConsultationHandoffs";
            cmd.ExecuteNonQuery();
        }
        await pc.Records.PollHandoffsOnceAsync();
        Assert.IsTrue(pc.Records.IsConsultationHandedOff, "Sigue en solo lectura");
        Assert.IsTrue(pc.Records.CanReleaseHandoffLocally);
        StringAssert.Contains(pc.Records.HandoffBannerDetail, "No se encontró");
    }

    [TestMethod]
    public async Task SendResponseLost_IsResolvedAgainstTheCloud_AndNeverLeavesTwoOwners()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);

        // Solo el envío (la resolución también inserta, pero con ON CONFLICT)
        _server.DropResponseAfterExecute = sql => sql.Contains("INSERT INTO ConsultationHandoffs") && !sql.Contains("ON CONFLICT");
        await SendToAsync(pc, tablet);
        _server.DropResponseAfterExecute = null;

        // El renglón llegó, pero la PC no lo supo: lo cancela antes de volver a editarla
        Assert.IsTrue(pc.Records.IsConsultationEditable, pc.Records.Info.InfoModalMessage);
        Assert.AreEqual("cancelada", ServerState());
        await tablet.Records.PollHandoffsOnceAsync();
        Assert.IsNull(tablet.Records.IncomingHandoff);
        Assert.AreEqual(1, Owners(pc, tablet));
    }

    [TestMethod]
    public async Task SendWhileOffline_KeepsTheConsultationHere()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await pc.Records.OpenSendConsultationSheetAsync();
        pc.Records.SelectedHandoffDevice = pc.Records.HandoffDevices.Single();

        _server.IsOffline = true;
        await pc.Records.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(pc.Records.IsConsultationEditable);
        Assert.IsTrue(pc.Records.IsSendConsultationSheetOpen);
        StringAssert.Contains(pc.Records.HandoffSheetError, "No se pudo subir");
        Assert.AreEqual("0", ServerScalar("SELECT COUNT(*) FROM ConsultationHandoffs"));

        await pc.Records.PollHandoffsOnceAsync();
        Assert.IsFalse(pc.Records.CanSendConsultation, "Sin conexión el botón se desactiva");
        StringAssert.Contains(pc.Records.SendConsultationHint, "Sin conexión");
        _server.IsOffline = false;
        await pc.Records.PollHandoffsOnceAsync();
        Assert.IsTrue(pc.Records.CanSendConsultation);
    }

    [TestMethod]
    public async Task RecoverWhileOffline_StaysReadOnly_AndCanReleaseThisDevice()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);

        _server.IsOffline = true;
        await pc.Records.RecoverHandedOffConsultationAsync();
        Assert.IsTrue(pc.Records.IsConsultationHandedOff, "Sin confirmar con la nube no vuelve a ser editable");
        Assert.IsTrue(pc.Records.CanReleaseHandoffLocally);
        StringAssert.Contains(pc.Records.HandoffBannerDetail, "No se pudo recuperar");

        await pc.Records.ReleaseHandoffLocallyAsync();
        Assert.IsFalse(pc.Consultations.HasActiveConsultation, "El equipo queda libre para otra consulta");
        Assert.IsTrue(Directory.GetFiles(Path.GetDirectoryName(pc.Device.DraftPath)!, "consulta-enviada-*.json").Any(), "La copia se aparta");

        // Si la tablet la acepta después, sigue allá
        _server.IsOffline = false;
        await tablet.Records.PollHandoffsOnceAsync();
        await tablet.Records.AcceptIncomingHandoffAsync();
        Assert.IsTrue(tablet.Consultations.HasActiveConsultation);
    }

    // ================================================================== reinicios y equipo ocupado

    [TestMethod]
    public async Task SenderRestart_WhileSent_ComesBackReadOnly_AndIsReleasedOnAccept()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);

        // Cerrar el programa (guarda la copia al salir) y volver a abrirlo
        pc.Records.Dispose();
        var reopened = await OpenAsync(pc.Device);
        Assert.IsTrue(reopened.Consultations.HasActiveConsultation);
        Assert.IsTrue(reopened.Records.IsConsultationHandedOff, "Vuelve en solo lectura, no editable");
        Assert.IsFalse(reopened.Records.IsConsultationEditable);
        StringAssert.Contains(reopened.Records.HandoffBannerText, "Tablet del consultorio");

        await tablet.Records.PollHandoffsOnceAsync();
        await tablet.Records.AcceptIncomingHandoffAsync();
        await reopened.Records.PollHandoffsOnceAsync();
        Assert.IsFalse(reopened.Consultations.HasActiveConsultation);
        Assert.AreEqual(1, Owners(reopened, tablet));
    }

    [TestMethod]
    public async Task ReceiverWithItsOwnConsultation_CannotAcceptUntilItFinishes()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);

        tablet.Records.SelectPatient(tablet.Records.Patients.Last());
        tablet.Records.StartActiveConsultation();
        await tablet.Records.PollHandoffsOnceAsync();
        Assert.IsNotNull(tablet.Records.IncomingHandoff);
        Assert.IsFalse(tablet.Records.CanAcceptIncomingHandoff);
        StringAssert.Contains(tablet.Records.IncomingHandoffBlockedReason, "Termine o descarte");

        var own = tablet.Consultations.CurrentConsultation!.Folio;
        await tablet.Records.AcceptIncomingHandoffAsync();
        Assert.AreEqual(own, tablet.Consultations.CurrentConsultation!.Folio, "La consulta propia no se reemplaza");
        Assert.AreEqual("enviada", ServerState());
    }

    [TestMethod]
    public async Task ProductMissingOnReceiver_IsChargedAsFreeLine_WithAWarning()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        var (_, product, _) = await StartConsultationAsync(pc);

        // En la tablet ese producto está dado de baja: no se adivina otro ni se descuenta de él
        using (var db = tablet.Device.Factory.CreateDbContext())
        {
            db.ApplyingRemoteChanges = true;
            db.Products.Single(p => p.SyncId == product.SyncId).IsActive = false;
            db.SaveChanges();
        }

        await SendToAsync(pc, tablet);
        await tablet.Records.PollHandoffsOnceAsync();
        await tablet.Records.AcceptIncomingHandoffAsync();

        var line = tablet.Consultations.CurrentConsultation!.Items.Single(i => i.Name == product.Name);
        Assert.IsTrue(line.ProductId <= -1_000_000, "Se cobra como cargo libre (sin descontar existencias)");
        StringAssert.Contains(tablet.Records.Info.InfoModalMessage, product.Name);
    }

    [TestMethod]
    public async Task ReadOnlyConsultation_BlocksEditsAndPatientChanges()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        var (patient, _, _) = await StartConsultationAsync(pc);
        await SendToAsync(pc, tablet);

        var items = pc.Consultations.CurrentConsultation!.Items.Count;
        await pc.Records.FinalizeConsultationAsync();
        Assert.IsFalse(pc.Records.IsFinishConsultationOpen, "No se puede terminar aquí");
        pc.Records.Account.RemoveItemFromActiveConsultation(pc.Consultations.CurrentConsultation.Items.First());
        Assert.AreEqual(items, pc.Consultations.CurrentConsultation.Items.Count);
        await pc.Records.DiscardActiveConsultationAsync();
        Assert.IsTrue(pc.Consultations.HasActiveConsultation, "Tampoco se descarta");

        pc.Records.PatientForm.EditPatient();
        Assert.IsFalse(pc.Records.PatientForm.IsPatientModalOpen, "El expediente se edita en el otro equipo");
        StringAssert.Contains(pc.Records.Info.InfoModalMessage, patient.FullName);
    }

    [TestMethod]
    public async Task ReadOnlyConsultation_DoesNotAddOrRemoveSuppliesAndServices()
    {
        var (pc, tablet) = await TwoDevicesAsync();
        var (_, product, service) = await StartConsultationAsync(pc);
        var records = pc.Records;

        // Agregados antes de enviarla (se cargan a la cuenta)
        await records.Supplies.SelectAndAddSupplyProductAsync(records.Catalog.AvailableProducts.Single(p => p.Id == product.Id));
        await records.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(records.Catalog.Services.Single(s => s.Id == service.Id));

        await SendToAsync(pc, tablet);
        Assert.IsTrue(records.IsConsultationHandedOff);

        var patient = records.SelectedPatient!;
        var supply = patient.ActiveSupplies.Last();
        var serviceItem = patient.ActiveServices.Last();
        var supplies = patient.ActiveSupplies.ToList();
        var services = patient.ActiveServices.ToList();
        var charges = pc.Consultations.CurrentConsultation!.Items.Select(i => $"{i.ProductId}x{i.Quantity}").ToList();

        await records.Supplies.SelectAndAddSupplyProductAsync(records.Catalog.AvailableProducts.Single(p => p.Id == product.Id));
        records.Supplies.SupplySearchText = "Férula de aluminio";
        await records.Supplies.SelectAndAddSupplyProductAsync(null);
        await records.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(records.Catalog.Services.Single(s => s.Id == service.Id));
        await records.Supplies.RemoveSupplyItemFromPatientAsync(supply);
        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(serviceItem);

        Assert.AreSame(patient, records.SelectedPatient);
        CollectionAssert.AreEqual(supplies, patient.ActiveSupplies, "La hoja de insumos no cambia en solo lectura");
        CollectionAssert.AreEqual(services, patient.ActiveServices, "La hoja de servicios no cambia en solo lectura");
        CollectionAssert.AreEqual(charges, pc.Consultations.CurrentConsultation.Items.Select(i => $"{i.ProductId}x{i.Quantity}").ToList(), "Ni la cuenta");
        StringAssert.Contains(records.Info.InfoModalMessage, "solo lectura");

        // Guardar las hojas tampoco escribe el expediente
        records.Info.InfoModalMessage = null;
        await records.Supplies.SaveSuppliesModalAsync();
        StringAssert.Contains(records.Info.InfoModalMessage, "solo lectura");
        records.Info.InfoModalMessage = null;
        await records.ClinicalServices.SaveServicesModalAsync();
        StringAssert.Contains(records.Info.InfoModalMessage, "solo lectura");
    }

    // ================================================================== sincronización con relojes distintos

    [TestMethod]
    public async Task EditAfterARowFromAFastClock_StillWins_AndIsPushed()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Una versión que llegó de un equipo con el reloj 10 min adelantado
        var future = DateTime.UtcNow.AddMinutes(10);
        int patientId;
        using (var db = pc.Factory.CreateDbContext())
        {
            db.ApplyingRemoteChanges = true;
            var patient = db.Patients.First();
            patientId = patient.Id;
            patient.UpdatedAtUtc = future;
            db.SaveChanges();
        }

        var edited = (await pc.Inventory.GetPatientByIdAsync(patientId))!;
        edited.Allergies = "Penicilina";
        await pc.Inventory.SavePatientAsync(edited);
        Assert.IsTrue(edited.UpdatedAtUtc > future, "La marca queda después de la que ya tenía");

        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = "SELECT Allergies FROM Patients WHERE SyncId = $id";
        cmd.Parameters.AddWithValue("$id", TursoSyncService.FormatGuid(edited.SyncId));
        Assert.AreEqual("Penicilina", cmd.ExecuteScalar()?.ToString(), "Sube aunque su marca sea posterior al corte");
    }

    [TestMethod]
    public async Task RowFromAFastClock_DeletedElsewhere_IsNotRevivedByTheNextPush()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La tablet (reloj 10 min adelantado) guarda un avance de nota y lo sube
        var tabletPatient = (await tablet.Inventory.GetPatientSummariesAsync()).First();
        await tablet.Inventory.AddConsultationNoteAsync(tabletPatient.Id,
            new PatientConsultation { Folio = "CNS-RELOJ-1", Date = DateTime.Now, ChiefComplaint = "Tos", Diagnosis = "J00" });
        using (var db = tablet.Factory.CreateDbContext())
        {
            db.ApplyingRemoteChanges = true;
            db.PatientConsultations.Single(c => c.Folio == "CNS-RELOJ-1").UpdatedAtUtc = DateTime.UtcNow.AddMinutes(10);
            db.SaveChanges();
        }
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual("1", ServerScalar("SELECT COUNT(*) FROM PatientConsultations WHERE Folio = 'CNS-RELOJ-1'"));

        // La tablet la descarta (borrado) y lo sube; la PC, que aún la tiene, sincroniza después
        Assert.IsTrue(await tablet.Inventory.DeleteConsultationNoteAsync(tabletPatient.Id, "CNS-RELOJ-1"));
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.AreEqual("0", ServerScalar("SELECT COUNT(*) FROM PatientConsultations WHERE Folio = 'CNS-RELOJ-1'"));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual("0", ServerScalar("SELECT COUNT(*) FROM PatientConsultations WHERE Folio = 'CNS-RELOJ-1'"), "La subida de la PC no la revive");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        Assert.AreEqual("0", ServerScalar("SELECT COUNT(*) FROM PatientConsultations WHERE Folio = 'CNS-RELOJ-1'"));
        using (var db = pc.Factory.CreateDbContext()) Assert.AreEqual(0, db.PatientConsultations.Count(c => c.Folio == "CNS-RELOJ-1"));
        using (var db = tablet.Factory.CreateDbContext()) Assert.AreEqual(0, db.PatientConsultations.Count(c => c.Folio == "CNS-RELOJ-1"));
    }

    [TestMethod]
    public void ErrorClassification_OnlyRejectionsAreDefinitive()
    {
        Assert.IsTrue(ConsultationHandoffService.IsDefinitiveRejection(new TursoException("x", "HTTP_401")));
        Assert.IsTrue(ConsultationHandoffService.IsDefinitiveRejection(new TursoException("no such column", "SQLITE_ERROR")));
        Assert.IsFalse(ConsultationHandoffService.IsDefinitiveRejection(new TursoException("UNIQUE constraint failed", "SQLITE_CONSTRAINT")));
        Assert.IsFalse(ConsultationHandoffService.IsDefinitiveRejection(new TursoException("x", "NETWORK_ERROR")));
        Assert.IsFalse(ConsultationHandoffService.IsDefinitiveRejection(new TursoException("x", "HTTP_503")));
        Assert.IsFalse(ConsultationHandoffService.IsDefinitiveRejection(new OperationCanceledException()));
        Assert.IsTrue(ConsultationHandoffService.IsDefinitiveRejection(new HandoffNotSentException(new HttpRequestException("x"))));
        Assert.IsTrue(ConsultationHandoffService.IsConnectivityFailure(new HttpRequestException("x")));
    }
}
