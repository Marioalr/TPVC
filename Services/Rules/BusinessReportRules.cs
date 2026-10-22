using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// El panel «Negocio» (pedido del médico del 2026-10-22): ventas, costo, utilidad y margen por periodo y por tipo, los más
/// vendidos y la gráfica en el tiempo. Las compras y el consumo de insumos están en <see cref="BusinessStockRules"/>. Sin WPF
/// ni base: vectores en <c>contracts/golden/business-summary.json</c>, <c>sales-by-type.json</c> y <c>best-sellers.json</c>.
/// <para>
/// Redondeo: los importes no se redondean (cantidad × costo de dos decimales es exacto); el margen, la participación y el
/// cambio contra el periodo anterior van en % con un decimal y el ticket promedio a centavos, con <see cref="Math.Round(decimal, int)"/>
/// (al par, <see cref="MidpointRounding.ToEven"/>). El IVA del costo y el costo por pieza suelta siguen las reglas del producto
/// (<see cref="Product.EffectiveUnitCost"/> y <see cref="Product.IndividualUnitCost"/>: centavos hacia arriba,
/// <see cref="MidpointRounding.AwayFromZero"/>).
/// </para>
/// </summary>
public static class BusinessReportRules
{
    /// <summary>Hasta cuántos días la gráfica va por día; un periodo más largo va por mes.</summary>
    public const int MaxDailyBuckets = 62;

    /// <summary>Cuántos renglones muestran «Más vendidos».</summary>
    public const int DefaultTop = 10;

    /// <summary>El código de un cargo libre de un insumo de un servicio (<c>ConsultationAccount</c>): cuenta como insumo.</summary>
    public const string FreeSupplyCode = "INSUMO";

    private static readonly StringComparer NameOrder = SuggestedPurchaseRules.NameOrder;

    // ================================================================== periodos

    /// <summary>
    /// El periodo del filtro el día <paramref name="today"/> (hora del consultorio, <see cref="ClinicClock.Today"/>): Hoy; Esta
    /// semana, del lunes a hoy; Este mes, del día 1 a hoy; Mes anterior, el mes completo; Este año, del 1 de enero a hoy;
    /// Personalizado, de <paramref name="customFrom"/> a <paramref name="customTo"/> (los dos días incluidos; si vienen al revés se
    /// voltean; sin alguno, hoy).
    /// </summary>
    public static BusinessPeriod PeriodFor(BusinessPeriodKind kind, DateTime today, DateTime? customFrom = null, DateTime? customTo = null)
    {
        var day = today.Date;
        var tomorrow = day.AddDays(1);
        switch (kind)
        {
            case BusinessPeriodKind.EstaSemana:
                return new BusinessPeriod(AgendaRules.StartOfWeek(day), tomorrow);
            case BusinessPeriodKind.EsteMes:
                return new BusinessPeriod(new DateTime(day.Year, day.Month, 1), tomorrow);
            case BusinessPeriodKind.MesAnterior:
                var thisMonth = new DateTime(day.Year, day.Month, 1);
                return new BusinessPeriod(thisMonth.AddMonths(-1), thisMonth);
            case BusinessPeriodKind.EsteAnio:
                return new BusinessPeriod(new DateTime(day.Year, 1, 1), tomorrow);
            case BusinessPeriodKind.Personalizado:
                var from = (customFrom ?? day).Date;
                var to = (customTo ?? day).Date;
                if (to < from) (from, to) = (to, from);
                return new BusinessPeriod(from, to.AddDays(1));
            default:
                return new BusinessPeriod(day, tomorrow);
        }
    }

