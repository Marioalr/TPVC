using System;
using System.Threading.Tasks;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Guardado automático de la consulta en curso: vive en Autosave (ConsultationAutosave), con todo el
/// contrato del borrador. Aquí quedan la carga inicial, la pausa compartida con el alta de paciente, el
/// punto del constructor donde se conecta y el guardado al cerrar el programa.
/// </summary>
public partial class MedicalRecordsViewModel
{
    // El mismo almacén que usa Autosave: el envío entre equipos (Handoff.cs, hasta el paso 20) anota ahí
    // las consultas recibidas y aparta las copias
    private ConsultationDraftStore? _draftStore;

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

    private void InitializeAutosave(ConsultationDraftStore? store)
    {
        _draftStore = store;
        Autosave.Attach();
    }

    /// <summary>Al cerrar el programa o apagar Windows: consulta y alta de paciente en curso.</summary>
    public void FlushConsultationDraft()
    {
        Autosave.SaveConsultationDraftNow();
        PatientForm.SavePatientFormDraftNow();
    }
}
