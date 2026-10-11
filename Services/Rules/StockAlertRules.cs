using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Las alertas del inventario (paso 8.11; antes dentro de InventoryService): productos con stock bajo y lotes vencidos o
/// por vencer. Sin base ni WPF: de aquí salen los vectores <c>contracts/golden/alerts.json</c> de las alertas del celular.
/// </summary>
public static class StockAlertRules
{
    /// <summary>Un lote "por vencer" en las alertas: vence hoy o en los siguientes 30 días.</summary>
    public const int NearExpiryDays = 30;

    /// <summary>Stock bajo: activo, con mínimo configurado y existencias en el mínimo o menos.</summary>
    public static bool IsLowStock(Product product) => product.IsActive && product.MinStock > 0 && product.CurrentStock <= product.MinStock;

    /// <summary>Los productos con stock bajo, del de menos existencias al de más.</summary>
    public static List<Product> LowStock(IEnumerable<Product> products) =>
        products.Where(IsLowStock).OrderBy(p => p.CurrentStock).ToList();

    /// <summary>Un lote cuenta para las alertas si tiene piezas, no está agotado y su producto sigue activo.</summary>
    public static bool CountsForAlerts(ProductBatch batch) =>
        batch.Quantity > 0 && !batch.IsDepleted && (batch.Product == null || batch.Product.IsActive);

    /// <summary>
    /// Los lotes vencidos (antes de <paramref name="today"/>) y los que vencen de hoy a 30 días, por fecha de caducidad.
    /// </summary>
    public static (List<ProductBatch> Expired, List<ProductBatch> NearExpiry) SplitByExpiration(IEnumerable<ProductBatch> batches, DateTime today)
    {
        var limit = today.Date.AddDays(NearExpiryDays);
        var counted = batches.Where(CountsForAlerts).OrderBy(b => b.ExpirationDate).ToList();
        return (counted.Where(b => b.ExpirationDate.Date < today.Date).ToList(),
                counted.Where(b => b.ExpirationDate.Date >= today.Date && b.ExpirationDate.Date <= limit).ToList());
    }
}
