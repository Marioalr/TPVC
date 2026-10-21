using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// La calculadora de dosis pediátrica de la receta (paso 8.26; antes dentro de <c>PrescriptionSheetViewModel</c>, sin
/// cambios): el peso que se sugiere, el resultado que se muestra y la indicación que se copia. Las cuentas son las de
/// <see cref="PediatricDoseRule"/> (ml = peso × ml/kg redondeado a un decimal; gotas = esos ml × gotas/ml redondeado a
/// entero; los dos con <c>Math.Round</c> de double, al par). Sin WPF ni base: vectores en
/// <c>contracts/golden/pediatric-dose.json</c>.
/// </summary>
public static class PediatricDoseRules
{
    public const string NoFormulaText = "Esta regla no tiene fórmula de ml por kg en el Excel.";
    public const string AskWeightText = "Capture el peso del paciente (kg).";

    /// <summary>Lo que se sugiere como peso solo si es menor que esto (un adulto no usa la calculadora).</summary>
    public const double MaxSuggestedCurrentWeight = 70;

    /// <summary>Un número con punto o coma decimal, sin espacios a los lados (cultura invariante).</summary>
    public static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Replace(',', '.').Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// El peso con que se llena la calculadora al elegir una regla con el peso vacío: el de la consulta en curso si es mayor
    /// que 0 y menor que 70; si no, el último peso mayor que 0 de las consultas del paciente (por fecha); vacío si no hay.
    /// Con "0.#" (invariante).
    /// </summary>
    public static string SuggestWeight(bool hasActiveConsultation, string? currentWeightText, IEnumerable<PatientConsultation>? consultations)
    {
        if (hasActiveConsultation && TryParseNumber(currentWeightText, out var current) && current > 0 && current < MaxSuggestedCurrentWeight)
        {
            return current.ToString("0.#", CultureInfo.InvariantCulture);
        }

        var last = consultations?
            .Where(c => c.Weight.HasValue && c.Weight > 0)
            .OrderByDescending(c => c.Date)
            .FirstOrDefault();
        return last?.Weight?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Lo que muestra la calculadora: vacío sin regla, el aviso de una regla sin fórmula, el de capturar el peso (no es un
    /// número mayor que 0), o "7.5 ml (150 gotas) por dosis" / "7.5 ml por dosis".
    /// </summary>
    public static string ResultText(PediatricDoseRule? rule, string? weightText)
    {
        if (rule == null) return string.Empty;
        if (!rule.CanCalculate) return NoFormulaText;
        if (!TryParseNumber(weightText, out var weight) || weight <= 0) return AskWeightText;

        var ml = rule.CalculateMl(weight)!.Value;
        var drops = rule.CalculateDrops(weight);
        return drops.HasValue
            ? $"{ml.ToString("0.#", CultureInfo.InvariantCulture)} ml ({drops.Value:0} gotas) por dosis"
            : $"{ml.ToString("0.#", CultureInfo.InvariantCulture)} ml por dosis";
    }

    /// <summary>
    /// La indicación que "Aplicar" copia al medicamento: "Dar 7.5 ml (150 gotas) vía oral cada ___ hrs por ___ días"; null si
    /// no hay regla, el peso no es un número o la regla no da ml con ese peso (sin fórmula, o peso 0 o negativo).
    /// </summary>
    public static string? DosageText(PediatricDoseRule? rule, string? weightText)
    {
        if (rule == null || !TryParseNumber(weightText, out var weight)) return null;

        var ml = rule.CalculateMl(weight);
        if (ml == null) return null;

        var drops = rule.CalculateDrops(weight);
        var amount = drops.HasValue
            ? $"{ml.Value.ToString("0.#", CultureInfo.InvariantCulture)} ml ({drops.Value:0} gotas)"
            : $"{ml.Value.ToString("0.#", CultureInfo.InvariantCulture)} ml";
        return $"Dar {amount} vía oral cada ___ hrs por ___ días";
    }
}
