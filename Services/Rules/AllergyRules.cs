using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// El aviso de alergia al recetar (paso 8.26; antes <c>InventoryService.CheckMedicationAllergy</c>, sin cambios). Se compara
/// el medicamento (lo escrito en la receta o el nombre del producto, más su genérico) con cada alergia declarada, sin
/// acentos ni mayúsculas: primero que uno contenga al otro y luego las familias con reactividad cruzada, en su orden. Así
/// una alergia a "Paracetamol" avisa al recetar "Tempra" si el producto lleva su genérico. Desde la revisión 20 también al
/// revés: una alergia escrita con la marca ("Tempra") avisa al recetar su genérico si el catálogo tiene un producto con esa
/// marca o ese nombre; y "Sulfato ferroso" ya no es de la familia sulfa. Sin WPF ni base: vectores en
/// <c>contracts/golden/allergy-check.json</c>.
/// </summary>
public static class AllergyRules
{
    /// <summary>
    /// Familias con reactividad cruzada común, en el orden en que se revisan: la llave y sus miembros. Una alergia es de la
    /// familia si contiene la llave o un miembro; el medicamento, igual. Los miembros con acento nunca coinciden con un texto
    /// normalizado (sin acentos), pero su versión sin acento está en la misma lista.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string[] Members)> DrugFamilies = new (string, string[])[]
    {
        ("penicilina", new[] { "penicilina", "amoxicilina", "ampicilina", "dicloxacilina", "bencilpenicilina", "clavulonato", "clavulanico", "clavulánico", "augmentin" }),
        ("betalactamico", new[] { "penicilina", "amoxicilina", "ampicilina", "cefalexina", "ceftriaxona", "cefotaxima", "cefuroxima" }),
        ("cefalosporina", new[] { "cefalexina", "ceftriaxona", "cefotaxima", "cefuroxima", "cefixima", "cefazolina" }),
        ("aines", new[] { "aspirina", "acido acetilsalicilico", "ácido acetilsalicílico", "ibuprofeno", "naproxeno", "ketorolaco", "diclofenaco", "meloxicam", "piroxicam", "indometacina", "clonixinato" }),
        ("aspirina", new[] { "aspirina", "acido acetilsalicilico", "ácido acetilsalicílico", "aas" }),
        ("ibuprofeno", new[] { "ibuprofeno", "motrin", "advil" }),
        ("sulfa", new[] { "sulfa", "sulfas", "sulfonamida", "sulfametoxazol", "trimetoprima", "bactrim", "sulfadiazina", "sulfasalazina" }),
        ("macrolido", new[] { "azitromicina", "claritromicina", "eritromicina" }),
        ("quinolona", new[] { "ciprofloxacino", "levofloxacino", "moxifloxacino" }),
    };

    /// <summary>
    /// Revisión 20: palabras completas que no cuentan para una familia (se quitan del texto antes de buscar su llave y sus
    /// miembros). El sulfato ferroso, el de magnesio o el salbutamol sulfato no son sulfonamidas: "sulfato" y "sulfatos" no
    /// son de la familia sulfa aunque empiecen con "sulfa".
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> FamilyExcludedWords = new Dictionary<string, string[]>
    {
        ["sulfa"] = new[] { "sulfato", "sulfatos" },
    };

    /// <summary>
    /// El texto normalizado sin las palabras completas que no cuentan para la familia (un espacio en su lugar). Una palabra
    /// es lo que va entre caracteres que no son letra ni dígito (a-z, 0-9 y lo demás que no sea ASCII).
    /// </summary>
    public static string WithoutExcludedWords(string normalizedText, string familyKey)
    {
        if (!FamilyExcludedWords.TryGetValue(familyKey, out var words)) return normalizedText;
        foreach (var word in words)
        {
            normalizedText = Regex.Replace(normalizedText, $"(?<![\\p{{L}}\\p{{Nd}}]){Regex.Escape(word)}(?![\\p{{L}}\\p{{Nd}}])", " ", RegexOptions.CultureInvariant);
        }
        return normalizedText;
    }

