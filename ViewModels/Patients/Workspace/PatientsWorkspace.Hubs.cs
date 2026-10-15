using System.Collections.ObjectModel;
using System.Linq;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Avisos de los hijos (IPatientsHubs, fase 6, plan §1.4 y §3): cada gancho generado de un hijo llama aquí
/// en el mismo punto en que antes corría su cuerpo, y el coordinador hace lo de siempre con los demás
/// dueños, en el mismo orden. Implementación explícita: no es API pública ni se enlaza.
/// </summary>
public partial class PatientsWorkspace
{
    void IPatientsHubs.OnSelectedPatientChanged(PatientRecord? oldValue, PatientRecord? newValue)
    {
        // Una "Cambiar indicación" abierta era de los renglones del paciente anterior
        Prescription.CancelIndicationEdits();
        // Solo un cambio real de paciente reinicia el estado de guardado de la consulta
        if (oldValue?.Id != newValue?.Id || newValue == null)
        {
            Consultation.IsCurrentConsultationSaved = false;
            LabResults.ResetForPatientChange();
        }
        _ = LabResults.LoadForPatientAsync(newValue?.Id);
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
