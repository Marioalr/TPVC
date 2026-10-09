namespace TPVC.ViewModels.Patients;

/// <summary>
/// Abre un enlace (WhatsApp, Google Calendar) con el programa del sistema. Lanza excepción si no se
/// puede: quien lo llama decide qué avisar.
/// </summary>
public interface IUrlLauncher
{
    void Open(string url);
}
