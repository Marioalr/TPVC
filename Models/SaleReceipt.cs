using System;
using System.Collections.Generic;
using System.Linq;

namespace TPVC.Models;

public class SaleReceiptItem
{
    public int ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Presentation { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal => Quantity * UnitPrice;
}

public class SaleReceipt
{
    public string Folio { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Now;
    public string PatientName { get; set; } = "Público General";
    public string PaymentMethod { get; set; } = "Efectivo";
    public string Cashier { get; set; } = "Dr. Titular / Consultorio";
    public List<SaleReceiptItem> Items { get; set; } = new();

    public int TotalItems => Items.Sum(i => i.Quantity);
    public decimal TotalAmount => Items.Sum(i => i.Subtotal);
}
