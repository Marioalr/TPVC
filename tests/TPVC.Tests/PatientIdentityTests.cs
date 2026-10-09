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
using TPVC.ViewModels.Patients;

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
    public void ConditionalSections_OnlyAppearWhenTheyMatchSexAndAge()
    {
        var vm = new MedicalRecordsViewModel();

        // Hombre adulto: no hay nada que llenar en gineco ni en desarrollo
        vm.PatientForm.FormGender = "Masculino";
        vm.PatientForm.FormAgeText = "40";
        Assert.IsFalse(vm.PatientForm.FormShowsGyneco);
        Assert.IsFalse(vm.PatientForm.FormIsPediatric);

        // Mujer adulta: solo gineco-obstétricos
        vm.PatientForm.FormGender = "Femenino";
        Assert.IsTrue(vm.PatientForm.FormShowsGyneco);
        Assert.IsFalse(vm.PatientForm.FormIsPediatric);

        // Niña de 6 años: desarrollo, sin antecedentes gineco-obstétricos
        vm.PatientForm.FormAgeText = "6";
        Assert.IsFalse(vm.PatientForm.FormShowsGyneco);
        Assert.IsTrue(vm.PatientForm.FormIsPediatric);

        // Adolescente: ambos
        vm.PatientForm.FormAgeText = "14";
        Assert.IsTrue(vm.PatientForm.FormShowsGyneco);
        Assert.IsTrue(vm.PatientForm.FormIsPediatric);
    }

    [TestMethod]
    public void ClearingAge_DoesNotTurnThePatientIntoANewborn()
    {
        var vm = new MedicalRecordsViewModel();
        vm.PatientForm.FormAgeText = "5";
        Assert.IsTrue(vm.PatientForm.FormIsPediatric);

        vm.PatientForm.FormAgeText = string.Empty;

        Assert.AreEqual(string.Empty, vm.PatientForm.FormAgeText, "No se rellena con 0");
        Assert.IsFalse(vm.PatientForm.FormHasAge);
        Assert.IsFalse(vm.PatientForm.FormIsPediatric);
    }

    [TestMethod]
    public void BirthDate_ParsesCommonFormatsAndRejectsImpossibleDates()
    {
        var today = new DateTime(2026, 10, 7);
        Assert.AreEqual(new DateTime(1992, 3, 14), PatientFormViewModel.TryParseBirthDate("14/03/1992", today));
        Assert.AreEqual(new DateTime(1992, 3, 14), PatientFormViewModel.TryParseBirthDate("14-3-1992", today));
        Assert.AreEqual(new DateTime(1992, 3, 14), PatientFormViewModel.TryParseBirthDate("1992-03-14", today));
        Assert.IsNull(PatientFormViewModel.TryParseBirthDate("31/02/2020", today));
        Assert.IsNull(PatientFormViewModel.TryParseBirthDate("08/10/2026", today), "Futura");
        Assert.IsNull(PatientFormViewModel.TryParseBirthDate("01/01/1850", today), "Más de 130 años");
        Assert.IsNull(PatientFormViewModel.TryParseBirthDate("", today));
    }

    [TestMethod]
    public async Task NewPatient_StartsEmpty_RequiresIdentity_AndSavesBirthDate()
    {
        var vm = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false));
        await vm.InitialLoad;
        await vm.NewPatientAsync();

        // Nada supuesto: ni edad 30, ni masculino, ni O+, ni "ninguna" alergia, ni signos de ejemplo
        Assert.AreEqual(string.Empty, vm.PatientForm.FormAgeText);
        Assert.AreEqual(string.Empty, vm.PatientForm.FormGender);
        Assert.AreEqual(PatientFormViewModel.BloodTypeUnknown, vm.PatientForm.FormBloodType);
        Assert.AreEqual(string.Empty, vm.PatientForm.FormAllergies);

        vm.PatientForm.FormFirstName = "Rodrigo";
        vm.PatientForm.FormLastName = "Medina";

        await vm.PatientForm.SavePatientModalAsync();
        StringAssert.Contains(vm.PatientForm.FormErrorMessage, "fecha de nacimiento");

        vm.PatientForm.FormBirthDateText = "31/02/2024";
        await vm.PatientForm.SavePatientModalAsync();
        StringAssert.Contains(vm.PatientForm.FormErrorMessage, "no es válida");

        vm.PatientForm.FormBirthDateText = DateTime.Today.AddMonths(-30).ToString("dd/MM/yyyy");
        Assert.AreEqual(2, vm.PatientForm.FormAge);
        Assert.AreEqual("2 años 6 meses", vm.PatientForm.FormAgeDisplay);
        Assert.IsTrue(vm.PatientForm.FormHasBirthDate);
        await vm.PatientForm.SavePatientModalAsync();
        StringAssert.Contains(vm.PatientForm.FormErrorMessage, "sexo");

        vm.PatientForm.FormGender = "Masculino";
        Assert.IsTrue(vm.PatientForm.FormIsPediatric, "Se muestra el desarrollo");
        Assert.IsFalse(vm.PatientForm.FormShowsGyneco);
        await vm.PatientForm.SavePatientModalAsync();
        StringAssert.Contains(vm.PatientForm.FormErrorMessage, "Negadas");

        vm.PatientForm.SetAllergiesDenied();
        await vm.PatientForm.SavePatientModalAsync();

        Assert.IsNull(vm.PatientForm.FormErrorMessage);
        var saved = (await _service.GetPatientsAsync()).Single(p => p.FullName == "Rodrigo Medina");
        Assert.AreEqual(DateTime.Today.AddMonths(-30), saved.BirthDate);
        Assert.AreEqual(2, saved.Age);
        Assert.AreEqual("Negadas", saved.Allergies);
        Assert.AreEqual(PatientFormViewModel.BloodTypeUnknown, saved.BloodType);
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
        await vm.FinalizeConsultationAsync();

        var note = (await _service.GetPatientConsultationsAsync(patient.Id)).Single(c => c.Folio == folio);
        Assert.AreEqual(string.Empty, note.VitalSigns, "No se tomaron signos en esta consulta");
        Assert.AreEqual("TA: 110/70 mmHg", vm.SelectedPatient.VitalSigns, "Se conservan los últimos registrados");
    }

    [TestMethod]
    public async Task EditPatient_LoadsTheNameParts_AndSavingKeepsTheLastVitalSigns()
    {
        // Antes, editar el expediente y guardar reescribía los signos sin la glucosa ni la saturación
        var vitals = "TA: 130/85 mmHg | FC: 78 lpm | Glucosa: 142 mg/dL | SpO2: 97%";
        var stored = await _service.SavePatientAsync(new PatientRecord
        {
            FullName = "Laura Gómez Rivas",
            RecordNumber = "EXP-EDIT-001",
            Gender = "Femenino",
            BirthDate = new DateTime(1980, 5, 12),
            Allergies = "Negadas",
            VitalSigns = vitals
        });

        var vm = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false));
        await vm.InitialLoad;
        vm.SelectPatient(vm.Patients.Single(p => p.Id == stored.Id));
        vm.PatientForm.EditPatient();

        Assert.AreEqual("Laura", vm.PatientForm.FormFirstName);
        Assert.AreEqual("Gómez", vm.PatientForm.FormLastName);
        Assert.AreEqual("Rivas", vm.PatientForm.FormMaternalLastName);

        vm.PatientForm.FormPhone = "5551234567";
        await vm.PatientForm.SavePatientModalAsync();

        Assert.IsNull(vm.PatientForm.FormErrorMessage);
        var saved = (await _service.GetPatientsAsync()).Single(p => p.Id == stored.Id);
        Assert.AreEqual("5551234567", saved.Phone);
        Assert.AreEqual(vitals, saved.VitalSigns);
    }
}
