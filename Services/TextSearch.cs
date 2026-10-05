using System;
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
}
