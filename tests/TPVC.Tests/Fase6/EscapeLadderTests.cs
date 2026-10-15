using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 2d): Esc por capas y "escribir para buscar" en el ViewModel (antes en
/// MedicalRecordsView.xaml.cs). Una fila por capa, de la más alta a la más baja: Esc cierra siempre la
/// de más arriba y deja las de abajo como estaban. La prueba de humo de Windows recorre lo mismo con
/// teclas de verdad.
/// </summary>
[TestClass]
public class EscapeLadderTests
{
    private sealed record Layer(string Name, Action<MedicalRecordsViewModel, ConsultationService> Open, Func<MedicalRecordsViewModel, bool> IsOpen);

    /// <summary>Las capas en el orden en que Esc las cierra (la primera es la de más arriba).</summary>
    private static readonly Layer[] Ladder =
    {
        new("info", (vm, _) => { vm.Info.InfoModalMessage = "Aviso"; vm.Info.IsInfoModalVisible = true; }, vm => vm.Info.IsInfoModalVisible),
        new("alergia", (vm, _) => vm.Prescription.IsAllergyAlertOpen = true, vm => vm.Prescription.IsAllergyAlertOpen),
        new("catálogo", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); Completes(vm.Prescription.OpenPrescriptionCatalogAsync(), "Catálogo"); }, vm => vm.CatalogBrowser.IsCatalogBrowserModalOpen),
        new("vista previa", (vm, _) => vm.Preview.IsPrescriptionPdfModalOpen = true, vm => vm.Preview.IsPrescriptionPdfModalOpen),
        new("enviar a", (vm, _) => vm.Handoff.IsSendConsultationSheetOpen = true, vm => vm.Handoff.IsSendConsultationSheetOpen),
        new("CIE-10", (vm, _) => vm.Consultation.ConsultationDiagnosis = "farin", vm => vm.Consultation.IsCie10DropdownOpen),
        new("terminar consulta", (vm, c) => { vm.SelectPatient(Mock(vm, 2)); vm.StartActiveConsultation(); FreezeClock(c); vm.Consultation.IsFinishConsultationOpen = true; }, vm => vm.Consultation.IsFinishConsultationOpen),
        new("receta", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.Prescription.OpenPrescriptionModal(); }, vm => vm.Prescription.IsPrescriptionModalOpen),
        new("insumos", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.Supplies.OpenSuppliesModal(); }, vm => vm.Supplies.IsSuppliesModalOpen),
        // Los insumos abiertos de un servicio (2026-10-14) se cierran antes que la hoja de servicios
        new("insumos del servicio", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.ClinicalServices.OpenServicesModal(); vm.ClinicalServices.SupplyEditorRow = ServiceRow(); },
            vm => vm.ClinicalServices.IsSupplyEditorOpen),
        new("servicios", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.ClinicalServices.OpenServicesModal(); }, vm => vm.ClinicalServices.IsServicesModalOpen),
        new("cambiar cita", (vm, _) => { Completes(vm.OpenAgendaPanelAsync(), "Agenda"); vm.Agenda.StartMovingAppointment(Appointment()); }, vm => vm.Agenda.IsMovingAppointment),
        new("agenda", (vm, _) => Completes(vm.OpenAgendaPanelAsync(), "Agenda"), vm => vm.Agenda.IsAgendaPanelOpen),
        new("búsqueda", (vm, _) => vm.Home.HomeSearchText = "gar", vm => vm.Navigation.IsHomePage && vm.Home.HasHomeQuery),
        new("ficha", (vm, _) => vm.OpenPatientChart(Mock(vm, 2)), vm => vm.Navigation.IsChartPage)
    };

    /// <summary>Las hojas y avisos que impiden escribir para buscar (la agenda y CIE-10 no son hojas).</summary>
    private static readonly (string Name, Action<MedicalRecordsViewModel> Open)[] Sheets =
    {
        ("info", vm => vm.Info.IsInfoModalVisible = true),
        ("alergia", vm => vm.Prescription.IsAllergyAlertOpen = true),
        ("catálogo", vm => vm.CatalogBrowser.IsCatalogBrowserModalOpen = true),
        ("vista previa", vm => vm.Preview.IsPrescriptionPdfModalOpen = true),
        ("terminar consulta", vm => vm.Consultation.IsFinishConsultationOpen = true),
        ("receta", vm => vm.Prescription.IsPrescriptionModalOpen = true),
        ("insumos", vm => vm.Supplies.IsSuppliesModalOpen = true),
        ("servicios", vm => vm.ClinicalServices.IsServicesModalOpen = true),
        ("enviar a", vm => vm.Handoff.IsSendConsultationSheetOpen = true)
    };

    private static (MedicalRecordsViewModel Records, ConsultationService Consultations) Open()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        return (new MedicalRecordsViewModel(null, consultations), consultations);
    }

    private static PatientRecord Mock(MedicalRecordsViewModel records, int id) => records.Patients.Single(p => p.Id == id);

    private static PatientServiceItem ServiceRow() => new() { ServiceName = "Curación Menor y Asepsia", Quantity = 1 };

    private static PatientAppointment Appointment() =>
        new() { Id = 9, PatientName = "Ana Ruiz", Date = new DateTime(2026, 10, 20), Time = "10:00" };

    /// <summary>El cronómetro de la consulta no cambia (empieza en el futuro): nada llega desde su hilo.</summary>
    private static void FreezeClock(ConsultationService consultations) =>
        consultations.CurrentConsultation!.StartedAt = DateTime.Now.AddHours(1);

    private static void Completes(Task task, string what) =>
        Assert.IsTrue(task.IsCompleted, $"{what}: sin base de datos debe terminar en el mismo hilo");

    private static string OpenLayers(MedicalRecordsViewModel vm) =>
        string.Join(", ", Ladder.Where(l => l.IsOpen(vm)).Select(l => l.Name));

    // ================================================================== Esc

    [TestMethod]
    public void EachLayer_IsClosedByEsc_AndNothingElseChanges()
    {
        foreach (var layer in Ladder)
        {
            var (records, consultations) = Open();
            layer.Open(records, consultations);
            Assert.IsTrue(layer.IsOpen(records), $"{layer.Name}: se abrió");
            var before = Ladder.Where(l => l != layer).ToDictionary(l => l.Name, l => l.IsOpen(records));

            Assert.IsTrue(records.TryCloseTopmost(), $"{layer.Name}: Esc la atiende");

            Assert.IsFalse(layer.IsOpen(records), $"{layer.Name}: Esc la cierra");
            foreach (var other in Ladder.Where(l => l != layer))
            {
                Assert.AreEqual(before[other.Name], other.IsOpen(records), $"{layer.Name}: Esc no cambia «{other.Name}»");
            }
            records.Dispose();
        }
    }

    [TestMethod]
    public void StackedLayers_CloseFromTheTopDown()
    {
        var (records, _) = Open();

        // Todo abierto a la vez sobre la ficha, de abajo hacia arriba
        records.OpenPatientChart(Mock(records, 2));
        Completes(records.OpenAgendaPanelAsync(), "Agenda");
        records.Agenda.StartMovingAppointment(Appointment());
        records.ClinicalServices.IsServicesModalOpen = true;
        records.ClinicalServices.SupplyEditorRow = ServiceRow();
        records.Supplies.IsSuppliesModalOpen = true;
        records.Prescription.IsPrescriptionModalOpen = true;
        records.Consultation.IsFinishConsultationOpen = true;
        records.Consultation.IsCie10DropdownOpen = true;
        records.Handoff.IsSendConsultationSheetOpen = true;
        records.Preview.IsPrescriptionPdfModalOpen = true;
        records.CatalogBrowser.IsCatalogBrowserModalOpen = true;
        records.Prescription.IsAllergyAlertOpen = true;
        records.Info.InfoModalMessage = "Aviso";
        records.Info.IsInfoModalVisible = true;

        var expectedOrder = Ladder.Where(l => l.Name != "búsqueda").Select(l => l.Name).ToList();
        Assert.AreEqual(string.Join(", ", expectedOrder), OpenLayers(records), "Todas las capas (salvo la búsqueda) abiertas");

        var closed = new List<string>();
        for (int i = 0; i < expectedOrder.Count; i++)
        {
            var openBefore = Ladder.Where(l => l.IsOpen(records)).ToList();
            Assert.IsTrue(records.TryCloseTopmost(), $"Esc {i + 1}: hay algo que cerrar ({OpenLayers(records)})");
            var openAfter = Ladder.Where(l => l.IsOpen(records)).ToList();
            var gone = openBefore.Except(openAfter).Select(l => l.Name).ToList();
            Assert.AreEqual(1, gone.Count, $"Esc {i + 1} cierra una sola capa (cerró: {string.Join(", ", gone)})");
            Assert.AreEqual(openBefore[0].Name, gone[0], $"Esc {i + 1} cierra la de más arriba");
            closed.Add(gone[0]);
        }

        CollectionAssert.AreEqual(expectedOrder, closed, "Orden en que Esc cierra las capas");
        Assert.IsTrue(records.Navigation.IsHomePage, "La última Esc vuelve de la ficha al inicio");
        Assert.IsFalse(records.TryCloseTopmost(), "En el inicio sin búsqueda Esc no tiene nada que cerrar");
        records.Dispose();
    }

    [TestMethod]
    public void OnHome_EscClosesTheAgendaBeforeClearingTheSearch()
    {
        var (records, _) = Open();
        records.Navigation.GoToPatientsHome();
        records.Home.HomeSearchText = "gar";
        Completes(records.OpenAgendaPanelAsync(), "Agenda");

        Assert.IsTrue(records.TryCloseTopmost());
        Assert.IsFalse(records.Agenda.IsAgendaPanelOpen, "Primero la agenda");
        Assert.AreEqual("gar", records.Home.HomeSearchText);

        Assert.IsTrue(records.TryCloseTopmost());
        Assert.AreEqual(string.Empty, records.Home.HomeSearchText, "Luego la búsqueda");
        Assert.IsTrue(records.Navigation.IsHomePage);

        Assert.IsFalse(records.TryCloseTopmost());
        records.Dispose();
    }

    [TestMethod]
    public void NothingOpen_EscIsNotHandled_AndNothingIsNotified()
    {
        var (records, _) = Open();
        records.Navigation.GoToPatientsHome();
        Assert.IsTrue(records.Navigation.IsHomePage);

        using var recorder = new PatientsNotificationRecorder(records, sameThreadOnly: true);
        Assert.IsFalse(records.TryCloseTopmost(), "Sin nada abierto la tecla sigue su camino");
        Assert.IsEmpty(recorder.Names, "Esc sin nada que cerrar no avisa nada: " + string.Join(", ", recorder.Names));
        records.Dispose();
    }

    [TestMethod]
    public void OnFormOrConsultation_EscHasNothingToClose()
    {
        var (form, _) = Open();
        Completes(form.NewPatientAsync(), "Alta");
        Assert.IsTrue(form.Navigation.IsFormPage);
        Assert.IsFalse(form.TryCloseTopmost(), "El alta se cierra con sus botones, no con Esc");
        Assert.IsTrue(form.Navigation.IsFormPage);
        form.Dispose();

        var (consultation, consultations) = Open();
        consultation.SelectPatient(Mock(consultation, 2));
        consultation.StartActiveConsultation();
        FreezeClock(consultations);
        Assert.IsTrue(consultation.Navigation.IsConsultationPage);
        Assert.IsFalse(consultation.TryCloseTopmost(), "La consulta no se deja con Esc");
        Assert.IsTrue(consultation.Navigation.IsConsultationPage);
        consultation.Dispose();
    }

    // ================================================================== dentro de la receta (2026-10-14)

    /// <summary>La receta de una consulta en curso con un medicamento ya agregado (para corregir su indicación).</summary>
    private static (MedicalRecordsViewModel Records, PatientPrescriptionItem Row) PrescriptionWithARow()
    {
        var (records, consultations) = Open();
        records.SelectPatient(Mock(records, 2));
        records.StartActiveConsultation();
        FreezeClock(consultations);
        records.Prescription.OpenPrescriptionModal();
        records.Prescription.PrescriptionSearchText = "parac";
        Assert.IsTrue(records.Prescription.ConfirmPrescriptionSearch());
        records.Prescription.PrescriptionDosage = "1 tableta cada 8 horas";
        Completes(records.Prescription.AddPrescriptionAsync(), "Agregar");
        records.Templates.PosologyTemplates = new System.Collections.ObjectModel.ObservableCollection<PrescriptionTemplate>
        {
            new() { Id = 1, Title = "Cada 8", Text = "1 tableta cada 8 horas por 5 días" },
            new() { Id = 2, Title = "Cada 12", Text = "1 tableta cada 12 horas por 7 días" }
        };
        return (records, records.SelectedPatient!.ActivePrescription.Single());
    }

    [TestMethod]
    public void InsideThePrescription_EscClosesTheListsAndTheCorrectionFirst_ThenTheSheet()
    {
        var (records, row) = PrescriptionWithARow();
        var rx = records.Prescription;

        // Todo abierto a la vez: sugerencias de medicamentos, la corrección de un renglón con su lista y la lista de la captura
        rx.PrescriptionSearchText = "ibu";
        rx.SheetIndication.Start(row);
        Assert.IsTrue(rx.SheetIndication.Picker.Open());
        Assert.IsTrue(rx.CaptureIndications.Open());
        Assert.IsTrue(rx.IsPrescriptionDropdownOpen && rx.SheetIndication.IsEditing);

        string State() => string.Join(", ", new[]
        {
            rx.CaptureIndications.IsOpen ? "frecuentes" : null,
            rx.SheetIndication.Picker.IsOpen ? "frecuentes del renglón" : null,
            rx.SheetIndication.IsEditing ? "cambiar indicación" : null,
            rx.IsPrescriptionDropdownOpen ? "sugerencias" : null,
            rx.IsPrescriptionModalOpen ? "receta" : null
        }.Where(n => n != null));

        var expected = new[]
        {
            "frecuentes del renglón, cambiar indicación, sugerencias, receta",
            "cambiar indicación, sugerencias, receta",
            "sugerencias, receta",
            "receta",
            string.Empty
        };
        foreach (var after in expected)
        {
            Assert.IsTrue(records.TryCloseTopmost(), $"Esc se atiende ({State()})");
            Assert.AreEqual(after, State(), "Esc cierra solo lo de más arriba");
        }
        Assert.AreEqual("ibu", rx.PrescriptionSearchText, "Cerrar las sugerencias no borra lo escrito");
        Assert.AreEqual("1 tableta cada 8 horas", row.Dosage, "Esc en la corrección deja la indicación que tenía");
        Assert.IsTrue(records.HasActiveConsultation);
        records.Dispose();
    }

    [TestMethod]
    public void ACorrectionWhoseRowIsNoLongerShown_DoesNotSwallowEsc()
    {
        var (records, row) = PrescriptionWithARow();
        var rx = records.Prescription;
        rx.SheetIndication.Start(row);
        Assert.IsTrue(rx.SheetIndication.IsEditing);

        // El renglón ya no está en la receta (p. ej. se cambió la lista con una recarga): su corrección no se ve
        records.SelectedPatient!.ActivePrescription = new List<PatientPrescriptionItem>();
        Assert.IsTrue(records.TryCloseTopmost());
        Assert.IsFalse(rx.IsPrescriptionModalOpen, "La primera Esc cierra la hoja, que es lo que se ve");
        Assert.IsFalse(rx.SheetIndication.IsEditing, "Cerrar la hoja también cierra la corrección");
        records.Dispose();
    }

    [TestMethod]
    public void OnTheConsultation_EscClosesTheIndicationListThenTheCorrection_AndNeverLeavesTheConsultation()
    {
        var (records, row) = PrescriptionWithARow();
        var plan = records.Prescription.PlanIndication;
        records.Prescription.ClosePrescriptionModal();
        Assert.IsTrue(records.Navigation.IsConsultationPage);

        plan.Start(row);
        Assert.IsTrue(plan.Picker.Open());
        Completes(records.OpenAgendaPanelAsync(), "Agenda");

        Assert.IsTrue(records.TryCloseTopmost());
        Assert.IsFalse(records.Agenda.IsAgendaPanelOpen, "Primero la agenda, que está encima");
        Assert.IsTrue(plan.Picker.IsOpen);
        Assert.IsTrue(records.TryCloseTopmost());
        Assert.IsFalse(plan.Picker.IsOpen, "Luego la lista de frecuentes");
        Assert.IsTrue(plan.IsEditing);
        Assert.IsTrue(records.TryCloseTopmost());
        Assert.IsFalse(plan.IsEditing, "Luego la corrección");
        Assert.IsFalse(records.TryCloseTopmost(), "La consulta no se deja con Esc");
        Assert.IsTrue(records.Navigation.IsConsultationPage);

        // Abrir la hoja de receta cancela una corrección que quedó abierta en la consulta (la hoja la taparía)
        plan.Start(row);
        records.Prescription.OpenPrescriptionModal();
        Assert.IsFalse(plan.IsEditing);
        records.Dispose();
    }

    // ================================================================== escribir para buscar

    [TestMethod]
    public void CanTypeToSearch_OnHomeOrChart_WithNothingOnTop()
    {
        var (records, consultations) = Open();
        records.Navigation.GoToPatientsHome();
        Assert.IsTrue(records.CanTypeToSearch, "Inicio");
        Assert.IsFalse(records.IsAnySheetOpen);

        records.OpenPatientChart(Mock(records, 2));
        Assert.IsTrue(records.CanTypeToSearch, "Ficha");

        Completes(records.OpenAgendaPanelAsync(), "Agenda");
        Assert.IsFalse(records.CanTypeToSearch, "Con la agenda abierta");
        Assert.IsFalse(records.IsAnySheetOpen, "La agenda no es una hoja");
        records.Agenda.CloseAgendaPanel();

        records.Consultation.IsCie10DropdownOpen = true;
        Assert.IsFalse(records.IsAnySheetOpen, "Las sugerencias CIE-10 no son una hoja");
        Assert.IsTrue(records.CanTypeToSearch);
        records.Consultation.IsCie10DropdownOpen = false;

        records.SelectPatient(Mock(records, 2));
        records.StartActiveConsultation();
        FreezeClock(consultations);
        Assert.IsTrue(records.Navigation.IsConsultationPage);
        Assert.IsFalse(records.CanTypeToSearch, "En la consulta");
        records.Dispose();

        var (form, _) = Open();
        Completes(form.NewPatientAsync(), "Alta");
        Assert.IsFalse(form.CanTypeToSearch, "En el alta");
        form.Dispose();
    }

    [TestMethod]
    public void EachSheet_BlocksTypingToSearch()
    {
        foreach (var (name, open) in Sheets)
        {
            foreach (var onChart in new[] { false, true })
            {
                var (records, _) = Open();
                if (onChart) records.OpenPatientChart(Mock(records, 2));
                else records.Navigation.GoToPatientsHome();
                Assert.IsTrue(records.CanTypeToSearch);

                open(records);
                Assert.IsTrue(records.IsAnySheetOpen, $"{name} es una hoja");
                Assert.IsFalse(records.CanTypeToSearch, $"{name} ({(onChart ? "ficha" : "inicio")}) impide escribir para buscar");
                records.Dispose();
            }
        }
    }

    [TestMethod]
    public void AppendTypedSearchText_AddsToTheSearch()
    {
        var (records, _) = Open();
        records.Navigation.GoToPatientsHome();
        records.AppendTypedSearchText("g");
        records.AppendTypedSearchText("ar");
        Assert.AreEqual("gar", records.Home.HomeSearchText);

        // Desde la ficha, lo tecleado abre la búsqueda en el inicio (como escribir en el buscador)
        records.Home.HomeSearchText = string.Empty;
        records.OpenPatientChart(Mock(records, 2));
        records.AppendTypedSearchText("l");
        Assert.AreEqual("l", records.Home.HomeSearchText);
        Assert.IsTrue(records.Navigation.IsHomePage);
        records.Dispose();
    }
}
