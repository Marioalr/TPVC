using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// El renglón de un medicamento en la receta. Desde el pedido del médico del 2026-10-21: la marca (el nombre comercial del
/// producto) va afuera con su concentración y forma farmacéutica, y el principio activo (la denominación genérica) entre
/// paréntesis; un producto genérico no lleva paréntesis. Ej.: "Tempra 500 mg tabletas (Paracetamol)" · "Paracetamol 500 mg
/// tabletas". El paréntesis se puede ocultar por medicamento al recetar (<see cref="WithActiveIngredient"/>). De v1.23.0 a
/// v1.45 el orden era el contrario, genérico afuera y la marca entre paréntesis (<see cref="LegacyMedicineLine"/>): esos
/// renglones se quedan como se guardaron y se siguen reconociendo. La presentación va aparte (art. 30 del Reglamento de
/// Insumos para la Salud: dosis, presentación, vía, frecuencia y duración; las demás las escribe el médico en la posología).
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

    /// <summary>
    /// Las partes del renglón: <see cref="Line"/> es lo que va afuera (la marca, o el genérico en un producto genérico, con
    /// su concentración y forma) y <see cref="ActiveIngredient"/> lo que va entre paréntesis (el principio activo de un
    /// producto de marca; null si no lleva paréntesis).
    /// </summary>
    public sealed record MedicineLineParts(string Line, string? ActiveIngredient)
    {
        /// <summary>El renglón con el paréntesis o sin él.</summary>
        public string Compose(bool showActiveIngredient) =>
            showActiveIngredient && !string.IsNullOrEmpty(ActiveIngredient) ? $"{Line} ({ActiveIngredient})" : Line;
    }

    /// <summary>
    /// El renglón del medicamento: la marca, su concentración y forma, y el principio activo entre paréntesis (si
    /// <paramref name="showActiveIngredient"/>); un genérico sin paréntesis; el nombre tal cual si el producto no tiene genérico.
    /// </summary>
    public static string MedicineLine(Product product, bool showActiveIngredient = true) => Parts(product).Compose(showActiveIngredient);

    /// <summary>
    /// Las partes del renglón (<see cref="MedicineLineParts"/>). La concentración y la forma se buscan como siempre: la
    /// concentración en el nombre o en la presentación (y en el genérico, si solo él la trae), la forma en la presentación o en
    /// el nombre. Nada se repite: la concentración que traiga el genérico sale del paréntesis y va afuera, y la forma no se
    /// agrega si el genérico ya la dice. La marca, con <see cref="BrandOf"/> (un nombre en mayúsculas se escribe "Valclan").
    /// </summary>
    public static MedicineLineParts Parts(Product product)
    {
        var name = (product.Name ?? string.Empty).Trim();
        var generic = (product.GenericName ?? string.Empty).Trim();
        if (generic.Length == 0) return new MedicineLineParts(name, null);

        var brand = BrandOf(name, generic);
        if (brand == null) return new MedicineLineParts(GenericLine(product, name, generic), null);

        var strength = Strength(name) ?? Strength(product.Presentation) ?? Strength(generic);
        var ingredient = WithoutStrength(generic);
        var parts = new System.Collections.Generic.List<string> { brand };
        if (strength != null) parts.Add(strength);
        if ((Form(product.Presentation) ?? Form(name)) is { } form
            && !TextSearch.Normalize(ingredient).Contains(TextSearch.Normalize(form), StringComparison.Ordinal))
        {
            parts.Add(form);
        }
        return new MedicineLineParts(string.Join(" ", parts), ingredient);
    }

    /// <summary>
    /// El renglón como lo escribían v1.23.0 a v1.45: el genérico con su concentración y forma, y la marca entre paréntesis.
    /// Solo para reconocer los renglones que se guardaron así (<see cref="Rules.CatalogMatchingRules.ProductMatchesName"/>).
    /// </summary>
    public static string LegacyMedicineLine(Product product)
    {
        var name = (product.Name ?? string.Empty).Trim();
        var generic = (product.GenericName ?? string.Empty).Trim();
        if (generic.Length == 0) return name;
        var line = GenericLine(product, name, generic);
        return LegacyBrandOf(name, generic) is { } brand ? $"{line} ({brand})" : line;
    }

    /// <summary>
    /// El renglón guardado <paramref name="medicineName"/> con el principio activo entre paréntesis al final, o sin él: quita
    /// el último paréntesis si es "(principio activo)" y lo vuelve a poner si <paramref name="show"/>, salvo que el renglón
    /// ya diga "(principio activo)" en otro lugar (sin distinguir acentos ni mayúsculas): nunca lo repite. Lo demás del texto
    /// no cambia.
    /// </summary>
    public static string WithActiveIngredient(string? medicineName, string? activeIngredient, bool show)
    {
        var line = (medicineName ?? string.Empty).Trim();
        var ingredient = (activeIngredient ?? string.Empty).Trim();
        if (ingredient.Length == 0) return line;
        // Ya lo dice (con cualquier escritura): mostrarlo no cambia nada
        if (show && TextSearch.Normalize(line).Contains(TextSearch.Normalize($"({ingredient})"), StringComparison.Ordinal)) return line;
        if (EndsWithActiveIngredient(line, ingredient)) line = line[..line.LastIndexOf('(')].TrimEnd();
        return show ? $"{line} ({ingredient})" : line;
    }

    /// <summary>El renglón termina con "(principio activo)", sin distinguir acentos ni mayúsculas (la casilla «Mostrar (…)»).</summary>
    public static bool EndsWithActiveIngredient(string? medicineName, string? activeIngredient)
    {
        var line = (medicineName ?? string.Empty).Trim();
        var ingredient = (activeIngredient ?? string.Empty).Trim();
        var open = line.LastIndexOf('(');
        return ingredient.Length > 0 && open >= 0 && TextSearch.Normalize(line[open..]) == TextSearch.Normalize($"({ingredient})");
    }

    // El genérico, su concentración (si el genérico no trae dígitos) y su forma (si el genérico no la dice)
    private static string GenericLine(Product product, string name, string generic)
    {
        var parts = new System.Collections.Generic.List<string> { generic };
        // El genérico puede traer ya su concentración ("Paracetamol 500 mg"): no se repite
        if (!generic.Any(char.IsDigit) && (Strength(name) ?? Strength(product.Presentation)) is { } strength) parts.Add(strength);
        if ((Form(product.Presentation) ?? Form(name)) is { } form)
        {
            if (!TextSearch.Normalize(generic).Contains(TextSearch.Normalize(form), StringComparison.Ordinal)) parts.Add(form);
        }
        return string.Join(" ", parts);
    }

    // El genérico sin su concentración ("Amoxicilina/ácido clavulánico 875/125 mg" → "Amoxicilina/ácido clavulánico"): la
    // concentración va afuera. Si no queda nada, el genérico tal cual.
    private static string WithoutStrength(string generic)
    {
        var stripped = Regex.Replace(StrengthRegex.Replace(generic, " ", 1), @"\s+", " ").Trim().TrimEnd(',', '-', '/').Trim();
        return stripped.Length > 0 ? stripped : generic;
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
    /// La marca: las palabras del nombre antes de la concentración, sin lo que va entre paréntesis ("TEMPRA (PARACETAMOL)
    /// 500MG" → "Tempra": el principio activo va una sola vez, en el paréntesis que controla «Mostrar (…)»), si no empiezan como
    /// el genérico (un nombre como "Paracetamol 500 mg" no es de marca). "VALCLAN 875/125MG C/14 TABS" → "Valclan".
    /// </summary>
    public static string? BrandOf(string? name, string? generic) =>
        BrandWords(string.IsNullOrWhiteSpace(name) ? name : ParenthesesRegex.Replace(name, " "), generic);

    // Lo que va entre paréntesis en un nombre ("(PARACETAMOL)", "(Ácido fólico)")
    private static readonly Regex ParenthesesRegex = new(@"\([^()]*\)", RegexOptions.CultureInvariant);

    // La marca como la sacaban v1.23 a v1.45 (con lo que hubiera entre paréntesis): solo para reconocer sus renglones
    private static string? LegacyBrandOf(string? name, string? generic) => BrandWords(name, generic);

    private static string? BrandWords(string? name, string? generic)
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
