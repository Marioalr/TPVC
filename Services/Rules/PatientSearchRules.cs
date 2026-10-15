using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// El buscador de pacientes del inicio (paso 8.26; antes <c>PatientsHomeViewModel.RankPatients</c>, sin cambios). Sin WPF
/// ni base: Kotlin la porta con <c>contracts/golden/rank-patients.json</c>.
/// </summary>
public static class PatientSearchRules
{
    /// <summary>Cuántos resultados muestra el inicio.</summary>
    public const int ResultLimit = 30;

    /// <summary>
    /// Pacientes que coinciden con lo escrito (sin acentos): primero los que empiezan así, luego
    /// los que tienen una palabra que empieza así y al final los que lo contienen en nombre, folio,
    /// folio sin conexión (paso 8.17), teléfono (4 dígitos o más) o diagnóstico. Desempate por el nombre completo con la
    /// cultura actual (es-MX en el consultorio).
    /// </summary>
    public static List<PatientRecord> Rank(IEnumerable<PatientRecord> patients, string? query, int limit = ResultLimit)
    {
        var q = TextSearch.Normalize(query);
        if (q.Length == 0) return new List<PatientRecord>();
        var digits = new string(q.Where(char.IsDigit).ToArray());

        return patients
            .Select(p =>
            {
                var name = TextSearch.Normalize(p.FullName);
                var other = TextSearch.Normalize($"{p.RecordNumber} {p.OriginalFolio} {p.LastDiagnosis}");
                int rank;
                if (name.StartsWith(q, StringComparison.Ordinal)) rank = 0;
                else if ((" " + name).Contains(" " + q, StringComparison.Ordinal)) rank = 1;
                else if (name.Contains(q, StringComparison.Ordinal) || other.Contains(q, StringComparison.Ordinal)) rank = 2;
                else if (digits.Length >= 4 && new string((p.Phone ?? string.Empty).Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)) rank = 2;
                else rank = -1;
                return (Patient: p, Rank: rank);
            })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Patient.FullName, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(x => x.Patient)
            .ToList();
    }
}
