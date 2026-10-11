using System;

namespace TPVC.Models;

public class SuggestedPurchaseItem
{
    private static readonly System.Globalization.CultureInfo Mexican = System.Globalization.CultureInfo.GetCultureInfo("es-MX");

    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string GenericName { get; set; } = string.Empty;
    public string Presentation { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;

    public int CurrentStock { get; set; }
    public int MinStock { get; set; }
    public int SuggestedQuantity { get; set; }

    /// <summary>El costo del producto (con IVA si no lo incluía) o, sin costo capturado, una estimación.</summary>
    public decimal EstimatedUnitCost { get; set; }
    public decimal EstimatedTotalCost => SuggestedQuantity * EstimatedUnitCost;

    /// <summary>El producto no tiene costo: <see cref="EstimatedUnitCost"/> sale del precio de venta.</summary>
    public bool IsCostEstimated { get; set; }

    /// <summary>"$6.50", o "≈ $7.80" si es estimado.</summary>
    public string UnitCostText => (IsCostEstimated ? "≈ " : string.Empty) + EstimatedUnitCost.ToString("C2", Mexican);

    public string Reason { get; set; } = "Stock bajo el mínimo"; // "Agotado", "Stock bajo el mínimo", "Lotes próximos a caducar"
}
