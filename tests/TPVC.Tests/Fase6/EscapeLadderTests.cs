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
        new("info", (vm, _) => { vm.InfoModalMessage = "Aviso"; vm.IsInfoModalVisible = true; }, vm => vm.IsInfoModalVisible),
        new("alergia", (vm, _) => vm.IsAllergyAlertOpen = true, vm => vm.IsAllergyAlertOpen),
        new("catálogo", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); Completes(vm.OpenPrescriptionCatalogAsync(), "Catálogo"); }, vm => vm.IsCatalogBrowserModalOpen),
        new("vista previa", (vm, _) => vm.IsPrescriptionPdfModalOpen = true, vm => vm.IsPrescriptionPdfModalOpen),
        new("enviar a", (vm, _) => vm.IsSendConsultationSheetOpen = true, vm => vm.IsSendConsultationSheetOpen),
        new("CIE-10", (vm, _) => vm.ConsultationDiagnosis = "farin", vm => vm.IsCie10DropdownOpen),
        new("terminar consulta", (vm, c) => { vm.SelectPatient(Mock(vm, 2)); vm.StartActiveConsultation(); FreezeClock(c); vm.IsFinishConsultationOpen = true; }, vm => vm.IsFinishConsultationOpen),
        new("receta", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.OpenPrescriptionModal(); }, vm => vm.IsPrescriptionModalOpen),
        new("insumos", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.OpenSuppliesModal(); }, vm => vm.IsSuppliesModalOpen),
        new("servicios", (vm, _) => { vm.SelectPatient(Mock(vm, 2)); vm.OpenServicesModal(); }, vm => vm.IsServicesModalOpen),
        new("cambiar cita", (vm, _) => { Completes(vm.OpenAgendaPanelAsync(), "Agenda"); vm.StartMovingAppointment(Appointment()); }, vm => vm.IsMovingAppointment),
        new("agenda", (vm, _) => Completes(vm.OpenAgendaPanelAsync(), "Agenda"), vm => vm.IsAgendaPanelOpen),
        new("búsqueda", (vm, _) => vm.HomeSearchText = "gar", vm => vm.IsHomePage && vm.HasHomeQuery),
        new("ficha", (vm, _) => vm.OpenPatientChart(Mock(vm, 2)), vm => vm.IsChartPage)
    };

    /// <summary>Las hojas y avisos que impiden escribir para buscar (la agenda y CIE-10 no son hojas).</summary>
    private static readonly (string Name, Action<MedicalRecordsViewModel> Open)[] Sheets =
    {
        ("info", vm => vm.IsInfoModalVisible = true),
        ("alergia", vm => vm.IsAllergyAlertOpen = true),
        ("catálogo", vm => vm.IsCatalogBrowserModalOpen = true),
        ("vista previa", vm => vm.IsPrescriptionPdfModalOpen = true),
        ("terminar consulta", vm => vm.IsFinishConsultationOpen = true),
        ("receta", vm => vm.IsPrescriptionModalOpen = true),
        ("insumos", vm => vm.IsSuppliesModalOpen = true),
        ("servicios", vm => vm.IsServicesModalOpen = true),
        ("enviar a", vm => vm.IsSendConsultationSheetOpen = true)
    };

    private static (MedicalRecordsViewModel Records, ConsultationService Consultations) Open()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        return (new MedicalRecordsViewModel(null, consultations), consultations);
    }

    private static PatientRecord Mock(MedicalRecordsViewModel records, int id) => records.Patients.Single(p => p.Id == id);

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
        records.StartMovingAppointment(Appointment());
        records.IsServicesModalOpen = true;
        records.IsSuppliesModalOpen = true;
        records.IsPrescriptionModalOpen = true;
        records.IsFinishConsultationOpen = true;
        records.IsCie10DropdownOpen = true;
        records.IsSendConsultationSheetOpen = true;
        records.IsPrescriptionPdfModalOpen = true;
        records.IsCatalogBrowserModalOpen = true;
        records.IsAllergyAlertOpen = true;
        records.InfoModalMessage = "Aviso";
        records.IsInfoModalVisible = true;

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
        Assert.IsTrue(records.IsHomePage, "La última Esc vuelve de la ficha al inicio");
        Assert.IsFalse(records.TryCloseTopmost(), "En el inicio sin búsqueda Esc no tiene nada que cerrar");
        records.Dispose();
    }

    [TestMethod]
    public void OnHome_EscClosesTheAgendaBeforeClearingTheSearch()
    {
        var (records, _) = Open();
        records.GoToPatientsHome();
        records.HomeSearchText = "gar";
        Completes(records.OpenAgendaPanelAsync(), "Agenda");

        Assert.IsTrue(records.TryCloseTopmost());
        Assert.IsFalse(records.IsAgendaPanelOpen, "Primero la agenda");
        Assert.AreEqual("gar", records.HomeSearchText);

        Assert.IsTrue(records.TryCloseTopmost());
        Assert.AreEqual(string.Empty, records.HomeSearchText, "Luego la búsqueda");
        Assert.IsTrue(records.IsHomePage);

        Assert.IsFalse(records.TryCloseTopmost());
        records.Dispose();
    }

    [TestMethod]
    public void NothingOpen_EscIsNotHandled_AndNothingIsNotified()
    {
        var (records, _) = Open();
        records.GoToPatientsHome();
        Assert.IsTrue(records.IsHomePage);

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
        Assert.IsTrue(form.IsFormPage);
        Assert.IsFalse(form.TryCloseTopmost(), "El alta se cierra con sus botones, no con Esc");
        Assert.IsTrue(form.IsFormPage);
        form.Dispose();

        var (consultation, consultations) = Open();
        consultation.SelectPatient(Mock(consultation, 2));
        consultation.StartActiveConsultation();
        FreezeClock(consultations);
        Assert.IsTrue(consultation.IsConsultationPage);
        Assert.IsFalse(consultation.TryCloseTopmost(), "La consulta no se deja con Esc");
        Assert.IsTrue(consultation.IsConsultationPage);
        consultation.Dispose();
    }

    // ================================================================== escribir para buscar

    [TestMethod]
    public void CanTypeToSearch_OnHomeOrChart_WithNothingOnTop()
    {
        var (records, consultations) = Open();
        records.GoToPatientsHome();
        Assert.IsTrue(records.CanTypeToSearch, "Inicio");
        Assert.IsFalse(records.IsAnySheetOpen);

        records.OpenPatientChart(Mock(records, 2));
        Assert.IsTrue(records.CanTypeToSearch, "Ficha");

        Completes(records.OpenAgendaPanelAsync(), "Agenda");
        Assert.IsFalse(records.CanTypeToSearch, "Con la agenda abierta");
        Assert.IsFalse(records.IsAnySheetOpen, "La agenda no es una hoja");
        records.CloseAgendaPanel();

        records.IsCie10DropdownOpen = true;
        Assert.IsFalse(records.IsAnySheetOpen, "Las sugerencias CIE-10 no son una hoja");
        Assert.IsTrue(records.CanTypeToSearch);
        records.IsCie10DropdownOpen = false;

        records.SelectPatient(Mock(records, 2));
        records.StartActiveConsultation();
        FreezeClock(consultations);
        Assert.IsTrue(records.IsConsultationPage);
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
                else records.GoToPatientsHome();
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
        records.GoToPatientsHome();
        records.AppendTypedSearchText("g");
        records.AppendTypedSearchText("ar");
        Assert.AreEqual("gar", records.HomeSearchText);

        // Desde la ficha, lo tecleado abre la búsqueda en el inicio (como escribir en el buscador)
        records.HomeSearchText = string.Empty;
        records.OpenPatientChart(Mock(records, 2));
        records.AppendTypedSearchText("l");
        Assert.AreEqual("l", records.HomeSearchText);
        Assert.IsTrue(records.IsHomePage);
        records.Dispose();
    }
}
