using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Las búsquedas del catálogo tal como las arma cada pantalla (paso 8.26; antes dentro de <c>PosViewModel</c>,
/// <c>ClinicalServicesSheetViewModel</c>, <c>SuppliesSheetViewModel</c>, <c>PrescriptionSheetViewModel</c>,
/// <c>CatalogBrowserViewModel</c> y <c>ConsultationViewModel</c>, sin cambios): qué candidatos entran y con qué regla se
/// ordenan (<see cref="TextSearch.RankProducts"/> o <see cref="MedicineSearchRules.Rank"/>). Sin WPF ni base: vectores en
/// <c>contracts/golden/rank-products.json</c>, <c>medicine-search.json</c> y <c>cie10-search.json</c>.
/// </summary>
public static class CatalogSearchRules
{
    private static readonly Regex QuantityPrefix = new(@"^\s*(\d{1,3})\s*\*\s*(.*)$", RegexOptions.Compiled);

    /// <summary>Cuántas sugerencias muestra el buscador del Punto de venta y el de los insumos de un servicio.</summary>
    public const int ProductSuggestionLimit = 8;

    /// <summary>Cuántas sugerencias muestran la hoja de insumos y el buscador del CIE-10 de la consulta.</summary>
    public const int SheetSuggestionLimit = 20;

    /// <summary>Lo mínimo que hay que escribir para que el CIE-10 sugiera algo.</summary>
    public const int Cie10MinLength = 2;

    /// <summary>"3*para" → (3, "para"); sin asterisco la cantidad es 1 y el texto queda tal cual; "0*para" → (1, "para").</summary>
    public static (int Quantity, string Query) ParseQuantityPrefix(string? text)
    {
        var match = QuantityPrefix.Match(text ?? string.Empty);
        if (!match.Success) return (1, text ?? string.Empty);
        int quantity = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return (Math.Max(1, quantity), match.Groups[2].Value);
    }

    /// <summary>
    /// El catálogo del Punto de venta: los productos de la categoría elegida (Id mayor que 0; si no, todos), por nombre con la
    /// cultura actual sin texto, o con <see cref="TextSearch.RankProducts"/> sin límite.
    /// </summary>
    public static List<Product> PosCatalog(IEnumerable<Product> products, int? categoryId, string? query)
    {
        var list = products;
        if (categoryId is > 0) list = list.Where(p => p.CategoryId == categoryId.Value);
        return string.IsNullOrWhiteSpace(query)
            ? list.OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList()
            : TextSearch.RankProducts(list, query, limit: int.MaxValue);
    }

    /// <summary>Insumos de un servicio en la consulta: solo insumos activos, con <see cref="TextSearch.RankProducts"/> (8 como mucho).</summary>
    public static List<Product> ServiceSupplySuggestions(IEnumerable<Product> products, string? query) =>
        TextSearch.RankProducts(products.Where(p => p.IsActive && CatalogMatchingRules.IsProductSupply(p)), query);

    /// <summary>
    /// La hoja de insumos: los insumos en el orden del catálogo que contienen lo escrito (sin acentos) en nombre, genérico o
    /// código, 20 como mucho; sin texto, los primeros 20.
    /// </summary>
    public static List<Product> SupplySuggestions(IEnumerable<Product> products, string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? products.Where(CatalogMatchingRules.IsProductSupply).Take(SheetSuggestionLimit).ToList()
            : products
                .Where(CatalogMatchingRules.IsProductSupply)
                .Where(p => TextSearch.Matches(TextSearch.Normalize(query), p.Name, p.GenericName, p.Code))
                .Take(SheetSuggestionLimit)
                .ToList();

    /// <summary>Las sugerencias de la hoja de receta: los medicamentos con <see cref="MedicineSearchRules.Rank"/> (20 como mucho).</summary>
    public static List<Product> PrescriptionSuggestions(IEnumerable<Product> products, string? query) =>
        MedicineSearchRules.Rank(products.Where(CatalogMatchingRules.IsProductMedication), query);

    /// <summary>El catálogo de medicamentos de la consulta: todos los que coinciden, también por presentación.</summary>
    public static List<Product> BrowserMedications(IEnumerable<Product> products, string? query) =>
        MedicineSearchRules.Rank(products.Where(CatalogMatchingRules.IsProductMedication), TextSearch.Normalize(query ?? string.Empty),
            limit: int.MaxValue, includePresentation: true);

    /// <summary>El catálogo de insumos de la consulta: los que contienen lo escrito en nombre, genérico, código o presentación, por nombre.</summary>
    public static List<Product> BrowserSupplies(IEnumerable<Product> products, string? query)
    {
        var q = TextSearch.Normalize(query ?? string.Empty);
        var list = products.Where(CatalogMatchingRules.IsProductSupply);
        if (!string.IsNullOrWhiteSpace(q))
        {
            list = list.Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Presentation));
        }
        return list.OrderBy(p => p.Name).ToList();
    }

    /// <summary>
    /// El buscador del diagnóstico de la consulta: con menos de 2 letras (sin contar los espacios de los lados) nada; si no,
    /// <see cref="Cie10Diagnosis.Search"/> con 20 como mucho.
    /// </summary>
    public static List<Cie10Diagnosis> Cie10Suggestions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < Cie10MinLength) return new List<Cie10Diagnosis>();
        return Cie10Diagnosis.Search(text.Trim(), SheetSuggestionLimit).ToList();
    }
}