    /// <summary>
    /// El periodo anterior igual: Hoy → ayer; Esta semana → los mismos días de la semana anterior (del lunes anterior, tantos días
    /// como van: un lunes se compara con el lunes anterior); un rango → los mismos días justo antes; Este mes y Este año →
    /// el mes (o el año) anterior desde su inicio y con los mismos días transcurridos, sin pasar de su fin (el 31 de marzo se
    /// compara con febrero completo); Mes anterior → el mes completo de antes.
    /// </summary>
    public static BusinessPeriod PreviousPeriod(BusinessPeriodKind kind, BusinessPeriod period)
    {
        int days = period.Days;
        switch (kind)
        {
            case BusinessPeriodKind.MesAnterior:
                return new BusinessPeriod(period.From.AddMonths(-1), period.From);
            case BusinessPeriodKind.EstaSemana:
                return new BusinessPeriod(period.From.AddDays(-7), period.From.AddDays(-7 + days));
            case BusinessPeriodKind.EsteMes:
            {
                var start = period.From.AddMonths(-1);
                return new BusinessPeriod(start, Min(start.AddDays(days), period.From));
            }
            case BusinessPeriodKind.EsteAnio:
            {
                var start = period.From.AddYears(-1);
                return new BusinessPeriod(start, Min(start.AddDays(days), period.From));
            }
            default:
                return new BusinessPeriod(period.From.AddDays(-days), period.From);
        }
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>"22/10/2026" para un día; "01/10/2026 – 22/10/2026" para un rango (el último día incluido).</summary>
    public static string PeriodText(BusinessPeriod period)
    {
        var last = period.To.AddDays(-1);
        return period.Days <= 1
            ? period.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : $"{period.From.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} – {last.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
    }

    /// <summary>El nombre de cada periodo en el filtro.</summary>
    public static string PeriodName(BusinessPeriodKind kind) => kind switch
    {
        BusinessPeriodKind.Hoy => "Hoy",
        BusinessPeriodKind.EstaSemana => "Esta semana",
        BusinessPeriodKind.EsteMes => "Este mes",
        BusinessPeriodKind.MesAnterior => "Mes anterior",
        BusinessPeriodKind.EsteAnio => "Este año",
        _ => "Personalizado"
    };

    /// <summary>Una fecha dd/mm/aaaa como la escribe la máscara de fecha; null si no es una fecha completa y válida.</summary>
    public static DateTime? ParseDate(string? text) =>
        DateTime.TryParseExact((text ?? string.Empty).Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date
            : null;

    // ================================================================== tipos

    /// <summary>El nombre de cada tipo en el filtro y en la tabla.</summary>
    public static string TypeName(BusinessLineType type) => type switch
    {
        BusinessLineType.Consultas => "Consultas",
        BusinessLineType.Medicamentos => "Medicamentos",
        BusinessLineType.Insumos => "Insumos",
        BusinessLineType.Servicios => "Servicios",
        _ => "Otros"
    };

    /// <summary>El tipo de un nombre del filtro ("Todo" o cualquier otro = null).</summary>
    public static BusinessLineType? TypeFromName(string? name) =>
        Enum.GetValues<BusinessLineType>().Cast<BusinessLineType?>().FirstOrDefault(t => TypeName(t!.Value) == name);

    /// <summary>
    /// El tipo de un renglón vendido, con las reglas que ya usa la app:
    /// <list type="bullet">
    /// <item>Servicio que es el honorario (<see cref="ConsultationAccountRules.IsConsultationFee"/>: con el servicio del catálogo si
    /// existe, si no con el código y el nombre del renglón) → Consultas.</item>
    /// <item>Otro servicio del catálogo (<c>ClinicalServiceId</c>, aunque ya se haya borrado) → Servicios.</item>
    /// <item>Cargo libre: el de un insumo ("INSUMO") → Insumos; los demás → Otros.</item>
    /// <item>Producto: medicamento o insumo con <see cref="CatalogMatchingRules.IsProductMedication"/> (su categoría y código de hoy);
    /// si ya no está en el catálogo → Otros.</item>
    /// </list>
    /// </summary>
    public static BusinessLineType Classify(BusinessSaleLine line, Product? product, ClinicalService? service)
    {
        if (line.IsService)
        {
            bool isFee = service != null
                ? ConsultationAccountRules.IsConsultationFee(service.Code, service.Name, service.Category)
                : ConsultationAccountRules.IsConsultationFee(line.Code, line.Description, null);
            if (isFee) return BusinessLineType.Consultas;
            if (line.ClinicalServiceId != null) return BusinessLineType.Servicios;
            return string.Equals(line.Code, FreeSupplyCode, StringComparison.OrdinalIgnoreCase) ? BusinessLineType.Insumos : BusinessLineType.Otros;
        }
        if (product == null) return BusinessLineType.Otros;
        return CatalogMatchingRules.IsProductMedication(product) ? BusinessLineType.Medicamentos : BusinessLineType.Insumos;
    }

    // ================================================================== costo

    /// <summary>
    /// El costo que se guarda al vender o al mover (revisión 18): el de una unidad con IVA (<see cref="Product.EffectiveUnitCost"/>)
    /// o, en piezas sueltas de un paquete, el de una pieza (<see cref="Product.IndividualUnitCost"/>); null si el producto no tiene
    /// costo (no se guarda un 0 que parecería real).
    /// </summary>
    public static decimal? CostToRecord(Product product, bool individualUnits)
    {
        if (!product.HasCost) return null;
        return individualUnits && product.HasMultipleUnitsPerPackage ? product.IndividualUnitCost : product.EffectiveUnitCost;
    }

    /// <summary>
    /// El renglón vendió piezas sueltas: su marca (revisión 17) o, en un renglón de antes, la regla vieja (el producto tiene varias
    /// piezas por paquete y el renglón se cobró a menos del precio del paquete de hoy). Es <c>InventoryService.SoldIndividualUnits</c>.
    /// </summary>
    public static bool SoldIndividualUnits(bool? isIndividualUnits, decimal unitPrice, Product? product) =>
        isIndividualUnits ?? (product is { HasMultipleUnitsPerPackage: true } && unitPrice < product.Price);

    /// <summary>
    /// En los reportes, el renglón vendió piezas sueltas solo si lo dice su marca (revisión 17). Un renglón sin marca es anterior a
    /// v1.47.0, cuando todo se vendía en paquetes completos: cuenta como paquetes. No se usa la regla vieja de
    /// <see cref="SoldIndividualUnits"/> (precio y piezas por paquete de hoy): con un precio que subió después, una caja vendida
    /// antes saldría como piezas, con un costo por pieza muy bajo. Esa regla se queda para cancelar.
    /// </summary>
    public static bool IsLoosePiecesLine(bool? isIndividualUnits) => isIndividualUnits ?? false;

    /// <summary>
    /// El costo de un renglón: un servicio o cargo libre no tiene (NotApplicable); un producto con costo guardado, cantidad × ese
    /// costo (Recorded); sin él, cantidad × el costo de hoy del producto, por pieza si el renglón tiene la marca de piezas sueltas
    /// (<see cref="IsLoosePiecesLine"/>; sin marca, por paquete) (Estimated); y si el producto no tiene costo hoy o ya no existe,
    /// 0 (Missing).
    /// </summary>
    public static (decimal Cost, BusinessCostSource Source) LineCost(BusinessSaleLine line, Product? product)
    {
        if (line.IsService) return (0m, BusinessCostSource.NotApplicable);
        if (line.UnitCost is decimal recorded) return (recorded * line.Quantity, BusinessCostSource.Recorded);
        if (product == null || !product.HasCost) return (0m, BusinessCostSource.Missing);
        var unit = IsLoosePiecesLine(line.IsIndividualUnits) ? product.IndividualUnitCost : product.EffectiveUnitCost;
        return (unit * line.Quantity, BusinessCostSource.Estimated);
    }

    // ================================================================== renglones

    /// <summary>
    /// Los renglones del periodo ya evaluados: sin las ventas canceladas ni las de fuera del periodo, y solo los del tipo
    /// <paramref name="type"/> si se da (null = Todo).
    /// </summary>
    public static List<BusinessLine> Lines(IEnumerable<BusinessSaleLine> lines, IEnumerable<Product> products, IEnumerable<ClinicalService> services,
        BusinessPeriod period, BusinessLineType? type = null)
    {
        var productById = ById(products, p => p.Id);
        var serviceById = ById(services, s => s.Id);
        var result = new List<BusinessLine>();
        foreach (var line in lines)
        {
            if (line.SaleCancelled || !period.Contains(line.SaleDate)) continue;
            var evaluated = Evaluate(line, productById, serviceById);
            if (type == null || evaluated.Type == type) result.Add(evaluated);
        }
        return result;
    }

    private static Dictionary<int, T> ById<T>(IEnumerable<T> items, Func<T, int> id)
    {
        var map = new Dictionary<int, T>();
        foreach (var item in items) map.TryAdd(id(item), item);
        return map;
    }

    private static BusinessLine Evaluate(BusinessSaleLine line, IReadOnlyDictionary<int, Product> products, IReadOnlyDictionary<int, ClinicalService> services)
    {
        Product? product = !line.IsService && line.ProductId is int pid && products.TryGetValue(pid, out var p) ? p : null;
        ClinicalService? service = line.IsService && line.ClinicalServiceId is int sid && services.TryGetValue(sid, out var s) ? s : null;
        var type = Classify(line, product, service);
        var (cost, source) = LineCost(line, product);
        bool loose = !line.IsService && IsLoosePiecesLine(line.IsIndividualUnits);
        // Un producto se nombra como hoy (dos renglones del mismo producto se juntan); un servicio o cargo, como se cobró
        var name = product?.Name ?? (string.IsNullOrWhiteSpace(line.Description) ? line.Code : line.Description);
        return new BusinessLine(line, type, name, loose, line.Subtotal, cost, source);
    }

    // ================================================================== resumen

    /// <summary>
    /// Las tarjetas: ventas (Σ subtotal), costo, utilidad (ventas − costo), margen (utilidad / ventas, en %), tickets (ventas
    /// distintas con algún renglón), ticket promedio (ventas / tickets) y cuántos renglones usan un costo estimado o no tienen.
    /// </summary>
    public static BusinessSummary Summarize(IReadOnlyCollection<BusinessLine> lines)
    {
        if (lines.Count == 0) return BusinessSummary.Empty;
        decimal sales = lines.Sum(l => l.Sales);
        decimal cost = lines.Sum(l => l.Cost);
        int tickets = lines.Select(l => l.Source.SaleId).Distinct().Count();
        return new BusinessSummary(
            sales,
            cost,
            sales - cost,
            Percent(sales - cost, sales),
            tickets,
            tickets == 0 ? null : Math.Round(sales / tickets, 2),
            lines.Count(l => l.CostSource == BusinessCostSource.Estimated),
            lines.Count(l => l.CostSource == BusinessCostSource.Missing));
    }

    /// <summary>El cambio de ventas, utilidad y tickets contra el periodo anterior igual: (actual − anterior) / |anterior|, en %.</summary>
    public static BusinessComparison Compare(BusinessSummary current, BusinessSummary previous) => new(
        Change(current.Sales, previous.Sales),
        Change(current.Profit, previous.Profit),
        Change(current.Tickets, previous.Tickets));

    /// <summary>(actual − anterior) / |anterior| × 100 con un decimal; null si el anterior es 0 (no hay con qué comparar).</summary>
    public static decimal? Change(decimal current, decimal previous) =>
        previous == 0 ? null : Math.Round((current - previous) / Math.Abs(previous) * 100m, 1);

    /// <summary><paramref name="part"/> / <paramref name="whole"/> × 100 con un decimal; null si el total es 0.</summary>
    public static decimal? Percent(decimal part, decimal whole) => whole == 0 ? null : Math.Round(part / whole * 100m, 1);

    /// <summary>"▲ 12.5 %", "▼ 3.0 %", "= 0.0 %" o "sin comparación".</summary>
    public static string ChangeText(decimal? change) => change switch
    {
        null => "sin comparación",
        > 0m => $"▲ {change.Value.ToString("0.0", CultureInfo.InvariantCulture)} %",
        < 0m => $"▼ {Math.Abs(change.Value).ToString("0.0", CultureInfo.InvariantCulture)} %",
        _ => "= 0.0 %"
    };

    /// <summary>La nota del costo: "", "N renglones con costo estimado (el costo de hoy)", más "· M sin costo".</summary>
    public static string EstimatedCostNote(int estimated, int missing)
    {
        var parts = new List<string>();
        if (estimated > 0) parts.Add(estimated == 1 ? "1 renglón con costo estimado (el costo de hoy del producto)" : $"{estimated} renglones con costo estimado (el costo de hoy del producto)");
        if (missing > 0) parts.Add(missing == 1 ? "1 sin costo (cuenta 0)" : $"{missing} sin costo (cuentan 0)");
        return string.Join(" · ", parts);
    }

    // ================================================================== por sección

    /// <summary>
    /// «Por sección»: un renglón por tipo que tiene ventas en el periodo, en el orden del enum, con ventas, costo, utilidad,
    /// margen y participación (sus ventas / las de todos, en %).
    /// </summary>
    public static List<BusinessTypeRow> ByType(IReadOnlyCollection<BusinessLine> lines)
    {
        decimal total = lines.Sum(l => l.Sales);
        return lines
            .GroupBy(l => l.Type)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                decimal sales = g.Sum(l => l.Sales), cost = g.Sum(l => l.Cost);
                return new BusinessTypeRow(g.Key, TypeName(g.Key), sales, cost, sales - cost, Percent(sales - cost, sales), Percent(sales, total),
                    g.Count(), g.Count(l => l.CostSource == BusinessCostSource.Estimated));
            })
            .ToList();
    }

