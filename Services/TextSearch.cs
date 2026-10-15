using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Búsqueda tolerante a mayúsculas y acentos: "acido" encuentra "Ácido", "perez" encuentra "Pérez".
/// SQLite (lower/LIKE) solo pliega ASCII, por eso el filtrado de texto se hace en memoria
/// sobre catálogos pequeños.
/// </summary>
public static class TextSearch
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// true si alguno de los campos contiene la consulta ya normalizada con <see cref="Normalize"/>.
    /// </summary>
    public static bool Matches(string normalizedQuery, params string?[] fields)
    {
        if (normalizedQuery.Length == 0) return true;
        return fields.Any(f => !string.IsNullOrEmpty(f) && Normalize(f).Contains(normalizedQuery, StringComparison.Ordinal));
    }

    /// <summary>
    /// Índice de la mejor opción para "escribir y saltar" en una lista: primero las que empiezan
    /// con lo escrito, luego las que tienen una palabra que empieza así y al final las que lo
    /// contienen. -1 si ninguna coincide.
    /// </summary>
    public static int FindBestMatchIndex(IEnumerable<string?> options, string? typed)
    {
        var q = Normalize(typed);
        if (q.Length == 0) return -1;

        int wordMatch = -1, containsMatch = -1, i = 0;
        foreach (var option in options)
        {
            var text = Normalize(option);
            if (text.StartsWith(q, StringComparison.Ordinal)) return i;
            if (wordMatch < 0 && (" " + text).Contains(" " + q, StringComparison.Ordinal)) wordMatch = i;
            if (containsMatch < 0 && text.Contains(q, StringComparison.Ordinal)) containsMatch = i;
            i++;
        }
        return wordMatch >= 0 ? wordMatch : containsMatch;
    }

    /// <summary>
    /// Productos que coinciden (sin acentos): el código exacto primero (lector de código de barras),
    /// luego el nombre que empieza así, el genérico que empieza así, una palabra que empieza así y
    /// al final lo que lo contiene en nombre, genérico, código o ubicación. Dentro de cada grupo,
    /// los que tienen existencias primero. La usan el Punto de venta (<c>PosViewModel.RankProducts</c>) y la
    /// búsqueda de insumos de un servicio en la consulta.
    /// </summary>
    public static List<Product> RankProducts(IEnumerable<Product> products, string? query, int limit = 8)
    {
        var q = Normalize(query);
        if (q.Length == 0) return new List<Product>();

        return products
            .Select(p =>
            {
                var name = Normalize(p.Name);
                var generic = Normalize(p.GenericName);
                var code = Normalize(p.Code);
                int rank;
                if (code == q) rank = 0;
                else if (name.StartsWith(q, StringComparison.Ordinal)) rank = 1;
                else if (generic.StartsWith(q, StringComparison.Ordinal)) rank = 2;
                else if ((" " + name).Contains(" " + q, StringComparison.Ordinal) || (" " + generic).Contains(" " + q, StringComparison.Ordinal)) rank = 3;
                else if (Matches(q, p.Name, p.GenericName, p.Code, p.Location)) rank = 4;
                else rank = -1;
                return (Product: p, Rank: rank);
            })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Product.CurrentStock <= 0)
            .ThenBy(x => x.Product.Name, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(x => x.Product)
            .ToList();
    }
}
