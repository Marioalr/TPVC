using System;
using System.Collections.Generic;

namespace TPVC.Models;

/// <summary>
/// Resumen del corte de caja diario. Agrupa ingresos por método de pago,
/// desglose de servicios clínicos vs productos de farmacia, y detalle de tickets.
/// </summary>
public class DailyCashCut
{
    public DateTime Date { get; set; } = DateTime.Today;

    public int TotalTicketsCount { get; set; }
    public int ActiveTicketsCount { get; set; }
    public int CancelledTicketsCount { get; set; }

    public decimal TotalSalesAmount { get; set; }
    public decimal TotalCancelledAmount { get; set; }

    // Ingresos netos por método de pago (solo tickets activos / no cancelados)
    public decimal TotalCash { get; set; }
    public decimal TotalDebitCard { get; set; }
    public decimal TotalCreditCard { get; set; }
    public decimal TotalTransfer { get; set; }
    public decimal TotalOtherPaymentMethods { get; set; }

    // Desglose de ingresos: Servicios clínicos vs Productos de inventario
    public decimal TotalProductsAmount { get; set; }
    public decimal TotalServicesAmount { get; set; }
    public int TotalProductsUnitsSold { get; set; }
    public int TotalServicesCount { get; set; }

    public List<Sale> Sales { get; set; } = new();
}
