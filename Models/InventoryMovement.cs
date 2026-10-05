using System;

namespace TPVC.Models;

public enum MovementType
{
    EntradaCompra,
    EntradaAjuste,
    SalidaConsulta,
    SalidaCaducidad,
    SalidaMerma,
    AjusteInventario,
    SalidaVenta,
    EntradaDevolucion
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

    public MovementType MovementType { get; set; }
    public int Quantity { get; set; }
    public int StockBefore { get; set; }
    public int StockAfter { get; set; }
    public DateTime Date { get; set; } = DateTime.Now;
    public string? Reason { get; set; }
    public string? Responsible { get; set; }

    public bool IsEntry => MovementType == MovementType.EntradaCompra || 
                           MovementType == MovementType.EntradaAjuste || 
                           MovementType == MovementType.EntradaDevolucion ||
                           (MovementType == MovementType.AjusteInventario && StockAfter >= StockBefore);

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
        _ => MovementType.ToString()
    };
}
