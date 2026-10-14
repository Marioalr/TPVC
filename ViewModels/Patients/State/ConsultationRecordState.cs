using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// El expediente durante la consulta en curso (fase 6, paso 8b). La receta empieza vacía: la última
/// del paciente no se arrastra, pero se puede repetir con un clic. Si la consulta termina sin receta,
/// la última emitida sigue siendo la del expediente; si se descarta, el expediente vuelve a como
/// estaba antes de empezarla. Al cambiar la receta avisa al coordinador (ficha, receta y vista previa).
/// </summary>
public sealed class ConsultationRecordState
{
    private readonly PatientSession _session;
    private readonly InventoryService? _inventoryService;
    private readonly IPatientsWorkflow _workflow;

    public ConsultationRecordState(PatientSession session, InventoryService? inventoryService, IPatientsWorkflow workflow)
    {
        _session = session;
        _inventoryService = inventoryService;
        _workflow = workflow;
    }

    /// <summary>Paciente de la consulta en curso (null fuera de consulta).</summary>
    public PatientRecord? Patient { get; private set; }

    /// <summary>Receta emitida antes de la consulta (la "última receta" mientras dura).</summary>
    public List<PatientPrescriptionItem> PreviousPrescription { get; private set; } = new();

    /// <summary>Resumen del expediente antes de la consulta (se regresa si se descarta).</summary>
    public PatientSummarySnapshot? SummaryBeforeConsultation { get; private set; }

    /// <summary>false si la consulta se recuperó de un borrador sin la receta anterior (versiones viejas).</summary>
    public bool PreviousPrescriptionKnown { get; private set; }

    /// <summary>Medicamentos de la receta anterior que aún no están en la de esta consulta.</summary>
    public List<PatientPrescriptionItem> PendingPreviousPrescription
    {
        get
        {
            if (Patient == null || _session.SelectedPatient?.Id != Patient.Id) return new();
            var current = Patient.ActivePrescription;
            return PreviousPrescription
                .Where(p => !current.Any(c => SameMedicine(c, p)))
                .ToList();
        }
    }

