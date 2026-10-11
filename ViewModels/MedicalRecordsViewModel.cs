using TPVC.Helpers.WpfPlatform;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// El apartado Pacientes en WPF. Todo vive en PatientsWorkspace (ViewModels/Patients, sin WPF): aquí solo
/// se arma la plataforma de WPF (hilo de la interfaz, DialogService, enlaces con el programa del sistema
/// e impresión de la receta). Las vistas (d:DataContext), CachedViewConverter (MedicalRecordsView),
/// MainViewModel, App y las pruebas siguen usando este nombre; los miembros son los de la base.
/// </summary>
public sealed class MedicalRecordsViewModel : PatientsWorkspace
{
    public MedicalRecordsViewModel() : this(null, null, null, null, null, null)
    {
    }

    public MedicalRecordsViewModel(
        InventoryService? inventoryService, 
        ConsultationService? consultationService = null, 
        ClinicProfileService? clinicProfileService = null,
        PrintingService? printingService = null,
        ConsultationDraftStore? consultationDraftStore = null,
        NotificationService? notificationService = null,
        ConsultationHandoffService? consultationHandoffService = null,
        LocalPreferencesStore? localPreferences = null,
        LabResultService? labResultService = null)
        : base(WpfPatientsPlatform.Create(printingService), inventoryService, consultationService, clinicProfileService,
            consultationDraftStore, notificationService, consultationHandoffService, localPreferences, labResultService)
    {
    }
}
