using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// El valor del inventario del panel (paso 8.26; antes la suma vivía en <c>InventoryService.GetDashboardMetricsAsync</c>, sin
/// cambios): lo que costó lo que hay en existencia. El costo de cada producto es <see cref="Product.EffectiveUnitCost"/> (el
/// capturado, con el 16 % de IVA sumado y redondeado a centavos hacia arriba si no lo incluía). Sin WPF ni base: vectores en
/// <c>contracts/golden/inventory-value.json</c>.
/// </summary>
public static class InventoryValueRules
{
    /// <summary>
    /// El valor de los productos activos con existencias (existencias × costo con IVA) y cuántos de ellos no tienen costo
    /// (no suman nada).
    /// </summary>
    public static (decimal Value, int StockedWithoutCost) Summarize(IEnumerable<Product> products)
    {
        var stocked = products.Where(p => p.IsActive && p.CurrentStock > 0).ToList();
        return (stocked.Sum(p => p.InventoryValue), stocked.Count(p => !p.HasCost));
    }

    /// <summary>"al costo", o "N sin costo" si algún producto con existencias no tiene costo todavía.</summary>
    public static string Detail(int stockedWithoutCost) => stockedWithoutCost == 0 ? "al costo" : $"{stockedWithoutCost} sin costo";
}
