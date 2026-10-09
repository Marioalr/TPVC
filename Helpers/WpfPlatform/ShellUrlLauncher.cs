using System.Diagnostics;
using TPVC.ViewModels.Patients;

namespace TPVC.Helpers.WpfPlatform;

/// <summary>Abre el enlace con el programa que Windows tenga asignado (navegador, WhatsApp).</summary>
public sealed class ShellUrlLauncher : IUrlLauncher
{
    public void Open(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
