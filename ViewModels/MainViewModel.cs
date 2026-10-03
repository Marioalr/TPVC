using System;
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
    private string _activeViewName = "MainHub";

    [ObservableProperty]
    private string _appTitle = "TPVC - Consultorio Médico";

    [ObservableProperty]
    private string _appVersion = "1.0.0";

    // ViewModels
    public MainHubViewModel MainHubVM { get; }
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

        MainHubVM = new MainHubViewModel(inventoryService);
        DashboardVM = new DashboardViewModel(inventoryService, NavigateTo);
        InventoryVM = new InventoryViewModel(inventoryService);
        BatchesVM = new BatchesViewModel(inventoryService);
        MovementsVM = new MovementsViewModel(inventoryService);
        SettingsVM = new SettingsViewModel(themeService, updateService);

        // Iniciar en la nueva Vista Principal (Expedientes y Venta POS)
        NavigateTo("MainHub");
    }

    [RelayCommand]
    public void NavigateTo(string viewName)
    {
        ActiveViewName = viewName;

        switch (viewName)
        {
            case "MainHub":
                CurrentView = MainHubVM;
                MainHubVM.SetMode("Divided");
                _ = MainHubVM.InitializeAsync();
                break;
            case "Pos":
                CurrentView = MainHubVM;
                MainHubVM.SetMode("Pos");
                _ = MainHubVM.InitializeAsync();
                break;
            case "Records":
                CurrentView = MainHubVM;
                MainHubVM.SetMode("Records");
                break;
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
}
