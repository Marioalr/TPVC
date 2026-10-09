using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Guardado automático de la consulta en curso (v1.11): se recupera tras cerrar la app y nunca
/// revive una consulta que ya se cobró o descartó.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConsultationAutosaveTests
{
    private string _dbPath = string.Empty;
    private string _draftPath = string.Empty;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_autosave_{id}.db");
        _draftPath = Path.Combine(Path.GetTempPath(), $"tpvc_autosave_{id}", "consulta-en-curso.json");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        var options = builder.Options;

        using (var db = new AppDbContext(options))
        {
            DatabaseInitializer.Initialize(db);
        }
        _service = new InventoryService(new AppDbContextFactory(options));
        DialogService.ConfirmHandler = (_, _) => true;
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
        try { Directory.Delete(Path.GetDirectoryName(_draftPath)!, recursive: true); } catch { }
    }

    private async Task<(MedicalRecordsViewModel Records, CheckoutViewModel Checkout, ConsultationService Consultations)> OpenAppAsync()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(_service, consultations, consultationDraftStore: new ConsultationDraftStore(_draftPath));
        await records.InitialLoad;
        return (records, records.Consultation.Checkout, consultations);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 20) await Task.Delay(20);
        Assert.IsTrue(condition());
    }

    [TestMethod]
    public async Task ConsultationIsSavedWhileTyping_AndRecoveredAfterRestart()
    {
        var (records, _, consultations) = await OpenAppAsync();
        records.AutosaveDelay = TimeSpan.FromMilliseconds(50);
        var patient = records.Patients.First();
        records.SelectPatient(patient);
        records.StartActiveConsultation();
        var folio = consultations.CurrentConsultation!.Folio;
        var startedAt = consultations.CurrentConsultation.StartedAt;

        records.Consultation.ConsultationChiefComplaint = "Fiebre de 3 días";
        records.Consultation.ConsultationTA = "130/85";
        records.Consultation.ConsultationSpO2 = "95";
        records.Consultation.ConsultationDiagnosis = "J00 Rinofaringitis aguda";
        records.Consultation.ConsultationSection = 4;
        consultations.AddCartItemToActiveConsultation(-1, "INY-01", "Aplicación de inyección", null, null, "Servicio", 80m, 1);

        // Se guarda solo tras una pausa al escribir, sin pulsar nada
        await WaitUntilAsync(() => File.Exists(_draftPath) && records.Autosave.AutosaveStatus.StartsWith("Guardado", StringComparison.Ordinal));

        // Cierre de la app (o apagón): se guarda lo último y se vuelve a abrir
        records.Dispose();
        var (reopened, checkout, reopenedConsultations) = await OpenAppAsync();

        Assert.IsTrue(reopened.HasActiveConsultation, "La consulta se retoma al abrir");
        Assert.IsTrue(reopened.IsConsultationRecovered);
        StringAssert.StartsWith(reopened.Autosave.AutosaveStatus, "Consulta recuperada");
        Assert.AreEqual(patient.Id, reopened.SelectedPatient?.Id);
        Assert.AreEqual(folio, reopenedConsultations.CurrentConsultation!.Folio, "Mismo folio: la nota no se duplica");
        Assert.AreEqual(startedAt, reopenedConsultations.CurrentConsultation.StartedAt, "La duración cuenta desde el inicio real");
        Assert.AreEqual("Fiebre de 3 días", reopened.Consultation.ConsultationChiefComplaint);
        Assert.AreEqual("130/85", reopened.Consultation.ConsultationTA);
        Assert.AreEqual("95", reopened.Consultation.ConsultationSpO2);
        Assert.AreEqual("J00 Rinofaringitis aguda", reopened.Consultation.ConsultationDiagnosis);
        Assert.AreEqual(4, reopened.Consultation.ConsultationSection);
        var charge = reopenedConsultations.CurrentConsultation.Items.Single();
        Assert.AreEqual("Aplicación de inyección", charge.Name);
        Assert.AreEqual(80m, charge.UnitPrice);

        // Al cobrar, la consulta terminada ya no se recupera
        await reopened.Consultation.FinalizeConsultationAsync();
        await checkout.ChargeAsync();
        await WaitUntilAsync(() => !reopenedConsultations.HasActiveConsultation);
        Assert.IsFalse(File.Exists(_draftPath));
        Assert.AreEqual(string.Empty, reopened.Autosave.AutosaveStatus);
    }

    [TestMethod]
    public async Task DiscardedConsultation_IsNotResurrectedByAPendingSave()
    {
        var (records, _, _) = await OpenAppAsync();
        records.SelectPatient(records.Patients.First());
        records.StartActiveConsultation();
        records.Autosave.SaveConsultationDraftNow();
        Assert.IsTrue(File.Exists(_draftPath));

        // Queda una escritura pendiente y se descarta la consulta antes de que ocurra
        records.AutosaveDelay = TimeSpan.FromMilliseconds(250);
        records.Consultation.ConsultationChiefComplaint = "Paciente equivocado";
        await records.DiscardActiveConsultationAsync();

        await Task.Delay(600);
        Assert.IsFalse(File.Exists(_draftPath), "Una escritura atrasada no revive la consulta descartada");

        var (reopened, _, _) = await OpenAppAsync();
        Assert.IsFalse(reopened.HasActiveConsultation);
    }

    [TestMethod]
    public async Task DraftOfDeletedPatient_IsDiscarded()
    {
        new ConsultationDraftStore(_draftPath).Save(new ConsultationDraft
        {
            PatientId = 987654,
            PatientName = "Expediente dado de baja",
            Folio = "CNS-20261007-120000",
            StartedAt = DateTime.Now.AddMinutes(-5)
        });

        var (records, _, _) = await OpenAppAsync();

        Assert.IsFalse(records.HasActiveConsultation);
        Assert.IsFalse(File.Exists(_draftPath));
    }

    [TestMethod]
    public async Task DamagedDraft_DoesNotBlockStartup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_draftPath)!);
        File.WriteAllText(_draftPath, "{ esto no es json");

        var (records, _, _) = await OpenAppAsync();

        Assert.IsFalse(records.HasActiveConsultation);
        Assert.IsFalse(File.Exists(_draftPath));
        Assert.IsTrue(File.Exists(_draftPath + ".danado"), "Se conserva aparte para revisarlo");

        // Y la siguiente consulta se guarda con normalidad
        records.SelectPatient(records.Patients.First());
        records.StartActiveConsultation();
        records.Autosave.SaveConsultationDraftNow();
        Assert.IsNotNull(new ConsultationDraftStore(_draftPath).Load());
    }

    [TestMethod]
    public void DraftStore_RoundTripsAccountAndPrescription()
    {
        var store = new ConsultationDraftStore(_draftPath);
        var draft = new ConsultationDraft
        {
            PatientId = 3,
            Folio = "CNS-1",
            StartedAt = new DateTime(2026, 10, 7, 10, 30, 0),
            Diagnosis = "Ácido úrico elevado",
            Items = { new ConsultationProductItem { ProductId = 7, Name = "Paracetamol", UnitPrice = 45m, Quantity = 2 } },
            Prescriptions = { new PatientPrescriptionItem { MedicineName = "Paracetamol", Dosage = "cada 8 h", DispenseInClinic = true } }
        };

        Assert.IsTrue(store.Save(draft));
        var loaded = store.Load()!;

        Assert.AreEqual("Ácido úrico elevado", loaded.Diagnosis);
        Assert.AreEqual(draft.StartedAt, loaded.StartedAt);
        Assert.AreEqual(2, loaded.Items.Single().Quantity);
        Assert.AreEqual(90m, loaded.Items.Single().Subtotal);
        Assert.IsTrue(loaded.Prescriptions.Single().DispenseInClinic);
        Assert.IsFalse(File.Exists(_draftPath + ".tmp"), "Sin archivos temporales sueltos");
    }

    [TestMethod]
    public async Task AttendedAppointment_IsInTheDraftBeforeAnyEdit_AndTheRecoveredConsultationMarksIt()
    {
        var (records, _, _) = await OpenAppAsync();
        records.AutosaveDelay = TimeSpan.FromMilliseconds(50);
        var patient = records.Patients.First();
        var appointment = await _service.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            Date = DateTime.Today,
            Time = "23:30",
            Reason = "Dolor de garganta"
        });
        Assert.IsTrue(appointment.Id > 0);

        records.AttendAppointment(appointment);
        Assert.IsTrue(records.HasActiveConsultation);

        // Sin tocar nada más, la copia guardada sola ya lleva la cita
        var store = new ConsultationDraftStore(_draftPath);
        await WaitUntilAsync(() => store.Load()?.AttendingAppointmentId == appointment.Id);

        // El programa se cae (sin guardar al cerrar) y se vuelve a abrir: al cobrar, la cita queda atendida
        var (reopened, checkout, reopenedConsultations) = await OpenAppAsync();
        Assert.IsTrue(reopened.HasActiveConsultation);
        await reopened.Consultation.FinalizeConsultationAsync();
        checkout.SelectedPaymentMethod = "Efectivo";
        checkout.CashReceived = 5000m;
        await checkout.ChargeAsync();
        await WaitUntilAsync(() => !reopenedConsultations.HasActiveConsultation && reopened.Navigation.IsHomePage);
        var stored =(await _service.GetAppointmentsByDayAsync(DateTime.Today)).Single(a => a.Id == appointment.Id);
        Assert.AreEqual("Atendida", stored.Status);
    }
}
