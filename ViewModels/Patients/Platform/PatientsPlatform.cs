namespace TPVC.ViewModels.Patients;

/// <summary>
/// Lo que Pacientes necesita de la plataforma (WPF hoy; MAUI más adelante). <paramref name="LabFiles"/>: elegir y
/// preparar los archivos de un resultado de laboratorio (paso 8.12b); sin él no se pueden agregar.
/// </summary>
public sealed record PatientsPlatform(IUiThread Ui, IUserDialogs Dialogs, IUrlLauncher Urls, IPrescriptionOutput? Printer, ILabFilePicker? LabFiles = null);