    // ================================================================== más vendidos

    /// <summary>
    /// Los productos más vendidos de <paramref name="type"/> (Medicamentos, Insumos, Otros, o null = todos los productos): un
    /// renglón por producto y por si son piezas sueltas (las piezas cuentan como piezas, aparte de los paquetes), por cantidad o
    /// por utilidad de mayor a menor; empates por ventas y luego por nombre (es-MX). <paramref name="top"/> como máximo.
    /// </summary>
    public static List<BusinessTopProduct> TopProducts(IEnumerable<BusinessLine> lines, BusinessLineType? type, BusinessRanking ranking, int top = DefaultTop)
    {
        var rows = lines
            .Where(l => !l.Source.IsService && (type == null || l.Type == type))
            .GroupBy(l => (Key: l.Source.ProductId?.ToString(CultureInfo.InvariantCulture) ?? "#" + l.Name, l.IsLoosePieces))
            .Select(g =>
            {
                decimal sales = g.Sum(l => l.Sales), cost = g.Sum(l => l.Cost);
                var first = g.First();
                return new BusinessTopProduct(first.Source.ProductId, first.Name, first.Type, g.Key.IsLoosePieces, g.Sum(l => l.Source.Quantity),
                    sales, cost, sales - cost, g.Any(l => l.CostSource != BusinessCostSource.Recorded));
            });
        var ordered = ranking == BusinessRanking.Profit
            ? rows.OrderByDescending(r => r.Profit).ThenByDescending(r => r.Quantity)
            : rows.OrderByDescending(r => r.Quantity).ThenByDescending(r => r.Profit);
        return ordered.ThenByDescending(r => r.Sales).ThenBy(r => r.Name, NameOrder).ThenBy(r => r.IsLoosePieces).Take(Math.Max(0, top)).ToList();
    }

