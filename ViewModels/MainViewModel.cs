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
    private string _appVersion = "1.3.0";

    // ViewModels
    public MainHubViewModel MainHubVM { get; }
    public PosViewModel PosVM { get; }
    public MedicalRecordsViewModel MedicalRecordsVM { get; }
    public ConfigurationShellViewModel ConfigurationShellVM { get; }
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

        AppVersion = $"v{_updateService.CurrentVersion.TrimStart('v', 'V')}";

        MainHubVM = new MainHubViewModel(NavigateTo);
        PosVM = new PosViewModel(inventoryService);
        MedicalRecordsVM = new MedicalRecordsViewModel(inventoryService);
        _ = MedicalRecordsVM.LoadServicesAsync();

        // Enlace para volver a la Vista Principal desde el POS o Expedientes
        PosVM.OnBackToHubRequested = () => NavigateTo("MainHub");
        MedicalRecordsVM.OnBackToHubRequested = () => NavigateTo("MainHub");

        // Enlace para enviar receta médica directamente al carrito de venta del POS
        MedicalRecordsVM.OnSendPrescriptionToPos = (medicine, qty) =>
        {
            PosVM.PatientName = MedicalRecordsVM.SelectedPatient?.FullName ?? "Paciente de Consulta";
            PosVM.AddPrescriptionItem(medicine, qty);
        };

        MedicalRecordsVM.OnNavigateToPosRequested = () =>
        {
            NavigateTo("Pos");
        };

        DashboardVM = new DashboardViewModel(inventoryService, NavigateTo);
        InventoryVM = new InventoryViewModel(inventoryService);
        BatchesVM = new BatchesViewModel(inventoryService);
        MovementsVM = new MovementsViewModel(inventoryService);
        SettingsVM = new SettingsViewModel(themeService, updateService);

        ConfigurationShellVM = new ConfigurationShellViewModel(
            NavigateTo,
            DashboardVM,
            InventoryVM,
            BatchesVM,
            MovementsVM,
            SettingsVM,
            AppVersion);

        // Iniciar en la Vista Principal (las 3 cards: Nueva Venta, Nueva Consulta y Configuración)
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
                break;
            case "Pos":
                CurrentView = PosVM;
                _ = PosVM.InitializeAsync();
                break;
            case "Records":
                CurrentView = MedicalRecordsVM;
                break;
            case "Config":
                CurrentView = ConfigurationShellVM;
                break;
            case "Dashboard":
            case "Inventory":
            case "Batches":
            case "Movements":
            case "Settings":
                ConfigurationShellVM.NavigateSubView(viewName);
                CurrentView = ConfigurationShellVM;
                break;
        }
    }
}
