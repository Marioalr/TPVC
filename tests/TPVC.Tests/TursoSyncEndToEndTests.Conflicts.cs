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

        // El aviso en la PC y la marca "Empalme" en la agenda de los dos equipos; ninguna cita se canceló
        var notice = notifications.Items.Single(n => n.Kind == NotificationKind.Warning).Message;
        StringAssert.StartsWith(notice, "Empalme en la agenda (llegó de otro equipo): ");
        StringAssert.Contains(notice, $"{ana.FullName} (10:30) y {luis.FullName} (10:45)");
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

        var banner = records.Home.DuplicatePatientsNoticeText;
        Assert.IsTrue(records.Home.HasDuplicatePatientsNotice);
        StringAssert.StartsWith(banner, "Posible expediente duplicado con lo que llegó de otro equipo: ");
        StringAssert.Contains(banner, $"Ixchel Hernández Tun ({onPc.RecordNumber})");
        StringAssert.Contains(banner, $"IXCHEL  HERNANDEZ TUN ({onTablet.RecordNumber})");
        StringAssert.Contains(banner, "misma fecha de nacimiento");
        Assert.AreEqual(2, (await pc.Inventory.GetPatientsAsync()).Count(p => p.FullName.Contains("IXCHEL", StringComparison.OrdinalIgnoreCase)),
            "Nunca se une nada solo");

        records.Home.DismissDuplicatePatientsNotice();
        Assert.IsFalse(records.Home.HasDuplicatePatientsNotice);

        // Un equipo que baja todo por primera vez no avisa lo que ya estaba en la nube
        var laptop = CreateDevice("laptop", seed: false);
        var first = await laptop.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        Assert.IsTrue(first.Arrivals.IsEmpty, "En la primera descarga llega todo: no se revisa");
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
    public async Task ChargingOnThePc_SyncsFirst_AndSeesWhatTheTabletAlreadySold()
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

        StringAssert.Contains(message, $"Stock insuficiente para '{product.Name}'");
        Assert.IsTrue(checkout.HasItems, "No se cobró: la cuenta sigue");
        Assert.AreEqual(salesBefore + 1, await CountSalesAsync(pc), "Solo bajó la venta de la tablet");
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
            Assert.IsLessThan(3.8, clock.Elapsed.TotalSeconds, "3 s y no más: el folio no espera otros 2 s");
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
            Assert.IsLessThan(3.8, clock.Elapsed.TotalSeconds);
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
