using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TPVC.ViewModels;

public partial class ConfigurationShellViewModel : ObservableObject
{
    private readonly Action<string> _navigateMain;

    [ObservableProperty]
    private object? _currentSubView;

    [ObservableProperty]
    private string _activeSubViewName = "Dashboard";

    [ObservableProperty]
    private string _appVersion = "v1.9.0";

    public DashboardViewModel DashboardVM { get; }
    public InventoryViewModel InventoryVM { get; }
    public BatchesViewModel BatchesVM { get; }
    public MovementsViewModel MovementsVM { get; }
    public ClinicProfileViewModel ClinicProfileVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public TursoSyncViewModel TursoSyncVM { get; }

    public ConfigurationShellViewModel(
        Action<string> navigateMain,
        DashboardViewModel dashboardVM,
        InventoryViewModel inventoryVM,
        BatchesViewModel batchesVM,
        MovementsViewModel movementsVM,
        ClinicProfileViewModel clinicProfileVM,
        SettingsViewModel settingsVM,
        TursoSyncViewModel tursoSyncVM,
        string appVersion)
    {
        _navigateMain = navigateMain;
        DashboardVM = dashboardVM;
        InventoryVM = inventoryVM;
        BatchesVM = batchesVM;
        MovementsVM = movementsVM;
        ClinicProfileVM = clinicProfileVM;
        SettingsVM = settingsVM;
        TursoSyncVM = tursoSyncVM;
        AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "v1.9.0" : appVersion;

        // Iniciar en el Panel y Métricas por defecto
        NavigateSubView("Dashboard");
    }

    [RelayCommand]
    public void NavigateSubView(string viewName)
    {
        ActiveSubViewName = viewName;

        switch (viewName)
        {
            case "Dashboard":
                CurrentSubView = DashboardVM;
                _ = DashboardVM.RefreshAsync();
                break;
            case "Inventory":
                CurrentSubView = InventoryVM;
                _ = InventoryVM.LoadDataAsync();
                break;
            case "Batches":
                CurrentSubView = BatchesVM;
                _ = BatchesVM.LoadBatchesAsync();
                break;
            case "Movements":
                CurrentSubView = MovementsVM;
                _ = MovementsVM.LoadMovementsAsync();
                break;
            case "ClinicProfile":
                CurrentSubView = ClinicProfileVM;
                ClinicProfileVM.LoadFromProfile();
                break;
            case "Settings":
                CurrentSubView = SettingsVM;
                break;
            case "TursoSync":
                CurrentSubView = TursoSyncVM;
                TursoSyncVM.LoadFromSettings();
                break;
        }
    }

    [RelayCommand]
    public void BackToHub()
    {
        _navigateMain("MainHub");
    }
}
