using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Orden de los medicamentos al recetar (pedido del consultorio del 2026-10-14): las sugerencias de la hoja de receta y
/// el catálogo de medicamentos ponen primero lo que hay en el consultorio. Antes se tomaban los primeros 20 que
/// coincidían en el orden del catálogo, y un medicamento con existencias podía quedar fuera de la lista.
/// <list type="number">
/// <item><b>Con existencias primero</b> (<see cref="IsInStock"/>: activo y con piezas).</item>
/// <item><b>Qué tan bien coincide</b> (<see cref="MatchTier"/>), como <c>PosViewModel.RankProducts</c>: el código igual a lo
/// escrito, el nombre que empieza así, el genérico que empieza así, una palabra del nombre o del genérico que empieza así y
/// al final lo que lo contiene en cualquier parte. Sin texto, todos empatan.</item>
/// <item><b>Desempate estable</b>: el nombre sin acentos ni mayúsculas (<see cref="TextSearch.Normalize"/>) en orden ordinal,
/// luego el nombre tal cual y el código (ordinal), y al final el orden en que llegaron.</item>
/// </list>
/// Solo ordena y recorta: quien llama elige los candidatos (los medicamentos del catálogo,
/// <c>CatalogStore.IsProductMedication</c>). Sin base ni WPF, para que la app de Kotlin la copie; sus vectores
/// compartidos llegan con el paso 8.26.
/// </summary>
public static class MedicineSearchRules
{
    /// <summary>Cuántas sugerencias muestra la hoja de receta.</summary>
    public const int SuggestionLimit = 20;

    /// <summary>Lo que no coincide con lo escrito.</summary>
    public const int NoMatch = -1;

    /// <summary>Hay en el consultorio: el producto sigue activo y tiene piezas.</summary>
    public static bool IsInStock(Product product) => product.IsActive && product.CurrentStock > 0;

    /// <summary>
    /// Qué tan bien coincide el producto con lo escrito, ya normalizado con <see cref="TextSearch.Normalize"/> (0 es lo
    /// mejor): 0 el código es igual, 1 el nombre empieza así, 2 el genérico empieza así, 3 una palabra del nombre o del
    /// genérico empieza así, 4 el nombre, el genérico o el código (y la presentación, si se pide) lo contienen;
    /// <see cref="NoMatch"/> si no coincide. Sin texto: 0.
    /// </summary>
    public static int MatchTier(Product product, string normalizedQuery, bool includePresentation = false)
    {
        var q = normalizedQuery;
        if (q.Length == 0) return 0;

        var name = TextSearch.Normalize(product.Name);
        var generic = TextSearch.Normalize(product.GenericName);
        var code = TextSearch.Normalize(product.Code);
        if (code.Length > 0 && code == q) return 0;
        if (name.StartsWith(q, StringComparison.Ordinal)) return 1;
        if (generic.StartsWith(q, StringComparison.Ordinal)) return 2;
        if ((" " + name).Contains(" " + q, StringComparison.Ordinal) || (" " + generic).Contains(" " + q, StringComparison.Ordinal)) return 3;
        if (name.Contains(q, StringComparison.Ordinal) || generic.Contains(q, StringComparison.Ordinal) || code.Contains(q, StringComparison.Ordinal)) return 4;
        if (includePresentation && TextSearch.Normalize(product.Presentation).Contains(q, StringComparison.Ordinal)) return 4;
        return NoMatch;
    }

    /// <summary>
    /// Los candidatos que coinciden con <paramref name="query"/>, primero los que hay en existencia, luego por qué tan bien
    /// coinciden y con el desempate estable; como mucho <paramref name="limit"/>. Sin texto, todos los candidatos.
    /// </summary>
    public static List<Product> Rank(IEnumerable<Product> candidates, string? query, int limit = SuggestionLimit, bool includePresentation = false)
    {
        var q = TextSearch.Normalize(query);
        return candidates
            .Select((product, index) => (Product: product, Tier: MatchTier(product, q, includePresentation), Index: index))
            .Where(x => x.Tier != NoMatch)
            .OrderBy(x => IsInStock(x.Product) ? 0 : 1)
            .ThenBy(x => x.Tier)
            .ThenBy(x => TextSearch.Normalize(x.Product.Name), StringComparer.Ordinal)
            .ThenBy(x => x.Product.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Product.Code, StringComparer.Ordinal)
            .ThenBy(x => x.Index)
            .Take(Math.Max(0, limit))
            .Select(x => x.Product)
            .ToList();
    }
}
