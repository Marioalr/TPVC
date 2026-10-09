using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// El apartado Pacientes visto desde la aplicación (fase 6, paso 21): lo que MainViewModel, App y las
/// pruebas usan del coordinador. Es la fachada ([NotForBinding] en PatientsWorkspace: atajos a miembros de
/// sus dueños) más la carga inicial, la recarga de expedientes, entrar al apartado, guardar los borradores
/// al cerrar el programa, el regreso al inicio de la app y Dispose. Lo implementa PatientsWorkspace con
/// sus miembros públicos; otra interfaz (MAUI) usaría lo mismo.
/// </summary>
public interface IPatientsSection : IDisposable
{
    // ------------------------------------------------------------------ fachada

    /// <summary>Session.Patients.</summary>
    ObservableCollection<PatientRecord> Patients { get; set; }

    /// <summary>Session.SelectedPatient.</summary>
    PatientRecord? SelectedPatient { get; set; }

    /// <summary>Session.ConsultationsHistory.</summary>
    ObservableCollection<PatientConsultation> ConsultationsHistory { get; set; }

    /// <summary>Session.SelectPatient.</summary>
    void SelectPatient(PatientRecord? patient);

    /// <summary>Active.HasActiveConsultation.</summary>
    bool HasActiveConsultation { get; }

    /// <summary>Active.ActiveConsultation.</summary>
    ActiveConsultation? ActiveConsultation { get; }

    /// <summary>Agenda.OpenAgendaPanelAsync.</summary>
    Task OpenAgendaPanelAsync();

    /// <summary>PatientForm.NewPatientAsync.</summary>
    Task NewPatientAsync();

    /// <summary>Consultation.StartActiveConsultation.</summary>
    void StartActiveConsultation();

    /// <summary>Autosave.IsConsultationRecovered.</summary>
    bool IsConsultationRecovered { get; }

    /// <summary>Handoff.LeavePatientsSection.</summary>
    void LeavePatientsSection();

    /// <summary>Handoff.StartHandoffPolling.</summary>
    void StartHandoffPolling();

    // ------------------------------------------------------------------ apartado

    /// <summary>Carga inicial lanzada por el constructor (expedientes, servicios y consulta recuperada).</summary>
    Task InitialLoad { get; }

    /// <summary>Recarga los expedientes de la base (p. ej. después de cargar los datos de demostración).</summary>
    Task LoadPatientsAsync();

    /// <summary>Al entrar desde el inicio de la app: a la consulta si hay una en curso, si no al inicio.</summary>
    void EnterPatientsSection();

    /// <summary>Al cerrar el programa o apagar el equipo: consulta y alta de paciente en curso.</summary>
    void FlushConsultationDraft();

    /// <summary>"Volver al inicio" de la app (BackToHubCommand).</summary>
    Action? OnBackToHubRequested { get; set; }
}
