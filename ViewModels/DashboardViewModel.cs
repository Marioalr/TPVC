using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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

    [ObservableProperty]
    private string _lastUpdatedText = "Sin actualizar";

    [ObservableProperty]
    private string _updateStatusText = "Buscar actualizaciones";

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateUrl = string.Empty;

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
    }

    [RelayCommand]
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

            LastUpdatedText = $"Actualizado hoy a las {DateTime.Now:hh:mm:ss tt}";
        }
        catch (Exception ex)
        {
            LastUpdatedText = $"Error al actualizar: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task CheckAppUpdateAsync()
    {
        UpdateStatusText = "Comprobando en GitHub...";
        var result = await _updateService.CheckForUpdatesAsync();
        UpdateStatusText = result.Message;
        IsUpdateAvailable = result.IsUpdateAvailable;
        UpdateUrl = result.ReleaseUrl;
    }

    [RelayCommand]
    public void OpenUpdateUrl()
    {
        _updateService.OpenReleaseUrl(UpdateUrl);
    }

    [RelayCommand]
    public void NavigateTo(string viewName)
    {
        _navigateAction(viewName);
    }
}
