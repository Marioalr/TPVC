using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Folios;
using TPVC.Services.Rules;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.23 (H2, H3, H11) con dos y tres equipos sobre el servidor simulado: las citas empalmadas y los expedientes
/// duplicados que llegan de otro equipo (la marca "Empalme", el aviso y el banner), la sincronización rápida antes de cobrar y
/// de agendar (3 s como máximo, sin red no espera, sin ciclos, freno y cuota agotada), cobrar sin conexión con el folio con
/// letra y <c>MergedIntoSyncId</c> como un campo más del expediente.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    /// <summary>Pacientes del equipo como los ve la agenda, con su avisos (la notificación de la esquina en lugar de la ventana).</summary>
    private static async Task<(MedicalRecordsViewModel Records, NotificationService Notifications)> OpenRecordsAsync(Device device)
    {
        var notifications = new NotificationService(uiContext: null);
        var records = new MedicalRecordsViewModel(device.Inventory, new ConsultationService(seedDemoPatients: false), notificationService: notifications);
        await records.InitialLoad;
        return (records, notifications);
    }

    private static async Task<PatientAppointment> ScheduleAsync(Device device, PatientRecord patient, DateTime day, string time, int minutes = 30) =>
        await device.Inventory.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id, Date = day, Time = time, DurationMinutes = minutes, Reason = "Control", Status = "Programada"
        });

    private static async Task<List<AgendaSlot>> AgendaOfAsync(Device device, DateTime day) =>
        AgendaPanelViewModel.BuildSlots(day, await device.Inventory.GetAppointmentsByWeekAsync(day), now: day.AddDays(-1))
            .Where(s => s.HasAppointment).ToList();

    // ================================================================== empalmes que llegan de otro equipo

    [TestMethod]
    public async Task TwoDevicesScheduleOverlappingAppointmentsOffline_BothShowEmpalme_AfterSyncing()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        var (records, notifications) = await OpenRecordsAsync(pc);
        var patients = await pc.Inventory.GetPatientsAsync();
        var ana = patients[0];
        var luis = await PatientBySyncIdAsync(tablet, patients[1].SyncId);
        var day = DateTime.Today.AddDays(2);

        _server.IsOffline = true;
        var onPc = await ScheduleAsync(pc, ana, day, "10:30");
        var onTablet = await ScheduleAsync(tablet, luis, day, "10:45");
        _server.IsOffline = false;

        var pcUpload = await pc.Sync.SyncAsync();
        Assert.IsTrue(pcUpload.Success, pcUpload.ErrorMessage);
        Assert.IsTrue(pcUpload.Arrivals.IsEmpty, "La PC todavía no recibe la cita de la tablet");

        var tabletSync = await tablet.Sync.SyncAsync();
        Assert.IsTrue(tabletSync.Success, tabletSync.ErrorMessage);
        var onTabletSide = tabletSync.Arrivals.Overlaps.Single();
        CollectionAssert.AreEquivalent(new[] { onPc.SyncId, onTablet.SyncId }, new[] { onTabletSide.First.SyncId, onTabletSide.Second.SyncId });

        var pcSync = await pc.Sync.SyncAsync();
        Assert.IsTrue(pcSync.Success, pcSync.ErrorMessage);
        var overlap = pcSync.Arrivals.Overlaps.Single();
        Assert.AreEqual(onPc.SyncId, overlap.First.SyncId, "La primera por hora");
        Assert.AreEqual(onTablet.SyncId, overlap.Second.SyncId);

        // El aviso en la PC (se queda hasta «Entendido», también al reiniciar) y la marca "Empalme" en la agenda de los dos
        // equipos; ninguna cita se canceló
        await WaitUntilAsync(() => records.Home.HasOverlapNotice);
        StringAssert.StartsWith(records.Home.OverlapNoticeText, "Empalme en la agenda con lo que llegó de otro equipo: ");
        StringAssert.Contains(records.Home.OverlapNoticeText, $"{ana.FullName} (10:30) y {luis.FullName} (10:45)");
        Assert.IsFalse(notifications.Items.Any(n => n.Kind == NotificationKind.Warning), "Ya no es una nota que se va sola");
        var (restarted, _) = await OpenRecordsAsync(pc);
        Assert.IsTrue(restarted.Home.HasOverlapNotice, "Sigue al cerrar y abrir TPVC");
        await restarted.ShowOverlapsInAgendaAsync();
        Assert.IsTrue(restarted.Agenda.IsAgendaPanelOpen, "«Ver en la agenda» la abre");
        Assert.AreEqual(day, restarted.Agenda.AgendaPanelDate, "en el día del empalme");
        foreach (var device in new[] { pc, tablet })
        {
            var slots = await AgendaOfAsync(device, day);
            Assert.HasCount(2, slots);
            Assert.IsTrue(slots.All(s => s.IsOverlapping), "Las dos llevan la marca");
            Assert.AreEqual($"Empalme con {luis.FullName} (10:45)", slots.Single(s => s.Appointment!.SyncId == onPc.SyncId).OverlapText);
            Assert.AreEqual($"Empalme con {ana.FullName} (10:30)", slots.Single(s => s.Appointment!.SyncId == onTablet.SyncId).OverlapText);
            Assert.IsTrue(slots.All(s => s.Appointment!.Status == "Programada"), "Nunca se cancela una cita sola");
        }

        records.Agenda.AgendaPanelDate = day;
        await records.Agenda.LoadAgendaPanelAsync();
        Assert.IsTrue(records.Agenda.AgendaSlots.Where(s => s.HasAppointment).All(s => s.IsOverlapping), "La agenda abierta en la PC también");

        // Una sincronización más no vuelve a avisar lo mismo
        var again = await pc.Sync.SyncAsync();
        Assert.IsTrue(again.Arrivals.IsEmpty);
    }

    [TestMethod]
    public async Task AnOverlapOnAPastDayOrWithACancelledAppointment_DoesNotWarn()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        var patients = await pc.Inventory.GetPatientsAsync();
        var luis = await PatientBySyncIdAsync(tablet, patients[1].SyncId);

        await ScheduleAsync(pc, patients[0], DateTime.Today.AddDays(-3), "09:00");
        var future = await ScheduleAsync(pc, patients[0], DateTime.Today.AddDays(3), "09:00");
        await pc.Inventory.UpdateAppointmentStatusAsync(future.Id, "Cancelada");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        await ScheduleAsync(tablet, luis, DateTime.Today.AddDays(-3), "09:00");
        await ScheduleAsync(tablet, luis, DateTime.Today.AddDays(3), "09:00");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var pcSync = await pc.Sync.SyncAsync();
        Assert.IsTrue(pcSync.Success, pcSync.ErrorMessage);
        Assert.IsEmpty(pcSync.Arrivals.Overlaps, "Lo que ya pasó no se avisa, y una cita cancelada no se empalma");
    }

    // ================================================================== duplicados que llegan de otro equipo

    [TestMethod]
    public async Task TheSamePatientCreatedOnTwoDevices_AppearsInTheBanner_AndAFirstDownloadDoesNotWarn()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        var (records, _) = await OpenRecordsAsync(pc);

        var onPc = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Ixchel Hernández Tun", BirthDate = new DateTime(1980, 5, 4), Phone = "55 1111 2222", Age = 46 });
        var onTablet = await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "IXCHEL  HERNANDEZ TUN", BirthDate = new DateTime(1980, 5, 4) });

        var pcUpload = await pc.Sync.SyncAsync();
        Assert.IsTrue(pcUpload.Arrivals.IsEmpty);
        var tabletSync = await tablet.Sync.SyncAsync();
        Assert.AreEqual(DuplicatePatientRules.Match.BirthDate, tabletSync.Arrivals.DuplicatePatients.Single().Match, "La tablet también lo ve");
        var pcSync = await pc.Sync.SyncAsync();
        var pair = pcSync.Arrivals.DuplicatePatients.Single();
        CollectionAssert.AreEquivalent(new[] { onPc.SyncId, onTablet.SyncId }, new[] { pair.First.SyncId, pair.Second.SyncId });

        await WaitUntilAsync(() => records.Home.HasDuplicatePatientsNotice);
        var banner = records.Home.DuplicatePatientsNoticeText;
        Assert.IsTrue(records.Home.HasDuplicatePatientsNotice);
        StringAssert.StartsWith(banner, "Posible expediente duplicado con lo que llegó de otro equipo: ");
        StringAssert.Contains(banner, $"Ixchel Hernández Tun ({onPc.RecordNumber})");
        StringAssert.Contains(banner, $"IXCHEL  HERNANDEZ TUN ({onTablet.RecordNumber})");
        StringAssert.Contains(banner, "misma fecha de nacimiento");
        Assert.AreEqual(2, (await pc.Inventory.GetPatientsAsync()).Count(p => p.FullName.Contains("IXCHEL", StringComparison.OrdinalIgnoreCase)),
            "Nunca se une nada solo");

        var (restarted, _) = await OpenRecordsAsync(pc);
        Assert.IsTrue(restarted.Home.HasDuplicatePatientsNotice, "Sigue al cerrar y abrir TPVC");
        await restarted.Home.DismissDuplicatePatientsNoticeAsync();
        Assert.IsFalse(restarted.Home.HasDuplicatePatientsNotice);
        Assert.IsFalse((await OpenRecordsAsync(pc)).Records.Home.HasDuplicatePatientsNotice, "Descartado para siempre en este equipo");

        // Un equipo que baja todo por primera vez no avisa lo que ya estaba en la nube
        var laptop = CreateDevice("laptop", seed: false);
        var first = await laptop.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        Assert.IsTrue(first.Arrivals.IsEmpty, "En la primera descarga llega todo: no se revisa");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 20) await Task.Delay(20);
        Assert.IsTrue(condition());
    }

    [TestMethod]
    public async Task ArrivalsOfASyncThatFailsAfterApplyingThem_AreRaisedByTheNextOne()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        var a = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Ixchel Hernández Tun", BirthDate = new DateTime(1980, 5, 4) });
        var b = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Rosalba Quintero Ek", BirthDate = new DateTime(1975, 1, 9) });
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "IXCHEL HERNANDEZ TUN", BirthDate = new DateTime(1980, 5, 4) });
        await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "ROSALBA QUINTERO EK", BirthDate = new DateTime(1975, 1, 9) });
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La PC baja de a un expediente por página: aplica el primero (y su cursor) y la segunda página se cae
        pc.Sync.PullPageSizeOverride = 1;
        int pages = 0;
        _server.FailWhen = sql => sql.Contains("FROM \"Patients\"", StringComparison.Ordinal) && sql.Contains("SyncedAtUtc", StringComparison.Ordinal) && ++pages == 2;
        var failed = await pc.Sync.SyncAsync();
        _server.FailWhen = null;
        Assert.IsFalse(failed.Success, "La sincronización se cortó después de aplicar el primer expediente");
        Assert.IsTrue((await pc.Inventory.GetSyncNoticesAsync()).IsEmpty, "No alcanzó a revisar");

        var next = await pc.Sync.SyncAsync();
        Assert.IsTrue(next.Success, next.ErrorMessage);
        var notices = await pc.Inventory.GetSyncNoticesAsync();
        Assert.HasCount(2, notices.DuplicatePatients, "Los dos pares avisan: también el del expediente que llegó en la sincronización que se cortó");
        foreach (var mine in new[] { a.SyncId, b.SyncId })
        {
            Assert.IsTrue(notices.DuplicatePatients.Any(p => p.First.SyncId == mine || p.Second.SyncId == mine));
        }
    }

    // ================================================================== sincronización rápida

    [TestMethod]
    public async Task SchedulingOnThePc_SyncsFirst_AndAsksAboutTheAppointmentTheTabletJustMade()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        var (records, _) = await OpenRecordsAsync(pc);
        var patients = await pc.Inventory.GetPatientsAsync();
        var luis = await PatientBySyncIdAsync(tablet, patients[1].SyncId);
        var day = DateTime.Today.AddDays(2);
        await ScheduleAsync(tablet, luis, day, "10:30");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La PC no ha bajado la cita de la tablet: la sincronización rápida antes de agendar la trae
        var questions = new List<string>();
        var confirm = DialogService.ConfirmHandler;
        DialogService.ConfirmHandler = (message, _) => { questions.Add(message); return false; };
        try
        {
            records.Agenda.AgendaPanelDate = day;
            await records.Agenda.LoadAgendaPanelAsync();
            Assert.IsFalse(records.Agenda.AgendaSlots.Any(s => s.HasAppointment), "Antes de agendar la PC no la tenía");
            await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "10:30"));
            records.Agenda.AgendaFormPatient = patients[0];
            records.Agenda.AgendaFormReason = "Control";
            await records.Agenda.SaveAgendaFormAsync();
        }
        finally
        {
            DialogService.ConfirmHandler = confirm;
        }

        Assert.AreEqual($"Ya hay una cita a las 10:30 con {luis.FullName}.\n\n¿Agendar de todos modos?", questions.Single());
        Assert.HasCount(1, (await pc.Inventory.GetAppointmentsByDayAsync(day)), "No se confirmó: solo está la de la tablet");
    }

    [TestMethod]
    public async Task ChargingOnThePc_SyncsFirst_AndWarnsAboutWhatTheTabletAlreadySold()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        var product = (await pc.Inventory.GetProductsAsync()).First(p => p.CurrentStock is > 0 and <= 50 && p.Price > 0);
        var onTablet = (await tablet.Inventory.GetProductsAsync()).Single(p => p.SyncId == product.SyncId);
        Assert.IsNotNull(await tablet.Inventory.RegisterSaleAsync(new[] { (onTablet.Id, onTablet.CurrentStock, onTablet.Price) }, "Público General", "Efectivo"));
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La PC todavía muestra existencias: la sincronización rápida antes de cobrar baja la venta de la tablet
        var salesBefore = await CountSalesAsync(pc);
        string? message = null;
        var checkout = new CheckoutViewModel(pc.Inventory) { Notify = (text, ok) => { if (!ok) message = text; } };
        Assert.IsTrue(checkout.AddProduct(product));
        await checkout.ChargeAsync();

        // Revisión 20 (un inventario mal contado nunca impide cobrar): se cobra igual, se avisa y queda en −1 para revisar
        Assert.AreEqual($"Existencias insuficientes: {product.Name} (quedan 0, se cobran 1). Revise el inventario.", message);
        Assert.IsFalse(checkout.HasItems, "Se cobró: la cuenta quedó vacía");
        Assert.AreEqual(salesBefore + 2, await CountSalesAsync(pc), "Bajó la venta de la tablet y se cobró la de la PC");
        var after = (await pc.Inventory.GetProductsAsync()).Single(p => p.Id == product.Id);
        Assert.AreEqual(-1, after.CurrentStock);
        Assert.AreEqual("\u22121 · revisar inventario", after.StockBadgeText);
        Assert.IsTrue((await pc.Inventory.GetSystemAlertsSummaryAsync()).InventoryReviewProducts.Any(p => p.Id == product.Id));
        Assert.IsTrue((await pc.Inventory.VerifyStockLedgerAsync()).IsClean, "El libro sigue cuadrando con el negativo");
    }

    private static async Task<int> CountSalesAsync(Device device)
    {
        using var db = device.Factory.CreateDbContext();
        return await db.Sales.CountAsync();
    }

    private static async Task<(CheckoutViewModel Checkout, Func<SaleReceipt?> Receipt)> CartWithOneProductAsync(Device device)
    {
        SaleReceipt? receipt = null;
        var checkout = new CheckoutViewModel(device.Inventory) { Completed = r => { receipt = r; return Task.CompletedTask; } };
        var product = (await device.Inventory.GetProductsAsync()).First(p => p.CurrentStock > 2 && p.Price > 0);
        Assert.IsTrue(checkout.AddProduct(product));
        return (checkout, () => receipt);
    }

    [TestMethod]
    public async Task ChargingWithACloudThatDoesNotAnswer_WaitsAbout3Seconds_AndTheTicketGetsTheDeviceLetter()
    {
        var pc = await CloudWithIdentityAsync();
        var (checkout, receipt) = await CartWithOneProductAsync(pc);
        var hold = new TaskCompletionSource();
        _server.BeforeExecuteAsync = _ => hold.Task;
        try
        {
            var clock = Stopwatch.StartNew();
            await checkout.ChargeAsync();
            clock.Stop();
            Assert.IsGreaterThanOrEqualTo(2.8, clock.Elapsed.TotalSeconds, "Se esperó a la sincronización rápida");
            Assert.IsLessThan(4.5, clock.Elapsed.TotalSeconds, "3 s y no más: el folio no espera otros 2 s");
            Assert.IsTrue(pc.Sync.IsQuickSyncHanging, "La sincronización sigue en segundo plano, sin cancelarse");
        }
        finally
        {
            hold.TrySetResult();
            _server.BeforeExecuteAsync = null;
        }

        Assert.AreEqual($"{FolioFactory.Series(FolioKind.Sale, DateTime.Today)}-P1-0001", receipt()?.Folio, "Sin respuesta de la nube, el folio lleva la letra");
        for (int i = 0; i < 100 && pc.Sync.IsQuickSyncHanging; i++) await Task.Delay(50);
        Assert.IsFalse(pc.Sync.IsQuickSyncHanging, "Al responder la nube, la sincronización termina sola");
        Assert.AreNotEqual(TursoSyncStatus.Error, pc.Sync.Status);
    }

    [TestMethod]
    public async Task SchedulingWithACloudThatDoesNotAnswer_WaitsAtMost3Seconds()
    {
        var pc = await CloudWithIdentityAsync();
        var (records, _) = await OpenRecordsAsync(pc);
        var day = DateTime.Today.AddDays(2);
        records.Agenda.AgendaPanelDate = day;
        await records.Agenda.LoadAgendaPanelAsync();
        await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree));
        records.Agenda.AgendaFormPatient = records.Patients.First();
        records.Agenda.AgendaFormReason = "Control";

        var hold = new TaskCompletionSource();
        _server.BeforeExecuteAsync = _ => hold.Task;
        try
        {
            var clock = Stopwatch.StartNew();
            await records.Agenda.SaveAgendaFormAsync();
            clock.Stop();
            Assert.IsLessThan(4.5, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            hold.TrySetResult();
            _server.BeforeExecuteAsync = null;
        }
        Assert.HasCount(1, await pc.Inventory.GetAppointmentsByDayAsync(day), "Se agendó con los datos de la PC");
        for (int i = 0; i < 100 && pc.Sync.IsQuickSyncHanging; i++) await Task.Delay(50);
    }

    [TestMethod]
    public async Task ChargingOffline_DoesNotWait_AndTheTicketGetsTheDeviceLetter()
    {
        var pc = await CloudWithIdentityAsync();
        var (checkout, receipt) = await CartWithOneProductAsync(pc);
        _server.IsOffline = true;

        var clock = Stopwatch.StartNew();
        await checkout.ChargeAsync();
        clock.Stop();

        Assert.IsLessThan(1.5, clock.Elapsed.TotalSeconds, "Sin red no se espera");
        Assert.AreEqual($"{FolioFactory.Series(FolioKind.Sale, DateTime.Today)}-P1-0001", receipt()?.Folio);
        Assert.AreEqual(QuickSyncOutcome.Failed, await pc.Sync.QuickSyncAsync());
        Assert.AreEqual(TursoSyncStatus.Offline, pc.Sync.Status);
    }

    [TestMethod]
    public async Task TheQuickSync_IsOneReadOnlyRequest_SharedByTwoCallers_AndDoesNotLoop()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success, "El esquema y el registro, una vez por sesión");

        _server.ClearRecording();
        var requests = _server.RequestCount;
        var reads = _server.BilledRowsRead;
        Assert.AreEqual(QuickSyncOutcome.Synced, await pc.Sync.QuickSyncAsync());
        Assert.AreEqual(1, _server.RequestCount - requests, "Sin nada que subir: una sola petición (la foto de la bajada)");
        Assert.AreEqual(0, _server.Requests.SelectMany(r => r.Statements).Sum(s => s.RowsWritten), "No escribe nada");
        var cost = _server.BilledRowsRead - reads;
        Console.WriteLine($"Lecturas estimadas de una sincronización rápida sin cambios: {cost}");
        Assert.IsLessThan(40, cost);

        // No deja nada que dispare otra
        await Task.Delay(300);
        Assert.AreEqual(requests + 1, _server.RequestCount);

        // Dos a la vez (cobrar y agendar, o dos clics) comparten la misma
        var both = await Task.WhenAll(pc.Sync.QuickSyncAsync(), pc.Sync.QuickSyncAsync());
        CollectionAssert.AreEqual(new[] { QuickSyncOutcome.Synced, QuickSyncOutcome.Synced }, both);
        Assert.AreEqual(requests + 2, _server.RequestCount);

        // Con otra sincronización en curso espera su turno (el candado de siempre) y las dos terminan bien
        var hold = new TaskCompletionSource();
        _server.BeforeExecuteAsync = _ => hold.Task;
        var running = pc.Sync.SyncAsync();
        var quick = pc.Sync.QuickSyncAsync();
        await Task.Delay(200);
        hold.SetResult();
        _server.BeforeExecuteAsync = null;
        Assert.IsTrue((await running).Success);
        Assert.AreEqual(QuickSyncOutcome.Synced, await quick);
        Assert.AreEqual(requests + 4, _server.RequestCount);
    }

    [TestMethod]
    public async Task TheQuickSync_RespectsTheBrakeTheExhaustedQuotaAndAFirstSync_WithoutAskingTurso()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Freno de consumo
        pc.Settings.TripConsumptionBrake();
        var requests = _server.RequestCount;
        Assert.AreEqual(QuickSyncOutcome.Skipped, await pc.Sync.QuickSyncAsync());
        Assert.AreEqual(requests, _server.RequestCount);
        pc.Sync.ResumeAfterConsumptionBrake();

        // Cuota agotada: la primera la encuentra (y pausa una hora); las siguientes ya no preguntan
        _server.Blocked = FakeBlockedMode.StatementError;
        Assert.AreEqual(QuickSyncOutcome.Failed, await pc.Sync.QuickSyncAsync());
        Assert.AreEqual(TursoSyncPauseReason.QuotaExhausted, pc.Sync.PauseReason);
        requests = _server.RequestCount;
        Assert.AreEqual(QuickSyncOutcome.Skipped, await pc.Sync.QuickSyncAsync());
        Assert.AreEqual(requests, _server.RequestCount);
        _server.Blocked = FakeBlockedMode.None;

        // Sin ninguna sincronización buena (la primera descarga la empieza el arranque) y con la nube apagada
        var tablet = CreateDevice("tablet", seed: false);
        Assert.AreEqual(QuickSyncOutcome.Skipped, await tablet.Sync.QuickSyncAsync());
        Assert.AreEqual(requests, _server.RequestCount);
        var off = tablet.Settings.CurrentSettings;
        off.IsSyncEnabled = false;
        tablet.Settings.SaveSettings(off);
        Assert.AreEqual(QuickSyncOutcome.Off, await tablet.Inventory.QuickSyncAsync());
        Assert.AreEqual(requests, _server.RequestCount);
    }

    [TestMethod]
    public async Task ChargingWhileABackgroundSyncIsStuck_WaitsAbout3Seconds_AndTheTicketGetsTheDeviceLetter()
    {
        var pc = await CloudWithIdentityAsync();
        var (checkout, receipt) = await CartWithOneProductAsync(pc);
        var hold = new TaskCompletionSource();
        _server.BeforeExecuteAsync = _ => hold.Task;
        // La sincronización automática tiene el candado y espera a la red
        var background = pc.Sync.SyncAsync(isBackgroundAutoSync: true);
        try
        {
            var clock = Stopwatch.StartNew();
            await checkout.ChargeAsync();
            clock.Stop();
            Assert.IsLessThan(4.5, clock.Elapsed.TotalSeconds, "La rápida no alcanzó a correr, y el folio tampoco espera a la nube");
            Assert.IsTrue(pc.Sync.IsQuickSyncHanging);
        }
        finally
        {
            hold.TrySetResult();
            _server.BeforeExecuteAsync = null;
        }

        Assert.AreEqual($"{FolioFactory.Series(FolioKind.Sale, DateTime.Today)}-P1-0001", receipt()?.Folio);
        Assert.IsTrue((await background).Success, "La automática termina sola cuando la nube responde");
        for (int i = 0; i < 100 && pc.Sync.IsQuickSyncHanging; i++) await Task.Delay(50);
        Assert.IsFalse(pc.Sync.IsQuickSyncHanging);
    }

    // ================================================================== crear un expediente

    private static void FillNewPatient(PatientFormViewModel form, string first, string last, string birth)
    {
        form.FormFirstName = first;
        form.FormLastName = last;
        form.FormBirthDateText = birth;
        form.FormGender = "Femenino";
        form.SetAllergiesDenied();
    }

    [TestMethod]
    public async Task CreatingAPatientOnThePc_SyncsFirst_AndAsksAboutTheOneTheTabletJustCreated()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        var (records, _) = await OpenRecordsAsync(pc);
        var onTablet = await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "Ixchel Hernández Tun", BirthDate = new DateTime(1980, 5, 4) });
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        var questions = new List<string>();
        var confirm = DialogService.ConfirmHandler;
        DialogService.ConfirmHandler = (message, _) => { questions.Add(message); return false; };
        try
        {
            await records.NewPatientAsync();
            FillNewPatient(records.PatientForm, "Ixchel", "Hernández Tun", "04/05/1980");
            await records.PatientForm.SavePatientModalAsync();
        }
        finally
        {
            DialogService.ConfirmHandler = confirm;
        }

        StringAssert.StartsWith(questions.Single(), $"Ya hay un expediente parecido:\n\n• Ixchel Hernández Tun · {onTablet.RecordNumber} · misma fecha de nacimiento");
        Assert.IsTrue(records.PatientForm.IsPatientModalOpen, "Sin confirmar, el formulario sigue abierto");
        Assert.AreEqual(1, (await pc.Inventory.GetPatientsAsync()).Count(p => p.FullName.Contains("Ixchel", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CreatingAPatientWithACloudThatDoesNotAnswer_WaitsAtMost3Seconds()
    {
        var pc = await CloudWithIdentityAsync();
        var (records, _) = await OpenRecordsAsync(pc);
        await records.NewPatientAsync();
        FillNewPatient(records.PatientForm, "Teodora", "Salas Ruiz", "03/03/1977");
        var hold = new TaskCompletionSource();
        _server.BeforeExecuteAsync = _ => hold.Task;
        try
        {
            var clock = Stopwatch.StartNew();
            await records.PatientForm.SavePatientModalAsync();
            clock.Stop();
            Assert.IsGreaterThanOrEqualTo(2.8, clock.Elapsed.TotalSeconds, "Se esperó a la sincronización rápida");
            Assert.IsLessThan(4.5, clock.Elapsed.TotalSeconds, "3 s y no más: el folio no espera otros 2 s");
        }
        finally
        {
            hold.TrySetResult();
            _server.BeforeExecuteAsync = null;
        }
        Assert.IsFalse(records.PatientForm.IsPatientModalOpen);
        var created = (await pc.Inventory.GetPatientsAsync()).Single(p => p.FullName == "Teodora Salas Ruiz");
        StringAssert.Contains(created.RecordNumber, "-P1-", "Sin respuesta de la nube, el folio lleva la letra");
        for (int i = 0; i < 100 && pc.Sync.IsQuickSyncHanging; i++) await Task.Delay(50);
    }

    [TestMethod]
    public async Task CreatingAPatientOffline_DoesNotWait()
    {
        var pc = await CloudWithIdentityAsync();
        var (records, _) = await OpenRecordsAsync(pc);
        await records.NewPatientAsync();
        FillNewPatient(records.PatientForm, "Teodora", "Salas Ruiz", "03/03/1977");
        _server.IsOffline = true;

        var clock = Stopwatch.StartNew();
        await records.PatientForm.SavePatientModalAsync();
        clock.Stop();

        Assert.IsLessThan(1.5, clock.Elapsed.TotalSeconds, "Sin red no se espera");
        StringAssert.Contains((await pc.Inventory.GetPatientsAsync()).Single(p => p.FullName == "Teodora Salas Ruiz").RecordNumber, "-P1-");
    }

    // ================================================================== el formulario de la agenda y las recargas

    [TestMethod]
    public async Task TheQuickSyncBeforeScheduling_ReloadsTheOpenAgenda_AndTheFormSurvives()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        var (records, _) = await OpenRecordsAsync(pc);
        var patients = await pc.Inventory.GetPatientsAsync();
        var luis = await PatientBySyncIdAsync(tablet, patients[1].SyncId);
        var day = DateTime.Today.AddDays(2);
        await ScheduleAsync(tablet, luis, day, "10:30");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La agenda abierta: lo que baje la recarga
        await records.Agenda.OpenAgendaOnDateAsync(day);
        await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "10:00"));
        records.Agenda.AgendaFormPatient = records.Patients.First(p => p.SyncId == patients[0].SyncId);
        records.Agenda.AgendaFormReason = "Control";
        records.Agenda.AgendaFormDuration = 60;

        var questions = new List<string>();
        var confirm = DialogService.ConfirmHandler;
        DialogService.ConfirmHandler = (message, _) => { questions.Add(message); return false; };
        try
        {
            await records.Agenda.SaveAgendaFormAsync();
            await WaitUntilAsync(() => records.Agenda.AgendaSlots.Any(s => s.HasAppointment));
        }
        finally
        {
            DialogService.ConfirmHandler = confirm;
        }

        StringAssert.StartsWith(questions.Single(), $"Ya hay una cita a las 10:30 con {luis.FullName}.");
        var open = records.Agenda.AgendaSlots.Single(s => s.IsFormOpen);
        Assert.AreEqual("10:00", open.TimeText, "La recarga no cerró el formulario: su hueco sigue libre");
        Assert.AreEqual(patients[0].SyncId, records.Agenda.AgendaFormPatient?.SyncId);
        Assert.AreEqual("Control", records.Agenda.AgendaFormReason);
        Assert.AreEqual(60, records.Agenda.AgendaFormDuration);
    }

    // ================================================================== horas escritas a mano en v1.7–v1.13

    /// <summary>Como al actualizar desde v1.39: la base todavía no normalizó las horas.</summary>
    private static void ForgetTimesNormalized(Device device)
    {
        using var db = device.Factory.CreateDbContext();
        TPVC.Data.DbMeta.Remove(db, AppointmentTimeMigration.DoneKey);
    }

    private static bool TimesNormalized(Device device)
    {
        using var db = device.Factory.CreateDbContext();
        return TPVC.Data.DbMeta.Get(db, AppointmentTimeMigration.DoneKey) == "1";
    }

    [TestMethod]
    public async Task HandWrittenAppointmentTimes_AreNormalizedOnceAfterTheFirstPull_AndUploadedOnce()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        Assert.IsTrue(TimesNormalized(pc), "La primera sincronización ya hizo la pasada (no había nada)");
        var patient = (await pc.Inventory.GetPatientsAsync())[0];
        var day = DateTime.Today.AddDays(5);
        // Como las guardaba la agenda de v1.7–v1.13 (tal cual se escribían), y ya en la nube
        var written = new[] { "5 pm", "12 PM", "4.30 pm", "16:30 PM", "7:5", "10.30", "9,30", "10 30", "10/30", "10-30", "mediodía" };
        foreach (var time in written) await ScheduleAsync(pc, patient, day, time);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Se actualiza a v1.40 con la nube activa: al abrir no se toca nada; la sincronización lo hace después de bajar
        ForgetTimesNormalized(pc);
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.AreEqual(0, AppointmentTimeMigration.RunAtOpen(db, pc.Settings.CurrentSettings));
        }
        CollectionAssert.AreEquivalent(written, (await pc.Inventory.GetAppointmentsByDayAsync(day)).Select(a => a.Time).ToList(), "Al abrir, con la nube, nada cambia");
        Assert.IsFalse(TimesNormalized(pc));

        var upload = await pc.Sync.SyncAsync();
        Assert.IsTrue(upload.Success, upload.ErrorMessage);
        var expected = new[] { "17:00", "12:00", "16:30", "16:30", "07:05", "10:30", "09:30", "10:30", "10/30", "10-30", "mediodía" };
        CollectionAssert.AreEquivalent(expected, (await pc.Inventory.GetAppointmentsByDayAsync(day)).Select(a => a.Time).ToList(),
            "Lo que la regla no lee y se lee sin adivinar pasa a HH:mm; lo que parece fecha o no se lee se queda como está (nunca 00:00)");
        Assert.AreEqual(8, upload.RowsPushed, "Suben como una edición, en la misma sincronización");
        Assert.IsTrue(TimesNormalized(pc));
        Assert.AreEqual(0, (await pc.Sync.SyncAsync()).RowsPushed, "Una sola vez");

        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        CollectionAssert.AreEquivalent(expected, (await tablet.Inventory.GetAppointmentsByDayAsync(day)).Select(a => a.Time).ToList(),
            "Los demás equipos quedan con la misma hora");
    }

    [TestMethod]
    public async Task AStaleCopyOnAnotherPc_DoesNotUndoARescheduleWhenItUpdates()
    {
        var (pc, laptop, _) = await PcAndTabletAsync();
        var patient = (await pc.Inventory.GetPatientsAsync())[0];
        var day = DateTime.Today.AddDays(3);
        var appointment = await ScheduleAsync(pc, patient, day, "5 pm");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);

        // La laptop se cierra con v1.39; mientras, la PC mueve la cita (la agenda escribe HH:mm)
        appointment.Date = day.AddDays(1);
        appointment.Time = "18:00";
        await pc.Inventory.SaveAppointmentAsync(appointment);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // La laptop abre con v1.40 y la nube activa: su copia vieja ("5 pm" el día de antes) no se reescribe al abrir
        ForgetTimesNormalized(laptop);
        using (var db = laptop.Factory.CreateDbContext())
        {
            Assert.AreEqual(0, AppointmentTimeMigration.RunAtOpen(db, laptop.Settings.CurrentSettings));
        }
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        foreach (var device in new[] { pc, laptop })
        {
            var moved = (await device.Inventory.GetAppointmentsByDayAsync(day.AddDays(1))).Single(a => a.SyncId == appointment.SyncId);
            Assert.AreEqual("18:00", moved.Time, "El cambio de la PC se queda en los dos equipos");
            Assert.IsEmpty((await device.Inventory.GetAppointmentsByDayAsync(day)).Where(a => a.SyncId == appointment.SyncId).ToList());
        }
        Assert.IsTrue(TimesNormalized(laptop));
    }

    [TestMethod]
    public async Task JoiningACloud_NormalizesTheHandWrittenTimesThatCameDown()
    {
        var pc = await CloudWithIdentityAsync();
        var patient = (await pc.Inventory.GetPatientsAsync())[0];
        var day = DateTime.Today.AddDays(4);
        // Una cita escrita a mano que ya está en la nube (la subió una PC que no había actualizado)
        await ScheduleAsync(pc, patient, day, "5 pm");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Una PC nueva que abrió sin nube: hizo su pasada (no había nada) y se quedó con la marca, y esta versión ya había
        // fallado una vez al hacerla; luego se une a la nube
        var laptop = CreateDevice("laptop", seed: false);
        using (var db = laptop.Factory.CreateDbContext())
        {
            Assert.AreEqual(0, AppointmentTimeMigration.RunAtOpen(db, new TursoSettings()));
            TPVC.Data.DbMeta.Set(db, AppointmentTimeMigration.FailedVersionKey, TursoSyncService.AppVersionText);
        }
        Assert.IsTrue(TimesNormalized(laptop));
        Assert.IsTrue((await laptop.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);

        Assert.AreEqual("17:00", (await laptop.Inventory.GetAppointmentsByDayAsync(day)).Single().Time,
            "Lo que bajó al unirse también se normaliza (unirse quita la marca de hecho y la del fallo)");
        Assert.IsTrue(TimesNormalized(laptop));
    }

    [TestMethod]
    public async Task APcThatLeftTheCloud_IsNormalizedWhenItOpens()
    {
        var (pc, _, _) = await PcAndTabletAsync();
        var patient = (await pc.Inventory.GetPatientsAsync())[0];
        var day = DateTime.Today.AddDays(5);
        await ScheduleAsync(pc, patient, day, "5 pm");
        await ScheduleAsync(pc, patient, day, "4.30 pm");

        // Se borra la URL de la nube (o un respaldo de esta PC se restaura en una nueva sin nube): la base sigue con el clinicId
        // y los cursores, pero ya no puede sincronizar. Al abrir con v1.40 se normaliza; si no, se quedaría a las 10:00 para siempre
        var settings = pc.Settings.CurrentSettings;
        settings.DatabaseUrl = string.Empty;
        settings.IsSyncEnabled = false;
        pc.Settings.SaveSettings(settings);
        ForgetTimesNormalized(pc);
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.IsNotNull(TPVC.Data.DbMeta.Get(db, TPVC.Data.DbMeta.ClinicId) ?? TPVC.Data.DbMeta.Get(db, TPVC.Data.DbMeta.PullCursorPrefix + "Appointments"),
                "La base guarda lo de la nube");
            Assert.AreEqual(2, AppointmentTimeMigration.RunAtOpen(db, pc.Settings.CurrentSettings));
        }
        CollectionAssert.AreEquivalent(new[] { "17:00", "16:30" }, (await pc.Inventory.GetAppointmentsByDayAsync(day)).Select(a => a.Time).ToList());
        Assert.IsTrue(TimesNormalized(pc));
    }

    [TestMethod]
    public async Task APcWithSyncSwitchedOff_DoesNotRewriteItsStaleCopyWhenItOpens()
    {
        var (pc, laptop, _) = await PcAndTabletAsync();
        var patient = (await pc.Inventory.GetPatientsAsync())[0];
        var day = DateTime.Today.AddDays(3);
        var appointment = await ScheduleAsync(pc, patient, day, "5 pm");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);

        // La laptop apaga «Sincronización activada» (se queda con la URL, el token y sus cursores); la PC cancela la cita
        var off = laptop.Settings.CurrentSettings;
        off.IsSyncEnabled = false;
        laptop.Settings.SaveSettings(off);
        await pc.Inventory.UpdateAppointmentStatusAsync(appointment.Id, "Cancelada");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // La laptop abre con v1.40: sigue ligada a la nube, así que su copia vieja no se reescribe ni se marca como hecho
        ForgetTimesNormalized(laptop);
        using (var db = laptop.Factory.CreateDbContext())
        {
            Assert.AreEqual(0, AppointmentTimeMigration.RunAtOpen(db, laptop.Settings.CurrentSettings));
        }
        Assert.IsFalse(TimesNormalized(laptop));

        // Vuelve a encender la sincronización: baja la cancelación y luego normaliza
        var on = laptop.Settings.CurrentSettings;
        on.IsSyncEnabled = true;
        laptop.Settings.SaveSettings(on);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        foreach (var device in new[] { pc, laptop })
        {
            var stored = (await device.Inventory.GetAppointmentsByDayAsync(day)).Single(a => a.SyncId == appointment.SyncId);
            Assert.AreEqual("Cancelada", stored.Status, "La cancelación de la PC se queda en los dos equipos");
            Assert.AreEqual("17:00", stored.Time);
        }
        Assert.IsTrue(TimesNormalized(laptop));
    }

    [TestMethod]
    public async Task AnUnreadableTimeOrAFailingPass_NeverStopsTheSync()
    {
        var (pc, _, _) = await PcAndTabletAsync();
        var patient = (await pc.Inventory.GetPatientsAsync())[0];
        var day = DateTime.Today.AddDays(6);
        await ScheduleAsync(pc, patient, day, "１０.３０");
        await ScheduleAsync(pc, patient, day, "5 pm");

        // Un texto con dígitos de ancho completo se deja como está; lo demás se normaliza y la sincronización termina
        ForgetTimesNormalized(pc);
        var sync = await pc.Sync.SyncAsync();
        Assert.IsTrue(sync.Success, sync.ErrorMessage);
        CollectionAssert.AreEquivalent(new[] { "１０.３０", "17:00" }, (await pc.Inventory.GetAppointmentsByDayAsync(day)).Select(a => a.Time).ToList());
        Assert.IsTrue(TimesNormalized(pc));

        // Si la pasada falla de todos modos, la sincronización sigue, no se marca como hecha y esta versión no la repite
        ForgetTimesNormalized(pc);
        int calls = 0;
        pc.Sync.NormalizeAppointmentTimes = _ => { calls++; throw new InvalidOperationException("Falla simulada de la pasada"); };
        var before = pc.Settings.CurrentSettings.LastSyncUtc;
        var failed = await pc.Sync.SyncAsync();
        Assert.IsTrue(failed.Success, failed.ErrorMessage);
        Assert.AreNotEqual(before, pc.Settings.CurrentSettings.LastSyncUtc, "La sincronización llegó al final");
        Assert.IsFalse(TimesNormalized(pc));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(1, calls, "Esta versión no la vuelve a intentar en cada sincronización");

        // Una versión nueva sí la intenta otra vez
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.AreEqual(TursoSyncService.AppVersionText, TPVC.Data.DbMeta.Get(db, AppointmentTimeMigration.FailedVersionKey));
            TPVC.Data.DbMeta.Set(db, AppointmentTimeMigration.FailedVersionKey, "0.0.0-anterior");
        }
        pc.Sync.NormalizeAppointmentTimes = AppointmentTimeMigration.Run;
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue(TimesNormalized(pc));
    }

    // ================================================================== MergedIntoSyncId

    [TestMethod]
    public async Task MergedIntoSyncId_TravelsAndMergesAsAnyOtherField()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();
        var into = (await pc.Inventory.GetPatientsAsync()).First(p => p.SyncId != patient).SyncId;

        // La tablet lo anota (lo que hará el 8.A) y la PC cambia el teléfono del mismo expediente, sin sincronizar entre ellos
        await EditAsync(tablet, patient, p => p.MergedIntoSyncId = into);
        await EditAsync(pc, patient, p => p.Phone = "5599990000");
        // Como cualquier campo (8.22): la PC sube su fila antes de bajar y la nube se queda con ella (LWW por fila); la tablet la
        // combina con la suya, sube el resultado y la PC lo baja
        await QuiesceAsync(tablet, pc);

        foreach (var device in new[] { pc, tablet })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual(into, stored.MergedIntoSyncId, "Se conserva lo de la tablet");
            Assert.AreEqual("5599990000", stored.Phone, "y lo de la PC");
            Assert.IsNotNull(PatientFieldStamps.Parse(stored.FieldStampsJson)[nameof(PatientRecord.MergedIntoSyncId)], "Tiene su marca, como cualquier campo");
        }
        Assert.AreEqual(TursoSyncService.FormatGuid(into),
            CloudScalar($"SELECT MergedIntoSyncId FROM Patients WHERE SyncId = '{TursoSyncService.FormatGuid(patient)}'"), "En la nube, en minúsculas");

        // Un expediente unido a otro ya no cuenta para los duplicados
        var merged = await PatientBySyncIdAsync(pc, patient);
        var twin = new PatientRecord { FullName = merged.FullName, BirthDate = merged.BirthDate, Phone = merged.Phone };
        Assert.AreNotEqual(DuplicatePatientRules.Match.None,
            DuplicatePatientRules.Compare(DuplicatePatientRules.Of(twin), DuplicatePatientRules.Of(merged) with { MergedIntoSyncId = null }), "Sin unir sería su duplicado");
        Assert.IsFalse((await pc.Inventory.FindDuplicatePatientsAsync(twin)).Any(d => d.Patient.SyncId == patient));
    }
}
