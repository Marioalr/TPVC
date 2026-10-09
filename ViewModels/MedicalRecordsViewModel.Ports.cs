using System.Collections.ObjectModel;
using TPVC.Models;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Puertos del coordinador (fase 6, plan §1.4): los hijos avisan aquí y el coordinador corre los
/// mismos cuerpos de antes, en el mismo orden. Implementación explícita: no es API pública ni se enlaza.
/// </summary>
public partial class MedicalRecordsViewModel : IPatientsHubs
{
    void IPatientsHubs.OnPatientsPageChanged() => NotifyPatientsPageState();

    void IPatientsHubs.OnServicesChanged() => NotifyAccountChanged();

    void IPatientsHubs.OnPrescriptionTemplatesLoaded() => OnPropertyChanged(nameof(ShowsPediatricDose));

    void IPatientsHubs.OnVitalSignsEvolutionPointsChanged(ObservableCollection<VitalSignsEvolutionPoint> value)
    {
        LatestVitals = BuildLatestVitals(value);
        OnPropertyChanged(nameof(HasLatestVitals));
    }
}
