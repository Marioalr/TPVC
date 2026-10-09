using System;
using System.Threading.Tasks;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Guardado automático de la consulta en curso: vive en Autosave (ConsultationAutosave), con todo el
/// contrato del borrador. Aquí quedan la carga inicial, la pausa compartida con el alta de paciente y el
/// guardado al cerrar el programa (el constructor lo conecta con Autosave.Attach()).
/// </summary>
public partial class MedicalRecordsViewModel
{
    // La pausa es la misma para la consulta y el alta de paciente (PatientForm la lee de aquí)
    private readonly AutosaveOptions _autosaveOptions = new();

    /// <summary>Pausa al escribir antes de guardar (las pruebas la acortan).</summary>
    public TimeSpan AutosaveDelay
    {
        get => _autosaveOptions.Delay;
        set => _autosaveOptions.Delay = value;
    }

    /// <summary>Carga inicial lanzada por el constructor (expedientes, servicios y consulta recuperada).</summary>
    public Task InitialLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Al cerrar el programa o apagar Windows: consulta y alta de paciente en curso.</summary>
    public void FlushConsultationDraft()
    {
        Autosave.SaveConsultationDraftNow();
        PatientForm.SavePatientFormDraftNow();
    }
}
