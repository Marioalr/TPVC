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

    /// <summary>Orden de laboratorio de esta consulta (desde v1.25.0); no se escribe si no se pidió nada.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LabOrder? LabOrder { get; set; }

    /// <summary>Resumen del expediente antes de la consulta (vuelve tal cual si se descarta).</summary>
    public PatientSummarySnapshot? PatientBefore { get; set; }

    /// <summary>Cita que se está atendiendo (queda "Atendida" al cobrar). Id local de este equipo.</summary>
    public int? AttendingAppointmentId { get; set; }

    // ---- Consulta enviada a otro equipo (fase E): identificadores que valen en cualquier equipo ----

    /// <summary>Expediente por su identificador de sincronización (el Id local no sirve en otro equipo).</summary>
    public Guid? PatientSyncId { get; set; }

    /// <summary>Versión del expediente en el equipo que envía: el que recibe espera tenerla tras descargar.</summary>
    public DateTime? PatientUpdatedAtUtc { get; set; }

    /// <summary>Avance de la nota ya guardado con este folio (debe llegar antes de continuar en el otro equipo).</summary>
    public Guid? ProgressNoteSyncId { get; set; }

    public Guid? AttendingAppointmentSyncId { get; set; }

    /// <summary>Honorario de consulta que usaba el equipo que envía.</summary>
    public Guid? FeeServiceSyncId { get; set; }

    /// <summary>
    /// Solo en el archivo local del equipo que la recibió de otro: con ella guardada en disco se
    /// borra la copia de la nube y no se vuelve a ofrecer (aunque se cierre el programa).
    /// </summary>
    public string? ReceivedHandoffId { get; set; }

    public string? ReceivedHandoffToken { get; set; }

    /// <summary>
    /// Solo en el archivo local del equipo que envía: la consulta se mandó a otro equipo y aquí queda
    /// en solo lectura hasta que se acepte (se cierra) o se recupere. Nunca viaja en la copia enviada.
    /// </summary>
    public ConsultationHandoffMarker? Handoff { get; set; }

    /// <summary>Consulta a la que pertenece la copia (evita reescribir una ya terminada).</summary>
    [JsonIgnore]
    public long Generation { get; set; }

    /// <summary>Orden en que se tomó la copia (una más vieja no sobrescribe a una más nueva).</summary>
    [JsonIgnore]
    public long Sequence { get; set; }
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
