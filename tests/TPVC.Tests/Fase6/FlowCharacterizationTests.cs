using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 1b): caracterización de los flujos que cruzan varias partes de Pacientes y que la
/// separación en ViewModels reescribe (con el mismo orden de sentencias). Fija lo que hacen hoy, con
/// las rarezas conocidas incluidas, para que un cambio de dueño no los altere sin aviso.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FlowCharacterizationTests
{
    [TestInitialize]
    public void Setup() => DialogService.ConfirmHandler = (_, _) => true;

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 20) await Task.Delay(20);
        Assert.IsTrue(condition(), what);
    }

    private static void BumpPatientInDatabase(Fase6Clinic clinic, int patientId, string allergies)
    {
        // Otro equipo cambió el expediente (llegó con la sincronización): fila más reciente en la base
        using var db = new AppDbContext(clinic.Options);
        db.ApplyingRemoteChanges = true;
        var row = db.Patients.Single(p => p.Id == patientId);
        row.Allergies = allergies;
        row.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(5);
        db.SaveChanges();
    }

    // ================================================================== cobro de la consulta

    [TestMethod]
    public async Task SaleCompleted_ClosesSheetsAndForm_ClearsThePatient_MarksTheAppointment_AndGoesHome()
    {
        using var clinic = new Fase6Clinic();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        var appointment = await clinic.Inventory.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            Date = DateTime.Today,
            Time = "23:30",
            Reason = "Dolor de garganta"
        });

        records.AttendAppointment(appointment);
        records.ConsultationDiagnosis = "J02 Faringitis aguda";
        var folio = consultations.CurrentConsultation!.Folio;
        await records.FinalizeConsultationAsync();
        Assert.IsTrue(records.IsFinishConsultationOpen);

        // Todo lo que pudiera quedar abierto detrás del cobro
        records.IsPrescriptionModalOpen = true;
        records.IsSuppliesModalOpen = true;
        records.IsServicesModalOpen = true;
        records.IsCatalogBrowserModalOpen = true;
        records.PatientForm.IsPatientModalOpen = true;
        records.Info.InfoModalMessage = "Aviso anterior";
        records.Info.IsInfoModalVisible = true;

        records.Checkout.SelectedPaymentMethod = "Efectivo";
        records.Checkout.CashReceived = 5000m;
        await records.Checkout.ChargeAsync();
        await WaitUntilAsync(() => !consultations.HasActiveConsultation && records.Navigation.IsHomePage, "La consulta se cierra y vuelve al inicio");
        await WaitUntilAsync(() => records.Home.TodayAppointments.Any(a => a.Id == appointment.Id && a.IsAttended), "La cita de hoy se ve atendida");

        Assert.IsFalse(records.IsFinishConsultationOpen);
        Assert.IsFalse(records.IsPrescriptionModalOpen);
        Assert.IsFalse(records.IsSuppliesModalOpen);
        Assert.IsFalse(records.IsServicesModalOpen);
        Assert.IsFalse(records.IsCatalogBrowserModalOpen);
        Assert.IsFalse(records.PatientForm.IsPatientModalOpen);
        Assert.IsFalse(records.Navigation.IsFormPage);
        Assert.IsNull(records.SelectedPatient, "Sin paciente seleccionado");
        Assert.IsEmpty(records.ConsultationsHistory);
        Assert.AreEqual(string.Empty, records.ConsultationChiefComplaint, "Campos de la consulta limpios");
        Assert.AreEqual(string.Empty, records.ConsultationDiagnosis);
        Assert.AreEqual("00:00", records.Active.ActiveConsultationElapsedTime);
        // El aviso anterior se cerró; el que queda es el del cobro (sin servicio de avisos usa la ventana)
        StringAssert.StartsWith(records.Info.InfoModalMessage, "Consulta cobrada");

        Assert.AreEqual("Atendida", (await clinic.Inventory.GetAppointmentsByDayAsync(DateTime.Today)).Single(a => a.Id == appointment.Id).Status);
        Assert.IsTrue((await clinic.Inventory.GetPatientConsultationsAsync(patient.Id)).Any(c => c.Folio == folio), "La nota quedó en el expediente");
        Assert.IsFalse(File.Exists(clinic.DraftPath), "Cobrada: ya no se recupera");
        records.Dispose();
    }

    // ================================================================== consulta que sigue en otro equipo

    [TestMethod]
    public async Task HandoffRelease_ClosesSheets_ClosesWithoutDiscarding_AndForcesAFreshListInstance()
    {
        using var clinic = new Fase6Clinic();
        await clinic.ConnectCloudAsync();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        var previous = patient.ActivePrescription.Select(p => p.MedicineName).ToList();

        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        await Fase6Scenarios.FillConsultationAsync(records, consultations);
        var folio = consultations.CurrentConsultation!.Folio;
        Assert.IsNotNull(await records.SaveActiveConsultationToDbAsync(), "Avance de la nota guardado antes de enviarla");

        await records.OpenSendConsultationSheetAsync();
        records.SelectedHandoffDevice = records.HandoffDevices.Single(d => d.DeviceId == "device-tablet");
        await records.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(records.IsConsultationHandedOff, records.HandoffSheetError ?? records.Info.InfoModalMessage);

        // Lo que estuviera abierto al llegar la aceptación
        records.IsPrescriptionModalOpen = true;
        records.IsSuppliesModalOpen = true;
        records.IsServicesModalOpen = true;
        records.IsCatalogBrowserModalOpen = true;
        records.IsPrescriptionPdfModalOpen = true;
        records.IsAllergyAlertOpen = true;
        records.Agenda.IsAgendaPanelOpen = true;
        records.IsSendConsultationSheetOpen = true;
        records.IsFinishConsultationOpen = true;

        // La instancia en memoria es "más nueva" que la base: solo una recarga forzada la cambia en la lista
        var before = records.SelectedPatient!;
        before.UpdatedAtUtc = DateTime.UtcNow.AddYears(1);
        await records.LoadPatientsAsync();
        Assert.AreSame(before, records.Patients.Single(p => p.Id == patient.Id), "Sin forzar y sin versión más nueva se conserva la instancia");

        // La tablet la tomó (en la nube) y la PC se entera en su revisión
        Assert.AreEqual("enviada", clinic.CloudScalar("SELECT State FROM ConsultationHandoffs"));
        clinic.CloudScalar("UPDATE ConsultationHandoffs SET State = 'aceptada', AcceptToken = 'ficha-tablet'");
        await records.PollHandoffsOnceAsync();

        Assert.IsFalse(consultations.HasActiveConsultation, records.Info.InfoModalMessage);
        Assert.IsFalse(records.IsConsultationHandedOff);
        Assert.IsFalse(records.IsPrescriptionModalOpen);
        Assert.IsFalse(records.IsSuppliesModalOpen);
        Assert.IsFalse(records.IsServicesModalOpen);
        Assert.IsFalse(records.IsCatalogBrowserModalOpen);
        Assert.IsFalse(records.IsPrescriptionPdfModalOpen);
        Assert.IsFalse(records.IsAllergyAlertOpen);
        Assert.IsFalse(records.Agenda.IsAgendaPanelOpen);
        Assert.IsFalse(records.IsSendConsultationSheetOpen);
        Assert.IsFalse(records.IsFinishConsultationOpen);
        Assert.IsTrue(records.Navigation.IsChartPage, "Queda la ficha del paciente");
        StringAssert.Contains(records.Info.InfoModalMessage, "continúa en Tablet del consultorio");
        Assert.IsFalse(File.Exists(clinic.DraftPath), "Ya no se recupera aquí");

        // Sin descartar: la nota sigue y el expediente no vuelve a como estaba antes de la consulta
        var stored = (await clinic.Inventory.GetPatientByIdAsync(patient.Id))!;
        Assert.AreEqual("Paracetamol 500 mg (Caja con 20 tabletas)", stored.ActivePrescription.Single().MedicineName);
        CollectionAssert.AreNotEqual(previous, stored.ActivePrescription.Select(p => p.MedicineName).ToList());
        Assert.AreEqual("E11.9 Diabetes mellitus tipo 2 sin complicaciones", stored.LastDiagnosis);
        Assert.IsTrue((await clinic.Inventory.GetPatientConsultationsAsync(patient.Id)).Any(c => c.Folio == folio));
        using (var db = new AppDbContext(clinic.Options))
        {
            Assert.AreEqual(0, db.SyncTombstones.Count(t => t.EntityType == nameof(PatientConsultation)), "Liberar no borra notas");
        }

        // Recarga forzada del expediente (aunque la base no traiga una versión más nueva): la lista y la
        // ficha toman la instancia de la base (SelectedPatient cambia por referencia, no por Id)
        await WaitUntilAsync(() => !ReferenceEquals(records.Patients.Single(p => p.Id == patient.Id), before), "La lista vuelve a leer el expediente");
        Assert.AreSame(records.Patients.Single(p => p.Id == patient.Id), records.SelectedPatient, "La ficha también toma la de la base");
        Assert.AreEqual("Paracetamol 500 mg (Caja con 20 tabletas)", records.Patients.Single(p => p.Id == patient.Id).ActivePrescription.Single().MedicineName);
        records.Dispose();
    }

    // ================================================================== cierre desde el Punto de Venta

    [TestMethod]
    public async Task PosClose_RestoresThePreviousPrescription_DeletesTheDraft_AndClearsFeeOverrideAndReceivedIds()
    {
        using var clinic = new Fase6Clinic();
        var services = await clinic.Inventory.GetClinicalServicesAsync();
        var fee = services.Single(s => s.Code == "SERV-002");
        var store = clinic.Store();

        // Una consulta recibida de otro equipo (honorario de aquel y su envío), aún sin receta
        Assert.IsTrue(store.Save(new ConsultationDraft
        {
            PatientId = 2,
            PatientName = "Rodríguez Silva, María Elena",
            Folio = "CNS-20261007-183015",
            StartedAt = DateTime.Now.AddMinutes(-20),
            ChiefComplaint = "Control mensual",
            Section = 2,
            Items = { new ConsultationProductItem { ProductId = -1_000_777, Code = "LIBRE", Name = "Curación especial", Unit = "Servicio", UnitPrice = 150m, Quantity = 1 } },
            PreviousPrescriptions = new List<PatientPrescriptionItem>
            {
                new() { MedicineName = "Tiras Reactivas para Glucosa Capilar", Dosage = "Monitoreo matutino", RecommendedQuantity = 1 },
                new() { MedicineName = "Alcohol Etílico Desnaturalizado 70%", Dosage = "Asepsia", RecommendedQuantity = 1 }
            },
            FeeServiceSyncId = fee.SyncId,
            ReceivedHandoffId = "envio-1",
            ReceivedHandoffToken = "ficha-1"
        }));

        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        Assert.IsTrue(records.HasActiveConsultation);
        Assert.AreEqual("SERV-002", records.ConsultationFeeService?.Code, "Honorario del equipo que la envió");
        var patient = records.SelectedPatient!;
        Assert.IsEmpty(patient.ActivePrescription, "La consulta aún no tiene receta");

        // Se cobra desde el Punto de Venta: el servicio la cierra sin pasar por "Terminar consulta"
        consultations.CloseActiveConsultation();

        Assert.IsFalse(records.HasActiveConsultation);
        Assert.IsFalse(File.Exists(clinic.DraftPath), "El borrador se borra");
        Assert.IsFalse(records.IsConsultationRecovered);
        Assert.AreEqual("SERV-001", records.ConsultationFeeService?.Code, "Vuelve el honorario propio");
        await WaitUntilAsync(() => patient.ActivePrescription.Count == 2, "Vuelve la receta anterior");
        CollectionAssert.AreEqual(
            new[] { "Tiras Reactivas para Glucosa Capilar", "Alcohol Etílico Desnaturalizado 70%" },
            patient.ActivePrescription.Select(p => p.MedicineName).ToList());
        await WaitUntilAsync(() => clinic.Inventory.GetPatientByIdAsync(patient.Id).Result!.ActivePrescription.Count == 2, "También en la base");

        // La siguiente consulta ya no lleva el envío recibido ni el honorario de otro equipo
        records.StartActiveConsultation();
        records.SaveConsultationDraftNow();
        var next = store.Load()!;
        Assert.IsNull(next.ReceivedHandoffId);
        Assert.IsNull(next.ReceivedHandoffToken);
        Assert.IsNull(next.FeeServiceSyncId);
        records.Dispose();
    }

    // ================================================================== recarga de expedientes

    [TestMethod]
    public async Task LoadPatients_FresherRow_ReplacesTheChartInstance_KeepingWhatTheDoctorWasLookingAt()
    {
        using var clinic = new Fase6Clinic();
        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false), drafts: false);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        records.OpenPatientChart(patient);
        await WaitUntilAsync(() => records.ConsultationsHistory.Count > 0, "Historial cargado");
        records.TimelineSearchText = "glucosa";
        records.IsVitalsChartVisible = true;
        records.ToggleAntecedent(records.AntecedentSections.First());
        var openSection = records.AntecedentSections.First().Key;

        BumpPatientInDatabase(clinic, patient.Id, "Látex");
        await records.LoadPatientsAsync();

        // La lista y la ficha toman la versión de la base (antes la ficha seguía con la copia vieja y al
        // guardarla pisaba lo que llegó de otro equipo); lo que el médico estaba viendo se conserva
        var listed = records.Patients.Single(p => p.Id == patient.Id);
        Assert.AreNotSame(patient, listed, "Se toma la versión de la base");
        Assert.AreEqual("Látex", listed.Allergies);
        Assert.AreSame(listed, records.SelectedPatient);
        Assert.AreEqual("Látex", records.SelectedPatient!.Allergies);
        Assert.AreEqual("glucosa", records.TimelineSearchText);
        Assert.IsTrue(records.IsVitalsChartVisible);
        Assert.IsTrue(records.AntecedentSections.Single(s => s.Key == openSection).IsOpen);
        Assert.IsTrue(records.Navigation.IsChartPage);
        records.Dispose();
    }

    [TestMethod]
    public async Task LoadPatients_KeepsTheInstance_WhileItsFormIsOpen_OrItIsTheConsultationPatient()
    {
        using var clinic = new Fase6Clinic();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations, drafts: false);
        var patient = Fase6Scenarios.ScriptedPatient(records);

        // Alta / edición abierta sobre la instancia
        records.OpenPatientChart(patient);
        records.PatientForm.EditPatient();
        Assert.IsTrue(records.PatientForm.IsPatientModalOpen);
        BumpPatientInDatabase(clinic, patient.Id, "Látex");
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.SelectedPatient);
        Assert.AreSame(patient, records.Patients.Single(p => p.Id == patient.Id), "Con el formulario abierto la lista conserva la instancia");
        Assert.AreNotEqual("Látex", patient.Allergies);
        records.PatientForm.ClosePatientModal();

        // Paciente de la consulta en curso
        records.ChartPrimaryAction();
        Assert.IsTrue(records.IsConsultationForSelectedPatient);
        BumpPatientInDatabase(clinic, patient.Id, "Sulfas");
        await records.LoadPatientsAsync();
        Assert.AreSame(patient, records.SelectedPatient);
        Assert.AreSame(patient, records.Patients.Single(p => p.Id == patient.Id), "El de la consulta en curso tampoco se cambia en la lista");
        Assert.AreNotEqual("Sulfas", patient.Allergies);
        records.Dispose();
    }

    // ================================================================== catálogo recargado

    [TestMethod]
    public async Task LoadServices_ReappliesThePrescriptionSuppliesServicesAndCatalogLists()
    {
        using var clinic = new Fase6Clinic();
        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false), drafts: false);
        records.PrescriptionSearchText = "zinc";
        records.SupplySearchText = "zinc";
        records.ServiceModalSearchText = "zinc";
        records.CatalogBrowserMode = "Medications";
        records.CatalogBrowserSearchText = "zinc";
        Assert.IsEmpty(records.PrescriptionProductSuggestions);
        Assert.IsEmpty(records.SupplyProductSuggestions);
        Assert.IsEmpty(records.ServiceModalSuggestions);
        Assert.IsEmpty(records.CatalogBrowserFilteredProducts);

        // El catálogo cambia (otro equipo, o Configuración) y se vuelve a leer
        using (var db = new AppDbContext(clinic.Options))
        {
            db.Products.Single(p => p.Code == "MED-001").GenericName = "Paracetamol con zinc";
            db.Products.Single(p => p.Code == "CUR-001").GenericName = "Gasa con óxido de zinc";
            db.ClinicalServices.Single(s => s.Code == "SERV-005").Category = "Zinc";
            db.SaveChanges();
        }
        await records.LoadServicesAsync();

        CollectionAssert.AreEqual(new[] { "MED-001" }, records.PrescriptionProductSuggestions.Select(p => p.Code).ToList());
        CollectionAssert.AreEqual(new[] { "CUR-001" }, records.SupplyProductSuggestions.Select(p => p.Code).ToList());
        CollectionAssert.AreEqual(new[] { "SERV-005" }, records.ServiceModalSuggestions.Select(s => s.Code).ToList());
        CollectionAssert.AreEqual(new[] { "MED-001" }, records.CatalogBrowserFilteredProducts.Select(p => p.Code).ToList());
        Assert.IsTrue(records.IsPrescriptionDropdownOpen);
        Assert.IsTrue(records.IsSupplyDropdownOpen);
        Assert.IsTrue(records.IsServiceModalDropdownOpen);
        Assert.AreEqual("MED-001", records.SelectedPrescriptionProduct?.Code);
        records.Dispose();
    }

    // ================================================================== nota guardada y cambio de paciente

    [TestMethod]
    public async Task SameIdReassignment_KeepsTheSavedFlag_AnotherPatientOrNoneResetsIt()
    {
        using var clinic = new Fase6Clinic();
        var store = clinic.Store();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        records.ConsultationDiagnosis = "Z00 Revisión general";
        Assert.IsNotNull(await records.SaveActiveConsultationToDbAsync());

        bool SavedToRecord()
        {
            records.SaveConsultationDraftNow();
            return store.Load()!.SavedToRecord;
        }

        Assert.IsTrue(SavedToRecord(), "El avance quedó guardado");

        // La misma ficha leída otra vez (otra instancia, mismo Id): se toma la instancia y sigue guardada
        var reread = new PatientRecord { Id = patient.Id, FullName = patient.FullName, RecordNumber = patient.RecordNumber };
        records.SelectedPatient = reread;
        Assert.AreSame(reread, records.SelectedPatient);
        Assert.IsTrue(SavedToRecord(), "Mismo paciente: no se reinicia");

        records.SelectedPatient = null;
        Assert.IsFalse(SavedToRecord(), "Sin paciente: se reinicia");

        records.SelectedPatient = patient;
        Assert.IsNotNull(await records.SaveActiveConsultationToDbAsync());
        records.SelectedPatient = records.Patients.First(p => p.Id != patient.Id);
        Assert.IsFalse(SavedToRecord(), "Otro paciente: se reinicia");
        records.Dispose();
    }

    // ================================================================== atender una cita

    [TestMethod]
    public async Task AttendAppointment_SetsTheAttendingIdLast_ThenSchedulesTheDraft()
    {
        using var clinic = new Fase6Clinic();
        var store = clinic.Store();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        var appointment = await clinic.Inventory.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            Date = DateTime.Today,
            Time = "23:30",
            Reason = "Dolor de garganta"
        });

        // Lo que se ve en cada aviso de la consulta mientras se atiende la cita
        var seen = new List<string>();
        PropertyChangedEventHandler onChanged = (_, e) =>
        {
            if (e.PropertyName is not (nameof(ActiveConsultationSession.HasActiveConsultation) or nameof(MedicalRecordsViewModel.ConsultationChiefComplaint))) return;
            if (!records.HasActiveConsultation) return;
            records.SaveConsultationDraftNow();
            seen.Add($"{e.PropertyName}:{store.Load()?.AttendingAppointmentId?.ToString() ?? "sin cita"}");
        };
        records.PropertyChanged += onChanged;
        records.Active.PropertyChanged += onChanged;
        records.AutosaveDelay = TimeSpan.FromMilliseconds(30);

        records.AttendAppointment(appointment);

        Assert.IsTrue(records.Navigation.IsConsultationPage);
        Assert.AreEqual("Dolor de garganta", records.ConsultationChiefComplaint, "El motivo se escribe después de iniciar");
        CollectionAssert.Contains(seen, "ConsultationChiefComplaint:sin cita");
        Assert.IsTrue(seen.All(s => s.EndsWith(":sin cita", StringComparison.Ordinal)), "La cita se asigna al final: " + string.Join(", ", seen));
        Assert.IsTrue(seen.Count(s => s.StartsWith("HasActiveConsultation", StringComparison.Ordinal)) >= 2, "Inicio de la consulta (dos avisos)");

        // Y el guardado programado después de asignarla ya la lleva, sin otra edición
        await WaitUntilAsync(() => store.Load()?.AttendingAppointmentId == appointment.Id, "La copia guardada sola lleva la cita");
        records.Dispose();
    }
}
