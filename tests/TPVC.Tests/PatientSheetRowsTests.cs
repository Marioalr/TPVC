using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;
using TPVC.Tests.Fase6;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.18 (A4): las hojas del paciente con identificadores que valen en cualquier equipo. Con dos consultorios de
/// prueba que tienen Id locales distintos, una consulta recibida de la PC mueve con + / − y la X el renglón correcto de
/// la cuenta; los campos de un equipo más nuevo sobreviven a una reescritura; y el mismo renglón viejo recibe el mismo
/// RowId en los dos equipos.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PatientSheetRowsTests
{
    [TestInitialize]
    public void Setup() => DialogService.ConfirmHandler = (_, _) => true;

    /// <summary>
    /// En la tablet, antes de bajar nada: sus productos y servicios empiezan en 5001 y 7001, así ningún Id local
    /// coincide con los de la PC (sembrada desde 1).
    /// </summary>
    private static void ShiftLocalIds(Fase6Clinic clinic)
    {
        using var db = new AppDbContext(clinic.Options);
        db.Database.ExecuteSqlRaw("DELETE FROM sqlite_sequence WHERE name IN ('Products', 'ClinicalServices')");
        db.Database.ExecuteSqlRaw("INSERT INTO sqlite_sequence (name, seq) VALUES ('Products', 5000), ('ClinicalServices', 7000)");
    }

    private static PatientRecord StoredPatient(Fase6Clinic clinic, Guid syncId)
    {
        using var db = new AppDbContext(clinic.Options);
        return db.Patients.AsNoTracking().Single(p => p.SyncId == syncId);
    }

    // ================================================================== consulta recibida con otros Id

    [TestMethod]
    public async Task ReceivedConsultation_PlusMinusAndRemove_MoveTheRightLines_WithOtherLocalIds()
    {
        using var pc = new Fase6Clinic();
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        ShiftLocalIds(tablet);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success, "La PC ve la tablet registrada");

        var pcConsultations = new ConsultationService(seedDemoPatients: false);
        var pcRecords = await pc.OpenAsync(pcConsultations);
        var tabletConsultations = new ConsultationService(seedDemoPatients: false);
        var tabletRecords = await tablet.OpenAsync(tabletConsultations);

        // ------------------------------------------------ en la PC: insumos y servicios del catálogo y libres
        pcRecords.OpenPatientChart(Fase6Scenarios.ScriptedPatient(pcRecords));
        pcRecords.ChartPrimaryAction();
        Assert.IsTrue(pcConsultations.HasActiveConsultation);
        var pcGauze = pcRecords.Catalog.AvailableProducts.First(p => p.Name == "Gasa Estéril 10x10 cm");
        var pcCuracion = pcRecords.Catalog.Services.Single(s => s.Code == "SERV-002");
        const string freeSupplyName = "Férula de aluminio a la medida";
        const string freeServiceName = "Retiro de puntos a domicilio";

        pcRecords.Supplies.SupplyQuantity = 2;
        await pcRecords.Supplies.SelectAndAddSupplyProductAsync(pcGauze);
        pcRecords.Supplies.SupplySearchText = freeSupplyName;
        pcRecords.Supplies.SupplyQuantity = 1;
        await pcRecords.Supplies.SelectAndAddSupplyProductAsync(null);
        pcRecords.ClinicalServices.ServiceModalQuantity = 1;
        await pcRecords.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(pcCuracion);
        pcRecords.ClinicalServices.ServiceModalSearchText = freeServiceName;
        await pcRecords.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(null);

        var pcPatient = pcRecords.SelectedPatient!;
        var pcRow = pcPatient.ActiveSupplies.Single(r => r.ChargedLineSyncId == pcGauze.SyncId);
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, pcRow.ChargedLineKind);
        Assert.AreEqual(pcGauze.Id, pcRow.ChargedLineId, "La PC sigue escribiendo su Id para las PC anteriores");
        var pcFree = pcPatient.ActiveSupplies.Single(r => r.SupplyName == freeSupplyName);
        Assert.AreEqual(HandoffPayloadCodec.LineFree, pcFree.ChargedLineKind);
        Assert.IsNull(pcFree.ChargedLineSyncId);
        Assert.AreEqual(pcCuracion.SyncId, pcPatient.ActiveServices.Single(s => s.ServiceName == pcCuracion.Name).ServiceSyncId);
        Assert.IsNull(pcPatient.ActiveServices.Single(s => s.ServiceName == freeServiceName).ServiceSyncId);
        Assert.IsTrue(pcPatient.ActiveSupplies.Concat<IPatientSheetRow>(pcPatient.ActiveServices).All(r => r.RowId is { } id && id != Guid.Empty),
            "Cada renglón nuevo tiene su RowId");
        var folio = pcConsultations.CurrentConsultation!.Folio;

        // El primer guardado automático es inmediato (otro hilo): se espera y se guarda a mano
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
        Assert.AreEqual(folio, tabletConsultations.CurrentConsultation!.Folio);

        var gauze = tabletRecords.Catalog.AvailableProducts.Single(p => p.SyncId == pcGauze.SyncId);
        var curacion = tabletRecords.Catalog.Services.Single(s => s.SyncId == pcCuracion.SyncId);
        Assert.IsGreaterThan(5000, gauze.Id, "Los productos de la tablet tienen otros Id");
        Assert.IsGreaterThan(7000, curacion.Id, "Los servicios de la tablet tienen otros Id");
        Assert.IsFalse(tabletRecords.Catalog.AvailableProducts.Any(p => p.Id == pcGauze.Id), "El Id de la PC no es de ningún producto de la tablet");

        var patient = tabletRecords.SelectedPatient!;
        var row = patient.ActiveSupplies.Single(r => r.ChargedLineSyncId == pcGauze.SyncId);
        var free = patient.ActiveSupplies.Single(r => r.SupplyName == freeSupplyName);
        var serviceRow = patient.ActiveServices.Single(s => s.ServiceSyncId == pcCuracion.SyncId);
        var freeService = patient.ActiveServices.Single(s => s.ServiceName == freeServiceName);
        Assert.AreEqual(pcRow.RowId, row.RowId, "El mismo renglón tiene el mismo RowId en los dos equipos");

        // Recibir también tradujo las hojas a los Id de la tablet (la clase y el SyncId no cambian)
        Assert.AreEqual(gauze.Id, row.ChargedLineId);
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, row.ChargedLineKind);
        Assert.AreEqual(curacion.Id, serviceRow.ServiceId);

        // Sin esa traducción (lo guardado en la base de la tablet trae los Id de la PC) se encuentra igual por el SyncId,
        // y un renglón de una PC anterior (solo con el Id de aquella) por su nombre
        var stored = new PatientRecord { SyncId = patient.SyncId, ActiveSuppliesJson = StoredPatient(tablet, patient.SyncId).ActiveSuppliesJson }
            .ActiveSupplies.Single(r => r.RowId == row.RowId);
        Assert.AreEqual(pcGauze.Id, stored.ChargedLineId);
        Assert.AreEqual(gauze.Id, tabletRecords.Account.SupplyLineId(stored));
        Assert.AreEqual(gauze.Id, tabletRecords.Account.SupplyLineId(new PatientSupplyItem { SupplyName = row.SupplyName, ChargedLineId = pcGauze.Id }));

        // ------------------------------------------------ + / − y la X en la tablet
        var items = tabletConsultations.CurrentConsultation.Items;
        int Charged(int lineId) => items.FirstOrDefault(i => i.ProductId == lineId)?.Quantity ?? 0;
        int gauzeBefore = Charged(gauze.Id);
        Assert.IsGreaterThanOrEqualTo(2, gauzeBefore, "La gasa del insumo (y la de la curación) llegó con el Id de la tablet");
        Assert.AreEqual(1, Charged(-curacion.Id));

        tabletRecords.Info.IsInfoModalVisible = false;
        await tabletRecords.Supplies.IncreaseSupplyQuantityAsync(row);
        Assert.AreEqual(3, row.Quantity);
        Assert.AreEqual(gauzeBefore + 1, Charged(gauze.Id), "+ carga la gasa de la tablet");
        Assert.AreEqual(0, Charged(pcGauze.Id), "Nada con el Id de la PC");
        Assert.IsFalse(tabletRecords.Info.IsInfoModalVisible, tabletRecords.Info.InfoModalMessage);
        await tabletRecords.Supplies.DecreaseSupplyQuantityAsync(row);
        Assert.AreEqual(2, row.Quantity);
        Assert.AreEqual(gauzeBefore, Charged(gauze.Id), "− la descuenta");

        var freeLine = HandoffPayloadCodec.FreeChargeId(freeSupplyName);
        Assert.AreEqual(1, Charged(freeLine));
        await tabletRecords.Supplies.IncreaseSupplyQuantityAsync(free);
        Assert.AreEqual(2, Charged(freeLine), "El cargo libre se calcula con el nombre en la tablet");

        // La X del servicio descuenta su renglón y sus insumos por defecto con los Id de la tablet
        var afterService = curacion.DefaultProducts.Where(dp => dp.Product != null)
            .ToDictionary(dp => dp.Product!.Id, dp => Charged(dp.Product!.Id) - Math.Max(1, dp.Quantity));
        Assert.IsNotEmpty(afterService);
        await tabletRecords.ClinicalServices.RemoveServiceItemFromPatientAsync(serviceRow);
        Assert.AreEqual(0, Charged(-curacion.Id));
        foreach (var (productId, expected) in afterService) Assert.AreEqual(expected, Charged(productId), $"Insumo por defecto {productId}");
        await tabletRecords.ClinicalServices.RemoveServiceItemFromPatientAsync(freeService);
        Assert.AreEqual(0, Charged(HandoffPayloadCodec.FreeChargeId(freeServiceName)));

        await tabletRecords.Supplies.RemoveSupplyItemFromPatientAsync(row);
        await tabletRecords.Supplies.RemoveSupplyItemFromPatientAsync(free);
        Assert.IsEmpty(items, "Quitar todo de las hojas deja la cuenta sin cargos: cada X encontró su renglón");

        // Lo guardado en la tablet lleva sus Id y conserva la identidad del renglón
        var saved = new PatientRecord { SyncId = patient.SyncId, ActiveSuppliesJson = StoredPatient(tablet, patient.SyncId).ActiveSuppliesJson }.ActiveSupplies;
        Assert.IsFalse(saved.Any(r => r.RowId == row.RowId || r.RowId == free.RowId), "Los renglones quitados ya no están");

        tabletRecords.Dispose();
        pcRecords.Dispose();
    }

    // ================================================================== producto que no existe en quien recibe

    [TestMethod]
    public void Receiving_AProductThatDoesNotExistHere_PointsTheRowToTheFreeLineOfTheAccount()
    {
        var syncId = Guid.NewGuid();
        var patient = new PatientRecord { SyncId = Guid.NewGuid() };
        patient.ActiveSupplies = new List<PatientSupplyItem>
        {
            new() { SupplyName = "Venda de yeso 15 cm (Rollo)", Quantity = 2, ChargedConsultationFolio = "CNS-20261012-100000", ChargedUnits = 2,
                    ChargedLineId = 44, ChargedLineKind = HandoffPayloadCodec.LineProduct, ChargedLineSyncId = syncId },
            new() { SupplyName = "Gasa", Quantity = 1, ChargedConsultationFolio = "CNS-20261001-090000", ChargedUnits = 1, ChargedLineId = 45 }
        };

        // La cuenta recibida: el producto no existe aquí, así que su renglón (que se llama distinto que el de la hoja, porque
        // lo abrió un insumo por defecto) quedó como el cargo libre de su nombre
        var draft = new ConsultationDraft { Folio = "CNS-20261012-100000" };
        draft.Items.Add(new ConsultationProductItem { Name = "Venda de yeso 15 cm", LineKind = HandoffPayloadCodec.LineProduct, CatalogSyncId = syncId, Quantity = 2 });
        CollectionAssert.AreEqual(new[] { "Venda de yeso 15 cm" }, HandoffPayloadCodec.MapReceivedItems(draft, Array.Empty<CatalogRef>()));
        var accountLine = HandoffPayloadCodec.FreeChargeId("Venda de yeso 15 cm");
        Assert.AreEqual(accountLine, draft.Items[0].ProductId);
        Assert.AreNotEqual(accountLine, HandoffPayloadCodec.FreeChargeId("Venda de yeso 15 cm (Rollo)"));

        Assert.AreEqual(1, PatientSheetRows.TranslateReceivedRows(patient, draft.Folio, draft.Items, Array.Empty<Product>(), null, Array.Empty<ClinicalService>()));
        var row = patient.ActiveSupplies[0];
        Assert.AreEqual(accountLine, row.ChargedLineId, "El renglón apunta al cargo libre de la cuenta");
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, row.ChargedLineKind, "La clase y el SyncId no cambian");
        Assert.AreEqual(syncId, row.ChargedLineSyncId);
        var line = PatientSheetRows.ResolveSupplyLine(row, Array.Empty<Product>());
        Assert.AreEqual(accountLine, line.LineId, "+ / − respetan esa traducción");
        PatientSheetRows.RefreshSupplyLine(row, line);
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, row.ChargedLineKind, "+ / − no lo convierten en cargo libre: en los demás equipos sigue siendo el producto");
        Assert.AreEqual(syncId, row.ChargedLineSyncId);
        Assert.AreEqual(45, patient.ActiveSupplies[1].ChargedLineId, "Lo de otra consulta no se toca");
    }

    // ================================================================== renglón de una PC anterior

    [TestMethod]
    public async Task PlusMinus_OnARowFromAnOlderPc_FindsTheProductByName_AndTheRowGetsItsIdentity()
    {
        using var clinic = new Fase6Clinic();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations, drafts: false);
        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        records.ChartPrimaryAction();
        var patient = records.SelectedPatient!;
        var product = records.Catalog.AvailableProducts.First(p => CatalogStore.IsProductSupply(p) && p.CurrentStock >= 3 && p.Price > 0);
        var folio = consultations.CurrentConsultation!.Folio;

        // Como lo dejaría una PC anterior al paso 8.18 que cargó una pieza: solo su Id (aquí no existe)
        Assert.IsTrue(consultations.AddCartItemToActiveConsultation(product.Id, product.Code, product.Name, product.GenericName, product.Presentation, product.Unit, product.Price, 1));
        var row = new PatientSupplyItem
        {
            SupplyName = PatientSheetRows.SupplyDisplayName(product), Quantity = 1, UnitPrice = product.Price,
            ChargedConsultationFolio = folio, ChargedUnits = 1, ChargedLineId = 987_654
        };
        patient.ActiveSupplies = new List<PatientSupplyItem>(patient.ActiveSupplies) { row };
        records.Supplies.OpenSuppliesModal();
        Assert.IsFalse(row.IsFromEarlierConsultation);

        await records.Supplies.IncreaseSupplyQuantityAsync(row);
        var items = consultations.CurrentConsultation.Items;
        Assert.AreEqual(2, items.Single(i => i.ProductId == product.Id).Quantity, "Por nombre: el producto de este equipo");
        Assert.IsFalse(items.Any(i => i.ProductId == 987_654));
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, row.ChargedLineKind, "Al volver a cargarse, el renglón recibe su clase…");
        Assert.AreEqual(product.SyncId, row.ChargedLineSyncId, "…y el SyncId del producto…");
        Assert.AreEqual(987_654, row.ChargedLineId, "…y conserva el Id de la PC que lo cargó, que lo usa tal cual si la consulta le regresa");

        await records.Supplies.RemoveSupplyItemFromPatientAsync(row);
        Assert.IsFalse(items.Any(i => i.ProductId == product.Id), "La X descuenta las dos piezas");
        records.Dispose();
    }

    // ================================================================== campos de un equipo más nuevo

    [TestMethod]
    public async Task FieldsFromANewerDevice_SurviveARewriteOfTheSheets()
    {
        using var clinic = new Fase6Clinic();
        var supplyRowId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        int patientId;
        using (var db = new AppDbContext(clinic.Options))
        {
            // Como llegaría de la tablet con una versión más nueva: campos que esta PC no conoce en cada renglón
            var row = db.Patients.Single(p => p.RecordNumber == Fase6Scenarios.ScriptedRecord);
            patientId = row.Id;
            row.ActiveSuppliesJson = $$"""[{"RowId":"{{supplyRowId}}","SupplyName":"Venda elástica 10 cm","Instructions":"","Quantity":1,"UnitPrice":25,"Stamps":{"Quantity":"2026-10-12 09:59:00.0000000"},"FromTablet":true,"Removed":null}]""";
            row.ActiveServicesJson = """[{"ServiceName":"Curación especial","Price":150,"Quantity":1,"Futuro":[1,"dos",{"tres":3}]}]""";
            row.ActivePrescriptionJson = """[{"MedicineName":"Paracetamol 500 mg","Dosage":"1 cada 8 horas","RecommendedQuantity":1,"DispenseInClinic":false,"Plantilla":{"Id":"fiebre"}}]""";
            db.SaveChanges();
        }

        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false), drafts: false);
        var patient = records.Patients.Single(p => p.Id == patientId);
        records.OpenPatientChart(patient);

        // La hoja de insumos se reescribe al agregar uno fuera de la consulta; las demás, con cualquier guardado del expediente
        records.Supplies.SupplySearchText = "Gasas de algodón";
        await records.Supplies.SelectAndAddSupplyProductAsync(null);
        patient.Phone = "5512345678";
        await clinic.Inventory.SavePatientAsync(patient);

        var saved = StoredPatient(clinic, patient.SyncId);
        var supplies = JsonNode.Parse(saved.ActiveSuppliesJson)!.AsArray();
        Assert.HasCount(2, supplies);
        Assert.AreEqual(supplyRowId.ToString(), supplies[0]!["RowId"]!.GetValue<string>(), "El RowId que trae se conserva");
        Assert.AreEqual("2026-10-12 09:59:00.0000000", supplies[0]!["Stamps"]!["Quantity"]!.GetValue<string>());
        Assert.IsTrue(supplies[0]!["FromTablet"]!.GetValue<bool>());
        Assert.IsTrue(supplies[0]!.AsObject().ContainsKey("Removed"), "También un nulo");
        Assert.AreEqual("Gasas de algodón", supplies[1]!["SupplyName"]!.GetValue<string>());
        Assert.IsFalse(supplies[1]!.AsObject().ContainsKey("FromTablet"));
        Assert.AreNotEqual(supplyRowId.ToString(), supplies[1]!["RowId"]!.GetValue<string>(), "El renglón nuevo tiene su propio RowId");

        var services = JsonNode.Parse(saved.ActiveServicesJson)!.AsArray();
        Assert.AreEqual("""[1,"dos",{"tres":3}]""", services[0]!["Futuro"]!.ToJsonString());
        Assert.AreEqual(PatientSheetRows.LegacyRowId(patient.SyncId, PatientSheetRows.ServicesColumn, 0, "Curación especial").ToString(),
            services[0]!["RowId"]!.GetValue<string>(), "Un renglón sin RowId se guarda con el determinista");

        var prescription = JsonNode.Parse(saved.ActivePrescriptionJson)!.AsArray();
        Assert.AreEqual("fiebre", prescription[0]!["Plantilla"]!["Id"]!.GetValue<string>());
        records.Dispose();
    }

    // ================================================================== el mismo renglón viejo en dos equipos

    [TestMethod]
    public async Task TheSameLegacyRow_GetsTheSameRowId_OnTwoDevices()
    {
        using var pc = new Fase6Clinic();
        await pc.ConnectCloudAsync();
        Guid patientSyncId;
        using (var db = new AppDbContext(pc.Options))
        {
            // Hojas escritas por una PC anterior al paso 8.18: sin RowId, con nombres repetidos
            var row = db.Patients.Single(p => p.RecordNumber == Fase6Scenarios.ScriptedRecord);
            patientSyncId = row.SyncId;
            row.ActivePrescriptionJson = """[{"MedicineName":"Paracetamol 500 mg","Dosage":"1 cada 8 horas","RecommendedQuantity":1,"DispenseInClinic":false},{"MedicineName":"Loratadina 10 mg","Dosage":"1 cada 24 horas","RecommendedQuantity":1,"DispenseInClinic":false}]""";
            row.ActiveSuppliesJson = """[{"SupplyName":"Guantes","Instructions":"","Quantity":1,"UnitPrice":5},{"SupplyName":"Guantes","Instructions":"","Quantity":2,"UnitPrice":5,"ChargedConsultationFolio":"CNS-20261001-090000","ChargedUnits":2,"ChargedLineId":-1234567}]""";
            row.ActiveServicesJson = """[{"ServiceId":2,"ServiceCode":"SERV-002","ServiceName":"Curación Menor y Asepsia","Price":250,"Quantity":1}]""";
            db.SaveChanges();
        }
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success);
        using var tablet = new Fase6Clinic(seed: false);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");

        var onPc = StoredPatient(pc, patientSyncId);
        var onTablet = StoredPatient(tablet, patientSyncId);
        Assert.AreEqual(onPc.ActiveSuppliesJson, onTablet.ActiveSuppliesJson, "La misma hoja vieja en los dos equipos");

        static List<Guid?> Ids(IEnumerable<IPatientSheetRow> rows) => rows.Select(r => r.RowId).ToList();
        CollectionAssert.AreEqual(Ids(onPc.ActivePrescription), Ids(onTablet.ActivePrescription));
        CollectionAssert.AreEqual(Ids(onPc.ActiveSupplies), Ids(onTablet.ActiveSupplies));
        CollectionAssert.AreEqual(Ids(onPc.ActiveServices), Ids(onTablet.ActiveServices));
        Assert.AreEqual(PatientSheetRows.LegacyRowId(patientSyncId, PatientSheetRows.SuppliesColumn, 1, "Guantes"), onTablet.ActiveSupplies[1].RowId);
        Assert.AreNotEqual(onTablet.ActiveSupplies[0].RowId, onTablet.ActiveSupplies[1].RowId, "Dos renglones con el mismo nombre no comparten RowId");
        StringAssert.DoesNotMatch(onTablet.ActiveSuppliesJson, new System.Text.RegularExpressions.Regex("RowId"), "Leer no reescribe nada (sin relleno)");

        // Guardado en la tablet y bajado en la PC: los RowId que se guardan son los mismos que ya veía la PC
        onTablet.Phone = "5512340000";
        await tablet.Inventory.SavePatientAsync(onTablet);
        Assert.IsTrue((await tablet.Sync!.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var after = StoredPatient(pc, patientSyncId);
        StringAssert.Contains(after.ActiveSuppliesJson, "\"RowId\"");
        CollectionAssert.AreEqual(Ids(onPc.ActivePrescription), Ids(after.ActivePrescription));
        CollectionAssert.AreEqual(Ids(onPc.ActiveSupplies), Ids(after.ActiveSupplies));
        CollectionAssert.AreEqual(Ids(onPc.ActiveServices), Ids(after.ActiveServices));
        Assert.AreEqual(-1234567, after.ActiveSupplies[1].ChargedLineId, "Sin relleno: el renglón viejo conserva lo que traía");
        Assert.IsNull(after.ActiveSupplies[1].ChargedLineKind);
    }

    // ================================================================== revisión: versiones mezcladas

    /// <summary>
    /// Un renglón que escribió una PC anterior (solo con sus Id) no se traduce al recibir la consulta: si la consulta
    /// le regresa a esa PC, ella usa sus Id tal cual. Aquí + / − y la X lo encuentran igual, por nombre.
    /// </summary>
    [TestMethod]
    public async Task ReceivedRowsFromAnOlderPc_KeepTheSendersIds_AndPlusMinusXStillHitTheRightLines()
    {
        using var pc = new Fase6Clinic();
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
        var pcGauze = pcRecords.Catalog.AvailableProducts.First(p => p.Name == "Gasa Estéril 10x10 cm");
        var pcCuracion = pcRecords.Catalog.Services.Single(s => s.Code == "SERV-002");
        pcRecords.Supplies.SupplyQuantity = 2;
        await pcRecords.Supplies.SelectAndAddSupplyProductAsync(pcGauze);
        pcRecords.ClinicalServices.ServiceModalQuantity = 1;
        await pcRecords.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(pcCuracion);

        // Como los habría escrito una PC v1.35: sin clase, sin SyncId, solo con sus Id
        var pcPatient = pcRecords.SelectedPatient!;
        var pcRow = pcPatient.ActiveSupplies.Single(r => r.ChargedLineSyncId == pcGauze.SyncId);
        pcRow.ChargedLineKind = null;
        pcRow.ChargedLineSyncId = null;
        var pcServiceRow = pcPatient.ActiveServices.Single(r => r.ServiceSyncId == pcCuracion.SyncId);
        pcServiceRow.ServiceSyncId = null;
        await pc.Inventory.SavePatientAsync(pcPatient);
        StringAssert.DoesNotMatch(StoredPatient(pc, pcPatient.SyncId).ActiveSuppliesJson, new System.Text.RegularExpressions.Regex("ChargedLineKind"));

        await Task.Delay(200);
        pcRecords.Autosave.SaveConsultationDraftNow();
        await pcRecords.Handoff.OpenSendConsultationSheetAsync();
        pcRecords.Handoff.SelectedHandoffDevice = pcRecords.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-tablet-sala");
        await pcRecords.Handoff.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(pcRecords.Handoff.IsConsultationHandedOff, pcRecords.Handoff.HandoffSheetError ?? pcRecords.Info.InfoModalMessage);
        await tabletRecords.Handoff.PollHandoffsOnceAsync();
        await tabletRecords.Handoff.AcceptIncomingHandoffAsync();
        Assert.IsTrue(tabletConsultations.HasActiveConsultation, tabletRecords.Info.InfoModalMessage);

        var gauze = tabletRecords.Catalog.AvailableProducts.Single(p => p.SyncId == pcGauze.SyncId);
        var curacion = tabletRecords.Catalog.Services.Single(s => s.SyncId == pcCuracion.SyncId);
        var patient = tabletRecords.SelectedPatient!;
        var row = patient.ActiveSupplies.Single(r => r.RowId == pcRow.RowId);
        var serviceRow = patient.ActiveServices.Single(r => r.RowId == pcServiceRow.RowId);
        Assert.AreEqual(pcGauze.Id, row.ChargedLineId, "Recibir no traduce un renglón viejo");
        Assert.AreEqual(pcCuracion.Id, serviceRow.ServiceId);

        var items = tabletConsultations.CurrentConsultation!.Items;
        int Charged(int lineId) => items.FirstOrDefault(i => i.ProductId == lineId)?.Quantity ?? 0;
        int gauzeBefore = Charged(gauze.Id);

        await tabletRecords.Supplies.IncreaseSupplyQuantityAsync(row);
        Assert.AreEqual(gauzeBefore + 1, Charged(gauze.Id), "+ carga la gasa de la tablet (por nombre)");
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, row.ChargedLineKind, "El renglón viejo recibe su clase…");
        Assert.AreEqual(gauze.SyncId, row.ChargedLineSyncId, "…y el SyncId del producto…");
        Assert.AreEqual(pcGauze.Id, row.ChargedLineId, "…y conserva el Id de la PC que lo cargó");

        // Lo guardado en la tablet sigue con los Id de la PC anterior
        var stored = StoredPatient(tablet, patient.SyncId);
        var storedRow = new PatientRecord { SyncId = patient.SyncId, ActiveSuppliesJson = stored.ActiveSuppliesJson }.ActiveSupplies.Single(r => r.RowId == row.RowId);
        Assert.AreEqual(pcGauze.Id, storedRow.ChargedLineId);
        var storedService = new PatientRecord { SyncId = patient.SyncId, ActiveServicesJson = stored.ActiveServicesJson }.ActiveServices.Single(r => r.RowId == serviceRow.RowId);
        Assert.AreEqual(pcCuracion.Id, storedService.ServiceId);
        Assert.IsNull(storedService.ServiceSyncId);

        await tabletRecords.Supplies.DecreaseSupplyQuantityAsync(row);
        Assert.AreEqual(gauzeBefore, Charged(gauze.Id), "− descuenta la gasa de la tablet");
        var afterService = curacion.DefaultProducts.Where(dp => dp.Product != null)
            .ToDictionary(dp => dp.Product!.Id, dp => Charged(dp.Product!.Id) - Math.Max(1, dp.Quantity));
        await tabletRecords.ClinicalServices.RemoveServiceItemFromPatientAsync(serviceRow);
        Assert.AreEqual(0, Charged(-curacion.Id), "La X del servicio viejo descuenta el de la tablet");
        foreach (var (productId, expected) in afterService) Assert.AreEqual(expected, Charged(productId));
        await tabletRecords.Supplies.RemoveSupplyItemFromPatientAsync(row);
        Assert.IsEmpty(items, "Cada X encontró su renglón");

        tabletRecords.Dispose();
        pcRecords.Dispose();
    }

    // ================================================================== revisión: la receta anterior

    [TestMethod]
    public async Task PreviousPrescription_KeepsItsRowIdAndUnknownFields_WhenTheConsultationIsDiscardedOrHasNoPrescription()
    {
        using var clinic = new Fase6Clinic();
        var rowId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        int patientId;
        using (var db = new AppDbContext(clinic.Options))
        {
            var row = db.Patients.Single(p => p.RecordNumber == Fase6Scenarios.ScriptedRecord);
            patientId = row.Id;
            row.ActivePrescriptionJson = $$$"""[{"RowId":"{{{rowId}}}","MedicineName":"Paracetamol 500 mg","Dosage":"1 cada 8 horas","RecommendedQuantity":1,"DispenseInClinic":false,"Plantilla":{"Id":"fiebre"}}]""";
            db.SaveChanges();
        }

        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations, drafts: false);
        var patient = records.Patients.Single(p => p.Id == patientId);

        void AssertKept(string when)
        {
            var stored = JsonNode.Parse(StoredPatient(clinic, patient.SyncId).ActivePrescriptionJson)!.AsArray();
            Assert.HasCount(1, stored, when);
            Assert.AreEqual(rowId.ToString(), stored[0]!["RowId"]!.GetValue<string>(), $"{when}: el mismo RowId");
            Assert.AreEqual("fiebre", stored[0]!["Plantilla"]!["Id"]!.GetValue<string>(), $"{when}: el campo desconocido");
            Assert.AreEqual(rowId, patient.ActivePrescription.Single().RowId, when);
        }

        // Descartada: la receta anterior vuelve con sus renglones
        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        Assert.IsEmpty(patient.ActivePrescription, "La consulta empieza con la receta vacía");
        await records.DiscardActiveConsultationAsync();
        Assert.IsFalse(consultations.HasActiveConsultation);
        AssertKept("Descartada");

        // Terminada y cobrada sin receta: la última emitida sigue siendo la anterior
        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        await records.Consultation.FinalizeConsultationAsync();
        records.Consultation.Checkout.SetExactCash();
        await records.Consultation.Checkout.ChargeCommand.ExecuteAsync(null);
        Assert.IsFalse(consultations.HasActiveConsultation, records.Info.InfoModalMessage);
        AssertKept("Terminada sin receta");

        // El borrador no los lleva: su formato no cambia
        Assert.IsNull(ConsultationRecordState.ClonePrescription(patient.ActivePrescription.Single()).RowId);
        records.Dispose();
    }

    // ================================================================== revisión: + en un renglón traducido a un cargo libre

    [TestMethod]
    public async Task PlusMinus_OnARowTranslatedToAFreeLineWithAnotherName_ChargeAndUnchargeTheSameLine()
    {
        using var clinic = new Fase6Clinic();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations, drafts: false);
        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        records.ChartPrimaryAction();
        var patient = records.SelectedPatient!;
        var folio = consultations.CurrentConsultation!.Folio;

        // Como queda al recibir una consulta cuyo producto no existe aquí: la cuenta lo cobra como el cargo libre del
        // nombre de su renglón (lo abrió un insumo por defecto) y el renglón de la hoja apunta a ese cargo
        const string accountName = "Venda de yeso 15 cm";
        var accountLine = HandoffPayloadCodec.FreeChargeId(accountName);
        Assert.IsTrue(consultations.AddCartItemToActiveConsultation(accountLine, "CARGO", accountName, null, null, "Piezas", 40m, 2));
        var row = new PatientSupplyItem
        {
            SupplyName = "Venda de yeso 15 cm (Rollo)", Quantity = 2, UnitPrice = 40m, ChargedConsultationFolio = folio, ChargedUnits = 2,
            ChargedLineId = accountLine, ChargedLineKind = HandoffPayloadCodec.LineProduct, ChargedLineSyncId = Guid.NewGuid()
        };
        var otherLine = HandoffPayloadCodec.FreeChargeId(row.SupplyName);
        Assert.AreNotEqual(accountLine, otherLine);
        patient.ActiveSupplies = new List<PatientSupplyItem>(patient.ActiveSupplies) { row };
        records.Supplies.OpenSuppliesModal();

        var items = consultations.CurrentConsultation.Items;
        int Charged(int lineId) => items.FirstOrDefault(i => i.ProductId == lineId)?.Quantity ?? 0;
        await records.Supplies.IncreaseSupplyQuantityAsync(row);
        Assert.AreEqual(3, Charged(accountLine), "+ carga el mismo cargo libre");
        Assert.AreEqual(0, Charged(otherLine), "No abre otro cargo con el nombre de la hoja");
        await records.Supplies.DecreaseSupplyQuantityAsync(row);
        Assert.AreEqual(2, Charged(accountLine));
        await records.Supplies.IncreaseSupplyQuantityAsync(row);
        Assert.AreEqual(3, Charged(accountLine));
        Assert.AreEqual(HandoffPayloadCodec.LineProduct, row.ChargedLineKind, "Sigue siendo el producto en los demás equipos");

        await records.Supplies.RemoveSupplyItemFromPatientAsync(row);
        Assert.IsEmpty(items, "La X descuenta todo lo que cargó el renglón");
        records.Dispose();
    }

    // ================================================================== revisión: renglones viejos de este equipo

    [TestMethod]
    [DataRow(false, DisplayName = "producto renombrado")]
    [DataRow(true, DisplayName = "producto desactivado")]
    public async Task X_OnALegacyRowOfThisPc_UnchargesIt_AfterTheProductIsRenamedOrDeactivated(bool deactivate)
    {
        using var clinic = new Fase6Clinic();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations, drafts: false);
        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        records.ChartPrimaryAction();
        var patient = records.SelectedPatient!;
        var product = records.Catalog.AvailableProducts.First(p => CatalogStore.IsProductSupply(p) && p.CurrentStock >= 3 && p.Price > 0);

        // Cargado por esta PC con v1.35: solo su Id
        records.Supplies.SupplyQuantity = 2;
        await records.Supplies.SelectAndAddSupplyProductAsync(product);
        var row = patient.ActiveSupplies.Last();
        row.ChargedLineKind = null;
        row.ChargedLineSyncId = null;
        var items = consultations.CurrentConsultation!.Items;
        Assert.AreEqual(2, items.Single(i => i.ProductId == product.Id).Quantity);

        using (var db = new AppDbContext(clinic.Options))
        {
            var stored = db.Products.Single(p => p.Id == product.Id);
            if (deactivate) stored.IsActive = false;
            else stored.Name = "Nombre nuevo del producto";
            db.SaveChanges();
        }
        await records.LoadServicesAsync();
        Assert.AreEqual(!deactivate, records.Catalog.AvailableProducts.Any(p => p.Id == product.Id));

        await records.Supplies.DecreaseSupplyQuantityAsync(row);
        Assert.AreEqual(1, items.Single(i => i.ProductId == product.Id).Quantity, "− descuenta del renglón con que se cargó");
        await records.Supplies.RemoveSupplyItemFromPatientAsync(row);
        Assert.IsEmpty(items, "La X lo descuenta");
        records.Dispose();
    }

    [TestMethod]
    [DataRow(false, DisplayName = "servicio renombrado")]
    [DataRow(true, DisplayName = "servicio desactivado")]
    public async Task X_OnALegacyServiceRowOfThisPc_UnchargesItAndItsDefaults_AfterTheServiceIsRenamedOrDeactivated(bool deactivate)
    {
        using var clinic = new Fase6Clinic();
        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await clinic.OpenAsync(consultations, drafts: false);
        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        records.ChartPrimaryAction();
        var patient = records.SelectedPatient!;
        var curacion = records.Catalog.Services.Single(s => s.Code == "SERV-002");
        Assert.IsNotEmpty(curacion.DefaultProducts);

        records.ClinicalServices.ServiceModalQuantity = 1;
        await records.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(curacion);
        var row = patient.ActiveServices.Last();
        row.ServiceSyncId = null;
        var items = consultations.CurrentConsultation!.Items;
        Assert.AreEqual(1, items.Single(i => i.ProductId == -curacion.Id).Quantity);

        using (var db = new AppDbContext(clinic.Options))
        {
            var stored = db.ClinicalServices.Single(s => s.Id == curacion.Id);
            if (deactivate) stored.IsActive = false;
            else stored.Name = "Curación con otro nombre";
            db.SaveChanges();
        }
        await records.LoadServicesAsync();
        Assert.AreEqual(!deactivate, records.Catalog.Services.Any(s => s.Id == curacion.Id));

        await records.ClinicalServices.RemoveServiceItemFromPatientAsync(row);
        Assert.IsEmpty(items, "La X descuenta el servicio y sus insumos por defecto");
        records.Dispose();
    }

    // ================================================================== segunda revisión: la receta anterior en el borrador

    private const string PreviousRowJson =
        """[{"RowId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","MedicineName":"Paracetamol 500 mg","Dosage":"1 cada 8 horas","RecommendedQuantity":1,"DispenseInClinic":false,"Plantilla":{"Id":"fiebre"}}]""";

    private static readonly Guid PreviousRowId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    private static int WritePreviousPrescription(Fase6Clinic clinic)
    {
        using var db = new AppDbContext(clinic.Options);
        var row = db.Patients.Single(p => p.RecordNumber == Fase6Scenarios.ScriptedRecord);
        row.ActivePrescriptionJson = PreviousRowJson;
        db.SaveChanges();
        return row.Id;
    }

    private static void AssertPreviousPrescriptionKept(Fase6Clinic clinic, Guid patientSyncId, string when)
    {
        var stored = JsonNode.Parse(StoredPatient(clinic, patientSyncId).ActivePrescriptionJson)!.AsArray();
        Assert.HasCount(1, stored, when);
        Assert.AreEqual(PreviousRowId.ToString(), stored[0]!["RowId"]!.GetValue<string>(), $"{when}: el mismo RowId");
        Assert.AreEqual("fiebre", stored[0]!["Plantilla"]!["Id"]!.GetValue<string>(), $"{when}: el campo desconocido");
    }

    [TestMethod]
    public async Task PreviousPrescription_KeepsItsIdentity_InARestoredDraft()
    {
        using var clinic = new Fase6Clinic();
        var patientId = WritePreviousPrescription(clinic);

        var records = await clinic.OpenAsync(new ConsultationService(seedDemoPatients: false));
        var patient = records.Patients.Single(p => p.Id == patientId);
        records.OpenPatientChart(patient);
        records.ChartPrimaryAction();
        Assert.IsEmpty(patient.ActivePrescription);
        records.FlushConsultationDraft();
        records.Dispose();

        // El borrador lleva la receta anterior con su RowId y sus campos (la receta en curso no lleva RowId)
        var draft = JsonNode.Parse(System.IO.File.ReadAllText(clinic.DraftPath))!;
        var previous = draft["PreviousPrescriptions"]!.AsArray().Single()!;
        Assert.AreEqual(PreviousRowId.ToString(), previous["RowId"]!.GetValue<string>());
        Assert.AreEqual("fiebre", previous["Plantilla"]!["Id"]!.GetValue<string>());

        // Al volver a abrir el programa se recupera; descartarla regresa los mismos renglones
        var consultations = new ConsultationService(seedDemoPatients: false);
        var reopened = await clinic.OpenAsync(consultations);
        Assert.IsTrue(reopened.IsConsultationRecovered);
        await reopened.DiscardActiveConsultationAsync();
        Assert.IsFalse(consultations.HasActiveConsultation);
        AssertPreviousPrescriptionKept(clinic, patient.SyncId, "Recuperada y descartada");
        reopened.Dispose();
    }

    [TestMethod]
    public async Task PreviousPrescription_KeepsItsIdentity_InAReceivedConsultation()
    {
        using var pc = new Fase6Clinic();
        WritePreviousPrescription(pc);
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success);

        var (pcRecords, tabletRecords, tabletConsultations) = await SendToTabletAsync(pc, tablet, _ => Task.CompletedTask);
        var patient = tabletRecords.SelectedPatient!;
        Assert.IsEmpty(patient.ActivePrescription, "La consulta recibida empieza con la receta vacía");

        await tabletRecords.DiscardActiveConsultationAsync();
        Assert.IsFalse(tabletConsultations.HasActiveConsultation);
        AssertPreviousPrescriptionKept(tablet, patient.SyncId, "Recibida y descartada");
        tabletRecords.Dispose();
        pcRecords.Dispose();
    }

    /// <summary>Abre los dos equipos, inicia en la PC la consulta del paciente del guion, la llena y la pasa a la tablet.</summary>
    private static async Task<(MedicalRecordsViewModel PcRecords, MedicalRecordsViewModel TabletRecords, ConsultationService TabletConsultations)> SendToTabletAsync(
        Fase6Clinic pc, Fase6Clinic tablet, Func<MedicalRecordsViewModel, Task> fillOnPc)
    {
        var pcRecords = await pc.OpenAsync(new ConsultationService(seedDemoPatients: false));
        var tabletConsultations = new ConsultationService(seedDemoPatients: false);
        var tabletRecords = await tablet.OpenAsync(tabletConsultations);

        pcRecords.OpenPatientChart(Fase6Scenarios.ScriptedPatient(pcRecords));
        pcRecords.ChartPrimaryAction();
        await fillOnPc(pcRecords);

        await Task.Delay(200);
        pcRecords.Autosave.SaveConsultationDraftNow();
        await pcRecords.Handoff.OpenSendConsultationSheetAsync();
        pcRecords.Handoff.SelectedHandoffDevice = pcRecords.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-tablet-sala");
        await pcRecords.Handoff.SendConsultationToSelectedDeviceAsync();
        Assert.IsTrue(pcRecords.Handoff.IsConsultationHandedOff, pcRecords.Handoff.HandoffSheetError ?? pcRecords.Info.InfoModalMessage);
        await tabletRecords.Handoff.PollHandoffsOnceAsync();
        await tabletRecords.Handoff.AcceptIncomingHandoffAsync();
        Assert.IsTrue(tabletConsultations.HasActiveConsultation, tabletRecords.Info.InfoModalMessage);
        return (pcRecords, tabletRecords, tabletConsultations);
    }

    // ================================================================== segunda revisión: insumo viejo cuyo producto no está aquí

    [TestMethod]
    public void Receiving_ALegacyRowWhoseProductIsMissingHere_PointsItToTheReceivedFreeLine()
    {
        const string folio = "CNS-20261012-110000";
        const string name = "Venda de yeso 15 cm (Rollo)";
        var syncId = Guid.NewGuid();
        var patient = new PatientRecord { SyncId = Guid.NewGuid() };
        patient.ActiveSupplies = new List<PatientSupplyItem>
        {
            new() { SupplyName = name, Quantity = 1, UnitPrice = 40m, ChargedConsultationFolio = folio, ChargedUnits = 1, ChargedLineId = 42 }
        };
        var draft = new ConsultationDraft { Folio = folio };
        draft.Items.Add(new ConsultationProductItem { Code = "CUR-042", Name = name, LineKind = HandoffPayloadCodec.LineProduct, CatalogSyncId = syncId, Quantity = 1 });
        HandoffPayloadCodec.MapReceivedItems(draft, Array.Empty<CatalogRef>());
        var free = HandoffPayloadCodec.FreeChargeId(name);
        Assert.AreEqual(free, draft.Items[0].ProductId, "La cuenta recibida lo cobra como cargo libre");

        Assert.AreEqual(1, PatientSheetRows.TranslateReceivedRows(patient, folio, draft.Items, Array.Empty<Product>(), null, Array.Empty<ClinicalService>()));
        var row = patient.ActiveSupplies[0];
        Assert.AreEqual(free, row.ChargedLineId, "Queda con el Id negativo del cargo libre, que la PC que lo envió también entiende");
        Assert.IsNull(row.ChargedLineKind, "Sin relleno: la clase llega al volver a cargarse");
    }

    [TestMethod]
    public void Receiving_ALegacyRowWhoseProductExistsHere_KeepsTheSendersId()
    {
        const string folio = "CNS-20261012-110000";
        var syncId = Guid.NewGuid();
        var product = new Product { Id = 9, SyncId = syncId, Code = "CUR-042", Name = "Venda de yeso 15 cm", Presentation = "Rollo" };
        var refs = new[] { new CatalogRef(CatalogRefKind.Product, product.Id, syncId, product.Code, product.Name, true) };
        var patient = new PatientRecord { SyncId = Guid.NewGuid() };
        patient.ActiveSupplies = new List<PatientSupplyItem>
        {
            new() { SupplyName = PatientSheetRows.SupplyDisplayName(product), Quantity = 1, ChargedConsultationFolio = folio, ChargedUnits = 1, ChargedLineId = 42 }
        };
        var draft = new ConsultationDraft { Folio = folio };
        draft.Items.Add(new ConsultationProductItem
        {
            Code = product.Code, Name = PatientSheetRows.SupplyDisplayName(product), LineKind = HandoffPayloadCodec.LineProduct, CatalogSyncId = syncId, Quantity = 1
        });
        HandoffPayloadCodec.MapReceivedItems(draft, refs);
        Assert.AreEqual(product.Id, draft.Items[0].ProductId, "La cuenta recibida tiene el renglón del producto");

        Assert.AreEqual(0, PatientSheetRows.TranslateReceivedRows(patient, folio, draft.Items, new[] { product }, refs, Array.Empty<ClinicalService>()));
        Assert.AreEqual(42, patient.ActiveSupplies[0].ChargedLineId, "Conserva el Id de la PC anterior que lo cargó");
    }

    // ================================================================== segunda revisión: insumo viejo recibido y producto desactivado aquí

    [TestMethod]
    [DataRow(false, DisplayName = "la X directo")]
    [DataRow(true, DisplayName = "+ y luego la X")]
    public async Task ReceivedLegacyRow_ProductDeactivatedHere_XStillUnchargesIt(bool plusFirst)
    {
        using var pc = new Fase6Clinic();
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        ShiftLocalIds(tablet);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success);

        Product pcGauze = null!;
        PatientSupplyItem pcRow = null!;
        var (pcRecords, tabletRecords, tabletConsultations) = await SendToTabletAsync(pc, tablet, async records =>
        {
            pcGauze = records.Catalog.AvailableProducts.First(p => p.Name == "Gasa Estéril 10x10 cm");
            records.Supplies.SupplyQuantity = 2;
            await records.Supplies.SelectAndAddSupplyProductAsync(pcGauze);
            // Como lo habría escrito una PC v1.35: solo con su Id
            var patient = records.SelectedPatient!;
            pcRow = patient.ActiveSupplies.Single(r => r.ChargedLineSyncId == pcGauze.SyncId);
            pcRow.ChargedLineKind = null;
            pcRow.ChargedLineSyncId = null;
            await pc.Inventory.SavePatientAsync(patient);
        });

        var gauze = tabletRecords.Catalog.AvailableProducts.Single(p => p.SyncId == pcGauze.SyncId);
        var row = tabletRecords.SelectedPatient!.ActiveSupplies.Single(r => r.RowId == pcRow.RowId);
        Assert.AreEqual(pcGauze.Id, row.ChargedLineId, "Trae el Id de la PC");
        var items = tabletConsultations.CurrentConsultation!.Items;
        Assert.AreEqual(2, items.Single(i => i.ProductId == gauze.Id).Quantity);

        if (plusFirst)
        {
            await tabletRecords.Supplies.IncreaseSupplyQuantityAsync(row);
            Assert.AreEqual(3, items.Single(i => i.ProductId == gauze.Id).Quantity);
            Assert.AreEqual(gauze.SyncId, row.ChargedLineSyncId);
            Assert.AreEqual(pcGauze.Id, row.ChargedLineId);
        }

        using (var db = new AppDbContext(tablet.Options))
        {
            db.Products.Single(p => p.Id == gauze.Id).IsActive = false;
            db.SaveChanges();
        }
        await tabletRecords.LoadServicesAsync();
        Assert.IsFalse(tabletRecords.Catalog.AvailableProducts.Any(p => p.Id == gauze.Id));

        await tabletRecords.Supplies.RemoveSupplyItemFromPatientAsync(row);
        Assert.IsEmpty(items, "La X descuenta la gasa de la tablet aunque ya no esté activa");
        tabletRecords.Dispose();
        pcRecords.Dispose();
    }
}
