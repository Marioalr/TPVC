using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Un producto que se cobró sin alcanzarle las existencias (revisión 20): cuántas había (<see cref="Available"/>, puede ser
/// negativo) y cuántas se cobraron (<see cref="Charged"/>), en paquetes o, con <see cref="Pieces"/>, en piezas sueltas.
/// </summary>
public sealed record StockShortage(string ProductName, int Available, int Charged, bool Pieces);

/// <summary>
/// La política de negativos (decisión del médico del 2026-10-22, la del paso 8.20 adelantada): <b>un inventario mal contado
/// nunca impide cobrar</b>. La venta, «Se surte aquí», los insumos de la consulta y de un servicio y el consumo en consulta
/// pasan siempre, aunque las existencias queden en negativo. Al cobrar se avisa sin bloquear
/// (<see cref="WarningText"/>) y, mientras el producto (o uno de sus lotes abiertos) siga en negativo, lleva la insignia
/// "−3 · revisar inventario" (<see cref="ReviewBadge"/>) y un aviso en las alertas del sistema (<see cref="InventoryReview"/>)
/// hasta que un conteo o una entrada lo corrijan. Nada se compensa solo. Sin WPF ni base: vectores en
/// <c>contracts/golden/alerts.json</c> y <c>checkout.json</c>.
/// </summary>
public static class StockReviewRules
{
    /// <summary>Lo que dice la insignia y la alerta de un producto en negativo.</summary>
    public const string ReviewText = "revisar inventario";

    /// <summary>Un número de existencias como se muestra: los negativos con el signo menos tipográfico (U+2212), "−3".</summary>
    public static string Number(int value) =>
        value < 0 ? "−" + (-(long)value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>La insignia de un producto en negativo: "−3 · revisar inventario".</summary>
    public static string ReviewBadge(int stock) => $"{Number(stock)} · {ReviewText}";

    /// <summary>Un lote cuenta para la revisión si está abierto (no cerrado, <see cref="ProductBatch.IsClosed"/>) y en negativo.</summary>
    public static bool IsNegativeBatch(ProductBatch batch) => !batch.IsClosed && batch.Quantity < 0;

    /// <summary>
    /// Un producto activo necesita revisar su inventario si sus existencias están en negativo o si uno de sus lotes abiertos lo
    /// está, aunque el total sea positivo (<paramref name="batches"/>: sus lotes; si es null, los que trae el producto).
    /// </summary>
    public static bool NeedsReview(Product product, IEnumerable<ProductBatch>? batches = null) =>
        product.IsActive && (product.CurrentStock < 0 || (batches ?? product.Batches).Any(IsNegativeBatch));

    /// <summary>
    /// Los productos que necesitan revisar su inventario (<see cref="NeedsReview"/>), del de menos existencias al de más y,
    /// con las mismas, por nombre (ordinal). Los lotes se juntan con su producto por <see cref="ProductBatch.ProductId"/>.
    /// </summary>
    public static List<Product> InventoryReview(IEnumerable<Product> products, IEnumerable<ProductBatch> batches)
    {
        var negativeLots = batches.Where(IsNegativeBatch).Select(b => b.ProductId).ToHashSet();
        return products
            .Where(p => p.IsActive && (p.CurrentStock < 0 || negativeLots.Contains(p.Id)))
            .OrderBy(p => p.CurrentStock)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Los lotes abiertos en negativo de un producto como se nombran en la alerta: "lote L-01 −2"; vacío si no hay.
    /// </summary>
    public static string NegativeBatchesText(IEnumerable<ProductBatch> batches) =>
        string.Join(", ", batches.Where(IsNegativeBatch).OrderBy(b => b.BatchNumber, StringComparer.Ordinal)
            .Select(b => $"lote {b.BatchNumber} {Number(b.Quantity)}"));

    /// <summary>
    /// El renglón de la alerta del sistema: "Paracetamol 500 mg: −3 · revisar inventario", y con un lote en negativo
    /// "Gasa: 4 · revisar inventario (lote L-01 −2)".
    /// </summary>
    public static string AlertLine(Product product, IEnumerable<ProductBatch> batches)
    {
        var lots = NegativeBatchesText(batches.Where(b => b.ProductId == product.Id));
        return $"{product.Name}: {ReviewBadge(product.CurrentStock)}{(lots.Length > 0 ? $" ({lots})" : string.Empty)}";
    }

    /// <summary>
    /// Cuántos paquetes cerrados hacen falta para <paramref name="wholePackages"/> paquetes completos más
    /// <paramref name="individualUnits"/> piezas sueltas, tomando primero las del paquete abierto
    /// (<see cref="Product.OpenedUnitsRemaining"/>, revisión 17).
    /// </summary>
    public static int PackagesNeededFor(Product product, int wholePackages, int individualUnits)
    {
        int packages = Math.Max(0, wholePackages);
        int units = Math.Max(0, individualUnits);
        if (units == 0) return packages;

        int u = product.EffectiveUnitsPerPackage;
        if (u <= 1) return packages + units;

        int opened = Math.Max(0, product.OpenedUnitsRemaining);
        if (units <= opened) return packages;

        int deficit = units - opened;
        return packages + (deficit + u - 1) / u;
    }

    /// <summary>
    /// Si cobrar <paramref name="wholePackages"/> paquetes y <paramref name="individualUnits"/> piezas sueltas de un producto
    /// lo deja sin existencias suficientes: los paquetes que hacen falta (<see cref="PackagesNeededFor"/>) pasan de
    /// <see cref="Product.CurrentStock"/>. Con piezas sueltas se cuenta en piezas (las que hay: existencias × piezas por
    /// paquete + las del paquete abierto, que puede dar negativo; las que se cobran: paquetes × piezas por paquete + piezas);
    /// si no, en paquetes. null si alcanza.
    /// </summary>
    public static StockShortage? Shortage(Product product, int wholePackages, int individualUnits)
    {
        int packages = Math.Max(0, wholePackages);
        int units = product.HasMultipleUnitsPerPackage ? Math.Max(0, individualUnits) : 0;
        if (!product.HasMultipleUnitsPerPackage) packages += Math.Max(0, individualUnits);
        if (PackagesNeededFor(product, packages, units) <= product.CurrentStock) return null;
        if (units == 0) return new StockShortage(product.Name, product.CurrentStock, packages, false);

        int u = product.EffectiveUnitsPerPackage;
        return new StockShortage(product.Name, product.CurrentStock * u + Math.Max(0, product.OpenedUnitsRemaining), packages * u + units, true);
    }

    /// <summary>
    /// El aviso al cobrar, sin bloquear: "Existencias insuficientes: Paracetamol (quedan 2, se cobran 5). Revise el
    /// inventario." Con varios, separados por "; " en su orden; en piezas sueltas "(quedan 3 pzas, se cobran 5 pzas)"; los
    /// negativos con <see cref="Number"/>. null sin faltantes.
    /// </summary>
    public static string? WarningText(IEnumerable<StockShortage> shortages)
    {
        var list = shortages.ToList();
        if (list.Count == 0) return null;
        static string Count(int n, bool pieces) => pieces ? $"{Number(n)} pzas" : Number(n);
        return "Existencias insuficientes: "
               + string.Join("; ", list.Select(s => $"{s.ProductName} (quedan {Count(s.Available, s.Pieces)}, se cobran {Count(s.Charged, s.Pieces)})"))
               + ". Revise el inventario.";
    }
}
