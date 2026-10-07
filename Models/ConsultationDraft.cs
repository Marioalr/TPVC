using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TPVC.Models;

/// <summary>
/// Copia de trabajo de la consulta en curso (guardado automático). Se guarda en un archivo local
/// del equipo y no se sincroniza: la nota definitiva se escribe en el expediente al terminar.
/// </summary>
public sealed class ConsultationDraft
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTime SavedAt { get; set; }

    public int PatientId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string Folio { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public string ChiefComplaintLabel { get; set; } = string.Empty;

    public string ChiefComplaint { get; set; } = string.Empty;
    public string SystemsReview { get; set; } = string.Empty;
    public string PhysicalExam { get; set; } = string.Empty;
    public string Diagnosis { get; set; } = string.Empty;
    public string TreatmentPlan { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string GeneralIndications { get; set; } = string.Empty;

    public string TA { get; set; } = string.Empty;
    public string FC { get; set; } = string.Empty;
    public string FR { get; set; } = string.Empty;
    public string Temp { get; set; } = string.Empty;
    public string Weight { get; set; } = string.Empty;
    public string Height { get; set; } = string.Empty;
    public string SpO2 { get; set; } = string.Empty;
    public string Glucose { get; set; } = string.Empty;

    public int Section { get; set; } = 1;

    /// <summary>true si ya se había escrito un avance de la nota en el expediente.</summary>
    public bool SavedToRecord { get; set; }

    public List<ConsultationProductItem> Items { get; set; } = new();
    /// <summary>Receta de esta consulta (la que se ve, se imprime y se cobra).</summary>
    public List<PatientPrescriptionItem> Prescriptions { get; set; } = new();

    /// <summary>Última receta emitida antes de esta consulta (para "Repetir"); null en borradores anteriores a v1.14.</summary>
    public List<PatientPrescriptionItem>? PreviousPrescriptions { get; set; }

    /// <summary>Resumen del expediente antes de la consulta (vuelve tal cual si se descarta).</summary>
    public PatientSummarySnapshot? PatientBefore { get; set; }

    /// <summary>Consulta a la que pertenece la copia (evita reescribir una ya terminada).</summary>
    [JsonIgnore]
    public long Generation { get; set; }
}

/// <summary>
/// Resumen que el expediente muestra de su última consulta. Guardar el avance de una nota lo
/// actualiza; si después la consulta se descarta, se regresa a como estaba.
/// </summary>
public sealed record PatientSummarySnapshot(DateTime LastVisit, string LastDiagnosis, string VitalSigns, string ClinicalNotes)
{
    public static PatientSummarySnapshot Of(PatientRecord patient) =>
        new(patient.LastVisit, patient.LastDiagnosis ?? string.Empty, patient.VitalSigns ?? string.Empty, patient.ClinicalNotes ?? string.Empty);

    public void ApplyTo(PatientRecord patient)
    {
        patient.LastVisit = LastVisit;
        patient.LastDiagnosis = LastDiagnosis;
        patient.VitalSigns = VitalSigns;
        patient.ClinicalNotes = ClinicalNotes;
    }
}
