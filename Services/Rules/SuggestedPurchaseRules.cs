using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// La lista de compras sugerida (paso 8.11; antes dentro de InventoryService): qué pedir de cada producto con stock bajo
/// y cuánto costaría. Sin base ni WPF: de aquí salen los vectores <c>contracts/golden/suggested-purchase.json</c>.
/// </summary>
public static class SuggestedPurchaseRules
{
    /// <summary>Sin costo capturado, el costo se estima como esta parte del precio de venta.</summary>
    public const decimal EstimatedCostShareOfPrice = 0.65m;

    /// <summary>Sin costo ni precio, el costo estimado por pieza.</summary>
    public const decimal FallbackUnitCost = 25.00m;

    /// <summary>Los empates de existencias se ordenan por nombre como en español de México (Á junto a A).</summary>
    public static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.GetCultureInfo("es-MX"), ignoreCase: false);

    /// <summary>
    /// Cuánto pedir: hasta el doble del mínimo y nunca menos que el mínimo (5 si el mínimo diera cero, lo que no pasa
    /// con mínimo configurado).
    /// </summary>
    public static int SuggestedQuantity(int currentStock, int minStock)
    {
        int quantity = Math.Max(minStock * 2 - currentStock, minStock);
        return quantity <= 0 ? 5 : quantity;
    }

    /// <summary>
    /// El costo de cada pieza: el capturado (con IVA si no lo incluía) o una estimación con el precio de venta, redondeada
    /// a centavos al par (.NET: 6.565 da 6.56).
    /// </summary>
    public static (decimal UnitCost, bool Estimated) UnitCost(Product product)
    {
        if (product.HasCost) return (product.EffectiveUnitCost, false);
        return (product.Price > 0 ? Math.Round(product.Price * EstimatedCostShareOfPrice, 2) : FallbackUnitCost, true);
    }

    /// <summary>La lista de los productos con stock bajo, del de menos existencias al de más y luego por nombre.</summary>
    public static List<SuggestedPurchaseItem> Build(IEnumerable<Product> products)
    {
        var list = new List<SuggestedPurchaseItem>();
        foreach (var p in products.Where(StockAlertRules.IsLowStock))
        {
            var (cost, estimated) = UnitCost(p);
            list.Add(new SuggestedPurchaseItem
            {
                ProductCode = p.Code,
                ProductName = p.Name,
                GenericName = p.GenericName ?? string.Empty,
                Presentation = p.Presentation ?? string.Empty,
                CategoryName = p.Category?.Name ?? "General",
                CurrentStock = p.CurrentStock,
                MinStock = p.MinStock,
                SuggestedQuantity = SuggestedQuantity(p.CurrentStock, p.MinStock),
                EstimatedUnitCost = cost,
                IsCostEstimated = estimated,
                Reason = p.CurrentStock == 0 ? "Agotado en consultorio" : $"Stock bajo ({p.CurrentStock} de {p.MinStock} mín.)"
            });
        }
        return list.OrderBy(s => s.CurrentStock).ThenBy(s => s.ProductName, NameOrder).ToList();
    }
}
