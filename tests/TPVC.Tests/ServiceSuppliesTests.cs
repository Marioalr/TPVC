using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
/// Pedido del médico del 2026-10-14 (revisión 14 del contrato): en la consulta en curso, los insumos de un servicio se ajustan
/// solo para esa consulta. Cada renglón de la hoja de servicios lleva la lista de lo que cargó; + / −, la X y agregar mueven
/// solo la diferencia en la cuenta, quitar el renglón descuenta exactamente su lista, y la configuración del servicio nunca
/// cambia. Un renglón sin lista (de la v1.39 u otra PC) y uno de una consulta anterior siguen como antes.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ServiceSuppliesTests
{
    private const string GauzeName = "Gasa Estéril 10x10 cm";
    private const string SalineName = "Solución Fisiológica 0.9%";
    private const string BandageName = "Venda Elástica 10 cm";
    private const string SyringeName = "Jeringa Desechable 5 ml con Aguja 21G";
    private const string GlovesName = "Guantes de Nitrilo (Talla M)";

    [TestInitialize]
    public void Setup() => DialogService.ConfirmHandler = (_, _) => true;

    // ================================================================== utilidades

    /// <summary>La curación del médico: una gasa por defecto (la siembra trae dos), con su solución y su venda.</summary>
    private static void SetDefaultQuantity(Fase6Clinic clinic, string serviceCode, string productName, int quantity)
    {
        using var db = new AppDbContext(clinic.Options);
        var service = db.ClinicalServices.Include(s => s.DefaultProducts).ThenInclude(d => d.Product).Single(s => s.Code == serviceCode);
        service.DefaultProducts.Single(d => d.Product!.Name == productName).Quantity = quantity;
        db.SaveChanges();
    }

    private static List<(string Product, int Quantity)> StoredDefaults(Fase6Clinic clinic, string serviceCode)
    {
        using var db = new AppDbContext(clinic.Options);
        return db.ClinicalServices.AsNoTracking().Include(s => s.DefaultProducts).ThenInclude(d => d.Product).Single(s => s.Code == serviceCode)
            .DefaultProducts.OrderBy(d => d.Id).Select(d => (d.Product!.Name, d.Quantity)).ToList();
    }

    private static PatientServiceItem StoredRow(Fase6Clinic clinic, Guid patientSyncId, Guid? rowId)
    {
        using var db = new AppDbContext(clinic.Options);
        var json = db.Patients.AsNoTracking().Single(p => p.SyncId == patientSyncId).ActiveServicesJson;
        return new PatientRecord { SyncId = patientSyncId, ActiveServicesJson = json }.ActiveServices.Single(r => r.RowId == rowId);
    }

    private static Product Product(MedicalRecordsViewModel records, string name) => records.Catalog.AvailableProducts.Single(p => p.Name == name);

    private static PatientServiceSupply Entry(PatientServiceItem row, Product product) =>
        row.Supplies!.Single(e => e.ChargedLineSyncId == product.SyncId);

    /// <summary>El insumo de ese producto que muestra el editor abierto (también la vista previa de un renglón sin lista).</summary>
    private static PatientServiceSupply EditorEntry(MedicalRecordsViewModel records, Product product) =>
        records.ClinicalServices.SupplyEditorEntries.Single(e => e.ChargedLineSyncId == product.SyncId);

    private static int Charged(ConsultationService consultations, int lineId) =>
        consultations.CurrentConsultation!.Items.FirstOrDefault(i => i.ProductId == lineId)?.Quantity ?? 0;

    /// <summary>Un consultorio con la curación de una gasa, la consulta del paciente del guion abierta y la cuenta vacía.</summary>
    private static async Task<(MedicalRecordsViewModel Records, ConsultationService Consultations, ClinicalService Curacion)> OpenConsultationAsync(Fase6Clinic clinic)
    {
        SetDefaultQuantity(clinic, "SERV-002", GauzeName, 1);
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations);
        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        records.ChartPrimaryAction();
        Assert.IsTrue(consultations.HasActiveConsultation);
        Assert.IsEmpty(consultations.CurrentConsultation!.Items, "La cuenta empieza vacía");
        var curacion = records.Catalog.Services.Single(s => s.Code == "SERV-002");
        return (records, consultations, curacion);
    }

    private static async Task<PatientServiceItem> AddServiceAsync(MedicalRecordsViewModel records, ClinicalService service, int units = 1)
    {
        var before = records.SelectedPatient!.ActiveServices.ToList();
        records.ClinicalServices.ServiceModalQuantity = units;
        await records.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(service);
        return records.SelectedPatient!.ActiveServices.Except(before).Single();
    }

    // ================================================================== el pedido del médico

    [TestMethod]
    public async Task Curacion_ChargesOneGauze_AdjustingToTwoChargesOneMore_AndRemovingTheRowUnchargesBoth()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var gauze = Product(records, GauzeName);

        var row = await AddServiceAsync(records, curacion);
        Assert.AreEqual(1, Charged(consultations, gauze.Id), "La curación carga su gasa por defecto");
        Assert.AreEqual(1, Charged(consultations, -curacion.Id));
        CollectionAssert.AreEqual(new[] { GauzeName, SalineName, BandageName }, row.Supplies!.Select(e => e.SupplyName).ToList(),
            "La lista empieza con los insumos por defecto, en el orden de la configuración");
        Assert.AreEqual(1, Entry(row, gauze).Quantity);
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, Entry(row, gauze).ChargedLineKind);
        Assert.AreEqual(gauze.Id, Entry(row, gauze).ChargedLineId);
        StringAssert.StartsWith(row.SuppliesSummary, $"{GauzeName} ×1 · {SalineName} ×1");

        records.ClinicalServices.OpenServicesModal();
        records.ClinicalServices.ToggleServiceSupplies(row);
        Assert.IsTrue(row.IsEditingSupplies && records.ClinicalServices.IsSupplyEditorOpen);
        await records.ClinicalServices.IncreaseServiceSupplyAsync(Entry(row, gauze));
        Assert.AreEqual(2, Entry(row, gauze).Quantity);
        Assert.AreEqual(2, Charged(consultations, gauze.Id), "Ajustar a dos carga solo una más");
        Assert.AreEqual(1, Charged(consultations, -curacion.Id), "El servicio no cambia");
        Assert.AreEqual(2, Entry(StoredRow(clinic, records.SelectedPatient!.SyncId, row.RowId), gauze).Quantity, "La lista ajustada quedó guardada");

        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(row);
        Assert.AreEqual(0, Charged(consultations, gauze.Id), "Quitar el renglón descuenta las dos gasas");
        Assert.IsEmpty(consultations.CurrentConsultation!.Items, "Y el servicio con sus demás insumos: la cuenta queda vacía");
        Assert.IsFalse(records.ClinicalServices.IsSupplyEditorOpen);
        records.Dispose();
    }

    [TestMethod]
    public async Task TheNextCuracion_StartsWithOneGauzeAgain_AndTheServiceConfigurationNeverChanges()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var gauze = Product(records, GauzeName);
        var configured = StoredDefaults(clinic, "SERV-002");

        var first = await AddServiceAsync(records, curacion);
        records.ClinicalServices.ToggleServiceSupplies(first);
        await records.ClinicalServices.IncreaseServiceSupplyAsync(Entry(first, gauze));
        await records.ClinicalServices.RemoveServiceSupplyAsync(first.Supplies!.Single(e => e.SupplyName == BandageName));
        Assert.AreEqual(2, Entry(first, gauze).Quantity);

        var second = await AddServiceAsync(records, curacion);
        Assert.AreEqual(1, Entry(second, gauze).Quantity, "La siguiente curación vuelve a empezar con una gasa");
        Assert.HasCount(3, second.Supplies!, "y con la venda que se quitó de la primera");
        Assert.AreEqual(3, Charged(consultations, gauze.Id));

        CollectionAssert.AreEqual(configured, StoredDefaults(clinic, "SERV-002"), "La configuración del servicio no cambió");
        Assert.AreEqual(1, curacion.DefaultProducts.Single(d => d.Product!.Name == GauzeName).Quantity, "Ni la del catálogo en memoria");
        Assert.HasCount(3, curacion.DefaultProducts);
        records.Dispose();
    }

    [TestMethod]
    public async Task RemovingASupplyUnchargesIt_AndAddingOneChargesIt()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var saline = Product(records, SalineName);
        var syringe = Product(records, SyringeName);
        var row = await AddServiceAsync(records, curacion);
        records.ClinicalServices.ToggleServiceSupplies(row);

        await records.ClinicalServices.RemoveServiceSupplyAsync(Entry(row, saline));
        Assert.AreEqual(0, Charged(consultations, saline.Id), "La X descuenta la solución");
        Assert.IsFalse(row.Supplies!.Any(e => e.SupplyName == SalineName), "y sale de la lista");

        // La búsqueda: solo insumos, con la del Punto de venta
        records.ClinicalServices.ServiceSupplySearchText = "jering";
        Assert.AreEqual(syringe.Id, records.ClinicalServices.ServiceSupplySuggestions.First().Id);
        records.ClinicalServices.ServiceSupplySearchText = "paracet";
        Assert.IsEmpty(records.ClinicalServices.ServiceSupplySuggestions, "Un medicamento no es insumo");
        records.ClinicalServices.ServiceSupplySearchText = "jering";

        await records.ClinicalServices.AddServiceSupplyAsync(null);
        Assert.AreEqual(1, Charged(consultations, syringe.Id), "Agregar carga una pieza");
        Assert.AreEqual(SyringeName, row.Supplies!.Last().SupplyName, "y entra al final de la lista");
        Assert.AreEqual(string.Empty, records.ClinicalServices.ServiceSupplySearchText, "La búsqueda se limpia");

        await records.ClinicalServices.AddServiceSupplyAsync(syringe);
        Assert.AreEqual(2, Entry(row, syringe).Quantity, "Agregar uno que ya tiene suma una pieza");
        Assert.AreEqual(2, Charged(consultations, syringe.Id));
        Assert.AreEqual(1, row.Supplies!.Count(e => e.SupplyName == SyringeName));

        await records.ClinicalServices.DecreaseServiceSupplyAsync(Entry(row, syringe));
        Assert.AreEqual(1, Charged(consultations, syringe.Id), "− descuenta una");

        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(row);
        Assert.IsEmpty(consultations.CurrentConsultation!.Items, "Quitar el renglón descuenta su lista tal como quedó (con la jeringa, sin la solución)");
        records.Dispose();
    }

    [TestMethod]
    public async Task Plus_AndAdd_PassTheStockWithAWarning_LikeTheSuppliesSheet()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var bandage = Product(records, BandageName);
        var gloves = Product(records, GlovesName);
        var row = await AddServiceAsync(records, curacion);
        records.ClinicalServices.ToggleServiceSupplies(row);

        // La venda tiene 4 en existencia; la de la curación ya cuenta
        for (int i = 0; i < 3; i++) await records.ClinicalServices.IncreaseServiceSupplyAsync(Entry(row, bandage));
        Assert.AreEqual(4, Charged(consultations, bandage.Id));
        Assert.IsFalse(records.Info.IsInfoModalVisible, records.Info.InfoModalMessage);
        // Revisión 20 (un inventario mal contado nunca impide cobrar): pasa de sus existencias y se avisa
        await records.ClinicalServices.IncreaseServiceSupplyAsync(Entry(row, bandage));
        Assert.AreEqual(5, Charged(consultations, bandage.Id), "Pasa de sus existencias");
        Assert.AreEqual(5, Entry(row, bandage).Quantity);
        Assert.IsTrue(records.Info.IsInfoModalVisible);
        StringAssert.Contains(records.Info.InfoModalMessage, "Existencias insuficientes: ");
        StringAssert.Contains(records.Info.InfoModalMessage, "(quedan 4, se cobran 5). Revise el inventario.");
        records.Info.IsInfoModalVisible = false;

        // Lo que ya está en la cuenta por otra hoja también cuenta: los guantes (2) cargados en la hoja de insumos
        records.Supplies.SupplyQuantity = 2;
        await records.Supplies.SelectAndAddSupplyProductAsync(gloves);
        Assert.AreEqual(2, Charged(consultations, gloves.Id));
        await records.ClinicalServices.AddServiceSupplyAsync(gloves);
        Assert.IsTrue(records.Info.IsInfoModalVisible, "Se agrega y se avisa");
        Assert.AreEqual(3, Charged(consultations, gloves.Id));
        Assert.IsTrue(row.Supplies!.Any(e => e.SupplyName == GlovesName));
        records.Dispose();
    }

    [TestMethod]
    public async Task Units_PlusAddsOneUnitOfDefaults_MinusRemovesOne_AndManualAdjustmentsStay()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var gauze = Product(records, GauzeName);
        var saline = Product(records, SalineName);
        var bandage = Product(records, BandageName);
        var row = await AddServiceAsync(records, curacion);
        records.ClinicalServices.ToggleServiceSupplies(row);
        await records.ClinicalServices.IncreaseServiceSupplyAsync(Entry(row, gauze));
        await records.ClinicalServices.RemoveServiceSupplyAsync(Entry(row, bandage));

        await records.ClinicalServices.IncreaseServiceUnitsAsync(row);
        Assert.AreEqual(2, row.Quantity);
        Assert.AreEqual(2, row.ChargedUnits);
        Assert.AreEqual(2, Charged(consultations, -curacion.Id));
        Assert.AreEqual(3, Entry(row, gauze).Quantity, "Una gasa más por la unidad; la ajustada se queda");
        Assert.AreEqual(2, Entry(row, saline).Quantity);
        Assert.AreEqual(1, Entry(row, bandage).Quantity, "La venda de la unidad nueva vuelve, al final");
        Assert.AreEqual(BandageName, row.Supplies!.Last().SupplyName);
        Assert.AreEqual(3, Charged(consultations, gauze.Id));
        Assert.AreEqual(1, Charged(consultations, bandage.Id));

        await records.ClinicalServices.DecreaseServiceUnitsAsync(row);
        Assert.AreEqual(1, row.Quantity);
        Assert.AreEqual(1, Charged(consultations, -curacion.Id));
        Assert.AreEqual(2, Entry(row, gauze).Quantity);
        Assert.AreEqual(1, Entry(row, saline).Quantity);
        Assert.IsFalse(row.Supplies!.Any(e => e.SupplyName == BandageName), "Un insumo que llega a 0 sale de la lista");
        Assert.AreEqual(0, Charged(consultations, bandage.Id));

        await records.ClinicalServices.DecreaseServiceUnitsAsync(row);
        Assert.DoesNotContain(row, records.SelectedPatient!.ActiveServices, "En 0 unidades se quita");
        Assert.IsEmpty(consultations.CurrentConsultation!.Items);
        records.Dispose();
    }

    // ================================================================== lo que sigue como antes

    [TestMethod]
    public async Task ARowFromAnEarlierConsultation_ShowsItsList_ButIsReadOnly_AndNeverTouchesTodaysAccount()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var gauze = Product(records, GauzeName);
        var today = await AddServiceAsync(records, curacion);

        var earlier = new PatientServiceItem
        {
            ServiceId = curacion.Id, ServiceSyncId = curacion.SyncId, ServiceCode = curacion.Code, ServiceName = curacion.Name, Price = curacion.Price,
            Quantity = 1, ChargedConsultationFolio = "CNS-20261001-090000", ChargedUnits = 1,
            Supplies = new List<PatientServiceSupply> { ServiceRowSupplies.NewEntry(gauze, 3) }
        };
        records.SelectedPatient!.ActiveServices = new List<PatientServiceItem>(records.SelectedPatient.ActiveServices) { earlier };
        records.ClinicalServices.OpenServicesModal();
        Assert.IsTrue(earlier.IsFromEarlierConsultation);
        Assert.IsFalse(today.IsFromEarlierConsultation);
        Assert.AreEqual($"{GauzeName} ×3", earlier.SuppliesSummary, "Su lista se ve");
        var before = consultations.CurrentConsultation!.Items.Select(i => (i.ProductId, i.Quantity)).ToList();

        records.ClinicalServices.ToggleServiceSupplies(earlier);
        Assert.IsFalse(records.ClinicalServices.IsSupplyEditorOpen, "No se abren sus insumos");
        records.ClinicalServices.SupplyEditorRow = earlier;
        await records.ClinicalServices.IncreaseServiceSupplyAsync(earlier.Supplies![0]);
        await records.ClinicalServices.AddServiceSupplyAsync(gauze);
        await records.ClinicalServices.IncreaseServiceUnitsAsync(earlier);
        Assert.AreEqual(3, earlier.Supplies![0].Quantity, "Ni con el editor forzado cambia");
        Assert.AreEqual(1, earlier.Quantity);
        records.ClinicalServices.CloseSupplyEditor();

        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(earlier);
        CollectionAssert.AreEqual(before, consultations.CurrentConsultation!.Items.Select(i => (i.ProductId, i.Quantity)).ToList(),
            "Quitarlo no descuenta nada de la cuenta de hoy");
        records.Dispose();
    }

    [TestMethod]
    public async Task ARowWithoutAList_FromV139_KeepsTheOldBehavior_AndGetsItsListOnlyWhenAdjusted()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var gauze = Product(records, GauzeName);

        // Una v1.39 cargó la curación (dos unidades) sin lista
        var row = await AddServiceAsync(records, curacion, units: 2);
        Assert.AreEqual(2, Charged(consultations, gauze.Id));
        row.Supplies = null;
        records.SelectedPatient!.SyncServicesJson();
        StringAssert.DoesNotMatch(records.SelectedPatient.ActiveServicesJson, new System.Text.RegularExpressions.Regex("Supplies"),
            "Sin lista no se escribe");

        // Abrir los insumos muestra lo que descontaría quitarlo, sin dárselo al renglón; cerrar sin cambios lo deja como estaba
        records.ClinicalServices.ToggleServiceSupplies(row);
        Assert.AreEqual(2, EditorEntry(records, gauze).Quantity, "Los insumos por defecto de hoy por sus unidades");
        Assert.IsNull(row.Supplies, "La vista previa no es del renglón");
        records.ClinicalServices.CloseSupplyEditor();
        Assert.IsNull(row.Supplies, "Sin cambios sigue sin lista");

        // Quitarlo descuenta como antes: el servicio y los insumos por defecto de hoy por sus unidades
        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(row);
        Assert.IsEmpty(consultations.CurrentConsultation!.Items);

        // Otro renglón sin lista que sí se ajusta: desde ahí la lista es lo que cargó
        var adjusted = await AddServiceAsync(records, curacion);
        adjusted.Supplies = null;
        records.ClinicalServices.ToggleServiceSupplies(adjusted);
        await records.ClinicalServices.IncreaseServiceSupplyAsync(EditorEntry(records, gauze));
        Assert.AreSame(adjusted.Supplies, records.ClinicalServices.SupplyEditorEntries, "Con el primer cambio, la lista pasa al renglón");
        records.ClinicalServices.CloseSupplyEditor();
        Assert.AreEqual(2, Entry(adjusted, gauze).Quantity, "Ya tiene su lista");
        Assert.AreEqual(2, Charged(consultations, gauze.Id));
        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(adjusted);
        Assert.IsEmpty(consultations.CurrentConsultation!.Items, "Y se descuenta por ella");
        records.Dispose();
    }

    [TestMethod]
    public async Task ThePreviewOfARowWithoutAList_IsNeverSaved_WhenThePatientIsSavedForAnotherReason()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var gauze = Product(records, GauzeName);
        var other = await AddServiceAsync(records, curacion);
        var legacy = await AddServiceAsync(records, curacion);
        legacy.Supplies = null;
        records.SelectedPatient!.SyncServicesJson();
        await clinic.Inventory.SavePatientAsync(records.SelectedPatient);
        var patientSyncId = records.SelectedPatient.SyncId;
        string StoredStamp()
        {
            using var db = new AppDbContext(clinic.Options);
            var stamps = PatientFieldStamps.Parse(db.Patients.AsNoTracking().Single(p => p.SyncId == patientSyncId).FieldStampsJson);
            return stamps[PatientFieldStamps.RowKey(PatientSheetRows.ServicesColumn, legacy.RowId!.Value)] ?? string.Empty;
        }
        var stampBefore = StoredStamp();
        Assert.IsNull(StoredRow(clinic, patientSyncId, legacy.RowId).Supplies);

        records.ClinicalServices.OpenServicesModal();
        records.ClinicalServices.ToggleServiceSupplies(legacy);
        Assert.IsNotEmpty(records.ClinicalServices.SupplyEditorEntries, "Se ve la vista previa");

        // Con los insumos del renglón viejo abiertos: una unidad más en otro renglón y un insumo en la hoja de insumos
        await records.ClinicalServices.IncreaseServiceUnitsAsync(other);
        Assert.AreEqual(2, other.Quantity);
        await records.Supplies.SelectAndAddSupplyProductAsync(gauze);

        Assert.IsNull(legacy.Supplies, "El renglón viejo sigue sin lista en memoria");
        Assert.IsNull(StoredRow(clinic, patientSyncId, legacy.RowId).Supplies, "y en la base");
        Assert.AreEqual(stampBefore, StoredStamp(), "Su renglón no cuenta como cambiado (8.22)");
        Assert.IsTrue(legacy.IsEditingSupplies, "Sus insumos siguen abiertos");

        records.ClinicalServices.CloseSupplyEditor();
        Assert.IsNull(legacy.Supplies);
        Assert.AreEqual(stampBefore, StoredStamp());
        records.Dispose();
    }

    [TestMethod]
    public async Task UnitChanges_RedrawTheOpenEditor_WithANewListInstance()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var bandage = Product(records, BandageName);
        var row = await AddServiceAsync(records, curacion);
        records.ClinicalServices.OpenServicesModal();
        records.ClinicalServices.ToggleServiceSupplies(row);
        await records.ClinicalServices.RemoveServiceSupplyAsync(EditorEntry(records, bandage));
        Assert.IsFalse(records.ClinicalServices.SupplyEditorEntries.Any(e => e.SupplyName == BandageName));

        var changes = new List<string?>();
        records.ClinicalServices.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var before = row.Supplies;
        await records.ClinicalServices.IncreaseServiceUnitsAsync(row);
        Assert.AreNotSame(before, row.Supplies, "Otra instancia: WPF ignora la misma lista asignada otra vez");
        Assert.AreSame(row.Supplies, records.ClinicalServices.SupplyEditorEntries);
        CollectionAssert.Contains(changes, nameof(records.ClinicalServices.SupplyEditorEntries), "El editor se vuelve a dibujar");
        Assert.AreEqual(1, EditorEntry(records, bandage).Quantity, "La venda de la unidad nueva aparece en el editor");

        changes.Clear();
        before = row.Supplies;
        await records.ClinicalServices.DecreaseServiceUnitsAsync(row);
        Assert.AreNotSame(before, row.Supplies);
        CollectionAssert.Contains(changes, nameof(records.ClinicalServices.SupplyEditorEntries));
        Assert.IsFalse(records.ClinicalServices.SupplyEditorEntries.Any(e => e.SupplyName == BandageName),
            "Un insumo en 0 sale del editor (no se queda en ×0 con botones que no hacen nada)");
        Assert.AreEqual(0, Charged(consultations, bandage.Id));
        records.Dispose();
    }

    [TestMethod]
    public async Task ARowOfThisConsultationWithZeroChargedUnits_StillUnchargesItsList()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var gauze = Product(records, GauzeName);
        var syringe = Product(records, SyringeName);
        var row = await AddServiceAsync(records, curacion);
        row.ChargedUnits = 0;
        int gauzeBefore = Charged(consultations, gauze.Id);
        records.ClinicalServices.ToggleServiceSupplies(row);
        await records.ClinicalServices.AddServiceSupplyAsync(syringe);
        Assert.AreEqual(1, Charged(consultations, syringe.Id));

        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(row);
        Assert.AreEqual(0, Charged(consultations, syringe.Id), "Lo que cargó su lista se descuenta aunque diga 0 unidades");
        Assert.AreEqual(gauzeBefore - 1, Charged(consultations, gauze.Id));
        records.Dispose();
    }

    // ================================================================== la cuenta, el cobro y los lotes

    [TestMethod]
    public async Task TheCheckout_ChargesTheAdjustedQuantities_WithOneMovementPerLot()
    {
        using var clinic = new Fase6Clinic();
        var (records, consultations, curacion) = await OpenConsultationAsync(clinic);
        var gauze = Product(records, GauzeName);
        var syringe = Product(records, SyringeName);
        var row = await AddServiceAsync(records, curacion);
        records.ClinicalServices.ToggleServiceSupplies(row);
        await records.ClinicalServices.IncreaseServiceSupplyAsync(Entry(row, gauze));
        await records.ClinicalServices.IncreaseServiceSupplyAsync(Entry(row, gauze));
        await records.ClinicalServices.AddServiceSupplyAsync(syringe);
        records.ClinicalServices.CloseServicesModal();
        Assert.IsFalse(records.ClinicalServices.IsSupplyEditorOpen, "Cerrar la hoja cierra los insumos");

        var expectedTotal = records.Account.ConsultationFeeAmount + consultations.CurrentConsultation!.Items.Sum(i => i.UnitPrice * i.Quantity);
        Assert.AreEqual(expectedTotal, records.Account.ConsultationAccountTotal, "La cuenta de la consulta suma lo ajustado");
        int gauzeStock, syringeStock;
        using (var db = new AppDbContext(clinic.Options))
        {
            gauzeStock = db.Products.Single(p => p.Id == gauze.Id).CurrentStock;
            syringeStock = db.Products.Single(p => p.Id == syringe.Id).CurrentStock;
        }

        records.Consultation.ConsultationDiagnosis = "T14.1 Herida de región no especificada";
        await records.Consultation.FinalizeConsultationAsync();
        Assert.IsTrue(records.Consultation.IsFinishConsultationOpen);
        records.Consultation.Checkout.SelectedPaymentMethod = "Efectivo";
        records.Consultation.Checkout.CashReceived = 5000m;
        await records.Consultation.Checkout.ChargeAsync();
        for (int i = 0; i < 100 && consultations.HasActiveConsultation; i++) await Task.Delay(50);
        Assert.IsFalse(consultations.HasActiveConsultation, "La consulta se cobró");

        using var check = new AppDbContext(clinic.Options);
        var sale = check.Sales.Include(s => s.Items).OrderByDescending(s => s.Id).First();
        Assert.AreEqual(3, sale.Items.Single(i => i.ProductId == gauze.Id).Quantity, "El ticket lleva las tres gasas");
        Assert.AreEqual(1, sale.Items.Single(i => i.ProductId == syringe.Id).Quantity);
        Assert.AreEqual(1, sale.Items.Single(i => i.ClinicalServiceId == curacion.Id || (i.IsService && i.Description == curacion.Name)).Quantity);
        Assert.AreEqual(gauzeStock - 3, check.Products.Single(p => p.Id == gauze.Id).CurrentStock);
        Assert.AreEqual(syringeStock - 1, check.Products.Single(p => p.Id == syringe.Id).CurrentStock);

        // Paso 8.19: un movimiento por lote con su SaleItem, que suman lo vendido
        var gauzeItem = sale.Items.Single(i => i.ProductId == gauze.Id);
        var movements = check.InventoryMovements.Where(m => m.SaleItemId == gauzeItem.Id).ToList();
        Assert.IsNotEmpty(movements);
        Assert.AreEqual(-3, movements.Sum(m => m.Delta));
        Assert.AreEqual(movements.Count, movements.Select(m => m.BatchId).Distinct().Count(), "Uno por lote");
        records.Dispose();
    }

    // ================================================================== consulta entre equipos

    /// <summary>En la tablet, sus productos y servicios empiezan en 5001 y 7001: ningún Id local coincide con los de la PC.</summary>
    private static void ShiftLocalIds(Fase6Clinic clinic)
    {
        using var db = new AppDbContext(clinic.Options);
        db.Database.ExecuteSqlRaw("DELETE FROM sqlite_sequence WHERE name IN ('Products', 'ClinicalServices')");
        db.Database.ExecuteSqlRaw("INSERT INTO sqlite_sequence (name, seq) VALUES ('Products', 5000), ('ClinicalServices', 7000)");
    }

    [TestMethod]
    public async Task AReceivedConsultation_TranslatesTheListToTheLocalIds_AndAdjustingOrRemovingHitsTheRightLines()
    {
        using var pc = new Fase6Clinic();
        SetDefaultQuantity(pc, "SERV-002", GauzeName, 1);
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        ShiftLocalIds(tablet);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success, "La PC ve la tablet registrada");

        var pcConsultations = new ConsultationService(seedDemoPatients: false);
        var pcRecords = await pc.OpenAsync(pcConsultations);
        var tabletConsultations = new ConsultationService(seedDemoPatients: false);
        var tabletRecords = await tablet.OpenAsync(tabletConsultations);

        // ------------------------------------------------ en la PC: la curación con dos gasas y una jeringa
        pcRecords.OpenPatientChart(Fase6Scenarios.ScriptedPatient(pcRecords));
        pcRecords.ChartPrimaryAction();
        var pcGauze = Product(pcRecords, GauzeName);
        var pcSyringe = Product(pcRecords, SyringeName);
        var pcCuracion = pcRecords.Catalog.Services.Single(s => s.Code == "SERV-002");
        var pcRow = await AddServiceAsync(pcRecords, pcCuracion);
        pcRecords.ClinicalServices.ToggleServiceSupplies(pcRow);
        await pcRecords.ClinicalServices.IncreaseServiceSupplyAsync(Entry(pcRow, pcGauze));
        await pcRecords.ClinicalServices.AddServiceSupplyAsync(pcSyringe);
        pcRecords.ClinicalServices.CloseServicesModal();
        Assert.AreEqual(pcGauze.Id, Entry(pcRow, pcGauze).ChargedLineId);

        await Task.Delay(200);
        pcRecords.Autosave.SaveConsultationDraftNow();
        await pcRecords.Handoff.OpenSendConsultationSheetAsync();
        pcRecords.Handoff.SelectedHandoffDevice = pcRecords.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-tablet-sala");
        await pcRecords.Handoff.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(pcRecords.Handoff.IsConsultationHandedOff, pcRecords.Handoff.HandoffSheetError ?? pcRecords.Info.InfoModalMessage);

        // ------------------------------------------------ la tablet la recibe
        await tabletRecords.Handoff.PollHandoffsOnceAsync();
        await tabletRecords.Handoff.AcceptIncomingHandoffAsync();
        Assert.IsTrue(tabletConsultations.HasActiveConsultation, tabletRecords.Info.InfoModalMessage);
        var gauze = tabletRecords.Catalog.AvailableProducts.Single(p => p.SyncId == pcGauze.SyncId);
        var syringe = tabletRecords.Catalog.AvailableProducts.Single(p => p.SyncId == pcSyringe.SyncId);
        var curacion = tabletRecords.Catalog.Services.Single(s => s.SyncId == pcCuracion.SyncId);
        Assert.IsGreaterThan(5000, gauze.Id, "Los productos de la tablet tienen otros Id");
        Assert.IsGreaterThan(7000, curacion.Id);

        var row = tabletRecords.SelectedPatient!.ActiveServices.Single(r => r.RowId == pcRow.RowId);
        Assert.AreEqual(2, Entry(row, gauze).Quantity, "La lista ajustada llegó con el expediente");
        Assert.AreEqual(gauze.Id, Entry(row, gauze).ChargedLineId, "Recibir tradujo cada insumo al Id de la tablet");
        Assert.AreEqual(syringe.Id, Entry(row, syringe).ChargedLineId);
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, Entry(row, gauze).ChargedLineKind);
        Assert.AreEqual(2, Charged(tabletConsultations, gauze.Id), "La cuenta recibida, con el Id de la tablet");
        Assert.AreEqual(0, Charged(tabletConsultations, pcGauze.Id));

        // + / − en la tablet mueven la línea de la tablet
        tabletRecords.Info.IsInfoModalVisible = false;
        tabletRecords.ClinicalServices.OpenServicesModal();
        tabletRecords.ClinicalServices.ToggleServiceSupplies(row);
        Assert.IsTrue(tabletRecords.ClinicalServices.IsSupplyEditorOpen, "Un renglón de la consulta recibida es de esta consulta");
        await tabletRecords.ClinicalServices.IncreaseServiceSupplyAsync(Entry(row, syringe));
        Assert.AreEqual(2, Charged(tabletConsultations, syringe.Id));
        Assert.IsFalse(tabletRecords.Info.IsInfoModalVisible, tabletRecords.Info.InfoModalMessage);
        await tabletRecords.ClinicalServices.DecreaseServiceSupplyAsync(Entry(row, gauze));
        Assert.AreEqual(1, Charged(tabletConsultations, gauze.Id));

        // Quitar el renglón descuenta exactamente su lista: no queda nada de la curación
        await tabletRecords.ClinicalServices.RemoveServiceItemFromPatientAsync(row);
        Assert.IsEmpty(tabletConsultations.CurrentConsultation!.Items, "Cada insumo encontró su línea en la tablet");

        tabletRecords.Dispose();
        pcRecords.Dispose();
    }

    private static void SetProductActive(Fase6Clinic clinic, string name, bool active)
    {
        using var db = new AppDbContext(clinic.Options);
        db.Products.Single(p => p.Name == name).IsActive = active;
        db.SaveChanges();
    }

    [TestMethod]
    public async Task AReceivedDefaultProductThatIsInactiveHere_UnitsPlusChargesItsFreeLine_AndRemovingTheRowLeavesNothing()
    {
        // La venda, insumo por defecto de la curación, está desactivada: la tablet cobra lo recibido como cargo libre
        using var pc = new Fase6Clinic();
        SetDefaultQuantity(pc, "SERV-002", GauzeName, 1);
        SetProductActive(pc, BandageName, false);
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        ShiftLocalIds(tablet);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success);

        var pcConsultations = new ConsultationService(seedDemoPatients: false);
        var pcRecords = await pc.OpenAsync(pcConsultations);
        var tabletConsultations = new ConsultationService(seedDemoPatients: false);
        var tabletRecords = await tablet.OpenAsync(tabletConsultations);

        pcRecords.OpenPatientChart(Fase6Scenarios.ScriptedPatient(pcRecords));
        pcRecords.ChartPrimaryAction();
        var pcCuracion = pcRecords.Catalog.Services.Single(s => s.Code == "SERV-002");
        var pcRow = await AddServiceAsync(pcRecords, pcCuracion);
        Assert.IsTrue(pcRow.Supplies!.Any(e => e.SupplyName == BandageName), "La venda desactivada sigue siendo insumo por defecto");

        await Task.Delay(200);
        pcRecords.Autosave.SaveConsultationDraftNow();
        await pcRecords.Handoff.OpenSendConsultationSheetAsync();
        pcRecords.Handoff.SelectedHandoffDevice = pcRecords.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-tablet-sala");
        await pcRecords.Handoff.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(pcRecords.Handoff.IsConsultationHandedOff, pcRecords.Handoff.HandoffSheetError ?? pcRecords.Info.InfoModalMessage);
        await tabletRecords.Handoff.PollHandoffsOnceAsync();
        await tabletRecords.Handoff.AcceptIncomingHandoffAsync();
        Assert.IsTrue(tabletConsultations.HasActiveConsultation, tabletRecords.Info.InfoModalMessage);

        int tabletBandageId;
        using (var db = new AppDbContext(tablet.Options)) tabletBandageId = db.Products.Single(p => p.Name == BandageName).Id;
        var freeBandage = HandoffPayloadCodec.FreeChargeId(BandageName);
        var row = tabletRecords.SelectedPatient!.ActiveServices.Single(r => r.RowId == pcRow.RowId);
        var bandageEntry = row.Supplies!.Single(e => e.SupplyName == BandageName);
        Assert.AreEqual(freeBandage, bandageEntry.ChargedLineId, "La venda se tradujo al cargo libre de la cuenta recibida");
        Assert.AreEqual(1, Charged(tabletConsultations, freeBandage));

        tabletRecords.ClinicalServices.OpenServicesModal();
        await tabletRecords.ClinicalServices.IncreaseServiceUnitsAsync(row);
        Assert.AreEqual(2, Charged(tabletConsultations, freeBandage), "Una unidad más carga la venda en su cargo libre");
        Assert.AreEqual(0, Charged(tabletConsultations, tabletBandageId), "No en el producto");
        Assert.AreEqual(2, row.Supplies!.Single(e => e.SupplyName == BandageName).Quantity);

        await tabletRecords.ClinicalServices.DecreaseServiceUnitsAsync(row);
        Assert.AreEqual(1, Charged(tabletConsultations, freeBandage), "Una menos la descuenta del mismo renglón");
        Assert.AreEqual(0, Charged(tabletConsultations, tabletBandageId));

        await tabletRecords.ClinicalServices.IncreaseServiceUnitsAsync(row);
        await tabletRecords.ClinicalServices.RemoveServiceItemFromPatientAsync(row);
        Assert.IsEmpty(tabletConsultations.CurrentConsultation!.Items, "Quitar el renglón no deja ninguna venda cobrada");

        tabletRecords.Dispose();
        pcRecords.Dispose();
    }

    private static Dictionary<string, object?> Heartbeat(string id, string platform, string role, int? contractRevision, int handoffProtocol = 1) => new()
    {
        ["DeviceId"] = id, ["DeviceName"] = id, ["DeviceRole"] = role, ["AppVersion"] = "1.39.0", ["Platform"] = platform,
        ["LastSeenUtc"] = TursoSyncService.FormatDate(DateTime.UtcNow.AddMinutes(-1)), ["HandoffProtocol"] = (long)handoffProtocol,
        ["ContractRevision"] = contractRevision is int r ? (long)r : null
    };

    [TestMethod]
    public void ADeviceWithAnOlderContract_IsOfferedWithTheServiceSuppliesWarning()
    {
        var registry = new[] { "tablet-vieja", "tablet-al-dia" }.Select(id => new Dictionary<string, object?>
        {
            ["DeviceId"] = id, ["DisplayName"] = id, ["Role"] = nameof(DeviceRole.AndroidTablet), ["DeviceCode"] = "T1"
        });
        var devices = ConsultationHandoffService.MergeDevices(new[]
        {
            Heartbeat("pc-v139", "Windows", nameof(DeviceRole.WindowsDesktop), SyncContract.ServiceSuppliesRevision - 1),
            Heartbeat("pc-al-dia", "Windows", nameof(DeviceRole.WindowsDesktop), SyncContract.ServiceSuppliesRevision),
            Heartbeat("pc-sin-revision", "Windows", nameof(DeviceRole.WindowsDesktop), null),
            Heartbeat("tablet-vieja", "Android", nameof(DeviceRole.AndroidTablet), SyncContract.ServiceSuppliesRevision - 1),
            Heartbeat("tablet-al-dia", "Android", nameof(DeviceRole.AndroidTablet), SyncContract.Revision),
            Heartbeat("sin-consultas", "Windows", nameof(DeviceRole.WindowsDesktop), 1, handoffProtocol: 0),
            Heartbeat("esta-pc", "Windows", nameof(DeviceRole.WindowsDesktop), SyncContract.ServiceSuppliesRevision - 1),
        }, registry, primaryDeviceId: "esta-pc", DateTime.UtcNow, thisDeviceId: "esta-pc");
        LinkedDevice Device(string id) => devices.Single(d => d.DeviceId == id);

        Assert.IsTrue(Device("pc-v139").IgnoresServiceSupplies);
        StringAssert.Contains(Device("pc-v139").ServiceSuppliesText, "insumos ajustados");
        Assert.IsTrue(Device("pc-sin-revision").IgnoresServiceSupplies, "Sin revisión: una versión anterior");
        Assert.IsTrue(Device("tablet-vieja").IgnoresServiceSupplies, "También una tablet que recibe consultas");
        Assert.IsFalse(Device("pc-al-dia").IgnoresServiceSupplies);
        Assert.IsFalse(Device("tablet-al-dia").IgnoresServiceSupplies);
        Assert.IsFalse(Device("sin-consultas").IgnoresServiceSupplies, "No se le puede enviar la consulta");
        Assert.IsFalse(Device("esta-pc").IgnoresServiceSupplies, "Este equipo no");
        Assert.AreEqual(string.Empty, Device("pc-al-dia").ServiceSuppliesText);
    }

    [TestMethod]
    public void TranslateReceivedRows_TranslatesTheList_AlsoOfALegacyOrFreeServiceRow()
    {
        var syncId = Guid.NewGuid();
        var missing = Guid.NewGuid();
        const string folio = "CNS-20261014-100000";
        var gauze = new Product { Id = 5011, SyncId = syncId, Name = GauzeName };
        var patient = new PatientRecord { SyncId = Guid.NewGuid() };
        var legacyService = new PatientServiceItem
        {
            ServiceId = 2, ServiceName = "Curación Menor y Asepsia", ChargedConsultationFolio = folio, ChargedUnits = 1,
            Supplies = new List<PatientServiceSupply>
            {
                new() { SupplyName = GauzeName, Quantity = 2, ChargedLineKind = HandoffPayloadCodec.LineProduct, ChargedLineSyncId = syncId, ChargedLineId = 11 },
                new() { SupplyName = "Venda de yeso 15 cm", Quantity = 1, ChargedLineKind = HandoffPayloadCodec.LineProduct, ChargedLineSyncId = missing, ChargedLineId = 44 }
            }
        };
        var earlier = new PatientServiceItem
        {
            ServiceName = "Retiro de puntos", ChargedConsultationFolio = "CNS-20261001-090000", ChargedUnits = 1,
            Supplies = new List<PatientServiceSupply> { new() { SupplyName = GauzeName, Quantity = 1, ChargedLineKind = HandoffPayloadCodec.LineProduct, ChargedLineSyncId = syncId, ChargedLineId = 11 } }
        };
        patient.ActiveServices = new List<PatientServiceItem> { legacyService, earlier };

        // La cuenta recibida, ya traducida: la gasa con el Id de aquí y la venda (no existe aquí) como cargo libre de su nombre
        var received = new List<ConsultationProductItem>
        {
            new() { ProductId = 5011, Name = GauzeName, LineKind = HandoffPayloadCodec.LineProduct, CatalogSyncId = syncId, Quantity = 2 },
            new() { ProductId = HandoffPayloadCodec.FreeChargeId("Venda de yeso 15 cm (Rollo)"), Name = "Venda de yeso 15 cm (Rollo)", LineKind = HandoffPayloadCodec.LineProduct, CatalogSyncId = missing, Quantity = 1 }
        };

        int changed = PatientSheetRows.TranslateReceivedRows(patient, folio, received, new[] { gauze }, null, Array.Empty<ClinicalService>());
        Assert.AreEqual(2, changed);
        Assert.AreEqual(5011, legacyService.Supplies[0].ChargedLineId, "Aunque el servicio sea un renglón viejo, su lista es del formato nuevo");
        Assert.AreEqual(HandoffPayloadCodec.FreeChargeId("Venda de yeso 15 cm (Rollo)"), legacyService.Supplies[1].ChargedLineId,
            "Un producto que aquí no existe apunta al cargo libre de la cuenta recibida, aunque se llame distinto");
        Assert.AreEqual(missing, legacyService.Supplies[1].ChargedLineSyncId, "La clase y el SyncId no cambian");
        Assert.AreEqual(2, legacyService.ServiceId, "Un servicio viejo conserva su Id");
        Assert.AreEqual(11, earlier.Supplies![0].ChargedLineId, "Lo de otra consulta no se toca");
    }

    // ================================================================== la combinación por renglón (8.22)

    private static readonly Guid MergePatientId = new("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly Guid RowA = new("a0000000-0000-4000-8000-00000000000a");
    private static readonly Guid RowB = new("a0000000-0000-4000-8000-00000000000b");
    private static readonly Guid GauzeSync = new("a1b2c3d4-0000-4000-8000-000000000001");
    private static readonly Guid SyringeSync = new("a1b2c3d4-0000-4000-8000-000000000005");
    private static readonly string[] MergeColumns = { nameof(PatientRecord.Phone), PatientSheetRows.ServicesColumn };

    private static string ServicesJson(params (Guid RowId, (Guid SyncId, string Name, int Pieces, int LocalId)[] Supplies)[] rows)
    {
        var patient = new PatientRecord { SyncId = MergePatientId };
        patient.ActiveServices = rows.Select(r => new PatientServiceItem
        {
            RowId = r.RowId, ServiceName = "Curación Menor y Asepsia", Price = 250m, ChargedConsultationFolio = "CNS-20261014-100000", ChargedUnits = 1,
            Supplies = r.Supplies.Select(s => new PatientServiceSupply
            {
                SupplyName = s.Name, Quantity = s.Pieces, UnitPrice = 10m, ChargedLineKind = HandoffPayloadCodec.LineProduct, ChargedLineSyncId = s.SyncId,
                ChargedLineId = s.LocalId
            }).ToList()
        }).ToList();
        return patient.ActiveServicesJson;
    }

    private static PatientMerge.Version MergeVersion(DateTime updated, string stamps, string services) => new()
    {
        SyncId = MergePatientId, UpdatedAtUtc = updated, FieldStampsJson = stamps,
        Values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [nameof(PatientRecord.Phone)] = JsonValue.Create(string.Empty),
            [PatientSheetRows.ServicesColumn] = JsonValue.Create(services)
        }
    };

    [TestMethod]
    public void TheMerge_CarriesEachListWithItsRow_AndALocalIdIsNotAChange()
    {
        var fields = PatientMerge.FieldColumns(MergeColumns);
        var t0 = new DateTime(2026, 10, 14, 15, 0, 0);
        var common = ServicesJson((RowA, new[] { (GauzeSync, GauzeName, 1, 11) }), (RowB, new[] { (GauzeSync, GauzeName, 1, 11) }));
        var baseStamps = PatientFieldStamps.ApplyLocalWrite(null, null, t0, Array.Empty<string>(),
            new[] { (PatientSheetRows.ServicesColumn, (string?)null, (string?)common) }, MergePatientId, fields).ToJson();

        // En la PC: dos gasas en la curación A. En la tablet (sus Id locales): una jeringa en la curación B
        var pcSheet = ServicesJson((RowA, new[] { (GauzeSync, GauzeName, 2, 11) }), (RowB, new[] { (GauzeSync, GauzeName, 1, 11) }));
        var pcStamps = PatientFieldStamps.ApplyLocalWrite(baseStamps, t0, t0.AddSeconds(20), Array.Empty<string>(),
            new[] { (PatientSheetRows.ServicesColumn, (string?)common, (string?)pcSheet) }, MergePatientId, fields);
        Assert.AreEqual(PatientFieldStamps.Format(t0.AddSeconds(20)), pcStamps[PatientFieldStamps.RowKey(PatientSheetRows.ServicesColumn, RowA)], "Cambiar la lista marca su renglón");
        Assert.AreNotEqual(PatientFieldStamps.Format(t0.AddSeconds(20)), pcStamps[PatientFieldStamps.RowKey(PatientSheetRows.ServicesColumn, RowB)], "y solo ese");

        var tabletSheet = ServicesJson((RowA, new[] { (GauzeSync, GauzeName, 1, 5011) }), (RowB, new[] { (GauzeSync, GauzeName, 1, 5011), (SyringeSync, SyringeName, 1, 5016) }));
        var tabletStamps = PatientFieldStamps.ApplyLocalWrite(baseStamps, t0, t0.AddSeconds(30), Array.Empty<string>(),
            new[] { (PatientSheetRows.ServicesColumn, (string?)common, (string?)tabletSheet) }, MergePatientId, fields);
        Assert.AreNotEqual(PatientFieldStamps.Format(t0.AddSeconds(30)), tabletStamps[PatientFieldStamps.RowKey(PatientSheetRows.ServicesColumn, RowA)],
            "Los Id locales de la tablet (traducidos al recibir) no son un cambio del renglón A");

        var pc = MergeVersion(t0.AddSeconds(20), pcStamps.ToJson(), pcSheet);
        var tabletVersion = MergeVersion(t0.AddSeconds(30), tabletStamps.ToJson(), tabletSheet);
        foreach (var (local, remote) in new[] { (pc, tabletVersion), (tabletVersion, pc) })
        {
            var merged = PatientMerge.Merge(local, remote, MergeColumns);
            var rows = new PatientRecord { SyncId = MergePatientId, ActiveServicesJson = CanonicalJson.AsString(merged.Values[PatientSheetRows.ServicesColumn]) ?? "" }.ActiveServices;
            var a = rows.Single(r => r.RowId == RowA);
            var b = rows.Single(r => r.RowId == RowB);
            Assert.AreEqual(2, a.Supplies!.Single().Quantity, "La lista de A es la de la PC");
            CollectionAssert.AreEqual(new[] { GauzeName, SyringeName }, b.Supplies!.Select(e => e.SupplyName).ToList(), "La de B, la de la tablet");
        }
    }

    [TestMethod]
    public void TheCanonicalRow_IncludesTheList_WithoutTheLocalIdOfEachSupply()
    {
        var row = new PatientServiceItem
        {
            RowId = RowA, ServiceName = "Curación", Supplies = new List<PatientServiceSupply>
            {
                new() { SupplyName = GauzeName, Quantity = 2, UnitPrice = 18m, ChargedLineKind = HandoffPayloadCodec.LineProduct, ChargedLineSyncId = GauzeSync, ChargedLineId = 11 }
            }
        };
        var text = CanonicalJson.Write(PatientSheetCanonical.CanonicalRow(PatientSheetRows.ServicesColumn, row));
        StringAssert.Contains(text, $"\"Supplies\":[{{\"ChargedLineKind\":\"producto\",\"ChargedLineSyncId\":\"{SyncIds.Format(GauzeSync)}\",\"Quantity\":2,\"SupplyName\":\"{GauzeName}\",\"UnitPrice\":18}}]");

        row.Supplies[0].ChargedLineId = 5011;
        Assert.AreEqual(text, CanonicalJson.Write(PatientSheetCanonical.CanonicalRow(PatientSheetRows.ServicesColumn, row)), "Otro Id local, el mismo significado");
        row.Supplies[0].Quantity = 3;
        Assert.AreNotEqual(text, CanonicalJson.Write(PatientSheetCanonical.CanonicalRow(PatientSheetRows.ServicesColumn, row)), "Otras piezas, otro renglón");
        row.Supplies = null;
        StringAssert.Contains(CanonicalJson.Write(PatientSheetCanonical.CanonicalRow(PatientSheetRows.ServicesColumn, row)), "\"Supplies\":null");
    }

    // ================================================================== JsonExtensionData

    /// <summary>El renglón de servicio como lo lee una PC v1.39: sin <c>Supplies</c>, con lo que no conoce en ExtensionData.</summary>
    private sealed class V139ServiceRow
    {
        public Guid? RowId { get; set; }
        public int ServiceId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ServiceSyncId { get; set; }
        public string ServiceName { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public int Quantity { get; set; } = 1;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? ChargedConsultationFolio { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ChargedUnits { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    [TestMethod]
    public void TheList_SurvivesARewriteByAV139Pc_AndUnknownFieldsOfASupplySurviveThisPc()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var patient = new PatientRecord { SyncId = MergePatientId };
        patient.ActiveServices = new List<PatientServiceItem>
        {
            new()
            {
                RowId = RowA, ServiceName = "Curación Menor y Asepsia", Price = 250m, ChargedConsultationFolio = "CNS-20261014-100000", ChargedUnits = 1,
                Supplies = new List<PatientServiceSupply>
                {
                    new() { SupplyName = GauzeName, Quantity = 2, UnitPrice = 18m, ChargedLineKind = HandoffPayloadCodec.LineProduct, ChargedLineSyncId = GauzeSync, ChargedLineId = 11 }
                }
            }
        };
        var written = patient.ActiveServicesJson;

        // Una PC v1.39 lee la hoja, cambia las notas de otro campo que conoce y la vuelve a escribir
        var old = JsonSerializer.Deserialize<List<V139ServiceRow>>(written, options)!;
        Assert.IsTrue(old[0].ExtensionData!.ContainsKey("Supplies"), "La v1.39 no conoce la lista: la guarda aparte");
        old[0].Price = 260m;
        var rewrittenByOld = JsonSerializer.Serialize(old, options);

        var back = new PatientRecord { SyncId = MergePatientId, ActiveServicesJson = rewrittenByOld }.ActiveServices.Single();
        Assert.AreEqual(260m, back.Price);
        Assert.AreEqual(2, back.Supplies!.Single().Quantity, "La lista sobrevive a la reescritura de la v1.39");
        Assert.AreEqual(GauzeSync, back.Supplies!.Single().ChargedLineSyncId);

        // Un equipo más nuevo agrega un campo a un insumo: esta PC lo conserva al reescribir
        var newer = rewrittenByOld.Replace("\"UnitPrice\":18", "\"UnitPrice\":18,\"Lote\":{\"Id\":\"L-7\"}");
        var again = new PatientRecord { SyncId = MergePatientId, ActiveServicesJson = newer };
        again.SyncServicesJson();
        StringAssert.Contains(again.ActiveServicesJson, "\"Lote\":{\"Id\":\"L-7\"}");
    }
}
