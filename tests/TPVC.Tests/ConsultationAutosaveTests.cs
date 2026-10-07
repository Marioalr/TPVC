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

    private async Task<(MedicalRecordsViewModel Records, PosViewModel Pos, ConsultationService Consultations)> OpenAppAsync()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var pos = new PosViewModel(_service, consultations);
        var records = new MedicalRecordsViewModel(_service, consultations, pos, null, null, new ConsultationDraftStore(_draftPath));
        await records.InitialLoad;
        return (records, pos, consultations);
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

        records.ConsultationChiefComplaint = "Fiebre de 3 días";
        records.ConsultationTA = "130/85";
        records.ConsultationSpO2 = "95";
        records.ConsultationDiagnosis = "J00 Rinofaringitis aguda";
        records.ConsultationSection = 4;
        consultations.AddCartItemToActiveConsultation(-1, "INY-01", "Aplicación de inyección", null, null, "Servicio", 80m, 1);

        // Se guarda solo tras una pausa al escribir, sin pulsar nada
        await WaitUntilAsync(() => File.Exists(_draftPath) && records.AutosaveStatus.StartsWith("Guardado", StringComparison.Ordinal));

        // Cierre de la app (o apagón): se guarda lo último y se vuelve a abrir
        records.Dispose();
        var (reopened, pos, reopenedConsultations) = await OpenAppAsync();

        Assert.IsTrue(reopened.HasActiveConsultation, "La consulta se retoma al abrir");
        Assert.IsTrue(reopened.IsConsultationRecovered);
        StringAssert.StartsWith(reopened.AutosaveStatus, "Consulta recuperada");
        Assert.AreEqual(patient.Id, reopened.SelectedPatient?.Id);
        Assert.AreEqual(folio, reopenedConsultations.CurrentConsultation!.Folio, "Mismo folio: la nota no se duplica");
        Assert.AreEqual(startedAt, reopenedConsultations.CurrentConsultation.StartedAt, "La duración cuenta desde el inicio real");
        Assert.AreEqual("Fiebre de 3 días", reopened.ConsultationChiefComplaint);
        Assert.AreEqual("130/85", reopened.ConsultationTA);
        Assert.AreEqual("95", reopened.ConsultationSpO2);
        Assert.AreEqual("J00 Rinofaringitis aguda", reopened.ConsultationDiagnosis);
        Assert.AreEqual(4, reopened.ConsultationSection);
        var charge = reopenedConsultations.CurrentConsultation.Items.Single();
        Assert.AreEqual("Aplicación de inyección", charge.Name);
        Assert.AreEqual(80m, charge.UnitPrice);

        // Al cobrar, la consulta terminada ya no se recupera
        await reopened.FinalizeConsultationAsync();
        await pos.ProcessSaleAsync();
        await WaitUntilAsync(() => !reopenedConsultations.HasActiveConsultation);
        Assert.IsFalse(File.Exists(_draftPath));
        Assert.AreEqual(string.Empty, reopened.AutosaveStatus);
    }

    [TestMethod]
    public async Task DiscardedConsultation_IsNotResurrectedByAPendingSave()
    {
        var (records, _, _) = await OpenAppAsync();
        records.SelectPatient(records.Patients.First());
        records.StartActiveConsultation();
        records.SaveConsultationDraftNow();
        Assert.IsTrue(File.Exists(_draftPath));

        // Queda una escritura pendiente y se descarta la consulta antes de que ocurra
        records.AutosaveDelay = TimeSpan.FromMilliseconds(250);
        records.ConsultationChiefComplaint = "Paciente equivocado";
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
        records.SaveConsultationDraftNow();
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
}
