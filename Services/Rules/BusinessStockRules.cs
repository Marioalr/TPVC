using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Las compras y el consumo de insumos del panel «Negocio» (pedido del médico del 2026-10-22), a partir del Kardex (paso 8.19)
/// y de los renglones de los tickets. Sin WPF ni base: vectores en <c>contracts/golden/purchases.json</c> y
/// <c>supply-consumption.json</c>. Los importes no se redondean (cantidad × costo de dos decimales).
/// </summary>
public static class BusinessStockRules
{
    /// <summary>Así empieza el motivo de las entradas de un pedido del proveedor (<c>InventoryService.SupplierOrderReason</c>).</summary>
    public const string OrderReasonPrefix = "Pedido";

    /// <summary>El proveedor de un pedido sin nombre de proveedor ("Pedido #5175").</summary>
    public const string OrderWithoutSupplier = "Pedido sin proveedor";

    /// <summary>Lo que no llegó en un pedido: un lote recibido o una entrada a mano en Productos.</summary>
    public const string EntryWithoutOrder = "Entrada sin pedido";

    // ================================================================== compras

    /// <summary>La entrada es de un pedido del proveedor: su motivo es "Pedido", "Pedido LUIN" o "Pedido LUIN #5175".</summary>
    public static bool IsSupplierOrder(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        return text.StartsWith(OrderReasonPrefix, StringComparison.Ordinal)
               && (text.Length == OrderReasonPrefix.Length || text[OrderReasonPrefix.Length] == ' ');
    }

    /// <summary>
    /// El proveedor de una entrada por su motivo: "Pedido LUIN #5175" → "LUIN" (lo que va entre "Pedido " y " #número"); un
    /// pedido sin proveedor → "Pedido sin proveedor"; cualquier otra entrada (un lote, una entrada a mano) → "Entrada sin pedido".
    /// </summary>
    public static string SupplierOf(string? reason)
    {
        if (!IsSupplierOrder(reason)) return EntryWithoutOrder;
        var rest = reason!.Trim()[OrderReasonPrefix.Length..].Trim();
        if (rest.StartsWith('#')) rest = string.Empty;
        int number = rest.LastIndexOf(" #", StringComparison.Ordinal);
        if (number >= 0) rest = rest[..number];
        rest = rest.Trim();
        return rest.Length == 0 ? OrderWithoutSupplier : rest;
    }

    /// <summary>El tipo de un producto del Kardex: medicamento, insumo u Otros si ya no está en el catálogo.</summary>
    public static BusinessLineType ProductType(Product? product) =>
        product == null ? BusinessLineType.Otros
        : CatalogMatchingRules.IsProductMedication(product) ? BusinessLineType.Medicamentos
        : BusinessLineType.Insumos;

    /// <summary>
    /// El costo de un movimiento: cantidad × el costo guardado (Recorded); sin él, × el costo de hoy del producto (Estimated); y
    /// 0 si el producto no tiene costo (Missing).
    /// </summary>
    public static (decimal Cost, BusinessCostSource Source) MovementCost(BusinessStockMovement movement, Product? product)
    {
        if (movement.UnitCost is decimal recorded) return (recorded * movement.Quantity, BusinessCostSource.Recorded);
        if (product == null || !product.HasCost) return (0m, BusinessCostSource.Missing);
        return (product.EffectiveUnitCost * movement.Quantity, BusinessCostSource.Estimated);
    }

    /// <summary>
    /// «Compras» del periodo: cada entrada por compra (EntradaCompra: un renglón de un pedido, un lote recibido o una entrada a
    /// mano) con su costo; los pedidos son los motivos "Pedido …" distintos. Por proveedor (de mayor a menor costo, luego por
    /// nombre es-MX) y por tipo (en el orden del enum). Con <paramref name="type"/>, solo los productos de ese tipo (Consultas o
    /// Servicios no tienen compras).
    /// </summary>
    public static BusinessPurchases Purchases(IEnumerable<BusinessStockMovement> movements, IEnumerable<Product> products, BusinessPeriod period,
        BusinessLineType? type = null)
    {
        var productById = ById(products);
        var entries = movements
            .Where(m => m.Type == MovementType.EntradaCompra && period.Contains(m.Date) && m.Quantity > 0)
            .Select(m =>
            {
                productById.TryGetValue(m.ProductId, out var product);
                var (cost, source) = MovementCost(m, product);
                return (Movement: m, Type: ProductType(product), Supplier: SupplierOf(m.Reason), Cost: cost, Source: source);
            })
            .Where(e => type == null || e.Type == type)
            .ToList();
        if (entries.Count == 0) return BusinessPurchases.Empty;

        var bySupplier = entries
            .GroupBy(e => e.Supplier)
            .Select(g => new BusinessPurchaseSupplierRow(g.Key, g.Count(), g.Sum(e => e.Movement.Quantity), g.Sum(e => e.Cost),
                g.Count(e => e.Source == BusinessCostSource.Estimated)))
            .OrderByDescending(r => r.Cost)
            .ThenBy(r => r.Supplier, SuggestedPurchaseRules.NameOrder)
            .ToList();
        var byType = entries
            .GroupBy(e => e.Type)
            .OrderBy(g => g.Key)
            .Select(g => new BusinessPurchaseTypeRow(g.Key, BusinessReportRules.TypeName(g.Key), g.Count(), g.Sum(e => e.Movement.Quantity), g.Sum(e => e.Cost)))
            .ToList();

        return new BusinessPurchases(
            entries.Count,
            entries.Where(e => IsSupplierOrder(e.Movement.Reason)).Select(e => e.Movement.Reason!.Trim()).Distinct(StringComparer.Ordinal).Count(),
            entries.Sum(e => e.Movement.Quantity),
            entries.Sum(e => e.Cost),
            entries.Count(e => e.Source == BusinessCostSource.Estimated),
            entries.Count(e => e.Source == BusinessCostSource.Missing),
            bySupplier,
            byType);
    }