    /// <summary>Si el texto normalizado es de la familia: contiene su llave o un miembro, sin las palabras que no cuentan.</summary>
    public static bool MatchesFamily(string normalizedText, string familyKey, IEnumerable<string> members)
    {
        var text = WithoutExcludedWords(normalizedText, familyKey);
        return text.Contains(familyKey, StringComparison.Ordinal) || members.Any(m => text.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>
    /// Revisión 20: los genéricos de los productos del catálogo cuya marca (<see cref="PrescriptionNaming.BrandOf"/>) o cuyo
    /// nombre es la alergia (normalizados, iguales), sin repetir y en el orden del catálogo. Así "Tempra" da "Paracetamol".
    /// Un genérico vacío o igual a la alergia no cuenta.
    /// </summary>
    public static List<string> GenericsOfBrand(string allergy, IEnumerable<Product>? catalog)
    {
        var result = new List<string>();
        if (catalog == null) return result;
        var normAllergy = TextSearch.Normalize(allergy);
        if (normAllergy.Length == 0) return result;
        foreach (var product in catalog)
        {
            var generic = (product.GenericName ?? string.Empty).Trim();
            var normGeneric = TextSearch.Normalize(generic);
            if (normGeneric.Length == 0 || normGeneric == normAllergy) continue;
            bool sameName = TextSearch.Normalize(product.Name) == normAllergy;
            bool sameBrand = TextSearch.Normalize(PrescriptionNaming.BrandOf(product.Name, product.GenericName)) == normAllergy;
            if ((sameName || sameBrand) && !result.Any(g => TextSearch.Normalize(g) == normGeneric)) result.Add(generic);
        }
        return result;
    }

    /// <summary>
    /// Revisión 20: palabras de una sal, de la forma química o de la concentración que no dicen el principio activo; se quitan
    /// del genérico para su base ("Losartán potásico" → "losartan", "Clorhidrato de metformina" → "metformina").
    /// </summary>
    public static readonly IReadOnlyList<string> SaltWords = new[]
    {
        "potasico", "potasica", "sodico", "sodica", "calcico", "calcica", "magnesico", "magnesica", "clorhidrato", "hidrocloruro",
        "bromhidrato", "maleato", "succinato", "besilato", "mesilato", "tartrato", "citrato", "fosfato", "sulfato", "acetato",
        "fumarato", "hiclato", "valerato", "propionato", "dipropionato", "furoato", "monohidrato", "dihidrato", "trihidrato",
        "anhidro", "anhidra", "base", "sal", "de", "del",
        "mg", "g", "mcg", "ug", "ml", "ui", "u",
    };

    /// <summary>Lo menos que debe tener la base de un genérico para compararla (letras), así una base corta no avisa de más.</summary>
    public const int MinGenericBaseLetters = 4;

    /// <summary>
    /// Revisión 20: la base de un genérico, normalizada: sin las palabras de <see cref="SaltWords"/> ni las que tienen dígitos o
    /// "%" (concentraciones), con las palabras separadas por lo que no sea letra; null si le quedan menos de
    /// <see cref="MinGenericBaseLetters"/> letras o si es igual al genérico normalizado (no hay nada que quitar).
    /// </summary>
    public static string? GenericBase(string? generic)
    {
        var normalized = TextSearch.Normalize(generic);
        var words = Regex.Split(normalized, @"[^\p{L}\p{Nd}%]+")
            .Where(w => w.Length > 0 && !w.Any(char.IsDigit) && !w.Contains('%') && !SaltWords.Contains(w))
            .ToList();
        var baseText = string.Join(" ", words);
        if (baseText.Count(char.IsLetter) < MinGenericBaseLetters || baseText == normalized) return null;
        return baseText;
    }

    /// <summary>
    /// Si el texto normalizado contiene la base como palabras completas y seguidas: las palabras del texto (separadas por lo
    /// que no sea letra, dígito ni "%") unidas por un espacio contienen la base con un espacio o el borde a cada lado.
    /// </summary>
    private static bool ContainsWords(string normalizedText, string words)
    {
        var text = " " + string.Join(" ", Regex.Split(normalizedText, @"[^\p{L}\p{Nd}%]+").Where(w => w.Length > 0)) + " ";
        return text.Contains(" " + words + " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Si recetar <paramref name="medicineName"/> (o el nombre de <paramref name="product"/>), con el genérico del producto,
    /// choca con una alergia del paciente; la primera alergia que choca (tal como se escribió) y el aviso. Sin alergias
    /// declaradas (vacías o "Negadas") no hay aviso. Las alergias se separan por coma, punto y coma, diagonal o renglón, y
    /// las que niegan ("Ninguna") no cuentan. <paramref name="activeIngredient"/> (revisión 16): el principio activo guardado en
    /// un renglón de la receta; entra a la comparación si el nombre no lo dice (el renglón con el paréntesis oculto, "Tempra 500
    /// mg tabletas"), así avisa aunque el producto ya no esté en el catálogo. <paramref name="catalog"/> (revisión 20): los
    /// productos del catálogo, para reconocer una alergia escrita con la marca (<see cref="GenericsOfBrand"/>); sin él, la
    /// regla de antes.
    /// </summary>
    public static (bool HasConflict, string MatchedAllergy, string WarningDetails) CheckMedication(
        PatientRecord? patient, Product? product, string? medicineName = null, string? activeIngredient = null,
        IEnumerable<Product>? catalog = null)
    {
        if (patient == null || !patient.HasAllergies || string.IsNullOrWhiteSpace(patient.Allergies))
        {
            return (false, string.Empty, string.Empty);
        }

        var medName = (medicineName ?? product?.Name ?? string.Empty).Trim();
        var generic = (product?.GenericName ?? string.Empty).Trim();
        var medCombined = $"{medName} {generic}".Trim();
        var ingredient = (activeIngredient ?? string.Empty).Trim();
        if (ingredient.Length > 0 && !TextSearch.Normalize(medName).Contains(TextSearch.Normalize(ingredient), StringComparison.Ordinal))
        {
            medCombined = $"{medCombined} {ingredient}".Trim();
        }
        if (string.IsNullOrWhiteSpace(medCombined))
        {
            return (false, string.Empty, string.Empty);
        }

        var normalizedMed = TextSearch.Normalize(medCombined);

        // Separar alergias declaradas por el paciente
        var allergyTokens = patient.Allergies
            .Split(new[] { ',', ';', '/', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrWhiteSpace(a) && !PatientRecord.DeniesAllergies(a))
            .ToList();

        foreach (var allergy in allergyTokens)
        {
            var normAllergy = TextSearch.Normalize(allergy);

            // Revisión 20: las palabras que no cuentan para las familias de la alergia tampoco cuentan en la coincidencia
            // directa (una alergia a "sulfa" no avisa con "Sulfato ferroso"; una a "Sulfato ferroso" sí, con él)
            var medForDirect = normalizedMed;
            foreach (var (familyKey, members) in DrugFamilies)
            {
                if (MatchesFamily(normAllergy, familyKey, members)) medForDirect = WithoutExcludedWords(medForDirect, familyKey);
            }

            // 1. Coincidencia directa de subcadena
            if (medForDirect.Contains(normAllergy, StringComparison.Ordinal)
                || (normAllergy.Length >= 4 && medForDirect.Trim().Length > 0 && normAllergy.Contains(medForDirect.Trim(), StringComparison.Ordinal)))
            {
                return (true, allergy, $"El paciente registra alergia a '{allergy}'. El medicamento recetado contiene '{medName}'.");
            }

            // 1b. Revisión 20: la alergia escrita con la marca ("Tempra") avisa con el genérico de ese producto del catálogo
            var generics = GenericsOfBrand(allergy, catalog);
            foreach (var brandGeneric in generics)
            {
                // El genérico tal cual o su base sin la sal ni la concentración ("Losartán potásico" avisa con "Losartán 50 mg")
                if (normalizedMed.Contains(TextSearch.Normalize(brandGeneric), StringComparison.Ordinal)
                    || (GenericBase(brandGeneric) is { } genericBase && ContainsWords(normalizedMed, genericBase)))
                {
                    return (true, allergy, $"El paciente registra alergia a '{allergy}' ({brandGeneric}). El medicamento recetado contiene '{medName}'.");
                }
            }

            // 2. Coincidencia por familias de fármacos (la alergia con los genéricos de su marca)
            var allergyForFamilies = generics.Count == 0 ? normAllergy : TextSearch.Normalize($"{allergy} {string.Join(" ", generics)}");
            foreach (var (familyKey, members) in DrugFamilies)
            {
                if (MatchesFamily(allergyForFamilies, familyKey, members) && MatchesFamily(normalizedMed, familyKey, members))
                {
                    return (true, allergy, $"Riesgo de hipersensibilidad: El paciente registra alergia a '{allergy}' (grupo {familyKey.ToUpperInvariant()}). El medicamento recetado '{medName}' pertenece o presenta reactividad cruzada con esta familia farmacológica.");
                }
            }
        }

        return (false, string.Empty, string.Empty);
    }
}
