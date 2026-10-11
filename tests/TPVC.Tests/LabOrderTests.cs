using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Documents;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Laboratorio (v1.25.0): la orden de estudios de la consulta (sección 6), opcional, guardada con la nota y con su
/// propia hoja impresa (membrete de la receta, media carta o carta si no cabe).
/// </summary>
[TestClass]
[DoNotParallelize]
public class LabOrderTests
{
    private string _dbPath = string.Empty;
    private string _draftPath = string.Empty;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_lab_{id}.db");
        _draftPath = Path.Combine(Path.GetTempPath(), $"tpvc_lab_{id}", "consulta-en-curso.json");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        using (var db = new AppDbContext(builder.Options)) DatabaseInitializer.Initialize(db);
        _service = new InventoryService(new AppDbContextFactory(builder.Options));
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

    private static LabOrder Order(string indications, params string[] studies) => new()
    {
        Studies = studies.Select(LabCatalog.ToStudy).ToList(),
        Indications = indications
    };

    // ================================================================== la orden y su JSON

    [TestMethod]
    public void LabOrder_RoundTripsThroughItsColumn_WithReadableAccents()
    {
        var consultation = new PatientConsultation { LabOrder = Order("Ayuno de 8 a 12 horas.", "BH", "Ácido úrico", "Estudio raro del consultorio") };

        StringAssert.Contains(consultation.LabOrderJson, "\"Name\":\"Biometría hemática completa\"", "Acentos sin escapar: la app de Kotlin lee el mismo texto");
        var read = consultation.LabOrder;
        CollectionAssert.AreEqual(new[] { "Biometría hemática completa", "Ácido úrico", "Estudio raro del consultorio" }, read.Studies.Select(s => s.Name).ToArray());
        CollectionAssert.AreEqual(new[] { LabCatalog.Hematology, LabCatalog.Chemistry, LabCatalog.OtherArea }, read.Studies.Select(s => s.Area).ToArray());
        Assert.AreEqual("Ayuno de 8 a 12 horas.", read.Indications);
        Assert.AreEqual("Biometría hemática completa, Ácido úrico, Estudio raro del consultorio", read.Summary);
    }

    [TestMethod]
    public void LabOrder_WithoutStudies_IsSavedEmpty_AndUnreadableTextIsAnEmptyOrder()
    {
        Assert.AreEqual(string.Empty, new PatientConsultation { LabOrder = Order("Ayuno") }.LabOrderJson, "Indicaciones solas no son una orden");
        Assert.IsTrue(new PatientConsultation().LabOrder.IsEmpty);
        Assert.IsTrue(LabOrder.FromJson("{no es json").IsEmpty);
        // Otra app puede escribir las propiedades en minúsculas
        var lower = LabOrder.FromJson("{\"studies\":[{\"name\":\" BH \",\"area\":\"Hematología\"},{\"name\":\"\"}],\"indications\":\" Ayuno \"}");
        Assert.AreEqual("BH", lower.Studies.Single().Name);
        Assert.AreEqual("Ayuno", lower.Indications);
    }

    // ================================================================== catálogo

    [TestMethod]
    public void Catalog_Search_ByAbbreviationOrWords_WithoutAccents()
    {
        Assert.AreEqual("Biometría hemática completa", LabCatalog.Search("bh").First().Name, "La abreviatura exacta primero");
        Assert.AreEqual("Química sanguínea de 6 elementos", LabCatalog.Search("quimica 6").First().Name, "Cada palabra cuenta, sin acentos");
        Assert.AreEqual("Examen general de orina", LabCatalog.Search("EGO").First().Name);
        Assert.IsTrue(LabCatalog.Search("orina").Any(s => s.Name == "Prueba de embarazo en orina"));
        Assert.AreEqual(0, LabCatalog.Search("   ").Count);
        Assert.AreEqual(0, LabCatalog.Search("zzzz").Count);
        Assert.IsTrue(LabCatalog.Search("a", max: 5).Count <= 5);
    }

    [TestMethod]
    public void Catalog_Search_IncludesWhatTheClinicAlreadyOrdered_WithoutRepeatingTheCatalog()
    {
        var custom = new[]
        {
            new LabCatalogStudy("Panel viral respiratorio", LabCatalog.OtherArea) { IsCustom = true },
            new LabCatalogStudy("biometria hematica completa", LabCatalog.OtherArea) { IsCustom = true }
        };
        var found = LabCatalog.Search("panel viral", custom);
        Assert.IsTrue(found.Single().IsCustom);
        Assert.AreEqual(1, LabCatalog.Search("biometria", custom).Count, "El del catálogo no se repite con otra escritura");
    }

