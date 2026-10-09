using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 21): el apartado Pacientes funciona sin WPF. Se crea PatientsWorkspace directamente (no
/// MedicalRecordsViewModel) con una plataforma de prueba: hilo en línea, confirmaciones con respuestas
/// escritas, una impresora que solo anota y un abridor de enlaces que solo anota. Con eso corre una
/// consulta completa (paciente nuevo, receta que se surte aquí, impresión, cobro con ChargeCommand) y
/// recibe una consulta enviada por otro equipo a través de la nube simulada. Es lo que una app MAUI
/// tendría que poder hacer con su propia plataforma.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PatientsWorkspaceReuseTests
{
    // ================================================================== plataforma de prueba

    /// <summary>Confirmaciones con respuestas escritas de antemano; sin respuesta escrita, "No".</summary>
    private sealed class ScriptedDialogs : IUserDialogs
    {
        private readonly Queue<bool> _answers = new();
        public List<string> Asked { get; } = new();
        public List<string> Errors { get; } = new();

        public void Answer(bool answer) => _answers.Enqueue(answer);

        public bool Confirm(string message, string title)
        {
            Asked.Add(title);
            return _answers.Count > 0 && _answers.Dequeue();
        }

        public Task<bool> ConfirmAsync(string message, string title) => Task.FromResult(Confirm(message, title));

        public void ShowError(string message) => Errors.Add(message);
    }

    private sealed record PrintedPrescription(PatientRecord Patient, List<PatientPrescriptionItem> Items, ClinicProfile Profile, string? GeneralNotes);

    /// <summary>Impresora de receta que solo anota lo que recibe.</summary>
    private sealed class RecordingPrescriptionOutput : IPrescriptionOutput
    {
        public List<PrintedPrescription> Printed { get; } = new();
        public List<PrintedPrescription> Exported { get; } = new();

        public bool PrintPrescription(PatientRecord patient, IEnumerable<PatientPrescriptionItem> items, ClinicProfile profile, string? generalNotes = null)
        {
            Printed.Add(new PrintedPrescription(patient, items.ToList(), profile, generalNotes));
            return true;
        }

        public string? ExportPrescriptionDocument(PatientRecord patient, IEnumerable<PatientPrescriptionItem> items, ClinicProfile profile,
            string? generalNotes = null, string? targetFilePath = null)
        {
            Exported.Add(new PrintedPrescription(patient, items.ToList(), profile, generalNotes));
            return targetFilePath ?? "receta-prueba.pdf";
        }
    }

    /// <summary>Abridor de enlaces que solo anota.</summary>
    private sealed class RecordingUrlLauncher : IUrlLauncher
    {
        public List<string> Opened { get; } = new();
        public void Open(string url) => Opened.Add(url);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 20) await Task.Delay(20);
        Assert.IsTrue(condition(), what);
    }

    /// <summary>El apartado sobre la base del consultorio, como lo abriría otra interfaz (sin WPF).</summary>
    private static async Task<PatientsWorkspace> OpenWorkspaceAsync(Fase6Clinic clinic, ConsultationService consultations, PatientsPlatform platform)
    {
        var workspace = new PatientsWorkspace(platform, clinic.Inventory, consultations,
            consultationDraftStore: clinic.Store(),
            consultationHandoffService: clinic.Handoff)
        {
            // Sin escrituras del temporizador durante la prueba: el borrador se escribe al pedirlo
            AutosaveDelay = TimeSpan.FromHours(1),
            Handoff = { HandoffSyncLockWait = TimeSpan.FromSeconds(5) }
        };
        await workspace.InitialLoad;
        return workspace;
    }

    // ================================================================== consulta completa y consulta recibida

    [TestMethod]
    public async Task Workspace_WithoutWpf_RunsAFullConsultation_AndAcceptsAHandoff()
    {
        using var pc = new Fase6Clinic();
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success, "La PC ve la tablet registrada");

        var dialogs = new ScriptedDialogs();
        var printer = new RecordingPrescriptionOutput();
        var urls = new RecordingUrlLauncher();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var workspace = await OpenWorkspaceAsync(pc, consultations, new PatientsPlatform(new InlineUiThread(), dialogs, urls, printer));
        IPatientsSection section = workspace;
        Assert.IsNotEmpty(section.Patients, "Expedientes de la base sembrada");

        // ------------------------------------------------ paciente nuevo desde el buscador
        workspace.Home.HomeSearchText = "lucía fernández ruiz";
        await workspace.CreatePatientFromSearchAsync();
        Assert.IsTrue(workspace.Navigation.IsFormPage);
        Assert.AreEqual("Lucía", workspace.PatientForm.FormFirstName);
        Assert.AreEqual("Fernández", workspace.PatientForm.FormLastName);
        Assert.AreEqual("Ruiz", workspace.PatientForm.FormMaternalLastName);

        // Salir del alta con algo capturado pregunta (confirmación de la plataforma): "No" la deja abierta
        dialogs.Answer(false);
        workspace.PatientForm.CancelPatientForm();
        CollectionAssert.AreEqual(new[] { "Salir del alta" }, dialogs.Asked);
        Assert.IsTrue(workspace.PatientForm.IsPatientModalOpen, "Contestar que no deja el alta abierta");

        workspace.PatientForm.FormGender = "Femenino";
        workspace.PatientForm.FormAgeText = "34";
        workspace.PatientForm.FormAllergies = "Negadas";
        workspace.PatientForm.FormPhone = "55 1234 5678";

        // ------------------------------------------------ guardar e iniciar la consulta
        await workspace.PatientForm.SavePatientAndStartConsultationAsync();
        Assert.IsNull(workspace.PatientForm.FormErrorMessage, workspace.PatientForm.FormErrorMessage);
        var patient = section.SelectedPatient;
        Assert.IsNotNull(patient);
        Assert.AreEqual("Lucía Fernández Ruiz", patient.FullName);
        Assert.IsGreaterThan(0, patient.Id, "El expediente quedó en la base");
        Assert.IsTrue(section.HasActiveConsultation);
        Assert.AreEqual(patient.Id, section.ActiveConsultation!.PatientId);
        Assert.IsTrue(workspace.Navigation.IsConsultationPage);

        workspace.Consultation.ConsultationChiefComplaint = "Cefalea de dos días";
        workspace.Consultation.ConsultationTA = "120/80";
        workspace.Consultation.ConsultationDiagnosis = "R51 Cefalea";
        workspace.Consultation.ConsultationTreatmentPlan = "Paracetamol y reposo; regresar si persiste";

        // ------------------------------------------------ receta que se surte aquí
        var paracetamol = workspace.Catalog.AvailableProducts.Single(p => p.Code == "MED-001");
        var stockBefore = (await pc.Inventory.GetProductsAsync()).Single(p => p.Id == paracetamol.Id).CurrentStock;
        workspace.Prescription.PrescriptionDosage = "1 tableta cada 8 horas por 3 días";
        await workspace.Prescription.SelectAndAddPrescriptionProductAsync(paracetamol);
        var item = patient.ActivePrescription.Single();
        Assert.IsTrue(item.DispenseInClinic, "Con existencias, \"Se surte aquí\" queda marcado y se carga a la cuenta");
        Assert.IsTrue(consultations.CurrentConsultation!.Items.Any(i => i.ProductId == paracetamol.Id));

        // Impresión y exportación con la salida de la plataforma
        workspace.Preview.OpenPrescriptionPdfPreview();
        Assert.IsTrue(workspace.Preview.IsPrescriptionPdfModalOpen);
        workspace.Preview.PrintOrExportPrescriptionPdf();
        var printed = printer.Printed.Single();
        Assert.AreSame(patient, printed.Patient);
        Assert.AreEqual(item.MedicineName, printed.Items.Single().MedicineName);
        Assert.AreEqual("Paracetamol y reposo; regresar si persiste", printed.GeneralNotes, "La receta lleva el plan del médico");
        StringAssert.Contains(workspace.Info.InfoModalMessage, "impresora");
        workspace.Preview.ExportPrescriptionDocument();
        Assert.HasCount(1, printer.Exported);
        StringAssert.Contains(workspace.Info.InfoModalMessage, "receta-prueba.pdf");
        workspace.Info.DismissInfoModal();

        // ------------------------------------------------ terminar y cobrar con ChargeCommand
        await workspace.Consultation.FinalizeConsultationAsync();
        Assert.IsTrue(workspace.Consultation.IsFinishConsultationOpen);
        var checkout = workspace.Consultation.Checkout;
        Assert.IsTrue(checkout.Cart.Any(c => c.ProductId == paracetamol.Id), "La cuenta lleva lo que se surte aquí");
        checkout.SetExactCash();
        await checkout.ChargeCommand.ExecuteAsync(null);

        Assert.IsFalse(consultations.HasActiveConsultation, workspace.Info.InfoModalMessage);
        Assert.IsFalse(workspace.Consultation.IsFinishConsultationOpen);
        Assert.IsNull(section.SelectedPatient, "Se vuelve al inicio sin paciente");
        Assert.IsTrue(workspace.Navigation.IsHomePage);
        StringAssert.StartsWith(workspace.Info.InfoModalMessage, "Consulta cobrada");
        var notes = await pc.Inventory.GetPatientConsultationsAsync(patient.Id);
        Assert.AreEqual("R51 Cefalea", notes.Single().Diagnosis);
        var stockAfter = (await pc.Inventory.GetProductsAsync()).Single(p => p.Id == paracetamol.Id).CurrentStock;
        Assert.AreEqual(stockBefore - item.RecommendedQuantity, stockAfter, "El cobro descontó lo surtido");

        // ------------------------------------------------ cita de hoy: recordatorio y cancelación
        var appointment = await pc.Inventory.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            PatientRecordNumber = patient.RecordNumber,
            PatientPhone = patient.Phone,
            Date = DateTime.Today,
            Time = "23:30",
            Reason = "Revisión de cefalea"
        });
        await WaitUntilAsync(() => workspace.Home.TodayAppointments.Any(a => a.Id == appointment.Id), "El coordinador se entera de la cita nueva");
        var today = workspace.Home.TodayAppointments.Single(a => a.Id == appointment.Id);

        workspace.Agenda.RemindByWhatsApp(today);
        StringAssert.StartsWith(urls.Opened.Single(), "https://wa.me/525512345678?text=", "El recordatorio se abre con el abridor de la plataforma");

        dialogs.Answer(true);
        await workspace.Agenda.CancelAppointmentAsync(today);
        CollectionAssert.AreEqual(new[] { "Salir del alta", "Cancelar cita" }, dialogs.Asked);
        await WaitUntilAsync(() => workspace.Home.TodayAppointments.All(a => a.Id != appointment.Id), "La cita cancelada sale de las de hoy");

        // ------------------------------------------------ recibir una consulta de otro equipo
        var tabletConsultations = new ConsultationService(seedDemoPatients: false);
        var sender = await OpenWorkspaceAsync(tablet, tabletConsultations, new PatientsPlatform(new InlineUiThread(), new ScriptedDialogs(), new RecordingUrlLauncher(), null));
        sender.SelectPatient(sender.Patients.Single(p => p.RecordNumber == Fase6Scenarios.ScriptedRecord));
        sender.StartActiveConsultation();
        sender.Consultation.ConsultationChiefComplaint = "Dolor de garganta";
        sender.Consultation.ConsultationDiagnosis = "J02.9 Faringitis aguda";
        sender.Consultation.ConsultationSection = 3;
        var folio = tabletConsultations.CurrentConsultation!.Folio;
        // El primer guardado automático es inmediato (otro hilo): se espera y se guarda a mano
        await Task.Delay(200);
        sender.Autosave.SaveConsultationDraftNow();

        await sender.Handoff.OpenSendConsultationSheetAsync();
        var target = sender.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-pc");
        Assert.IsTrue(target.IsAvailable, target.StatusText);
        sender.Handoff.SelectedHandoffDevice = target;
        await sender.Handoff.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(sender.Handoff.IsConsultationHandedOff, sender.Handoff.HandoffSheetError ?? sender.Info.InfoModalMessage);

        await workspace.Handoff.PollHandoffsOnceAsync();
        Assert.IsTrue(workspace.Handoff.CanAcceptIncomingHandoff, workspace.Handoff.IncomingHandoffBlockedReason);
        await workspace.Handoff.AcceptIncomingHandoffAsync();

        Assert.IsTrue(consultations.HasActiveConsultation, workspace.Info.InfoModalMessage);
        Assert.AreEqual(folio, consultations.CurrentConsultation!.Folio, "Mismo folio");
        Assert.AreEqual(Fase6Scenarios.ScriptedRecord, section.SelectedPatient?.RecordNumber);
        Assert.AreEqual("Dolor de garganta", workspace.Consultation.ConsultationChiefComplaint);
        Assert.AreEqual("J02.9 Faringitis aguda", workspace.Consultation.ConsultationDiagnosis);
        Assert.AreEqual(3, workspace.Consultation.ConsultationSection);
        Assert.IsTrue(workspace.Handoff.IsConsultationEditable);
        Assert.IsTrue(workspace.Navigation.IsConsultationPage);

        // El otro equipo se entera en su revisión y la suelta
        await sender.Handoff.PollHandoffsOnceAsync();
        Assert.IsFalse(tabletConsultations.HasActiveConsultation, sender.Info.InfoModalMessage);

        // La consulta recibida se termina y se cobra aquí
        await workspace.Consultation.FinalizeConsultationAsync();
        workspace.Consultation.Checkout.SetExactCash();
        await workspace.Consultation.Checkout.ChargeCommand.ExecuteAsync(null);
        Assert.IsFalse(consultations.HasActiveConsultation, workspace.Info.InfoModalMessage);
        Assert.IsTrue(workspace.Navigation.IsHomePage);

        // Nada pasó por WPF: ni errores mostrados ni más preguntas que las del guion
        Assert.IsEmpty(dialogs.Errors);
        CollectionAssert.AreEqual(new[] { "Salir del alta", "Cancelar cita" }, dialogs.Asked);

        sender.Dispose();
        section.Dispose();
    }
}
