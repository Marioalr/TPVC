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
/// Paso 8.23: lo que una bajada trajo y choca con lo de este equipo: citas que se empalman (de hoy en adelante) y expedientes
/// que pueden ser el mismo paciente. Solo se revisa lo que llegó (las filas que la bajada creó o cambió aquí), nunca en la
/// primera descarga de una base (ahí llega todo). Va a los avisos de este equipo (<see cref="SyncNotices"/>), que se quedan
/// hasta que el médico los descarta; los mismos tipos sirven para mostrarlos.
/// </summary>
public sealed class SyncArrivals
{
    public static readonly SyncArrivals None = new(Array.Empty<AppointmentOverlap>(), Array.Empty<DuplicatePatientPair>());

    public SyncArrivals(IReadOnlyList<AppointmentOverlap> overlaps, IReadOnlyList<DuplicatePatientPair> duplicatePatients)
    {
        Overlaps = overlaps;
        DuplicatePatients = duplicatePatients;
    }

    public IReadOnlyList<AppointmentOverlap> Overlaps { get; }
    public IReadOnlyList<DuplicatePatientPair> DuplicatePatients { get; }
    public bool IsEmpty => Overlaps.Count == 0 && DuplicatePatients.Count == 0;

    /// <summary>El aviso de los empalmes que llegaron (vacío si no hay): los tres primeros y cuántos más.</summary>
    public string OverlapNotice => Overlaps.Count == 0
        ? string.Empty
        : (Overlaps.Count == 1 ? "Empalme en la agenda" : "Empalmes en la agenda") + " con lo que llegó de otro equipo: " +
          string.Join("; ", Overlaps.Take(3).Select(o => o.Text)) +
          (Overlaps.Count > 3 ? $" y {Overlaps.Count - 3} más" : string.Empty) +
          ". No se canceló ninguna cita: decida cuál mover o cancelar.";
}
