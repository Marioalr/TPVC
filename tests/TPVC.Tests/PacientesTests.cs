using System;
using System.Collections.Generic;
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
/// Sección Pacientes v1.14: inicio con búsqueda, ficha del paciente (lo esencial, antecedentes
/// según el paciente e historial único), alta por secciones y agenda como panel lateral.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PacientesTests
{
    private string _dbPath = string.Empty;
    private DbContextOptions<AppDbContext> _options = null!;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_pacientes_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        _options = builder.Options;

        using (var db = new AppDbContext(_options))
        {
            DatabaseInitializer.Initialize(db);
        }
        _service = new InventoryService(new AppDbContextFactory(_options));
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
    }

    private async Task<(MedicalRecordsViewModel Records, CheckoutViewModel Checkout, ConsultationService Consultations)> CreateRecordsAsync()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(_service, consultations);
        await records.LoadInitialDataAsync();
        return (records, records.Checkout, consultations);
    }

    private static PatientRecord Patient(string name, string record = "", string phone = "", string diagnosis = "") => new()
    {
        FullName = name,
        RecordNumber = record,
        Phone = phone,
        LastDiagnosis = diagnosis
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.IsTrue(condition());
    }

    // ------------------------------------------------------------------ búsqueda

    [TestMethod]
    public void RankPatients_PrefixThenWordThenContains_IgnoringAccents()
    {
        var patients = new[]
        {
            Patient("Ramírez Ana", "EXP-001"),
            Patient("Ana María López", "EXP-002"),
            Patient("Juliana Pérez", "EXP-003"),
            Patient("Carlos Anaya", "EXP-004"),
            Patient("Pedro Gómez", "EXP-005", diagnosis: "Fisura anal"),
            Patient("Luis Torres", "EXP-006")
        };

        var result = MedicalRecordsViewModel.RankPatients(patients, "ana").Select(p => p.FullName).ToList();

        Assert.AreEqual("Ana María López", result[0], "Primero quien empieza así");
        CollectionAssert.AreEquivalent(new[] { "Carlos Anaya", "Ramírez Ana" }, result.Skip(1).Take(2).ToList(), "Luego una palabra que empieza así");
        CollectionAssert.AreEquivalent(new[] { "Juliana Pérez", "Pedro Gómez" }, result.Skip(3).ToList(), "Al final quien lo contiene (nombre o diagnóstico)");
        Assert.HasCount(5, result);

        Assert.AreEqual("Pedro Gómez", MedicalRecordsViewModel.RankPatients(patients, "GOMEZ").Single().FullName, "Sin acentos ni mayúsculas");
        Assert.AreEqual("Luis Torres", MedicalRecordsViewModel.RankPatients(patients, "exp-006").Single().FullName, "Por folio");
        Assert.IsEmpty(MedicalRecordsViewModel.RankPatients(patients, "   "));
    }

    [TestMethod]
    public void RankPatients_FindsByPhoneDigits_OnlyWithFourOrMore()
    {
        var patients = new[] { Patient("Elena Ruiz", phone: "55 1234 5678"), Patient("Mario Díaz", phone: "3312345678") };

        Assert.AreEqual("Elena Ruiz", MedicalRecordsViewModel.RankPatients(patients, "5512345678").Single().FullName);
        Assert.AreEqual("Elena Ruiz", MedicalRecordsViewModel.RankPatients(patients, "5678").First().FullName);
        Assert.IsEmpty(MedicalRecordsViewModel.RankPatients(patients, "567"), "Con menos de 4 dígitos no busca en teléfonos");
    }

    [TestMethod]
    public async Task HomeSearch_EnterOpensFirstResult_AndEscClears()
    {
        var (records, _, _) = await CreateRecordsAsync();
        records.EnterPatientsSection();
        Assert.IsTrue(records.Navigation.IsHomePage);
        Assert.IsFalse(records.HasHomeQuery);
        Assert.IsNotEmpty(records.RecentPatients, "El inicio muestra pacientes recientes");

        var target = records.Patients.First();
        var firstWord = target.FullName.Split(' ')[0];
        records.HomeSearchText = firstWord;

        Assert.IsTrue(records.HasHomeQuery);
        Assert.IsNotEmpty(records.HomeResults);
        Assert.IsFalse(records.HasNoHomeResults);

        var expected = records.HomeResults[0];
        records.OpenFirstHomeResult();
        Assert.IsTrue(records.Navigation.IsChartPage);
        Assert.AreEqual(expected.Id, records.SelectedPatient?.Id);

        records.HomeSearchText = "zzzz-nadie";
        Assert.IsTrue(records.HasNoHomeResults);
        Assert.AreEqual("Crear paciente \"zzzz-nadie\"", records.CreateFromSearchText);
    }

    [TestMethod]
    public async Task CreatePatientFromSearch_PrefillsNameFromWhatWasTyped()
    {
        var (records, _, _) = await CreateRecordsAsync();
        records.EnterPatientsSection();
        records.HomeSearchText = "maría lópez garcía";

        await records.CreatePatientFromSearchAsync();

        Assert.IsTrue(records.Navigation.IsFormPage, "Abre el alta como página");
        Assert.IsTrue(records.IsFormNew);
        Assert.AreEqual("María", records.FormFirstName);
        Assert.AreEqual("López", records.FormLastName);
        Assert.AreEqual("García", records.FormMaternalLastName);
        Assert.AreEqual(string.Empty, records.HomeSearchText);

        // Un folio o teléfono no se usa como nombre
        records.CancelPatientForm();
        records.HomeSearchText = "EXP-123";
        await records.CreatePatientFromSearchAsync();
        Assert.AreEqual(string.Empty, records.FormFirstName);
    }

    // ------------------------------------------------------------------ navegación

    [TestMethod]
    public async Task ChartPrimaryAction_StartsConsultation_AndBannerLeadsBack()
    {
        var (records, _, consultations) = await CreateRecordsAsync();
        records.EnterPatientsSection();
        var patient = records.Patients.First();

        records.OpenPatientChart(patient);
        Assert.IsTrue(records.Navigation.IsChartPage);
        Assert.IsTrue(records.Navigation.ShowsPatientsTopBar);
        Assert.AreEqual("Iniciar consulta", records.ChartPrimaryActionText);

        records.ChartPrimaryAction();
        Assert.IsTrue(consultations.HasActiveConsultation);
        Assert.IsTrue(records.Navigation.IsConsultationPage);
        Assert.IsFalse(records.Navigation.ShowsPatientsTopBar, "La consulta ocupa toda la pantalla");
        Assert.IsFalse(records.ShowsConsultationBanner);

        // Consultar el expediente durante la consulta no la cierra
        records.ShowChartFromConsultation();
        Assert.IsTrue(records.Navigation.IsChartPage);
        Assert.IsTrue(records.ShowsConsultationBanner);
        StringAssert.Contains(records.ConsultationBannerText, patient.FullName);
        Assert.AreEqual("Volver a la consulta", records.ChartPrimaryActionText);

        // Ir al inicio y volver a entrar regresa a la consulta en curso
        records.Navigation.GoToPatientsHome();
        Assert.IsTrue(records.ShowsConsultationBanner);
        records.EnterPatientsSection();
        Assert.IsTrue(records.Navigation.IsConsultationPage);

        // Descartar la consulta deja la ficha del paciente
        await records.DiscardActiveConsultationAsync();
        Assert.IsFalse(consultations.HasActiveConsultation);
        Assert.IsTrue(records.Navigation.IsChartPage);
        Assert.IsFalse(records.ShowsConsultationBanner);
    }

    [TestMethod]
    public async Task ChartOfAnotherPatient_DuringConsultation_DoesNotOfferToStartAnother()
    {
        var (records, _, consultations) = await CreateRecordsAsync();
        var first = records.Patients[0];
        var second = records.Patients[1];

        records.OpenPatientChart(first);
        records.ChartPrimaryAction();
        Assert.IsTrue(consultations.HasActiveConsultation);

        records.Navigation.GoToPatientsHome();
        records.OpenPatientChart(second);
        Assert.IsTrue(records.Navigation.IsChartPage);
        Assert.IsFalse(records.IsConsultationForSelectedPatient);
        Assert.IsTrue(records.ShowsConsultationBanner, "El aviso recuerda la consulta en curso");

        records.ChartPrimaryAction();
        Assert.AreEqual(first.Id, consultations.CurrentConsultation?.PatientId, "No se reemplaza la consulta en curso");
    }

    [TestMethod]
    public async Task NewPatient_OpensFormPage_AndCancelReturns()
    {
        var (records, _, _) = await CreateRecordsAsync();
        records.EnterPatientsSection();

        await records.NewPatientAsync();
        Assert.IsTrue(records.Navigation.IsFormPage);
        Assert.AreEqual("Nuevo paciente", records.FormPageTitle);
        Assert.AreEqual("Identificacion", records.FormOpenSection);

        records.CancelPatientForm();
        Assert.IsFalse(records.Navigation.IsFormPage);
        Assert.IsTrue(records.Navigation.IsHomePage);
    }

    [TestMethod]
    public async Task SavePatientAndStartConsultation_CreatesPatientAndOpensConsultation()
    {
        var (records, _, consultations) = await CreateRecordsAsync();
        await records.NewPatientAsync();
        records.FormFirstName = "Sofía";
        records.FormLastName = "Hernández";
        records.FormBirthDateText = DateTime.Today.AddYears(-34).ToString("dd/MM/yyyy");
        records.FormGender = "Femenino";
        records.SetAllergiesDenied();

        await records.SavePatientAndStartConsultationAsync();

        Assert.IsNull(records.FormErrorMessage);
        Assert.IsTrue(consultations.HasActiveConsultation);
        Assert.IsTrue(records.Navigation.IsConsultationPage);
        Assert.AreEqual("Sofía Hernández", records.SelectedPatient?.FullName);
    }

    [TestMethod]
    public async Task FormValidationError_OpensTheSectionWithTheMissingData()
    {
        var (records, _, _) = await CreateRecordsAsync();
        await records.NewPatientAsync();
        records.OpenFormSection("Notas");

        await records.SavePatientModalAsync();

        Assert.IsNotNull(records.FormErrorMessage);
        Assert.AreEqual("Identificacion", records.FormOpenSection, "Lleva al dato que falta");
        Assert.IsTrue(records.Navigation.IsFormPage, "No sale del alta");
    }

    [TestMethod]
    public async Task FormSections_OnlyOfferWhatAppliesToThePatient()
    {
        var (records, _, _) = await CreateRecordsAsync();
        await records.NewPatientAsync();

        records.FormGender = "Masculino";
        records.FormBirthDateText = DateTime.Today.AddYears(-40).ToString("dd/MM/yyyy");
        var keys = records.FormSections.Select(s => s.Key).ToList();
        Assert.DoesNotContain("Gineco", keys);
        Assert.DoesNotContain("Desarrollo", keys);

        records.FormGender = "Femenino";
        keys = records.FormSections.Select(s => s.Key).ToList();
        Assert.Contains("Gineco", keys);
        Assert.DoesNotContain("Desarrollo", keys);

        records.FormBirthDateText = DateTime.Today.AddYears(-4).ToString("dd/MM/yyyy");
        keys = records.FormSections.Select(s => s.Key).ToList();
        Assert.DoesNotContain("Gineco", keys);
        Assert.Contains("Desarrollo", keys);

        // Numeración continua aunque falte una sección
        var numbers = records.FormSections.Select(s => s.Number).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(1, numbers.Count).Select(n => n.ToString()).ToList(), numbers);
        StringAssert.Contains(records.FormProgressText, "obligatoria");
    }

    [TestMethod]
    public async Task FormProgressText_FollowsWhatIsCaptured()
    {
        var (records, _, _) = await CreateRecordsAsync();
        await records.NewPatientAsync();

        // Lo que ve la pantalla: el texto leído cada vez que se avisa que cambió
        var shown = records.FormProgressText;
        records.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MedicalRecordsViewModel.FormProgressText)) shown = records.FormProgressText;
        };
        StringAssert.Contains(shown, "Faltan 2");

        records.FormFirstName = "Lucía";
        records.FormLastName = "Pérez";
        records.FormGender = "Femenino";
        records.FormBirthDateText = DateTime.Today.AddYears(-30).ToString("dd/MM/yyyy");
        Assert.AreEqual(records.FormProgressText, shown);
        StringAssert.Contains(shown, "Faltan 1", "Falta solo alergias");

        records.FormAllergies = "Negadas";
        Assert.AreEqual(records.FormProgressText, shown);
        StringAssert.Contains(shown, "lo obligatorio está completo");
    }

    // ------------------------------------------------------------------ ficha

    [TestMethod]
    public void BuildAntecedentSections_DependOnSexAndAge()
    {
        var man = new PatientRecord { FullName = "Juan", Gender = "Masculino", BirthDate = DateTime.Today.AddYears(-45), FamilyHistory = "Padre con diabetes" };
        var woman = new PatientRecord { FullName = "Ana", Gender = "Femenino", BirthDate = DateTime.Today.AddYears(-30), PregnanciesCount = 2, CesareansCount = 1 };
        var child = new PatientRecord { FullName = "Leo", Gender = "Masculino", BirthDate = DateTime.Today.AddYears(-5), ImmunizationSchedule = "Completo" };

        var manKeys = MedicalRecordsViewModel.BuildAntecedentSections(man).Select(s => s.Key).ToList();
        CollectionAssert.AreEqual(new[] { "Heredofamiliares", "Patologicos", "NoPatologicos", "Contacto" }, manKeys);
        var family = MedicalRecordsViewModel.BuildAntecedentSections(man)[0];
        Assert.AreEqual("Padre con diabetes", family.Summary);
        Assert.IsFalse(family.IsEmpty);

        var womanSections = MedicalRecordsViewModel.BuildAntecedentSections(woman);
        var gyneco = womanSections.Single(s => s.Key == "Gineco");
        StringAssert.Contains(gyneco.Summary, "G2 C1 A0");
        Assert.IsTrue(womanSections.Single(s => s.Key == "Heredofamiliares").IsEmpty);
        Assert.AreEqual("Sin registrar", womanSections.Single(s => s.Key == "Heredofamiliares").Summary);

        var childSections = MedicalRecordsViewModel.BuildAntecedentSections(child);
        Assert.IsFalse(childSections.Any(s => s.Key == "Gineco"));
        StringAssert.Contains(childSections.Single(s => s.Key == "Desarrollo").Detail, "Vacunación: Completo");
        Assert.IsFalse(childSections.Single(s => s.Key == "Patologicos").Detail.Contains("Vacunación"), "La vacunación del niño va en Desarrollo");
    }

    [TestMethod]
    public void ChronicDiseases_DeniedAnswersDoNotShowAsABadge()
    {
        foreach (var denied in new[] { "Ninguna relevante", "Ninguna", "Negadas", "Niega", "no", "Sin enfermedades crónicas", "" })
        {
            Assert.IsFalse(new PatientRecord { ChronicDiseases = denied }.HasChronicDiseases, denied);
        }
        var diabetic = new PatientRecord { ChronicDiseases = "Diabetes mellitus tipo 2" };
        Assert.IsTrue(diabetic.HasChronicDiseases);
        Assert.AreEqual("Crónicos: Diabetes mellitus tipo 2", diabetic.ChronicBadgeText);
        Assert.IsTrue(new PatientRecord { ChronicDiseases = "Síndrome de Down" }.HasChronicDiseases);
    }

    [TestMethod]
    public void BuildTimeline_MergesConsultationsAndAppointments_NewestFirst()
    {
        var older = new PatientConsultation
        {
            Folio = "CNS-1",
            Date = new DateTime(2026, 9, 1, 10, 0, 0),
            ChiefComplaint = "Tos",
            Diagnosis = "J00 Rinofaringitis",
            VitalSigns = "T/A: 120/80 | Peso: 70 kg"
        };
        var newer = new PatientConsultation
        {
            Folio = "CNS-2",
            Date = new DateTime(2026, 10, 1, 11, 0, 0),
            ChiefComplaint = "Control",
            Prescriptions = new List<PatientPrescriptionItem> { new() { MedicineName = "Paracetamol", Dosage = "1 cada 8 h" } }
        };
        var appointment = new PatientAppointment { Date = new DateTime(2026, 10, 15), Time = "09:30", Reason = "Seguimiento", Status = "Programada" };

        var timeline = MedicalRecordsViewModel.BuildTimeline(new[] { older, newer }, new[] { appointment });

        Assert.AreEqual(TimelineKind.Appointment, timeline[0].Kind, "La cita futura arriba");
        Assert.AreEqual("Seguimiento", timeline[0].Title);
        Assert.AreEqual(TimelineKind.Consultation, timeline[1].Kind);
        Assert.AreEqual("Control", timeline[1].Title, "Sin diagnóstico, el título es el motivo");
        Assert.AreEqual(TimelineKind.Prescription, timeline[2].Kind);
        StringAssert.Contains(timeline[2].Detail, "Paracetamol: 1 cada 8 h");
        Assert.AreEqual("J00 Rinofaringitis", timeline[3].Title, "Con diagnóstico, el título es el diagnóstico");
        Assert.AreEqual(TimelineKind.Vitals, timeline[4].Kind);
        Assert.AreEqual("T/A: 120/80 · Peso: 70 kg", timeline[4].Detail);
        Assert.HasCount(5, timeline);
    }

    [TestMethod]
    public void BuildLatestVitals_ShowsLastValueAndTrend()
    {
        var points = new[]
        {
            new VitalSignsEvolutionPoint { Date = new DateTime(2026, 9, 1), Systolic = 130, Diastolic = 85, Weight = 82.5m, Glucose = 110 },
            new VitalSignsEvolutionPoint { Date = new DateTime(2026, 10, 1), Systolic = 120, Diastolic = 80, Weight = 80.0m }
        };

        var vitals = MedicalRecordsViewModel.BuildLatestVitals(points);

        var pressure = vitals.Single(v => v.Label.StartsWith("T/A"));
        Assert.AreEqual("120/80", pressure.Value);
        Assert.AreEqual("↓ antes 130/85", pressure.Trend, "La flecha marca la bajada sin depender del color");
        Assert.IsFalse(pressure.IsUp);

        var weight = vitals.Single(v => v.Label.StartsWith("Peso"));
        Assert.AreEqual("80.0", weight.Value);
        Assert.AreEqual("↓ 2.5", weight.Trend);

        var glucose = vitals.Single(v => v.Label.StartsWith("Glucosa"));
        Assert.AreEqual("110", glucose.Value, "El último valor registrado aunque la última consulta no lo tenga");
        Assert.AreEqual(string.Empty, glucose.Trend);

        Assert.IsEmpty(MedicalRecordsViewModel.BuildLatestVitals(null));
    }

    [TestMethod]
    public void DeselectingThePatient_ClearsTheEssentialVitals()
    {
        var records = new MedicalRecordsViewModel();
        records.SelectedPatient = new PatientRecord
        {
            Id = 7,
            FullName = "Ana Ruiz",
            Consultations = new()
            {
                new PatientConsultation { Folio = "CNS-1", Date = DateTime.Today.AddDays(-7), BloodPressureSystolic = 130, BloodPressureDiastolic = 85, Weight = 70m }
            }
        };
        Assert.IsTrue(records.HasLatestVitals);

        bool? shown = null;
        records.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MedicalRecordsViewModel.HasLatestVitals)) shown = records.HasLatestVitals;
        };

        records.SelectedPatient = null;

        Assert.IsEmpty(records.LatestVitals, "No quedan los signos del paciente anterior");
        Assert.IsFalse(records.HasLatestVitals);
        Assert.AreEqual(false, shown, "La pantalla se entera");
    }

    // ------------------------------------------------------------------ receta de la consulta

    private async Task<List<string>> StoredPrescriptionAsync(int patientId) =>
        (await _service.GetPatientByIdAsync(patientId))!.ActivePrescription.Select(p => p.MedicineName).ToList();

    private static async Task ChargeAsync(MedicalRecordsViewModel records, CheckoutViewModel checkout, ConsultationService consultations)
    {
        await records.FinalizeConsultationAsync();
        checkout.SelectedPaymentMethod = "Efectivo";
        checkout.CashReceived = 5000m;
        await checkout.ChargeAsync();
        await WaitUntilAsync(() => !consultations.HasActiveConsultation && records.Navigation.IsHomePage);
    }

    [TestMethod]
    public async Task NewConsultation_StartsWithEmptyPrescription_AndOffersToRepeatThePrevious()
    {
        var (records, checkout, consultations) = await CreateRecordsAsync();
        var patient = records.Patients.First(p => p.ActivePrescription.Count > 0);
        var previous = patient.ActivePrescription.Select(p => p.MedicineName).ToList();

        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();

        Assert.IsEmpty(patient.ActivePrescription, "La receta anterior no se arrastra a la consulta nueva");
        Assert.IsTrue(records.CanRepeatPreviousPrescription);
        StringAssert.Contains(records.PreviousPrescriptionSummary, previous[0]);

        // En la ficha, durante la consulta, "Última receta" sigue siendo la anterior
        records.ShowChartFromConsultation();
        CollectionAssert.AreEqual(previous, records.ChartPrescription.Select(p => p.MedicineName).ToList());
        records.ShowConsultation();

        // Terminar sin receta: la nota no lleva medicamentos y el expediente conserva la última receta
        records.ConsultationDiagnosis = "Z00 Revisión general";
        var folio = consultations.CurrentConsultation!.Folio;
        await ChargeAsync(records, checkout, consultations);

        var note = (await _service.GetPatientConsultationsAsync(patient.Id)).Single(c => c.Folio == folio);
        Assert.IsEmpty(note.Prescriptions, "Una consulta sin receta no guarda la anterior como suya");
        CollectionAssert.AreEqual(previous, await StoredPrescriptionAsync(patient.Id));
        CollectionAssert.AreEqual(previous, patient.ActivePrescription.Select(p => p.MedicineName).ToList());
    }

    [TestMethod]
    public async Task RepeatPreviousPrescription_CopiesItems_AndTheyBecomeThisConsultationsPrescription()
    {
        var (records, checkout, consultations) = await CreateRecordsAsync();
        var patient = records.Patients.First(p => p.ActivePrescription.Count > 0);
        var previous = patient.ActivePrescription.Select(p => (p.MedicineName, p.Dosage)).ToList();

        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        await records.RepeatPreviousPrescriptionAsync();

        CollectionAssert.AreEqual(previous, patient.ActivePrescription.Select(p => (p.MedicineName, p.Dosage)).ToList());
        Assert.IsFalse(records.CanRepeatPreviousPrescription, "Ya no queda nada por repetir");

        // Quitar un medicamento también lo quita de la nota (antes se guardaba aunque se hubiera quitado)
        var removed = patient.ActivePrescription[0];
        await records.RemovePrescriptionItemFromPatientAsync(removed);
        Assert.IsTrue(records.CanRepeatPreviousPrescription, "Lo quitado se puede volver a repetir");

        var folio = consultations.CurrentConsultation!.Folio;
        records.ConsultationDiagnosis = "Seguimiento";
        await ChargeAsync(records, checkout, consultations);

        var note = (await _service.GetPatientConsultationsAsync(patient.Id)).Single(c => c.Folio == folio);
        CollectionAssert.AreEqual(previous.Skip(1).Select(p => p.MedicineName).ToList(), note.Prescriptions.Select(p => p.MedicineName).ToList());
        CollectionAssert.AreEqual(previous.Skip(1).Select(p => p.MedicineName).ToList(), await StoredPrescriptionAsync(patient.Id));
    }

    [TestMethod]
    public async Task RepeatPreviousPrescription_SkipsMedicinesThePatientIsNowAllergicTo()
    {
        var (records, _, _) = await CreateRecordsAsync();
        var patient = records.Patients.First(p => p.ActivePrescription.Count > 0);
        var allergic = patient.ActivePrescription[0].MedicineName.Split(' ')[0];
        patient.Allergies = allergic;

        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        await records.RepeatPreviousPrescriptionAsync();

        Assert.IsFalse(patient.ActivePrescription.Any(p => p.MedicineName.StartsWith(allergic)), "No se repite lo que causa alergia");
        Assert.IsTrue(records.Info.IsInfoModalVisible);
        StringAssert.Contains(records.Info.InfoModalMessage, allergic);
    }

    [TestMethod]
    public async Task DiscardConsultation_RestoresThePreviousPrescription()
    {
        var (records, _, consultations) = await CreateRecordsAsync();
        var patient = records.Patients.First(p => p.ActivePrescription.Count > 0);
        var previous = patient.ActivePrescription.Select(p => p.MedicineName).ToList();

        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        records.PrescriptionMedicineName = "Loratadina 10 mg";
        records.PrescriptionDosage = "1 tableta cada 24 horas";
        await records.SelectAndAddPrescriptionProductAsync(records.SelectedPrescriptionProduct);
        CollectionAssert.AreEqual(new[] { "Loratadina 10 mg" }, await StoredPrescriptionAsync(patient.Id));

        await records.DiscardActiveConsultationAsync();

        Assert.IsFalse(consultations.HasActiveConsultation);
        CollectionAssert.AreEqual(previous, patient.ActivePrescription.Select(p => p.MedicineName).ToList());
        CollectionAssert.AreEqual(previous, await StoredPrescriptionAsync(patient.Id), "Lo recetado en la consulta descartada no queda en el expediente");
    }

    [TestMethod]
    public async Task DiscardAfterSavingTheNote_LeavesTheRecordAsItWas()
    {
        var (records, _, consultations) = await CreateRecordsAsync();
        var patient = records.Patients.First(p => p.ActivePrescription.Count > 0);
        records.OpenPatientChart(patient);
        await WaitUntilAsync(() => records.ConsultationsHistory.Count > 0);
        var before = (Diagnosis: patient.LastDiagnosis, Vitals: patient.VitalSigns, Visit: patient.LastVisit, Notes: patient.ClinicalNotes);
        var historyBefore = records.ConsultationsHistory.Count;

        records.ChartPrimaryAction();
        records.ConsultationChiefComplaint = "Paciente equivocado";
        records.ConsultationTA = "150/95";
        records.ConsultationDiagnosis = "R51 Cefalea";
        var folio = consultations.CurrentConsultation!.Folio;

        // "Terminar consulta" guarda la nota; luego "Volver a la consulta" y se descarta
        await records.FinalizeConsultationAsync();
        Assert.AreEqual("R51 Cefalea", patient.LastDiagnosis);
        records.CloseFinishConsultation();
        await records.DiscardActiveConsultationAsync();

        Assert.AreEqual(before.Diagnosis, patient.LastDiagnosis);
        Assert.AreEqual(before.Vitals, patient.VitalSigns);
        Assert.AreEqual(before.Visit, patient.LastVisit);
        Assert.AreEqual(before.Notes, patient.ClinicalNotes);
        Assert.IsFalse(patient.Consultations.Any(c => c.Folio == folio));
        Assert.AreEqual(historyBefore, records.ConsultationsHistory.Count);
        Assert.IsFalse(records.Timeline.Any(t => t.Title == "R51 Cefalea"), "El historial no muestra la consulta descartada");

        var stored = (await _service.GetPatientByIdAsync(patient.Id))!;
        Assert.AreEqual(before.Diagnosis, stored.LastDiagnosis);
        Assert.AreEqual(before.Vitals, stored.VitalSigns);
        Assert.IsFalse((await _service.GetPatientConsultationsAsync(patient.Id)).Any(c => c.Folio == folio));
    }

    [TestMethod]
    public async Task RecoveredConsultation_KeepsItsPrescriptionAndThePreviousOne()
    {
        var draftDir = Path.Combine(Path.GetTempPath(), $"tpvc_receta_{Guid.NewGuid():N}");
        var draftPath = Path.Combine(draftDir, "consulta.json");
        try
        {
            var consultations = new ConsultationService(seedDemoPatients: false);
            var records = new MedicalRecordsViewModel(_service, consultations, consultationDraftStore: new ConsultationDraftStore(draftPath));
            await records.LoadInitialDataAsync();
            var patient = records.Patients.First(p => p.ActivePrescription.Count > 0);
            var previous = patient.ActivePrescription.Select(p => p.MedicineName).ToList();

            records.OpenPatientChart(patient);
            records.ChartPrimaryAction();
            records.PrescriptionMedicineName = "Loratadina 10 mg";
            records.PrescriptionDosage = "1 tableta cada 24 horas";
            await records.SelectAndAddPrescriptionProductAsync(records.SelectedPrescriptionProduct);
            records.FlushConsultationDraft();
            records.Dispose();

            // Se cierra el programa sin terminar la consulta y se vuelve a abrir
            var reopened = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false), consultationDraftStore: new ConsultationDraftStore(draftPath));
            await reopened.LoadInitialDataAsync();

            Assert.IsTrue(reopened.HasActiveConsultation);
            CollectionAssert.AreEqual(new[] { "Loratadina 10 mg" }, reopened.SelectedPatient!.ActivePrescription.Select(p => p.MedicineName).ToList());
            Assert.IsTrue(reopened.CanRepeatPreviousPrescription);
            StringAssert.Contains(reopened.PreviousPrescriptionSummary, previous[0]);
        }
        finally
        {
            try { Directory.Delete(draftDir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ alta guardada sola

    private sealed class DraftFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tpvc_alta_{Guid.NewGuid():N}");
        public ConsultationDraftStore Store() => new(System.IO.Path.Combine(Path, "consulta.json"));
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private async Task<MedicalRecordsViewModel> OpenAppAsync(DraftFolder folder)
    {
        var records = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false), consultationDraftStore: folder.Store());
        await records.LoadInitialDataAsync();
        return records;
    }

    [TestMethod]
    public async Task NewPatientForm_IsRecoveredAfterTheAppCloses_AndClearedWhenSaved()
    {
        using var folder = new DraftFolder();
        var records = await OpenAppAsync(folder);
        await records.NewPatientAsync();
        var folio = records.FormRecordNumber;
        records.FormFirstName = "Lucía";
        records.FormLastName = "Pérez";
        records.FormGender = "Femenino";
        records.FormBirthDateText = DateTime.Today.AddYears(-30).ToString("dd/MM/yyyy");
        records.FormPregnanciesCount = 2;
        records.OpenFormSection("Gineco");
        records.FlushConsultationDraft();
        records.Dispose();
        Assert.IsTrue(File.Exists(folder.Store().PatientFormPath));

        var reopened = await OpenAppAsync(folder);

        Assert.IsTrue(reopened.Navigation.IsFormPage, "Se reabre el alta");
        Assert.IsTrue(reopened.IsFormNew);
        Assert.AreEqual(folio, reopened.FormRecordNumber);
        Assert.AreEqual("Lucía", reopened.FormFirstName);
        Assert.AreEqual("Pérez", reopened.FormLastName);
        Assert.AreEqual(2, reopened.FormPregnanciesCount);
        Assert.AreEqual(30, reopened.FormAge, "La edad se recalcula de la fecha");
        Assert.IsTrue(reopened.FormShowsGyneco);
        Assert.AreEqual("Gineco", reopened.FormOpenSection);

        reopened.SetAllergiesDenied();
        await reopened.SavePatientModalAsync();
        Assert.IsNull(reopened.FormErrorMessage);
        Assert.IsFalse(File.Exists(folder.Store().PatientFormPath), "Guardado: ya no hay nada que recuperar");
        reopened.Dispose();
    }

    [TestMethod]
    public async Task EditPatientForm_IsRecoveredOnTheSamePatient_AndCancelDiscardsIt()
    {
        using var folder = new DraftFolder();
        var records = await OpenAppAsync(folder);
        var patient = records.Patients.First();
        records.OpenPatientChart(patient);
        records.EditPatient();
        records.FormPhone = "5598765432";
        var typedPhone = records.FormPhone;
        records.FlushConsultationDraft();
        records.Dispose();

        var reopened = await OpenAppAsync(folder);

        Assert.IsTrue(reopened.Navigation.IsFormPage);
        Assert.AreEqual(patient.Id, reopened.FormPatientId);
        Assert.AreEqual(patient.Id, reopened.SelectedPatient?.Id);
        Assert.AreEqual(typedPhone, reopened.FormPhone);

        reopened.CancelPatientForm();
        Assert.IsFalse(reopened.Navigation.IsFormPage);
        Assert.IsFalse(File.Exists(folder.Store().PatientFormPath));
        reopened.Dispose();
    }

    [TestMethod]
    public async Task PatientForm_WithoutChanges_LeavesNothingToRecover()
    {
        using var folder = new DraftFolder();
        var records = await OpenAppAsync(folder);

        await records.NewPatientAsync();
        records.FlushConsultationDraft();
        Assert.IsFalse(File.Exists(folder.Store().PatientFormPath), "Un alta vacía no se recupera");
        records.CancelPatientForm();

        records.OpenPatientChart(records.Patients.First());
        records.EditAntecedent(records.AntecedentSections.First());
        records.FlushConsultationDraft();
        Assert.IsFalse(File.Exists(folder.Store().PatientFormPath), "Abrir la edición (en una sección) sin cambiar nada no se recupera");
        records.Dispose();
    }

    [TestMethod]
    public async Task PatientForm_SavesItselfAfterAPauseInTyping()
    {
        using var folder = new DraftFolder();
        var records = await OpenAppAsync(folder);
        records.AutosaveDelay = TimeSpan.FromMilliseconds(30);

        await records.NewPatientAsync();
        records.FormFirstName = "Mateo";

        await WaitUntilAsync(() => File.Exists(folder.Store().PatientFormPath));
        StringAssert.Contains(File.ReadAllText(folder.Store().PatientFormPath), "Mateo");
        records.Dispose();
    }

    // ------------------------------------------------------------------ agenda

    [TestMethod]
    public void BuildSlots_OffersFreeHalfHours_SkippingBusyAndPastOnes()
    {
        var day = new DateTime(2026, 10, 20);
        var appointments = new[]
        {
            new PatientAppointment { Date = day, Time = "10:15", DurationMinutes = 45, PatientName = "Ana" },
            new PatientAppointment { Date = day, Time = "12:00", Status = "Cancelada", PatientName = "Luis" },
            new PatientAppointment { Date = day.AddDays(1), Time = "08:00", PatientName = "Otro día" }
        };

        var slots = MedicalRecordsViewModel.BuildSlots(day, appointments, now: day.AddDays(-1));

        Assert.AreEqual(1, slots.Count(s => s.HasAppointment), "Las canceladas y las de otro día no aparecen");
        var free = slots.Where(s => s.IsFree).Select(s => s.TimeText).ToList();
        Assert.DoesNotContain("10:00", free, "10:15–11:00 ocupa el hueco de las 10:00");
        Assert.DoesNotContain("10:30", free);
        Assert.Contains("11:00", free);
        Assert.Contains("12:00", free, "La cita cancelada libera su hueco");
        Assert.AreEqual("08:00", free.First());
        Assert.AreEqual("19:30", free.Last());
        Assert.HasCount(22, free);
        Assert.AreEqual("10:15", slots.Single(s => s.HasAppointment).TimeText);
        Assert.IsTrue(slots.Select(s => s.Time).SequenceEqual(slots.Select(s => s.Time).OrderBy(t => t)), "En orden de hora");

        // Hoy no se ofrecen horas que ya pasaron
        var today = MedicalRecordsViewModel.BuildSlots(day, Array.Empty<PatientAppointment>(), now: day.AddHours(13).AddMinutes(10));
        Assert.AreEqual("13:30", today.First().TimeText);
    }

    [TestMethod]
    public async Task AgendaPanel_SchedulesMovesAndCancelsAppointments()
    {
        var (records, _, _) = await CreateRecordsAsync();
        var patient = records.Patients.First();
        records.OpenPatientChart(patient);

        await records.ScheduleForSelectedPatientAsync();
        Assert.IsTrue(records.IsAgendaPanelOpen);
        Assert.AreEqual(DateTime.Today.AddDays(1), records.AgendaPanelDate);
        Assert.HasCount(7, records.AgendaWeek);

        var slot = records.AgendaSlots.First(s => s.IsFree && s.TimeText == "09:00");
        await records.OpenAgendaSlotAsync(slot);
        Assert.IsTrue(slot.IsFormOpen);
        Assert.AreEqual(patient.Id, records.AgendaFormPatient?.Id, "El paciente de la ficha ya viene elegido");

        // Sin motivo no se guarda
        await records.SaveAgendaFormAsync();
        Assert.IsNotNull(records.AgendaFormError);

        records.SetAgendaReason("Control");
        records.AgendaFormDuration = 60;
        await records.SaveAgendaFormAsync();

        var saved = (await _service.GetAppointmentsByDayAsync(DateTime.Today.AddDays(1))).Single(a => a.PatientRecordId == patient.Id);
        Assert.AreEqual("09:00", saved.Time);
        Assert.AreEqual(60, saved.DurationMinutes);
        Assert.AreEqual("Control", saved.Reason);
        Assert.IsFalse(records.AgendaSlots.Any(s => s.IsFree && (s.TimeText == "09:00" || s.TimeText == "09:30")), "La hora queda ocupada");
        Assert.AreEqual(1, records.AgendaWeek.Single(d => d.Date == DateTime.Today.AddDays(1)).Count);

        // Cambiar de horario: se elige la cita y luego el nuevo hueco
        var booked = records.AgendaSlots.Single(s => s.HasAppointment).Appointment!;
        records.StartMovingAppointment(booked);
        Assert.IsTrue(records.IsMovingAppointment);
        await records.OpenAgendaSlotAsync(records.AgendaSlots.First(s => s.IsFree && s.TimeText == "16:00"));
        Assert.IsFalse(records.IsMovingAppointment);
        var moved = (await _service.GetAppointmentsByDayAsync(DateTime.Today.AddDays(1))).Single(a => a.PatientRecordId == patient.Id);
        Assert.AreEqual(saved.Id, moved.Id, "Es la misma cita, no una nueva");
        Assert.AreEqual("16:00", moved.Time);

        await records.CancelAppointmentAsync(records.AgendaSlots.Single(s => s.HasAppointment).Appointment);
        var cancelled = (await _service.GetAppointmentsByDayAsync(DateTime.Today.AddDays(1))).Single(a => a.Id == saved.Id);
        Assert.AreEqual("Cancelada", cancelled.Status);
        Assert.IsFalse(records.AgendaSlots.Any(s => s.HasAppointment));

        // Al volver a abrir el panel empieza en hoy
        records.CloseAgendaPanel();
        await records.ToggleAgendaPanelAsync();
        Assert.AreEqual(DateTime.Today, records.AgendaPanelDate);
    }

    [TestMethod]
    public async Task AgendaPanel_RequiresPatient()
    {
        var (records, _, _) = await CreateRecordsAsync();
        await records.OpenAgendaPanelAsync();
        records.AgendaPanelDate = DateTime.Today.AddDays(2);
        await records.LoadAgendaPanelAsync();

        await records.OpenAgendaSlotAsync(records.AgendaSlots.First(s => s.IsFree));
        Assert.IsNull(records.AgendaFormPatient, "Sin ficha abierta hay que elegir al paciente");
        records.SetAgendaReason("Primera vez");
        await records.SaveAgendaFormAsync();
        StringAssert.Contains(records.AgendaFormError, "paciente");
    }

    [TestMethod]
    public async Task RemindByWhatsApp_OpensChatWithMexicanPrefix_AndExplainsMissingPhone()
    {
        var (records, _, _) = await CreateRecordsAsync();
        string? opened = null;
        records.OpenUrl = url => { opened = url; return true; };

        records.RemindByWhatsApp(new PatientAppointment { PatientName = "Ana", PatientPhone = "55 1234 5678", Date = new DateTime(2026, 10, 20), Time = "10:00" });
        Assert.IsNotNull(opened);
        StringAssert.StartsWith(opened, "https://wa.me/525512345678?text=");

        opened = null;
        records.RemindByWhatsApp(new PatientAppointment { PatientName = "Luis", PatientPhone = "123" });
        Assert.IsNull(opened);
        Assert.IsTrue(records.Info.IsInfoModalVisible);
    }

    [TestMethod]
    public async Task AttendAppointment_StartsConsultationWithReason_AndChargingMarksItAttended()
    {
        var (records, checkout, consultations) = await CreateRecordsAsync();
        var patient = records.Patients.First();
        await _service.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            Date = DateTime.Today,
            Time = "23:30",
            Reason = "Dolor de garganta"
        });
        await records.LoadTodayAppointmentsAsync();
        var appointment = records.TodayAppointments.Single(a => a.PatientRecordId == patient.Id);

        records.AttendAppointment(appointment);

        Assert.IsTrue(consultations.HasActiveConsultation);
        Assert.IsTrue(records.Navigation.IsConsultationPage);
        Assert.AreEqual(patient.Id, records.SelectedPatient?.Id);
        Assert.AreEqual("Dolor de garganta", records.ConsultationChiefComplaint);

        records.ConsultationDiagnosis = "J02 Faringitis aguda";
        await records.FinalizeConsultationAsync();
        checkout.SelectedPaymentMethod = "Efectivo";
        checkout.CashReceived = 5000m;
        await checkout.ChargeAsync();

        await WaitUntilAsync(() => !consultations.HasActiveConsultation && records.Navigation.IsHomePage);
        await WaitUntilAsync(() => records.TodayAppointments.Any(a => a.Id == appointment.Id && a.IsAttended));
        Assert.AreEqual("Atendida", (await _service.GetAppointmentsByDayAsync(DateTime.Today)).Single(a => a.Id == appointment.Id).Status);
    }

    [TestMethod]
    public async Task AttendAppointment_WithoutRecord_SearchesByName()
    {
        var (records, _, consultations) = await CreateRecordsAsync();

        records.AttendAppointment(new PatientAppointment { PatientRecordId = 0, PatientName = "Paciente Sin Expediente", Date = DateTime.Today });

        Assert.IsFalse(consultations.HasActiveConsultation);
        Assert.IsTrue(records.Navigation.IsHomePage);
        Assert.AreEqual("Paciente Sin Expediente", records.HomeSearchText);
    }

    [TestMethod]
    public async Task DeactivatePatient_HidesItFromSearch_ButKeepsTheRecord()
    {
        var (records, _, _) = await CreateRecordsAsync();
        var patient = records.Patients.First();
        records.OpenPatientChart(patient);

        await records.DeactivateSelectedPatientAsync();

        Assert.IsTrue(records.Navigation.IsHomePage);
        Assert.IsNull(records.SelectedPatient);
        Assert.IsFalse(records.Patients.Any(p => p.Id == patient.Id), "Deja de aparecer");
        Assert.IsFalse(MedicalRecordsViewModel.RankPatients(records.Patients, patient.FullName).Any(p => p.Id == patient.Id));

        using var db = new AppDbContext(_options);
        var stored = db.Patients.Single(p => p.Id == patient.Id);
        Assert.IsFalse(stored.IsActive, "Baja lógica: el expediente se conserva");
    }
}
