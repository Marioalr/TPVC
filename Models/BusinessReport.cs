using System;
using System.Collections.Generic;

namespace TPVC.Models;

// Panel «Negocio» (pedido del médico del 2026-10-22): lo que leen las reglas puras de Services/Rules/BusinessReportRules.cs y
// BusinessStockRules.cs y lo que regresan. Las entradas son proyecciones de la base (sin EF) y las salidas son cifras con
// decimal, iguales a las de los vectores contracts/golden/business-*.json, sales-by-type, best-sellers, purchases y
// supply-consumption (la app de Kotlin los porta).

/// <summary>El tipo de cada renglón vendido: el filtro «Tipo» y la tabla «Por sección». El orden es el de la tabla.</summary>
public enum BusinessLineType
{
    /// <summary>El honorario de la consulta (un servicio con código SERV-001, de categoría "Consulta" o con "Consulta" en el nombre).</summary>
    Consultas,

    /// <summary>Un producto que es medicamento (<c>CatalogMatchingRules.IsProductMedication</c>).</summary>
    Medicamentos,

    /// <summary>Un producto que es insumo (<c>CatalogMatchingRules.IsProductSupply</c>) o el cargo libre de un insumo ("INSUMO").</summary>
    Insumos,

    /// <summary>Otro servicio clínico del catálogo (aunque después se haya borrado).</summary>
    Servicios,

    /// <summary>Un cargo libre o un producto que ya no está en el catálogo.</summary>
    Otros
}

/// <summary>De dónde sale el costo de un renglón.</summary>
public enum BusinessCostSource
{
    /// <summary>El que se guardó al vender o al moverse (revisión 18).</summary>
    Recorded,

    /// <summary>Sin costo guardado: el costo de hoy del producto ("costo estimado").</summary>
    Estimated,

    /// <summary>Sin costo guardado y el producto no tiene costo hoy (o ya no existe): cuenta 0 ("sin costo").</summary>
    Missing,

    /// <summary>Un servicio o un cargo libre: no tiene costo propio (sus insumos son sus propios renglones).</summary>
    NotApplicable
}

/// <summary>Los periodos del filtro; <see cref="Personalizado"/> usa un rango de fechas.</summary>
public enum BusinessPeriodKind
{
    Hoy,
    EstaSemana,
    EsteMes,
    MesAnterior,
    EsteAnio,
    Personalizado
}

/// <summary>Cómo se ordenan los más vendidos.</summary>
public enum BusinessRanking
{
    Quantity,
    Profit
}

/// <summary>Un periodo en hora del consultorio: desde <see cref="From"/> (incluido) hasta <see cref="To"/> (excluido), días completos.</summary>
public sealed record BusinessPeriod(DateTime From, DateTime To)
{
    public int Days => (int)(To.Date - From.Date).TotalDays;

    public bool Contains(DateTime date) => date >= From && date < To;
}

/// <summary>Un renglón de un ticket como lo lee el reporte (proyección de SaleItems con su venta).</summary>
public sealed record BusinessSaleLine(
    int SaleId,
    DateTime SaleDate,
    bool SaleCancelled,
    int? ProductId,
    int? ClinicalServiceId,
    bool IsService,
    string Code,
    string Description,
    int Quantity,
    decimal UnitPrice,
    decimal Subtotal,
    decimal? UnitCost,
    bool? IsIndividualUnits);

/// <summary>Un movimiento del Kardex como lo leen las compras y el consumo (proyección de InventoryMovements).</summary>
public sealed record BusinessStockMovement(
    int ProductId,
    DateTime Date,
    MovementType Type,
    int Quantity,
    decimal? UnitCost,
    string? Reason);

/// <summary>Un renglón vendido ya evaluado: su tipo, si son piezas sueltas, su venta y su costo.</summary>
public sealed record BusinessLine(
    BusinessSaleLine Source,
    BusinessLineType Type,
    string Name,
    bool IsLoosePieces,
    decimal Sales,
    decimal Cost,
    BusinessCostSource CostSource)
{
    public decimal Profit => Sales - Cost;
}

/// <summary>Las tarjetas del resumen.</summary>
public sealed record BusinessSummary(
    decimal Sales,
    decimal Cost,
    decimal Profit,
    decimal? MarginPercent,
    int Tickets,
    decimal? AverageTicket,
    int EstimatedCostLines,
    int MissingCostLines)
{
    public static readonly BusinessSummary Empty = new(0m, 0m, 0m, null, 0, null, 0, 0);
}

