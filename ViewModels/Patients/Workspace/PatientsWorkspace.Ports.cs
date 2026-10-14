using System.Threading.Tasks;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Puertos del coordinador (fase 6, plan §1.4): los flujos que piden los hijos (IPatientsWorkflow) corren
/// los mismos cuerpos de antes, en el mismo orden. Implementación explícita: no es API pública ni se enlaza.
/// El candado de la consulta (IConsultationGate) y lo del envío que guarda el borrador
/// (IDraftHandoffParticipant) los resuelve Handoff (ConsultationHandoffViewModel). Los avisos de los
/// hijos (IPatientsHubs) están en PatientsWorkspace.Hubs.cs.
/// </summary>
public partial class PatientsWorkspace
{
    void IPatientsWorkflow.SyncActiveConsultation() => SyncActiveConsultation();

    void IPatientsWorkflow.NotifyPrescriptionStateChanged() => NotifyPrescriptionStateChanged();

    void IPatientsWorkflow.RaisePrescriptionIndications() => Preview.RaisePrescriptionIndications();

    Task IPatientsWorkflow.RefreshAgendaAfterChangeAsync(int patientId) => RefreshAgendaAfterChangeAsync(patientId);

    Task IPatientsWorkflow.LoadPatientsAsync() => LoadPatientsAsync();

    Task IPatientsWorkflow.ReloadIfReleasedAsync() => ReloadIfReleasedAsync();

    Task IPatientsWorkflow.LoadServicesAsync() => LoadServicesAsync();

    void IPatientsWorkflow.StartActiveConsultation() => Consultation.StartActiveConsultation();

    void IPatientsWorkflow.NotifyAccountChanged() => NotifyAccountChanged();

    void IPatientsWorkflow.RefreshVitalSignsEvolution(PatientRecord? patient) => RefreshVitalSignsEvolution(patient);

    Task IPatientsWorkflow.OnConsultationChargedAsync(SaleReceipt? receipt) => OnConsultationChargedAsync(receipt);

    void IPatientsWorkflow.ShowConsultation() => ShowConsultation();

    void IPatientsWorkflow.OpenPatientChart(PatientRecord? patient) => OpenPatientChart(patient);

    Task IPatientsWorkflow.InitialLoad => InitialLoad;

    void IPatientsWorkflow.NotifyPatientsPageState() => NotifyPatientsPageState();

    void IPatientsWorkflow.RaiseConsultationBannerText() => OnPropertyChanged(nameof(ConsultationBannerText));

    void IPatientsWorkflow.CloseConsultationSheets() => CloseConsultationSheets();

    bool IConsultationGate.BlockIfConsultationReadOnly() => Handoff.BlockIfConsultationReadOnly();

    bool IConsultationGate.BlockIfPatientHandedOff(PatientRecord? patient) => Handoff.BlockIfPatientHandedOff(patient);

    bool IConsultationGate.BlockIfReceivingHandoff() => Handoff.BlockIfReceivingHandoff();

    ConsultationHandoffMarker? IDraftHandoffParticipant.OutgoingMarker => Handoff.OutgoingMarker;

    string? IDraftHandoffParticipant.ReceivedHandoffId
    {
        get => Handoff.ReceivedHandoffId;
        set => Handoff.ReceivedHandoffId = value;
    }

    string? IDraftHandoffParticipant.ReceivedHandoffToken
    {
        get => Handoff.ReceivedHandoffToken;
        set => Handoff.ReceivedHandoffToken = value;
    }

    void IDraftHandoffParticipant.RestoreOutgoingHandoff(ConsultationHandoffMarker marker) => Handoff.RestoreOutgoingHandoff(marker);

    void IDraftHandoffParticipant.OnReceivedHandoffPersisted(ConsultationDraft draft) => Handoff.OnReceivedHandoffPersisted(draft);
}
