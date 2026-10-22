using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Helpers.WpfPlatform;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Paso 8.15: «Perdí este equipo» y la guía para cortarle el acceso. Sin token de la API, la PC no puede anular los tokens
/// sola: guía al médico a anularlos en Turso (se anulan todos), pegar el nuevo aquí y en los demás equipos.
/// </summary>
public partial class TursoSyncViewModel
{
    public const string TursoDashboardUrl = "https://app.turso.tech";

    /// <summary>Abre el panel de Turso (las pruebas ponen uno que solo anota).</summary>
    public IUrlLauncher Urls { get; set; } = new ShellUrlLauncher();

    /// <summary>Los pasos para cortar el acceso; vacío si no se espera un token nuevo.</summary>
    [ObservableProperty]
    private string _cutAccessGuideText = string.Empty;

    private void RefreshCutAccessGuide()
    {
        if (!_settingsService.IsTokenRotationExpected)
        {
            CutAccessGuideText = string.Empty;
            return;
        }

        var cloud = CloudHost(_settingsService.CurrentSettings.DatabaseUrl);
        var first = _settingsService.IsCurrentTokenRejected
            ? "1. ✓ Los tokens ya se anularon: Turso rechazó el de esta computadora."
            : $"1. Anule los tokens de la base ({cloud}) en el panel de Turso, o en una terminal con 'turso db tokens invalidate' y el " +
              "nombre de la base. Se anulan al instante todos, también el de esta computadora (V4): los equipos dejan de sincronizar " +
              "hasta recibir el nuevo, sin perder nada.";
        CutAccessGuideText =
            "Para cortarle el acceso al equipo perdido:\n" +
            first + "\n" +
            "2. Cree un token nuevo en el panel (o con 'turso db tokens create'), péguelo en «Conexión con Turso» y pulse " +
            "«Sincronizar ahora».\n" +
            "3. En los demás equipos, pegue el token nuevo o lea el QR de esta computadora. No hay que aprobarlos otra vez; la lista " +
            "marca los que faltan.\n" +
            "4. Si es una tablet o un celular, bórrelo también con «Encontrar mi dispositivo» de Google.\n" +
            "Mientras no se anulen los tokens, el equipo perdido conserva el acceso a la nube.";
    }

    /// <summary>"prueba-estres-usuario.aws-us-east-2.turso.io" de la URL (para reconocer la base en el panel).</summary>
    internal static string CloudHost(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "la de esta computadora";

    /// <summary>«Perdí este equipo»: lo revoca con el motivo "perdido" y muestra los pasos para cortarle el acceso.</summary>
    [RelayCommand]
    public async Task ReportLostDeviceAsync(LinkedDevice? device)
    {
        if (!RequireCloudAdministrator()) return;
        if (device == null || !device.CanReportLost) return;
        var pending = device.PendingCount is > 0 ? $"\n\n{device.PendingText}: se pierden." : string.Empty;
        bool confirm = DialogService.Confirm(
            $"¿«{device.DeviceName}» ({device.RoleText.ToLowerInvariant()}) se perdió o se lo robaron?\n\n" +
            "Queda marcado como perdido: si se conecta antes de que se anulen los tokens, borra sus datos sin subir nada." + pending +
            "\n\nMarcarlo no le corta el acceso a la nube: después se muestran los pasos para anular los tokens.",
            "TPVC - Perdí este equipo");
        if (!confirm) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await _syncService.ReportLostDeviceAsync(device);
            SaveStatusMessage = ok ? message : $"✕ {message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCutAccessGuide();
        }
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    public void OpenTursoDashboard()
    {
        try
        {
            Urls.Open(TursoDashboardUrl);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo abrir el panel de Turso: {ex.Message}");
            SaveStatusMessage = $"✕ No se pudo abrir el navegador. Entre a {TursoDashboardUrl}.";
        }
    }

    /// <summary>«Ya no hace falta»: cierra los pasos sin anotar una rotación.</summary>
    [RelayCommand]
    public void DismissCutAccessGuide()
    {
        bool confirm = DialogService.Confirm(
            "Se cierran los pasos. Si no se anularon los tokens, el equipo perdido sigue con acceso a la nube.\n\n¿Desea continuar?",
            "TPVC - Cerrar los pasos");
        if (!confirm) return;
        _settingsService.ClearTokenRotation();
        RefreshCutAccessGuide();
    }
}
