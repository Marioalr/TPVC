using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly InventoryService _inventoryService;
    private readonly AppUpdateService _updateService;
    private readonly Action<string> _navigateAction;

    [ObservableProperty]
    private DashboardMetrics _metrics = new();

    [ObservableProperty]
    private ObservableCollection<InventoryMovement> _recentMovements = new();

    [ObservableProperty]
    private ObservableCollection<Product> _lowStockProducts = new();

    [ObservableProperty]
    private ObservableCollection<ProductBatch> _expiringBatches = new();

    [ObservableProperty]
    private bool _isLoading;

    // Actualización de la Aplicación
    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _isUpdateModalOpen;

    [ObservableProperty]
    private string _updateModalTitle = "Actualización de la Aplicación";

    [ObservableProperty]
    private string _updateModalMessage = string.Empty;

    [ObservableProperty]
    private string _latestVersion = "1.0.0";

    [ObservableProperty]
    private string _releaseNotes = string.Empty;

    [ObservableProperty]
    private string _updateUrl = string.Empty;

    [ObservableProperty]
    private string _directDownloadUrl = string.Empty;

    [ObservableProperty]
    private string _downloadFileName = string.Empty;

    public bool HasNoLowStock => LowStockProducts.Count == 0;
    public bool HasNoExpiringBatches => ExpiringBatches.Count == 0;
    public bool HasNoRecentMovements => RecentMovements.Count == 0;

    public DashboardViewModel(
        InventoryService inventoryService, 
        AppUpdateService updateService,
        Action<string> navigateAction)
    {
        _inventoryService = inventoryService;
        _updateService = updateService;
        _navigateAction = navigateAction;

        // Auto-actualización reactiva en tiempo real sin presionar botones
        _inventoryService.OnInventoryChanged += () =>
        {
            Application.Current?.Dispatcher?.Invoke(() => _ = RefreshAsync());
        };

        // Verificación silenciosa en segundo plano al iniciar
        _ = CheckAppUpdateSilentlyAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            Metrics = await _inventoryService.GetDashboardMetricsAsync();

            var movements = await _inventoryService.GetRecentMovementsAsync(15);
            RecentMovements = new ObservableCollection<InventoryMovement>(movements);

            var lowStock = await _inventoryService.GetProductsAsync(onlyLowStock: true);
            LowStockProducts = new ObservableCollection<Product>(lowStock.Take(8));

            var batches = await _inventoryService.GetBatchesAsync();
            var expiring = batches.Where(b => !b.IsDepleted && b.Quantity > 0 && b.DaysRemaining <= 60)
                                  .OrderBy(b => b.ExpirationDate)
                                  .Take(8);
            ExpiringBatches = new ObservableCollection<ProductBatch>(expiring);

            OnPropertyChanged(nameof(HasNoLowStock));
            OnPropertyChanged(nameof(HasNoExpiringBatches));
            OnPropertyChanged(nameof(HasNoRecentMovements));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CheckAppUpdateSilentlyAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            if (result.IsUpdateAvailable)
            {
                IsUpdateAvailable = true;
                LatestVersion = result.LatestVersion;
                ReleaseNotes = result.ReleaseNotes;
                UpdateUrl = result.ReleaseUrl;
                DirectDownloadUrl = result.DirectDownloadUrl;
                DownloadFileName = result.DownloadFileName;
            }
        }
        catch
        {
            // Silencioso en comprobación en segundo plano
        }
    }

    [RelayCommand]
    public async Task CheckAppUpdateCommandAsync()
    {
        IsCheckingUpdate = true;
        UpdateModalTitle = "Comprobando Actualización de la App...";
        UpdateModalMessage = "Consultando el repositorio oficial en GitHub...";
        IsUpdateModalOpen = true;

        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            LatestVersion = result.LatestVersion;
            ReleaseNotes = result.ReleaseNotes;
            UpdateUrl = result.ReleaseUrl;
            DirectDownloadUrl = result.DirectDownloadUrl;
            DownloadFileName = result.DownloadFileName;
            IsUpdateAvailable = result.IsUpdateAvailable;

            if (result.IsUpdateAvailable)
            {
                UpdateModalTitle = "🚀 ¡Nueva Versión Disponible!";
                UpdateModalMessage = $"Existe una versión más reciente (v{result.LatestVersion}) en GitHub. Tu versión actual es v{result.CurrentVersion}.";
            }
            else
            {
                UpdateModalTitle = "Sistema al Día";
                UpdateModalMessage = result.Message;
            }
        }
        catch (Exception ex)
        {
            UpdateModalTitle = "Error de Conexión";
            UpdateModalMessage = $"No se pudo verificar la actualización: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    public async Task DownloadAndApplyUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(DirectDownloadUrl))
        {
            // Si no hay archivo directo .exe adjunto, abrir la página de Releases
            _updateService.OpenReleaseUrl(UpdateUrl);
            IsUpdateModalOpen = false;
            return;
        }

        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        var progress = new Progress<double>(p => DownloadProgress = Math.Round(p, 1));

        bool success = await _updateService.DownloadAndInstallUpdateAsync(DirectDownloadUrl, DownloadFileName, progress);
        IsDownloadingUpdate = false;

        if (!success)
        {
            UpdateModalMessage = "Hubo un inconveniente al descargar la actualización. Abriendo la página de GitHub...";
            _updateService.OpenReleaseUrl(UpdateUrl);
        }
    }

    [RelayCommand]
    public void CloseUpdateModal()
    {
        IsUpdateModalOpen = false;
    }

    [RelayCommand]
    public void OpenReleasePage()
    {
        _updateService.OpenReleaseUrl(UpdateUrl);
    }

    [RelayCommand]
    public void NavigateTo(string viewName)
    {
        _navigateAction(viewName);
    }
}
