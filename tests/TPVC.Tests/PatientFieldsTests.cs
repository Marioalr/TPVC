using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;
using TPVC.Tests.Fase6;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.21 (H1a), guardar solo lo que cambió: una copia vieja del expediente (la ficha o la consulta abiertas) ya no pisa
/// lo que otro camino escribió mientras tanto (la próxima cita, lo que bajó de otro equipo, el folio regularizado), la
/// copia queda al día después de guardar, y descartar una consulta regresa el expediente igual que antes.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PatientFieldsTests
{
    [TestInitialize]
    public void Setup() => DialogService.ConfirmHandler = (_, _) => true;

    private static PatientRecord Stored(Fase6Clinic clinic, int id)
    {
        using var db = new AppDbContext(clinic.Options);
        return db.Patients.AsNoTracking().Single(p => p.Id == id);
    }

    private static int ScriptedId(Fase6Clinic clinic)
    {
        using var db = new AppDbContext(clinic.Options);
        return db.Patients.Single(p => p.RecordNumber == Fase6Scenarios.ScriptedRecord).Id;
    }

    /// <summary>Como una bajada de otro equipo: escribe la fila directo, con una marca posterior a la que tenía.</summary>
    private static void Download(Fase6Clinic clinic, int id, Action<PatientRecord> change)
    {
        using var db = new AppDbContext(clinic.Options);
        db.ApplyingRemoteChanges = true;
        var row = db.Patients.Single(p => p.Id == id);
        change(row);
        row.UpdatedAtUtc = row.UpdatedAtUtc.AddSeconds(1);
        db.SaveChanges();
    }

    private static long? SeqOf(Fase6Clinic clinic, Guid syncId)
    {
        using var db = new AppDbContext(clinic.Options);
        return ChangeLogTestSupport.SeqOf(db, nameof(PatientRecord), syncId);
    }

    private static async Task<(MedicalRecordsViewModel Records, ConsultationService Consultations, PatientRecord Patient)> OpenScriptedAsync(Fase6Clinic clinic)
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations, drafts: false);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        records.OpenPatientChart(patient);
        return (records, consultations, patient);
    }

    private static Task AddFreeSupplyAsync(MedicalRecordsViewModel records, string name)
    {
        records.Supplies.SupplySearchText = name;
        records.Supplies.SupplyQuantity = 1;
        return records.Supplies.SelectAndAddSupplyProductAsync(null);
    }

    // ================================================================== los campos

    [TestMethod]
    public void TheFields_AreEveryColumnOfThePatientButItsIdentityAndSyncMetadata()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, "Data Source=:memory:");
        using var db = new AppDbContext(builder.Options);
        var entity = db.Model.FindEntityType(typeof(PatientRecord))!;
        var columns = entity.GetProperties().Select(p => p.Name).ToHashSet();

        Assert.IsTrue(entity.GetProperties().All(p => p.PropertyInfo != null), "Sin columnas ocultas: cada una se lee de la instancia");
        CollectionAssert.IsSubsetOf(PatientFields.Metadata.ToList(), columns.ToList(), "Lo que no es campo existe en el modelo");
        CollectionAssert.AreEquivalent(columns.Except(PatientFields.Metadata).ToList(), PatientFields.All.Select(f => f.Name).ToList(),
            "Una columna nueva del expediente entra sola a lo que se compara");
        CollectionAssert.IsSubsetOf(PatientFields.Sheets.ToList(), PatientFields.All.Select(f => f.Name).ToList());
        Assert.IsTrue(PatientFields.All.All(f => f.Property.CanWrite), "Cada campo se puede poner al día desde la fila");

        // Lo que viaja a la nube es un campo, salvo la fecha de alta (la identidad y la marca no son columnas de la tabla)
        var synced = SyncSchema.FromModel(db.Model).Tables.Single(t => t.ClrType == typeof(PatientRecord)).Columns.Select(c => c.Name);
        CollectionAssert.AreEquivalent(synced.Except(new[] { nameof(PatientRecord.CreatedAt) }).ToList(), PatientFields.All.Select(f => f.Name).ToList());
    }

    /// <summary>Un valor distinto para cada campo según su tipo: un tipo nuevo obliga a decidir cómo se compara.</summary>
    private static object? Different(PatientFields.Field field, object? value)
    {
        if (field.IsSheet) return """[{"MedicineName":"Otro","ServiceName":"Otro","SupplyName":"Otro"}]""";
        var type = field.Property.PropertyType;
        if (type == typeof(string)) return (string?)value + " (otro)";
        if (type == typeof(int)) return (int)value! + 1;
        if (type == typeof(bool)) return !(bool)value!;
        if (type == typeof(DateTime)) return ((DateTime)value!).AddDays(1);
        if (type == typeof(DateTime?)) return ((DateTime?)value ?? new DateTime(2020, 1, 1)).AddDays(1);
        Assert.Fail($"El campo {field.Name} es de un tipo sin regla en esta prueba ({type.Name}): decida cómo se compara al guardar.");
        return null;
    }

    [TestMethod]
    public void EachField_IsSeenAsChanged_AndOnlyIt()
    {
        // Sin fecha de nacimiento, así la edad es la capturada (con ella se calcula al día)
        var patient = new PatientRecord { Id = 7, SyncId = Guid.NewGuid(), FullName = "Paciente de Prueba", RecordNumber = "EXP-2026-900", Age = 40 };
        patient.ActivePrescription = new List<PatientPrescriptionItem> { new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 cada 8 horas" } };
        PatientFields.Track(patient);
        Assert.IsEmpty(PatientFields.Changed(patient, patient.StoredSnapshot), "Sin cambios");
        Assert.HasCount(PatientFields.All.Count, PatientFields.Changed(patient, null), "Sin foto, todo");

        foreach (var field in PatientFields.All)
        {
            var copy = new PatientRecord { SyncId = patient.SyncId };
            foreach (var f in PatientFields.All) f.Set(copy, f.Get(patient));
            field.Set(copy, Different(field, field.Get(copy)));
            // Con fecha de nacimiento la edad se calcula al día: cambia con ella
            var expected = field.Name == nameof(PatientRecord.BirthDate)
                ? new[] { nameof(PatientRecord.Age), nameof(PatientRecord.BirthDate) }
                : new[] { field.Name };
            CollectionAssert.AreEqual(expected, PatientFields.Changed(copy, patient.StoredSnapshot).Select(f => f.Name).ToList(), field.Name);
        }

        // Una hoja escrita con otro formato (otro orden, sin el RowId que se deriva) dice lo mismo: no cambió
        var legacy = new PatientRecord { Id = 8, SyncId = Guid.NewGuid(), ActivePrescriptionJson = """[{"Dosage":"1 cada 8 horas","MedicineName":"Paracetamol 500 mg"}]""" };
        PatientFields.Track(legacy);
        legacy.SyncPrescriptionJson();
        StringAssert.Contains(legacy.ActivePrescriptionJson, "RowId");
        Assert.IsEmpty(PatientFields.Changed(legacy, legacy.StoredSnapshot));
        Assert.HasCount(1, PatientFields.Changed(legacy, legacy.StoredSnapshot!.Without(new[] { nameof(PatientRecord.Phone) })),
            "Lo que falta en la foto cuenta como cambiado");
    }

    // ================================================================== copia vieja abierta

    [TestMethod]
    public async Task ASheetSavedFromAStaleCopy_KeepsTheNextAppointmentWrittenMeanwhile_AndTheCopyShowsIt()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, patient) = await OpenScriptedAsync(clinic);
        records.ChartPrimaryAction();
        Assert.IsTrue(consultations.HasActiveConsultation, "Con la consulta en curso la copia abierta no se cambia");

        // Se agenda la siguiente cita: SaveAppointmentAsync la escribe en la fila, no en la copia abierta
        var date = DateTime.Today.AddDays(14);
        await clinic.Inventory.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id, Date = date, Time = "10:30", Reason = "Control de glucosa", Notes = "En ayunas"
        });
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.SelectedPatient);
        Assert.AreNotEqual(date, patient.NextAppointmentDate, "La copia abierta todavía no la ve");

        await AddFreeSupplyAsync(records, "Venda elástica 10 cm");

        var stored = Stored(clinic, patient.Id);
        Assert.AreEqual(date, stored.NextAppointmentDate, "Guardar la hoja no borra la cita");
        Assert.AreEqual("10:30", stored.NextAppointmentTime);
        Assert.AreEqual("Control de glucosa", stored.NextAppointmentReason);
        Assert.AreEqual("En ayunas", stored.NextAppointmentNotes);
        Assert.IsTrue(stored.ActiveSupplies.Any(s => s.SupplyName == "Venda elástica 10 cm"), "La hoja sí se guarda");

        Assert.AreEqual(date, patient.NextAppointmentDate, "La copia abierta queda al día");
        Assert.AreEqual("Control de glucosa", patient.NextAppointmentReason);
        Assert.AreEqual(stored.UpdatedAtUtc, patient.UpdatedAtUtc, "Con la versión guardada");
        records.Dispose();
    }

    [TestMethod]
    public async Task ADownloadedChange_SurvivesASheetSavedFromTheOpenCopy()
    {
        using var clinic = new Fase6Clinic();
        var (records, _, patient) = await OpenScriptedAsync(clinic);
        records.ChartPrimaryAction();

        // Bajan de otro equipo el teléfono y las alergias mientras la consulta está abierta
        Download(clinic, patient.Id, p =>
        {
            p.Phone = "5511112222";
            p.Allergies = "Penicilina (anafilaxia)";
        });
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.SelectedPatient);
        Assert.AreNotEqual("5511112222", patient.Phone);

        await AddFreeSupplyAsync(records, "Gasas de algodón");

        var stored = Stored(clinic, patient.Id);
        Assert.AreEqual("5511112222", stored.Phone);
        Assert.AreEqual("Penicilina (anafilaxia)", stored.Allergies, "Las alergias que bajaron no se pierden");
        Assert.IsTrue(stored.ActiveSupplies.Any(s => s.SupplyName == "Gasas de algodón"));
        Assert.AreEqual("Penicilina (anafilaxia)", patient.Allergies, "La copia abierta las muestra");
        records.Dispose();
    }

    [TestMethod]
    public async Task AFormEdit_WritesWhatTheDoctorChanged_AndKeepsWhatCameMeanwhile()
    {
        using var clinic = new Fase6Clinic();
        var (records, _, patient) = await OpenScriptedAsync(clinic);
        Assert.AreEqual(string.Empty, patient.IsPeriodHeavy, "El formulario lo muestra como \"Normal\"");

        records.PatientForm.EditPatient();
        Assert.IsTrue(records.PatientForm.IsPatientModalOpen);

        // Con el formulario abierto bajan de otro equipo el teléfono, las alergias y un dato que el formulario solo normaliza
        Download(clinic, patient.Id, p =>
        {
            p.Phone = "5511112222";
            p.Allergies = "Penicilina (anafilaxia)";
            p.IsPeriodHeavy = "Abundante";
        });
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.SelectedPatient, "Con el formulario abierto la copia no se cambia");

        records.PatientForm.FormOccupation = "Contadora";
        await records.PatientForm.SavePatientModalAsync();
        Assert.IsNull(records.PatientForm.FormErrorMessage);
        Assert.IsFalse(records.PatientForm.IsPatientModalOpen);

        var stored = Stored(clinic, patient.Id);
        Assert.AreEqual("Contadora", stored.Occupation, "Lo que se editó en el formulario se escribe");
        Assert.AreEqual("5511112222", stored.Phone);
        Assert.AreEqual("Penicilina (anafilaxia)", stored.Allergies);
        Assert.AreEqual("Abundante", stored.IsPeriodHeavy, "El \"Normal\" que solo pone el formulario no pisa lo que llegó");
        Assert.AreEqual("5598761235", stored.EmergencyContactPhone, "Lo que el formulario solo normaliza se escribe si en la base sigue igual, como antes");

        var shown = records.SelectedPatient!;
        Assert.AreEqual("Contadora", shown.Occupation);
        Assert.AreEqual("5511112222", shown.Phone, "La ficha queda al día");
        Assert.AreEqual("Abundante", shown.IsPeriodHeavy);
        records.Dispose();
    }

    [TestMethod]
    public async Task AfterASave_TheSnapshotMovesWithTheCopy_SoTheNextSaveStillDiffs()
    {
        using var clinic = new Fase6Clinic();
        var inventory = clinic.Inventory;
        var copy = (await inventory.GetPatientByIdAsync(ScriptedId(clinic)))!;

        var first = DateTime.Today.AddDays(7);
        await inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = copy.Id, Date = first, Time = "09:00", Reason = "Revisión" });
        copy.ActiveSupplies = new List<PatientSupplyItem>(copy.ActiveSupplies) { new() { SupplyName = "Venda", Quantity = 1 } };
        Assert.AreSame(copy, await inventory.SavePatientAsync(copy), "Regresa la misma instancia");
        Assert.AreEqual(first, copy.NextAppointmentDate, "La copia toma la cita de la fila");

        // La cita se cambia de día y baja un teléfono nuevo; la misma copia guarda otra cosa
        var second = DateTime.Today.AddDays(9);
        await inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = copy.Id, Date = second, Time = "12:00", Reason = "Revisión" });
        Download(clinic, copy.Id, p => p.Phone = "5533334444");
        copy.Allergies = "Sulfas";
        await inventory.SavePatientAsync(copy);

        var stored = Stored(clinic, copy.Id);
        Assert.AreEqual("Sulfas", stored.Allergies);
        Assert.AreEqual(second, stored.NextAppointmentDate, "La foto se movió con la copia: la cita que ya tenía no cuenta como edición");
        Assert.AreEqual("12:00", stored.NextAppointmentTime);
        Assert.AreEqual("5533334444", stored.Phone);
        Assert.IsTrue(stored.ActiveSupplies.Any(s => s.SupplyName == "Venda"));
        Assert.AreEqual(second, copy.NextAppointmentDate);
        Assert.AreEqual("5533334444", copy.Phone);
        Assert.IsEmpty(PatientFields.Changed(copy, copy.StoredSnapshot), "Al día con la base");
    }

    [TestMethod]
    public async Task ASaveWithoutChanges_WritesNothing_ButTheCopyCatchesUp()
    {
        using var clinic = new Fase6Clinic();
        var copy = (await clinic.Inventory.GetPatientByIdAsync(ScriptedId(clinic)))!;
        Download(clinic, copy.Id, p => p.Phone = "5599990000");
        var before = Stored(clinic, copy.Id);
        var seq = SeqOf(clinic, copy.SyncId);

        await clinic.Inventory.SavePatientAsync(copy);

        var after = Stored(clinic, copy.Id);
        Assert.AreEqual(before.UpdatedAtUtc, after.UpdatedAtUtc, "La marca no se mueve");
        Assert.AreEqual(seq, SeqOf(clinic, copy.SyncId), "Nada nuevo que subir");
        Assert.AreEqual(before.ActivePrescriptionJson, after.ActivePrescriptionJson);
        Assert.AreEqual("5599990000", copy.Phone);
        Assert.AreEqual(after.UpdatedAtUtc, copy.UpdatedAtUtc);
    }

    [TestMethod]
    public async Task ACopyWhoseFolioWasRegularizedMeanwhile_DoesNotBringBackTheLetteredFolio()
    {
        using var clinic = new Fase6Clinic();
        var copy = (await clinic.Inventory.GetPatientByIdAsync(ScriptedId(clinic)))!;
        var lettered = copy.RecordNumber;
        Download(clinic, copy.Id, p =>
        {
            p.OriginalFolio = p.RecordNumber;
            p.RecordNumber = "EXP-2026-777";
        });

        copy.Allergies = "Látex";
        await clinic.Inventory.SavePatientAsync(copy);

        var stored = Stored(clinic, copy.Id);
        Assert.AreEqual("EXP-2026-777", stored.RecordNumber);
        Assert.AreEqual(lettered, stored.OriginalFolio);
        Assert.AreEqual("Látex", stored.Allergies);
        Assert.AreEqual("EXP-2026-777", copy.RecordNumber);
    }

    [TestMethod]
    public async Task ARepeatedFolio_NoLongerBlocksSavingSomethingElse()
    {
        using var clinic = new Fase6Clinic();
        int id = ScriptedId(clinic);
        string thirdFolio;
        using (var db = new AppDbContext(clinic.Options))
        {
            // Dos expedientes con el mismo folio (paso 8.16: se conservan y aparecen en un aviso)
            var others = db.Patients.Where(p => p.Id != id).OrderBy(p => p.Id).ToList();
            others[0].RecordNumber = db.Patients.Single(p => p.Id == id).RecordNumber;
            thirdFolio = others[1].RecordNumber;
            db.SaveChanges();
        }

        var copy = (await clinic.Inventory.GetPatientByIdAsync(id))!;
        copy.Allergies = "Látex";
        await clinic.Inventory.SavePatientAsync(copy);
        Assert.AreEqual("Látex", Stored(clinic, id).Allergies, "El folio no se escribe, así que no se revisa (antes se rechazaba todo el guardado)");

        copy.RecordNumber = thirdFolio;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => clinic.Inventory.SavePatientAsync(copy),
            "Un folio tecleado que ya usa otro expediente sí se rechaza");
    }

    [TestMethod]
    public async Task AStaleSheet_IsNotWrittenNorReplacedInMemory_AndTheNextReloadTakesTheStoredOne()
    {
        using var clinic = new Fase6Clinic();
        var (records, _, patient) = await OpenScriptedAsync(clinic);
        records.ChartPrimaryAction();

        // Baja de otro equipo la hoja de insumos
        Download(clinic, patient.Id, p => p.ActiveSupplies = new List<PatientSupplyItem> { new() { SupplyName = "Venda de la tablet", Quantity = 1 } });
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.SelectedPatient);

        // La consulta guarda la receta
        records.Prescription.PrescriptionMedicineName = "Loratadina 10 mg";
        records.Prescription.PrescriptionDosage = "1 tableta cada 24 horas";
        await records.Prescription.SelectAndAddPrescriptionProductAsync(null);

        var stored = Stored(clinic, patient.Id);
        Assert.AreEqual("Venda de la tablet", stored.ActiveSupplies.Single().SupplyName, "La hoja que no se editó aquí no se pisa");
        Assert.IsTrue(stored.ActivePrescription.Any(p => p.MedicineName == "Loratadina 10 mg"));
        Assert.IsFalse(patient.ActiveSupplies.Any(s => s.SupplyName == "Venda de la tablet"), "En memoria no se cambia a media consulta");
        Assert.IsLessThan(stored.UpdatedAtUtc, patient.UpdatedAtUtc, "La copia no se da por al día");

        // Descartar la consulta tampoco la escribe, y al soltar la copia se recarga sola y toma la de la base
        await records.DiscardActiveConsultationAsync();
        Assert.AreEqual("Venda de la tablet", Stored(clinic, patient.Id).ActiveSupplies.Single().SupplyName);
        Assert.AreNotSame(patient, records.SelectedPatient, "Libre otra vez: la recarga cambia la copia por la de la base");
        Assert.AreEqual("Venda de la tablet", records.SelectedPatient!.ActiveSupplies.Single().SupplyName);
        records.Dispose();
    }

    [TestMethod]
    public async Task AfterTheConsultation_TheChartTakesTheSheetThatCameMeanwhile_AndEditingItKeepsTheOtherDevicesRow()
    {
        using var clinic = new Fase6Clinic();
        var (records, _, patient) = await OpenScriptedAsync(clinic);
        Download(clinic, patient.Id, p => p.ActiveSupplies = new List<PatientSupplyItem> { new() { SupplyName = "Gasas", Quantity = 1 } });
        await records.LoadPatientsAsync();
        patient = records.SelectedPatient!;
        Assert.AreEqual("Gasas", patient.ActiveSupplies.Single().SupplyName, "Sin consulta, la recarga toma la hoja de la base");

        // A media consulta baja la hoja con un renglón más de la tablet: la copia abierta se queda como estaba
        records.ChartPrimaryAction();
        Download(clinic, patient.Id, p => p.ActiveSupplies = new List<PatientSupplyItem>
        {
            new() { SupplyName = "Gasas", Quantity = 1 },
            new() { SupplyName = "Venda de la tablet", Quantity = 1 }
        });
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.SelectedPatient);
        records.Prescription.PrescriptionMedicineName = "Loratadina 10 mg";
        records.Prescription.PrescriptionDosage = "1 tableta cada 24 horas";
        await records.Prescription.SelectAndAddPrescriptionProductAsync(null);

        // Se descarta: la ficha ya muestra la hoja de la base sin otra recarga
        await records.DiscardActiveConsultationAsync();
        var chart = records.SelectedPatient!;
        Assert.AreNotSame(patient, chart, "Al cerrar la consulta la copia vieja se cambia por la de la base");
        CollectionAssert.AreEqual(new[] { "Gasas", "Venda de la tablet" }, chart.ActiveSupplies.Select(s => s.SupplyName).ToList());

        // Quitar las gasas en la ficha no se lleva la venda de la tablet
        await records.Supplies.RemoveSupplyItemFromPatientAsync(chart.ActiveSupplies.Single(s => s.SupplyName == "Gasas"));
        Assert.AreEqual("Venda de la tablet", Stored(clinic, patient.Id).ActiveSupplies.Single().SupplyName);
        records.Dispose();
    }

    [TestMethod]
    public async Task AnotherPatientsForm_MidConsultation_DoesNotSwapTheConsultationCopy_UntilTheConsultationEnds()
    {
        using var clinic = new Fase6Clinic();
        var (records, _, patient) = await OpenScriptedAsync(clinic);
        records.ChartPrimaryAction();
        Download(clinic, patient.Id, p => p.Allergies = "Látex");
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.SelectedPatient, "La copia de la consulta se queda");

        // A media consulta se abre otro expediente y se cierra su edición: no se cambia la copia de la consulta
        var other = records.Patients.First(p => p.Id != patient.Id);
        records.OpenPatientChart(other);
        records.PatientForm.EditPatient();
        records.PatientForm.ClosePatientModal();
        Assert.AreSame(patient, records.Patients.Single(p => p.Id == patient.Id), "Con otro seleccionado la lista conserva la copia de la consulta");
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.Patients.Single(p => p.Id == patient.Id), "Tampoco una recarga cualquiera");

        // "Volver a la consulta" la retoma sobre la misma instancia
        records.ShowConsultation();
        Assert.AreSame(patient, records.SelectedPatient);
        Assert.AreNotEqual("Látex", patient.Allergies);

        // Al terminar la consulta la ficha muestra lo de la base (el guardado del descarte ya la pone al día)
        await records.DiscardActiveConsultationAsync();
        Assert.AreEqual("Látex", records.SelectedPatient!.Allergies);
        Assert.AreEqual("Látex", Stored(clinic, patient.Id).Allergies, "Descartar no regresa las alergias de antes");
        records.Dispose();
    }

    // ================================================================== descartar la consulta

    [TestMethod]
    public async Task DiscardingAConsultation_RestoresTheSummaryAndThePreviousPrescription()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, patient) = await OpenScriptedAsync(clinic);
        var before = Stored(clinic, patient.Id);
        var previous = patient.ActivePrescription.Select(p => (p.MedicineName, p.RowId)).ToList();
        Assert.IsNotEmpty(previous);

        records.ChartPrimaryAction();
        records.Prescription.PrescriptionMedicineName = "Loratadina 10 mg";
        records.Prescription.PrescriptionDosage = "1 tableta cada 24 horas";
        await records.Prescription.SelectAndAddPrescriptionProductAsync(null);
        records.Consultation.ConsultationChiefComplaint = "Paciente equivocado";
        records.Consultation.ConsultationTA = "150/95";
        records.Consultation.ConsultationDiagnosis = "R51 Cefalea";
        records.Consultation.ConsultationNotes = "Se descartará";
        Assert.IsNotNull(await records.Consultation.SaveActiveConsultationToDbAsync());
        Assert.AreEqual("R51 Cefalea", Stored(clinic, patient.Id).LastDiagnosis, "La nota escribió el resumen en la base");
        Assert.IsFalse(PatientFields.Changed(patient, patient.StoredSnapshot).Any(f => PatientSummarySnapshot.Fields.Contains(f.Name)),
            "La copia refleja el resumen guardado y su foto también");

        // Una hoja guardada después de la nota (la copia se pone al día con la fila)
        await AddFreeSupplyAsync(records, "Gasas");
        await records.DiscardActiveConsultationAsync();
        Assert.IsFalse(consultations.HasActiveConsultation);

        var stored = Stored(clinic, patient.Id);
        Assert.AreEqual(before.LastVisit, stored.LastVisit);
        Assert.AreEqual(before.LastDiagnosis, stored.LastDiagnosis);
        Assert.AreEqual(before.VitalSigns, stored.VitalSigns);
        Assert.AreEqual(before.ClinicalNotes, stored.ClinicalNotes);
        CollectionAssert.AreEqual(previous, stored.ActivePrescription.Select(p => (p.MedicineName, p.RowId)).ToList(), "La receta anterior, con sus renglones");
        CollectionAssert.AreEqual(previous, patient.ActivePrescription.Select(p => (p.MedicineName, p.RowId)).ToList());
        Assert.AreEqual(before.LastDiagnosis, patient.LastDiagnosis);
        records.Dispose();
    }

    // ================================================================== sin foto y alta

    [TestMethod]
    public async Task ACopyWithoutSnapshot_IsSavedWhole_AsBefore_AndThenDiffs()
    {
        using var clinic = new Fase6Clinic();
        int id = ScriptedId(clinic);
        var before = Stored(clinic, id);
        Assert.IsNotEmpty(before.Phone);

        // Hecha en memoria (como algunas pruebas y la vista sin base): sin foto, la fila completa
        var bare = new PatientRecord { Id = id, FullName = before.FullName, RecordNumber = before.RecordNumber, Allergies = "Sulfas" };
        Assert.IsNull(bare.StoredSnapshot);
        await clinic.Inventory.SavePatientAsync(bare);
        var stored = Stored(clinic, id);
        Assert.AreEqual("Sulfas", stored.Allergies);
        Assert.AreEqual(string.Empty, stored.Phone, "Como antes del paso 8.21: se escribió todo");
        Assert.AreEqual(before.SyncId, stored.SyncId);
        Assert.AreEqual(before.CreatedAt, stored.CreatedAt, "La identidad y la fecha de alta no se tocan");

        // Ya guardada tiene su foto: la siguiente vez solo escribe lo que cambió
        Assert.IsNotNull(bare.StoredSnapshot);
        Download(clinic, id, p => p.Phone = "5544443333");
        bare.Occupation = "Maestra";
        await clinic.Inventory.SavePatientAsync(bare);
        stored = Stored(clinic, id);
        Assert.AreEqual("Maestra", stored.Occupation);
        Assert.AreEqual("5544443333", stored.Phone);
    }

    [TestMethod]
    public async Task ANewPatient_IsInsertedWhole_WithItsFolio_AndGetsItsSnapshot()
    {
        using var clinic = new Fase6Clinic();
        var created = new PatientRecord { FullName = "  Paciente Nuevo Prueba  ", Age = 30, Phone = "5512345678" };
        var returned = await clinic.Inventory.SavePatientAsync(created);

        Assert.AreSame(created, returned);
        Assert.IsGreaterThan(0, created.Id);
        Assert.AreEqual("Paciente Nuevo Prueba", created.FullName);
        StringAssert.StartsWith(created.RecordNumber, "EXP-");
        Assert.IsNotNull(created.StoredSnapshot);
        var stored = Stored(clinic, created.Id);
        Assert.AreEqual(created.RecordNumber, stored.RecordNumber);
        Assert.AreEqual("5512345678", stored.Phone);
        Assert.AreEqual(stored.UpdatedAtUtc, created.UpdatedAtUtc);

        // La forma del plan regresa la fila fresca (otra instancia) y deja al día a la editada
        created.Email = "nuevo@correo.mx";
        var fresh = await clinic.Inventory.SavePatientAsync(created, created.StoredSnapshot);
        Assert.AreNotSame(created, fresh);
        Assert.AreEqual("nuevo@correo.mx", fresh.Email);
        Assert.AreEqual(fresh.UpdatedAtUtc, created.UpdatedAtUtc);
        Assert.IsNotNull(fresh.StoredSnapshot);
    }
}
