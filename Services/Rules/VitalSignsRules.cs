using System.Collections.Generic;
using System.Globalization;

namespace TPVC.Services.Rules;

/// <summary>
/// El IMC y el aviso de signos vitales fuera de rango mientras se capturan (paso 8.11; antes dentro de
/// ConsultationViewModel). Sin WPF: la referencia de la consulta de la app de Android.
/// </summary>
public static class VitalSignsRules
{
    public const string BmiUnavailable = "Valores no disponibles";

    /// <summary>
    /// La talla en metros: más de 3 se escribió en centímetros y se divide entre 100 (170 → 1.70, 95 → 0.95). Es la misma regla
    /// al capturar (<see cref="BmiText"/>) y al leer la nota guardada (<c>PatientConsultation.ParseAndSetVitalSigns</c>).
    /// </summary>
    public static decimal HeightInMeters(decimal height) => height > 3 ? height / 100m : height;

    /// <summary>
    /// "24.8 kg/m² (Peso normal)" con el peso en kg y la talla en metros o en centímetros (<see cref="HeightInMeters"/>; antes
    /// de la revisión 20 la talla en centímetros daba "0.0 kg/m² (Bajo peso)"); <see cref="BmiUnavailable"/> si falta alguno.
    /// </summary>
    public static string BmiText(string? weight, string? height)
    {
        if (decimal.TryParse((weight ?? string.Empty).Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var kg) &&
            decimal.TryParse((height ?? string.Empty).Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var typed) &&
            typed > 0)
        {
            var meters = HeightInMeters(typed);
            var imc = kg / (meters * meters);
            return $"{imc:F1} kg/m² ({BmiCategory(imc)})";
        }
        return BmiUnavailable;
    }

    /// <summary>Clasificación del IMC de la OMS.</summary>
    public static string BmiCategory(decimal imc)
    {
        if (imc < 18.5m) return "Bajo peso";
        if (imc < 25.0m) return "Peso normal";
        if (imc < 30.0m) return "Sobrepeso";
        if (imc < 35.0m) return "Obesidad Grado I";
        return "Obesidad Grado II+";
    }

    /// <summary>
    /// Valores fuera de rango para adulto o niño (menos de 12 años) en una sola línea, separados por " · "; vacío si todo
    /// es normal. La T/A va como "120/80"; la SpO₂ se acepta como fracción (0.91 = 91 %).
    /// </summary>
    public static string Warning(string? temperature, string? bloodPressure, string? oxygenSaturation, string? glucose, string? heartRate, int? patientAge)
    {
        var warnings = new List<string>();
        if (TryParse(temperature, out var temp))
        {
            if (temp >= 38) warnings.Add($"Temp {temp:0.#} °C: fiebre");
            else if (temp < 35.5 && temp > 0) warnings.Add($"Temp {temp:0.#} °C: hipotermia");
        }

        var ta = (bloodPressure ?? string.Empty).Split('/');
        if (ta.Length == 2 && TryParse(ta[0], out var sys) && TryParse(ta[1], out var dia))
        {
            if (sys >= 140 || dia >= 90) warnings.Add($"T/A {sys:0}/{dia:0}: elevada");
            else if (sys > 0 && sys < 90) warnings.Add($"T/A {sys:0}/{dia:0}: baja");
        }

        if (TryParse(oxygenSaturation, out var sat) && sat > 0)
        {
            if (sat <= 1) sat *= 100;
            if (sat < 92) warnings.Add($"SpO₂ {sat:0} %: baja");
        }

        if (TryParse(glucose, out var glu) && glu > 0)
        {
            if (glu >= 200) warnings.Add($"Glucosa {glu:0} mg/dL: elevada");
            else if (glu < 70) warnings.Add($"Glucosa {glu:0} mg/dL: hipoglucemia");
        }

        if (TryParse(heartRate, out var fc) && fc > 0)
        {
            bool child = patientAge is < 12;
            if (!child && fc > 100) warnings.Add($"FC {fc:0}: taquicardia");
            else if (child && fc > 140) warnings.Add($"FC {fc:0}: alta para la edad");
            else if (fc < 50) warnings.Add($"FC {fc:0}: bradicardia");
        }

        return string.Join(" · ", warnings);
    }

    /// <summary>
    /// El texto de los signos que se guarda en la nota (<c>PatientConsultation.VitalSigns</c>), con lo capturado tal cual (sin
    /// espacios a los lados) y separado por " | ": "TA: 120/80 mmHg | FC: 76 lpm | FR: 18 rpm | Temp: 36.5 °C | Peso: 70 kg |
    /// Talla: 1.70 m | Glucosa: 95 mg/dL | SpO2: 98% | IMC: 24.2 kg/m² (Peso normal)". Lo vacío no va; la T/A pierde la diagonal
    /// del final, la SpO₂ su "%" y el IMC no va si dice "no disponibles". Vacío sin nada capturado.
    /// </summary>
    public static string Format(string? bloodPressure, string? heartRate, string? respiratoryRate, string? temperature, string? weight,
        string? height, string? glucose, string? oxygenSaturation, string? bmiText)
    {
        var cleanBloodPressure = (bloodPressure ?? string.Empty).Trim().TrimEnd('/');
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(cleanBloodPressure)) parts.Add($"TA: {cleanBloodPressure} mmHg");
        if (!string.IsNullOrWhiteSpace(heartRate)) parts.Add($"FC: {heartRate.Trim()} lpm");
        if (!string.IsNullOrWhiteSpace(respiratoryRate)) parts.Add($"FR: {respiratoryRate.Trim()} rpm");
        if (!string.IsNullOrWhiteSpace(temperature)) parts.Add($"Temp: {temperature.Trim()} °C");
        if (!string.IsNullOrWhiteSpace(weight)) parts.Add($"Peso: {weight.Trim()} kg");
        if (!string.IsNullOrWhiteSpace(height)) parts.Add($"Talla: {height.Trim()} m");
        if (!string.IsNullOrWhiteSpace(glucose)) parts.Add($"Glucosa: {glucose.Trim()} mg/dL");
        if (!string.IsNullOrWhiteSpace(oxygenSaturation)) parts.Add($"SpO2: {oxygenSaturation.Trim().TrimEnd('%')}%");
        if (!string.IsNullOrWhiteSpace(bmiText) && !bmiText.Contains("no disponibles")) parts.Add($"IMC: {bmiText.Trim()}");
        return string.Join(" | ", parts);
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Replace(',', '.').Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
}