    /// <summary>
    /// Los servicios más cobrados (consultas y servicios): un renglón por servicio del catálogo (o por nombre si no tiene),
    /// cuántas veces (Σ cantidad) y cuánto dejó (Σ subtotal), de más a menos veces; empates por ingreso y nombre (es-MX).
    /// </summary>
    public static List<BusinessTopService> TopServices(IEnumerable<BusinessLine> lines, int top = DefaultTop) =>
        lines
            .Where(l => l.Source.IsService && l.Type is BusinessLineType.Consultas or BusinessLineType.Servicios)
            .GroupBy(l => l.Source.ClinicalServiceId?.ToString(CultureInfo.InvariantCulture) ?? "#" + l.Name)
            .Select(g => new BusinessTopService(g.First().Source.ClinicalServiceId, g.First().Name, g.First().Type, g.Sum(l => l.Source.Quantity), g.Sum(l => l.Sales)))
            .OrderByDescending(r => r.Count)
            .ThenByDescending(r => r.Income)
            .ThenBy(r => r.Name, NameOrder)
            .Take(Math.Max(0, top))
            .ToList();

    // ================================================================== en el tiempo

    /// <summary>
    /// «En el tiempo»: ventas y utilidad por día (periodos de hasta <see cref="MaxDailyBuckets"/> días, etiqueta "dd/MM") o por mes
    /// ("MM/yyyy"), con los días o meses sin ventas en 0, en orden.
    /// </summary>
    public static List<BusinessTimePoint> Timeline(IReadOnlyCollection<BusinessLine> lines, BusinessPeriod period)
    {
        bool daily = period.Days <= MaxDailyBuckets;
        DateTime BucketOf(DateTime d) => daily ? d.Date : new DateTime(d.Year, d.Month, 1);
        var sums = lines
            .GroupBy(l => BucketOf(l.Source.SaleDate))
            .ToDictionary(g => g.Key, g => (Sales: g.Sum(l => l.Sales), Profit: g.Sum(l => l.Profit)));

        var points = new List<BusinessTimePoint>();
        for (var start = BucketOf(period.From); start < period.To; start = daily ? start.AddDays(1) : start.AddMonths(1))
        {
            sums.TryGetValue(start, out var sum);
            var label = start.ToString(daily ? "dd/MM" : "MM/yyyy", CultureInfo.InvariantCulture);
            points.Add(new BusinessTimePoint(start, label, sum.Sales, sum.Profit));
        }
        return points;
    }