    [TestMethod]
    public void Catalog_FrequentAndPanels_AreAllInTheCatalog()
    {
        Assert.AreEqual("BH", LabCatalog.Frequent[0].ChipText);
        Assert.IsTrue(LabCatalog.Frequent.All(f => LabCatalog.Find(f.Name) != null));
        Assert.IsTrue(LabCatalog.Panels.SelectMany(p => p.Studies).All(name => LabCatalog.Find(name) != null), "Cada estudio de un paquete tiene su área");
        Assert.AreEqual("Examen general de orina", LabCatalog.ToStudy("ego").Name);
        Assert.AreEqual(LabCatalog.OtherArea, LabCatalog.ToStudy("Prueba de aliento").Area);
    }

    // ================================================================== la hoja

    private static PatientRecord Patient(string gender = "Femenino") => new() { FullName = "Lucía <Prueba> Laboratorio", Age = 41, Gender = gender };

    [TestMethod]
    public void Sheet_GroupsStudiesByAreaInTheCatalogOrder_AndLeavesPlaceholdersBlank()
    {
        var order = Order("Ayuno", "Prueba de aliento", "EGO", "Perfil de lípidos", "BH", "Glucosa en ayuno");
        var sheet = LabOrderSheet.Create(Patient("No especificado"), order, new ClinicProfile(), LabOrderSheet.PlaceholderDiagnosis, new DateTime(2026, 10, 11));

        CollectionAssert.AreEqual(new[] { LabCatalog.Hematology, LabCatalog.Chemistry, LabCatalog.Urinalysis, LabCatalog.OtherArea }, sheet.Groups.Select(g => g.Area).ToArray());
        CollectionAssert.AreEqual(new[] { "Perfil de lípidos", "Glucosa en ayuno" }, sheet.Groups[1].Studies.ToArray(), "Dentro del área, en el orden en que se pidieron");
        Assert.AreEqual(5, sheet.StudyCount);
        Assert.AreEqual(string.Empty, sheet.Sex, "Sin especificar queda en blanco");
        Assert.AreEqual(string.Empty, sheet.Diagnosis, "El diagnóstico de relleno no sale en la hoja");
        Assert.AreEqual("11/10/2026", sheet.Letterhead.Date);
        Assert.AreEqual(PrescriptionSheet.DefaultDoctorName, sheet.Letterhead.DoctorName, "El membrete es el de la receta");
    }

    [TestMethod]
    public void Sheet_ManyLines_GoInTwoColumns_WithoutSplittingAnArea()
    {
        var few = LabOrderSheet.Create(Patient(), Order("", "BH", "EGO"), null, "J02.9", DateTime.Today);
        Assert.AreEqual(1, LabOrderHtmlBuilder.SplitInColumns(few.Groups).Count);

        var many = LabOrderSheet.Create(Patient(), Order("", LabCatalog.Panels.SelectMany(p => p.Studies).Distinct().ToArray()), null, "E11", DateTime.Today);
        var columns = LabOrderHtmlBuilder.SplitInColumns(many.Groups);
        Assert.AreEqual(2, columns.Count);
        CollectionAssert.AreEqual(many.Groups.ToList(), columns.SelectMany(c => c).ToList(), "Todas las áreas, en orden y enteras");
    }

    [TestMethod]
    public void Html_HasTheLetterheadFields_TheTitle_TheStudiesAndTheIndications()
    {
        var order = Order("Ayuno de 8 a 12 horas.\nTraer resultados.", "BH", "EGO", "Estudio <especial> & raro");
        var sheet = LabOrderSheet.Create(Patient(), order, new ClinicProfile { DoctorName = "Dra. Prueba", ProfessionalLicense = "1234567" }, "J02.9 Faringitis aguda", new DateTime(2026, 10, 11));
        var html = LabOrderHtmlBuilder.Build(sheet);
        var doc = XDocument.Parse(html.Replace("<!DOCTYPE html>", string.Empty));

        string Field(string name) => doc.Descendants().Single(e => (string?)e.Attribute("data-field") == name).Value;
        bool HasClass(XElement e, string name) => ((string?)e.Attribute("class") ?? string.Empty).Split(' ').Contains(name);

        Assert.AreEqual("Lucía <Prueba> Laboratorio", Field("paciente"));
        Assert.AreEqual("11/10/2026", Field("fecha"));
        Assert.AreEqual("Femenino", Field("sexo"));
        Assert.AreEqual("J02.9 Faringitis aguda", Field("diagnostico"));
        Assert.AreEqual("Dra. Prueba", doc.Descendants().Single(e => HasClass(e, "doctor")).Value);
        Assert.AreEqual(LabOrderSheet.Title, doc.Descendants().Single(e => HasClass(e, "title")).Value);
        CollectionAssert.AreEqual(new[] { LabCatalog.Hematology, LabCatalog.Urinalysis, LabCatalog.OtherArea },
            doc.Descendants().Where(e => HasClass(e, "area-title")).Select(e => e.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "Biometría hemática completa", "Examen general de orina", "Estudio <especial> & raro" },
            doc.Descendants().Where(e => HasClass(e, "study")).Select(e => e.Value).ToArray());
        Assert.AreEqual("Ayuno de 8 a 12 horas.\nTraer resultados.", doc.Descendants().Single(e => HasClass(e, "indications-text")).Value);
        StringAssert.Contains(html, "@page { size: 8.5in 5.5in;", "Media carta horizontal, como la receta");
        Assert.IsFalse(html.Contains("studies two", StringComparison.Ordinal), "Pocos estudios: una columna");
    }