    // ================================================================== consumo de insumos

    /// <summary>
    /// «Consumo de insumos» del periodo, por producto (y aparte sus piezas sueltas):
    /// <list type="bullet">
    /// <item>Usado en consulta: las salidas del Kardex de tipo SalidaConsulta (lo que se usó sin cobrarlo), con su costo guardado o
    /// estimado.</item>
    /// <item>Cobrado con consultas y servicios: los renglones de insumos (<see cref="BusinessLineType.Insumos"/>) de un ticket no
    /// cancelado que también cobró una consulta o un servicio (los insumos de un servicio y los de la hoja de la consulta), con el
    /// costo de su renglón. Las piezas sueltas se cuentan como piezas: el Kardex solo anota los paquetes que se abren.</item>
    /// </list>
    /// <paramref name="lines"/> son los renglones del periodo de todos los tipos (<see cref="BusinessReportRules.Lines"/> sin tipo).
    /// Con <paramref name="type"/>: Consultas, lo usado en consulta y lo cobrado en tickets con consulta; Servicios, lo cobrado en
    /// tickets con un servicio; Medicamentos, Insumos u Otros, los productos de ese tipo. De mayor a menor costo, luego cantidad
    /// y nombre (es-MX).
    /// </summary>
    public static BusinessConsumption SupplyConsumption(IEnumerable<BusinessStockMovement> movements, IReadOnlyCollection<BusinessLine> lines,
        IEnumerable<Product> products, BusinessPeriod period, BusinessLineType? type = null)
    {
        var productById = ById(products);
        var rows = new Dictionary<(int ProductId, bool Loose), Accumulator>();

        Accumulator Row(int productId, bool loose, string name, BusinessLineType rowType)
        {
            if (!rows.TryGetValue((productId, loose), out var acc)) rows[(productId, loose)] = acc = new Accumulator(productId, name, rowType, loose);
            return acc;
        }

        bool includeUsed = type is null or BusinessLineType.Consultas or BusinessLineType.Medicamentos or BusinessLineType.Insumos or BusinessLineType.Otros;
        if (includeUsed)
        {
            foreach (var m in movements.Where(m => m.Type == MovementType.SalidaConsulta && period.Contains(m.Date) && m.Quantity > 0))
            {
                productById.TryGetValue(m.ProductId, out var product);
                var rowType = ProductType(product);
                if (type is BusinessLineType.Medicamentos or BusinessLineType.Insumos or BusinessLineType.Otros && rowType != type) continue;
                var (cost, source) = MovementCost(m, product);
                var acc = Row(m.ProductId, false, product?.Name ?? $"Producto {m.ProductId}", rowType);
                acc.Used += m.Quantity;
                acc.Add(cost, source);
            }
        }

        var saleTypes = lines.GroupBy(l => l.Source.SaleId).ToDictionary(g => g.Key, g => g.Select(l => l.Type).ToHashSet());
        bool ChargedWithServices(int saleId) => saleTypes.TryGetValue(saleId, out var kinds) && type switch
        {
            BusinessLineType.Consultas => kinds.Contains(BusinessLineType.Consultas),
            BusinessLineType.Servicios => kinds.Contains(BusinessLineType.Servicios),
            _ => kinds.Contains(BusinessLineType.Consultas) || kinds.Contains(BusinessLineType.Servicios)
        };

        if (type is null or BusinessLineType.Consultas or BusinessLineType.Servicios or BusinessLineType.Insumos)
        {
            foreach (var line in lines.Where(l => l.Type == BusinessLineType.Insumos && !l.Source.IsService && l.Source.ProductId != null))
            {
                if (!ChargedWithServices(line.Source.SaleId)) continue;
                var acc = Row(line.Source.ProductId!.Value, line.IsLoosePieces, line.Name, line.Type);
                acc.Charged += line.Source.Quantity;
                acc.Add(line.Cost, line.CostSource);
            }
        }

        var result = rows.Values
            .Select(a => new BusinessConsumptionRow(a.ProductId, a.Name, a.Type, a.Loose, a.Used, a.Charged, a.Cost, a.Estimated > 0 || a.Missing > 0))
            .OrderByDescending(r => r.Cost)
            .ThenByDescending(r => r.Quantity)
            .ThenBy(r => r.Name, SuggestedPurchaseRules.NameOrder)
            .ThenBy(r => r.IsLoosePieces)
            .ToList();
        if (result.Count == 0) return BusinessConsumption.Empty;
        return new BusinessConsumption(result.Sum(r => r.Cost), rows.Values.Sum(a => a.Estimated), rows.Values.Sum(a => a.Missing), result);
    }

    private sealed class Accumulator(int productId, string name, BusinessLineType type, bool loose)
    {
        public int ProductId { get; } = productId;
        public string Name { get; } = name;
        public BusinessLineType Type { get; } = type;
        public bool Loose { get; } = loose;
        public int Used { get; set; }
        public int Charged { get; set; }
        public decimal Cost { get; private set; }
        public int Estimated { get; private set; }
        public int Missing { get; private set; }

        public void Add(decimal cost, BusinessCostSource source)
        {
            Cost += cost;
            if (source == BusinessCostSource.Estimated) Estimated++;
            if (source == BusinessCostSource.Missing) Missing++;
        }
    }

    private static Dictionary<int, Product> ById(IEnumerable<Product> products)
    {
        var map = new Dictionary<int, Product>();
        foreach (var p in products) map.TryAdd(p.Id, p);
        return map;
    }
}
