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
/// Ficha de identificación (v1.12): fecha de nacimiento con edad calculada, antecedentes que solo
/// se piden cuando corresponden al sexo y la edad, y ningún dato clínico supuesto.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PatientIdentityTests
{
    private string _dbPath = string.Empty;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_identity_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        using (var db = new AppDbContext(builder.Options))
        {
            DatabaseInitializer.Initialize(db);
        }
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
    }

    [TestMethod]
    public void Age_IsCalculatedFromBirthDate_OnBirthdaysAndLeapDays()
    {
        var birth = new DateTime(1992, 3, 14);
        Assert.AreEqual(33, PatientRecord.CalculateAge(birth, new DateTime(2026, 3, 13)));
        Assert.AreEqual(34, PatientRecord.CalculateAge(birth, new DateTime(2026, 3, 14)));

        var leap = new DateTime(2024, 2, 29);
        Assert.AreEqual(0, PatientRecord.CalculateAge(leap, new DateTime(2025, 2, 28)));
        Assert.AreEqual(1, PatientRecord.CalculateAge(leap, new DateTime(2025, 3, 1)));

        // Con fecha la edad no se queda congelada en la del alta
        var patient = new PatientRecord { Age = 99, BirthDate = DateTime.Today.AddYears(-10).AddDays(1) };
        Assert.AreEqual(9, patient.Age, "Cumple 10 mañana");
        Assert.AreEqual(99, new PatientRecord { Age = 99 }.Age, "Sin fecha se usa la edad capturada");
    }

    [TestMethod]
    public void AgeDisplay_UsesDaysAndMonthsForBabies()
    {
        var today = new DateTime(2026, 10, 7);
        Assert.AreEqual("1 día", PatientRecord.FormatAge(today.AddDays(-1), 0, today));
        Assert.AreEqual("10 días", PatientRecord.FormatAge(today.AddDays(-10), 0, today));
        Assert.AreEqual("1 mes", PatientRecord.FormatAge(today.AddMonths(-1), 0, today));
        Assert.AreEqual("8 meses", PatientRecord.FormatAge(today.AddMonths(-8), 0, today));
        Assert.AreEqual("23 meses", PatientRecord.FormatAge(today.AddMonths(-23), 0, today));
        Assert.AreEqual("2 años 6 meses", PatientRecord.FormatAge(today.AddMonths(-30), 0, today));
        Assert.AreEqual("5 años", PatientRecord.FormatAge(today.AddYears(-5), 0, today));
        Assert.AreEqual("34 años", PatientRecord.FormatAge(new DateTime(1992, 3, 14), 0, today));
        Assert.AreEqual("1 año", PatientRecord.FormatAge(null, 1, today));
        Assert.AreEqual("40 años", PatientRecord.FormatAge(null, 40, today));
    }

    [TestMethod]
    public void Allergies_DistinguishDeniedFromNotAsked()
    {
        var notAsked = new PatientRecord { Allergies = "" };
        Assert.IsTrue(notAsked.AllergiesNotAsked);
        Assert.IsFalse(notAsked.HasAllergies);
        Assert.AreEqual("No interrogadas", notAsked.AllergySummary);
        Assert.AreEqual("Alergias sin interrogar", notAsked.AllergyBadgeText);
        Assert.IsTrue(new PatientRecord().AllergiesNotAsked, "Un paciente nuevo no supone \"ninguna\"");

        foreach (var denied in new[] { "Negadas", "ninguna conocida.", "NIEGA", "Ninguna" })
        {
            var patient = new PatientRecord { Allergies = denied };
            Assert.IsFalse(patient.HasAllergies, denied);
            Assert.IsFalse(patient.AllergiesNotAsked, denied);
            Assert.AreEqual("Negadas", patient.AllergySummary, denied);
        }

        var allergic = new PatientRecord { Allergies = " Penicilina " };
        Assert.IsTrue(allergic.HasAllergies);
        Assert.AreEqual("Penicilina", allergic.AllergySummary);
        Assert.AreEqual("ALERGIAS: Penicilina", allergic.AllergyBadgeText);
    }

    [TestMethod]
    public void ConditionalStep_OnlyAppearsWhenItMatchesSexAndAge()
    {
        var vm = new MedicalRecordsViewModel();

        // Hombre adulto: no hay nada que llenar en gineco ni en desarrollo
        vm.FormGender = "Masculino";
        vm.FormAgeText = "40";
        Assert.IsFalse(vm.FormHasConditionalStep);
        Assert.AreEqual(3, vm.FormPatientStepCount);
        Assert.AreEqual("3. ", vm.FormNoPathologicalStepNumber);
        vm.SetFormPatientStepTab("Patologicos");
        vm.NextFormPatientStep();
        Assert.AreEqual("NoPatologicos", vm.FormPatientActiveTab, "Salta el paso que no aplica");
        vm.PreviousFormPatientStep();
        Assert.AreEqual("Patologicos", vm.FormPatientActiveTab);
        vm.SetFormPatientStepTab("GinecoHitos");
        Assert.AreEqual("Patologicos", vm.FormPatientActiveTab, "No se puede abrir un paso que no aplica");

        // Mujer adulta: solo gineco-obstétricos
        vm.FormGender = "Femenino";
        Assert.IsTrue(vm.FormShowsGyneco);
        Assert.IsFalse(vm.FormIsPediatric);
        Assert.AreEqual("Gineco-obstétricos", vm.FormConditionalStepTitle);
        Assert.AreEqual(4, vm.FormPatientStepCount);

        // Niña de 6 años: desarrollo, sin antecedentes gineco-obstétricos
        vm.FormAgeText = "6";
        Assert.IsFalse(vm.FormShowsGyneco);
        Assert.AreEqual("Desarrollo", vm.FormConditionalStepTitle);

        // Adolescente: ambos
        vm.FormAgeText = "14";
        Assert.AreEqual("Gineco / Desarrollo", vm.FormConditionalStepTitle);

        // Si se corrige el sexo estando en ese paso, no se queda en un paso vacío
        vm.FormAgeText = "30";
        vm.SetFormPatientStepTab("GinecoHitos");
        vm.FormGender = "Masculino";
        Assert.AreEqual("NoPatologicos", vm.FormPatientActiveTab);
    }

    [TestMethod]
    public void ClearingAge_DoesNotTurnThePatientIntoANewborn()
    {
        var vm = new MedicalRecordsViewModel();
        vm.FormAgeText = "5";
        Assert.IsTrue(vm.FormIsPediatric);

        vm.FormAgeText = string.Empty;

        Assert.AreEqual(string.Empty, vm.FormAgeText, "No se rellena con 0");
        Assert.IsFalse(vm.FormHasAge);
        Assert.IsFalse(vm.FormIsPediatric);
    }

    [TestMethod]
    public void BirthDate_ParsesCommonFormatsAndRejectsImpossibleDates()
    {
        var today = new DateTime(2026, 10, 7);
        Assert.AreEqual(new DateTime(1992, 3, 14), MedicalRecordsViewModel.TryParseBirthDate("14/03/1992", today));
        Assert.AreEqual(new DateTime(1992, 3, 14), MedicalRecordsViewModel.TryParseBirthDate("14-3-1992", today));
        Assert.AreEqual(new DateTime(1992, 3, 14), MedicalRecordsViewModel.TryParseBirthDate("1992-03-14", today));
        Assert.IsNull(MedicalRecordsViewModel.TryParseBirthDate("31/02/2020", today));
        Assert.IsNull(MedicalRecordsViewModel.TryParseBirthDate("08/10/2026", today), "Futura");
        Assert.IsNull(MedicalRecordsViewModel.TryParseBirthDate("01/01/1850", today), "Más de 130 años");
        Assert.IsNull(MedicalRecordsViewModel.TryParseBirthDate("", today));
    }

    [TestMethod]
    public async Task NewPatient_StartsEmpty_RequiresIdentity_AndSavesBirthDate()
    {
        var vm = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false));
        await vm.InitialLoad;
        await vm.NewPatientAsync();

        // Nada supuesto: ni edad 30, ni masculino, ni O+, ni "ninguna" alergia, ni signos de ejemplo
        Assert.AreEqual(string.Empty, vm.FormAgeText);
        Assert.AreEqual(string.Empty, vm.FormGender);
        Assert.AreEqual(MedicalRecordsViewModel.BloodTypeUnknown, vm.FormBloodType);
        Assert.AreEqual(string.Empty, vm.FormAllergies);
        Assert.AreEqual(string.Empty, vm.FormVitalTA);

        vm.FormFirstName = "Rodrigo";
        vm.FormLastName = "Medina";

        await vm.SavePatientModalAsync();
        StringAssert.Contains(vm.FormErrorMessage, "fecha de nacimiento");

        vm.FormBirthDateText = "31/02/2024";
        await vm.SavePatientModalAsync();
        StringAssert.Contains(vm.FormErrorMessage, "no es válida");

        vm.FormBirthDateText = DateTime.Today.AddMonths(-30).ToString("dd/MM/yyyy");
        Assert.AreEqual(2, vm.FormAge);
        Assert.AreEqual("2 años 6 meses", vm.FormAgeDisplay);
        Assert.IsTrue(vm.FormHasBirthDate);
        await vm.SavePatientModalAsync();
        StringAssert.Contains(vm.FormErrorMessage, "sexo");

        vm.FormGender = "Masculino";
        Assert.AreEqual("Desarrollo", vm.FormConditionalStepTitle);
        await vm.SavePatientModalAsync();
        StringAssert.Contains(vm.FormErrorMessage, "Negadas");

        vm.SetAllergiesDenied();
        await vm.SavePatientModalAsync();

        Assert.IsNull(vm.FormErrorMessage);
        var saved = (await _service.GetPatientsAsync()).Single(p => p.FullName == "Rodrigo Medina");
        Assert.AreEqual(DateTime.Today.AddMonths(-30), saved.BirthDate);
        Assert.AreEqual(2, saved.Age);
        Assert.AreEqual("Negadas", saved.Allergies);
        Assert.AreEqual(MedicalRecordsViewModel.BloodTypeUnknown, saved.BloodType);
        Assert.AreEqual(string.Empty, saved.VitalSigns, "Sin signos inventados");
    }

    [TestMethod]
    public async Task ConsultationNote_WithoutVitals_DoesNotCopyThePatientsLastVitals()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var vm = new MedicalRecordsViewModel(_service, consultations);
        await vm.InitialLoad;
        var patient = vm.Patients.First();
        vm.SelectPatient(patient);
        vm.SelectedPatient!.VitalSigns = "TA: 110/70 mmHg";
        vm.StartActiveConsultation();
        var folio = consultations.CurrentConsultation!.Folio;

        vm.ConsultationChiefComplaint = "Revisión de resultados";
        vm.ConsultationDiagnosis = "Z01 Examen de control";
        await vm.SaveActiveConsultationNoteAsync();

        var note = (await _service.GetPatientConsultationsAsync(patient.Id)).Single(c => c.Folio == folio);
        Assert.AreEqual(string.Empty, note.VitalSigns, "No se tomaron signos en esta consulta");
        Assert.AreEqual("TA: 110/70 mmHg", vm.SelectedPatient.VitalSigns, "Se conservan los últimos registrados");
    }
}
