using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Paso 8.23 (H3): expedientes que pueden ser el mismo paciente. No hay CURP: dos expedientes son posibles duplicados si su
/// nombre normalizado es igual y además tienen la misma fecha de nacimiento o los mismos últimos 10 dígitos del teléfono.
/// <list type="bullet">
/// <item><b>El nombre</b> se normaliza como la búsqueda (<see cref="TextSearch.Normalize"/>: sin acentos ni diéresis, la ñ
/// como n, minúsculas) y los espacios seguidos (también tabuladores y el espacio duro) cuentan como uno. Nada más: un guion
/// o un punto siguen contando ("María-José" no es "María José").</item>
/// <item><b>El teléfono</b>: solo sus dígitos ASCII, los últimos 10 (así "+52 (55) 1234-5678" es "55 1234 5678"). Con menos
/// de 10 dígitos no se compara.</item>
/// <item><b>La fecha de nacimiento</b>, solo la fecha; sin ella no se compara.</item>
/// <item>Solo cuentan los expedientes activos y sin <c>MergedIntoSyncId</c> (el que se unió a otro en el paso 8.A), con
/// nombre. Un expediente nunca es duplicado de sí mismo (mismo SyncId, no vacío: uno nuevo todavía no lo tiene).</item>
/// </list>
/// Solo avisa (al crear un paciente y en el banner después de bajar); nunca une ni cambia nada. Vectores en
/// <c>contracts/golden/duplicate-patients.json</c>.
/// </summary>
public static class DuplicatePatientRules
{
    /// <summary>Un expediente como lo ve la regla.</summary>
    public sealed record Patient(Guid SyncId, string? FullName, DateTime? BirthDate, string? Phone, bool IsActive = true, Guid? MergedIntoSyncId = null);

    /// <summary>El expediente guardado como lo ve la regla.</summary>
    public static Patient Of(PatientRecord patient) =>
        new(patient.SyncId, patient.FullName, patient.BirthDate, patient.Phone, patient.IsActive, patient.MergedIntoSyncId);

    /// <summary>Por qué se parecen dos expedientes con el mismo nombre.</summary>
    [Flags]
    public enum Match
    {
        None = 0,
        BirthDate = 1,
        Phone = 2,
        BirthDateAndPhone = BirthDate | Phone
    }

    /// <summary>Cuántos dígitos del final del teléfono se comparan.</summary>
    public const int PhoneDigits = 10;

    /// <summary>El nombre para comparar: la normalización de la búsqueda con los espacios seguidos como uno.</summary>
    public static string NormalizeName(string? name) =>
        string.Join(' ', TextSearch.Normalize(name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Los últimos 10 dígitos del teléfono, o null si tiene menos.</summary>
    public static string? PhoneKey(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= PhoneDigits ? digits[^PhoneDigits..] : null;
    }

    /// <summary>Activo, sin unir a otro y con nombre.</summary>
    public static bool Counts(Patient patient) =>
        patient.IsActive && patient.MergedIntoSyncId == null && NormalizeName(patient.FullName).Length > 0;

    /// <summary>Por qué <paramref name="a"/> y <paramref name="b"/> pueden ser el mismo paciente, o <see cref="Match.None"/>.</summary>
    public static Match Compare(Patient a, Patient b)
    {
        if ((a.SyncId != Guid.Empty && a.SyncId == b.SyncId) || !Counts(a) || !Counts(b)) return Match.None;
        if (!string.Equals(NormalizeName(a.FullName), NormalizeName(b.FullName), StringComparison.Ordinal)) return Match.None;
        var match = Match.None;
        if (a.BirthDate is { } birthA && b.BirthDate is { } birthB && birthA.Date == birthB.Date) match |= Match.BirthDate;
        if (PhoneKey(a.Phone) is { } phoneA && phoneA == PhoneKey(b.Phone)) match |= Match.Phone;
        return match;
    }

    /// <summary>Los expedientes de <paramref name="others"/> que pueden ser <paramref name="candidate"/>, por SyncId.</summary>
    public static List<(Patient Patient, Match Match)> DuplicatesOf(Patient candidate, IEnumerable<Patient> others) =>
        others.Select(o => (Patient: o, Match: Compare(candidate, o)))
            .Where(x => x.Match != Match.None)
            .OrderBy(x => SyncIds.Format(x.Patient.SyncId), StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Los pares de posibles duplicados en que está alguno de <paramref name="involving"/> (los que acaban de bajar): cada par
    /// una vez, el primero el de SyncId menor, ordenados por SyncId.
    /// </summary>
    public static List<(Patient First, Patient Second, Match Match)> Pairs(IEnumerable<Patient> patients, IReadOnlySet<Guid> involving)
    {
        var byName = patients.Where(Counts).GroupBy(p => NormalizeName(p.FullName), StringComparer.Ordinal);
        var pairs = new List<(Patient, Patient, Match)>();
        foreach (var group in byName)
        {
            var list = group.OrderBy(p => SyncIds.Format(p.SyncId), StringComparer.Ordinal).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (!involving.Contains(list[i].SyncId) && !involving.Contains(list[j].SyncId)) continue;
                    var match = Compare(list[i], list[j]);
                    if (match != Match.None) pairs.Add((list[i], list[j], match));
                }
            }
        }
        return pairs.OrderBy(p => SyncIds.Format(p.Item1.SyncId), StringComparer.Ordinal)
            .ThenBy(p => SyncIds.Format(p.Item2.SyncId), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>"misma fecha de nacimiento", "mismo teléfono" o las dos.</summary>
    public static string Describe(Match match) => match switch
    {
        Match.BirthDateAndPhone => "misma fecha de nacimiento y mismo teléfono",
        Match.BirthDate => "misma fecha de nacimiento",
        Match.Phone => "mismo teléfono",
        _ => string.Empty
    };
}