    [TestMethod]
    public void PrescriptionHtml_KeepsItsLayoutAfterSharingTheLetterhead()
    {
        var sheet = PrescriptionSheet.Create(Patient(), new[] { new PatientPrescriptionItem { MedicineName = "Paracetamol 500 mg tabletas", Dosage = "1 cada 8 horas" } },
            new ClinicProfile(), "Reposo", DateTime.Today);
        var html = PrescriptionHtmlBuilder.Build(sheet);
        var doc = XDocument.Parse(html.Replace("<!DOCTYPE html>", string.Empty));
        var sheetDiv = doc.Descendants().Single(e => (string?)e.Attribute("class") == "sheet");
        CollectionAssert.AreEqual(new[] { "header", "row patient", "row vitals", "body", "footer" }, sheetDiv.Elements().Select(e => (string?)e.Attribute("class")).ToArray());
        Assert.IsTrue(html.IndexOf(".measures-text", StringComparison.Ordinal) < html.IndexOf(".footer {", StringComparison.Ordinal), "Los estilos siguen en su orden");
    }

    [TestMethod]
    [TestCategory("Windows")]
    public void Paper_FewStudiesFitInHalfLetter_ManyGoToLetter()
    {
        var few = LabOrderSheet.Create(Patient(), Order("Ayuno", "BH", "QS 6", "EGO", "Perfil de lípidos"), null, "Chequeo", DateTime.Today);
        Assert.AreEqual(PrescriptionPaper.HalfLetter, PrintingService.MeasureLabOrderPaper(few));

        var all = LabOrderSheet.Create(Patient(), Order("Ayuno", LabCatalog.Studies.Select(s => s.Name).ToArray()), null, "Chequeo", DateTime.Today);
        Assert.AreEqual(PrescriptionPaper.Letter, PrintingService.MeasureLabOrderPaper(all));
    }

    // ================================================================== la sección de la consulta

    /// <summary>Impresora que solo anota la orden que recibe.</summary>
    private sealed class RecordingPrinter : IPrescriptionOutput
    {
        public List<(LabOrder Order, string? Diagnosis, PatientConsultation? Consultation)> Labs { get; } = new();

        public bool PrintPrescription(PatientRecord patient, IEnumerable<PatientPrescriptionItem> items, ClinicProfile profile, string? generalNotes = null,
            PatientConsultation? consultation = null) => true;

        public string? ExportPrescriptionDocument(PatientRecord patient, IEnumerable<PatientPrescriptionItem> items, ClinicProfile profile,
            string? generalNotes = null, string? targetFilePath = null, PatientConsultation? consultation = null) => null;

        public bool PrintLabOrder(PatientRecord patient, LabOrder order, ClinicProfile profile, string? diagnosis, PatientConsultation? consultation = null)
        {
            Labs.Add((order, diagnosis, consultation));
            return true;
        }

        public string? ExportLabOrderDocument(PatientRecord patient, LabOrder order, ClinicProfile profile, string? diagnosis, string? targetFilePath = null,
            PatientConsultation? consultation = null) => "laboratorio.html";
    }

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

