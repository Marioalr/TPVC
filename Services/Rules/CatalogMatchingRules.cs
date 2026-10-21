using System;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Qué producto del inventario es un renglón de la receta o de los insumos, y qué es medicamento o insumo (paso 8.26; antes
/// en <c>CatalogStore</c>, sin cambios). El producto por nombre de un renglón es
/// <see cref="PatientSheetRows.FindProductByName"/>. Sin WPF ni base: vectores en <c>contracts/golden/catalog-matching.json</c>.
/// </summary>
public static class CatalogMatchingRules
{
    /// <summary>
    /// El renglón <paramref name="name"/> es este producto (sin acentos ni mayúsculas): su nombre, "Nombre (Presentación)", su
    /// nombre seguido de algo más ("Paracetamol 500 mg cada 8 horas"), su genérico o su renglón en el orden normado
    /// (<see cref="PrescriptionNaming.MedicineLine"/>, desde v1.23.0).
    /// </summary>
    public static bool ProductMatchesName(Product product, string name)
    {
        var n = TextSearch.Normalize(name);
        var productName = TextSearch.Normalize(product.Name);
        return n == productName
               || n == TextSearch.Normalize($"{product.Name} ({product.Presentation})")
               || n.StartsWith(productName + " ", StringComparison.Ordinal)
               || (!string.IsNullOrEmpty(product.GenericName) && n == TextSearch.Normalize(product.GenericName))
               || (!string.IsNullOrEmpty(product.GenericName) && n == TextSearch.Normalize(PrescriptionNaming.MedicineLine(product)));
    }

    /// <summary>
    /// Medicamento: categoría que dice "Medicamento" o "Fármaco", o código que empieza con MED; no lo es una categoría de
    /// curación, insumo, antiséptico o diagnóstico, ni un código CUR, INS, ANT, DIA o MAT; lo demás sí (sin distinguir
    /// mayúsculas; los acentos cuentan).
    /// </summary>
    public static bool IsProductMedication(Product p)
    {
        if (p.Category != null && (p.Category.Name.Contains("Medicamento", StringComparison.OrdinalIgnoreCase) || p.Category.Name.Contains("Fármaco", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (!string.IsNullOrEmpty(p.Code) && p.Code.StartsWith("MED", StringComparison.OrdinalIgnoreCase))
            return true;
        if (p.Category != null && (
            p.Category.Name.Contains("Curación", StringComparison.OrdinalIgnoreCase) ||
            p.Category.Name.Contains("Insumo", StringComparison.OrdinalIgnoreCase) ||
            p.Category.Name.Contains("Antiséptico", StringComparison.OrdinalIgnoreCase) ||
            p.Category.Name.Contains("Diagnóstico", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrEmpty(p.Code) && (
            p.Code.StartsWith("CUR", StringComparison.OrdinalIgnoreCase) ||
            p.Code.StartsWith("INS", StringComparison.OrdinalIgnoreCase) ||
            p.Code.StartsWith("ANT", StringComparison.OrdinalIgnoreCase) ||
            p.Code.StartsWith("DIA", StringComparison.OrdinalIgnoreCase) ||
            p.Code.StartsWith("MAT", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    /// <summary>Insumo: todo lo que no es medicamento.</summary>
    public static bool IsProductSupply(Product p) => !IsProductMedication(p);
}
