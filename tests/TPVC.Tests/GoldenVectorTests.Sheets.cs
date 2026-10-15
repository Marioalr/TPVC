using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.18: contracts/golden/synced-json-columns.json, las hojas del paciente (Active*Json) con renglones que tienen
/// identidad: el RowId de un renglón viejo, lo que la PC lee y vuelve a escribir (los campos desconocidos se quedan), lo
/// que escribe al cargar a la cuenta y con qué renglón de la cuenta se cargó cada insumo o servicio
/// (Services/Rules/PatientSheetRows). Kotlin lo repite igual.
/// </summary>
public partial class GoldenVectorTests
{
    private static readonly Guid SheetPatient = new("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
    private static readonly Guid GauzeSyncId = new("a1b2c3d4-0000-4000-8000-000000000001");
    private static readonly Guid AlcoholSyncId = new("a1b2c3d4-0000-4000-8000-000000000002");
    private static readonly Guid RetiredSyncId = new("a1b2c3d4-0000-4000-8000-000000000003");
    private static readonly Guid ParacetamolSyncId = new("a1b2c3d4-0000-4000-8000-000000000004");
    private static readonly Guid ForeignSyncId = new("a1b2c3d4-0000-4000-8000-0000000000ff");
    private static readonly Guid CuracionSyncId = new("b1b2c3d4-0000-4000-8000-000000000001");
    private static readonly Guid GlucemiaSyncId = new("b1b2c3d4-0000-4000-8000-000000000002");
    private static readonly Guid SuturaSyncId = new("b1b2c3d4-0000-4000-8000-000000000003");

    /// <summary>Los productos de "este equipo" en los casos: Id locales distintos de los del equipo que escribió.</summary>
    private static List<Product> SheetProducts() => new()
    {
        new Product { Id = 11, SyncId = GauzeSyncId, Code = "CUR-001", Name = "Gasa Estéril 10x10 cm", Presentation = "Paquete con 10 piezas", GenericName = "Gasa de algodón hidrófilo estéril" },
        new Product { Id = 12, SyncId = AlcoholSyncId, Code = "ANT-001", Name = "Alcohol Etílico 70%", Presentation = "Botella de 1000 ml", GenericName = "Etanol 70%" },
        new Product { Id = 13, SyncId = RetiredSyncId, Code = "CUR-009", Name = "Venda de gasa 5 cm", Presentation = "Rollo", IsActive = false },
        new Product { Id = 14, SyncId = ParacetamolSyncId, Code = "MED-001", Name = "Paracetamol 500 mg", Presentation = "Caja con 20 tabletas", GenericName = "Paracetamol" },
    };

    private static List<ClinicalService> SheetServices() => new()
    {
        new ClinicalService { Id = 21, SyncId = CuracionSyncId, Code = "SERV-002", Name = "Curación Menor y Asepsia" },
        new ClinicalService { Id = 22, SyncId = GlucemiaSyncId, Code = "SERV-004", Name = "Monitoreo de Glucemia Capilar" },
        new ClinicalService { Id = 23, SyncId = SuturaSyncId, Code = "SERV-003", Name = "Sutura de Herida Simple", IsActive = false },
    };

    private static JsonArray ProductsNode(IEnumerable<Product> products) => new(products.Select(p => (JsonNode)new JsonObject
    {
        ["id"] = p.Id, ["syncId"] = SyncIds.Format(p.SyncId), ["name"] = p.Name, ["presentation"] = p.Presentation,
        ["genericName"] = p.GenericName, ["active"] = p.IsActive
    }).ToArray());

    private static JsonArray ServicesNode(IEnumerable<ClinicalService> services) => new(services.Select(s => (JsonNode)new JsonObject
    {
        ["id"] = s.Id, ["syncId"] = SyncIds.Format(s.SyncId), ["code"] = s.Code, ["name"] = s.Name, ["active"] = s.IsActive
    }).ToArray());

    /// <summary>La hoja que la PC escribe al guardar la de <paramref name="json"/> sin cambios (leer y volver a escribir).</summary>
    private static string RewriteSheet(string column, string json)
    {
        var patient = new PatientRecord { SyncId = SheetPatient };
        switch (column)
        {
            case PatientSheetRows.PrescriptionColumn:
                patient.ActivePrescriptionJson = json;
                patient.SyncPrescriptionJson();
                return patient.ActivePrescriptionJson;
            case PatientSheetRows.SuppliesColumn:
                patient.ActiveSuppliesJson = json;
                patient.SyncSuppliesJson();
                return patient.ActiveSuppliesJson;
            default:
                patient.ActiveServicesJson = json;
                patient.SyncServicesJson();
                return patient.ActiveServicesJson;
        }
    }

    /// <summary>El renglón tal como lo escribe la PC en su hoja.</summary>
    private static JsonNode WrittenRow(IPatientSheetRow row)
    {
        var patient = new PatientRecord { SyncId = SheetPatient };
        string json;
        switch (row)
        {
            case PatientPrescriptionItem prescription:
                patient.ActivePrescription = new List<PatientPrescriptionItem> { prescription };
                json = patient.ActivePrescriptionJson;
                break;
            case PatientSupplyItem supply:
                patient.ActiveSupplies = new List<PatientSupplyItem> { supply };
                json = patient.ActiveSuppliesJson;
                break;
            default:
                patient.ActiveServices = new List<PatientServiceItem> { (PatientServiceItem)row };
                json = patient.ActiveServicesJson;
                break;
        }
        return JsonNode.Parse(json)!.AsArray()[0]!.DeepClone();
    }

    private static JsonObject LineNode(PatientSheetRows.SupplyLine line, string name) => line.IsFree
        ? new JsonObject { ["line"] = line.Kind, ["name"] = name, ["freeChargeId"] = line.LineId }
        : new JsonObject { ["line"] = line.Kind, ["productId"] = line.LineId, ["syncId"] = line.SyncId is { } s ? SyncIds.Format(s) : null };

    [TestMethod]
    public void PatientSheets_MatchTheVectors()
    {
        var cases = new JsonArray();
        var patient = SyncIds.Format(SheetPatient);

        // ------------------------------------------------ RowId de un renglón que llegó sin él
        foreach (var (name, syncId, column, position, rowName) in new (string, string, string, int, string)[]
                 {
                     ("receta, primer renglón", patient, PatientSheetRows.PrescriptionColumn, 0, "Paracetamol 500 mg tabletas"),
                     ("el SyncId del paciente en mayúsculas da lo mismo", patient.ToUpperInvariant(), PatientSheetRows.PrescriptionColumn, 0, "Paracetamol 500 mg tabletas"),
                     ("insumos, tercer renglón, con acentos", patient, PatientSheetRows.SuppliesColumn, 2, "Gasa Estéril 10x10 cm (Paquete con 10 piezas)"),
                     ("servicios", patient, PatientSheetRows.ServicesColumn, 0, "Curación Menor y Asepsia"),
                     ("el nombre tal cual (con espacios y mayúsculas)", patient, PatientSheetRows.ServicesColumn, 0, " CURACIÓN menor y asepsia "),
                     ("sin nombre", patient, PatientSheetRows.SuppliesColumn, 5, ""),
                 })
        {
            var legacyId = PatientSheetRows.LegacyRowId(Guid.Parse(syncId), column, position, rowName);
            cases.Add(Case($"RowId viejo · {name}", new JsonObject
            {
                ["patientSyncId"] = syncId, ["column"] = column, ["position"] = position, ["rowName"] = rowName,
                ["uuid5Name"] = $"sheetrow:{SyncIds.Format(Guid.Parse(syncId))}:{column}:{position}:{rowName}"
            }, SyncIds.Format(legacyId)));
        }
        Assert.AreEqual(SyncIds.Uuid5(SyncIds.TpvcNamespace, $"sheetrow:{patient}:ActiveSuppliesJson:2:Gasa Estéril 10x10 cm (Paquete con 10 piezas)"),
            PatientSheetRows.LegacyRowId(SheetPatient, PatientSheetRows.SuppliesColumn, 2, "Gasa Estéril 10x10 cm (Paquete con 10 piezas)"));

        // ------------------------------------------------ leer y volver a escribir sin cambios
        const string kept = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
        foreach (var (name, column, json) in new (string, string, string)[]
                 {
                     ("receta vieja: cada renglón recibe su RowId por posición y nombre", PatientSheetRows.PrescriptionColumn,
                         """[{"MedicineName":"Paracetamol 500 mg tabletas","Dosage":"1 cada 8 horas","RecommendedQuantity":1,"Presentation":"Caja con 20 tabletas","DispenseInClinic":true},{"MedicineName":"Loratadina 10 mg","Dosage":"1 cada 24 horas","RecommendedQuantity":1,"DispenseInClinic":false}]"""),
                     ("insumos: el RowId que ya trae se conserva; el renglón viejo (de una PC anterior, solo con ChargedLineId) recibe el suyo", PatientSheetRows.SuppliesColumn,
                         $$"""[{"RowId":"{{kept}}","SupplyName":"Alcohol Etílico 70% (Botella de 1000 ml)","Instructions":"Asepsia","Quantity":1,"UnitPrice":50.0,"ChargedConsultationFolio":"CNS-20261009-103015","ChargedUnits":1,"ChargedLineId":12,"ChargedLineKind":"producto","ChargedLineSyncId":"{{SyncIds.Format(AlcoholSyncId)}}"},{"SupplyName":"Gasa Estéril 10x10 cm (Paquete con 10 piezas)","Instructions":"Curación","Quantity":2,"UnitPrice":35.0,"ChargedConsultationFolio":"CNS-20261009-103015","ChargedUnits":2,"ChargedLineId":4}]"""),
                     ("un RowId repetido o vacío se trata como si faltara", PatientSheetRows.SuppliesColumn,
                         $$"""[{"RowId":"{{kept}}","SupplyName":"Guantes","Instructions":"","Quantity":1,"UnitPrice":5},{"RowId":"{{kept}}","SupplyName":"Guantes","Instructions":"","Quantity":1,"UnitPrice":5},{"RowId":"00000000-0000-0000-0000-000000000000","SupplyName":"Cubrebocas","Instructions":"","Quantity":1,"UnitPrice":3}]"""),
                     ("los campos que la PC no conoce se vuelven a escribir tal cual", PatientSheetRows.ServicesColumn,
                         $$"""[{"RowId":"{{kept}}","ServiceId":0,"ServiceSyncId":"{{SyncIds.Format(CuracionSyncId)}}","ServiceCode":"SERV-002","ServiceName":"Curación Menor y Asepsia","Category":"Curaciones","Notes":"","Price":250,"Quantity":1,"RemovedAtUtc":"2026-10-12 10:00:00.0000000","Stamps":{"Quantity":"2026-10-12 09:59:00.0000000"},"Tags":["tablet",1,null,true]}]"""),
                     ("se lee sin distinguir mayúsculas y se escribe en PascalCase", PatientSheetRows.ServicesColumn,
                         $$"""[{"rowId":"{{kept}}","serviceSyncId":"{{SyncIds.Format(GlucemiaSyncId)}}","serviceName":"Monitoreo de Glucemia Capilar","price":80,"quantity":1}]"""),
                     ("hoja vacía", PatientSheetRows.PrescriptionColumn, ""),
                 })
        {
            var written = RewriteSheet(column, json);
            Assert.AreEqual(written, RewriteSheet(column, written), $"{name}: volver a escribir lo escrito no cambia nada");
            cases.Add(Case($"leer y volver a escribir · {name}", new JsonObject
            {
                ["patientSyncId"] = patient, ["column"] = column, ["json"] = json
            }, JsonNode.Parse(written)));
        }
        StringAssert.Contains(RewriteSheet(PatientSheetRows.ServicesColumn, """[{"ServiceName":"Curación","Futuro":{"a":[1,2]}}]"""), "\"Futuro\":{\"a\":[1,2]}");

        // ------------------------------------------------ lo que escribe la PC al cargar a la cuenta
        var products = SheetProducts();
        var services = SheetServices();
        var gauze = products[0];
        var rowId = Guid.Parse(kept);
        const string folio = "CNS-20261012-103015";

        var productRow = new PatientSupplyItem
        {
            RowId = rowId, SupplyName = PatientSheetRows.SupplyDisplayName(gauze), Instructions = "Curación de herida", Quantity = 2, UnitPrice = 35m,
            ChargedConsultationFolio = folio, ChargedUnits = 2
        };
        PatientSheetRows.SetSupplyLine(productRow, PatientSheetRows.SupplyLine.ForProduct(gauze));
        cases.Add(Case("escribir · insumo del inventario cargado a la cuenta", new JsonObject
        {
            ["column"] = PatientSheetRows.SuppliesColumn, ["product"] = ProductsNode(new[] { gauze })[0]!.DeepClone(), ["quantity"] = 2, ["folio"] = folio, ["rowId"] = kept
        }, WrittenRow(productRow)));

        var freeRow = new PatientSupplyItem
        {
            RowId = rowId, SupplyName = "Férula de aluminio a la medida", Instructions = "Uso clínico en consulta", Quantity = 1, UnitPrice = 35m,
            ChargedConsultationFolio = folio, ChargedUnits = 1
        };
        PatientSheetRows.SetSupplyLine(freeRow, PatientSheetRows.SupplyLine.Free(freeRow.SupplyName));
        cases.Add(Case("escribir · insumo fuera del inventario (cargo libre)", new JsonObject
        {
            ["column"] = PatientSheetRows.SuppliesColumn, ["name"] = freeRow.SupplyName, ["quantity"] = 1, ["folio"] = folio, ["rowId"] = kept
        }, WrittenRow(freeRow)));

        cases.Add(Case("escribir · insumo sin consulta (no se carga a ninguna cuenta)", new JsonObject
        {
            ["column"] = PatientSheetRows.SuppliesColumn, ["product"] = ProductsNode(new[] { gauze })[0]!.DeepClone(), ["quantity"] = 1, ["rowId"] = kept
        }, WrittenRow(new PatientSupplyItem { RowId = rowId, SupplyName = PatientSheetRows.SupplyDisplayName(gauze), Instructions = "Uso clínico en consulta", Quantity = 1, UnitPrice = 35m })));

        var curacion = services[0];
        cases.Add(Case("escribir · servicio del catálogo", new JsonObject
        {
            ["column"] = PatientSheetRows.ServicesColumn, ["service"] = ServicesNode(new[] { curacion })[0]!.DeepClone(), ["quantity"] = 1, ["folio"] = folio, ["rowId"] = kept
        }, WrittenRow(new PatientServiceItem
        {
            RowId = rowId, ServiceId = curacion.Id, ServiceSyncId = curacion.SyncId, ServiceCode = curacion.Code, ServiceName = curacion.Name, Category = "Curaciones",
            Notes = "Procedimiento clínico en consulta", Price = 250m, Quantity = 1, ChargedConsultationFolio = folio, ChargedUnits = 1
        })));
        cases.Add(Case("escribir · servicio fuera del catálogo (cargo libre)", new JsonObject
        {
            ["column"] = PatientSheetRows.ServicesColumn, ["name"] = "Retiro de puntos a domicilio", ["quantity"] = 1, ["folio"] = folio, ["rowId"] = kept
        }, WrittenRow(new PatientServiceItem
        {
            RowId = rowId, ServiceCode = "SERV-001", ServiceName = "Retiro de puntos a domicilio", Category = "Consulta", Notes = "Procedimiento clínico en consulta",
            Price = 200m, Quantity = 1, ChargedConsultationFolio = folio, ChargedUnits = 1
        })));
        cases.Add(Case("escribir · renglón de receta", new JsonObject
        {
            ["column"] = PatientSheetRows.PrescriptionColumn, ["rowId"] = kept
        }, WrittenRow(new PatientPrescriptionItem
        {
            RowId = rowId, MedicineName = "Paracetamol 500 mg tabletas", Dosage = "1 tableta cada 8 horas por 3 días", RecommendedQuantity = 1,
            Presentation = "Caja con 20 tabletas", DispenseInClinic = true
        })));

        // ------------------------------------------------ con qué renglón de la cuenta se cargó un insumo
        var refs = products.Select(p => new CatalogRef(CatalogRefKind.Product, p.Id, p.SyncId, p.Code, p.Name, p.IsActive)).ToList();
        var display = PatientSheetRows.SupplyDisplayName(gauze);
        foreach (var (name, pcOnly, row) in new (string, bool, PatientSupplyItem)[]
                 {
                     ("cargo libre: el de su nombre", false,
                         new PatientSupplyItem { SupplyName = "Férula de aluminio a la medida", ChargedLineKind = "libre", ChargedLineId = -1_234_567 }),
                     ("lo cargó otro equipo: por el SyncId, aunque su Id local sea otro", false,
                         new PatientSupplyItem { SupplyName = display, ChargedLineKind = "producto", ChargedLineSyncId = GauzeSyncId, ChargedLineId = 4 }),
                     ("el Id del otro equipo es aquí otro producto: manda el SyncId", false,
                         new PatientSupplyItem { SupplyName = display, ChargedLineKind = "producto", ChargedLineSyncId = GauzeSyncId, ChargedLineId = 12 }),
                     ("el producto ya no está activo: el de este equipo con ese SyncId", false,
                         new PatientSupplyItem { SupplyName = "Venda de gasa 5 cm (Rollo)", ChargedLineKind = "producto", ChargedLineSyncId = RetiredSyncId, ChargedLineId = 13 }),
                     ("producto inactivo con el Id de otro equipo: el de este equipo con ese SyncId", false,
                         new PatientSupplyItem { SupplyName = "Venda de gasa 5 cm (Rollo)", ChargedLineKind = "producto", ChargedLineSyncId = RetiredSyncId, ChargedLineId = 7 }),
                     ("SyncId que no existe aquí: por nombre", false,
                         new PatientSupplyItem { SupplyName = "Alcohol Etílico 70% (Botella de 1000 ml)", ChargedLineKind = "producto", ChargedLineSyncId = ForeignSyncId, ChargedLineId = 11 }),
                     ("renglón viejo con el Id de un producto de aquí con el mismo nombre", true,
                         new PatientSupplyItem { SupplyName = "gasa esteril 10x10 cm (paquete con 10 piezas)", ChargedLineId = 11 }),
                     ("renglón viejo con un Id que aquí es otro producto: por nombre", false,
                         new PatientSupplyItem { SupplyName = display, ChargedLineId = 12 }),
                     ("renglón viejo de un cargo libre (Id negativo), aunque un producto se llame igual", false,
                         new PatientSupplyItem { SupplyName = "Gasa Estéril 10x10 cm", ChargedLineId = HandoffPayloadCodec.FreeChargeId("Gasa Estéril 10x10 cm") }),
                     ("producto que no existía al recibir la consulta: el cargo libre al que se tradujo, tal cual", false,
                         new PatientSupplyItem { SupplyName = "Venda de yeso 15 cm (Rollo)", ChargedLineKind = "producto", ChargedLineSyncId = ForeignSyncId, ChargedLineId = HandoffPayloadCodec.FreeChargeId("Venda de yeso 15 cm") }),
                     ("renglón anterior a v1.18.7 (sin Id): por nombre, también el genérico", false,
                         new PatientSupplyItem { SupplyName = "Etanol 70%" }),
                     ("nada coincide: cargo libre de su nombre", false,
                         new PatientSupplyItem { SupplyName = "Abatelenguas", ChargedLineId = 99 }),
                 })
        {
            var line = PatientSheetRows.ResolveSupplyLine(row, products, refs);
            var input = new JsonObject
            {
                ["row"] = new JsonObject
                {
                    ["SupplyName"] = row.SupplyName, ["ChargedLineKind"] = row.ChargedLineKind,
                    ["ChargedLineSyncId"] = row.ChargedLineSyncId is { } s ? SyncIds.Format(s) : null, ["ChargedLineId"] = row.ChargedLineId
                },
                ["products"] = ProductsNode(products)
            };
            if (pcOnly) input["pcOnly"] = true;
            cases.Add(Case($"renglón de la cuenta · insumo · {name}", input, LineNode(line, row.SupplyName)));
        }
        Assert.AreEqual(11, PatientSheetRows.ResolveSupplyLine(new PatientSupplyItem { SupplyName = display, ChargedLineSyncId = GauzeSyncId, ChargedLineId = 4 }, products, refs).LineId);
        Assert.AreEqual(13, PatientSheetRows.ResolveSupplyLine(new PatientSupplyItem { SupplyName = "x", ChargedLineSyncId = RetiredSyncId, ChargedLineId = 13 }, products, refs).LineId,
            "El Id que anotó este equipo se reconoce aunque el producto ya no esté activo");

        // ------------------------------------------------ con qué servicio se cargó (null = cargo libre)
        foreach (var (name, pcOnly, row) in new (string, bool, PatientServiceItem)[]
                 {
                     ("por el SyncId, aunque su Id local sea otro", false,
                         new PatientServiceItem { ServiceSyncId = CuracionSyncId, ServiceId = 2, ServiceName = "Curación Menor y Asepsia" }),
                     ("SyncId que no existe aquí: por nombre normalizado", false,
                         new PatientServiceItem { ServiceSyncId = ForeignSyncId, ServiceId = 21, ServiceName = "monitoreo de glucemia capilar" }),
                     ("servicio inactivo: cargo libre", false,
                         new PatientServiceItem { ServiceSyncId = SuturaSyncId, ServiceId = 23, ServiceName = "Sutura de Herida Simple" }),
                     ("fuera del catálogo (sin SyncId y con Id 0): cargo libre aunque otro se llame igual", false,
                         new PatientServiceItem { ServiceId = 0, ServiceName = "Curación Menor y Asepsia" }),
                     ("renglón viejo con el Id de un servicio de aquí con el mismo nombre", false,
                         new PatientServiceItem { ServiceId = 22, ServiceName = "Monitoreo de Glucemia Capilar" }),
                     ("renglón viejo con un Id que aquí es otro servicio: por nombre", false,
                         new PatientServiceItem { ServiceId = 21, ServiceName = "Monitoreo de Glucemia Capilar" }),
                     ("renglón viejo que no está en el catálogo: cargo libre", false,
                         new PatientServiceItem { ServiceId = 5, ServiceName = "Retiro de puntos a domicilio" }),
                 })
        {
            var service = PatientSheetRows.ResolveService(row, services);
            var input = new JsonObject
            {
                ["row"] = new JsonObject
                {
                    ["ServiceName"] = row.ServiceName, ["ServiceSyncId"] = row.ServiceSyncId is { } s ? SyncIds.Format(s) : null, ["ServiceId"] = row.ServiceId
                },
                ["services"] = ServicesNode(services)
            };
            if (pcOnly) input["pcOnly"] = true;
            cases.Add(Case($"renglón de la cuenta · servicio · {name}", input, service == null
                ? new JsonObject { ["line"] = HandoffPayloadCodec.LineFree, ["name"] = row.ServiceName, ["freeChargeId"] = HandoffPayloadCodec.FreeChargeId(row.ServiceName) }
                : new JsonObject { ["line"] = HandoffPayloadCodec.LineService, ["serviceId"] = service.Id, ["syncId"] = SyncIds.Format(service.SyncId) }));
        }

        ServiceRowSupplyCases(cases, refs);

        CheckOrWrite("contracts/golden/synced-json-columns.json", VectorFile("synced-json-columns", cases,
            "Hojas del paciente (paso 8.18; los insumos de un servicio desde la revisión 14): Patients.ActivePrescriptionJson, ActiveSuppliesJson y ActiveServicesJson son arreglos JSON de renglones con nombres PascalCase (se leen sin distinguir mayúsculas) y acentos sin escapar; '' es una hoja vacía. Los campos de cada columna, en su 'note' de sync/schema.json.",
            "RowId (GUID en minúsculas): uno nuevo se toma al azar. Al leer, el renglón sin RowId, con el GUID vacío o con uno que ya usó otro renglón de la misma hoja recibe uuid5(tpvc, 'sheetrow:' + SyncId del paciente en minúsculas + ':' + columna + ':' + posición desde 0 + ':' + el nombre tal cual: MedicineName, SupplyName o ServiceName); 'uuid5Name' es ese nombre. Se guarda con la siguiente escritura de la hoja: no hay relleno al arrancar.",
            "Leer y volver a escribir: 'expected' es la hoja que escribe la PC al guardarla sin cambios. Los campos que no conoce se conservan (también objetos, arreglos y nulos), después de los suyos. Un campo con valor por omisión no se escribe (Presentation, ChargedConsultationFolio, ChargedUnits, ChargedLineId, ChargedLineKind, ChargedLineSyncId, ServiceSyncId); se comparan como JSON, no como texto.",
            "Escribir: un insumo cargado a la cuenta lleva el folio de la consulta, sus piezas, ChargedLineKind ('producto' o 'libre') y, si es producto, ChargedLineSyncId. Un servicio del catálogo lleva ServiceSyncId. ChargedLineId y ServiceId son los Id locales de la PC, para las PC anteriores: Kotlin no los escribe (ServiceId 0) y no los usa.",
            "Renglón de la cuenta: el insumo se busca, en este orden, por 'libre' (el cargo libre de su nombre); por un ChargedLineId negativo, que es el cargo libre con ese Id tal cual (también en un producto: el que no existía al recibir la consulta; sale de un nombre y es igual en todos los equipos); por ChargedLineSyncId (el producto activo con ese SyncId o, si no hay, el de este equipo con ese SyncId aunque esté inactivo); por el Id viejo positivo solo si ese producto local activo tiene el mismo nombre normalizado (su nombre o 'Nombre (Presentación)'); por nombre (nombre, 'Nombre (Presentación)', genérico o el renglón normado de la receta, sin distinguir mayúsculas) y, si no, cargo libre. El servicio: por ServiceSyncId (activo); sin él y con ServiceId 0 es un cargo libre; el Id viejo con el mismo nombre normalizado; el servicio activo con el mismo nombre normalizado; si no, cargo libre.",
            "Kotlin compara 'syncId' (o la clase 'libre' en un cargo libre); 'productId' y 'serviceId' son los Id locales de la PC, y 'freeChargeId' el Id del cargo libre en la PC (sale de un nombre normalizado: HandoffPayloadCodec.FreeChargeId). Un ChargedLineId negativo no es local: es un cargo libre en cualquier equipo, y Kotlin también lo lee así. Los casos con 'pcOnly' dependen de un Id local positivo de la PC (ChargedLineId o ServiceId) y no le aplican: Kotlin no tiene esos Id y pasa al nombre.",
            "Al recibir una consulta, la PC traduce a sus Id los renglones con el formato de la revisión 10 que esa consulta cargó (ChargedConsultationFolio igual al folio; ChargedLineKind en un insumo, ServiceSyncId en un servicio): un insumo cargado como producto toma el Id del renglón de la cuenta recibida con su SyncId, ya traducido (el cargo libre del nombre de ese renglón si el producto no existe aquí); lo demás, con las reglas de arriba. Cambian ChargedLineId y ServiceId; la clase y los SyncId no. Un renglón viejo conserva los Id de la PC anterior que lo escribió, que los usa tal cual si la consulta le regresa, salvo un insumo que aquí es un cargo libre que la cuenta recibida sí tiene (el producto no existe en este equipo): queda con ese Id negativo, que esa PC también entiende.",
            "Solo en la PC (Kotlin no tiene Id locales): antes de buscar, un renglón viejo (sin clase ni SyncId, con un Id positivo) cuyo Id tiene en la cuenta abierta un renglón con su mismo nombre (sin distinguir mayúsculas) lo cargó este equipo antes de la revisión 10, y es ese renglón aunque el producto o el servicio se haya renombrado o desactivado; el Id de otro equipo nunca coincide con un renglón de esta cuenta con el mismo nombre. Si su Id no está en la cuenta (trae el de la PC que lo cargó), un insumo viejo es el único renglón de producto de la cuenta con su mismo nombre; con ninguno o con varios, se busca como arriba. + / − en un renglón viejo le pone la clase y el SyncId y conserva su ChargedLineId; en un cargo libre carga el mismo renglón libre que ya tiene, aunque se llame distinto.",
            "Insumos de un servicio (revisión 14, Services/Rules/ServiceRowSupplies): un renglón de ActiveServicesJson que se cargó a la cuenta lleva Supplies, lo que cargó además del servicio. 'supplies' en 'expected' va en forma canónica (contracts/golden/canonical-json.json: cada insumo con SupplyName, Quantity, UnitPrice, ChargedLineKind y ChargedLineSyncId, sin ChargedLineId, que es el Id local de la PC y Kotlin no escribe); el caso 'escribir' muestra el texto que escribe la PC. Quantity son las piezas de todas las unidades juntas.",
            "Empezar: al cargar un servicio del catálogo, su lista son sus insumos por defecto ('defaults', con 'perUnit' = max(1, Quantity) de ServiceDefaultProducts) por las unidades: un insumo por producto (por su SyncId; dos renglones por defecto del mismo producto se suman), en el orden de la configuración, con el nombre y el precio del producto, clase 'producto' y su SyncId. Un servicio sin insumos por defecto o un cargo libre empieza con []. 'charges' es lo que se carga a la cuenta: el servicio ('servicio', por sus unidades) y cada insumo ('producto' con su SyncId, o 'libre' con su nombre).",
            "Ajustar (solo para esta consulta; la configuración del servicio no cambia): 'requested' son las piezas nuevas del insumo ('add' = agregar un producto del inventario: si la lista ya lo tiene, una pieza más; si no, entra al final con una). Si suben, solo caben min(lo pedido, existencias − lo que ya está en la cuenta de ese producto, de cualquier hoja, servicio o receta), nunca menos de 0 ('stock' e 'inAccount'); si cabe menos de lo pedido, 'warning' (\"No hay más existencias de …\") y se carga lo que quepa. 'charge' es lo que se carga (positivo) o se descuenta (negativo) de su renglón de la cuenta; en 0 piezas el insumo sale de la lista.",
            "Unidades: una más ('units' de antes a después) carga el servicio y los insumos por defecto de una unidad: cada uno se suma al insumo de la lista del mismo producto (por su SyncId) o entra al final. Una menos descuenta el servicio y los de una unidad, cada uno hasta lo que tenga la lista (un insumo que llega a 0 sale). Lo que el médico ajustó a mano (piezas de más, otro producto, uno quitado) se queda. En 0 unidades se quita el renglón.",
            "Quitar el renglón: solo si lo cargó la consulta en curso (su ChargedConsultationFolio es el folio de la consulta en curso; uno de una consulta anterior no descuenta nada). Con lista: el servicio por ChargedUnits y cada insumo de la lista con sus piezas. Sin lista (un renglón de la revisión 13 o anterior): como antes, el servicio y los insumos por defecto que el servicio tiene hoy por ChargedUnits.",
            "Cada insumo de la lista busca su renglón de la cuenta con las reglas de un insumo de la hoja (los casos 'renglón de la cuenta · insumo de un servicio'), y al recibir una consulta se traduce como ellos (ChargedLineId al renglón de la cuenta recibida con su SyncId, o al cargo libre al que se tradujo), aunque el servicio sea un renglón viejo o un cargo libre. Quien reescribe la hoja conserva los campos que no conoce de cada insumo."));
    }

    // ================================================================== insumos de un servicio (revisión 14)

    private static readonly Guid SyringeSyncId = new("a1b2c3d4-0000-4000-8000-000000000005");

    /// <summary>Los insumos con precio y existencias de los casos de un servicio.</summary>
    private static (Product Gauze, Product Alcohol, Product Syringe) ServiceSupplyProducts() => (
        new Product { Id = 11, SyncId = GauzeSyncId, Code = "CUR-001", Name = "Gasa Estéril 10x10 cm", Presentation = "Paquete con 10 piezas", Price = 18m, CurrentStock = 10 },
        new Product { Id = 12, SyncId = AlcoholSyncId, Code = "ANT-001", Name = "Alcohol Etílico 70%", Presentation = "Botella de 1000 ml", Price = 50m, CurrentStock = 3 },
        new Product { Id = 16, SyncId = SyringeSyncId, Code = "INS-002", Name = "Jeringa Desechable 5 ml", Presentation = "Pieza estéril", Price = 12m, CurrentStock = 40 });

    private static JsonObject SupplyProductNode(Product p) => new()
    {
        ["syncId"] = SyncIds.Format(p.SyncId), ["name"] = p.Name, ["price"] = CanonicalJson.FromValue(p.Price), ["stock"] = p.CurrentStock
    };

    private static JsonObject ServiceNode(ClinicalService service) => new()
    {
        ["syncId"] = SyncIds.Format(service.SyncId), ["name"] = service.Name, ["price"] = CanonicalJson.FromValue(service.Price),
        ["defaults"] = new JsonArray(service.DefaultProducts.Select(dp => (JsonNode)new JsonObject
        {
            ["product"] = SupplyProductNode(dp.Product!), ["perUnit"] = ServiceRowSupplies.PiecesPerUnit(dp)
        }).ToArray())
    };

    private static JsonArray SuppliesNode(IEnumerable<PatientServiceSupply> list) =>
        new(list.Select(e => (JsonNode)PatientSheetCanonical.CanonicalServiceSupply(e)).ToArray());

    private static JsonObject ChargeNode(PatientServiceSupply entry, int pieces) => entry.ChargedLineKind == HandoffPayloadCodec.LineFree
        ? new JsonObject { ["line"] = HandoffPayloadCodec.LineFree, ["name"] = entry.SupplyName, ["pieces"] = pieces }
        : new JsonObject { ["line"] = HandoffPayloadCodec.LineProduct, ["syncId"] = entry.ChargedLineSyncId is { } s ? SyncIds.Format(s) : null, ["pieces"] = pieces };

    private static JsonObject ServiceChargeNode(ClinicalService? service, string name, int pieces) => service == null
        ? new JsonObject { ["line"] = HandoffPayloadCodec.LineFree, ["name"] = name, ["pieces"] = pieces }
        : new JsonObject { ["line"] = HandoffPayloadCodec.LineService, ["syncId"] = SyncIds.Format(service.SyncId), ["pieces"] = pieces };

    private static List<PatientServiceSupply> CloneSupplies(IEnumerable<PatientServiceSupply> list) =>
        list.Select(e => new PatientServiceSupply
        {
            SupplyName = e.SupplyName, Quantity = e.Quantity, UnitPrice = e.UnitPrice, ChargedLineId = e.ChargedLineId,
            ChargedLineKind = e.ChargedLineKind, ChargedLineSyncId = e.ChargedLineSyncId
        }).ToList();

    private static List<PatientServiceSupply> Supplies(params (Product Product, int Pieces)[] entries) =>
        entries.Select(e => ServiceRowSupplies.NewEntry(e.Product, e.Pieces)).ToList();

    private static void ServiceRowSupplyCases(JsonArray cases, List<CatalogRef> refs)
    {
        var (gauze, alcohol, syringe) = ServiceSupplyProducts();
        var curacion = new ClinicalService { Id = 21, SyncId = CuracionSyncId, Code = "SERV-002", Name = "Curación Menor y Asepsia", Price = 250m, Category = "Curaciones" };
        curacion.DefaultProducts.Add(new ServiceDefaultProduct { Product = gauze, Quantity = 1 });
        curacion.DefaultProducts.Add(new ServiceDefaultProduct { Product = alcohol, Quantity = 1 });
        var sutura = new ClinicalService { Id = 23, SyncId = SuturaSyncId, Code = "SERV-003", Name = "Sutura de Herida Simple", Price = 450m };
        sutura.DefaultProducts.Add(new ServiceDefaultProduct { Product = gauze, Quantity = 2 });
        sutura.DefaultProducts.Add(new ServiceDefaultProduct { Product = syringe, Quantity = 0 });
        sutura.DefaultProducts.Add(new ServiceDefaultProduct { Product = gauze, Quantity = 1 });
        var glucemia = new ClinicalService { Id = 22, SyncId = GlucemiaSyncId, Code = "SERV-004", Name = "Monitoreo de Glucemia Capilar", Price = 80m };
        const string folio = "CNS-20261014-103015";

        // ------------------------------------------------ empezar con los insumos por defecto
        foreach (var (name, service, units) in new (string, ClinicalService, int)[]
                 {
                     ("los insumos por defecto por las unidades", curacion, 2),
                     ("dos renglones por defecto del mismo producto se suman; uno con Quantity 0 carga 1 por unidad", sutura, 1),
                     ("servicio sin insumos por defecto: lista vacía", glucemia, 1),
                 })
        {
            var list = new List<PatientServiceSupply>();
            var charges = new JsonArray(ServiceChargeNode(service, service.Name, units));
            foreach (var pieces in ServiceRowSupplies.AddUnits(list, service.DefaultProducts, units)) charges.Add(ChargeNode(pieces.Entry, pieces.Count));
            CollectionAssert.AreEqual(list.Select(e => e.Quantity).ToList(), ServiceRowSupplies.FromDefaults(service.DefaultProducts, units).Select(e => e.Quantity).ToList());
            cases.Add(Case($"insumos de un servicio · empezar · {name}", new JsonObject
            {
                ["service"] = ServiceNode(service), ["units"] = units
            }, new JsonObject { ["supplies"] = SuppliesNode(list), ["charges"] = charges }));
        }
        cases.Add(Case("insumos de un servicio · empezar · servicio fuera del catálogo (cargo libre): lista vacía", new JsonObject
        {
            ["service"] = null, ["name"] = "Retiro de puntos a domicilio", ["units"] = 1
        }, new JsonObject { ["supplies"] = new JsonArray(), ["charges"] = new JsonArray(ServiceChargeNode(null, "Retiro de puntos a domicilio", 1)) }));

        // ------------------------------------------------ lo que escribe la PC
        var written = new PatientServiceItem
        {
            RowId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), ServiceId = curacion.Id, ServiceSyncId = curacion.SyncId, ServiceCode = curacion.Code,
            ServiceName = curacion.Name, Category = curacion.Category, Notes = "Procedimiento clínico en consulta", Price = curacion.Price, Quantity = 1,
            ChargedConsultationFolio = folio, ChargedUnits = 1, Supplies = ServiceRowSupplies.FromDefaults(curacion.DefaultProducts, 1)
        };
        cases.Add(Case("insumos de un servicio · escribir · servicio del catálogo con su lista (ChargedLineId es el Id local de la PC)", new JsonObject
        {
            ["column"] = PatientSheetRows.ServicesColumn, ["service"] = ServiceNode(curacion), ["units"] = 1, ["folio"] = folio
        }, WrittenRow(written)));

        // ------------------------------------------------ ajustar solo para esta consulta
        foreach (var (name, start, target, requested, inAccount) in new (string, List<PatientServiceSupply>, Product, int?, int)[]
                 {
                     ("+ una pieza", Supplies((gauze, 1), (alcohol, 1)), gauze, 2, 1),
                     ("− una pieza", Supplies((gauze, 2), (alcohol, 1)), gauze, 1, 2),
                     ("la X: el insumo sale de la lista", Supplies((gauze, 1), (alcohol, 1)), alcohol, 0, 1),
                     ("+ sin existencias: no carga y avisa", Supplies((gauze, 1), (alcohol, 1)), alcohol, 2, 3),
                     ("+ varias piezas: solo carga las que caben y avisa", Supplies((gauze, 1), (alcohol, 1)), alcohol, 4, 2),
                     ("agregar un producto que no tiene: entra al final con una pieza", Supplies((gauze, 1), (alcohol, 1)), syringe, null, 0),
                     ("agregar un producto que ya tiene: una pieza más", Supplies((gauze, 1), (alcohol, 1)), gauze, null, 1),
                 })
        {
            var input = new JsonObject
            {
                ["supplies"] = SuppliesNode(start), ["product"] = SupplyProductNode(target),
                ["requested"] = requested is int r ? r : "add", ["inAccount"] = inAccount
            };
            var list = CloneSupplies(start);
            var entry = ServiceRowSupplies.Find(list, target);
            int current = entry?.Quantity ?? 0;
            int wanted = requested ?? current + 1;
            int difference = wanted - current;
            bool warning = false;
            if (difference > 0)
            {
                int fit = ServiceRowSupplies.PiecesThatFit(difference, target.CurrentStock, inAccount);
                warning = fit < difference;
                difference = fit;
            }
            if (difference != 0)
            {
                if (entry == null)
                {
                    entry = ServiceRowSupplies.NewEntry(target, 0);
                    list.Add(entry);
                }
                entry.Quantity += difference;
                list.RemoveAll(e => e.Quantity <= 0);
            }
            cases.Add(Case($"insumos de un servicio · ajustar · {name}", input, new JsonObject
            {
                ["supplies"] = SuppliesNode(list), ["charge"] = difference, ["warning"] = warning
            }));
        }

        // ------------------------------------------------ cambiar las unidades
        foreach (var (name, start, from, to) in new (string, List<PatientServiceSupply>, int, int)[]
                 {
                     ("una más: suma los insumos por defecto de una unidad y lo ajustado se queda (el alcohol que se quitó vuelve al final)",
                         Supplies((gauze, 2), (syringe, 1)), 1, 2),
                     ("una menos: descuenta los de una unidad; lo agregado a mano se queda", Supplies((gauze, 3), (syringe, 1), (alcohol, 2)), 2, 1),
                     ("una menos: cada insumo solo hasta lo que tenga la lista", Supplies((gauze, 1)), 2, 1),
                 })
        {
            var list = CloneSupplies(start);
            var charges = new JsonArray(ServiceChargeNode(curacion, curacion.Name, to - from));
            var pieces = to > from
                ? ServiceRowSupplies.AddUnits(list, curacion.DefaultProducts, to - from)
                : ServiceRowSupplies.RemoveUnits(list, curacion.DefaultProducts, from - to);
            foreach (var p in pieces) charges.Add(ChargeNode(p.Entry, to > from ? p.Count : -p.Count));
            cases.Add(Case($"insumos de un servicio · unidades · {name}", new JsonObject
            {
                ["service"] = ServiceNode(curacion), ["supplies"] = SuppliesNode(start), ["units"] = new JsonObject { ["from"] = from, ["to"] = to }
            }, new JsonObject { ["supplies"] = SuppliesNode(list), ["charges"] = charges }));
        }

        // ------------------------------------------------ quitar el renglón
        foreach (var (name, supplies, rowFolio, units) in new (string, List<PatientServiceSupply>?, string?, int)[]
                 {
                     ("con lista: el servicio por sus unidades y cada insumo con sus piezas (lo ajustado incluido)", Supplies((gauze, 3), (syringe, 1)), folio, 1),
                     ("sin lista (revisión 13 o anterior): como antes, el servicio y sus insumos por defecto de hoy por sus unidades", null, folio, 2),
                     ("de una consulta anterior: no descuenta nada de la consulta en curso", Supplies((gauze, 1), (alcohol, 1)), "CNS-20261001-090000", 1),
                     ("con lista vacía: solo el servicio", new List<PatientServiceSupply>(), folio, 1),
                 })
        {
            var row = new PatientServiceItem
            {
                ServiceSyncId = curacion.SyncId, ServiceName = curacion.Name, Quantity = units, ChargedConsultationFolio = rowFolio, ChargedUnits = units,
                Supplies = supplies
            };
            var charges = new JsonArray();
            if (ServiceRowSupplies.IsFromConsultation(row, folio))
            {
                charges.Add(ServiceChargeNode(curacion, curacion.Name, -row.ChargedUnits));
                foreach (var p in ServiceRowSupplies.UnchargeAll(row.Supplies ?? ServiceRowSupplies.LegacySupplies(curacion, row.ChargedUnits)))
                {
                    charges.Add(ChargeNode(p.Entry, -p.Count));
                }
            }
            cases.Add(Case($"insumos de un servicio · quitar el renglón · {name}", new JsonObject
            {
                ["service"] = ServiceNode(curacion), ["currentFolio"] = folio,
                ["row"] = new JsonObject
                {
                    ["ChargedConsultationFolio"] = rowFolio, ["ChargedUnits"] = units, ["Supplies"] = supplies == null ? null : SuppliesNode(supplies)
                }
            }, new JsonObject { ["charges"] = charges }));
        }

        // ------------------------------------------------ con qué renglón de la cuenta se cargó cada insumo
        var products = new List<Product> { gauze, alcohol, syringe };
        foreach (var (name, entry) in new (string, PatientServiceSupply)[]
                 {
                     ("por el SyncId, aunque su Id local sea otro",
                         new PatientServiceSupply { SupplyName = gauze.Name, ChargedLineKind = "producto", ChargedLineSyncId = GauzeSyncId, ChargedLineId = 4 }),
                     ("producto que no existía al recibir la consulta: el cargo libre al que se tradujo, tal cual",
                         new PatientServiceSupply { SupplyName = "Venda de yeso 15 cm", ChargedLineKind = "producto", ChargedLineSyncId = ForeignSyncId, ChargedLineId = HandoffPayloadCodec.FreeChargeId("Venda de yeso 15 cm") }),
                     ("SyncId que no existe aquí: por nombre", new PatientServiceSupply { SupplyName = "Jeringa Desechable 5 ml", ChargedLineKind = "producto", ChargedLineSyncId = ForeignSyncId }),
                     ("cargo libre: el de su nombre", new PatientServiceSupply { SupplyName = "Férula de aluminio a la medida", ChargedLineKind = "libre", UnitPrice = 35m }),
                 })
        {
            var line = PatientSheetRows.ResolveSupplyLine(entry, products, refs);
            cases.Add(Case($"renglón de la cuenta · insumo de un servicio · {name}", new JsonObject
            {
                ["supply"] = new JsonObject
                {
                    ["SupplyName"] = entry.SupplyName, ["ChargedLineKind"] = entry.ChargedLineKind,
                    ["ChargedLineSyncId"] = entry.ChargedLineSyncId is { } s ? SyncIds.Format(s) : null, ["ChargedLineId"] = entry.ChargedLineId
                },
                ["products"] = new JsonArray(products.Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id, ["syncId"] = SyncIds.Format(p.SyncId), ["name"] = p.Name, ["active"] = p.IsActive
                }).ToArray())
            }, LineNode(line, entry.SupplyName)));
        }

        // ------------------------------------------------ leer y volver a escribir
        const string withList = """[{"RowId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","ServiceId":21,"ServiceSyncId":"b1b2c3d4-0000-4000-8000-000000000001","ServiceCode":"SERV-002","ServiceName":"Curación Menor y Asepsia","Category":"Curaciones","Notes":"","Price":250,"Quantity":1,"ChargedConsultationFolio":"CNS-20261014-103015","ChargedUnits":1,"Supplies":[{"SupplyName":"Gasa Estéril 10x10 cm","Quantity":2,"UnitPrice":18,"ChargedLineKind":"producto","ChargedLineSyncId":"a1b2c3d4-0000-4000-8000-000000000001","Lote":{"Id":"L-7","Caduca":"2027-01"}},{"SupplyName":"Férula","Quantity":1,"UnitPrice":35,"ChargedLineKind":"libre"}],"Futuro":true}]""";
        var rewritten = RewriteSheet(PatientSheetRows.ServicesColumn, withList);
        Assert.AreEqual(rewritten, RewriteSheet(PatientSheetRows.ServicesColumn, rewritten));
        StringAssert.Contains(rewritten, "\"Lote\":{\"Id\":\"L-7\",\"Caduca\":\"2027-01\"}");
        cases.Add(Case("insumos de un servicio · leer y volver a escribir · la lista y los campos desconocidos de cada insumo se quedan", new JsonObject
        {
            ["patientSyncId"] = SyncIds.Format(SheetPatient), ["column"] = PatientSheetRows.ServicesColumn, ["json"] = withList
        }, JsonNode.Parse(rewritten)));
    }
}