    private async Task<(PatientsWorkspace Workspace, RecordingPrinter Printer, ConsultationService Consultations)> StartConsultationAsync()
    {
        var saved = await _service.SavePatientAsync(new PatientRecord { FullName = "Lucía Prueba Laboratorio", Age = 41, Gender = "Femenino" });
        var printer = new RecordingPrinter();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var workspace = new PatientsWorkspace(new PatientsPlatform(new InlineUiThread(), new NoDialogs(), new NoUrls(), printer), _service, consultations,
            consultationDraftStore: new ConsultationDraftStore(_draftPath));
        await workspace.InitialLoad;
        workspace.SelectPatient(workspace.Patients.Single(p => p.Id == saved.Id));
        workspace.StartActiveConsultation();
        Assert.IsTrue(workspace.HasActiveConsultation);
        return (workspace, printer, consultations);
    }

    [TestMethod]
    public async Task Section6_AddsFromFrequentPanelsSearchAndTyping_WithoutRepeating()
    {
        var (workspace, _, _) = await StartConsultationAsync();
        var labs = workspace.Labs;
        Assert.AreEqual(6, ConsultationViewModel.ConsultationSectionCount);
        Assert.AreEqual("Sin estudios (opcional)", labs.Order.Summary);

        labs.AddFrequent(LabCatalog.Frequent[0]);                                  // BH
        labs.AddPanel(LabCatalog.Panels.Single(p => p.Name == "Chequeo general"));  // BH (ya estaba) + QS 6 + lípidos + EGO
        Assert.AreEqual(4, labs.Order.Studies.Count, "Lo que ya estaba no se repite");

        labs.StudySearchText = "hba1c";
        Assert.IsTrue(labs.HasSuggestions);
        Assert.AreEqual(0, labs.SelectedSuggestionIndex, "Enter toma la primera");
        labs.AcceptStudy();
        Assert.AreEqual("Hemoglobina glucosilada", labs.Order.Studies.Last().Name);
        Assert.AreEqual(string.Empty, labs.StudySearchText);
        Assert.IsFalse(labs.HasSuggestions);

        labs.StudySearchText = "Prueba de aliento con urea";
        await labs.CustomStudiesLoaded;
        Assert.IsFalse(labs.HasSuggestions);
        labs.AcceptStudy();
        Assert.AreEqual(LabCatalog.OtherArea, labs.Order.Studies.Last().Area, "Lo escrito fuera del catálogo va en Otros");

        labs.StudySearchText = "aliento";
        Assert.IsTrue(labs.Suggestions.Single().IsCustom, "Lo escrito se vuelve a sugerir en la misma sesión");
        labs.StudySearchText = string.Empty;

        labs.AppendIndication(LabCatalog.IndicationPresets[0]);
        labs.AppendIndication(LabCatalog.IndicationPresets[0]);
        Assert.AreEqual(LabCatalog.IndicationPresets[0].Text, labs.Order.Indications, "Una indicación no se repite");

        labs.RemoveStudy(labs.Order.Studies.First());
        Assert.AreEqual(5, labs.Order.Studies.Count);
        StringAssert.StartsWith(labs.Order.Summary, "Química sanguínea de 6 elementos");
    }

    [TestMethod]
    public async Task SavedNote_KeepsTheLabOrder_TheCardShowsIt_AndTheDetailPrintsIt()
    {
        var (workspace, printer, _) = await StartConsultationAsync();
        workspace.Consultation.ConsultationDiagnosis = "E11 Diabetes mellitus tipo 2";
        workspace.Labs.AddPanel(LabCatalog.Panels.Single(p => p.Name == "Control de diabetes"));
        workspace.Labs.AppendIndication(LabCatalog.IndicationPresets[0]);

        // Imprimir durante la consulta lleva el diagnóstico que se está escribiendo
        workspace.Labs.Print();
        Assert.AreEqual("E11 Diabetes mellitus tipo 2", printer.Labs.Single().Diagnosis);
        Assert.AreEqual(6, printer.Labs.Single().Order.Studies.Count);
        Assert.IsNull(printer.Labs.Single().Consultation, "La de hoy");

        var note = await workspace.Consultation.SaveActiveConsultationToDbAsync();
        Assert.IsNotNull(note);
        var stored = (await _service.GetPatientByIdAsync(workspace.SelectedPatient!.Id))!.Consultations.Single();
        Assert.AreEqual(6, stored.LabOrder.Studies.Count);
        Assert.AreEqual(LabCatalog.IndicationPresets[0].Text, stored.LabOrder.Indications);

        var card = PatientChartViewModel.BuildTimeline(new[] { stored }, Array.Empty<PatientAppointment>()).Single();
        StringAssert.StartsWith(card.Lines.Single(l => l.Label == "Laboratorio").Value, "Glucosa en ayuno · Hemoglobina glucosilada");
        StringAssert.Contains(card.Detail, "Microalbuminuria", "La búsqueda del historial encuentra los estudios");

        workspace.ConsultationDetail.Open(stored);
        Assert.IsTrue(workspace.ConsultationDetail.HasLabOrder);
        Assert.IsTrue(workspace.ConsultationDetail.HasLabIndications);
        workspace.ConsultationDetail.PrintLabOrder();
        Assert.AreSame(stored, printer.Labs.Last().Consultation, "Una consulta anterior sale con su fecha");
        Assert.AreEqual("E11 Diabetes mellitus tipo 2", printer.Labs.Last().Diagnosis);
    }

