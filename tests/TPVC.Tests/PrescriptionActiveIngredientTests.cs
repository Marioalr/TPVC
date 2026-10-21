using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
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
/// Pedido del médico del 2026-10-21: la marca afuera y el principio activo entre paréntesis, con la casilla «Mostrar (…)» por
/// medicamento. Al recetar (junto a "Indicación") empieza con la última elección de este equipo (LocalPreferences, no se
/// sincroniza; la primera vez, marcada); en un renglón agregado (hoja y sección 5) cambia ese mismo renglón y se guarda como
/// cualquier otra edición, con la guarda de solo lectura y la marca del renglón del paso 8.22. Los renglones de antes no se
/// tocan y siguen encontrando su producto; los campos nuevos viajan y vuelven intactos.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PrescriptionActiveIngredientTests
{
    [TestInitialize]
    public void Setup() => DialogService.ConfirmHandler = (_, _) => true;

    private static Product Tempra() => new()
    {
        Id = 60, Code = "MED-060", Name = "Tempra 500 mg", GenericName = "Paracetamol", Presentation = "Caja con 20 tabletas", CurrentStock = 10, Price = 50m
    };

    private static Product Valclan() => new()
    {
        Id = 61, Code = "MED-061", Name = "VALCLAN 875/125MG C/14 TABS", GenericName = "Amoxicilina/Ácido clavulánico", Presentation = "Caja con 14 tabletas",
        CurrentStock = 5, Price = 180m
    };

    /// <summary>La receta abierta en una consulta en curso del paciente 2, con dos marcas en el catálogo.</summary>
    private static (MedicalRecordsViewModel Records, ConsultationService Consultations, PatientRecord Patient) OpenConsultation(LocalPreferencesStore? preferences = null)
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(null, consultations, localPreferences: preferences);
        records.Catalog.AvailableProducts.Add(Tempra());
        records.Catalog.AvailableProducts.Add(Valclan());
        var patient = records.Patients.Single(p => p.Id == 2);
        records.SelectPatient(patient);
        records.StartActiveConsultation();
        consultations.CurrentConsultation!.StartedAt = DateTime.Now.AddHours(1);
        records.Prescription.OpenPrescriptionModal();
        return (records, consultations, patient);
    }

    private static Product Medicine(MedicalRecordsViewModel records, string code) => records.Catalog.AvailableProducts.Single(p => p.Code == code);

    private static async Task<PatientPrescriptionItem> PrescribeAsync(MedicalRecordsViewModel records, Product product, bool? show = null)
    {
        var rx = records.Prescription;
        rx.ChoosePrescriptionProduct(product);
        if (show is { } value) rx.ShowChosenActiveIngredient = value;
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        return records.SelectedPatient!.ActivePrescription[^1];
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"tpvc_pref_{Guid.NewGuid():N}.json");

    // ================================================================== al recetar

    [TestMethod]
    public async Task ABrandMedicine_ShowsTheToggleNextToTheIndication_AndIsAddedWithOrWithoutTheActiveIngredient()
    {
        var (records, consultations, patient) = OpenConsultation();
        var rx = records.Prescription;
        var tempra = Medicine(records, "MED-060");
        Assert.IsTrue(rx.ShowChosenActiveIngredient, "La primera vez, marcada");
        Assert.IsFalse(rx.HasChosenActiveIngredient, "Sin medicamento elegido no se ve");

        rx.PrescriptionSearchText = "tempra";
        Assert.IsTrue(rx.ConfirmPrescriptionSearch());
        Assert.AreSame(tempra, rx.ChosenPrescriptionProduct);
        Assert.IsTrue(rx.HasChosenActiveIngredient);
        Assert.AreEqual("Mostrar (Paracetamol)", rx.ChosenActiveIngredientToggleText, "Dice lo que va entre paréntesis");
        Assert.AreEqual("Tempra 500 mg tabletas (Paracetamol)", rx.PrescriptionSearchText);

        // Desmarcarla: el buscador muestra el renglón como se va a escribir, sin volver a buscar
        rx.ShowChosenActiveIngredient = false;
        Assert.AreEqual("Tempra 500 mg tabletas", rx.PrescriptionSearchText);
        Assert.IsTrue(rx.IsMedicineChosen, "Sigue elegido");
        Assert.IsFalse(rx.IsPrescriptionDropdownOpen);

        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        var row = patient.ActivePrescription.Single();
        Assert.AreEqual("Tempra 500 mg tabletas", row.MedicineName);
        Assert.AreEqual("Paracetamol", row.ActiveIngredient, "El renglón guarda el principio activo para volver a mostrarlo");
        Assert.IsFalse(row.ShowActiveIngredient);
        Assert.AreEqual(tempra.Presentation, row.Presentation);
        Assert.IsTrue(row.DispenseInClinic, "Sin el paréntesis sigue siendo el producto que se surte y se cobra");
        Assert.AreEqual(1, consultations.CurrentConsultation!.Items.Single(i => i.ProductId == tempra.Id).Quantity);

        // El siguiente empieza sin el paréntesis (la última elección); marcarlo lo agrega con él
        var valclan = await PrescribeAsync(records, Medicine(records, "MED-061"));
        Assert.AreEqual("Valclan 875/125 mg tabletas", valclan.MedicineName);
        Assert.IsFalse(valclan.ShowActiveIngredient);
        rx.ChoosePrescriptionProduct(tempra);
        Assert.IsFalse(rx.ShowChosenActiveIngredient);
        Assert.AreEqual("Tempra 500 mg tabletas", rx.PrescriptionSearchText);
        rx.ShowChosenActiveIngredient = true;
        rx.ClearChosenMedicine();
        var shown = await PrescribeAsync(records, Medicine(records, "MED-061"));
        Assert.AreEqual("Valclan 875/125 mg tabletas (Amoxicilina/Ácido clavulánico)", shown.MedicineName, "Marcada otra vez, se recuerda");
        Assert.IsTrue(shown.ShowActiveIngredient);
        records.Dispose();
    }

    [TestMethod]
    public async Task AGenericOrATypedMedicine_HasNoToggle_AndNoActiveIngredient()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;

        rx.ChoosePrescriptionProduct(Medicine(records, "MED-001"));
        Assert.IsFalse(rx.HasChosenActiveIngredient, "Un genérico no lleva paréntesis");
        Assert.AreEqual(string.Empty, rx.ChosenActiveIngredientToggleText);
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        var generic = patient.ActivePrescription.Single();
        Assert.AreEqual("Paracetamol 500 mg tabletas", generic.MedicineName);
        Assert.IsNull(generic.ActiveIngredient);
        Assert.IsNull(generic.ShowActiveIngredient);
        Assert.IsFalse(generic.HasActiveIngredient, "El renglón no tiene la casilla");

        // Escrito tal cual (aunque sea una marca del inventario): el texto, sin casilla
        rx.PrescriptionSearchText = "Tempra 500 mg";
        rx.ClosePrescriptionSuggestions();
        Assert.IsTrue(rx.ConfirmPrescriptionSearch());
        Assert.IsFalse(rx.HasChosenActiveIngredient);
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        var typed = patient.ActivePrescription[^1];
        Assert.AreEqual("Tempra 500 mg", typed.MedicineName);
        Assert.IsNull(typed.ActiveIngredient);
        StringAssert.DoesNotMatch(patient.ActivePrescriptionJson, new System.Text.RegularExpressions.Regex("ActiveIngredient"), "Ni el genérico ni lo escrito escriben los campos nuevos");
        records.Dispose();
    }

    [TestMethod]
    public void TheRememberedChoice_IsKeptInThisComputersPreferences()
    {
        var file = TempFile();
        try
        {
            Assert.IsFalse(new LocalPreferencesStore(file).Current.HidePrescriptionActiveIngredient, "Sin archivo: se muestra");
            var (records, _, _) = OpenConsultation(new LocalPreferencesStore(file));
            records.Prescription.ChoosePrescriptionProduct(Medicine(records, "MED-060"));
            records.Prescription.ShowChosenActiveIngredient = false;
            Assert.IsTrue(new LocalPreferencesStore(file).Current.HidePrescriptionActiveIngredient, "Se guardó en este equipo al elegir");
            records.Dispose();

            // Otra vez la pantalla (o la app): empieza desmarcada
            var (again, _, _) = OpenConsultation(new LocalPreferencesStore(file));
            Assert.IsFalse(again.Prescription.ShowChosenActiveIngredient);
            again.Prescription.ChoosePrescriptionProduct(Medicine(again, "MED-060"));
            Assert.AreEqual("Tempra 500 mg tabletas", again.Prescription.PrescriptionSearchText);
            again.Prescription.ShowChosenActiveIngredient = true;
            Assert.IsFalse(new LocalPreferencesStore(file).Current.HidePrescriptionActiveIngredient);
            again.Dispose();

            // Elegir otro medicamento vuelve a la última elección sin contarla como una nueva
            var (third, _, _) = OpenConsultation(new LocalPreferencesStore(file));
            third.Prescription.ChoosePrescriptionProduct(Medicine(third, "MED-061"));
            Assert.IsTrue(third.Prescription.ShowChosenActiveIngredient);
            third.Dispose();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public async Task AnAllergyAlert_KeepsTheChoice_UntilItIsConfirmed()
    {
        var (records, _, patient) = OpenConsultation();
        patient.Allergies = "Paracetamol";
        var rx = records.Prescription;
        rx.ChoosePrescriptionProduct(Medicine(records, "MED-060"));
        rx.ShowChosenActiveIngredient = false;
        rx.PrescriptionDosage = "1 tableta cada 8 horas";
        await rx.AddPrescriptionAsync();
        Assert.IsTrue(rx.IsAllergyAlertOpen, "Sin el paréntesis, la alergia se revisa por el genérico del producto");

        await rx.ConfirmOverrideAllergyPrescriptionAsync();
        var row = patient.ActivePrescription.Single();
        Assert.AreEqual("Tempra 500 mg tabletas", row.MedicineName);
        Assert.AreEqual("Paracetamol", row.ActiveIngredient);
        Assert.IsFalse(row.ShowActiveIngredient);
        records.Dispose();
    }

    // ================================================================== en un renglón agregado

    [TestMethod]
    public async Task TheRowToggle_ChangesTheSameRow_KeepsTheAccount_AndIsRemembered()
    {
        var (records, consultations, patient) = OpenConsultation();
        var rx = records.Prescription;
        var tempra = Medicine(records, "MED-060");
        var row = await PrescribeAsync(records, tempra);
        Assert.AreEqual("Tempra 500 mg tabletas (Paracetamol)", row.MedicineName);
        Assert.IsTrue(row.HasActiveIngredient);
        Assert.AreEqual("Mostrar (Paracetamol)", row.ActiveIngredientToggleText);
        row.RowId = Guid.NewGuid();
        var rowId = row.RowId;
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // La casilla del renglón (hoja o sección 5): ya cambió el valor y el comando quita el paréntesis
        row.ShowsActiveIngredient = false;
        await rx.ToggleActiveIngredientCommand.ExecuteAsync(row);
        Assert.AreSame(row, patient.ActivePrescription.Single(), "El mismo renglón");
        Assert.AreEqual(rowId, row.RowId);
        Assert.AreEqual("Tempra 500 mg tabletas", row.MedicineName);
        Assert.IsFalse(row.ShowActiveIngredient);
        Assert.AreEqual("1 tableta cada 8 horas", row.Dosage);
        Assert.IsTrue(row.DispenseInClinic);
        CollectionAssert.Contains(changed, nameof(PatientPrescriptionItem.MedicineName), "La lista lo muestra sin rehacerse");
        StringAssert.Contains(patient.ActivePrescriptionJson, "\"MedicineName\":\"Tempra 500 mg tabletas\",\"ActiveIngredient\":\"Paracetamol\",\"ShowActiveIngredient\":false");
        Assert.AreEqual(1, consultations.CurrentConsultation!.Items.Single(i => i.ProductId == tempra.Id).Quantity, "La cuenta no cambia");
        rx.ChoosePrescriptionProduct(Medicine(records, "MED-061"));
        Assert.IsFalse(rx.ShowChosenActiveIngredient, "El siguiente medicamento empieza con esta elección");
        rx.ClearChosenMedicine();

        Assert.IsTrue(await rx.SetActiveIngredientShownAsync(row, true));
        Assert.AreEqual("Tempra 500 mg tabletas (Paracetamol)", row.MedicineName);
        Assert.IsTrue(row.ShowActiveIngredient);
        Assert.IsTrue(await rx.SetActiveIngredientShownAsync(row, true), "Marcarla otra vez no repite el paréntesis");
        Assert.AreEqual("Tempra 500 mg tabletas (Paracetamol)", row.MedicineName);

        // Oculto, quitarlo de la receta descuenta su producto de la cuenta
        await rx.SetActiveIngredientShownAsync(row, false);
        await rx.RemovePrescriptionItemFromPatientAsync(row);
        Assert.IsFalse(consultations.CurrentConsultation.Items.Any(i => i.ProductId == tempra.Id));
        records.Dispose();
    }

    [TestMethod]
    public async Task AConsultationSentToAnotherComputer_BlocksTheRowToggle_AndTheCheckBoxGoesBack()
    {
        var (records, _, patient) = OpenConsultation();
        var rx = records.Prescription;
        var row = await PrescribeAsync(records, Medicine(records, "MED-060"));
        var json = patient.ActivePrescriptionJson;

        records.Handoff.IsConsultationHandedOff = true;
        row.ShowsActiveIngredient = false;
        await rx.ToggleActiveIngredientCommand.ExecuteAsync(row);
        Assert.AreEqual("Tempra 500 mg tabletas (Paracetamol)", row.MedicineName, "No cambia");
        Assert.IsTrue(row.ShowActiveIngredient, "La casilla regresa");
        Assert.IsFalse(await rx.SetActiveIngredientShownAsync(row, false));
        Assert.IsTrue(row.ShowsActiveIngredient);
        Assert.AreEqual(json, patient.ActivePrescriptionJson);
        Assert.IsTrue(rx.ShowChosenActiveIngredient, "Lo bloqueado no cuenta como elección");

        // Al recuperarla se puede otra vez
        records.Handoff.IsConsultationHandedOff = false;
        Assert.IsTrue(await rx.SetActiveIngredientShownAsync(row, false));
        Assert.AreEqual("Tempra 500 mg tabletas", row.MedicineName);
        records.Dispose();
    }

    [TestMethod]
    public async Task RepeatingThePreviousPrescription_KeepsEachRowAsItWasPrescribed()
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(null, consultations);
        records.Catalog.AvailableProducts.Add(Tempra());
        records.Catalog.AvailableProducts.Add(Valclan());
        var patient = records.Patients.Single(p => p.Id == 2);
        patient.ActivePrescription = new List<PatientPrescriptionItem>
        {
            new()
            {
                MedicineName = "Valclan 875/125 mg tabletas", ActiveIngredient = "Amoxicilina/Ácido clavulánico", ShowActiveIngredient = false,
                Dosage = "1 cada 12 horas", Presentation = "Caja con 14 tabletas"
            },
            new() { MedicineName = "Paracetamol 500 mg tabletas (Tempra)", Dosage = "1 cada 8 horas" }
        };
        records.SelectPatient(patient);
        records.StartActiveConsultation();
        Assert.IsTrue(records.Prescription.CanRepeatPreviousPrescription);
        Assert.IsTrue(records.Prescription.ShowChosenActiveIngredient, "La elección de hoy es mostrarlo");

        await records.Prescription.RepeatPreviousPrescriptionAsync();
        var rows = patient.ActivePrescription;
        Assert.HasCount(2, rows);
        Assert.AreEqual("Valclan 875/125 mg tabletas", rows[0].MedicineName);
        Assert.AreEqual("Amoxicilina/Ácido clavulánico", rows[0].ActiveIngredient);
        Assert.IsFalse(rows[0].ShowActiveIngredient, "Como se recetó, no con la elección de hoy");
        Assert.IsTrue(rows[0].DispenseInClinic, "Sin el paréntesis encuentra su producto");
        Assert.AreEqual("Paracetamol 500 mg tabletas (Tempra)", rows[1].MedicineName, "El de antes, tal cual");
        Assert.IsNull(rows[1].ActiveIngredient);
        Assert.IsTrue(rows[1].DispenseInClinic, "Y encuentra su producto para cobrarlo");

        // Mostrar el principio activo del repetido no lo vuelve a ofrecer como pendiente
        Assert.IsTrue(await records.Prescription.SetActiveIngredientShownAsync(rows[0], true));
        Assert.IsFalse(records.Prescription.CanRepeatPreviousPrescription, "Es el mismo medicamento con o sin el paréntesis");
        records.Dispose();
    }

    // ================================================================== renglones de antes y de otros equipos

    private static readonly Guid PatientSync = new("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
    private static readonly Guid RowA = new("a0000000-0000-4000-8000-00000000000a");
    private static readonly Guid RowB = new("a0000000-0000-4000-8000-00000000000b");

    private const string LegacySheet =
        """[{"RowId":"a0000000-0000-4000-8000-00000000000b","MedicineName":"Paracetamol 500 mg tabletas (Tempra)","Dosage":"1 cada 8 horas","RecommendedQuantity":1,"Presentation":"Caja con 20 tabletas","DispenseInClinic":true}]""";

    [TestMethod]
    public async Task ALegacyRow_IsNeverRewritten_HasNoToggle_AndStillFindsItsProduct()
    {
        var (records, consultations, patient) = OpenConsultation();
        var tempra = Medicine(records, "MED-060");
        // Un renglón guardado por v1.45 (o por otro equipo sin los campos nuevos)
        patient.ActivePrescription = new PatientRecord { ActivePrescriptionJson = LegacySheet }.ActivePrescription;
        patient.SyncPrescriptionJson();
        var legacy = patient.ActivePrescription.Single();
        Assert.IsFalse(legacy.HasActiveIngredient, "Sin casilla");

        Assert.IsFalse(await records.Prescription.SetActiveIngredientShownAsync(legacy, false));
        Assert.AreEqual("Paracetamol 500 mg tabletas (Tempra)", legacy.MedicineName, "No se reescribe");
        Assert.IsNull(legacy.ShowActiveIngredient);

        // Agregar otro renglón vuelve a escribir la hoja: el de antes queda igual, sin los campos nuevos
        await PrescribeAsync(records, tempra, show: false);
        var written = JsonNode.Parse(patient.ActivePrescriptionJson)!.AsArray();
        Assert.AreEqual(JsonNode.Parse(LegacySheet)!.AsArray()[0]!.ToJsonString(), written[0]!.ToJsonString());

        Assert.AreSame(tempra, records.Catalog.FindCatalogProduct(legacy.MedicineName), "Encuentra su producto (cobrar y alergias)");
        Assert.IsTrue(AllergyRules.CheckMedication(new PatientRecord { Allergies = "Paracetamol" }, records.Catalog.FindCatalogProduct(legacy.MedicineName), legacy.MedicineName).HasConflict);
        Assert.IsTrue(consultations.CurrentConsultation!.Items.Any(i => i.ProductId == tempra.Id));
        records.Dispose();
    }

    /// <summary>Un renglón de receta como lo conoce una PC v1.45 (revisión 15): sin los campos nuevos.</summary>
    private sealed class V145PrescriptionItem
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? RowId { get; set; }
        public string MedicineName { get; set; } = string.Empty;
        public string Dosage { get; set; } = string.Empty;
        public int RecommendedQuantity { get; set; } = 1;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Presentation { get; set; }
        public bool DispenseInClinic { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private static readonly JsonSerializerOptions V145Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNameCaseInsensitive = true };

    [TestMethod]
    public void TheNewFields_TravelThroughAnOlderComputer_AndComeBackIntact()
    {
        var pc = new PatientRecord { SyncId = PatientSync };
        pc.ActivePrescription = new List<PatientPrescriptionItem>
        {
            new()
            {
                RowId = RowA, MedicineName = "Tempra 500 mg tabletas", ActiveIngredient = "Paracetamol", ShowActiveIngredient = false, Dosage = "1 cada 8 horas",
                Presentation = "Caja con 20 tabletas", DispenseInClinic = true
            }
        };
        var written = pc.ActivePrescriptionJson;
        StringAssert.Contains(written, "\"ActiveIngredient\":\"Paracetamol\",\"ShowActiveIngredient\":false");

        // Una PC anterior lo lee y lo vuelve a escribir (por ejemplo al cambiar la indicación): los campos se quedan
        var old = JsonSerializer.Deserialize<List<V145PrescriptionItem>>(written, V145Options)!;
        Assert.AreEqual("Tempra 500 mg tabletas", old[0].MedicineName, "Imprime el renglón tal cual");
        old[0].Dosage = "1 cada 6 horas";
        var back = JsonSerializer.Serialize(old, V145Options);

        var again = new PatientRecord { SyncId = PatientSync, ActivePrescriptionJson = back };
        var row = again.ActivePrescription.Single();
        Assert.AreEqual("Paracetamol", row.ActiveIngredient);
        Assert.IsFalse(row.ShowActiveIngredient);
        Assert.AreEqual("1 cada 6 horas", row.Dosage);
        Assert.IsNull(row.ExtensionData, "Esta PC los conoce: no quedan como desconocidos");
        Assert.IsTrue(row.HasActiveIngredient, "La casilla vuelve a funcionar aquí");

        // Un renglón de otro equipo con campos que esta PC no conoce: se quedan junto a los nuevos
        const string future = """[{"RowId":"a0000000-0000-4000-8000-00000000000a","MedicineName":"Tempra 500 mg tabletas (Paracetamol)","ActiveIngredient":"Paracetamol","ShowActiveIngredient":true,"Dosage":"x","RecommendedQuantity":1,"DispenseInClinic":false,"Futuro":{"a":1}}]""";
        var tablet = new PatientRecord { SyncId = PatientSync, ActivePrescriptionJson = future };
        tablet.SyncPrescriptionJson();
        Assert.AreEqual(JsonNode.Parse(future)!.ToJsonString(), JsonNode.Parse(tablet.ActivePrescriptionJson)!.ToJsonString());

        // Y un renglón de antes vuelve a escribirse igual
        var legacy = new PatientRecord { SyncId = PatientSync, ActivePrescriptionJson = LegacySheet };
        legacy.SyncPrescriptionJson();
        Assert.AreEqual(LegacySheet, legacy.ActivePrescriptionJson);
    }

    [TestMethod]
    public void ShowingOrHiding_IsAChangeOfThatRowOnly_AndTheMergeCarriesIt()
    {
        string Sheet(bool showA, string dosageB)
        {
            var p = new PatientRecord { SyncId = PatientSync };
            p.ActivePrescription = new List<PatientPrescriptionItem>
            {
                new()
                {
                    RowId = RowA, MedicineName = PrescriptionNaming.WithActiveIngredient("Tempra 500 mg tabletas", "Paracetamol", showA), ActiveIngredient = "Paracetamol",
                    ShowActiveIngredient = showA, Dosage = "1 cada 8 horas"
                },
                new() { RowId = RowB, MedicineName = "Paracetamol 500 mg tabletas (Tempra)", Dosage = dosageB }
            };
            return p.ActivePrescriptionJson;
        }

        var columns = new[] { nameof(PatientRecord.Phone), PatientSheetRows.PrescriptionColumn };
        var fields = PatientMerge.FieldColumns(columns);
        var t0 = new DateTime(2026, 10, 21, 10, 0, 0);
        var common = Sheet(true, "1 cada 8 horas");
        var baseStamps = PatientFieldStamps.ApplyLocalWrite(null, null, t0, Array.Empty<string>(),
            new[] { (PatientSheetRows.PrescriptionColumn, (string?)null, (string?)common) }, PatientSync, fields).ToJson();

        // En la PC se oculta el principio activo del renglón A; en otro equipo se corrige la indicación del B (de antes)
        var pcSheet = Sheet(false, "1 cada 8 horas");
        var pcStamps = PatientFieldStamps.ApplyLocalWrite(baseStamps, t0, t0.AddSeconds(20), Array.Empty<string>(),
            new[] { (PatientSheetRows.PrescriptionColumn, (string?)common, (string?)pcSheet) }, PatientSync, fields);
        Assert.AreEqual(PatientFieldStamps.Format(t0.AddSeconds(20)), pcStamps[PatientFieldStamps.RowKey(PatientSheetRows.PrescriptionColumn, RowA)], "Marca su renglón");
        Assert.AreNotEqual(PatientFieldStamps.Format(t0.AddSeconds(20)), pcStamps[PatientFieldStamps.RowKey(PatientSheetRows.PrescriptionColumn, RowB)], "Y solo ese");

        var otherSheet = Sheet(true, "1 cada 6 horas");
        var otherStamps = PatientFieldStamps.ApplyLocalWrite(baseStamps, t0, t0.AddSeconds(30), Array.Empty<string>(),
            new[] { (PatientSheetRows.PrescriptionColumn, (string?)common, (string?)otherSheet) }, PatientSync, fields);

        PatientMerge.Version Version(DateTime updated, string stamps, string sheet) => new()
        {
            SyncId = PatientSync, UpdatedAtUtc = updated, FieldStampsJson = stamps,
            Values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                [nameof(PatientRecord.Phone)] = JsonValue.Create(string.Empty),
                [PatientSheetRows.PrescriptionColumn] = JsonValue.Create(sheet)
            }
        };
        var pc = Version(t0.AddSeconds(20), pcStamps.ToJson(), pcSheet);
        var other = Version(t0.AddSeconds(30), otherStamps.ToJson(), otherSheet);
        foreach (var (local, remote) in new[] { (pc, other), (other, pc) })
        {
            var merged = PatientMerge.Merge(local, remote, columns);
            var rows = new PatientRecord { SyncId = PatientSync, ActivePrescriptionJson = CanonicalJson.AsString(merged.Values[PatientSheetRows.PrescriptionColumn]) ?? "" }.ActivePrescription;
            var a = rows.Single(r => r.RowId == RowA);
            Assert.AreEqual("Tempra 500 mg tabletas", a.MedicineName, "A, oculto como en la PC");
            Assert.IsFalse(a.ShowActiveIngredient);
            var b = rows.Single(r => r.RowId == RowB);
            Assert.AreEqual("1 cada 6 horas", b.Dosage, "B, con la indicación del otro equipo");
            Assert.AreEqual("Paracetamol 500 mg tabletas (Tempra)", b.MedicineName, "y su nombre de antes");
        }
    }

    [TestMethod]
    public async Task SavingTheRowToggle_StampsOnlyThatRow_InTheDatabase()
    {
        using var clinic = new Fase6Clinic();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        records.Catalog.AvailableProducts.Add(Tempra());
        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        records.ChartPrimaryAction();
        Assert.IsTrue(consultations.HasActiveConsultation);
        records.Prescription.OpenPrescriptionModal();
        var patient = records.SelectedPatient!;
        var before = patient.ActivePrescription.ToList();
        var other = await PrescribeAsync(records, records.Catalog.AvailableProducts.First(p => p.Code == "MED-060"));
        var row = await PrescribeAsync(records, records.Catalog.AvailableProducts.First(p => p.Code == "MED-060"));
        Assert.AreNotSame(other, row);
        var syncId = patient.SyncId;

        string Stamp(Guid? rowId)
        {
            using var db = new AppDbContext(clinic.Options);
            var stamps = PatientFieldStamps.Parse(db.Patients.AsNoTracking().Single(p => p.SyncId == syncId).FieldStampsJson);
            return stamps[PatientFieldStamps.RowKey(PatientSheetRows.PrescriptionColumn, rowId!.Value)] ?? string.Empty;
        }
        string StoredSheet()
        {
            using var db = new AppDbContext(clinic.Options);
            return db.Patients.AsNoTracking().Single(p => p.SyncId == syncId).ActivePrescriptionJson;
        }
        Assert.IsNotNull(row.RowId, "Guardado: ya tiene su RowId");
        var rowStamp = Stamp(row.RowId);
        var otherStamp = Stamp(other.RowId);
        await Task.Delay(20);

        Assert.IsTrue(await records.Prescription.SetActiveIngredientShownAsync(row, false));
        StringAssert.Contains(StoredSheet(), "\"MedicineName\":\"Tempra 500 mg tabletas\",\"ActiveIngredient\":\"Paracetamol\",\"ShowActiveIngredient\":false");
        Assert.AreNotEqual(rowStamp, Stamp(row.RowId), "El renglón cuenta como cambiado (8.22)");
        Assert.AreEqual(otherStamp, Stamp(other.RowId), "El otro no");
        Assert.HasCount(before.Count + 2, patient.ActivePrescription);
        records.Dispose();
    }

    // ================================================================== revisión de la rama (5 defectos)

    /// <summary>Una consulta en curso del paciente 2 cuya receta anterior es <paramref name="previous"/>.</summary>
    private static (MedicalRecordsViewModel Records, ConsultationService Consultations, PatientRecord Patient) OpenWithPrevious(
        bool withTempra, params PatientPrescriptionItem[] previous)
    {
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = new MedicalRecordsViewModel(null, consultations);
        if (withTempra) records.Catalog.AvailableProducts.Add(Tempra());
        var patient = records.Patients.Single(p => p.Id == 2);
        patient.ActivePrescription = previous.ToList();
        records.SelectPatient(patient);
        records.StartActiveConsultation();
        consultations.CurrentConsultation!.StartedAt = DateTime.Now.AddHours(1);
        records.Prescription.OpenPrescriptionModal();
        return (records, consultations, patient);
    }

    [TestMethod]
    public async Task Repeat_ComparesByProduct_SoAnOldFormatRowIsTheSameAsTheNewOne_AndNothingIsAddedOrChargedTwice()
    {
        var (records, consultations, patient) = OpenWithPrevious(true,
            new PatientPrescriptionItem { MedicineName = "Paracetamol 500 mg tabletas (Tempra)", Dosage = "1 cada 8 horas" },
            new PatientPrescriptionItem { MedicineName = "Té de manzanilla", Dosage = "Una taza en la noche" });
        var tempra = Medicine(records, "MED-060");
        await PrescribeAsync(records, tempra);
        Assert.AreEqual("Tempra 500 mg tabletas (Paracetamol)", patient.ActivePrescription.Single().MedicineName);
        CollectionAssert.AreEqual(new[] { "Té de manzanilla" }, records.Record.PendingPreviousPrescription.Select(p => p.MedicineName).ToList(),
            "El renglón de antes es el mismo producto: ya no está pendiente");

        await records.Prescription.RepeatPreviousPrescriptionAsync();
        CollectionAssert.AreEqual(new[] { "Tempra 500 mg tabletas (Paracetamol)", "Té de manzanilla" }, patient.ActivePrescription.Select(p => p.MedicineName).ToList(),
            "Solo se agrega lo que faltaba");
        Assert.AreEqual(1, consultations.CurrentConsultation!.Items.Single(i => i.ProductId == tempra.Id).Quantity, "Y se cobra una sola vez");

        // Lo que no es un producto del catálogo se sigue comparando por el texto
        Assert.IsFalse(records.Prescription.CanRepeatPreviousPrescription);
        records.Dispose();
    }

    [TestMethod]
    public async Task Repeat_WithTheIngredientHidden_AndAProductThatNoLongerResolves_StillWarnsOfTheAllergy()
    {
        var (records, _, patient) = OpenWithPrevious(false, new PatientPrescriptionItem
        {
            MedicineName = "Tempra 500 mg tabletas", ActiveIngredient = "Paracetamol", ShowActiveIngredient = false, Dosage = "1 cada 8 horas"
        });
        patient.Allergies = "Paracetamol";
        Assert.IsNull(records.Catalog.FindCatalogProduct("Tempra 500 mg tabletas"), "El producto ya no está en el catálogo");

        await records.Prescription.RepeatPreviousPrescriptionAsync();
        Assert.IsEmpty(patient.ActivePrescription, "No se repite: choca con la alergia por su principio activo guardado");
        StringAssert.Contains(records.Info.InfoModalMessage, "Tempra 500 mg tabletas");
        Assert.IsTrue(AllergyRules.CheckMedication(patient, null, "Tempra 500 mg tabletas", "Paracetamol").HasConflict);
        Assert.IsFalse(AllergyRules.CheckMedication(patient, null, "Tempra 500 mg tabletas").HasConflict, "Sin él, el nombre solo no avisa");
        records.Dispose();
    }
}
