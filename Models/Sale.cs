using System;
using System.Collections.Generic;

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
    public DateTime Date { get; set; } = DateTime.Now;
    public string CustomerName { get; set; } = "Público General";
    public string PatientName { get => CustomerName; set => CustomerName = value; }
    public string PaymentMethod { get; set; } = "Efectivo";
    public string Cashier { get; set; } = string.Empty;
    public decimal Total { get; set; }

    public bool IsCancelled { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    public List<SaleItem> Items { get; set; } = new();
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
    public int ProductIdSafe => ProductId ?? 0;
    public int? ClinicalServiceId { get; set; }
    public bool IsService { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ProductName { get => Description; set => Description = value; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }
}

/// <summary>
/// Línea solicitada al registrar una venta. Un ProductId &lt;= 0 representa un servicio
/// clínico (convención del carrito del POS: -Id del servicio).
/// </summary>
public record SaleLineRequest(int ProductId, int Quantity, decimal UnitPrice, string? Code = null, string? Description = null);
