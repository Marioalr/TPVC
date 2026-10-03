using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly InventoryService _inventoryService;
    private readonly ThemeService _themeService;
    private readonly AppUpdateService _updateService;

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _activeViewName = "Dashboard";

    [ObservableProperty]
    private string _appTitle = "TPVC - Inventario Consultorio Médico";

    [ObservableProperty]
    private string _appVersion = "1.0.0";

    [ObservableProperty]
    private bool _isRefreshing;

    // ViewModels
    public DashboardViewModel DashboardVM { get; }
    public InventoryViewModel InventoryVM { get; }
    public BatchesViewModel BatchesVM { get; }
    public MovementsViewModel MovementsVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public MainViewModel(
        InventoryService inventoryService, 
        ThemeService themeService, 
        AppUpdateService updateService)
    {
        _inventoryService = inventoryService;
        _themeService = themeService;
        _updateService = updateService;

        AppVersion = $"v{_updateService.CurrentVersion}";

        DashboardVM = new DashboardViewModel(inventoryService, updateService, NavigateTo);
        InventoryVM = new InventoryViewModel(inventoryService);
        BatchesVM = new BatchesViewModel(inventoryService);
        MovementsVM = new MovementsViewModel(inventoryService);
        SettingsVM = new SettingsViewModel(themeService, updateService);

        // Iniciar en el Dashboard
        NavigateTo("Dashboard");
    }

    [RelayCommand]
    public void NavigateTo(string viewName)
    {
        ActiveViewName = viewName;

        switch (viewName)
        {
            case "Dashboard":
                CurrentView = DashboardVM;
                _ = DashboardVM.RefreshAsync();
                break;
            case "Inventory":
                CurrentView = InventoryVM;
                _ = InventoryVM.LoadDataAsync();
                break;
            case "Batches":
                CurrentView = BatchesVM;
                _ = BatchesVM.LoadBatchesAsync();
                break;
            case "Movements":
                CurrentView = MovementsVM;
                _ = MovementsVM.LoadMovementsAsync();
                break;
            case "Settings":
                CurrentView = SettingsVM;
                break;
        }
    }

    [RelayCommand]
    public async Task GlobalRefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            if (CurrentView is DashboardViewModel db)
            {
                await db.RefreshAsync();
            }
            else if (CurrentView is InventoryViewModel inv)
            {
                await inv.FilterProductsAsync();
            }
            else if (CurrentView is BatchesViewModel bat)
            {
                await bat.LoadBatchesAsync();
            }
            else if (CurrentView is MovementsViewModel mov)
            {
                await mov.LoadMovementsAsync();
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}