    // ================================================================== exportar

    /// <summary>
    /// El CSV de lo que se ve (separador ",", importes F2 invariantes, celdas entrecomilladas si traen coma, comillas o salto):
    /// el periodo y el filtro, el resumen, por sección, los más vendidos, los servicios, en el tiempo, las compras, el consumo y
    /// el valor del inventario. Se escribe con UTF-8 con BOM para Excel, como el corte de caja.
    /// </summary>
    public static string ToCsv(BusinessCsvReport report)
    {
        var sb = new StringBuilder();
        void Row(params object?[] cells) => sb.AppendLine(string.Join(",", cells.Select(Cell)));

        Row("PANEL DEL NEGOCIO", report.ClinicName);
        Row("Periodo", report.PeriodName, PeriodText(report.Period));
        Row("Tipo", report.TypeFilter is { } t ? TypeName(t) : "Todo");
        Row("Generado el", report.GeneratedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        sb.AppendLine();

        var s = report.Summary;
        Row("RESUMEN");
        Row("Ventas", Money(s.Sales), "Comparado con el periodo anterior", ChangeText(report.Comparison.SalesChangePercent));
        Row("Costo", Money(s.Cost));
        Row("Utilidad bruta", Money(s.Profit), "Comparado con el periodo anterior", ChangeText(report.Comparison.ProfitChangePercent));
        Row("Margen %", PercentCell(s.MarginPercent));
        Row("Tickets", s.Tickets, "Comparado con el periodo anterior", ChangeText(report.Comparison.TicketsChangePercent));
        Row("Ticket promedio", s.AverageTicket is { } avg ? Money(avg) : string.Empty);
        var note = EstimatedCostNote(s.EstimatedCostLines, s.MissingCostLines);
        if (note.Length > 0) Row("Nota", note);
        sb.AppendLine();

        Row("POR SECCIÓN");
        Row("Tipo", "Ventas", "Costo", "Utilidad", "Margen %", "Participación %", "Renglones", "Con costo estimado");
        foreach (var r in report.ByType)
            Row(r.Name, Money(r.Sales), Money(r.Cost), Money(r.Profit), PercentCell(r.MarginPercent), PercentCell(r.SharePercent), r.Lines, r.EstimatedCostLines);
        sb.AppendLine();

        Row("PRODUCTOS MÁS VENDIDOS", report.TopProductsTitle);
        Row("Producto", "Tipo", "Cantidad", "Ventas", "Costo", "Utilidad", "Costo estimado");
        foreach (var p in report.TopProducts)
            Row(p.Name, TypeName(p.Type), p.QuantityText, Money(p.Sales), Money(p.Cost), Money(p.Profit), p.HasEstimatedCost ? "Sí" : "No");
        sb.AppendLine();

        Row("SERVICIOS MÁS COBRADOS");
        Row("Servicio", "Tipo", "Veces", "Ingreso");
        foreach (var x in report.TopServices) Row(x.Name, TypeName(x.Type), x.Count, Money(x.Income));
        sb.AppendLine();

        Row("EN EL TIEMPO");
        Row("Fecha", "Ventas", "Utilidad");
        foreach (var x in report.Timeline) Row(x.Label, Money(x.Sales), Money(x.Profit));
        sb.AppendLine();

        var pu = report.Purchases;
        Row("COMPRAS");
        Row("Entradas", pu.Entries, "Pedidos", pu.Orders, "Unidades", pu.Units, "Costo", Money(pu.Cost));
        Row("Proveedor", "Entradas", "Unidades", "Costo", "Con costo estimado");
        foreach (var x in pu.BySupplier) Row(x.Supplier, x.Entries, x.Units, Money(x.Cost), x.EstimatedCostLines);
        Row("Tipo", "Entradas", "Unidades", "Costo");
        foreach (var x in pu.ByType) Row(x.Name, x.Entries, x.Units, Money(x.Cost));
        sb.AppendLine();

        var c = report.Consumption;
        Row("CONSUMO DE INSUMOS");
        Row("Producto", "Tipo", "Usado en consulta", "Cobrado con consultas y servicios", "Cantidad", "Costo", "Costo estimado");
        foreach (var x in c.Rows)
            Row(x.Name, TypeName(x.Type), x.UsedInConsultation, x.ChargedWithServices, x.QuantityText, Money(x.Cost), x.HasEstimatedCost ? "Sí" : "No");
        Row("Total", string.Empty, string.Empty, string.Empty, string.Empty, Money(c.Cost));
        sb.AppendLine();

        Row("VALOR DEL INVENTARIO (HOY)", Money(report.InventoryValue), InventoryValueRules.Detail(report.StockedWithoutCost));
        return sb.ToString();
    }

    private static string Money(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    private static string PercentCell(decimal? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Cell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}

/// <summary>Lo que se exporta a CSV: la vista actual del panel.</summary>
public sealed record BusinessCsvReport(
    string ClinicName,
    string PeriodName,
    BusinessPeriod Period,
    BusinessLineType? TypeFilter,
    DateTime GeneratedAt,
    BusinessSummary Summary,
    BusinessComparison Comparison,
    IReadOnlyList<BusinessTypeRow> ByType,
    string TopProductsTitle,
    IReadOnlyList<BusinessTopProduct> TopProducts,
    IReadOnlyList<BusinessTopService> TopServices,
    IReadOnlyList<BusinessTimePoint> Timeline,
    BusinessPurchases Purchases,
    BusinessConsumption Consumption,
    decimal InventoryValue,
    int StockedWithoutCost);
