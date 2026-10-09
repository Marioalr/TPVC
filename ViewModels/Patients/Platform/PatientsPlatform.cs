namespace TPVC.ViewModels.Patients;

/// <summary>Lo que Pacientes necesita de la plataforma (WPF hoy; MAUI más adelante).</summary>
public sealed record PatientsPlatform(IUiThread Ui, IUserDialogs Dialogs, IUrlLauncher Urls, IPrescriptionOutput? Printer);
