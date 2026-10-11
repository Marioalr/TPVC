using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// El renglón de un medicamento en la receta, en el orden que pide el Reglamento de Insumos para la Salud: primero la
/// denominación genérica (el principio activo, obligatoria, art. 31) con su concentración y forma farmacéutica, y la
/// denominación distintiva (la marca) entre paréntesis solo si el producto es de marca. La presentación va aparte
/// (art. 30: dosis, presentación, vía, frecuencia y duración; las demás las escribe el médico en la posología).
/// Ej.: "Paracetamol 500 mg tabletas (Tempra)" · "Caja con 20 tabletas".
/// </summary>
public static class PrescriptionNaming
{
    private static readonly TextInfo SpanishText = CultureInfo.GetCultureInfo("es-MX").TextInfo;

    // Concentración: números (con / entre varios) y una unidad de masa, UI o %; opcionalmente "por" un volumen.
    // "500 mg", "875/125MG", "600MG/100 ML", "7/1.5MG/ML", "2G/100 ML", "0.9%". Un volumen solo (360ML) no es concentración.
    private static readonly Regex StrengthRegex = new(
        @"(?<![\w/.,])(?<num>\d+(?:[.,]\d+)?(?:\s*/\s*\d+(?:[.,]\d+)?)*)\s*(?<unit>mcg|µg|ug|mg|g|ui|%)(?:\s*/\s*(?<per>\d+(?:[.,]\d+)?\s*)?(?<perUnit>ml|g))?(?![\w])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Forma farmacéutica: lo que se busca en la presentación y en el nombre, y cómo se escribe en la receta
    private static readonly (string Pattern, string Form)[] Forms =
    {
        (@"\b(tabletas?|tabs?)\b", "tabletas"),
        (@"\b(c[aá]psulas?|caps?)\b", "cápsulas"),
        (@"\bgrageas?\b", "grageas"),
        (@"\bjarabe\b", "jarabe"),
        (@"\bsuspensi[oó]n\b", "suspensión"),
        (@"\b(soluci[oó]n\s+inyectable|inyectables?|iny|ampolletas?|ampulas?|amp)\b", "solución inyectable"),
        (@"\bgotas\b", "gotas"),
        (@"\bsoluci[oó]n\b", "solución"),
        (@"\bcrema\b", "crema"),
        (@"\b(ung[uü]ento|pomada)\b", "ungüento"),
        (@"\bgel\b", "gel"),
        (@"\b[oó]vulos?\b", "óvulos"),
        (@"\bsupositorios?\b", "supositorios"),
        (@"\bsobres?\b", "sobres"),
        (@"\bparches?\b", "parches"),
        (@"\b(aerosol|spray|inhalador)\b", "aerosol"),
    };

    /// <summary>El renglón del medicamento en el orden normado; el nombre tal cual si el producto no tiene genérico.</summary>
    public static string MedicineLine(Product product)
    {
        var name = (product.Name ?? string.Empty).Trim();
        var generic = (product.GenericName ?? string.Empty).Trim();
        if (generic.Length == 0) return name;

        var parts = new System.Collections.Generic.List<string> { generic };
        // El genérico puede traer ya su concentración ("Paracetamol 500 mg"): no se repite
        if (!generic.Any(char.IsDigit) && (Strength(name) ?? Strength(product.Presentation)) is { } strength) parts.Add(strength);
        if ((Form(product.Presentation) ?? Form(name)) is { } form)
        {
            if (!TextSearch.Normalize(generic).Contains(TextSearch.Normalize(form), StringComparison.Ordinal)) parts.Add(form);
        }

        var line = string.Join(" ", parts);
        return BrandOf(name, generic) is { } brand ? $"{line} ({brand})" : line;
    }

    /// <summary>La presentación que se surte ("Caja con 20 tabletas"); vacía si el producto no la tiene.</summary>
    public static string Presentation(Product product) => (product.Presentation ?? string.Empty).Trim();

    /// <summary>"500 mg", "875/125 mg", "600 mg/100 ml", "0.9 %"; null si el texto no trae una concentración.</summary>
    public static string? Strength(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = StrengthRegex.Match(text);
        if (!m.Success) return null;

        var number = Regex.Replace(m.Groups["num"].Value, @"\s*/\s*", "/");
        var unit = m.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "ui" => "UI",
            "ug" or "µg" => "mcg",
            var u => u
        };
        var strength = unit == "%" ? $"{number} %" : $"{number} {unit}";
        if (m.Groups["perUnit"].Success)
        {
            var per = m.Groups["per"].Value.Trim();
            strength += "/" + (per.Length > 0 ? per + " " : string.Empty) + m.Groups["perUnit"].Value.ToLowerInvariant();
        }
        return strength;
    }

    /// <summary>La forma farmacéutica que dice el texto ("tabletas", "jarabe"…); null si no la dice.</summary>
    public static string? Form(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var (pattern, form) in Forms)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return form;
        }
        return null;
    }

    /// <summary>
    /// La marca: las palabras del nombre antes de la concentración, si no empiezan como el genérico (un nombre como
    /// "Paracetamol 500 mg" no es de marca). "VALCLAN 875/125MG C/14 TABS" → "Valclan".
    /// </summary>
    public static string? BrandOf(string? name, string? generic)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var brandWords = words.TakeWhile(w => !w.Any(char.IsDigit) && !w.StartsWith("C/", StringComparison.OrdinalIgnoreCase)).ToList();
        // Las palabras que solo dicen la forma ("TABS", "JARABE") no son parte de la marca
        while (brandWords.Count > 0 && Form(brandWords[^1]) != null) brandWords.RemoveAt(brandWords.Count - 1);
        if (brandWords.Count == 0) return null;

        var firstGeneric = FirstWord(generic);
        if (firstGeneric.Length > 0 && FirstWord(brandWords[0]) == firstGeneric) return null;

        var brand = string.Join(" ", brandWords);
        // En mayúsculas como en el correo del proveedor: "Valclan", no "VALCLAN"
        return brand == brand.ToUpperInvariant() ? SpanishText.ToTitleCase(brand.ToLowerInvariant()) : brand;
    }

    private static string FirstWord(string? text) =>
        TextSearch.Normalize(text).Split(new[] { ' ', '/', '-', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
}
