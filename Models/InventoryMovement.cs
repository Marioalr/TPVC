using System;

namespace TPVC.Models;

/// <summary>
/// Tipo de movimiento del Kardex. Se guarda como entero (en la base y en la nube): los valores nuevos van siempre al final.
/// </summary>
public enum MovementType
{
    EntradaCompra,
    EntradaAjuste,
    SalidaConsulta,
    SalidaCaducidad,
    SalidaMerma,
    AjusteInventario,
    SalidaVenta,
    EntradaDevolucion,

    /// <summary>Paso 8.19 (lo escribe la PC principal en el 8.20): el saldo inicial de un lote o de las piezas sin lote.</summary>
    SaldoInicial = 8
}

public class InventoryMovement : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public int? BatchId { get; set; }
    public ProductBatch? Batch { get; set; }

    /// <summary>
    /// Paso 8.19: el renglón del ticket que dejó esta salida (o al que regresa una devolución). Viaja como
    /// <c>SaleItemSyncId</c>; con él se cancela una venta con <see cref="Sale.LedgerVersion"/> 1.
    /// </summary>
    public int? SaleItemId { get; set; }
    public SaleItem? SaleItem { get; set; }

    public MovementType MovementType { get; set; }

    /// <summary>Piezas que movió, siempre positivas (|<see cref="Delta"/>| en un movimiento nuevo).</summary>
    public int Quantity { get; set; }

    /// <summary>
    /// Paso 8.19: lo que el movimiento suma al libro de existencias, con signo (positivo entra, negativo sale). 0 marca una
    /// fila anterior: cuenta <see cref="Quantity"/> con el signo de la regla vieja de <see cref="IsEntry"/>
    /// (<see cref="Services.StockLedger.EffectiveDelta"/>). Un movimiento nuevo nunca lleva 0.
    /// </summary>
    public int Delta { get; set; }

    public int StockBefore { get; set; }
    public int StockAfter { get; set; }
    public DateTime Date { get; set; } = DateTime.Now;
    public string? Reason { get; set; }
    public string? Responsible { get; set; }

    /// <summary>
    /// Revisión 18 (panel «Negocio»): lo que costó cada unidad de inventario al moverse, con IVA
    /// (<see cref="Product.EffectiveUnitCost"/> en ese momento). Lo llevan las entradas por compra (lo que costó la entrada:
    /// el reporte de compras) y el consumo en consulta (lo que costó lo usado). null en los demás tipos, en una fila de antes o
    /// si el producto no tenía costo: el reporte usa el costo de hoy y lo marca como estimado.
    /// </summary>
    public decimal? UnitCost { get; set; }

    /// <summary>Entra al inventario: por el signo de <see cref="Delta"/> o, en una fila anterior al paso 8.19, por su tipo.</summary>
    public bool IsEntry => Delta != 0 ? Delta > 0 : IsLegacyEntry(MovementType, StockBefore, StockAfter);

    /// <summary>La regla de antes del paso 8.19: los tipos de entrada, y un ajuste que no bajó la existencia.</summary>
    public static bool IsLegacyEntry(MovementType type, int stockBefore, int stockAfter) =>
        type == MovementType.EntradaCompra ||
        type == MovementType.EntradaAjuste ||
        type == MovementType.EntradaDevolucion ||
        (type == MovementType.AjusteInventario && stockAfter >= stockBefore);

    public string MovementTypeDisplay => MovementType switch
    {
        MovementType.EntradaCompra => "Entrada por compra",
        MovementType.EntradaAjuste => "Entrada por donación/ajuste",
        MovementType.EntradaDevolucion => "Devolución por cancelación de ticket",
        MovementType.SalidaConsulta => "Consumo en consulta",
        MovementType.SalidaCaducidad => "Baja por caducidad",
        MovementType.SalidaMerma => "Baja por daño/merma",
        MovementType.AjusteInventario => "Ajuste de inventario",
        MovementType.SalidaVenta => "Venta en farmacia / POS",
        MovementType.SaldoInicial => "Saldo inicial",
        _ => MovementType.ToString()
    };
}
