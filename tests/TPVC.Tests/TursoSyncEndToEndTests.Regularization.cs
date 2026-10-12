using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Folios;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.17: una vez al día, la PC principal da el número normal a los expedientes y tickets hechos sin conexión y guarda
/// el folio con letra en OriginalFolio.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private sealed class FakeRegularizationHost : IFolioRegularizationHost
    {
        public bool IsBusy { get; set; }
        public int? OpenPatientId { get; set; }
        public bool CanReplacePatient(int patientId) => OpenPatientId != patientId;
    }

    /// <summary>La laptop (P2) crea sin conexión un expediente con su cita y un ticket, y después sube todo; la PC lo baja.</summary>
    private async Task<(Device Pc, Device Laptop, PatientRecord Patient, Sale Sale, ManualTimeProvider Clock)> LetteredFoliosOnThePcAsync()
    {
        var (pc, laptop) = await TwoVerifiedPcsAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        pc.Sync.FolioRegularization.Clock = clock;
        laptop.Sync.FolioRegularization.Clock = clock;

        _server.IsOffline = true;
        var patient = await NewPatientAsync(laptop, "Paciente Hecho Sin Red");
        await laptop.Inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = patient.Id, Date = DateTime.Today.AddDays(3), Time = "10:00", Reason = "Revisión" });
        var sale = await SellOneAsync(laptop);
        _server.IsOffline = false;
        StringAssert.Contains(patient.RecordNumber, "-P2-");
        StringAssert.Contains(sale.Folio, "-P2-");

        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        return (pc, laptop, patient, sale, clock);
    }

    private static async Task<PatientRecord> PatientBySyncIdAsync(Device device, Guid syncId)
    {
        using var db = device.Factory.CreateDbContext();
        return await db.Patients.AsNoTracking().SingleAsync(p => p.SyncId == syncId);
    }

    private static async Task<Sale> SaleBySyncIdAsync(Device device, Guid syncId)
    {
        using var db = device.Factory.CreateDbContext();
        return await db.Sales.AsNoTracking().SingleAsync(s => s.SyncId == syncId);
    }

    [TestMethod]
    public async Task ThePrimaryPc_GivesLetteredFoliosTheNextNumber_AndKeepsTheOriginalForTheSearch()
    {
        var (pc, laptop, patient, sale, _) = await LetteredFoliosOnThePcAsync();
        var lettered = patient.RecordNumber;
        int expected = (int)CloudCounter(ExpSeries)! + 1;

        var result = await pc.Sync.FolioRegularization.RunIfDueAsync();
        Assert.AreEqual(FolioRegularizationOutcome.Done, result.Outcome);
        Assert.AreEqual(1, result.Patients);
        Assert.AreEqual(1, result.Sales);

        var onPc = await PatientBySyncIdAsync(pc, patient.SyncId);
        Assert.AreEqual(FolioFactory.Format(FolioKind.Patient, ExpSeries, expected), onPc.RecordNumber);
        Assert.AreEqual(lettered, onPc.OriginalFolio);
        var ticket = await SaleBySyncIdAsync(pc, sale.SyncId);
        Assert.AreEqual(sale.Folio, ticket.OriginalFolio);
        Assert.IsNotNull(FolioFactory.NumberIn(FolioFactory.Series(FolioKind.Sale, DateTime.Today), ticket.Folio), ticket.Folio);
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.AreEqual(onPc.RecordNumber, (await db.Appointments.SingleAsync(a => a.PatientRecordId == onPc.Id)).PatientRecordNumber, "La cita copia el folio nuevo");
        }

        // La búsqueda encuentra los dos folios
        Assert.IsTrue((await pc.Inventory.GetPatientsAsync(lettered)).Any(p => p.SyncId == patient.SyncId));
        Assert.IsTrue((await pc.Inventory.GetPatientsAsync(onPc.RecordNumber)).Any(p => p.SyncId == patient.SyncId));
        Assert.IsTrue(await pc.Inventory.IsRecordNumberTakenAsync(lettered));
        Assert.AreEqual(sale.SyncId, (await pc.Inventory.GetSaleByFolioAsync(sale.Folio))!.SyncId);
        Assert.AreEqual(sale.SyncId, (await pc.Inventory.GetSaleByFolioAsync(ticket.Folio))!.SyncId);

        // Sube por el ciclo normal y la laptop recibe el folio nuevo con el original
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        var onLaptop = await PatientBySyncIdAsync(laptop, patient.SyncId);
        Assert.AreEqual(onPc.RecordNumber, onLaptop.RecordNumber);
        Assert.AreEqual(lettered, onLaptop.OriginalFolio);

        // Una vez al día
        Assert.AreEqual(FolioRegularizationOutcome.AlreadyToday, (await pc.Sync.FolioRegularization.RunIfDueAsync()).Outcome);
    }

    [TestMethod]
    public async Task TheRegularization_WaitsForAFreeScreen_AndForEveryDeviceToUpload_AndOnlyRunsOnThePrimary()
    {
        var (pc, laptop, patient, _, clock) = await LetteredFoliosOnThePcAsync();
        var host = new FakeRegularizationHost { IsBusy = true };
        pc.Sync.FolioRegularization.Host = host;

        // Una consulta o una venta en curso
        Assert.AreEqual(FolioRegularizationOutcome.Busy, (await pc.Sync.FolioRegularization.RunIfDueAsync()).Outcome);

        // Una segunda PC nunca regulariza
        Assert.AreEqual(FolioRegularizationOutcome.NotPrimary, (await laptop.Sync.FolioRegularization.RunIfDueAsync()).Outcome);

        // La laptop tiene cambios sin subir (su latido lo dice)
        host.IsBusy = false;
        await NewPatientAsync(laptop, "Paciente Sin Subir");
        await laptop.Sync.GetDevicesAsync();
        Assert.AreEqual(FolioRegularizationOutcome.PendingElsewhere, (await pc.Sync.FolioRegularization.RunIfDueAsync()).Outcome);
        Assert.IsEmpty(pc.Sync.FolioRegularization.StaleDevices, "Se vio hace un momento: no va en el aviso");
        Assert.AreEqual(patient.RecordNumber, (await PatientBySyncIdAsync(pc, patient.SyncId)).RecordNumber, "Nada cambió");

        // Sube sus cambios; al día siguiente (la laptop estuvo cerrada) corre y lo recupera
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        clock.Advance(TimeSpan.FromDays(1));
        var result = await pc.Sync.FolioRegularization.RunIfDueAsync();
        Assert.AreEqual(FolioRegularizationOutcome.Done, result.Outcome);
        Assert.AreEqual(patient.RecordNumber, (await PatientBySyncIdAsync(pc, patient.SyncId)).OriginalFolio);
    }

    [TestMethod]
    public async Task AnOpenPatient_IsLeftForLater_AndARegularizedFolioThatComesBack_KeepsItsNumber()
    {
        var (pc, laptop, patient, _, clock) = await LetteredFoliosOnThePcAsync();
        int pcId = (await PatientBySyncIdAsync(pc, patient.SyncId)).Id;
        var host = new FakeRegularizationHost { OpenPatientId = pcId };
        pc.Sync.FolioRegularization.Host = host;

        // El paciente está abierto en la ficha: el ticket sí, el expediente después
        var partial = await pc.Sync.FolioRegularization.RunIfDueAsync();
        Assert.AreEqual(FolioRegularizationOutcome.Partial, partial.Outcome);
        Assert.AreEqual(0, partial.Patients);
        Assert.AreEqual(1, partial.Sales);
        host.OpenPatientId = null;
        Assert.AreEqual(FolioRegularizationOutcome.PendingHere, (await pc.Sync.FolioRegularization.RunIfDueAsync()).Outcome,
            "Primero sube el ticket que acaba de regularizar");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var rest = await pc.Sync.FolioRegularization.RunIfDueAsync();
        Assert.AreEqual(FolioRegularizationOutcome.Done, rest.Outcome);
        var regularized = (await PatientBySyncIdAsync(pc, patient.SyncId)).RecordNumber;
        Assert.AreEqual(patient.RecordNumber, (await PatientBySyncIdAsync(pc, patient.SyncId)).OriginalFolio);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // La laptop, sin bajar todavía, edita el expediente: vuelve a subir el folio con letra
        var stale = await PatientBySyncIdAsync(laptop, patient.SyncId);
        stale.Phone = "5512345678";
        await laptop.Inventory.SavePatientAsync(stale);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(patient.RecordNumber, (await PatientBySyncIdAsync(pc, patient.SyncId)).RecordNumber);

        // Al día siguiente recibe el mismo número, sin tomar otro del contador
        var counter = CloudCounter(ExpSeries);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.AreEqual(FolioRegularizationOutcome.Done, (await pc.Sync.FolioRegularization.RunIfDueAsync()).Outcome);
        var again = await PatientBySyncIdAsync(pc, patient.SyncId);
        Assert.AreEqual(regularized, again.RecordNumber);
        Assert.AreEqual(patient.RecordNumber, again.OriginalFolio);
        Assert.AreEqual("5512345678", again.Phone, "Se conserva la edición de la laptop");
        Assert.AreEqual(counter, CloudCounter(ExpSeries));
    }
}
