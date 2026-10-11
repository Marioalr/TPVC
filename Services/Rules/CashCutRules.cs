using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// El corte de caja del día a partir de sus ventas (paso 8.11; antes dentro de InventoryService): tickets cobrados y
/// cancelados, ingresos por forma de pago y servicios contra productos. Sin base ni WPF: de aquí salen los vectores
/// <c>contracts/golden/cash-cut.json</c> del corte que ve el celular.
/// </summary>
public static class CashCutRules
{
    /// <summary>Las formas de pago del corte, según el texto de la venta (sin distinguir mayúsculas ni el acento).</summary>
    public enum PaymentKind
    {
        Cash,
        DebitCard,
        CreditCard,
        Transfer,
        Other
    }

    public static PaymentKind ClassifyPayment(string? paymentMethod)
    {
        var method = (paymentMethod ?? "").Trim().ToLowerInvariant();
        if (method.Contains("efectivo")) return PaymentKind.Cash;
        if (method.Contains("débito") || method.Contains("debito")) return PaymentKind.DebitCard;
        if (method.Contains("crédito") || method.Contains("credito")) return PaymentKind.CreditCard;
        if (method.Contains("transfer")) return PaymentKind.Transfer;
        return PaymentKind.Other;
    }

    /// <summary>El corte de <paramref name="day"/> con las ventas de ese día (con sus renglones). Las canceladas solo cuentan aparte.</summary>
    public static DailyCashCut Compute(IReadOnlyList<Sale> sales, DateTime day)
    {
        var cut = new DailyCashCut
        {
            Date = day.Date,
            Sales = sales.ToList(),
            TotalTicketsCount = sales.Count,
            ActiveTicketsCount = sales.Count(s => !s.IsCancelled),
            CancelledTicketsCount = sales.Count(s => s.IsCancelled),
            TotalSalesAmount = sales.Where(s => !s.IsCancelled).Sum(s => s.Total),
            TotalCancelledAmount = sales.Where(s => s.IsCancelled).Sum(s => s.Total)
        };

        var activeSales = sales.Where(s => !s.IsCancelled).ToList();

        // Ingresos por método de pago
        foreach (var sale in activeSales)
        {
            switch (ClassifyPayment(sale.PaymentMethod))
            {
                case PaymentKind.Cash: cut.TotalCash += sale.Total; break;
                case PaymentKind.DebitCard: cut.TotalDebitCard += sale.Total; break;
                case PaymentKind.CreditCard: cut.TotalCreditCard += sale.Total; break;
                case PaymentKind.Transfer: cut.TotalTransfer += sale.Total; break;
                default: cut.TotalOtherPaymentMethods += sale.Total; break;
            }
        }

        // Desglose Servicios vs Productos
        foreach (var item in activeSales.SelectMany(s => s.Items))
        {
            if (item.IsService)
            {
                cut.TotalServicesAmount += item.Subtotal;
                cut.TotalServicesCount += item.Quantity;
            }
            else
            {
                cut.TotalProductsAmount += item.Subtotal;
                cut.TotalProductsUnitsSold += item.Quantity;
            }
        }

        return cut;
    }
}
