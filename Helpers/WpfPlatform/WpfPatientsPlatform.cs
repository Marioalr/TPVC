using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.Helpers.WpfPlatform;

/// <summary>
/// La plataforma de Pacientes en WPF: el hilo de la interfaz (toma el contexto de sincronización al
/// crearse, es decir al crear MedicalRecordsViewModel), las confirmaciones con DialogService, los enlaces
/// con el programa del sistema y el servicio de impresión para la receta.
/// </summary>
public static class WpfPatientsPlatform
{
    public static PatientsPlatform Create(PrintingService? printingService) =>
        new(new WpfUiThread(), new WpfUserDialogs(), new ShellUrlLauncher(), printingService);
}
