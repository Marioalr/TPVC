using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Puertos del coordinador (fase 6, plan §1.4): los hijos avisan aquí y el coordinador corre los
/// mismos cuerpos de antes, en el mismo orden. Implementación explícita: no es API pública ni se enlaza.
/// El candado de la consulta (IConsultationGate) y lo del envío que guarda el borrador
/// (IDraftHandoffParticipant) los resuelve Handoff (ConsultationHandoffViewModel).
/// </summary>
public partial class MedicalRecordsViewModel : IPatientsHubs, IPatientsWorkflow, IConsultationGate, IDraftHandoffParticipant
{
    void IPatientsWorkflow.SyncActiveConsultation() => SyncActiveConsultation();

    void IPatientsWorkflow.NotifyPrescriptionStateChanged() => NotifyPrescriptionStateChanged();

    void IPatientsWorkflow.RaisePrescriptionIndications() => Preview.RaisePrescriptionIndications();

    Task IPatientsWorkflow.RefreshAgendaAfterChangeAsync(int patientId) => RefreshAgendaAfterChangeAsync(patientId);

    Task IPatientsWorkflow.LoadPatientsAsync() => LoadPatientsAsync();

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

    void IPatientsHubs.OnSelectedPatientChanged(PatientRecord? oldValue, PatientRecord? newValue)
    {
        // Solo un cambio real de paciente reinicia el estado de guardado de la consulta
        if (oldValue?.Id != newValue?.Id || newValue == null)
        {
            Consultation.IsCurrentConsultationSaved = false;
        }
        Prescription.RaiseShowsPediatricDose();
        Consultation.NotifyVitalSignsWarningChanged();
        Chart.RefreshChartData();
        NotifyPatientsPageState();
        if (newValue != null)
        {
            var list = newValue.Consultations.OrderByDescending(c => c.Date).ToList();
            Session.ConsultationsHistory = new ObservableCollection<PatientConsultation>(list);
            RefreshVitalSignsEvolution(newValue);
            _ = Chart.LoadPatientAppointmentsAsync(newValue.Id);

            if (NeedsConsultationsLoad(newValue))
            {
                _ = LoadConsultationsForAsync(newValue, refreshSelectedHistory: true);
            }
        }
        else
        {
            Session.ConsultationsHistory.Clear();
            RefreshVitalSignsEvolution(null);
            Chart.PatientAppointmentsHistory.Clear();
        }
    }

    void IPatientsHubs.OnPatientsChanged()
    {
        Home.RefreshRecentPatients();
        Home.RefreshHomeResults();
    }

    void IPatientsHubs.OnConsultationsHistoryChanged(ObservableCollection<PatientConsultation> value)
    {
        Chart.ObserveConsultationsHistory(value);
        Consultation.RaisePreviousConsultation();
    }

    void IPatientsHubs.OnPatientsPageChanged() => NotifyPatientsPageState();

    void IPatientsHubs.OnActiveConsultationElapsedTimeChanged() => OnPropertyChanged(nameof(ConsultationBannerText));

    void IPatientsHubs.OnServicesChanged() => NotifyAccountChanged();

    void IPatientsHubs.OnPrescriptionTemplatesLoaded() => Prescription.RaiseShowsPediatricDose();

    void IPatientsHubs.OnVitalSignsEvolutionPointsChanged(ObservableCollection<VitalSignsEvolutionPoint> value) => Chart.OnVitalSignsEvolutionPointsChanged(value);
}
