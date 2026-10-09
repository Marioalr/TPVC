using System.Collections.ObjectModel;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Fachada del coordinador (fase 6, plan §1.3): atajos en C# a miembros que se movieron a su dueño,
/// para MainViewModel, App y las pruebas. No avisan ni se enlazan ([NotForBinding]): las vistas usan la
/// ruta del dueño (Session.SelectedPatient...) y el coordinador escribe en el dueño.
/// </summary>
public partial class MedicalRecordsViewModel
{
    /// <summary>Session.Patients.</summary>
    [NotForBinding]
    public ObservableCollection<PatientRecord> Patients
    {
        get => Session.Patients;
        set => Session.Patients = value;
    }

    /// <summary>Session.SelectedPatient.</summary>
    [NotForBinding]
    public PatientRecord? SelectedPatient
    {
        get => Session.SelectedPatient;
        set => Session.SelectedPatient = value;
    }

    /// <summary>Session.ConsultationsHistory.</summary>
    [NotForBinding]
    public ObservableCollection<PatientConsultation> ConsultationsHistory
    {
        get => Session.ConsultationsHistory;
        set => Session.ConsultationsHistory = value;
    }

    /// <summary>Session.SelectPatient.</summary>
    [NotForBinding]
    public void SelectPatient(PatientRecord? patient) => Session.SelectPatient(patient);

    /// <summary>Active.HasActiveConsultation.</summary>
    [NotForBinding]
    public bool HasActiveConsultation => Active.HasActiveConsultation;

    /// <summary>Active.ActiveConsultation.</summary>
    [NotForBinding]
    public ActiveConsultation? ActiveConsultation => Active.ActiveConsultation;

    /// <summary>Agenda.OpenAgendaPanelAsync.</summary>
    [NotForBinding]
    public Task OpenAgendaPanelAsync() => Agenda.OpenAgendaPanelAsync();

    /// <summary>PatientForm.NewPatientAsync.</summary>
    [NotForBinding]
    public Task NewPatientAsync() => PatientForm.NewPatientAsync();

    /// <summary>Consultation.StartActiveConsultation.</summary>
    [NotForBinding]
    public void StartActiveConsultation() => Consultation.StartActiveConsultation();

    /// <summary>Autosave.IsConsultationRecovered.</summary>
    [NotForBinding]
    public bool IsConsultationRecovered => Autosave.IsConsultationRecovered;

    /// <summary>Handoff.LeavePatientsSection.</summary>
    [NotForBinding]
    public void LeavePatientsSection() => Handoff.LeavePatientsSection();

    /// <summary>Handoff.StartHandoffPolling.</summary>
    [NotForBinding]
    public void StartHandoffPolling() => Handoff.StartHandoffPolling();
}
