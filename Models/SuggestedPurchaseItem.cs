using System;

namespace TPVC.Models;

public class SuggestedPurchaseItem
{
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string GenericName { get; set; } = string.Empty;
    public string Presentation { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;

    public int CurrentStock { get; set; }
    public int MinStock { get; set; }
    public int SuggestedQuantity { get; set; }

    public decimal EstimatedUnitCost { get; set; }
    public decimal EstimatedTotalCost => SuggestedQuantity * EstimatedUnitCost;

    public string Reason { get; set; } = "Stock bajo el mínimo"; // "Agotado", "Stock bajo el mínimo", "Lotes próximos a caducar"
}
