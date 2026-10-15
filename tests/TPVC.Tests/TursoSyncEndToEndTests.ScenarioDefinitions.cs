using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.25: los escenarios de varios equipos (contracts/scenarios/*.json). Aquí solo los pasos; qué queda en la nube y en
/// cada equipo lo dice el motor real al regenerar (TursoSyncEndToEndTests.Scenarios.cs). Los SyncId de los pasos son fijos
/// (10000000-0000-4000-8000-…); A es la PC principal (la «Sincronización completa» le da la identidad de la nube) y B se
/// une a ella (en la PC, otra computadora; en Kotlin, la tablet).
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private static string Fx(int n) => SyncIds.Format(FixtureId(n));

    /// <summary>Un paso: el equipo (o null en uno del arnés), la operación y sus datos.</summary>
    private static JsonObject Step(string? device, string op, JsonObject? data = null)
    {
        var step = new JsonObject();
        if (device != null) step["device"] = device;
        step["op"] = op;
        foreach (var (key, value) in data ?? new JsonObject()) step[key] = value?.DeepClone();
        return step;
    }

    private static JsonObject Values(params (string Column, string? Value)[] values)
    {
        var row = new JsonObject();
        foreach (var (column, value) in values) row[column] = value;
        return row;
    }

    private static JsonObject Create(string device, string table, params (string Column, string? Value)[] values) =>
        Step(device, "create", new JsonObject { ["table"] = table, ["row"] = Values(values) });

    private static JsonObject Edit(string device, string table, int id, params (string Column, string? Value)[] values) =>
        Step(device, "edit", new JsonObject { ["table"] = table, ["syncId"] = Fx(id), ["set"] = Values(values) });

    private static JsonObject Delete(string device, string table, int id) =>
        Step(device, "delete", new JsonObject { ["table"] = table, ["syncId"] = Fx(id) });

    private static JsonObject RawWrite(string device, string table, int id, string stamp, params (string Column, string? Value)[] values) =>
        Step(device, "rawWrite", new JsonObject { ["table"] = table, ["syncId"] = Fx(id), ["stamp"] = stamp, ["set"] = Values(values) });

    private static JsonObject Sync(string device, JsonObject? options = null) => Step(device, "sync", options);

    private static JsonObject Watch(params (string Table, string[] Columns)[] tables)
    {
        var watch = new JsonObject();
        foreach (var (table, columns) in tables) watch[table] = new JsonArray(columns.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        return watch;
    }

    private static JsonArray Steps(params JsonObject[] steps) => new(steps.Select(s => (JsonNode?)s).ToArray());

    /// <summary>A sube los datos con la «Sincronización completa» y B se une y los baja.</summary>
    private static JsonObject[] CloudOfAJoinedByB() => new[] { Step("A", "fullSync"), Step("B", "join"), Sync("B") };

    private static JsonObject[] CategoryAndProduct(int category, int product, string price, string stock = "12") => new[]
    {
        Create("A", "Categories", ("SyncId", Fx(category)), ("Name", "Analgésicos"), ("Icon", "Pill")),
        Create("A", "Products", ("SyncId", Fx(product)), ("Code", "PARA-500"), ("Name", "Paracetamol 500 mg"), ("CategorySyncId", Fx(category)),
            ("Price", price), ("CurrentStock", stock))
    };

    // ================================================================== los escenarios

    private static ScenarioDefinition LwwAndTies() => new(
        "lww-and-ties",
        new[] { "8.5", "8.6" },
        new[]
        {
            "Una edición en un equipo llega al otro; dos ediciones de la misma fila: gana la marca más nueva (LWW por fila, la nube con el upsert condicional).",
            "Empate (pasos 14 a 18): los dos equipos escriben la misma fila con la misma marca (rawWrite, T0+90s). La nube se queda con la primera que sube; a la segunda no la acepta (afecta 0 filas), el equipo vuelve a leer la fila de la nube y la aplica aunque las marcas empaten (SyncRules.ServerRowAppliesAfterReject, golden/lww.json), y no vuelve a subir nada.",
            "pending: cambios anotados sin subir; parked: filas apartadas; saved: cambios guardados por otra generación."
        },
        new[] { "A", "B" },
        Watch(("Categories", new[] { "Name" }), ("Products", new[] { "Code", "Name", "Price", "CategorySyncId" })),
        Steps(CategoryAndProduct(101, 102, "35.5")
            .Concat(CloudOfAJoinedByB())
            .Concat(new[]
            {
                Edit("B", "Products", 102, ("Price", "40")),
                Sync("B"),
                Sync("A"),
                Edit("A", "Products", 102, ("Price", "41")),
                Edit("B", "Products", 102, ("Price", "42")),
                Sync("A"),
                Sync("B"),
                Sync("A"),
                RawWrite("A", "Products", 102, "T0+90s", ("Price", "111.5")),
                RawWrite("B", "Products", 102, "T0+90s", ("Price", "222.5")),
                Sync("B"),
                Sync("A"),
                Sync("A")
            }).ToArray()));

    private static ScenarioDefinition Tombstones() => new(
        "tombstones",
        new[] { "8.5" },
        new[]
        {
            "Borrar contra editar. Pasos 7 a 11: B edita una nota y A la borra después: la lápida es más nueva y la fila desaparece en la nube y en B.",
            "Pasos 12 a 16: A borra la otra nota y B la edita después, sin haber bajado la lápida: la edición es más nueva que la lápida (el upsert solo se niega con una lápida igual o más nueva), así que la nota vuelve a existir en la nube y en A. La lápida se queda en la nube.",
            "Pasos 17 a 19: borrar el expediente borra en cascada su nota: cada fila borrada en cascada deja su lápida (los triggers del registro de cambios) y llega así a A."
        },
        new[] { "A", "B" },
        Watch(("Patients", new[] { "RecordNumber", "FullName" }), ("PatientConsultations", new[] { "PatientRecordSyncId", "Folio", "Diagnosis" })),
        Steps(new[]
            {
                Create("A", "Patients", ("SyncId", Fx(201)), ("RecordNumber", "EXP-2020-001"), ("FullName", "Ana Pérez Núñez"), ("Gender", "Femenino")),
                Create("A", "PatientConsultations", ("SyncId", Fx(202)), ("PatientRecordSyncId", Fx(201)), ("Folio", "CNS-20200115-001"),
                    ("ChiefComplaint", "Dolor de garganta"), ("Diagnosis", "Faringitis aguda (J02.9)")),
                Create("A", "PatientConsultations", ("SyncId", Fx(203)), ("PatientRecordSyncId", Fx(201)), ("Folio", "CNS-20200116-001"),
                    ("ChiefComplaint", "Tos seca"), ("Diagnosis", "Rinofaringitis (J00)")),
            }
            .Concat(CloudOfAJoinedByB())
            .Concat(new[]
            {
                Edit("B", "PatientConsultations", 202, ("Diagnosis", "Faringitis estreptocócica (J02.0)")),
                Delete("A", "PatientConsultations", 202),
                Sync("B"),
                Sync("A"),
                Sync("B"),
                Delete("A", "PatientConsultations", 203),
                Edit("B", "PatientConsultations", 203, ("Diagnosis", "Rinofaringitis aguda (J00)")),
                Sync("A"),
                Sync("B"),
                Sync("A"),
                Delete("B", "Patients", 201),
                Sync("B"),
                Sync("A")
            }).ToArray()));

    private static ScenarioDefinition ParkedRows() => new(
        "parked-rows",
        new[] { "8.4" },
        new[]
        {
            "La nube esconde el expediente y el lote (cloudHold: como si todavía no llegaran) y B baja todo: la nota sin su expediente (referencia obligatoria) y el movimiento con un lote que B no tiene (referencia opcional con un SyncId desconocido) se apartan (parked 2) en lugar de perderse o guardarse sin lote.",
            "Al volver las filas (cloudRelease, con un SyncedAtUtc nuevo), la siguiente sincronización las baja y aplica lo apartado al final de la descarga: el contador vuelve a 0 y el movimiento queda con su lote."
        },
        new[] { "A", "B" },
        Watch(("Patients", new[] { "FullName" }), ("PatientConsultations", new[] { "PatientRecordSyncId", "Diagnosis" }),
            ("ProductBatches", new[] { "ProductSyncId", "BatchNumber", "Quantity" }),
            ("InventoryMovements", new[] { "ProductSyncId", "BatchSyncId", "MovementType", "Quantity", "Delta" })),
        Steps(CategoryAndProduct(301, 302, "35.5", stock: "10")
            .Concat(new[]
            {
                Create("A", "ProductBatches", ("SyncId", Fx(303)), ("ProductSyncId", Fx(302)), ("BatchNumber", "L-2026-01"), ("Quantity", "10")),
                Create("A", "InventoryMovements", ("SyncId", Fx(304)), ("ProductSyncId", Fx(302)), ("BatchSyncId", Fx(303)), ("MovementType", "0"),
                    ("Quantity", "10"), ("Delta", "10"), ("StockBefore", "0"), ("StockAfter", "10")),
                Create("A", "Patients", ("SyncId", Fx(305)), ("RecordNumber", "EXP-2020-003"), ("FullName", "Paciente Apartado")),
                Create("A", "PatientConsultations", ("SyncId", Fx(306)), ("PatientRecordSyncId", Fx(305)), ("Diagnosis", "Cefalea tensional (G44.2)")),
                Step("A", "fullSync"),
                Step(null, "cloudHold", new JsonObject { ["table"] = "Patients", ["syncId"] = Fx(305) }),
                Step(null, "cloudHold", new JsonObject { ["table"] = "ProductBatches", ["syncId"] = Fx(303) }),
                Step("B", "join"),
                Sync("B"),
                Step(null, "cloudRelease", new JsonObject { ["table"] = "Patients" }),
                Step(null, "cloudRelease", new JsonObject { ["table"] = "ProductBatches" }),
                Sync("B")
            }).ToArray()));

    private static ScenarioDefinition ClockSkew() => new(
        "clock-skew",
        new[] { "8.5", "8.7" },
        new[]
        {
            "Desfase (pasos 5 a 12): la nube va 10 min adelante de los dos equipos (serverClock). Cada sincronización mide el desfase y desde ahí cada equipo marca con su hora más el desfase (golden/stamping.json): la edición más reciente en el tiempo (la de B) gana aunque los relojes de los equipos estén atrasados.",
            "Salto de reloj (pasos 13 a 17): el reloj de B salta 3 h adelante sin conexión y marca una edición así (rawWrite, T0+3h). Antes de subir, B repara las marcas más de 2 min adelante de la hora de la nube; si no, esa edición le ganaría a todas las de después. La de A, posterior, gana."
        },
        new[] { "A", "B" },
        Watch(("Products", new[] { "Price" })),
        Steps(CategoryAndProduct(401, 402, "35.5")
            .Concat(CloudOfAJoinedByB())
            .Concat(new[]
            {
                Step(null, "serverClock", new JsonObject { ["offsetSeconds"] = 600 }),
                Sync("A"),
                Sync("B"),
                Edit("A", "Products", 402, ("Price", "50")),
                Edit("B", "Products", 402, ("Price", "60")),
                Sync("B"),
                Sync("A"),
                Sync("B"),
                RawWrite("B", "Products", 402, "T0+3h", ("Price", "70")),
                Sync("B"),
                Edit("A", "Products", 402, ("Price", "80")),
                Sync("A"),
                Sync("B")
            }).ToArray()));

    private static ScenarioDefinition UploadCut() => new(
        "upload-cut",
        new[] { "8.5", "8.6" },
        new[]
        {
            "Corte a media subida (pasos 10 a 12): A sube 5 categorías en lotes de 2 (pushBatchSize) y la red se cae antes de la segunda petición de la subida (cut beforeApply). Cada petición es una transacción (BEGIN/COMMIT, V14): la primera quedó completa en la nube, la segunda no llegó. A no avanza su cursor de subida: la siguiente sincronización vuelve a mandar lo pendiente sin duplicar nada (el upsert por SyncId).",
            "Respuesta perdida (pasos 13 a 17): la primera petición de la subida sí se aplica pero la respuesta no llega (cut lostResponse). A lo cuenta como no subido y lo vuelve a mandar; la nube ya lo tiene con la misma marca (no lo acepta otra vez) y A lo relee y lo aplica (es la misma versión). B baja cada fila una vez."
        },
        new[] { "A", "B" },
        Watch(("Categories", new[] { "Name" })),
        Steps(new[] { Create("A", "Categories", ("SyncId", Fx(501)), ("Name", "Categoría 1"), ("Icon", "Folder")) }
            .Concat(CloudOfAJoinedByB())
            .Concat(Enumerable.Range(2, 5).Select(i => Create("A", "Categories", ("SyncId", Fx(500 + i)), ("Name", $"Categoría {i}"), ("Icon", "Folder"))))
            .Concat(new[]
            {
                Sync("A", new JsonObject { ["pushBatchSize"] = 2, ["cut"] = new JsonObject { ["uploadRequest"] = 2, ["mode"] = "beforeApply" } }),
                Sync("A"),
                Sync("B"),
                Create("A", "Categories", ("SyncId", Fx(507)), ("Name", "Categoría 7"), ("Icon", "Folder")),
                Create("A", "Categories", ("SyncId", Fx(508)), ("Name", "Categoría 8"), ("Icon", "Folder")),
                Sync("A", new JsonObject { ["cut"] = new JsonObject { ["uploadRequest"] = 1, ["mode"] = "lostResponse" } }),
                Sync("A"),
                Sync("B")
            }).ToArray()));

    private static ScenarioDefinition GenerationFence() => new(
        "generation-fence",
        new[] { "8.9" },
        new[]
        {
            "A guarda un respaldo (paso 7); después B registra un paciente y lo sube, A cambia un precio y lo sube, y B registra otro paciente que no alcanza a subir.",
            "A restaura el respaldo y reemplaza la nube con él (pcOnly): generación nueva, la nube queda igual al respaldo (sin el paciente de B ni el precio nuevo).",
            "B ve otra generación en la primera instrucción: guarda lo que no había subido (saved 1), se vacía y baja la nube nueva una sola vez. «Volver a aplicar» (reapplySaved) escribe lo guardado como una edición nueva (pending 1) que sube en la siguiente sincronización. Lo que B ya había subido antes del reemplazo se perdió con él (A lo guardó en su archivo de lo perdido; no está en este escenario)."
        },
        new[] { "A", "B" },
        Watch(("Patients", new[] { "RecordNumber", "FullName" }), ("Products", new[] { "Price" })),
        Steps(CategoryAndProduct(601, 602, "35.5")
            .Concat(new[] { Create("A", "Patients", ("SyncId", Fx(603)), ("RecordNumber", "EXP-2020-004"), ("FullName", "Ana Pérez Núñez")) })
            .Concat(CloudOfAJoinedByB())
            .Concat(new[]
            {
                Step("A", "backup"),
                Create("B", "Patients", ("SyncId", Fx(604)), ("RecordNumber", "EXP-2020-005"), ("FullName", "Paciente De B Después Del Respaldo")),
                Sync("B"),
                Edit("A", "Products", 602, ("Price", "99.5")),
                Sync("A"),
                Create("B", "Patients", ("SyncId", Fx(605)), ("RecordNumber", "EXP-2020-006"), ("FullName", "Paciente De B Sin Subir")),
                Step("A", "restoreBackup"),
                Step("A", "replaceCloud"),
                Sync("B"),
                Step("B", "reapplySaved"),
                Sync("B"),
                Sync("A")
            }).ToArray()));

    private static ScenarioDefinition Folios() => new(
        "folios",
        new[] { "8.16", "8.17", "8.22" },
        new[]
        {
            "Con conexión (pasos 4 a 6), cada expediente nuevo toma el siguiente número de FolioCounters en la nube (folio.take de hrana/statements.json): A, B y A dan 001, 002 y 003 sin sincronizar entre ellos.",
            "Sin red (pasos 7 a 9), B usa su código: EXP-<year>-<code:B>-001 (golden/folios.json). <code:B> es P2 en la PC (otra computadora) y T1 en la tablet.",
            "A, la PC principal, regulariza (pcOnly, paso 13) mientras B edita el teléfono de ese paciente sin haber bajado nada (paso 12): A le da el siguiente número (004) y guarda el folio con letra en OriginalFolio. Al bajar, B combina por campo (8.22): quedan el folio regularizado y el teléfono de B, en los dos."
        },
        new[] { "A", "B" },
        Watch(("Patients", new[] { "RecordNumber", "OriginalFolio", "FullName", "Phone" })),
        Steps(
            Step("A", "fullSync"),
            Step("B", "join"),
            Sync("B"),
            Step("A", "newPatient", new JsonObject { ["syncId"] = Fx(701), ["fullName"] = "Paciente Uno En A" }),
            Step("B", "newPatient", new JsonObject { ["syncId"] = Fx(702), ["fullName"] = "Paciente Dos En B" }),
            Step("A", "newPatient", new JsonObject { ["syncId"] = Fx(703), ["fullName"] = "Paciente Tres En A" }),
            Step(null, "network", new JsonObject { ["online"] = false }),
            Step("B", "newPatient", new JsonObject { ["syncId"] = Fx(704), ["fullName"] = "Paciente Sin Red En B" }),
            Step(null, "network", new JsonObject { ["online"] = true }),
            Sync("B"),
            Sync("A"),
            Edit("B", "Patients", 704, ("Phone", "5512345678")),
            Step("A", "regularizeFolios"),
            Sync("A"),
            Sync("B"),
            Sync("A")));

    private static ScenarioDefinition StockLedgerScenario() => new(
        "stock-ledger",
        new[] { "8.19" },
        new[]
        {
            "A vende una pieza (FEFO: una salida por lote con su SaleItemSyncId; el folio sale del contador de la nube).",
            "Los dos equipos cancelan el mismo ticket sin sincronizar entre ellos (pasos 11 a 15): cada uno escribe la devolución con el mismo SyncId determinista (uuid5 'return:' + SyncId de la salida, golden/uuid5.json), así que la nube y los dos equipos terminan con una sola devolución.",
            "A cierra el lote mientras B vende de él sin haber bajado el cierre (pasos 16 a 20): la salida de B se queda en su lote. Hasta el paso 8.20 las existencias viajan como valor (LWW por fila): la fila del lote que escribió B es la más nueva, así que el lote vuelve a quedar abierto con lo que B le dejó, y el producto con la existencia de B. Este resultado cambia con el 8.20 (existencias derivadas del libro): sus escenarios llegan con él."
        },
        new[] { "A", "B" },
        Watch(("Products", new[] { "CurrentStock" }),
            ("ProductBatches", new[] { "ProductSyncId", "BatchNumber", "Quantity", "IsDepleted", "ClosedAtUtc" }),
            ("Sales", new[] { "Folio", "IsCancelled", "LedgerVersion" }),
            ("SaleItems", new[] { "SaleSyncId", "ProductSyncId", "Quantity" }),
            ("InventoryMovements", new[] { "ProductSyncId", "BatchSyncId", "SaleItemSyncId", "MovementType", "Quantity", "Delta" })),
        Steps(CategoryAndProduct(801, 802, "35.5", stock: "10")
            .Concat(new[]
            {
                Create("A", "ProductBatches", ("SyncId", Fx(803)), ("ProductSyncId", Fx(802)), ("BatchNumber", "L-2026-01"), ("Quantity", "10")),
                Create("A", "InventoryMovements", ("SyncId", Fx(804)), ("ProductSyncId", Fx(802)), ("BatchSyncId", Fx(803)), ("MovementType", "0"),
                    ("Quantity", "10"), ("Delta", "10"), ("StockBefore", "0"), ("StockAfter", "10")),
            })
            .Concat(CloudOfAJoinedByB())
            .Concat(new[]
            {
                Step("A", "sell", new JsonObject { ["productSyncId"] = Fx(802), ["quantity"] = 1 }),
                Sync("A"),
                Sync("B"),
                Step("A", "cancelSale", new JsonObject { ["folio"] = "TKT-<today>-0001" }),
                Step("B", "cancelSale", new JsonObject { ["folio"] = "TKT-<today>-0001" }),
                Sync("A"),
                Sync("B"),
                Sync("A"),
                Step("A", "closeBatch", new JsonObject { ["syncId"] = Fx(803) }),
                Step("B", "sell", new JsonObject { ["productSyncId"] = Fx(802), ["quantity"] = 2 }),
                Sync("A"),
                Sync("B"),
                Sync("A")
            }).ToArray()));

    private static ScenarioDefinition PatientMergeScenario() => new(
        "patient-merge",
        new[] { "8.21", "8.22" },
        new[]
        {
            "Por campo (pasos 5 a 9): B cambia las alergias y A, después, el teléfono, sin bajar nada entre una y otra. La nube se queda con la fila de A (LWW por fila); B la combina al bajar (golden/patient-merge.json), sube el resultado una vez y A lo baja: los dos cambios sobreviven en los dos equipos.",
            "Por renglón (pasos 10 a 15): cada equipo agrega un insumo a la misma hoja sin bajar el del otro. A sube primero; la fila de B es más nueva y la nube se queda con ella (LWW por fila), y B no la combina porque es la suya. A la baja, combina por renglón (los dos insumos: el orden de la hoja ganadora y al final el que solo tenía A) y sube el resultado en esa misma sincronización; B lo baja en la siguiente.",
            "Quitar contra editar (pasos 16 a 21): A quita un renglón y B, después, cambia su cantidad: la marca del renglón de B es más nueva que la de quitado, así que el renglón sigue vivo con la cantidad de B; la marca de quitado se queda en FieldStampsJson.",
            "Escritura sin marcas (pasos 22 a 29): A escribe la fila completa sin tocar FieldStampsJson (rawWrite, como una PC v1.19–v1.38) con una marca posterior a la edición de B. Su UpdatedAtUtc es mayor que su llave 'UpdatedAtUtc': es una escritura sin marcas y ese UpdatedAtUtc es su piso, que le gana a todo lo marcado antes (el teléfono de B). B la toma tal cual (la combinación da lo mismo que la nube). Cuando B vuelve a editar (paso 27), el piso queda guardado en '#piso' y solo se marca lo que B cambió.",
            "ActiveSuppliesJson va en su forma canónica (golden/canonical-json.json) y FieldStampsJson en forma canónica con las horas como <time>: las llaves dicen qué campos y renglones tienen marca."
        },
        new[] { "A", "B" },
        Watch(("Patients", new[] { "FullName", "Phone", "Allergies", "ActiveSuppliesJson", "FieldStampsJson" })),
        Steps(new[] { Create("A", "Patients", ("SyncId", Fx(901)), ("RecordNumber", "EXP-2020-007"), ("FullName", "Juan Pérez Gómez"), ("Phone", "5511111111"), ("Allergies", "Ninguna conocida")) }
            .Concat(CloudOfAJoinedByB())
            .Concat(new[]
            {
                Edit("B", "Patients", 901, ("Allergies", "Penicilina (anafilaxia)")),
                Edit("A", "Patients", 901, ("Phone", "5599887766")),
                Sync("A"),
                Sync("B"),
                Sync("A"),
                Step("A", "sheetRow", new JsonObject { ["syncId"] = Fx(901), ["action"] = "add", ["row"] = new JsonObject { ["RowId"] = Fx(911), ["SupplyName"] = "Gasa estéril 10x10", ["Quantity"] = 1 } }),
                Step("B", "sheetRow", new JsonObject { ["syncId"] = Fx(901), ["action"] = "add", ["row"] = new JsonObject { ["RowId"] = Fx(912), ["SupplyName"] = "Venda elástica 10 cm", ["Quantity"] = 2 } }),
                Sync("A"),
                Sync("B"),
                Sync("A"),
                Sync("B"),
                Step("A", "sheetRow", new JsonObject { ["syncId"] = Fx(901), ["action"] = "remove", ["row"] = new JsonObject { ["RowId"] = Fx(911) } }),
                Step("B", "sheetRow", new JsonObject { ["syncId"] = Fx(901), ["action"] = "setQuantity", ["row"] = new JsonObject { ["RowId"] = Fx(911), ["Quantity"] = 3 } }),
                Sync("A"),
                Sync("B"),
                Sync("A"),
                Sync("B"),
                Edit("B", "Patients", 901, ("Phone", "5522222222")),
                Sync("B"),
                RawWrite("A", "Patients", 901, "T0+90s", ("Phone", "5533333333")),
                Sync("A"),
                Sync("B"),
                Edit("B", "Patients", 901, ("Allergies", "Penicilina y sulfas")),
                Sync("B"),
                Sync("A")
            }).ToArray()));

    // ================================================================== las pruebas

    [TestMethod]
    public Task Scenario_LwwAndTies() => RunScenarioAsync(LwwAndTies());

    [TestMethod]
    public Task Scenario_Tombstones() => RunScenarioAsync(Tombstones());

    [TestMethod]
    public Task Scenario_ParkedRows() => RunScenarioAsync(ParkedRows());

    [TestMethod]
    public Task Scenario_ClockSkew() => RunScenarioAsync(ClockSkew());

    [TestMethod]
    public Task Scenario_UploadCut() => RunScenarioAsync(UploadCut());

    [TestMethod]
    public Task Scenario_GenerationFence() => RunScenarioAsync(GenerationFence());

    [TestMethod]
    public Task Scenario_Folios() => RunScenarioAsync(Folios());

    [TestMethod]
    public Task Scenario_StockLedger() => RunScenarioAsync(StockLedgerScenario());

    [TestMethod]
    public Task Scenario_PatientMerge() => RunScenarioAsync(PatientMergeScenario());

    /// <summary>Cada archivo de contracts/scenarios tiene su definición (uno que sobra no lo correría nadie).</summary>
    [TestMethod]
    public void Scenario_EveryFileHasItsDefinition()
    {
        var names = new[] { LwwAndTies(), Tombstones(), ParkedRows(), ClockSkew(), UploadCut(), GenerationFence(), Folios(), StockLedgerScenario(), PatientMergeScenario() }
            .Select(d => d.Name + ".json").ToHashSet();
        var dir = GoldenVectorTests.RepoPath("contracts/scenarios");
        if (!System.IO.Directory.Exists(dir)) return;
        foreach (var file in System.IO.Directory.GetFiles(dir, "*.json"))
        {
            Assert.Contains(System.IO.Path.GetFileName(file), names, $"{file} no tiene definición en TursoSyncEndToEndTests.ScenarioDefinitions.cs");
        }
    }
}
