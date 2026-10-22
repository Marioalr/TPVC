using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Tests.Fase6;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Pedidos del consultorio del 2026-10-14 en la hoja de receta (sin WPF): elegir el medicamento ya no lo agrega (la
/// indicación se escribe después y se agrega con Enter o "Agregar"), las indicaciones frecuentes dentro del campo,
/// "Cambiar indicación" en un renglón (el mismo objeto: RowId y ExtensionData se quedan) y las sugerencias con
/// existencias primero. Lo de siempre sigue igual: alergias, "Se surte aquí", lo escrito a mano, la dosis pediátrica y
/// la guarda de solo lectura.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PrescriptionCaptureTests
{
    [TestInitialize]
    public void Setup() => DialogService.ConfirmHandler = (_, _) => true;

    /// <summary>Sin base de datos: el catálogo y los pacientes de demostración, todo en el mismo hilo.</summary>
    private static (MedicalRecordsViewModel Records, ConsultationService Consultations) Open()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        return (new MedicalRecordsViewModel(null, consultations), consultations);
    }

    /// <summary>La receta abierta en una consulta en curso del paciente 2 (sin alergias que choquen).</summary>
    private static (MedicalRecordsViewModel Records, ConsultationService Consultations, PatientRecord Patient) OpenConsultation()
    {
        var (records, consultations) = Open();
        var patient = records.Patients.Single(p => p.Id == 2);
        records.SelectPatient(patient);
        records.StartActiveConsultation();
        consultations.CurrentConsultation!.StartedAt = DateTime.Now.AddHours(1);
        records.Prescription.OpenPrescriptionModal();
        Assert.IsEmpty(patient.ActivePrescription, "La receta de la consulta empieza vacía");
        return (records, consultations, patient);
    }

    private static Product Medicine(MedicalRecordsViewModel records, string code) => records.Catalog.AvailableProducts.Single(p => p.Code == code);

    private static void UsePosologies(MedicalRecordsViewModel records, params string[] texts) =>
        records.Templates.PosologyTemplates = new ObservableCollection<PrescriptionTemplate>(
            texts.Select((t, i) => new PrescriptionTemplate { Id = i + 1, Kind = PrescriptionTemplateKinds.Posology, Title = $"P{i + 1}", Text = t }));

    // ================================================================== elegir no agrega

    [TestMethod]
    public async Task ChoosingFromTheSuggestions_DoesNotAdd_TheIndicationIsWrittenAfterwards_AndEnterAdds()
    {
        var (records, consultations, patient) = OpenConsultation();
        var rx = records.Prescription;
        var amoxicillin = Medicine(records, "MED-002");
        int focusRequests = 0;
        rx.FocusSearchRequested += () => focusRequests++;

        rx.PrescriptionSearchText = "amox";
        Assert.IsTrue(rx.IsPrescriptionDropdownOpen);
        Assert.AreEqual(amoxicillin, rx.SelectedPrescriptionProduct);

        // Enter en el buscador: lo elige, no lo agrega
        Assert.IsTrue(rx.ConfirmPrescriptionSearch(), "El cursor pasa a la indicación");
        Assert.IsEmpty(patient.ActivePrescription, "Elegir no agrega");
        Assert.IsTrue(rx.IsMedicineChosen);
        Assert.AreSame(amoxicillin, rx.ChosenPrescriptionProduct);
        Assert.AreEqual(PrescriptionNaming.MedicineLine(amoxicillin), rx.PrescriptionSearchText, "El buscador muestra el elegido en el orden normado");
        Assert.IsFalse(rx.IsPrescriptionDropdownOpen, "Las sugerencias se cierran");
        StringAssert.StartsWith(rx.ChosenMedicineCaption, "Elegido del inventario · Existencias:");

        // La indicación después; Enter en Indicación (o Agregar) lo agrega con ella
        rx.PrescriptionDosage = "1 cápsula cada 8 horas por 7 días";
        rx.PrescriptionQuantity = 2;
        Assert.IsTrue(rx.AddPrescriptionCommand.CanExecute(null));
        await rx.AddPrescriptionAsync();

        var row = patient.ActivePrescription.Single();
        Assert.AreEqual(PrescriptionNaming.MedicineLine(amoxicillin), row.MedicineName);
        Assert.AreEqual("1 cápsula cada 8 horas por 7 días", row.Dosage);
        Assert.AreEqual(2, row.RecommendedQuantity);
        Assert.AreEqual(amoxicillin.Presentation, row.Presentation);
        Assert.IsTrue(row.DispenseInClinic, "Se surte aquí, marcado por haber existencias");
        Assert.AreEqual(2, consultations.CurrentConsultation!.Items.Single(i => i.ProductId == amoxicillin.Id).Quantity, "Cargado a la cuenta");

        // Todo queda listo para el siguiente
        Assert.AreEqual(string.Empty, rx.PrescriptionSearchText);
        Assert.IsFalse(rx.IsMedicineChosen);
        Assert.IsNull(rx.ChosenPrescriptionProduct);
        Assert.AreEqual(string.Empty, rx.PrescriptionDosage);
        Assert.AreEqual(1, rx.PrescriptionQuantity, "La cantidad vuelve a 1");
        Assert.AreEqual(1, focusRequests, "El cursor vuelve al buscador");
        Assert.IsFalse(rx.AddPrescriptionCommand.CanExecute(null), "Sin medicamento, Agregar no hace nada");
        records.Dispose();
    }

    [TestMethod]
    public async Task ClickingASuggestion_OrPickingInTheCatalog_OnlyChooses()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        var ibuprofen = Medicine(records, "MED-003");

        rx.PrescriptionSearchText = "ibu";
        rx.ChoosePrescriptionProduct(ibuprofen);
        Assert.IsEmpty(patient.ActivePrescription);
        Assert.AreSame(ibuprofen, rx.ChosenPrescriptionProduct);

        // Escribir otra vez en el buscador deshace la elección y vuelve a buscar
        rx.PrescriptionSearchText = "ome";
        Assert.IsFalse(rx.IsMedicineChosen);
        Assert.IsNull(rx.ChosenPrescriptionProduct);
        Assert.IsTrue(rx.IsPrescriptionDropdownOpen);

        // El catálogo también solo elige
        await rx.OpenPrescriptionCatalogAsync();
        var omeprazole = Medicine(records, "MED-004");
        await records.CatalogBrowser.AddProductFromCatalogBrowserAsync(omeprazole);
        Assert.IsFalse(records.CatalogBrowser.IsCatalogBrowserModalOpen);
        Assert.IsEmpty(patient.ActivePrescription);
        Assert.AreSame(omeprazole, rx.ChosenPrescriptionProduct);

        // La X del buscador deshace la elección
        rx.ClearChosenMedicine();
        Assert.IsFalse(rx.IsMedicineChosen);
        Assert.AreEqual(string.Empty, rx.PrescriptionSearchText);
        records.Dispose();
    }

    [TestMethod]
    public async Task EnterWithTextThatMatchesNothing_KeepsItAsAFreeTextMedicine()
    {
        var (records, consultations, patient) = OpenConsultation();
        var rx = records.Prescription;

        rx.PrescriptionSearchText = "Té de manzanilla";
        Assert.IsFalse(rx.IsPrescriptionDropdownOpen, "Nada del catálogo coincide");
        Assert.IsTrue(rx.ConfirmPrescriptionSearch(), "Enter lo toma tal cual y pasa a la indicación");
        Assert.IsTrue(rx.IsMedicineChosen);
        Assert.IsNull(rx.ChosenPrescriptionProduct);
        Assert.AreEqual("Té de manzanilla", rx.PrescriptionSearchText);
        Assert.AreEqual("Escrito a mano (no se eligió del inventario)", rx.ChosenMedicineCaption);

        rx.PrescriptionDosage = "1 taza cada 12 horas";
        await rx.AddPrescriptionAsync();
        var row = patient.ActivePrescription.Single();
        Assert.AreEqual("Té de manzanilla", row.MedicineName);
        Assert.AreEqual("1 taza cada 12 horas", row.Dosage);
        Assert.IsFalse(row.DispenseInClinic, "No está en el inventario: no se surte aquí");
        Assert.IsEmpty(consultations.CurrentConsultation!.Items);

        // Con la lista cerrada con Esc, Enter también toma lo escrito
        rx.PrescriptionSearchText = "parac";
        Assert.IsTrue(rx.IsPrescriptionDropdownOpen);
        rx.ClosePrescriptionSuggestions();
        Assert.IsTrue(rx.ConfirmPrescriptionSearch());
        Assert.IsNull(rx.ChosenPrescriptionProduct);
        Assert.AreEqual("parac", rx.PrescriptionSearchText);
        records.Dispose();
    }

    [TestMethod]
    public async Task EmptySearch_EnterAndAgregarDoNothing_AndDownOpensTheListWithStockFirst()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;

        Assert.IsFalse(rx.ConfirmPrescriptionSearch(), "Sin nada escrito, Enter no elige");
        Assert.IsFalse(rx.AddPrescriptionCommand.CanExecute(null));
        await rx.AddPrescriptionAsync();
        Assert.IsEmpty(patient.ActivePrescription, "Antes Enter con el buscador vacío agregaba el primero del catálogo");

        // ↓ con el buscador vacío abre la lista; ↑ ↓ la recorren y vuelven a empezar
        Assert.IsFalse(rx.IsPrescriptionDropdownOpen);
        rx.MovePrescriptionSuggestion(1);
        Assert.IsTrue(rx.IsPrescriptionDropdownOpen);
        var list = rx.PrescriptionProductSuggestions;
        Assert.IsTrue(list.All(CatalogStore.IsProductMedication));
        Assert.AreEqual(list[0], rx.SelectedPrescriptionProduct, "Al abrir, la primera");
        rx.MovePrescriptionSuggestion(1);
        Assert.AreEqual(list[1], rx.SelectedPrescriptionProduct);
        rx.MovePrescriptionSuggestion(-1);
        rx.MovePrescriptionSuggestion(-1);
        Assert.AreEqual(list[^1], rx.SelectedPrescriptionProduct, "De la primera, ↑ pasa a la última");
        Assert.IsTrue(rx.ConfirmPrescriptionSearch());
        Assert.AreSame(list[^1], rx.ChosenPrescriptionProduct);
        records.Dispose();
    }

    [TestMethod]
    public async Task TypedButNotChosen_AgregarChoosesTheMarkedSuggestionFirst()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        var ibuprofen = Medicine(records, "MED-003");

        // Escribió y pasó directo a la indicación con el ratón: Agregar elige como Enter en el buscador
        rx.PrescriptionSearchText = "ibupro";
        rx.PrescriptionDosage = "1 tableta cada 8 horas si hay dolor";
        await rx.AddPrescriptionAsync();
        Assert.AreEqual(PrescriptionNaming.MedicineLine(ibuprofen), patient.ActivePrescription.Single().MedicineName);
        records.Dispose();
    }

    [TestMethod]
    public async Task SuggestionsPutWhatIsInStockFirst()
    {
        var (records, _, _) = OpenConsultation();
        var rx = records.Prescription;
        var empty = new Product { Id = 50, Code = "MED-050", Name = "Paracetamol infantil", GenericName = "Paracetamol", CurrentStock = 0 };
        records.Catalog.AvailableProducts.Insert(0, empty);

        rx.PrescriptionSearchText = "parac";
        Assert.AreEqual("MED-001", rx.PrescriptionProductSuggestions[0].Code, "El que hay en el consultorio, primero");
        Assert.AreEqual(empty, rx.PrescriptionProductSuggestions[^1]);

        // El catálogo de medicamentos usa el mismo orden
        await rx.OpenPrescriptionCatalogAsync();
        records.CatalogBrowser.CatalogBrowserSearchText = "parac";
        CollectionAssert.AreEqual(new[] { "MED-001", "MED-050" }, records.CatalogBrowser.CatalogBrowserFilteredProducts.Select(p => p.Code).ToList());
        records.Dispose();
    }

    // ================================================================== lo de siempre al agregar

    [TestMethod]
    public async Task Allergy_StillAsksBeforeAdding_AndKeepsTheIndicationWrittenAfterChoosing()
    {
        var (records, consultations) = Open();
        var patient = new PatientRecord { Id = 77, FullName = "Ana Ruiz", RecordNumber = "EXP-77", Allergies = "Amoxicilina", ActivePrescription = new() };
        records.SelectedPatient = patient;
        records.StartActiveConsultation();
        var rx = records.Prescription;
        rx.OpenPrescriptionModal();

        rx.PrescriptionSearchText = "amox";
        Assert.IsTrue(rx.ConfirmPrescriptionSearch());
        rx.PrescriptionDosage = "1 cápsula cada 12 horas";
        await rx.AddPrescriptionAsync();
        Assert.IsTrue(rx.IsAllergyAlertOpen, "La alerta de alergia sigue antes de agregar");
        Assert.IsEmpty(patient.ActivePrescription);

        await rx.ConfirmOverrideAllergyPrescriptionAsync();
        var row = patient.ActivePrescription.Single();
        Assert.AreEqual("1 cápsula cada 12 horas", row.Dosage, "Con la indicación que se escribió después de elegirlo");
        Assert.IsTrue(row.DispenseInClinic);
        Assert.IsNotEmpty(consultations.CurrentConsultation!.Items);
        records.Dispose();
    }

    [TestMethod]
    public async Task ReadOnlyConsultation_BlocksAddingAndChangingTheIndication()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        rx.PrescriptionSearchText = "parac";
        rx.ConfirmPrescriptionSearch();
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        var row = patient.ActivePrescription.Single();

        // La consulta se envió a otro equipo con la hoja abierta: aquí ya no se modifica
        records.Handoff.IsConsultationHandedOff = true;
        rx.PrescriptionSearchText = "ibu";
        rx.ConfirmPrescriptionSearch();
        await rx.AddPrescriptionAsync();
        Assert.HasCount(1, patient.ActivePrescription, "No se agrega");

        rx.SheetIndication.Start(row);
        Assert.IsFalse(rx.SheetIndication.IsEditing, "No se abre la corrección");
        rx.PlanIndication.Start(row);
        Assert.IsFalse(rx.PlanIndication.IsEditing);

        // Abierta antes del envío: Guardar tampoco escribe
        records.Handoff.IsConsultationHandedOff = false;
        rx.SheetIndication.Start(row);
        records.Handoff.IsConsultationHandedOff = true;
        rx.SheetIndication.Text = "Otra";
        await rx.SheetIndication.SaveAsync();
        Assert.AreEqual("1 tableta cada 8 horas", row.Dosage);
        Assert.IsTrue(rx.SheetIndication.IsEditing, "El campo sigue abierto (se puede recuperar la consulta y guardar)");
        records.Dispose();
    }

    [TestMethod]
    public async Task PediatricDose_AndDefaults_StillWork()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;

        // La dosis pediátrica deja el medicamento sin escribirlo en el buscador ("Usar")
        rx.PrescriptionMedicineName = "Paracetamol 500 mg";
        rx.PrescriptionDosage = "Dar 7 ml vía oral cada 6 hrs por 3 días";
        Assert.IsTrue(rx.AddPrescriptionCommand.CanExecute(null));
        await rx.AddPrescriptionAsync();
        var pediatric = patient.ActivePrescription.Single();
        Assert.AreEqual("Paracetamol 500 mg", pediatric.MedicineName);
        Assert.IsTrue(pediatric.DispenseInClinic, "El producto se encuentra por su nombre, como antes");

        // Sin indicación ni cantidad: las de siempre
        rx.PrescriptionSearchText = "omepra";
        rx.ConfirmPrescriptionSearch();
        rx.PrescriptionQuantity = 0;
        await rx.AddPrescriptionAsync();
        var row = patient.ActivePrescription.Last();
        Assert.AreEqual(PrescriptionSheetViewModel.DefaultDosage, row.Dosage);
        Assert.AreEqual(1, row.RecommendedQuantity);
        records.Dispose();
    }

    [TestMethod]
    public async Task RepeatPreviousPrescription_StillAddsWhatWasMissing()
    {
        var (records, _) = Open();
        var patient = records.Patients.Single(p => p.Id == 2);
        records.SelectPatient(patient);
        var previous = patient.ActivePrescription.Select(p => p.MedicineName).ToList();
        Assert.IsNotEmpty(previous);
        records.StartActiveConsultation();

        Assert.IsTrue(records.Prescription.CanRepeatPreviousPrescription);
        await records.Prescription.RepeatPreviousPrescriptionAsync();
        CollectionAssert.AreEqual(previous, patient.ActivePrescription.Select(p => p.MedicineName).ToList());
        records.Dispose();
    }

    // ================================================================== «Se surte aquí» por defecto (2026-10-22)

    [TestMethod]
    public async Task DispenseInClinic_ComesTickedOnlyWithStock_AndTickedByHandChargesWithTheWarning()
    {
        var (records, consultations, patient) = OpenConsultation();
        var rx = records.Prescription;
        var amoxicillin = Medicine(records, "MED-002");
        var omeprazole = Medicine(records, "MED-004");
        amoxicillin.CurrentStock = 0;
        omeprazole.CurrentStock = -2;
        int Charged(Product p) => consultations.CurrentConsultation!.Items.Where(i => i.ProductId == p.Id).Sum(i => i.Quantity);

        rx.PrescriptionSearchText = "amox";
        rx.ConfirmPrescriptionSearch();
        await rx.AddPrescriptionAsync();
        var noStock = patient.ActivePrescription.Last();
        Assert.IsFalse(noStock.DispenseInClinic, "Sin existencias viene desmarcado");
        Assert.AreEqual(0, Charged(amoxicillin), "Y no se carga");
        Assert.IsFalse(records.Info.IsInfoModalVisible, records.Info.InfoModalMessage);

        rx.PrescriptionSearchText = "omepra";
        rx.ConfirmPrescriptionSearch();
        await rx.AddPrescriptionAsync();
        var negative = patient.ActivePrescription.Last();
        Assert.IsFalse(negative.DispenseInClinic, "En negativo viene desmarcado");
        Assert.AreEqual(0, Charged(omeprazole));

        // Marcado a mano: se carga con el aviso de siempre (un inventario mal contado nunca impide cobrar)
        noStock.DispenseInClinic = true;
        await rx.ToggleDispenseInClinicAsync(noStock);
        Assert.IsTrue(noStock.DispenseInClinic);
        Assert.AreEqual(1, Charged(amoxicillin));
        Assert.IsTrue(records.Info.IsInfoModalVisible);
        Assert.AreEqual($"Existencias insuficientes: {amoxicillin.Name} (quedan 0, se cobran 1). Revise el inventario.", records.Info.InfoModalMessage);

        // Con existencias sigue marcado por defecto (la regla de la consulta: «Se surte aquí» por defecto)
        records.Info.IsInfoModalVisible = false;
        var paracetamol = Medicine(records, "MED-001");
        Assert.IsGreaterThan(0, paracetamol.CurrentStock);
        rx.PrescriptionSearchText = "paracet";
        rx.ConfirmPrescriptionSearch();
        await rx.AddPrescriptionAsync();
        Assert.IsTrue(patient.ActivePrescription.Last().DispenseInClinic);
        Assert.AreEqual(1, Charged(paracetamol));
        records.Dispose();
    }

    [TestMethod]
    public async Task RepeatPreviousPrescription_TicksOnlyWhatHasStock()
    {
        var (records, consultations) = Open();
        var patient = records.Patients.Single(p => p.Id == 2);
        var paracetamol = Medicine(records, "MED-001");
        var amoxicillin = Medicine(records, "MED-002");
        amoxicillin.CurrentStock = 0;
        patient.ActivePrescription = new List<PatientPrescriptionItem>
        {
            new() { MedicineName = paracetamol.Name, Dosage = "1 cada 8 horas", RecommendedQuantity = 1 },
            new() { MedicineName = amoxicillin.Name, Dosage = "1 cada 8 horas", RecommendedQuantity = 1 }
        };
        records.SelectPatient(patient);
        records.StartActiveConsultation();

        await records.Prescription.RepeatPreviousPrescriptionAsync();
        var rows = patient.ActivePrescription;
        Assert.IsTrue(rows.Single(r => r.MedicineName == paracetamol.Name).DispenseInClinic, "Con existencias viene marcado");
        Assert.IsFalse(rows.Single(r => r.MedicineName == amoxicillin.Name).DispenseInClinic, "Sin existencias viene desmarcado");
        Assert.AreEqual(paracetamol.Id, consultations.CurrentConsultation!.Items.Single().ProductId);
        records.Dispose();
    }

    [TestMethod]
    public async Task SuppliesCatalog_EnterAddsTheFirstOne_EvenWithoutStock_WithTheWarning()
    {
        // Revisión de la revisión 20: Enter en el catálogo de insumos saltaba los que no tienen existencias y, sin ninguno con
        // existencias, no agregaba nada
        var (records, consultations, patient) = OpenConsultation();
        foreach (var supply in records.Catalog.AvailableProducts.Where(CatalogStore.IsProductSupply)) supply.CurrentStock = 0;
        await records.Supplies.OpenSupplyCatalogAsync();
        var first = records.CatalogBrowser.CatalogBrowserFilteredProducts.First();

        await records.CatalogBrowser.AddFirstFromCatalogBrowserAsync();
        Assert.IsTrue(patient.ActiveSupplies.Any(s => s.SupplyName.Contains(first.Name)), "Se agrega el primero");
        Assert.AreEqual(1, consultations.CurrentConsultation!.Items.Single(i => i.ProductId == first.Id).Quantity, "Y se carga");
        Assert.IsTrue(records.Info.IsInfoModalVisible);
        StringAssert.StartsWith(records.Info.InfoModalMessage, $"Existencias insuficientes: {first.Name} (quedan 0, se cobran 1)");
        records.Dispose();
    }

    // ================================================================== revisión adversarial de 4214fdf

    [TestMethod]
    public async Task TypedBrandName_AfterClosingTheList_StillChecksAllergiesByItsGeneric()
    {
        var (records, consultations) = Open();
        var patient = new PatientRecord { Id = 78, FullName = "Luis Pérez", RecordNumber = "EXP-78", Allergies = "Paracetamol", ActivePrescription = new() };
        records.SelectedPatient = patient;
        records.StartActiveConsultation();
        var tempra = new Product { Id = 60, Code = "MED-060", Name = "Tempra 500 mg", GenericName = "Paracetamol", Presentation = "Caja con 20 tabletas", CurrentStock = 10, Price = 50m };
        records.Catalog.AvailableProducts.Add(tempra);
        var rx = records.Prescription;
        rx.OpenPrescriptionModal();

        // Escribe la marca, cierra la lista con Esc y Enter: lo toma tal cual, pero es un producto del inventario
        rx.PrescriptionSearchText = "Tempra 500 mg";
        Assert.IsTrue(rx.IsPrescriptionDropdownOpen);
        rx.ClosePrescriptionSuggestions();
        Assert.IsTrue(rx.ConfirmPrescriptionSearch());
        Assert.AreEqual("Tempra 500 mg", rx.PrescriptionSearchText, "Lo escrito se queda");
        Assert.AreEqual(tempra, rx.ChosenPrescriptionProduct, "Es el producto que se surtirá y se cobrará");
        StringAssert.StartsWith(rx.ChosenMedicineCaption, "Elegido del inventario");

        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        Assert.IsTrue(rx.IsAllergyAlertOpen, "La alergia al paracetamol se revisa por el genérico de la marca");
        Assert.IsEmpty(patient.ActivePrescription);

        await rx.ConfirmOverrideAllergyPrescriptionAsync();
        var row = patient.ActivePrescription.Single();
        Assert.AreEqual("Tempra 500 mg", row.MedicineName, "Con el nombre que se escribió");
        Assert.AreEqual(tempra.Presentation, row.Presentation);
        Assert.IsTrue(row.DispenseInClinic);
        Assert.AreEqual(tempra.Id, consultations.CurrentConsultation!.Items.Single().ProductId);
        records.Dispose();
    }

    [TestMethod]
    public async Task PediatricDose_WithTheSuggestionListOpen_AddsThePediatricMedicine_NotTheMarkedSuggestion()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        var rule = new PediatricDoseRule { Id = 1, Medication = "Paracetamol 3.2 g/100 ml", DoseText = "15 mg/kg/dosis", MlPerKg = 15.0 / 32.0, DropsPerMl = 20 };
        records.Templates.PediatricDoseRules = new ObservableCollection<PediatricDoseRule> { rule };

        // ↓ con el buscador vacío abre la lista con la primera marcada; luego la dosis pediátrica y "Usar"
        rx.MovePrescriptionSuggestion(1);
        Assert.IsTrue(rx.IsPrescriptionDropdownOpen);
        var marked = rx.SelectedPrescriptionProduct!;
        rx.SelectedPediatricRule = rule;
        rx.PediatricWeightText = "15";
        rx.ApplyPediatricDose();
        Assert.AreEqual(rule.Medication, rx.PrescriptionMedicineName);
        Assert.IsFalse(rx.IsPrescriptionDropdownOpen, "\"Usar\" cierra la lista: ya no marca otro medicamento");

        // Aunque la lista se vuelva a abrir, Agregar usa el medicamento de la dosis pediátrica
        rx.MovePrescriptionSuggestion(1);
        Assert.IsTrue(rx.IsPrescriptionDropdownOpen);
        await rx.AddPrescriptionAsync();
        var row = patient.ActivePrescription.Single();
        Assert.AreEqual(rule.Medication, row.MedicineName);
        Assert.AreNotEqual(PrescriptionNaming.MedicineLine(marked), row.MedicineName);
        StringAssert.StartsWith(row.Dosage, "Dar 7 ml (140 gotas) vía oral");
        Assert.IsFalse(rx.IsPrescriptionDropdownOpen);
        records.Dispose();
    }

    [TestMethod]
    public async Task FooterGuardar_AddsTheChosenMedicine_AndSavesAnOpenCorrection_InsteadOfDroppingThem()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        rx.PrescriptionSearchText = "parac";
        rx.ConfirmPrescriptionSearch();
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        var row = patient.ActivePrescription.Single();

        // Una corrección abierta y un medicamento elegido con su indicación, sin Enter: "Guardar" del pie
        rx.SheetIndication.Start(row);
        rx.SheetIndication.Text = "1 tableta cada 6 horas";
        rx.PrescriptionSearchText = "ibupro";
        rx.ConfirmPrescriptionSearch();
        rx.PrescriptionDosage = "1 tableta cada 12 horas";
        await rx.SavePrescriptionModalAsync();

        Assert.IsFalse(rx.IsPrescriptionModalOpen);
        Assert.AreEqual("1 tableta cada 6 horas", row.Dosage, "La corrección abierta se guardó");
        Assert.IsFalse(rx.SheetIndication.IsEditing);
        Assert.HasCount(2, patient.ActivePrescription, "El medicamento elegido se agregó");
        Assert.AreEqual(PrescriptionNaming.MedicineLine(Medicine(records, "MED-003")), patient.ActivePrescription[1].MedicineName);
        Assert.AreEqual("1 tableta cada 12 horas", patient.ActivePrescription[1].Dosage);
        Assert.IsFalse(rx.IsMedicineChosen);
        StringAssert.Contains(records.Info.InfoModalMessage, "guardada");

        // Lo escrito sin elegirlo se queda como en v1.39: no se agrega
        records.Info.DismissInfoModal();
        rx.OpenPrescriptionModal();
        rx.PrescriptionSearchText = "omepra";
        await rx.SavePrescriptionModalAsync();
        Assert.HasCount(2, patient.ActivePrescription);
        Assert.IsFalse(rx.IsPrescriptionModalOpen);
        records.Dispose();
    }

    [TestMethod]
    public async Task FooterGuardar_WithAnAllergyOrAReadOnlyConsultation_KeepsTheSheetOpen_AndDoesNotSaySaved()
    {
        var (records, _) = Open();
        var patient = new PatientRecord { Id = 79, FullName = "Ana Ruiz", RecordNumber = "EXP-79", Allergies = "Amoxicilina", ActivePrescription = new() };
        records.SelectedPatient = patient;
        records.StartActiveConsultation();
        var rx = records.Prescription;
        rx.OpenPrescriptionModal();

        rx.PrescriptionSearchText = "amox";
        rx.ConfirmPrescriptionSearch();
        await rx.SavePrescriptionModalAsync();
        Assert.IsTrue(rx.IsAllergyAlertOpen, "La alerta pide confirmar antes de agregarlo");
        Assert.IsTrue(rx.IsPrescriptionModalOpen, "La hoja sigue abierta");
        Assert.IsFalse(records.Info.IsInfoModalVisible, "No dice que se guardó");
        rx.CancelAllergyPrescription();

        // Consulta enviada a otro equipo: la corrección abierta no se puede guardar y la hoja sigue abierta
        rx.ClearChosenMedicine();
        rx.PrescriptionSearchText = "ibupro";
        rx.ConfirmPrescriptionSearch();
        await rx.AddPrescriptionAsync();
        var row = patient.ActivePrescription.Single();
        rx.SheetIndication.Start(row);
        rx.SheetIndication.Text = "Otra";
        records.Handoff.IsConsultationHandedOff = true;
        records.Info.DismissInfoModal();
        await rx.SavePrescriptionModalAsync();
        Assert.IsTrue(rx.IsPrescriptionModalOpen);
        Assert.IsTrue(rx.SheetIndication.IsEditing);
        Assert.AreNotEqual("Otra", row.Dosage);
        records.Dispose();
    }

    [TestMethod]
    public async Task RemovingTheRowBeingCorrected_ClosesItsCorrection()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        rx.PrescriptionSearchText = "parac";
        rx.ConfirmPrescriptionSearch();
        await rx.AddPrescriptionAsync();
        var row = patient.ActivePrescription.Single();

        rx.SheetIndication.Start(row);
        await rx.RemovePrescriptionItemFromPatientAsync(row);
        Assert.IsFalse(rx.SheetIndication.IsEditing);
        Assert.IsEmpty(patient.ActivePrescription);
        records.Dispose();
    }

    [TestMethod]
    public async Task FinishingTheConsultation_SavesAPendingCorrection_AndEndingOrChangingPatientClosesIt()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        rx.PrescriptionSearchText = "parac";
        rx.ConfirmPrescriptionSearch();
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        rx.ClosePrescriptionModal();
        var row = patient.ActivePrescription.Single();

        // "Terminar consulta" (o Ctrl+Enter) con la corrección de la sección 5 abierta: se guarda antes de la nota
        rx.PlanIndication.Start(row);
        rx.PlanIndication.Text = "1 tableta cada 6 horas";
        await records.Consultation.FinalizeConsultationAsync();
        Assert.AreEqual("1 tableta cada 6 horas", row.Dosage);
        Assert.IsFalse(rx.PlanIndication.IsEditing);
        records.Info.DismissInfoModal();

        // Al cambiar de paciente no queda abierta
        rx.PlanIndication.Start(row);
        Assert.IsTrue(rx.PlanIndication.IsEditing);
        records.SelectPatient(records.Patients.First(p => p.Id != patient.Id));
        Assert.IsFalse(rx.PlanIndication.IsEditing);

        // Ni al terminar la consulta (descartada, cobrada o cerrada desde el Punto de Venta)
        records.SelectPatient(patient);
        records.ShowConsultation();
        rx.PlanIndication.Start(row);
        Assert.IsTrue(rx.PlanIndication.IsEditing);
        await records.DiscardActiveConsultationAsync();
        Assert.IsFalse(records.HasActiveConsultation);
        Assert.IsFalse(rx.PlanIndication.IsEditing, "La siguiente consulta no hereda una corrección que se queda con la Esc");
        records.Dispose();
    }

    // ================================================================== lo pendiente antes de cada salida (revisión de 3c35f26)

    /// <summary>Consulta con un medicamento agregado, la hoja cerrada y la corrección de la sección 5 abierta con texto nuevo.</summary>
    private static async Task<(MedicalRecordsViewModel Records, PatientPrescriptionItem Row)> PendingPlanCorrectionAsync()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        rx.PrescriptionSearchText = "parac";
        rx.ConfirmPrescriptionSearch();
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        rx.ClosePrescriptionModal();
        var row = patient.ActivePrescription.Single();
        rx.PlanIndication.Start(row);
        rx.PlanIndication.Text = "1 tableta cada 6 horas";
        return (records, row);
    }

    [TestMethod]
    public async Task PlusMedicamento_SavesAnOpenSection5Correction_BeforeOpeningTheSheet()
    {
        var (records, row) = await PendingPlanCorrectionAsync();
        await records.Prescription.OpenPrescriptionModalAsync();
        Assert.IsTrue(records.Prescription.IsPrescriptionModalOpen);
        Assert.AreEqual("1 tableta cada 6 horas", row.Dosage, "La corrección se guardó en lugar de perderse");
        Assert.IsFalse(records.Prescription.PlanIndication.IsEditing);
        records.Dispose();

        // Si no se puede guardar (consulta en otro equipo) la hoja no se abre y la corrección sigue abierta
        (records, row) = await PendingPlanCorrectionAsync();
        records.Handoff.IsConsultationHandedOff = true;
        await records.Prescription.OpenPrescriptionModalAsync();
        Assert.IsFalse(records.Prescription.IsPrescriptionModalOpen);
        Assert.IsTrue(records.Prescription.PlanIndication.IsEditing);
        Assert.AreEqual("1 tableta cada 6 horas", records.Prescription.PlanIndication.Text);
        Assert.AreEqual("1 tableta cada 8 horas", row.Dosage);
        records.Dispose();
    }

    [TestMethod]
    public async Task VistaPrevia_SavesAnOpenCorrection_SoThePreviewAndPrintCarryIt()
    {
        var (records, row) = await PendingPlanCorrectionAsync();
        await records.Preview.OpenPrescriptionPdfPreviewCommand.ExecuteAsync(null);
        Assert.IsTrue(records.Preview.IsPrescriptionPdfModalOpen);
        Assert.AreEqual("1 tableta cada 6 horas", row.Dosage);
        Assert.IsFalse(records.Prescription.PlanIndication.IsEditing);
        records.Preview.ClosePrescriptionPdfPreview();

        // También la de la hoja (su "Vista previa" del pie); un medicamento elegido no se agrega por ver la vista previa
        var rx = records.Prescription;
        await rx.OpenPrescriptionModalAsync();
        rx.SheetIndication.Start(row);
        rx.SheetIndication.Text = "1 tableta cada 4 horas";
        rx.PrescriptionSearchText = "ibupro";
        rx.ConfirmPrescriptionSearch();
        await records.Preview.OpenPrescriptionPdfPreviewCommand.ExecuteAsync(null);
        Assert.AreEqual("1 tableta cada 4 horas", row.Dosage);
        Assert.IsTrue(rx.IsMedicineChosen, "La vista previa no agrega el medicamento elegido");
        Assert.HasCount(1, records.SelectedPatient!.ActivePrescription);
        records.Dispose();
    }

    [TestMethod]
    public async Task FooterGuardar_AddsAPendingPediatricMedicine_WithTheUsualGuards()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        var rule = new PediatricDoseRule { Id = 1, Medication = "Paracetamol 3.2 g/100 ml", DoseText = "15 mg/kg/dosis", MlPerKg = 15.0 / 32.0, DropsPerMl = 20 };
        records.Templates.PediatricDoseRules = new ObservableCollection<PediatricDoseRule> { rule };
        rx.SelectedPediatricRule = rule;
        rx.PediatricWeightText = "15";
        rx.ApplyPediatricDose();

        await rx.SavePrescriptionModalAsync();
        var row = patient.ActivePrescription.Single();
        Assert.AreEqual(rule.Medication, row.MedicineName, "La dosis pediátrica pendiente se agregó en lugar de perderse");
        StringAssert.StartsWith(row.Dosage, "Dar 7 ml");
        Assert.IsFalse(rx.IsPrescriptionModalOpen);
        records.Dispose();

        // Con la consulta en otro equipo no se agrega y la hoja sigue abierta (sin "guardada")
        (records, _, patient) = OpenConsultation();
        rx = records.Prescription;
        records.Templates.PediatricDoseRules = new ObservableCollection<PediatricDoseRule> { rule };
        rx.SelectedPediatricRule = rule;
        rx.PediatricWeightText = "15";
        rx.ApplyPediatricDose();
        records.Handoff.IsConsultationHandedOff = true;
        await rx.SavePrescriptionModalAsync();
        Assert.IsEmpty(patient.ActivePrescription);
        Assert.IsTrue(rx.IsPrescriptionModalOpen);
        Assert.AreEqual(rule.Medication, rx.PrescriptionMedicineName, "Lo pendiente se queda");
        records.Dispose();
    }

    [TestMethod]
    public async Task SendingToAnotherDevice_SavesAnOpenSection5Correction_BeforeTheCopyIsBuilt()
    {
        using var clinic = new Fase6Clinic();
        await clinic.ConnectCloudAsync();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        patient = records.SelectedPatient!;
        var rx = records.Prescription;
        await rx.OpenPrescriptionModalAsync();
        rx.PrescriptionSearchText = "Paracetamol";
        Assert.IsTrue(rx.ConfirmPrescriptionSearch());
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        rx.ClosePrescriptionModal();
        var row = patient.ActivePrescription.Single();
        rx.PlanIndication.Start(row);
        rx.PlanIndication.Text = "1 tableta cada 6 horas";

        await records.Handoff.OpenSendConsultationSheetAsync();
        records.Handoff.SelectedHandoffDevice = records.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-tablet");
        await records.Handoff.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(records.Handoff.IsConsultationHandedOff, records.Handoff.HandoffSheetError ?? records.Info.InfoModalMessage);

        Assert.AreEqual("1 tableta cada 6 horas", row.Dosage, "Se guardó antes de enviar");
        Assert.IsFalse(rx.PlanIndication.IsEditing);
        var draft = ConsultationDraftStore.Deserialize(clinic.CloudScalar("SELECT Payload FROM ConsultationHandoffs"));
        Assert.AreEqual("1 tableta cada 6 horas", draft!.Prescriptions.Single().Dosage, "El otro equipo recibe la indicación nueva");
        var stored = (await clinic.Inventory.GetPatientsAsync()).Single(p => p.Id == patient.Id);
        Assert.AreEqual("1 tableta cada 6 horas", stored.ActivePrescription.Single().Dosage);
        records.Dispose();
    }

    // ================================================================== indicaciones frecuentes

    [TestMethod]
    public async Task FrequentIndications_OpenMoveAndPick_IntoTheIndicationField_ThatStaysEditable()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        var picker = rx.CaptureIndications;
        UsePosologies(records, "1 tableta vía oral cada 8 horas por 5 días", "1 tableta vía oral cada 12 horas por 7 días", "Aplicar 2 veces al día");
        int picked = 0;
        picker.Picked += () => picked++;

        Assert.IsTrue(picker.Open());
        Assert.HasCount(3, picker.Suggestions);
        Assert.AreEqual(0, picker.SelectedIndex);
        picker.MoveNext();
        picker.MoveNext();
        picker.MoveNext();
        Assert.AreEqual(0, picker.SelectedIndex, "De la última vuelve a la primera");
        picker.MovePrevious();
        Assert.AreEqual(2, picker.SelectedIndex, "De la primera pasa a la última");
        picker.MovePrevious();

        Assert.IsTrue(picker.Accept());
        Assert.AreEqual("1 tableta vía oral cada 12 horas por 7 días", rx.PrescriptionDosage);
        Assert.IsFalse(picker.IsOpen);
        Assert.AreEqual(1, picked);

        // Se puede editar antes de agregar
        rx.PrescriptionDosage += " después de los alimentos";
        rx.PrescriptionSearchText = "parac";
        rx.ConfirmPrescriptionSearch();
        await rx.AddPrescriptionAsync();
        Assert.AreEqual("1 tableta vía oral cada 12 horas por 7 días después de los alimentos", patient.ActivePrescription.Single().Dosage);

        // Clic en una de la lista
        picker.Toggle();
        Assert.IsTrue(picker.IsOpen);
        picker.Pick(picker.Suggestions[2]);
        Assert.AreEqual("Aplicar 2 veces al día", rx.PrescriptionDosage);
        picker.Toggle();
        picker.Toggle();
        Assert.IsFalse(picker.IsOpen, "La flecha también la cierra");
        records.Dispose();
    }

    [TestMethod]
    public void FrequentIndications_TypingFilters_WithoutAccents_AndClosesWhenNothingMatches()
    {
        var (records, _, _) = OpenConsultation();
        var rx = records.Prescription;
        var picker = rx.CaptureIndications;
        UsePosologies(records, "1 tableta cada 8 horas por 5 días", "1 tableta cada 12 horas", "Aplicar en la zona afectada");

        // Al abrir con texto se ven las que coinciden
        rx.PrescriptionDosage = "cada";
        Assert.IsTrue(picker.Open());
        Assert.HasCount(2, picker.Suggestions);

        // Con la lista abierta, lo escrito la filtra (sin acentos ni mayúsculas)
        rx.PrescriptionDosage = "5 DIAS";
        Assert.IsTrue(picker.IsOpen);
        Assert.AreEqual("1 tableta cada 8 horas por 5 días", picker.Suggestions.Single().Text);
        rx.PrescriptionDosage = string.Empty;
        Assert.HasCount(3, picker.Suggestions, "Sin texto, todas");

        // Si ya no coincide ninguna se cierra y Enter vuelve a ser del campo
        rx.PrescriptionDosage = "media tableta";
        Assert.IsFalse(picker.IsOpen);
        Assert.IsFalse(picker.Accept());

        // Abrir con un texto que no coincide muestra todas; con el de una ya elegida, todas con esa marcada
        Assert.IsTrue(picker.Open());
        Assert.HasCount(3, picker.Suggestions);
        picker.Close();
        rx.PrescriptionDosage = "Aplicar en la zona afectada";
        Assert.IsTrue(picker.Open());
        Assert.HasCount(3, picker.Suggestions);
        Assert.AreEqual(2, picker.SelectedIndex);

        // Sin indicaciones frecuentes en el consultorio no hay lista
        picker.Close();
        records.Templates.PosologyTemplates = new ObservableCollection<PrescriptionTemplate>();
        Assert.IsFalse(picker.Open());
        Assert.IsFalse(picker.IsOpen);
        records.Dispose();
    }

    // ================================================================== cambiar indicación

    [TestMethod]
    public async Task ChangeIndication_EditsTheSameRow_KeepingItsRowIdAndExtensionData_AndNothingElse()
    {
        var (records, consultations, patient) = OpenConsultation();
        var rx = records.Prescription;
        foreach (var (search, dosage) in new[] { ("parac", "1 tableta cada 8 horas"), ("ibupro", "1 tableta cada 12 horas") })
        {
            rx.PrescriptionSearchText = search;
            rx.ConfirmPrescriptionSearch();
            rx.PrescriptionDosage = dosage;
            await rx.AddPrescriptionAsync();
        }
        var row = patient.ActivePrescription[0];
        var other = patient.ActivePrescription[1];
        row.ExtensionData = new Dictionary<string, JsonElement> { ["FromTablet"] = JsonSerializer.SerializeToElement(true) };
        patient.SyncPrescriptionJson();
        var rowId = row.RowId;
        var otherJson = JsonSerializer.Serialize(other);
        var charged = consultations.CurrentConsultation!.Items.Sum(i => i.Quantity);
        Assert.IsNotNull(rowId);

        var editor = rx.SheetIndication;
        editor.Start(row);
        Assert.IsTrue(editor.IsEditing);
        Assert.AreSame(row, editor.Item);
        Assert.AreEqual("1 tableta cada 8 horas", editor.Text, "Empieza con la que tiene");

        // Las frecuentes también aquí
        UsePosologies(records, "1 tableta cada 6 horas por 3 días");
        Assert.IsTrue(editor.Picker.Open());
        Assert.IsTrue(editor.Picker.Accept());
        Assert.AreEqual("1 tableta cada 6 horas por 3 días", editor.Text);
        Assert.AreEqual(string.Empty, rx.PrescriptionDosage, "No toca la indicación de la captura");

        var dosageChanges = new List<string?>();
        row.PropertyChanged += (_, e) => dosageChanges.Add(e.PropertyName);
        await editor.SaveAsync();

        Assert.IsFalse(editor.IsEditing, "Guardar cierra el campo");
        Assert.AreSame(row, patient.ActivePrescription[0], "El mismo objeto, no uno nuevo");
        Assert.AreEqual("1 tableta cada 6 horas por 3 días", row.Dosage);
        CollectionAssert.Contains(dosageChanges, nameof(PatientPrescriptionItem.Dosage), "La lista la muestra sin rehacerse");
        Assert.AreEqual(rowId, row.RowId);
        Assert.IsTrue(row.ExtensionData!["FromTablet"].GetBoolean(), "Lo que trae de otro equipo se queda");
        Assert.IsTrue(row.DispenseInClinic);
        Assert.AreEqual(otherJson, JsonSerializer.Serialize(other), "El otro renglón no cambia");
        Assert.AreEqual(charged, consultations.CurrentConsultation!.Items.Sum(i => i.Quantity), "La cuenta no cambia");

        var stored = JsonSerializer.Deserialize<List<JsonElement>>(patient.ActivePrescriptionJson)!;
        Assert.AreEqual("1 tableta cada 6 horas por 3 días", stored[0].GetProperty("Dosage").GetString());
        Assert.AreEqual(rowId.ToString(), stored[0].GetProperty("RowId").GetString());
        Assert.IsTrue(stored[0].GetProperty("FromTablet").GetBoolean());

        // Cancelar la deja como estaba; sin texto queda la de siempre
        editor.Start(row);
        editor.Text = "Cualquier cosa";
        editor.Cancel();
        Assert.AreEqual("1 tableta cada 6 horas por 3 días", row.Dosage);
        Assert.IsNull(editor.Item);
        editor.Start(row);
        editor.Text = "   ";
        await editor.SaveAsync();
        Assert.AreEqual(PrescriptionSheetViewModel.DefaultDosage, row.Dosage);

        // Un renglón que no es de la receta abierta no se corrige
        rx.PlanIndication.Start(new PatientPrescriptionItem { MedicineName = "Otro" });
        Assert.IsFalse(rx.PlanIndication.IsEditing);
        records.Dispose();
    }

    /// <summary>Como una bajada de otro equipo: cambia la hoja de la receta en la fila, con una marca posterior.</summary>
    private static void DownloadPrescription(Fase6Clinic clinic, int patientId, Action<List<Dictionary<string, JsonElement>>> change)
    {
        using var db = new AppDbContext(clinic.Options);
        db.ApplyingRemoteChanges = true;
        var row = db.Patients.Single(p => p.Id == patientId);
        var sheet = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(row.ActivePrescriptionJson)!;
        change(sheet);
        row.ActivePrescriptionJson = JsonSerializer.Serialize(sheet);
        row.UpdatedAtUtc = row.UpdatedAtUtc.AddSeconds(1);
        db.SaveChanges();
    }

    [TestMethod]
    public async Task ChangeIndication_SavesOnlyThatRow_OverWhatAnotherDeviceWroteMeanwhile()
    {
        using var clinic = new Fase6Clinic();
        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false), drafts: false);
        var patient = Fase6Scenarios.ScriptedPatient(records);
        records.OpenPatientChart(patient);
        Assert.IsGreaterThanOrEqualTo(2, patient.ActivePrescription.Count, "El paciente del guion trae dos renglones");
        var rowIds = patient.ActivePrescription.Select(r => r.RowId).ToList();

        // Llega de otro equipo un campo que esta PC no conoce en el primer renglón; sin consulta, la ficha toma la de la base
        DownloadPrescription(clinic, patient.Id, sheet =>
        {
            for (int i = 0; i < sheet.Count; i++) sheet[i]["RowId"] = JsonSerializer.SerializeToElement(rowIds[i]);
            sheet[0]["FromTablet"] = JsonSerializer.SerializeToElement("sí");
        });
        await records.LoadPatientsAsync();
        patient = records.SelectedPatient!;
        var first = patient.ActivePrescription[0];
        Assert.IsNotNull(first.ExtensionData, "El renglón trae lo del otro equipo");

        // Con la receta abierta, otro equipo cambia el segundo renglón
        DownloadPrescription(clinic, patient.Id, sheet => sheet[1]["Dosage"] = JsonSerializer.SerializeToElement("Cambiada en la tablet"));

        records.Prescription.OpenPrescriptionModal();
        records.Prescription.SheetIndication.Start(first);
        records.Prescription.SheetIndication.Text = "Monitoreo diario en ayunas";
        await records.Prescription.SheetIndication.SaveAsync();
        Assert.AreSame(first, patient.ActivePrescription[0]);

        using var check = new AppDbContext(clinic.Options);
        var stored = check.Patients.AsNoTracking().Single(p => p.Id == patient.Id);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(stored.ActivePrescriptionJson)!;
        Assert.AreEqual("Monitoreo diario en ayunas", rows[0].GetProperty("Dosage").GetString(), "Se guardó la indicación nueva");
        Assert.AreEqual(rowIds[0].ToString(), rows[0].GetProperty("RowId").GetString(), "En el mismo renglón");
        Assert.AreEqual("sí", rows[0].GetProperty("FromTablet").GetString(), "Con lo que el otro equipo le puso");
        Assert.AreEqual("Cambiada en la tablet", rows[1].GetProperty("Dosage").GetString(), "El otro renglón no se pisa");
        records.Dispose();
    }
}
