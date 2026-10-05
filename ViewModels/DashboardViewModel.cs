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

    public bool HasNoLowStock => LowStockProducts.Count == 0;
    public bool HasNoExpiringBatches => ExpiringBatches.Count == 0;
    public bool HasNoRecentMovements => RecentMovements.Count == 0;

    public DashboardViewModel(
        InventoryService inventoryService, 
        Action<string> navigateAction)
    {
        _inventoryService = inventoryService;
        _navigateAction = navigateAction;

        // Auto-actualización reactiva en tiempo real al detectar cualquier cambio
        _inventoryService.OnInventoryChanged += () =>
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.InvokeAsync(async () => await RefreshAsync());
            }
            else
            {
                _ = RefreshAsync();
            }
        };
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

    [RelayCommand]
    public void NavigateTo(string viewName)
    {
        _navigateAction(viewName);
    }
}