/// <summary>El cambio contra el periodo anterior igual, en % con un decimal (null si el anterior fue 0).</summary>
public sealed record BusinessComparison(decimal? SalesChangePercent, decimal? ProfitChangePercent, decimal? TicketsChangePercent);

/// <summary>Un renglón de «Por sección».</summary>
public sealed record BusinessTypeRow(
    BusinessLineType Type,
    string Name,
    decimal Sales,
    decimal Cost,
    decimal Profit,
    decimal? MarginPercent,
    decimal? SharePercent,
    int Lines,
    int EstimatedCostLines);

/// <summary>Un producto de «Más vendidos» (las piezas sueltas van aparte de los paquetes del mismo producto).</summary>
public sealed record BusinessTopProduct(
    int? ProductId,
    string Name,
    BusinessLineType Type,
    bool IsLoosePieces,
    int Quantity,
    decimal Sales,
    decimal Cost,
    decimal Profit,
    bool HasEstimatedCost)
{
    /// <summary>"12 piezas sueltas" o "12".</summary>
    public string QuantityText => IsLoosePieces ? $"{Quantity} {(Quantity == 1 ? "pieza suelta" : "piezas sueltas")}" : Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Un servicio de «Más vendidos»: cuántas veces se cobró y cuánto dejó.</summary>
public sealed record BusinessTopService(int? ClinicalServiceId, string Name, BusinessLineType Type, int Count, decimal Income);

/// <summary>Una barra de «En el tiempo»: un día o un mes.</summary>
public sealed record BusinessTimePoint(DateTime Start, string Label, decimal Sales, decimal Profit);

/// <summary>Las compras de un proveedor en el periodo.</summary>
public sealed record BusinessPurchaseSupplierRow(string Supplier, int Entries, int Units, decimal Cost, int EstimatedCostLines);

/// <summary>Las compras de un tipo (medicamentos o insumos) en el periodo.</summary>
public sealed record BusinessPurchaseTypeRow(BusinessLineType Type, string Name, int Entries, int Units, decimal Cost);

/// <summary>«Compras»: las entradas por compra del periodo (pedidos del proveedor, lotes recibidos y entradas a mano).</summary>
public sealed record BusinessPurchases(
    int Entries,
    int Orders,
    int Units,
    decimal Cost,
    int EstimatedCostLines,
    int MissingCostLines,
    IReadOnlyList<BusinessPurchaseSupplierRow> BySupplier,
    IReadOnlyList<BusinessPurchaseTypeRow> ByType)
{
    public static readonly BusinessPurchases Empty = new(0, 0, 0, 0m, 0, 0, Array.Empty<BusinessPurchaseSupplierRow>(), Array.Empty<BusinessPurchaseTypeRow>());
}

/// <summary>Un producto de «Consumo de insumos»: lo usado sin cobrar (Kardex) y lo cobrado en consultas y servicios.</summary>
public sealed record BusinessConsumptionRow(
    int ProductId,
    string Name,
    BusinessLineType Type,
    bool IsLoosePieces,
    int UsedInConsultation,
    int ChargedWithServices,
    decimal Cost,
    bool HasEstimatedCost)
{
    public int Quantity => UsedInConsultation + ChargedWithServices;

    public string QuantityText => IsLoosePieces ? $"{Quantity} {(Quantity == 1 ? "pieza suelta" : "piezas sueltas")}" : Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>«Consumo de insumos» del periodo.</summary>
public sealed record BusinessConsumption(
    decimal Cost,
    int EstimatedCostLines,
    int MissingCostLines,
    IReadOnlyList<BusinessConsumptionRow> Rows)
{
    public static readonly BusinessConsumption Empty = new(0m, 0, 0, Array.Empty<BusinessConsumptionRow>());
}

/// <summary>Lo que el panel lee de la base para un rango de fechas (proyecciones, sin seguimiento).</summary>
public sealed class BusinessReportData
{
    public IReadOnlyList<BusinessSaleLine> SaleLines { get; init; } = Array.Empty<BusinessSaleLine>();
    public IReadOnlyList<BusinessStockMovement> Movements { get; init; } = Array.Empty<BusinessStockMovement>();

    /// <summary>Todos los productos (también los dados de baja: un renglón viejo los nombra), con su categoría.</summary>
    public IReadOnlyList<Product> Products { get; init; } = Array.Empty<Product>();

    /// <summary>Todos los servicios del catálogo (activos o no).</summary>
    public IReadOnlyList<ClinicalService> Services { get; init; } = Array.Empty<ClinicalService>();
}
