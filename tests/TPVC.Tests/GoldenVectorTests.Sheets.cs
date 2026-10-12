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

        CheckOrWrite("contracts/golden/synced-json-columns.json", VectorFile("synced-json-columns", cases,
            "Hojas del paciente (paso 8.18): Patients.ActivePrescriptionJson, ActiveSuppliesJson y ActiveServicesJson son arreglos JSON de renglones con nombres PascalCase (se leen sin distinguir mayúsculas) y acentos sin escapar; '' es una hoja vacía. Los campos de cada columna, en su 'note' de sync/schema.json.",
            "RowId (GUID en minúsculas): uno nuevo se toma al azar. Al leer, el renglón sin RowId, con el GUID vacío o con uno que ya usó otro renglón de la misma hoja recibe uuid5(tpvc, 'sheetrow:' + SyncId del paciente en minúsculas + ':' + columna + ':' + posición desde 0 + ':' + el nombre tal cual: MedicineName, SupplyName o ServiceName); 'uuid5Name' es ese nombre. Se guarda con la siguiente escritura de la hoja: no hay relleno al arrancar.",
            "Leer y volver a escribir: 'expected' es la hoja que escribe la PC al guardarla sin cambios. Los campos que no conoce se conservan (también objetos, arreglos y nulos), después de los suyos. Un campo con valor por omisión no se escribe (Presentation, ChargedConsultationFolio, ChargedUnits, ChargedLineId, ChargedLineKind, ChargedLineSyncId, ServiceSyncId); se comparan como JSON, no como texto.",
            "Escribir: un insumo cargado a la cuenta lleva el folio de la consulta, sus piezas, ChargedLineKind ('producto' o 'libre') y, si es producto, ChargedLineSyncId. Un servicio del catálogo lleva ServiceSyncId. ChargedLineId y ServiceId son los Id locales de la PC, para las PC anteriores: Kotlin no los escribe (ServiceId 0) y no los usa.",
            "Renglón de la cuenta: el insumo se busca, en este orden, por 'libre' (el cargo libre de su nombre); por un ChargedLineId negativo, que es el cargo libre con ese Id tal cual (también en un producto: el que no existía al recibir la consulta; sale de un nombre y es igual en todos los equipos); por ChargedLineSyncId (el producto activo con ese SyncId o, si no hay, el de este equipo con ese SyncId aunque esté inactivo); por el Id viejo positivo solo si ese producto local activo tiene el mismo nombre normalizado (su nombre o 'Nombre (Presentación)'); por nombre (nombre, 'Nombre (Presentación)', genérico o el renglón normado de la receta, sin distinguir mayúsculas) y, si no, cargo libre. El servicio: por ServiceSyncId (activo); sin él y con ServiceId 0 es un cargo libre; el Id viejo con el mismo nombre normalizado; el servicio activo con el mismo nombre normalizado; si no, cargo libre.",
            "Kotlin compara 'syncId' (o la clase 'libre' en un cargo libre); 'productId' y 'serviceId' son los Id locales de la PC, y 'freeChargeId' el Id del cargo libre en la PC (sale de un nombre normalizado: HandoffPayloadCodec.FreeChargeId). Un ChargedLineId negativo no es local: es un cargo libre en cualquier equipo, y Kotlin también lo lee así. Los casos con 'pcOnly' dependen de un Id local positivo de la PC (ChargedLineId o ServiceId) y no le aplican: Kotlin no tiene esos Id y pasa al nombre.",
            "Al recibir una consulta, la PC traduce a sus Id los renglones con el formato de la revisión 10 que esa consulta cargó (ChargedConsultationFolio igual al folio; ChargedLineKind en un insumo, ServiceSyncId en un servicio): un insumo cargado como producto toma el Id del renglón de la cuenta recibida con su SyncId, ya traducido (el cargo libre del nombre de ese renglón si el producto no existe aquí); lo demás, con las reglas de arriba. Cambian ChargedLineId y ServiceId; la clase y los SyncId no. Un renglón viejo conserva los Id de la PC anterior que lo escribió, que los usa tal cual si la consulta le regresa, salvo un insumo que aquí es un cargo libre que la cuenta recibida sí tiene (el producto no existe en este equipo): queda con ese Id negativo, que esa PC también entiende.",
            "Solo en la PC (Kotlin no tiene Id locales): antes de buscar, un renglón viejo (sin clase ni SyncId, con un Id positivo) cuyo Id tiene en la cuenta abierta un renglón con su mismo nombre (sin distinguir mayúsculas) lo cargó este equipo antes de la revisión 10, y es ese renglón aunque el producto o el servicio se haya renombrado o desactivado; el Id de otro equipo nunca coincide con un renglón de esta cuenta con el mismo nombre. Si su Id no está en la cuenta (trae el de la PC que lo cargó), un insumo viejo es el único renglón de producto de la cuenta con su mismo nombre; con ninguno o con varios, se busca como arriba. + / − en un renglón viejo le pone la clase y el SyncId y conserva su ChargedLineId; en un cargo libre carga el mismo renglón libre que ya tiene, aunque se llame distinto."));
    }
}
