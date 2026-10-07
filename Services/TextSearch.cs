using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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
}
