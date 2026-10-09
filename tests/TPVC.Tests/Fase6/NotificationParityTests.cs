using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 1c): paridad de avisos PropertyChanged. Cada escenario graba los nombres que se avisan
/// (sin importar qué objeto los levanta, PatientsNotificationRecorder) y los compara con
/// TestAssets/fase6/notifications/*.txt, tomados del código de la v1.18. Mover un miembro a otro
/// ViewModel no debe cambiar ni los nombres ni, en los escenarios síncronos, su orden: si cambian, la
/// pantalla deja de enterarse de algo o se entera en otro momento.
///
/// Escenarios síncronos (orden exacto): sin base de datos, en el hilo de la prueba. Escenarios con base
/// de datos o tareas en segundo plano: el conjunto de nombres, después de que dejan de llegar avisos.
/// Para que no dependan del reloj, la consulta en curso "empieza" en el futuro: su cronómetro queda en
/// 00:00 y no avisa (el cronómetro avisa desde otro hilo cada segundo).
/// Estos archivos no se regeneran durante la fase 6.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NotificationParityTests
{
    [TestInitialize]
    public void Setup() => DialogService.ConfirmHandler = (_, _) => true;

    // ================================================================== utilidades

    private sealed class Golden
    {
        private readonly List<string> _lines = new();
        public Golden(string kind) => _lines.Add(kind);

        public void Section(string title, IEnumerable<string> names)
        {
            _lines.Add($"## {title}");
            _lines.AddRange(names);
        }

        public void AssertMatches(string file, string what) =>
            Fase6Assets.AssertMatchesGolden($"notifications/{file}", Fase6Assets.Lines(_lines), what);
    }

    private const string Ordered = "# Avisos en orden (escenario síncrono, sin base de datos)";
    private const string Unordered = "# Conjunto de avisos (con base de datos o tareas en segundo plano), en orden alfabético";

    /// <summary>Pacientes sin base de datos: catálogo y pacientes de demostración, todo síncrono.</summary>
    private static (MedicalRecordsViewModel Records, ConsultationService Consultations) OpenWithoutDatabase(ConsultationDraftStore? store = null)
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        return (new MedicalRecordsViewModel(null, consultations, consultationDraftStore: store), consultations);
    }

    /// <summary>El cronómetro de la consulta no cambia (empieza en el futuro): no avisa desde su hilo.</summary>
    private static void FreezeClock(ConsultationService consultations)
    {
        consultations.CurrentConsultation!.StartedAt = DateTime.Now.AddHours(1);
    }

    private static List<string> RecordSync(PatientsNotificationRecorder recorder, Action action)
    {
        recorder.Clear();
        action();
        return recorder.Names;
    }

    private static void Completes(Task task, string what) =>
        Assert.IsTrue(task.IsCompleted, $"{what}: sin base de datos debe terminar en el mismo hilo");

    private static PatientRecord Mock(MedicalRecordsViewModel records, int id) => records.Patients.Single(p => p.Id == id);

    [TestMethod]
    public void Recorder_WalksTheCoordinatorAndItsChildViewModels()
    {
        // Hoy: el coordinador y Checkout; cada ViewModel que se separe (Session, Agenda...) se agrega solo
        var (records, _) = OpenWithoutDatabase();
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        Assert.AreSame(records, recorder.Sources[0]);
        CollectionAssert.Contains(recorder.Sources.ToList(), records.Consultation.Checkout);

        recorder.Clear();
        records.Consultation.Checkout.CashReceived = 100m;
        records.Prescription.IsAllergyAlertOpen = true;
        CollectionAssert.IsSubsetOf(new[] { "CashReceived", "IsAllergyAlertOpen" }, recorder.Names, "Avisos de los dos objetos, sin importar el dueño");
    }

    // ================================================================== escenarios síncronos (orden)

    [TestMethod]
    public void SelectPatient_SameId_AndNone()
    {
        var (records, _) = OpenWithoutDatabase();
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        var golden = new Golden(Ordered);
        var patient = Mock(records, 2);

        golden.Section("elegir un paciente", RecordSync(recorder, () => records.SelectPatient(patient)));
        golden.Section("otra instancia con el mismo Id", RecordSync(recorder, () => records.SelectedPatient = new PatientRecord { Id = patient.Id, FullName = patient.FullName }));
        golden.Section("ningún paciente", RecordSync(recorder, () => records.SelectedPatient = null));
        golden.AssertMatches("select-patient.txt", "Avisos al elegir paciente");
    }

    [TestMethod]
    public void StartActiveConsultation()
    {
        var (records, consultations) = OpenWithoutDatabase();
        records.SelectPatient(Mock(records, 2));
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        var golden = new Golden(Ordered);

        golden.Section("iniciar la consulta", RecordSync(recorder, () =>
        {
            records.StartActiveConsultation();
            FreezeClock(consultations);
        }));
        golden.AssertMatches("start-consultation.txt", "Avisos al iniciar la consulta");
    }

    [TestMethod]
    public void DispenseInClinicCharge_SyncsTwice()
    {
        var (records, consultations) = OpenWithoutDatabase();
        records.SelectPatient(Mock(records, 2));
        records.StartActiveConsultation();
        FreezeClock(consultations);
        var paracetamol = records.Catalog.AvailableProducts.Single(p => p.Code == "MED-001");
        records.Prescription.PrescriptionDosage = "1 tableta cada 8 horas";
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        var golden = new Golden(Ordered);

        golden.Section("recetar un medicamento que se surte aquí", RecordSync(recorder, () =>
            Completes(records.Prescription.SelectAndAddPrescriptionProductAsync(paracetamol), "Recetar")));
        Assert.IsTrue(records.SelectedPatient!.ActivePrescription.Single().DispenseInClinic, "Se cargó a la cuenta");
        golden.AssertMatches("dispense-charge.txt", "Avisos al recetar con surtido en el consultorio");
    }

    [TestMethod]
    public void AddServiceToActiveConsultation()
    {
        var (records, consultations) = OpenWithoutDatabase();
        records.SelectPatient(Mock(records, 2));
        records.StartActiveConsultation();
        FreezeClock(consultations);
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        var golden = new Golden(Ordered);

        golden.Section("cargar un servicio con sus insumos", RecordSync(recorder, () => records.Account.AddServiceToActiveConsultation(records.Catalog.Services.First(s => s.DefaultProducts.Count > 0))));
        golden.AssertMatches("add-service.txt", "Avisos al cargar un servicio");
    }

    [TestMethod]
    public void CloseFinishConsultation()
    {
        var (records, consultations) = OpenWithoutDatabase();
        records.SelectPatient(Mock(records, 2));
        records.StartActiveConsultation();
        FreezeClock(consultations);
        records.Consultation.IsFinishConsultationOpen = true;
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        var golden = new Golden(Ordered);

        golden.Section("volver a la consulta", RecordSync(recorder, records.Consultation.CloseFinishConsultation));
        golden.AssertMatches("close-finish.txt", "Avisos al volver de Terminar consulta");
    }

    [TestMethod]
    public void PosStyleClose()
    {
        var (records, consultations) = OpenWithoutDatabase();
        records.SelectPatient(Mock(records, 2));
        records.StartActiveConsultation();
        FreezeClock(consultations);
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        var golden = new Golden(Ordered);

        golden.Section("el servicio cierra la consulta (cobro desde el Punto de Venta)", RecordSync(recorder, consultations.CloseActiveConsultation));
        Assert.IsFalse(records.HasActiveConsultation);
        golden.AssertMatches("pos-close.txt", "Avisos al cerrar la consulta desde el Punto de Venta");
    }

    [TestMethod]
    public void EscapeLayers()
    {
        var layers = new (string Layer, Action<MedicalRecordsViewModel, ConsultationService> Open)[]
        {
            ("info", (vm, _) => { vm.Info.InfoModalMessage = "Aviso"; vm.Info.IsInfoModalVisible = true; }),
            ("alergia", (vm, _) => vm.Prescription.IsAllergyAlertOpen = true),
            ("catálogo", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); Completes(vm.Prescription.OpenPrescriptionCatalogAsync(), "Catálogo"); }),
            ("vista previa", (vm, _) => vm.Preview.IsPrescriptionPdfModalOpen = true),
            ("enviar a", (vm, _) => vm.Handoff.IsSendConsultationSheetOpen = true),
            ("CIE-10", (vm, _) => vm.Consultation.ConsultationDiagnosis = "farin"),
            ("terminar consulta", (vm, c) => { vm.SelectPatient(Mock(vm, 2)); vm.StartActiveConsultation(); FreezeClock(c); vm.Consultation.IsFinishConsultationOpen = true; }),
            ("receta", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.Prescription.OpenPrescriptionModal(); }),
            ("insumos", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.Supplies.OpenSuppliesModal(); }),
            ("servicios", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.ClinicalServices.OpenServicesModal(); }),
            ("cambiar cita", (vm, _) =>
            {
                Completes(vm.OpenAgendaPanelAsync(), "Agenda");
                vm.Agenda.StartMovingAppointment(new PatientAppointment { Id = 9, PatientName = "Ana Ruiz", Date = new DateTime(2026, 10, 20), Time = "10:00" });
            }),
            ("agenda", (vm, _) => Completes(vm.OpenAgendaPanelAsync(), "Agenda")),
            ("búsqueda", (vm, _) => vm.Home.HomeSearchText = "gar"),
            ("ficha", (vm, _) => vm.OpenPatientChart(Mock(vm, 2)))
        };

        var golden = new Golden(Ordered);
        foreach (var (layer, open) in layers)
        {
            var (records, consultations) = OpenWithoutDatabase();
            open(records, consultations);
            using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
            // Desde el paso 2d, el mismo Esc que usa MedicalRecordsView (antes, una copia de HandleEscapeKey);
            // qué capa cierra cada Esc lo revisa EscapeLadderTests
            bool closed = false;
            var names = RecordSync(recorder, () => closed = records.TryCloseTopmost());
            Assert.IsTrue(closed, $"Esc cierra la capa {layer}");
            golden.Section($"Esc: {layer}", names);
        }
        golden.AssertMatches("escape-layers.txt", "Avisos de Esc en cada capa");
    }

    [TestMethod]
    public void PatientForm_OpenAndClose()
    {
        var (records, consultations) = OpenWithoutDatabase();
        records.Navigation.GoToPatientsHome();
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        var golden = new Golden(Ordered);

        golden.Section("nuevo paciente desde el inicio", RecordSync(recorder, () => Completes(records.NewPatientAsync(), "Alta")));
        Assert.IsTrue(records.Navigation.IsFormPage);
        golden.Section("cancelar el alta sin datos", RecordSync(recorder, records.PatientForm.CancelPatientForm));
        Assert.IsTrue(records.Navigation.IsHomePage);

        records.SelectPatient(Mock(records, 2));
        records.StartActiveConsultation();
        FreezeClock(consultations);
        golden.Section("editar el expediente durante la consulta", RecordSync(recorder, records.PatientForm.EditPatient));
        Assert.IsTrue(records.Navigation.IsFormPage);
        golden.Section("cerrar y volver a la consulta", RecordSync(recorder, records.PatientForm.ClosePatientModal));
        Assert.IsTrue(records.Navigation.IsConsultationPage);
        golden.AssertMatches("patient-form.txt", "Avisos al abrir y cerrar el alta");
    }

    // ================================================================== escenarios asíncronos (conjunto)

    private static async Task<List<string>> RecordSetAsync(PatientsNotificationRecorder recorder, MedicalRecordsViewModel records, Func<Task> action)
    {
        // Nada de lo anterior se mezcla: el aviso abierto se cierra (su cierre a los 4 s no cae en este paso)
        records.Info.DismissInfoModal();
        await recorder.WaitForQuietAsync();
        recorder.Clear();
        await action();
        // Lo que la app hace al avisar el inventario de cambios en pacientes y citas. En la app lo corre
        // el despachador de WPF; en la CI de Windows otra prueba deja creada una Application cuyo
        // despachador ya terminó y esos avisos nunca llegan (en Linux corren directo). Se llama aquí para
        // que el conjunto sea el mismo en los dos sistemas.
        await records.LoadPatientsAsync();
        await records.Home.LoadTodayAppointmentsAsync();
        await recorder.WaitForQuietAsync();
        return recorder.Set;
    }

    [TestMethod]
    public async Task Agenda_BookMoveAndCancel()
    {
        using var clinic = new Fase6Clinic();
        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false), drafts: false);
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: false);
        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        var golden = new Golden(Unordered);

        golden.Section("agendar desde la ficha", await RecordSetAsync(recorder, records, async () =>
        {
            await records.Agenda.ScheduleForSelectedPatientAsync();
            await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "09:00"));
            records.Agenda.SetAgendaReason("Control");
            await records.Agenda.SaveAgendaFormAsync();
        }));
        Assert.IsTrue(records.Agenda.AgendaSlots.Any(s => s.HasAppointment));

        golden.Section("cambiar de horario", await RecordSetAsync(recorder, records, async () =>
        {
            records.Agenda.StartMovingAppointment(records.Agenda.AgendaSlots.Single(s => s.HasAppointment).Appointment);
            await records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree && s.TimeText == "16:00"));
        }));
        Assert.AreEqual("16:00", records.Agenda.AgendaSlots.Single(s => s.HasAppointment).TimeText);

        golden.Section("cancelar la cita", await RecordSetAsync(recorder, records, () =>
            records.Agenda.CancelAppointmentAsync(records.Agenda.AgendaSlots.Single(s => s.HasAppointment).Appointment)));
        Assert.IsFalse(records.Agenda.AgendaSlots.Any(s => s.HasAppointment));

        golden.AssertMatches("agenda.txt", "Avisos de la agenda");
        records.Dispose();
    }

    private static void FillNewPatient(MedicalRecordsViewModel records)
    {
        records.PatientForm.FormFirstName = "Sofía";
        records.PatientForm.FormLastName = "Hernández";
        records.PatientForm.FormGender = "Femenino";
        records.PatientForm.FormBirthDateText = "14/03/1992";
        records.PatientForm.SetAllergiesDenied();
    }

    [TestMethod]
    public async Task PatientFormSave_WithAndWithoutDatabase()
    {
        var golden = new Golden(Unordered);

        using (var clinic = new Fase6Clinic())
        {
            var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false), drafts: false);
            using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: false);
            await records.NewPatientAsync();
            FillNewPatient(records);
            golden.Section("guardar el alta con base de datos", await RecordSetAsync(recorder, records, records.PatientForm.SavePatientModalAsync));
            Assert.IsNull(records.PatientForm.FormErrorMessage);
            Assert.IsTrue(records.Navigation.IsChartPage);
            records.Dispose();
        }

        var (offline, _) = OpenWithoutDatabase();
        using (var recorder = new PatientsNotificationRecorder(offline, sameThreadOnly: false))
        {
            Completes(offline.NewPatientAsync(), "Alta");
            FillNewPatient(offline);
            golden.Section("guardar el alta sin base de datos", await RecordSetAsync(recorder, offline, offline.PatientForm.SavePatientModalAsync));
            Assert.IsNull(offline.PatientForm.FormErrorMessage);
        }

        golden.AssertMatches("patient-form-save.txt", "Avisos al guardar el alta");
    }

    [TestMethod]
    public async Task DraftRestore_ConsultationAndPatientForm()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tpvc_fase6_restore_{Guid.NewGuid():N}");
        try
        {
            var golden = new Golden(Unordered);

            // Consulta sin terminar del paciente de demostración 2 (sin base de datos: todo en la carga inicial)
            var store = new ConsultationDraftStore(Path.Combine(folder, "consulta", "consulta-en-curso.json"));
            Assert.IsTrue(store.Save(new ConsultationDraft
            {
                PatientId = 2,
                PatientName = "Rodríguez Silva, María Elena",
                Folio = "CNS-20261007-183015",
                StartedAt = DateTime.Now.AddHours(1),
                ChiefComplaint = "Control mensual",
                TA = "135/85",
                Diagnosis = "E11.9 Diabetes mellitus tipo 2",
                Section = 3,
                Items = { new ConsultationProductItem { ProductId = 1, Code = "MED-001", Name = "Paracetamol 500 mg", Unit = "Cajas", UnitPrice = 45m, Quantity = 1 } },
                Prescriptions = { new PatientPrescriptionItem { MedicineName = "Paracetamol 500 mg", Dosage = "1 cada 8 horas", RecommendedQuantity = 1, DispenseInClinic = true } },
                PreviousPrescriptions = new List<PatientPrescriptionItem> { new() { MedicineName = "Tiras Reactivas para Glucosa Capilar", Dosage = "Monitoreo", RecommendedQuantity = 1 } },
                PatientBefore = new PatientSummarySnapshot(new DateTime(2026, 10, 1), "Control", "TA: 130/80 mmHg", "Notas")
            }));
            var (records, _) = OpenWithoutDatabase(store);
            using (var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: false))
            {
                golden.Section("recuperar la consulta al abrir", await RecordSetAsync(recorder, records, records.LoadInitialDataAsync));
            }
            Assert.IsTrue(records.HasActiveConsultation);
            Assert.IsTrue(records.IsConsultationRecovered);

            // Alta de paciente sin guardar
            var formStore = new ConsultationDraftStore(Path.Combine(folder, "alta", "consulta-en-curso.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(formStore.PatientFormPath)!);
            File.WriteAllText(formStore.PatientFormPath, Fase6Assets.ReadAsset("patient-form-draft.v1_18.json"));
            var (form, _) = OpenWithoutDatabase(formStore);
            using (var recorder = new PatientsNotificationRecorder(form, sameThreadOnly: false))
            {
                golden.Section("recuperar el alta al abrir", await RecordSetAsync(recorder, form, form.LoadInitialDataAsync));
            }
            Assert.IsTrue(form.Navigation.IsFormPage);
            Assert.AreEqual("EXP-2026-777", form.PatientForm.FormRecordNumber);

            golden.AssertMatches("draft-restore.txt", "Avisos al recuperar borradores");
            records.Dispose();
            form.Dispose();
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task Handoff_SendRecoverAcceptRelease()
    {
        using var pc = new Fase6Clinic();
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success);

        var pcConsultations = new ConsultationService(seedDemoPatients: false);
        var pcRecords = await pc.OpenAsync(pcConsultations);
        var tabletConsultations = new ConsultationService(seedDemoPatients: false);
        var tabletRecords = await tablet.OpenAsync(tabletConsultations);
        using var pcRecorder = new PatientsNotificationRecorder(pcRecords, sameThreadOnly: false);
        using var tabletRecorder = new PatientsNotificationRecorder(tabletRecords, sameThreadOnly: false);
        var golden = new Golden(Unordered);

        pcRecords.OpenPatientChart(Fase6Scenarios.ScriptedPatient(pcRecords));
        pcRecords.ChartPrimaryAction();
        FreezeClock(pcConsultations);
        await Fase6Scenarios.FillConsultationAsync(pcRecords, pcConsultations);
        // El primer guardado automático es inmediato (otro hilo): se espera y se guarda a mano
        await Task.Delay(200);
        pcRecords.Autosave.SaveConsultationDraftNow();

        async Task SendAsync()
        {
            await pcRecords.Handoff.OpenSendConsultationSheetAsync();
            pcRecords.Handoff.SelectedHandoffDevice = pcRecords.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-tablet-sala");
            await pcRecords.Handoff.SendConsultationToSelectedDeviceAsync();
            Assert.IsTrue(pcRecords.Handoff.IsConsultationHandedOff, pcRecords.Handoff.HandoffSheetError ?? pcRecords.Info.InfoModalMessage);
        }

        golden.Section("enviar a la tablet (PC)", await RecordSetAsync(pcRecorder, pcRecords, SendAsync));
        golden.Section("recuperarla (PC)", await RecordSetAsync(pcRecorder, pcRecords, pcRecords.Handoff.RecoverHandedOffConsultationAsync));
        Assert.IsTrue(pcRecords.Handoff.IsConsultationEditable);

        await SendAsync();
        golden.Section("revisar y continuar aquí (tablet)", await RecordSetAsync(tabletRecorder, tabletRecords, async () =>
        {
            await tabletRecords.Handoff.PollHandoffsOnceAsync();
            await tabletRecords.Handoff.AcceptIncomingHandoffAsync();
        }));
        Assert.IsTrue(tabletConsultations.HasActiveConsultation, tabletRecords.Info.InfoModalMessage);

        golden.Section("la PC se entera y la suelta (PC)", await RecordSetAsync(pcRecorder, pcRecords, () => pcRecords.Handoff.PollHandoffsOnceAsync()));
        Assert.IsFalse(pcConsultations.HasActiveConsultation, pcRecords.Info.InfoModalMessage);

        golden.AssertMatches("handoff.txt", "Avisos al pasar la consulta a otro equipo");
        tabletRecords.Dispose();
        pcRecords.Dispose();
    }

    [TestMethod]
    public async Task LoadPatients_RefreshWithAFresherRow()
    {
        using var clinic = new Fase6Clinic();
        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false), drafts: false);
        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: false);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        records.OpenPatientChart(patient);
        var golden = new Golden(Unordered);

        golden.Section("recargar con una versión más nueva del expediente abierto", await RecordSetAsync(recorder, records, async () =>
        {
            using (var db = new AppDbContext(clinic.Options))
            {
                db.ApplyingRemoteChanges = true;
                var row = db.Patients.Single(p => p.Id == patient.Id);
                row.Allergies = "Látex";
                row.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(5);
                db.SaveChanges();
            }
            await records.LoadPatientsAsync();
        }));
        Assert.AreEqual("Látex", records.Patients.Single(p => p.Id == patient.Id).Allergies);

        golden.AssertMatches("load-patients-fresher.txt", "Avisos al recargar expedientes");
        records.Dispose();
    }
}
