using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Pairing;

namespace TPVC.ViewModels;

/// <summary>
/// Paso 8.14 (decisión del médico del 2026-10-11): vincular la tablet y el celular con la URL y el token. Esta PC muestra
/// un QR con los dos durante 2 minutos (nunca en el portapapeles ni en el registro) y aprueba o rechaza cada equipo nuevo
/// que aparece en Equipos vinculados.
/// </summary>
public partial class TursoSyncViewModel
{
    /// <summary>Cuánto se queda el QR en pantalla: lleva el token completo.</summary>
    public static readonly TimeSpan LinkQrLifetime = TimeSpan.FromMinutes(2);

    /// <summary>El reloj de la cuenta regresiva (las pruebas ponen uno manual).</summary>
    public TimeProvider LinkClock { get; set; } = TimeProvider.System;

    private DispatcherTimer? _linkTimer;
    private DateTimeOffset _linkQrExpiresAt;

    [ObservableProperty]
    private bool _isLinkQrVisible;

    [ObservableProperty]
    private BitmapSource? _linkQrImage;

    [ObservableProperty]
    private string _linkQrCountdownText = string.Empty;

    [ObservableProperty]
    private string _linkStatusText = string.Empty;

    /// <summary>
    /// Administrar la nube y los equipos (vincular, aprobar, rechazar, quitar, «Perdí este equipo», la PC principal, unirse,
    /// «Esta PC ya usaba esta nube», reemplazar y la sincronización completa) es de la PC: <see cref="DevicePermissions.CanAdministerCloud"/>. La tablet,
    /// copia de la PC desde el 2026-10-22, ve «Equipos vinculados» sin estos botones. En la PC siempre es verdadero.
    /// </summary>
    public static bool IsCloudAdministrator => DevicePermissions.CanAdministerCloud(ConfigurationShellViewModel.LocalRole);

    private bool RequireCloudAdministrator()
    {
        if (IsCloudAdministrator) return true;
        SaveStatusMessage = "Esto se hace en la PC: la nube y los equipos vinculados se administran desde ahí.";
        return false;
    }

    [RelayCommand]
    public async Task ShowLinkQrAsync()
    {
        if (!RequireCloudAdministrator()) return;
        var (data, problem) = await _syncService.GetLinkDataAsync();
        if (data == null)
        {
            LinkStatusText = problem ?? string.Empty;
            return;
        }

        var png = DeviceLinkCode.QrPng(DeviceLinkCode.Encode(data));
        var image = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();

        LinkQrImage = image;
        LinkStatusText = string.Empty;
        _linkQrExpiresAt = LinkClock.GetUtcNow() + LinkQrLifetime;
        IsLinkQrVisible = true;
        TickLinkQr();
        StartLinkTimer();
        AppLogger.Info("Turso: se mostró el QR para vincular la tablet o el celular.");
    }

    [RelayCommand]
    public void CloseLinkQr()
    {
        _linkTimer?.Stop();
        IsLinkQrVisible = false;
        LinkQrImage = null; // la imagen lleva el token: no se queda en memoria más de lo necesario
        LinkQrCountdownText = string.Empty;
    }

    /// <summary>La cuenta regresiva; al llegar a cero el QR se cierra solo.</summary>
    public void TickLinkQr()
    {
        if (!IsLinkQrVisible) return;
        var left = _linkQrExpiresAt - LinkClock.GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            CloseLinkQr();
            LinkStatusText = "El QR se cerró solo. Ábralo otra vez si todavía hace falta.";
            return;
        }
        LinkQrCountdownText = $"Se cierra solo en {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void StartLinkTimer()
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return; // pruebas sin WPF: se llama TickLinkQr a mano
        _linkTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickLinkQr(), dispatcher);
        _linkTimer.Start();
    }

    [RelayCommand]
    public Task ApproveAsTabletAsync(LinkedDevice? device) => ApproveAsync(device, DeviceRole.AndroidTablet);

    [RelayCommand]
    public Task ApproveAsPhoneAsync(LinkedDevice? device) => ApproveAsync(device, DeviceRole.AndroidMobileAdmin);

    private async Task ApproveAsync(LinkedDevice? device, DeviceRole role)
    {
        if (!RequireCloudAdministrator()) return;
        if (device == null || !device.IsPendingApproval) return;
        var kind = role == DeviceRole.AndroidTablet ? "tablet" : "celular";
        bool confirm = DialogService.Confirm(
            $"¿Vincular «{device.DeviceName}» como {kind}?\n\nVerifique que el equipo muestre {device.DeviceIdSuffix}. Después podrá ver y " +
            (role == DeviceRole.AndroidTablet
                ? "hacer todo lo que hace esta computadora (consultas, recetas, cobros, inventario y configuración), salvo administrar la nube y los equipos."
                : "ver expedientes, agendar citas y registrar entradas de mercancía."),
            "TPVC - Vincular equipo");
        if (!confirm) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await _syncService.ApproveDeviceAsync(device, role);
            SaveStatusMessage = ok ? message : $"✕ {message}";
            if (ok) CloseLinkQr();
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    public async Task RejectDeviceAsync(LinkedDevice? device)
    {
        if (!RequireCloudAdministrator()) return;
        if (device == null || !device.IsPendingApproval) return;
        bool confirm = DialogService.Confirm(
            $"¿Rechazar «{device.DeviceName}»?\n\nNo bajará ningún dato y sale de la lista. Si no reconoce ese equipo, alguien más tiene el token: " +
            "anúlelo en el panel de Turso y pegue el nuevo en esta computadora.",
            "TPVC - Rechazar equipo");
        if (!confirm) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await _syncService.RejectDeviceAsync(device);
            SaveStatusMessage = ok ? message : $"✕ {message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshDevicesAsync();
    }
}
