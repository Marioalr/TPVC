using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// El aviso de alergia al recetar (paso 8.26; antes <c>InventoryService.CheckMedicationAllergy</c>, sin cambios). Se compara
/// el medicamento (lo escrito en la receta o el nombre del producto, más su genérico) con cada alergia declarada, sin
/// acentos ni mayúsculas: primero que uno contenga al otro y luego las familias con reactividad cruzada, en su orden. Así
/// una alergia a "Paracetamol" avisa al recetar "Tempra" si el producto lleva su genérico. Sin WPF ni base: vectores en
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
        ("sulfa", new[] { "sulfa", "sulfas", "sulfametoxazol", "trimetoprima", "bactrim", "sulfadiazina", "sulfasalazina" }),
        ("macrolido", new[] { "azitromicina", "claritromicina", "eritromicina" }),
        ("quinolona", new[] { "ciprofloxacino", "levofloxacino", "moxifloxacino" }),
    };

    /// <summary>
    /// Si recetar <paramref name="medicineName"/> (o el nombre de <paramref name="product"/>), con el genérico del producto,
    /// choca con una alergia del paciente; la primera alergia que choca (tal como se escribió) y el aviso. Sin alergias
    /// declaradas (vacías o "Negadas") no hay aviso. Las alergias se separan por coma, punto y coma, diagonal o renglón, y
    /// las que niegan ("Ninguna") no cuentan.
    /// </summary>
    public static (bool HasConflict, string MatchedAllergy, string WarningDetails) CheckMedication(
        PatientRecord? patient, Product? product, string? medicineName = null)
    {
        if (patient == null || !patient.HasAllergies || string.IsNullOrWhiteSpace(patient.Allergies))
        {
            return (false, string.Empty, string.Empty);
        }

        var medName = (medicineName ?? product?.Name ?? string.Empty).Trim();
        var generic = (product?.GenericName ?? string.Empty).Trim();
        var medCombined = $"{medName} {generic}".Trim();
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

            // 1. Coincidencia directa de subcadena
            if (normalizedMed.Contains(normAllergy) || (normAllergy.Length >= 4 && normAllergy.Contains(normalizedMed)))
            {
                return (true, allergy, $"El paciente registra alergia a '{allergy}'. El medicamento recetado contiene '{medName}'.");
            }

            // 2. Coincidencia por familias de fármacos
            foreach (var (familyKey, members) in DrugFamilies)
            {
                bool allergyMatchesFamily = normAllergy.Contains(familyKey) || members.Any(m => normAllergy.Contains(m));
                if (allergyMatchesFamily)
                {
                    bool medMatchesFamily = normalizedMed.Contains(familyKey) || members.Any(m => normalizedMed.Contains(m));
                    if (medMatchesFamily)
                    {
                        return (true, allergy, $"Riesgo de hipersensibilidad: El paciente registra alergia a '{allergy}' (grupo {familyKey.ToUpper()}). El medicamento recetado '{medName}' pertenece o presenta reactividad cruzada con esta familia farmacológica.");
                    }
                }
            }
        }

        return (false, string.Empty, string.Empty);
    }
}
