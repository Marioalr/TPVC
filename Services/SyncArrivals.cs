using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

/// <summary>Paso 8.23: dos citas empalmadas (<see cref="AppointmentRules"/>), la primera por hora.</summary>
public sealed record AppointmentOverlap(PatientAppointment First, PatientAppointment Second)
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>"mié 15 oct: Ana López (10:30) y Juan Pérez (10:45)".</summary>
    public string Text =>
        $"{First.Date.ToString("ddd d MMM", Mexican)}: {First.PatientName} ({AppointmentRules.TimeText(First.Time)}) y " +
        $"{Second.PatientName} ({AppointmentRules.TimeText(Second.Time)})";
}

/// <summary>Paso 8.23: dos expedientes que pueden ser el mismo paciente (<see cref="DuplicatePatientRules"/>).</summary>
public sealed record DuplicatePatientPair(PatientRecord First, PatientRecord Second, DuplicatePatientRules.Match Match)
{
    /// <summary>El par sin importar el orden (los dos SyncId en minúsculas, el menor primero).</summary>
    public string Key
    {
        get
        {
            var a = SyncIds.Format(First.SyncId);
            var b = SyncIds.Format(Second.SyncId);
            return string.CompareOrdinal(a, b) <= 0 ? $"{a}:{b}" : $"{b}:{a}";
        }
    }

    /// <summary>"Juan Pérez (EXP-2026-004) y Juan Perez (EXP-2026-T1-001), misma fecha de nacimiento".</summary>
    public string Text => $"{Describe(First)} y {Describe(Second)}, {DuplicatePatientRules.Describe(Match)}";

    private static string Describe(PatientRecord patient) =>
        string.IsNullOrWhiteSpace(patient.RecordNumber) ? patient.FullName : $"{patient.FullName} ({patient.RecordNumber})";
}

/// <summary>
/// Dos registros del catálogo (productos, servicios o categorías) que pueden ser el mismo, dados de alta en dos equipos
/// (<see cref="CatalogDuplicateRules"/>; la tablet edita el catálogo desde el 2026-10-22).
/// </summary>
public sealed record DuplicateCatalogPair(CatalogDuplicateRules.Item First, CatalogDuplicateRules.Item Second, CatalogDuplicateRules.Match Match)
{
    /// <summary>"catalogo:&lt;tipo&gt;:&lt;SyncId menor&gt;:&lt;SyncId mayor&gt;".</summary>
    public string Key => SyncNotices.CatalogKey(First.Kind, First.SyncId, Second.SyncId);

    /// <summary>"producto «Paracetamol» (MED-010) y «Paracetamol» (MED-011), mismo nombre".</summary>
    public string Text => $"{CatalogDuplicateRules.KindText(First.Kind)} {Describe(First)} y {Describe(Second)}, {CatalogDuplicateRules.Describe(Match)}";

    private static string Describe(CatalogDuplicateRules.Item item)
    {
        var name = string.IsNullOrWhiteSpace(item.Presentation) ? $"«{item.Name?.Trim()}»" : $"«{item.Name?.Trim()} ({item.Presentation.Trim()})»";
        return string.IsNullOrWhiteSpace(item.Code) ? name : $"{name} ({item.Code.Trim()})";
    }
}

/// <summary>
/// Paso 8.23: lo que una bajada trajo y choca con lo de este equipo: citas que se empalman (de hoy en adelante) y expedientes
/// que pueden ser el mismo paciente. Solo se revisa lo que llegó (las filas que la bajada creó o cambió aquí), nunca en la
/// primera descarga de una base (ahí llega todo). Va a los avisos de este equipo (<see cref="SyncNotices"/>), que se quedan
/// hasta que el médico los descarta; los mismos tipos sirven para mostrarlos.
/// </summary>
public sealed class SyncArrivals
{
    public static readonly SyncArrivals None = new(Array.Empty<AppointmentOverlap>(), Array.Empty<DuplicatePatientPair>());

    public SyncArrivals(IReadOnlyList<AppointmentOverlap> overlaps, IReadOnlyList<DuplicatePatientPair> duplicatePatients,
        IReadOnlyList<DuplicateCatalogPair>? duplicateCatalog = null)
    {
        Overlaps = overlaps;
        DuplicatePatients = duplicatePatients;
        DuplicateCatalog = duplicateCatalog ?? Array.Empty<DuplicateCatalogPair>();
    }

    public IReadOnlyList<AppointmentOverlap> Overlaps { get; }
    public IReadOnlyList<DuplicatePatientPair> DuplicatePatients { get; }

    /// <summary>Productos, servicios o categorías que pueden estar dados de alta dos veces (2026-10-22).</summary>
    public IReadOnlyList<DuplicateCatalogPair> DuplicateCatalog { get; }

    public bool IsEmpty => Overlaps.Count == 0 && DuplicatePatients.Count == 0 && DuplicateCatalog.Count == 0;

    /// <summary>Cuántos pares del catálogo se nombran en el aviso; los demás se cuentan.</summary>
    public const int MaxCatalogPairsShown = 3;

    /// <summary>El aviso del catálogo duplicado (vacío si no hay): los tres primeros y cuántos más.</summary>
    public string DuplicateCatalogNotice => DuplicateCatalog.Count == 0
        ? string.Empty
        : (DuplicateCatalog.Count == 1 ? "Posible registro repetido en el catálogo" : "Posibles registros repetidos en el catálogo") +
          " con lo que llegó de otro equipo: " +
          string.Join("; ", DuplicateCatalog.Take(MaxCatalogPairsShown).Select(p => p.Text)) +
          (DuplicateCatalog.Count > MaxCatalogPairsShown ? $" y {DuplicateCatalog.Count - MaxCatalogPairsShown} más" : string.Empty) +
          ". Si son lo mismo, desactive uno (sus ventas y lotes se conservan) o cámbiele el código. TPVC no los une solo.";

    /// <summary>El aviso de los empalmes que llegaron (vacío si no hay): los tres primeros y cuántos más.</summary>
    public string OverlapNotice => Overlaps.Count == 0
        ? string.Empty
        : (Overlaps.Count == 1 ? "Empalme en la agenda" : "Empalmes en la agenda") + " con lo que llegó de otro equipo: " +
          string.Join("; ", Overlaps.Take(3).Select(o => o.Text)) +
          (Overlaps.Count > 3 ? $" y {Overlaps.Count - 3} más" : string.Empty) +
          ". No se canceló ninguna cita: decida cuál mover o cancelar.";
}