    private static bool SameMedicine(PatientPrescriptionItem a, PatientPrescriptionItem b) =>
        string.Equals(a.MedicineName?.Trim(), b.MedicineName?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Al iniciar una consulta nueva: foto del expediente y receta vacía.</summary>
    public void BeginConsultationOnRecord(PatientRecord patient)
    {
        Patient = patient;
        SummaryBeforeConsultation = PatientSummarySnapshot.Of(patient);
        // Con su RowId y sus campos desconocidos (paso 8.18): si la receta anterior se restaura, son los mismos renglones
        PreviousPrescription = patient.ActivePrescription.Select(ClonePrescriptionKeepingIdentity).ToList();
        foreach (var item in PreviousPrescription) item.DispenseInClinic = false;
        PreviousPrescriptionKnown = true;

        patient.ActivePrescription = new List<PatientPrescriptionItem>();
        patient.SyncPrescriptionJson();
        _session.NotifySelectedPatientMutated();
        _workflow.NotifyPrescriptionStateChanged();
    }

    /// <summary>Al recuperar una consulta: su receta en curso, la anterior y la foto, tal como se guardaron.</summary>
    public void ResumeConsultationOnRecord(PatientRecord patient, ConsultationDraft draft)
    {
        // Se enlazan las mismas instancias del expediente cuando existen para que "Se surte aquí"
        // siga funcionando sobre la lista visible
        var resumed = draft.Prescriptions
            .Select(saved => patient.ActivePrescription.FirstOrDefault(p => p.MedicineName == saved.MedicineName && p.Dosage == saved.Dosage) ?? saved)
            .ToList();

        Patient = patient;
        SummaryBeforeConsultation = draft.PatientBefore;
        PreviousPrescriptionKnown = draft.PreviousPrescriptions != null;
        // Con el RowId y los campos desconocidos que trae la copia (paso 8.18; un borrador anterior no los trae)
        PreviousPrescription = draft.PreviousPrescriptions?.Select(ClonePrescriptionKeepingIdentity).ToList() ?? new();

        patient.ActivePrescription = resumed;
        patient.SyncPrescriptionJson();
        _workflow.NotifyPrescriptionStateChanged();
    }

    /// <summary>
    /// Al terminar o descartar la consulta. Descartada: el expediente vuelve a como estaba
    /// (resumen y receta). Terminada sin receta: la última emitida sigue siendo la anterior.
    /// Terminada con receta: queda la nueva.
    /// </summary>
    public async Task EndConsultationOnRecordAsync(bool discarded)
    {
        var patient = Patient;
        if (patient == null) return;
        Patient = null;
        var previous = PreviousPrescription;
        var known = PreviousPrescriptionKnown;
        var summary = SummaryBeforeConsultation;
        PreviousPrescription = new();
        PreviousPrescriptionKnown = false;
        SummaryBeforeConsultation = null;

        bool changed = false;
        // Paso 8.21: al descartar, lo que se regresa se escribe aunque la foto de la instancia diga que no cambió. La nota
        // de la consulta lo escribe en la base y la instancia lo refleja (y lo marca como guardado), pero si algo se
        // cruzara con eso, comparar con la foto vería "sin cambios" y el expediente se quedaría con la consulta descartada
        var restored = new List<string>();
        if (discarded && summary != null)
        {
            summary.ApplyTo(patient);
            restored.AddRange(PatientSummarySnapshot.Fields);
            changed = true;
        }
        if (known && (discarded || patient.ActivePrescription.Count == 0)
            && !(previous.Count == 0 && patient.ActivePrescription.Count == 0))
        {
            patient.ActivePrescription = previous;
            patient.SyncPrescriptionJson();
            if (discarded) restored.Add(nameof(PatientRecord.ActivePrescriptionJson));
            changed = true;
        }

        if (changed && _inventoryService != null && patient.Id > 0)
        {
            try
            {
                await _inventoryService.SavePatientAsync(patient, patient.StoredSnapshot?.Without(restored));
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo regresar el expediente a como estaba antes de la consulta", ex);
            }
        }

        if (_session.SelectedPatient?.Id == patient.Id) _session.NotifySelectedPatientMutated();
        _workflow.NotifyPrescriptionStateChanged();
    }

    /// <summary>
    /// La consulta pasó a otro equipo y se cierra aquí sin descartarla: se sueltan las referencias sin
    /// tocar el expediente (sin ellas el cierre no regresa la receta ni el resumen).
    /// </summary>
    public void Abandon()
    {
        Patient = null;
        SummaryBeforeConsultation = null;
        PreviousPrescription = new List<PatientPrescriptionItem>();
        PreviousPrescriptionKnown = false;
    }

    /// <summary>
    /// Copia de la receta anterior (paso 8.18): conserva el <c>RowId</c> y una copia de los campos que esta PC no conoce,
    /// así al restaurarla la hoja tiene los mismos renglones. La usan la memoria de la consulta, su borrador y la copia
    /// que viaja a otro equipo (<c>PreviousPrescriptions</c>, campo aditivo: sin <c>RowId</c> no se escribe y una PC
    /// anterior lo ignora). La receta en curso del borrador se copia con <see cref="ClonePrescription"/>.
    /// </summary>
    internal static PatientPrescriptionItem ClonePrescriptionKeepingIdentity(PatientPrescriptionItem item)
    {
        var copy = ClonePrescription(item);
        copy.RowId = item.RowId;
        copy.ExtensionData = item.ExtensionData is { } extra ? new(extra) : null;
        return copy;
    }

    internal static PatientPrescriptionItem ClonePrescription(PatientPrescriptionItem item) => new()
    {
        MedicineName = item.MedicineName,
        Dosage = item.Dosage,
        RecommendedQuantity = item.RecommendedQuantity,
        Presentation = item.Presentation,
        DispenseInClinic = item.DispenseInClinic
    };
}
