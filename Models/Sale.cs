using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using TPVC.Services.Rules;

namespace TPVC.Models;

/// <summary>
/// Ticket de venta persistido (encabezado). Antes de v1.5.0 las ventas solo existían
/// como movimientos de Kardex y los servicios cobrados no quedaban registrados.
/// </summary>
public class Sale : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string Folio { get; set; } = string.Empty; // TKT-yyyyMMdd-0001

    /// <summary>
    /// Paso 8.17: el folio con letra con que se cobró sin conexión (TKT-20261009-T1-0001), después de que la PC principal le
    /// dio el número normal. El ticket reimpreso lo muestra y la búsqueda lo compara. null si nunca llevó letra.
    /// </summary>
    public string? OriginalFolio { get; set; }
    public DateTime Date { get; set; } = DateTime.Now;
    public string CustomerName { get; set; } = "Público General";
    public string PatientName { get => CustomerName; set => CustomerName = value; }
    public string PaymentMethod { get; set; } = "Efectivo";
    public string Cashier { get; set; } = string.Empty;
    public decimal Total { get; set; }

    /// <summary>
    /// Paso 8.19: cómo se cancela. 1 = la venta dejó un movimiento por lote con su renglón (<see cref="InventoryMovement.SaleItemId"/>)
    /// y cada uno se devuelve a su lote; 0 = venta anterior, que se devuelve por producto con los movimientos que dicen
    /// "Venta POS {folio}". Toda venta nueva lleva <see cref="CurrentLedgerVersion"/>.
    /// </summary>
    public int LedgerVersion { get; set; }

    public const int CurrentLedgerVersion = 1;

    public bool IsCancelled { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    public List<SaleItem> Items { get; set; } = new();

    /// <summary>
    /// Revisión 20: los productos que se cobraron sin que alcanzaran sus existencias (quedaron en negativo), para el aviso al
    /// cobrar. Solo lo llena <c>RegisterSaleAsync</c>; no se guarda ni viaja.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<StockShortage> StockShortages { get; set; } = Array.Empty<StockShortage>();
}

/// <summary>
/// Partida de un ticket: puede ser un producto de inventario o un servicio clínico.
/// Se guardan código, descripción y precio como "foto" del momento de la venta.
/// </summary>
public class SaleItem : ISyncEntity
{
    public int Id { get; set; }
    public Guid SyncId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public int SaleId { get; set; }
    public Sale? Sale { get; set; }

    public int? ProductId { get; set; }
    public int? ClinicalServiceId { get; set; }
    public bool IsService { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ProductName { get => Description; set => Description = value; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }

    /// <summary>
    /// Revisión 17: true si el renglón vendió piezas sueltas de un paquete (<see cref="Quantity"/> son piezas, no paquetes),
    /// false si vendió paquetes completos. null = renglón de antes, que al cancelarse se reconoce con la regla vieja
    /// (<see cref="Services.InventoryService.SoldIndividualUnits"/>).
    /// </summary>
    public bool? IsIndividualUnits { get; set; }

    /// <summary>
    /// Revisión 18 (panel «Negocio», pedido del 2026-10-22): lo que costó cada unidad del renglón al venderlo, con IVA
    /// (<see cref="Product.EffectiveUnitCost"/>; una pieza suelta, <see cref="Product.IndividualUnitCost"/>). null = renglón de
    /// antes, un servicio o un producto que no tenía costo al venderse: el reporte usa el costo de hoy y lo marca como estimado
    /// (<see cref="Services.Rules.BusinessReportRules.LineCost"/>).
    /// </summary>
    public decimal? UnitCost { get; set; }
}

/// <summary>
/// Línea solicitada al registrar una venta. Un ProductId &lt;= 0 representa un servicio
/// clínico (convención del carrito del POS: -Id del servicio).
/// Cuando <paramref name="IsIndividualUnits"/> es true, <paramref name="Quantity"/> indica piezas individuales
/// de un paquete multipieza consumidas en un servicio clínico.
/// </summary>
public record SaleLineRequest(int ProductId, int Quantity, decimal UnitPrice, string? Code = null, string? Description = null, bool IsIndividualUnits = false);
