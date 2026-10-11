using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// La ficha agrupa por consulta (v1.24.0): una tarjeta por consulta y, al abrirla, la hoja con el detalle por datos,
/// donde la receta se corrige e imprime después de terminar la consulta.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConsultationDetailTests
{
    private string _dbPath = string.Empty;
    private InventoryService _service = null!;
    private DbContextOptions<AppDbContext> _options = null!;

    private sealed class NoDialogs : IUserDialogs
    {
        public bool Confirm(string message, string title) => true;
        public Task<bool> ConfirmAsync(string message, string title) => Task.FromResult(true);
        public void ShowError(string message) { }
    }

    private sealed class NoUrls : IUrlLauncher
    {
        public void Open(string url) { }
    }

    /// <summary>Impresora de receta que solo anota lo que recibe.</summary>
    private sealed class RecordingPrinter : IPrescriptionOutput
    {
        public List<(List<PatientPrescriptionItem> Items, string? Notes, PatientConsultation? Consultation)> Printed { get; } = new();

        public bool PrintPrescription(PatientRecord patient, IEnumerable<PatientPrescriptionItem> items, ClinicProfile profile, string? generalNotes = null,
            PatientConsultation? consultation = null)
        {
            Printed.Add((items.ToList(), generalNotes, consultation));
            return true;
        }

        public string? ExportPrescriptionDocument(PatientRecord patient, IEnumerable<PatientPrescriptionItem> items, ClinicProfile profile,
            string? generalNotes = null, string? targetFilePath = null, PatientConsultation? consultation = null) => "receta.html";
    }

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_consulta_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        _options = builder.Options;
        using (var db = new AppDbContext(_options)) DatabaseInitializer.Initialize(db);
        _service = new InventoryService(new AppDbContextFactory(_options));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private static PatientConsultation Consultation(string folio, DateTime date, string diagnosis, params PatientPrescriptionItem[] prescriptions) => new()
    {
        Folio = folio,
        Date = date,
        StartedAt = date,
        EndedAt = date.AddMinutes(20),
        ChiefComplaint = "Dolor de garganta",
        SystemsReview = "Sin datos de alarma",
        PhysicalExamination = "Faringe hiperémica",
        Diagnosis = diagnosis,
        TreatmentPlan = "Reposo e hidratación",
        VitalSigns = "T/A: 118/76 | Temp: 37.9 °C",
        BloodPressureSystolic = 118,
        BloodPressureDiastolic = 76,
        Temperature = 37.9m,
        Prescriptions = prescriptions.ToList()
    };

    /// <summary>Un paciente con dos consultas (la de hoy es la última) y Pacientes abierto en su ficha.</summary>
    private async Task<(PatientsWorkspace Workspace, RecordingPrinter Printer, PatientRecord Patient)> OpenChartAsync()
    {
        var older = Consultation("CNS-1", DateTime.Today.AddDays(-30), "J00 Rinofaringitis",
            new PatientPrescriptionItem { MedicineName = "Loratadina 10 mg tabletas", Dosage = "1 tableta cada 24 horas por 5 días" });
        var latest = Consultation("CNS-2", DateTime.Today.AddHours(9), "J02.9 Faringitis aguda",
            new PatientPrescriptionItem { MedicineName = "Paracetamol 500 mg tabletas", Presentation = "Caja con 20 tabletas", Dosage = "1 tableta cada 8 horas por 3 días" },
            new PatientPrescriptionItem { MedicineName = "Ibuprofeno 400 mg tabletas", Dosage = "1 tableta cada 8 horas si hay dolor" });
        var saved = await _service.SavePatientAsync(new PatientRecord
        {
            FullName = "Ana Prueba Consulta",
            Age = 34,
            Consultations = new List<PatientConsultation> { older, latest },
            ActivePrescription = latest.Prescriptions.ToList()
        });

        var printer = new RecordingPrinter();
        var workspace = new PatientsWorkspace(new PatientsPlatform(new InlineUiThread(), new NoDialogs(), new NoUrls(), printer), _service,
            new ConsultationService(seedDemoPatients: false));
        await workspace.LoadInitialDataAsync();
        var patient = workspace.Patients.Single(p => p.Id == saved.Id);
        workspace.OpenPatientChart(patient);
        for (int i = 0; i < 100 && workspace.Chart.Timeline.Count(t => t.Consultation != null) < 2; i++) await Task.Delay(20);
        Assert.AreEqual(2, workspace.Chart.Timeline.Count(t => t.Consultation != null), "Una tarjeta por consulta");
        return (workspace, printer, workspace.SelectedPatient!);
    }

    private static PatientConsultation Card(PatientsWorkspace workspace, string folio) =>
        workspace.Chart.Timeline.Single(t => t.Consultation?.Folio == folio).Consultation!;

    [TestMethod]
    public async Task TheCard_SummarizesTheConsultation_AndTheSheetShowsItByData()
    {
        var (workspace, _, _) = await OpenChartAsync();
        var card = workspace.Chart.Timeline.First(t => t.Consultation != null);
        Assert.AreEqual("J02.9 Faringitis aguda", card.Title, "La más reciente arriba");
        CollectionAssert.AreEqual(new[] { "Motivo", "Signos", "Receta", "Plan" }, card.Lines.Select(l => l.Label).ToArray());
        Assert.AreEqual("Paracetamol 500 mg tabletas · Ibuprofeno 400 mg tabletas", card.Lines.Single(l => l.Label == "Receta").Value);

        var detail = workspace.ConsultationDetail;
        detail.Open(card.Consultation);
        Assert.IsTrue(detail.IsOpen);
        Assert.IsTrue(workspace.IsAnySheetOpen);
        StringAssert.StartsWith(detail.Title, "Consulta del ");
        StringAssert.Contains(detail.Subtitle, "CNS-2");
        CollectionAssert.AreEqual(new[] { "Motivo de consulta", "Interrogatorio por aparatos y sistemas", "Exploración física", "Diagnóstico", "Plan y tratamiento" },
            detail.Sections.Select(s => s.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "T/A (mmHg)", "Temp (°C)" }, detail.Vitals.Select(v => v.Label).ToArray());
        Assert.AreEqual("118/76", detail.Vitals[0].Value);
        Assert.HasCount(2, detail.Prescription);
        Assert.IsTrue(detail.CanEditPrescription);
    }

    [TestMethod]
    public async Task CorrectingTheLatestPrescription_SavesItInTheConsultation_AndAsTheCurrentOne()
    {
        var (workspace, _, patient) = await OpenChartAsync();
        var detail = workspace.ConsultationDetail;
        detail.Open(Card(workspace, "CNS-2"));

        detail.StartEditPrescription();
        Assert.IsTrue(detail.IsEditingPrescription);
        Assert.HasCount(2, detail.EditLines);
        detail.EditLines[0].Dosage = "1 tableta vía oral cada 8 horas por 5 días";
        detail.RemoveLine(detail.EditLines[1]);

        // Del catálogo, en el orden normado y con su presentación (el producto demo tiene su genérico)
        detail.NewMedicineText = "amoxi";
        var amoxicillin = detail.MedicineSuggestions.First();
        detail.AddSuggestedMedicine(amoxicillin);
        Assert.AreEqual(PrescriptionNaming.MedicineLine(amoxicillin), detail.EditLines[^1].MedicineName);
        Assert.AreEqual(amoxicillin.Presentation, detail.EditLines[^1].Presentation);
        detail.NewMedicineText = "Nebulizaciones con solución salina";
        detail.AddTypedMedicine();

        await detail.SavePrescriptionAsync();
        Assert.IsFalse(detail.IsEditingPrescription);
        Assert.HasCount(3, detail.Prescription);

        var stored = (await _service.GetPatientConsultationsAsync(patient.Id)).Single(c => c.Folio == "CNS-2").Prescriptions;
        CollectionAssert.AreEqual(new[] { "Paracetamol 500 mg tabletas", PrescriptionNaming.MedicineLine(amoxicillin), "Nebulizaciones con solución salina" },
            stored.Select(i => i.MedicineName).ToArray());
        Assert.AreEqual("1 tableta vía oral cada 8 horas por 5 días", stored[0].Dosage);
        Assert.AreEqual("Caja con 20 tabletas", stored[0].Presentation);

        var storedPatient = (await _service.GetPatientByIdAsync(patient.Id))!;
        Assert.HasCount(3, storedPatient.ActivePrescription, "La última consulta: también es la receta vigente");
        Assert.HasCount(3, patient.ActivePrescription, "Y se ve así en la ficha");

        var card = workspace.Chart.Timeline.Single(t => t.Consultation?.Folio == "CNS-2");
        StringAssert.Contains(card.Lines.Single(l => l.Label == "Receta").Value, "Nebulizaciones", "La tarjeta se rehace");
    }

    [TestMethod]
    public async Task CorrectingAnOlderPrescription_DoesNotChangeTheCurrentOne()
    {
        var (workspace, _, patient) = await OpenChartAsync();
        var detail = workspace.ConsultationDetail;
        detail.Open(Card(workspace, "CNS-1"));
        detail.StartEditPrescription();
        detail.EditLines[0].Dosage = "1 tableta cada 24 horas por 7 días";
        await detail.SavePrescriptionAsync();

        var stored = (await _service.GetPatientConsultationsAsync(patient.Id)).Single(c => c.Folio == "CNS-1");
        Assert.AreEqual("1 tableta cada 24 horas por 7 días", stored.Prescriptions.Single().Dosage);
        var storedPatient = (await _service.GetPatientByIdAsync(patient.Id))!;
        CollectionAssert.AreEqual(new[] { "Paracetamol 500 mg tabletas", "Ibuprofeno 400 mg tabletas" },
            storedPatient.ActivePrescription.Select(i => i.MedicineName).ToArray(), "La receta vigente sigue siendo la de la última consulta");
    }

    [TestMethod]
    public async Task PrintingAnOlderPrescription_UsesThatConsultation()
    {
        var (workspace, printer, _) = await OpenChartAsync();
        var older = Card(workspace, "CNS-1");
        workspace.ConsultationDetail.Open(older);
        workspace.ConsultationDetail.PrintPrescription();

        var printed = printer.Printed.Single();
        Assert.AreSame(older, printed.Consultation, "Con la fecha y los signos de esa consulta");
        Assert.AreEqual("Reposo e hidratación", printed.Notes, "Sus indicaciones");
        Assert.AreEqual("Loratadina 10 mg tabletas", printed.Items.Single().MedicineName);

        // La hoja impresa toma los signos de la consulta, no los del expediente
        var sheet = PrescriptionSheet.Create(new PatientRecord { FullName = "Ana", VitalSigns = "T/A: 100/60" }, printed.Items, new ClinicProfile(), null,
            older.Date, null, older.VitalSigns);
        StringAssert.StartsWith(sheet.BloodPressure, "118/76", "La T/A de la consulta, no la del expediente (100/60)");
        Assert.AreEqual(PrescriptionSheet.FormatDate(older.Date), sheet.Date);
    }

    [TestMethod]
    public async Task WithAConsultationInProgress_ThePrescriptionIsCorrectedInsideIt()
    {
        var (workspace, _, _) = await OpenChartAsync();
        var latest = Card(workspace, "CNS-2");
        workspace.StartActiveConsultation();
        workspace.ConsultationDetail.Open(latest);
        Assert.IsFalse(workspace.ConsultationDetail.CanEditPrescription);
        workspace.ConsultationDetail.StartEditPrescription();
        Assert.IsFalse(workspace.ConsultationDetail.IsEditingPrescription);
    }

    [TestMethod]
    public async Task Escape_CancelsTheCorrectionFirst_ThenClosesTheSheet()
    {
        var (workspace, _, _) = await OpenChartAsync();
        workspace.ConsultationDetail.Open(Card(workspace, "CNS-2"));
        workspace.ConsultationDetail.StartEditPrescription();

        Assert.IsTrue(workspace.TryCloseTopmost());
        Assert.IsFalse(workspace.ConsultationDetail.IsEditingPrescription);
        Assert.IsTrue(workspace.ConsultationDetail.IsOpen);
        Assert.IsTrue(workspace.TryCloseTopmost());
        Assert.IsFalse(workspace.ConsultationDetail.IsOpen);
    }
}
