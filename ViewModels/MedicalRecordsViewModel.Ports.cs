using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Puertos del coordinador (fase 6, plan §1.4): los hijos avisan aquí y el coordinador corre los
/// mismos cuerpos de antes, en el mismo orden. Implementación explícita: no es API pública ni se enlaza.
/// Hasta el paso 20, el candado de la consulta (IConsultationGate) lo resuelve Handoff.cs del coordinador.
/// </summary>
public partial class MedicalRecordsViewModel : IPatientsHubs, IPatientsWorkflow, IConsultationGate
{
    void IPatientsWorkflow.NotifyPrescriptionStateChanged() => NotifyPrescriptionStateChanged();

    Task IPatientsWorkflow.RefreshAgendaAfterChangeAsync(int patientId) => RefreshAgendaAfterChangeAsync(patientId);

    Task IPatientsWorkflow.LoadPatientsAsync() => LoadPatientsAsync();

    void IPatientsWorkflow.StartActiveConsultation() => StartActiveConsultation();

    void IPatientsWorkflow.ShowConsultation() => ShowConsultation();

    void IPatientsWorkflow.OpenPatientChart(PatientRecord? patient) => OpenPatientChart(patient);

    bool IConsultationGate.BlockIfPatientHandedOff(PatientRecord? patient) => BlockIfPatientHandedOff(patient);

    void IPatientsHubs.OnSelectedPatientChanged(PatientRecord? oldValue, PatientRecord? newValue)
    {
        // Solo un cambio real de paciente reinicia el estado de guardado de la consulta
        if (oldValue?.Id != newValue?.Id || newValue == null)
        {
            _isCurrentConsultationSaved = false;
        }
        OnPropertyChanged(nameof(ShowsPediatricDose));
        NotifyVitalSignsWarningChanged();
        RefreshChartData();
        NotifyPatientsPageState();
        if (newValue != null)
        {
            var list = newValue.Consultations.OrderByDescending(c => c.Date).ToList();
            Session.ConsultationsHistory = new ObservableCollection<PatientConsultation>(list);
            RefreshVitalSignsEvolution(newValue);
            _ = LoadPatientAppointmentsAsync(newValue.Id);

            if (NeedsConsultationsLoad(newValue))
            {
                _ = LoadConsultationsForAsync(newValue, refreshSelectedHistory: true);
            }
        }
        else
        {
            Session.ConsultationsHistory.Clear();
            RefreshVitalSignsEvolution(null);
            PatientAppointmentsHistory.Clear();
        }
    }

    void IPatientsHubs.OnPatientsChanged()
    {
        Home.RefreshRecentPatients();
        Home.RefreshHomeResults();
    }

    void IPatientsHubs.OnConsultationsHistoryChanged(ObservableCollection<PatientConsultation> value)
    {
        ObserveConsultationsHistory(value);
        OnPropertyChanged(nameof(PreviousConsultation));
    }

    void IPatientsHubs.OnPatientsPageChanged() => NotifyPatientsPageState();

    void IPatientsHubs.OnActiveConsultationElapsedTimeChanged() => OnPropertyChanged(nameof(ConsultationBannerText));

    void IPatientsHubs.OnServicesChanged() => NotifyAccountChanged();

    void IPatientsHubs.OnPrescriptionTemplatesLoaded() => OnPropertyChanged(nameof(ShowsPediatricDose));

    void IPatientsHubs.OnVitalSignsEvolutionPointsChanged(ObservableCollection<VitalSignsEvolutionPoint> value)
    {
        LatestVitals = BuildLatestVitals(value);
        OnPropertyChanged(nameof(HasLatestVitals));
    }
}
