using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Panel «Negocio» (pedido del médico del 2026-10-22): los vectores de las cuentas del negocio con el formato del paso 8.26 (se
/// regeneran desde los casos de C# y cada caso del archivo se vuelve a evaluar desde su <c>input</c>): business-summary,
/// sales-by-type, best-sellers, purchases y supply-consumption. El celular (C7, K4) da las mismas cifras con ellos.
/// </summary>
public partial class GoldenVectorTests
{
    // ================================================================== datos de entrada

    private static JsonObject BizProduct(int id, string code, string name, string category, string price, string unitCost, bool excludesVat = false,
        int unitsPerPackage = 1, int stock = 0, bool active = true)
    {
        var o = Prod(id, code, name, stock: stock, category: category, active: active, price: price);
        o["unitCost"] = unitCost;
        o["costExcludesVat"] = excludesVat;
        o["unitsPerPackage"] = unitsPerPackage;
        return o;
    }

    private static JsonObject BizService(int id, string code, string name, string category) =>
        new() { ["id"] = id, ["code"] = code, ["name"] = name, ["category"] = category };

    /// <summary>Un renglón de producto: cantidad, precio, costo guardado (null = renglón de antes) y si son piezas sueltas.</summary>
    private static JsonObject BizItem(int productId, string code, string description, int quantity, string unitPrice, string? unitCost, bool? individualUnits = null)
    {
        var o = new JsonObject
        {
            ["productId"] = productId, ["isService"] = false, ["code"] = code, ["description"] = description,
            ["quantity"] = quantity, ["unitPrice"] = unitPrice,
            ["subtotal"] = M2(decimal.Parse(unitPrice, CultureInfo.InvariantCulture) * quantity)
        };
        if (unitCost != null) o["unitCost"] = unitCost;
        if (individualUnits != null) o["isIndividualUnits"] = individualUnits;
        return o;
    }

    /// <summary>Un renglón de servicio (del catálogo con <paramref name="serviceId"/>, o un cargo libre sin él).</summary>
    private static JsonObject BizServiceItem(int? serviceId, string code, string description, int quantity, string unitPrice)
    {
        var o = new JsonObject
        {
            ["isService"] = true, ["code"] = code, ["description"] = description, ["quantity"] = quantity, ["unitPrice"] = unitPrice,
            ["subtotal"] = M2(decimal.Parse(unitPrice, CultureInfo.InvariantCulture) * quantity)
        };
        if (serviceId != null) o["serviceId"] = serviceId;
        return o;
    }

    private static JsonObject BizSale(int id, string date, bool cancelled, params JsonObject[] items) => new()
    {
        ["id"] = id, ["date"] = date, ["cancelled"] = cancelled, ["items"] = new JsonArray(items.Cast<JsonNode>().ToArray())
    };

    private static JsonObject BizMovement(int productId, string date, string type, int quantity, string? unitCost, string? reason)
    {
        var o = new JsonObject { ["productId"] = productId, ["date"] = date, ["type"] = type, ["quantity"] = quantity };
        if (unitCost != null) o["unitCost"] = unitCost;
        if (reason != null) o["reason"] = reason;
        return o;
    }

    private static JsonObject Range(string from, string to) => new() { ["from"] = from, ["to"] = to };

    /// <summary>El consultorio de los vectores: medicamentos, insumos con piezas por paquete, uno sin costo y los servicios sembrados.</summary>
    private static JsonArray BizProducts() => new(
        BizProduct(1, "MED-001", "Paracetamol 500 mg", "Medicamentos", "45", "20", stock: 30),
        BizProduct(2, "MED-002", "Amoxicilina 500 mg", "Medicamentos", "120", "70", stock: 10),
        BizProduct(3, "CUR-001", "Gasas estériles 10x10", "Material de Curación", "60", "30", excludesVat: true, unitsPerPackage: 10, stock: 5),
        BizProduct(4, "INS-001", "Jeringa 5 ml", "Insumos", "8", "0", stock: 50),
        BizProduct(5, "MED-003", "Ketorolaco ampolleta 30 mg", "Medicamentos", "90", "35.5", unitsPerPackage: 3, stock: 4),
        BizProduct(6, "ANT-001", "Ácido acético 3 %", "Antisépticos", "40", "12", stock: 0, active: false));

    private static JsonArray BizServices() => new(
        BizService(1, "SERV-001", "Consulta Médica General", "Consulta"),
        BizService(2, "SERV-002", "Curación Menor y Asepsia", "Curaciones"),
        BizService(3, "SERV-005", "Certificado Médico Oficial", "Certificados"),
        BizService(4, "SERV-010", "Consulta de seguimiento", "Seguimiento"));

    /// <summary>
    /// Las ventas de octubre de 2026 (y una de septiembre y otra de noviembre): honorario con medicamento, curación con piezas
    /// sueltas de gasa y una jeringa sin costo, una venta de antes sin costo guardado (con piezas sueltas por la regla vieja), una
    /// cancelada, cargos libres, un producto que ya no está en el catálogo y una venta a las 23:30 del último día.
    /// </summary>
    private static JsonArray BizSales() => new(
        BizSale(1, "2026-10-05 10:00", false,
            BizServiceItem(1, "SERV-001", "Consulta Médica General", 1, "300"),
            BizItem(1, "MED-001", "Paracetamol 500 mg", 2, "45", "20", false)),
        BizSale(2, "2026-10-06 12:00", false,
            BizServiceItem(2, "SERV-002", "Curación Menor y Asepsia", 1, "250"),
            BizItem(3, "CUR-001", "Gasas estériles 10x10", 4, "6.00", "3.48", true),
            BizItem(4, "INS-001", "Jeringa 5 ml", 1, "8", null, false)),
        BizSale(3, "2026-10-07 09:00", false,
            BizItem(2, "MED-002", "Amoxicilina 500 mg", 1, "120", null),
            BizItem(5, "MED-003", "Ketorolaco ampolleta 30 mg", 2, "30", null)),
        BizSale(4, "2026-10-08 11:00", true,
            BizItem(2, "MED-002", "Amoxicilina 500 mg", 3, "120", "70", false)),
        BizSale(5, "2026-10-09 17:45", false,
            BizServiceItem(null, "CARGO", "Aplicación de inyección", 1, "50"),
            BizServiceItem(null, "INSUMO", "Venda elástica", 1, "35")),
        BizSale(6, "2026-10-10 13:00", false,
            BizItem(99, "OLD-1", "Producto borrado", 1, "15", null)),
        BizSale(7, "2026-10-12 16:20", false,
            BizServiceItem(4, "SERV-010", "Consulta de seguimiento", 1, "200"),
            BizItem(3, "CUR-001", "Gasas estériles 10x10", 2, "6.00", "3.48", true),
            BizItem(5, "MED-003", "Ketorolaco ampolleta 30 mg", 1, "30", "11.83", true)),
        BizSale(8, "2026-10-31 23:30", false,
            BizServiceItem(3, "SERV-005", "Certificado Médico Oficial", 1, "200"),
            BizServiceItem(77, "SERV-077", "Retiro de puntos", 1, "150")),
        BizSale(9, "2026-11-01 00:10", false,
            BizItem(1, "MED-001", "Paracetamol 500 mg", 1, "45", "20", false)),
        BizSale(10, "2026-09-30 18:00", false,
            BizServiceItem(1, "SERV-001", "Consulta Médica General", 1, "300"),
            BizItem(1, "MED-001", "Paracetamol 500 mg", 1, "45", "20", false)));

    private static JsonObject BizData(JsonObject input)
    {
        input["products"] = BizProducts();
        input["services"] = BizServices();
        input["sales"] = BizSales();
        return input;
    }

    // ================================================================== lectura de las entradas

    private static Product ToBizProduct(JsonNode n)
    {
        var p = ToProduct(n);
        p.UnitsPerPackage = Int(n, "unitsPerPackage", 1);
        return p;
    }

    private static List<Product> BizProductsIn(JsonNode input) => Items(input, "products").Select(ToBizProduct).ToList();

    private static List<ClinicalService> BizServicesIn(JsonNode input) =>
        Items(input, "services").Select(n => new ClinicalService { Id = Int(n, "id"), Code = Str(n, "code"), Name = Str(n, "name"), Category = Str(n, "category") }).ToList();

    private static List<BusinessSaleLine> BizLinesIn(JsonNode input) =>
        Items(input, "sales").SelectMany(s => Items(s, "items").Select(i => ToBizLine(s, i))).ToList();

    private static BusinessSaleLine ToBizLine(JsonNode sale, JsonNode i)
    {
        bool isService = Bool(i, "isService");
        return new BusinessSaleLine(
            Int(sale, "id"), ParseLocal(Str(sale, "date")), Bool(sale, "cancelled"),
            isService ? null : IntOrNull(i, "productId"), isService ? IntOrNull(i, "serviceId") : null, isService,
            Str(i, "code"), Str(i, "description"), Int(i, "quantity"), Dec(i, "unitPrice"), Dec(i, "subtotal"),
            DecOrNull(i, "unitCost"), i["isIndividualUnits"] is JsonValue v ? v.GetValue<bool>() : null);
    }

    private static List<BusinessStockMovement> BizMovementsIn(JsonNode input) =>
        Items(input, "movements").Select(m => new BusinessStockMovement(Int(m, "productId"), ParseLocal(Str(m, "date")),
            Enum.Parse<MovementType>(Str(m, "type")), Int(m, "quantity"), DecOrNull(m, "unitCost"), S(m, "reason"))).ToList();

    private static BusinessPeriod PeriodIn(JsonNode input, string key = "period") =>
        new(ParseLocal(Str(input[key], "from")), ParseLocal(Str(input[key], "to")));

    private static BusinessLineType? TypeIn(JsonNode input) => S(input, "type") is { } t ? Enum.Parse<BusinessLineType>(t) : null;

    private static List<BusinessLine> BizLinesFor(JsonNode input, string periodKey = "period", BusinessLineType? type = null) =>
        BusinessReportRules.Lines(BizLinesIn(input), BizProductsIn(input), BizServicesIn(input), PeriodIn(input, periodKey), type);

    /// <summary>Un importe en los vectores del negocio: siempre con 2 decimales ("1544.00"), igual en C# y en Kotlin.</summary>
    private static string M2(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Un porcentaje en los vectores del negocio: siempre con 1 decimal ("100.0"); null si no hay con qué calcularlo.</summary>
    private static string? Pct(decimal? value) => value?.ToString("0.0", CultureInfo.InvariantCulture);

    private static JsonObject SummaryJson(BusinessSummary s) => new()
    {
        ["sales"] = M2(s.Sales), ["cost"] = M2(s.Cost), ["profit"] = M2(s.Profit), ["marginPercent"] = Pct(s.MarginPercent),
        ["tickets"] = s.Tickets, ["averageTicket"] = Pct(s.AverageTicket),
        ["estimatedCostLines"] = s.EstimatedCostLines, ["missingCostLines"] = s.MissingCostLines,
        ["costNote"] = BusinessReportRules.EstimatedCostNote(s.EstimatedCostLines, s.MissingCostLines)
    };

    // ================================================================== contracts/golden/business-summary.json

    [TestMethod]
    public void BusinessSummary_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();

        // Periodos con la hora del consultorio: el mismo instante es otro día en UTC y en America/Mexico_City
        foreach (var (name, kind, nowUtc, from, to) in new (string, BusinessPeriodKind, string, string?, string?)[]
                 {
                     ("Este mes a las 05:30 UTC del 1 de noviembre", BusinessPeriodKind.EsteMes, "2026-11-01 05:30", null, null),
                     ("Hoy a las 05:30 UTC del 1 de noviembre", BusinessPeriodKind.Hoy, "2026-11-01 05:30", null, null),
                     ("Esta semana (empieza el lunes)", BusinessPeriodKind.EstaSemana, "2026-11-01 05:30", null, null),
                     ("Mes anterior", BusinessPeriodKind.MesAnterior, "2026-11-01 05:30", null, null),
                     ("Este año", BusinessPeriodKind.EsteAnio, "2026-11-01 05:30", null, null),
                     ("Este mes el 31 de marzo se compara con febrero completo", BusinessPeriodKind.EsteMes, "2027-03-31 18:00", null, null),
                     ("Este año en un año bisiesto", BusinessPeriodKind.EsteAnio, "2028-02-29 18:00", null, null),
                     ("Personalizado: las fechas al revés se voltean", BusinessPeriodKind.Personalizado, "2026-11-01 05:30", "2026-10-20", "2026-10-11"),
                     ("Personalizado de un día", BusinessPeriodKind.Personalizado, "2026-11-01 05:30", "2026-10-15", "2026-10-15"),
                 })
        {
            foreach (var (zoneName, _) in VectorZones)
            {
                var input = new JsonObject { ["check"] = "period", ["kind"] = kind.ToString(), ["nowUtc"] = nowUtc, ["zone"] = zoneName };
                if (from != null) input["customFrom"] = from;
                if (to != null) input["customTo"] = to;
                cases.Add(new RuleCase($"periodo · {name} · {zoneName}", input));
            }
        }

        // El costo que se guarda al vender o al mover
        foreach (var (name, product, individual) in new (string, JsonObject, bool)[]
                 {
                     ("medicamento con IVA incluido", BizProduct(1, "MED-001", "Paracetamol", "Medicamentos", "45", "20"), false),
                     ("insumo sin IVA: se suma 16 % hacia arriba", BizProduct(3, "CUR-001", "Gasas", "Material de Curación", "60", "30", excludesVat: true, unitsPerPackage: 10), false),
                     ("pieza suelta: el costo con IVA entre las piezas, centavos hacia arriba", BizProduct(3, "CUR-001", "Gasas", "Material de Curación", "60", "30", excludesVat: true, unitsPerPackage: 10), true),
                     ("pieza suelta con punto medio: 35.5 / 3 = 11.833 → 11.83; 0.25 / 2 = 0.125 → 0.13", BizProduct(7, "MED-009", "Ampolleta", "Medicamentos", "1", "0.25", unitsPerPackage: 2), true),
                     ("sin costo no se guarda nada", BizProduct(4, "INS-001", "Jeringa", "Insumos", "8", "0"), false),
                     ("piezas sueltas de un producto de una pieza: el de la unidad", BizProduct(1, "MED-001", "Paracetamol", "Medicamentos", "45", "20"), true),
                 })
        {
            cases.Add(new RuleCase($"costo al guardar · {name}", new JsonObject { ["check"] = "cost-to-record", ["product"] = product, ["individualUnits"] = individual }));
        }

        // Cada renglón: tipo, piezas sueltas y costo
        var products = BizProducts();
        foreach (var (name, item, sale) in new (string, JsonObject, string)[]
                 {
                     ("producto con costo guardado", BizItem(1, "MED-001", "Paracetamol 500 mg", 2, "45", "20", false), "2026-10-05 10:00"),
                     ("piezas sueltas con costo guardado por pieza", BizItem(3, "CUR-001", "Gasas estériles 10x10", 4, "6.00", "3.48", true), "2026-10-05 10:00"),
                     ("renglón de antes: el costo de hoy, estimado", BizItem(2, "MED-002", "Amoxicilina 500 mg", 1, "120", null), "2026-10-05 10:00"),
                     ("renglón de antes con piezas sueltas por la regla vieja (precio menor al del paquete)", BizItem(5, "MED-003", "Ketorolaco ampolleta 30 mg", 2, "30", null), "2026-10-05 10:00"),
                     ("renglón de antes a precio del paquete: paquetes", BizItem(5, "MED-003", "Ketorolaco ampolleta 30 mg", 1, "90", null), "2026-10-05 10:00"),
                     ("producto sin costo hoy: sin costo", BizItem(4, "INS-001", "Jeringa 5 ml", 1, "8", null, false), "2026-10-05 10:00"),
                     ("producto que ya no está en el catálogo: Otros y sin costo", BizItem(99, "OLD-1", "Producto borrado", 1, "15", null), "2026-10-05 10:00"),
                     ("producto dado de baja: sigue siendo de su tipo", BizItem(6, "ANT-001", "Ácido acético 3 %", 1, "40", null), "2026-10-05 10:00"),
                     ("honorario: Consultas y sin costo propio", BizServiceItem(1, "SERV-001", "Consulta Médica General", 1, "300"), "2026-10-05 10:00"),
                     ("otro servicio: Servicios", BizServiceItem(2, "SERV-002", "Curación Menor y Asepsia", 1, "250"), "2026-10-05 10:00"),
                     ("servicio con 'Consulta' en el nombre: Consultas", BizServiceItem(4, "SERV-010", "Consulta de seguimiento", 1, "200"), "2026-10-05 10:00"),
                     ("servicio borrado: Servicios por su Id", BizServiceItem(77, "SERV-077", "Retiro de puntos", 1, "150"), "2026-10-05 10:00"),
                     ("honorario sin servicio en el catálogo: por su código", BizServiceItem(null, "SERV-001", "Honorario", 1, "300"), "2026-10-05 10:00"),
                     ("cargo libre: Otros", BizServiceItem(null, "CARGO", "Aplicación de inyección", 1, "50"), "2026-10-05 10:00"),
                     ("cargo libre de un insumo: Insumos sin costo propio", BizServiceItem(null, "INSUMO", "Venda elástica", 1, "35"), "2026-10-05 10:00"),
                 })
        {
            cases.Add(new RuleCase($"renglón · {name}", new JsonObject
            {
                ["check"] = "line", ["products"] = products.DeepClone(), ["services"] = BizServices(),
                ["sales"] = new JsonArray(BizSale(1, sale, false, item)), ["period"] = Range("2026-10-01", "2026-11-01")
            }));
        }

        // El resumen de octubre y la comparación con septiembre, por tipo
        foreach (var type in new BusinessLineType?[] { null, BusinessLineType.Consultas, BusinessLineType.Medicamentos, BusinessLineType.Insumos, BusinessLineType.Servicios, BusinessLineType.Otros })
        {
            var input = BizData(new JsonObject { ["check"] = "summary", ["period"] = Range("2026-10-01", "2026-11-01"), ["previous"] = Range("2026-09-01", "2026-10-01") });
            if (type != null) input["type"] = type.ToString();
            cases.Add(new RuleCase($"resumen · octubre contra septiembre · {(type is { } t ? BusinessReportRules.TypeName(t) : "Todo")}", input));
        }
        cases.Add(new RuleCase("resumen · la venta de las 23:30 del 31 es de octubre y la de las 00:10 del 1 de noviembre no", BizData(new JsonObject
        {
            ["check"] = "summary", ["period"] = Range("2026-10-31", "2026-11-01"), ["previous"] = Range("2026-10-30", "2026-10-31")
        })));
        cases.Add(new RuleCase("resumen · sin ventas: nada que comparar", BizData(new JsonObject
        {
            ["check"] = "summary", ["period"] = Range("2026-12-01", "2027-01-01"), ["previous"] = Range("2026-11-01", "2026-12-01")
        })));

        // Redondeo al par: margen 49 / 400 = 12.25 % → 12.2; ticket promedio 10.05 / 2 = 5.025 → 5.02; cambio 449 contra 400 = 12.25 % → 12.2
        var rounding = new JsonObject
        {
            ["check"] = "summary",
            ["products"] = new JsonArray(BizProduct(1, "MED-001", "Paracetamol", "Medicamentos", "400", "351")),
            ["services"] = new JsonArray(),
            ["sales"] = new JsonArray(
                BizSale(1, "2026-10-05 10:00", false, BizItem(1, "MED-001", "Paracetamol", 1, "400", "351")),
                BizSale(2, "2026-10-06 10:00", false, BizServiceItem(null, "CARGO", "Cargo", 1, "5.02")),
                BizSale(3, "2026-10-07 10:00", false, BizServiceItem(null, "CARGO", "Cargo", 1, "5.03")),
                BizSale(4, "2026-09-05 10:00", false, BizServiceItem(null, "CARGO", "Cargo", 1, "400"))),
            ["period"] = Range("2026-10-05", "2026-10-06"),
            ["previous"] = Range("2026-09-01", "2026-10-01")
        };
        cases.Add(new RuleCase("redondeo · margen con punto medio al par", rounding));
        var average = (JsonObject)rounding.DeepClone();
        average["period"] = Range("2026-10-06", "2026-10-08");
        cases.Add(new RuleCase("redondeo · ticket promedio con punto medio al par", average));
        var change = (JsonObject)rounding.DeepClone();
        ((JsonArray)change["sales"]!).Add(BizSale(5, "2026-10-05 11:00", false, BizServiceItem(null, "CARGO", "Cargo", 1, "49")));
        cases.Add(new RuleCase("redondeo · cambio contra el anterior con punto medio al par (449 contra 400)", change));
        cases.Add(new RuleCase("cambio · utilidad que pasa de negativa a positiva: contra |anterior|", new JsonObject
        {
            ["check"] = "summary",
            ["products"] = new JsonArray(BizProduct(1, "MED-001", "Paracetamol", "Medicamentos", "10", "30")),
            ["services"] = new JsonArray(),
            ["sales"] = new JsonArray(
                BizSale(1, "2026-10-05 10:00", false, BizItem(1, "MED-001", "Paracetamol", 1, "60", "30")),
                BizSale(2, "2026-09-05 10:00", false, BizItem(1, "MED-001", "Paracetamol", 1, "10", "30"))),
            ["period"] = Range("2026-10-01", "2026-11-01"),
            ["previous"] = Range("2026-09-01", "2026-10-01")
        }));

        // En el tiempo: por día hasta 62 días, por mes si es más largo
        cases.Add(new RuleCase("en el tiempo · por día", BizData(new JsonObject { ["check"] = "timeline", ["period"] = Range("2026-10-04", "2026-10-11") })));
        cases.Add(new RuleCase("en el tiempo · por mes (más de 62 días)", BizData(new JsonObject { ["check"] = "timeline", ["period"] = Range("2026-08-01", "2026-12-01") })));
        cases.Add(new RuleCase("en el tiempo · solo Medicamentos", BizData(new JsonObject { ["check"] = "timeline", ["period"] = Range("2026-10-04", "2026-10-08"), ["type"] = "Medicamentos" })));

        CheckRuleVectors("business-summary.json", "business-summary", cases, input =>
            {
                switch (S(input, "check"))
                {
                    case "period":
                    {
                        var zone = VectorZones.Single(z => z.Name == Str(input, "zone")).Zone;
                        var utc = DateTime.SpecifyKind(ParseLocal(Str(input, "nowUtc")), DateTimeKind.Utc);
                        using var _ = ClinicClock.Use(new ZoneClock(new DateTimeOffset(utc), zone));
                        var kind = Enum.Parse<BusinessPeriodKind>(Str(input, "kind"));
                        var today = ClinicClock.Today;
                        var period = BusinessReportRules.PeriodFor(kind, today, DateOrNull(input, "customFrom"), DateOrNull(input, "customTo"));
                        var previous = BusinessReportRules.PreviousPeriod(kind, period);
                        return new JsonObject
                        {
                            ["today"] = LocalText(today), ["from"] = LocalText(period.From), ["to"] = LocalText(period.To),
                            ["previousFrom"] = LocalText(previous.From), ["previousTo"] = LocalText(previous.To),
                            ["name"] = BusinessReportRules.PeriodName(kind), ["text"] = BusinessReportRules.PeriodText(period)
                        };
                    }
                    case "cost-to-record":
                        return BusinessReportRules.CostToRecord(ToBizProduct(input["product"]!), Bool(input, "individualUnits")) is { } cost ? M2(cost) : null;
                    case "line":
                    {
                        var line = BizLinesFor(input).Single();
                        return new JsonObject
                        {
                            ["type"] = line.Type.ToString(), ["name"] = line.Name, ["isLoosePieces"] = line.IsLoosePieces,
                            ["sales"] = M2(line.Sales), ["cost"] = M2(line.Cost), ["costSource"] = line.CostSource.ToString(), ["profit"] = M2(line.Profit)
                        };
                    }
                    case "summary":
                    {
                        var type = TypeIn(input);
                        var current = BusinessReportRules.Summarize(BizLinesFor(input, "period", type));
                        var previous = BusinessReportRules.Summarize(BizLinesFor(input, "previous", type));
                        var comparison = BusinessReportRules.Compare(current, previous);
                        return new JsonObject
                        {
                            ["current"] = SummaryJson(current), ["previous"] = SummaryJson(previous),
                            ["salesChangePercent"] = Pct(comparison.SalesChangePercent), ["profitChangePercent"] = Pct(comparison.ProfitChangePercent),
                            ["ticketsChangePercent"] = Pct(comparison.TicketsChangePercent),
                            ["salesChangeText"] = BusinessReportRules.ChangeText(comparison.SalesChangePercent)
                        };
                    }
                    case "timeline":
                        return new JsonArray(BusinessReportRules.Timeline(BizLinesFor(input, "period", TypeIn(input)), PeriodIn(input))
                            .Select(p => (JsonNode)new JsonObject { ["start"] = LocalText(p.Start), ["label"] = p.Label, ["sales"] = M2(p.Sales), ["profit"] = M2(p.Profit) })
                            .ToArray());
                    default:
                        throw new ArgumentException(S(input, "check"));
                }
            },
            "El resumen del panel «Negocio» (Services/Rules/BusinessReportRules.cs; pedido del médico del 2026-10-22). Entradas: products (con unitCost, costExcludesVat y unitsPerPackage), services ({id, code, name, category}) y sales ({id, date en hora del consultorio, cancelled, items}); cada renglón {productId o serviceId, isService, code, description, quantity, unitPrice, subtotal, unitCost (SaleItems.UnitCost, revisión 18; falta = renglón de antes), isIndividualUnits (falta = renglón de antes)}. Periodos [from, to) en hora del consultorio. Importes con 2 decimales y porcentajes con 1 (texto invariante, ya redondeados por la regla; el formato no vuelve a redondear).",
            "period: 'hoy' es la fecha del consultorio en la zona del caso (nowUtc). Hoy; Esta semana del lunes a hoy; Este mes del día 1 a hoy; Mes anterior completo; Este año del 1 de enero a hoy; Personalizado de customFrom a customTo incluidos (al revés se voltean). El anterior igual: los mismos días justo antes; Este mes y Este año, el mes o el año anterior desde su inicio con los mismos días transcurridos sin pasar de su fin; Mes anterior, el mes completo de antes. text: 'dd/MM/yyyy' o 'dd/MM/yyyy – dd/MM/yyyy' (el último día incluido).",
            "cost-to-record (lo que se escribe en SaleItems.UnitCost y en InventoryMovements.UnitCost): null sin costo; si no, el costo con IVA (Math.Round(costo × 1.16, 2, AwayFromZero) si costExcludesVat) o, en piezas sueltas de un producto con unitsPerPackage > 1, ese costo entre las piezas con Math.Round(…, 2, AwayFromZero).",
            "line: tipo (honorario = servicio con código SERV-001, categoría 'Consulta' o 'Consulta' en el nombre, con el servicio del catálogo o, sin él, con el código y la descripción del renglón → Consultas; otro servicio con serviceId → Servicios; cargo libre 'INSUMO' → Insumos; otro cargo libre → Otros; producto medicamento/insumo con catalog-matching.json; producto que no existe → Otros). Piezas sueltas: isIndividualUnits o, si falta, unitsPerPackage > 1 y unitPrice < price. Costo: servicio o cargo libre NotApplicable 0; unitCost guardado → quantity × unitCost (Recorded); sin él y con costo hoy → quantity × costo de hoy (por pieza si son piezas sueltas) Estimated; si no, 0 Missing.",
            "summary: sin canceladas ni fuera del periodo, solo el type si se da. sales = Σ subtotal; cost = Σ costo; profit = sales − cost; marginPercent = Math.Round(profit / sales × 100, 1) (al par; null sin ventas); tickets = ventas distintas con algún renglón; averageTicket = Math.Round(sales / tickets, 2) (al par); cambio = Math.Round((actual − anterior) / |anterior| × 100, 1) (al par), null si el anterior es 0; salesChangeText '▲ 12.5 %', '▼ 3.0 %', '= 0.0 %' o 'sin comparación'. costNote dice cuántos renglones usan costo estimado o no tienen costo.",
            "timeline: por día ('dd/MM') si el periodo tiene 62 días o menos, si no por mes ('MM/yyyy'); los días o meses sin ventas en 0; profit = ventas − costo de ese día o mes.");
    }

    // ================================================================== contracts/golden/sales-by-type.json

    [TestMethod]
    public void SalesByType_MatchesTheVectors()
    {
        var cases = new List<RuleCase>
        {
            new("octubre · todos los tipos", BizData(new JsonObject { ["period"] = Range("2026-10-01", "2026-11-01") })),
            new("del 5 al 6 de octubre", BizData(new JsonObject { ["period"] = Range("2026-10-05", "2026-10-07") })),
            new("septiembre", BizData(new JsonObject { ["period"] = Range("2026-09-01", "2026-10-01") })),
            new("sin ventas", BizData(new JsonObject { ["period"] = Range("2026-12-01", "2027-01-01") })),
        };

        CheckRuleVectors("sales-by-type.json", "sales-by-type", cases, input =>
                new JsonArray(BusinessReportRules.ByType(BizLinesFor(input))
                    .Select(r => (JsonNode)new JsonObject
                    {
                        ["type"] = r.Type.ToString(), ["name"] = r.Name, ["sales"] = M2(r.Sales), ["cost"] = M2(r.Cost), ["profit"] = M2(r.Profit),
                        ["marginPercent"] = Pct(r.MarginPercent), ["sharePercent"] = Pct(r.SharePercent), ["lines"] = r.Lines, ["estimatedCostLines"] = r.EstimatedCostLines
                    })
                    .ToArray()),
            "«Por sección» del panel «Negocio» (BusinessReportRules.ByType): las mismas entradas y reglas de renglón que business-summary.json. Un renglón por tipo con ventas en el periodo, en el orden Consultas, Medicamentos, Insumos, Servicios, Otros.",
            "sales, cost y profit como en el resumen; marginPercent = Math.Round(profit / sales × 100, 1); sharePercent = Math.Round(sales del tipo / sales de todos × 100, 1) (al par); lines = renglones; estimatedCostLines = renglones con costo estimado. Las canceladas no cuentan.");
    }

    // ================================================================== contracts/golden/best-sellers.json

    [TestMethod]
    public void BestSellers_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, type, ranking, top) in new (string, BusinessLineType?, BusinessRanking, int)[]
                 {
                     ("medicamentos por cantidad (la vista de inicio)", BusinessLineType.Medicamentos, BusinessRanking.Quantity, 10),
                     ("medicamentos por utilidad", BusinessLineType.Medicamentos, BusinessRanking.Profit, 10),
                     ("insumos: las piezas sueltas cuentan como piezas", BusinessLineType.Insumos, BusinessRanking.Quantity, 10),
                     ("todos los productos", null, BusinessRanking.Quantity, 10),
                     ("todos los productos, los 2 primeros", null, BusinessRanking.Quantity, 2),
                 })
        {
            var input = BizData(new JsonObject { ["check"] = "products", ["period"] = Range("2026-10-01", "2026-11-01"), ["ranking"] = ranking.ToString(), ["top"] = top });
            if (type != null) input["type"] = type.ToString();
            cases.Add(new RuleCase($"productos · {name}", input));
        }
        cases.Add(new RuleCase("productos · empate de cantidad y utilidad: por ventas y luego por nombre es-MX (Á junto a A)", new JsonObject
        {
            ["check"] = "products", ["ranking"] = "Quantity", ["top"] = 10, ["period"] = Range("2026-10-01", "2026-11-01"),
            ["products"] = new JsonArray(
                BizProduct(1, "MED-1", "Azitromicina", "Medicamentos", "10", "5"),
                BizProduct(2, "MED-2", "Ácido fólico", "Medicamentos", "10", "5"),
                BizProduct(3, "MED-3", "Bromhexina", "Medicamentos", "10", "5")),
            ["services"] = new JsonArray(),
            ["sales"] = new JsonArray(BizSale(1, "2026-10-02 10:00", false,
                BizItem(1, "MED-1", "Azitromicina", 1, "10", "5"), BizItem(2, "MED-2", "Ácido fólico", 1, "10", "5"), BizItem(3, "MED-3", "Bromhexina", 1, "10", "5")))
        }));
        cases.Add(new RuleCase("servicios · consultas y servicios por veces", BizData(new JsonObject { ["check"] = "services", ["period"] = Range("2026-10-01", "2026-11-01"), ["top"] = 10 })));
        cases.Add(new RuleCase("servicios · solo Consultas", BizData(new JsonObject { ["check"] = "services", ["period"] = Range("2026-09-01", "2026-11-01"), ["top"] = 10, ["type"] = "Consultas" })));

        CheckRuleVectors("best-sellers.json", "best-sellers", cases, input =>
            {
                if (S(input, "check") == "services")
                {
                    var lines = BizLinesFor(input, "period", TypeIn(input));
                    return new JsonArray(BusinessReportRules.TopServices(lines, Int(input, "top"))
                        .Select(s => (JsonNode)new JsonObject { ["serviceId"] = s.ClinicalServiceId, ["name"] = s.Name, ["type"] = s.Type.ToString(), ["count"] = s.Count, ["income"] = M2(s.Income) })
                        .ToArray());
                }
                return new JsonArray(BusinessReportRules.TopProducts(BizLinesFor(input), TypeIn(input), Enum.Parse<BusinessRanking>(Str(input, "ranking")), Int(input, "top"))
                    .Select(p => (JsonNode)new JsonObject
                    {
                        ["productId"] = p.ProductId, ["name"] = p.Name, ["type"] = p.Type.ToString(), ["isLoosePieces"] = p.IsLoosePieces, ["quantity"] = p.Quantity,
                        ["quantityText"] = p.QuantityText, ["sales"] = M2(p.Sales), ["cost"] = M2(p.Cost), ["profit"] = M2(p.Profit), ["hasEstimatedCost"] = p.HasEstimatedCost
                    })
                    .ToArray());
            },
            "«Más vendidos» del panel «Negocio» (BusinessReportRules.TopProducts y TopServices): las mismas entradas y reglas de renglón que business-summary.json; sin canceladas.",
            "products: los renglones de producto del type (Medicamentos por omisión en la pantalla; sin type, todos los productos), uno por producto y por si son piezas sueltas (las piezas cuentan como piezas: quantityText '4 piezas sueltas'). ranking Quantity: Σ cantidad de mayor a menor, luego utilidad; Profit: utilidad, luego cantidad; después ventas de mayor a menor y el nombre (de hoy) con Collator es-MX. hasEstimatedCost si algún renglón no tiene costo guardado. top como máximo.",
            "services: los renglones de Consultas y Servicios (del type si se da), uno por serviceId (o por nombre sin él): count = Σ cantidad, income = Σ subtotal; de más a menos veces, luego ingreso y nombre (es-MX).");
    }

    // ================================================================== contracts/golden/purchases.json

    [TestMethod]
    public void Purchases_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var reason in new[] { "Pedido LUIN #5175", "Pedido Distribuidora Médica del Norte #A-12", "Pedido LUIN", "Pedido #5175", "Pedido", "Entrada de lote L-2026-10", "Compra en farmacia", "Pedidos varios", "" })
        {
            cases.Add(new RuleCase($"proveedor · «{reason}»", new JsonObject { ["check"] = "supplier", ["reason"] = reason }));
        }

        JsonObject PurchaseData(JsonObject input)
        {
            input["products"] = BizProducts();
            input["movements"] = new JsonArray(
                BizMovement(1, "2026-10-02 09:00", "EntradaCompra", 50, "20", "Pedido LUIN #5175"),
                BizMovement(3, "2026-10-02 09:00", "EntradaCompra", 10, "34.80", "Pedido LUIN #5175"),
                BizMovement(2, "2026-10-03 10:00", "EntradaCompra", 5, null, "Entrada de lote AMX-77"),
                BizMovement(4, "2026-10-03 10:00", "EntradaCompra", 100, null, "Pedido Proveedora del Bajío #88"),
                BizMovement(5, "2026-10-15 18:00", "EntradaCompra", 3, "35.5", "Pedido Proveedora del Bajío #91"),
                BizMovement(1, "2026-10-20 08:00", "SalidaConsulta", 2, "20", "Uso en consulta"),
                BizMovement(2, "2026-09-28 10:00", "EntradaCompra", 4, "70", "Pedido LUIN #5100"),
                BizMovement(2, "2026-11-01 00:05", "EntradaCompra", 4, "70", "Pedido LUIN #5200"));
            return input;
        }
        cases.Add(new RuleCase("compras · octubre", PurchaseData(new JsonObject { ["check"] = "purchases", ["period"] = Range("2026-10-01", "2026-11-01") })));
        cases.Add(new RuleCase("compras · octubre, solo Medicamentos", PurchaseData(new JsonObject { ["check"] = "purchases", ["period"] = Range("2026-10-01", "2026-11-01"), ["type"] = "Medicamentos" })));
        cases.Add(new RuleCase("compras · octubre, solo Insumos", PurchaseData(new JsonObject { ["check"] = "purchases", ["period"] = Range("2026-10-01", "2026-11-01"), ["type"] = "Insumos" })));
        cases.Add(new RuleCase("compras · Consultas no tiene compras", PurchaseData(new JsonObject { ["check"] = "purchases", ["period"] = Range("2026-10-01", "2026-11-01"), ["type"] = "Consultas" })));
        cases.Add(new RuleCase("compras · septiembre", PurchaseData(new JsonObject { ["check"] = "purchases", ["period"] = Range("2026-09-01", "2026-10-01") })));

        CheckRuleVectors("purchases.json", "purchases", cases, input =>
            {
                if (S(input, "check") == "supplier")
                {
                    return new JsonObject { ["isOrder"] = BusinessStockRules.IsSupplierOrder(S(input, "reason")), ["supplier"] = BusinessStockRules.SupplierOf(S(input, "reason")) };
                }
                var p = BusinessStockRules.Purchases(BizMovementsIn(input), BizProductsIn(input), PeriodIn(input), TypeIn(input));
                return new JsonObject
                {
                    ["entries"] = p.Entries, ["orders"] = p.Orders, ["units"] = p.Units, ["cost"] = M2(p.Cost),
                    ["estimatedCostLines"] = p.EstimatedCostLines, ["missingCostLines"] = p.MissingCostLines,
                    ["bySupplier"] = new JsonArray(p.BySupplier.Select(r => (JsonNode)new JsonObject
                    {
                        ["supplier"] = r.Supplier, ["entries"] = r.Entries, ["units"] = r.Units, ["cost"] = M2(r.Cost), ["estimatedCostLines"] = r.EstimatedCostLines
                    }).ToArray()),
                    ["byType"] = new JsonArray(p.ByType.Select(r => (JsonNode)new JsonObject
                    {
                        ["type"] = r.Type.ToString(), ["name"] = r.Name, ["entries"] = r.Entries, ["units"] = r.Units, ["cost"] = M2(r.Cost)
                    }).ToArray())
                };
            },
            "«Compras» del panel «Negocio» (Services/Rules/BusinessStockRules.cs). Entradas: products como en business-summary.json y movements ({productId, date en hora del consultorio, type (MovementType), quantity, unitCost (InventoryMovements.UnitCost, revisión 18; falta = fila de antes), reason}).",
            "supplier: el motivo de un pedido es 'Pedido', 'Pedido {proveedor}' o 'Pedido {proveedor} #{número}' (InventoryService.SupplierOrderReason): isOrder si empieza con 'Pedido' seguido de nada o de un espacio; supplier = lo que va entre 'Pedido ' y ' #', 'Pedido sin proveedor' si no hay, y 'Entrada sin pedido' para cualquier otra entrada (un lote recibido, una entrada a mano).",
            "purchases: los EntradaCompra del periodo (y del type de su producto si se da; Consultas y Servicios no tienen). Costo: quantity × unitCost (guardado) o quantity × el costo de hoy con IVA (estimado) o 0 (sin costo). orders = motivos distintos de pedido; bySupplier de mayor a menor costo y luego por nombre (es-MX); byType en el orden Medicamentos, Insumos, Otros. Importes sin redondear (cantidad × costo de dos decimales es exacto), escritos con 2 decimales.");
    }

    // ================================================================== contracts/golden/supply-consumption.json

    [TestMethod]
    public void SupplyConsumption_MatchesTheVectors()
    {
        JsonObject ConsumptionData(JsonObject input)
        {
            BizData(input);
            input["movements"] = new JsonArray(
                BizMovement(3, "2026-10-04 11:00", "SalidaConsulta", 1, "34.80", "Uso / aplicación durante consulta médica"),
                BizMovement(3, "2026-10-04 11:00", "SalidaConsulta", 1, "34.80", "Uso / aplicación durante consulta médica"),
                BizMovement(4, "2026-10-05 10:30", "SalidaConsulta", 3, null, "Uso / aplicación durante consulta médica"),
                BizMovement(1, "2026-10-08 09:00", "SalidaConsulta", 1, null, "Dosis aplicada en consultorio"),
                BizMovement(3, "2026-09-20 09:00", "SalidaConsulta", 5, "34.80", "Antes del periodo"),
                BizMovement(2, "2026-10-09 09:00", "EntradaCompra", 5, "70", "Pedido LUIN #1"));
            return input;
        }
        var cases = new List<RuleCase>();
        foreach (var type in new BusinessLineType?[] { null, BusinessLineType.Consultas, BusinessLineType.Servicios, BusinessLineType.Insumos, BusinessLineType.Medicamentos })
        {
            var input = ConsumptionData(new JsonObject { ["period"] = Range("2026-10-01", "2026-11-01") });
            if (type != null) input["type"] = type.ToString();
            cases.Add(new RuleCase($"consumo · octubre · {(type is { } t ? BusinessReportRules.TypeName(t) : "Todo")}", input));
        }
        cases.Add(new RuleCase("consumo · sin nada en el periodo", ConsumptionData(new JsonObject { ["period"] = Range("2026-12-01", "2027-01-01") })));

        CheckRuleVectors("supply-consumption.json", "supply-consumption", cases, input =>
            {
                var c = BusinessStockRules.SupplyConsumption(BizMovementsIn(input), BizLinesFor(input), BizProductsIn(input), PeriodIn(input), TypeIn(input));
                return new JsonObject
                {
                    ["cost"] = M2(c.Cost), ["estimatedCostLines"] = c.EstimatedCostLines, ["missingCostLines"] = c.MissingCostLines,
                    ["rows"] = new JsonArray(c.Rows.Select(r => (JsonNode)new JsonObject
                    {
                        ["productId"] = r.ProductId, ["name"] = r.Name, ["type"] = r.Type.ToString(), ["isLoosePieces"] = r.IsLoosePieces,
                        ["usedInConsultation"] = r.UsedInConsultation, ["chargedWithServices"] = r.ChargedWithServices, ["quantity"] = r.Quantity,
                        ["quantityText"] = r.QuantityText, ["cost"] = M2(r.Cost), ["hasEstimatedCost"] = r.HasEstimatedCost
                    }).ToArray())
                };
            },
            "«Consumo de insumos» del panel «Negocio» (BusinessStockRules.SupplyConsumption). Entradas: products, services y sales como en business-summary.json, y movements como en purchases.json.",
            "usedInConsultation: Σ quantity de los SalidaConsulta del periodo (lo usado sin cobrar, del Kardex; cada parte por lote es un movimiento), costo guardado o estimado como en las compras. chargedWithServices: Σ quantity de los renglones de Insumos de un ticket no cancelado del periodo que también cobró una Consulta o un Servicio (en piezas si son piezas sueltas: el Kardex solo anota los paquetes que se abren), con el costo de su renglón.",
            "type: Consultas = lo usado en consulta y lo cobrado en tickets con consulta; Servicios = lo cobrado en tickets con un servicio; Medicamentos, Insumos u Otros = solo los productos de ese tipo. Un renglón por producto y por si son piezas sueltas, de mayor a menor costo, luego cantidad y nombre (es-MX). Importes sin redondear (cantidad × costo de dos decimales es exacto), escritos con 2 decimales.");
    }
}