    [TestMethod]
    public async Task ConsultationWithoutLabs_SavesAnEmptyOrder_AndTheCardHasNoLabLine()
    {
        var (workspace, _, _) = await StartConsultationAsync();
        workspace.Consultation.ConsultationDiagnosis = "J00 Rinofaringitis aguda";
        var note = await workspace.Consultation.SaveActiveConsultationToDbAsync();
        Assert.AreEqual(string.Empty, note!.LabOrderJson);
        var card = PatientChartViewModel.BuildTimeline(new[] { note }, Array.Empty<PatientAppointment>()).Single();
        Assert.IsFalse(card.Lines.Any(l => l.Label == "Laboratorio"));
        workspace.ConsultationDetail.Open(note);
        Assert.IsFalse(workspace.ConsultationDetail.HasLabOrder);
    }

    [TestMethod]
    public async Task LabOrder_TravelsInTheDraft_AndIsClearedForTheNextConsultation()
    {
        var (workspace, _, consultations) = await StartConsultationAsync();
        workspace.Labs.AddFrequent(LabCatalog.Frequent[2]); // EGO
        workspace.Labs.Order.Indications = "Primera orina de la mañana";
        workspace.Consultation.ConsultationSection = 6;
        workspace.Autosave.SaveConsultationDraftNow();

        var draft = new ConsultationDraftStore(_draftPath).Load();
        Assert.AreEqual("Examen general de orina", draft!.LabOrder!.Studies.Single().Name);
        Assert.AreEqual(6, draft.Section);
        workspace.Dispose();

        // Al volver a abrir, la consulta se retoma con su orden
        var reopened = new PatientsWorkspace(new PatientsPlatform(new InlineUiThread(), new NoDialogs(), new NoUrls(), new RecordingPrinter()), _service,
            new ConsultationService(seedDemoPatients: false), consultationDraftStore: new ConsultationDraftStore(_draftPath));
        await reopened.InitialLoad;
        Assert.IsTrue(reopened.HasActiveConsultation);
        Assert.AreEqual("Examen general de orina", reopened.Labs.Order.Studies.Single().Name);
        Assert.AreEqual("Primera orina de la mañana", reopened.Labs.Order.Indications);
        Assert.AreEqual(6, reopened.Consultation.ConsultationSection);

        // Al descartarla, la orden se vacía para la siguiente
        await reopened.DiscardActiveConsultationAsync();
        Assert.IsFalse(reopened.Labs.Order.HasStudies);
        Assert.AreEqual(string.Empty, reopened.Labs.Order.Indications);
    }

    [TestMethod]
    public void DraftWithoutLabOrder_DoesNotWriteTheProperty()
    {
        var store = new ConsultationDraftStore(_draftPath);
        store.Save(new ConsultationDraft { PatientId = 1, Folio = "CNS-1" });
        StringAssert.DoesNotMatch(File.ReadAllText(_draftPath), new System.Text.RegularExpressions.Regex("LabOrder"),
            "Los borradores sin laboratorio se escriben como antes (y una versión anterior los lee igual)");
    }

    [TestMethod]
    public async Task ClinicCustomStudies_AreSuggested_MostOrderedFirst()
    {
        var patient = await _service.SavePatientAsync(new PatientRecord
        {
            FullName = "Paciente Estudios",
            Consultations = new List<PatientConsultation>
            {
                new() { Folio = "CNS-A", Date = DateTime.Today, LabOrder = Order("", "BH", "Panel de alergenos") },
                new() { Folio = "CNS-B", Date = DateTime.Today, LabOrder = Order("", "Panel de alergenos", "Cultivo de uña") },
                new() { Folio = "CNS-C", Date = DateTime.Today }
            }
        });
        Assert.IsTrue(patient.Id > 0);

        var custom = await _service.GetCustomLabStudiesAsync();
        CollectionAssert.AreEqual(new[] { "Panel de alergenos", "Cultivo de uña" }, custom.Select(s => s.Name).ToArray(), "Sin los del catálogo; lo más pedido primero");
    }
}
