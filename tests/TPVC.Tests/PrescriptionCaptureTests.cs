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
