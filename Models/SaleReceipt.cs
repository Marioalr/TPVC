using System;
using System.Collections.Generic;
using System.Linq;

namespace TPVC.Models;

public class SaleReceiptItem
{
    public string Name { get; set; } = string.Empty;
    public string? Presentation { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal => Quantity * UnitPrice;
}

public class SaleReceipt
{
    public string Folio { get; set; } = string.Empty;

    /// <summary>Paso 8.17: el folio con letra con que se cobró sin conexión, si la PC principal ya lo regularizó.</summary>
    public string? OriginalFolio { get; set; }

    public DateTime Date { get; set; } = DateTime.Now;
    public string PatientName { get; set; } = "Público General";
    public string PaymentMethod { get; set; } = "Efectivo";
    public string Cashier { get; set; } = "Dr. Titular / Consultorio";
    public List<SaleReceiptItem> Items { get; set; } = new();

    /// <summary>Efectivo recibido y cambio (solo pagos en efectivo; no se guardan en la base).</summary>
    public decimal? CashReceived { get; set; }
    public decimal? Change { get; set; }
    public bool HasCashDetail => CashReceived.HasValue;

    /// <summary>
    /// Revisión 20: el aviso de existencias de esta venta ("Existencias insuficientes: …. Revise el inventario."), o null si
    /// todo alcanzó. No se imprime ni se guarda.
    /// </summary>
    public string? StockWarning { get; set; }

    public int TotalItems => Items.Sum(i => i.Quantity);
    public decimal TotalAmount => Items.Sum(i => i.Subtotal);
}
