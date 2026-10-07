using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public class CommandPaletteItem
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Badge { get; set; } = string.Empty;
    public Action Action { get; set; } = () => { };
}

public partial class MainViewModel : ObservableObject
{
    private readonly InventoryService _inventoryService;
    private readonly ConsultationService _consultationService;
    private readonly ThemeService _themeService;
    private readonly AppUpdateService _updateService;
    private readonly ClinicProfileService _clinicProfileService;
    private readonly PrintingService? _printingService;
    private readonly TursoSyncService _tursoSyncService;
    private readonly TursoSettingsService _tursoSettingsService;

    private readonly List<CommandPaletteItem> _staticCommandItems = new();

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _activeViewName = "MainHub";

    [ObservableProperty]
    private string _appTitle = "TPVC - Consultorio Médico";

    [ObservableProperty]
    private string _appVersion = "1.14.0";

    [ObservableProperty]
    private string _tursoStatusText = "Modo Local (SQLite)";

    [ObservableProperty]
    private string _tursoStatusToolTip = "Almacenamiento local SQLite (0 latencia). Clic para configurar Turso Cloud.";

    [ObservableProperty]
    private string _tursoStatusColorHex = "#64748B";

    // ==========================================
    // ASISTENTE DE PRIMER ARRANQUE (BIENVENIDA)
    // ==========================================
    [ObservableProperty]
    private bool _isFirstRunWizardOpen;

    [ObservableProperty]
    private string _wizardDoctorName = string.Empty;

    [ObservableProperty]
    private string _wizardDoctorLicense = string.Empty;

    [ObservableProperty]
    private string _wizardDoctorSpecialty = "Medicina General";

    [ObservableProperty]
    private string _wizardClinicName = "Consultorio Médico";

    [ObservableProperty]
    private string _wizardClinicAddress = string.Empty;

    [ObservableProperty]
    private string _wizardClinicPhone = string.Empty;

    [ObservableProperty]
    private bool _wizardSeedDemoData = false;

    [ObservableProperty]
    private string? _wizardErrorMessage;

    // ==========================================
    // PALETA GLOBAL DE COMANDOS (Ctrl + K / Ctrl + P)
    // ==========================================
    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _paletteSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CommandPaletteItem> _paletteResults = new();

    [ObservableProperty]
    private CommandPaletteItem? _selectedPaletteItem;

    partial void OnPaletteSearchTextChanged(string value)
    {
        UpdatePaletteResults(value);
    }

    // ViewModels
    public MainHubViewModel MainHubVM { get; }
    public PosViewModel PosVM { get; }
    public MedicalRecordsViewModel MedicalRecordsVM { get; }
    public ConfigurationShellViewModel ConfigurationShellVM { get; }
    public ClinicProfileViewModel ClinicProfileVM { get; }
    public DashboardViewModel DashboardVM { get; }
    public InventoryViewModel InventoryVM { get; }
    public BatchesViewModel BatchesVM { get; }
    public MovementsViewModel MovementsVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public TursoSyncViewModel TursoSyncVM { get; }

    public MainViewModel(
        InventoryService inventoryService, 
        ConsultationService consultationService,
        ThemeService themeService, 
        AppUpdateService updateService,
        BackupService? backupService = null,
        ClinicProfileService? clinicProfileService = null,
        PrintingService? printingService = null,
        TursoSyncService? tursoSyncService = null,
        TursoSettingsService? tursoSettingsService = null,
        DataImportService? dataImportService = null,
        ConsultationDraftStore? consultationDraftStore = null,
        NotificationService? notificationService = null)
    {
        Notifications = notificationService ?? new NotificationService();
        _inventoryService = inventoryService;
        _consultationService = consultationService;
        _themeService = themeService;
        _updateService = updateService;
        _clinicProfileService = clinicProfileService ?? new ClinicProfileService();
        _printingService = printingService;

        _tursoSettingsService = tursoSettingsService ?? new TursoSettingsService();
        _tursoSyncService = tursoSyncService ?? new TursoSyncService(
            new AppDbContextFactory(),
            _tursoSettingsService,
            _clinicProfileService,
            _inventoryService);

        AppVersion = $"v{_updateService.CurrentVersion.TrimStart('v', 'V')}";

        MainHubVM = new MainHubViewModel(NavigateTo, inventoryService, _printingService);
        MainHubVM.OnOpenAgendaRequested = () =>
        {
            // La agenda es un panel del apartado Pacientes
            NavigateTo("Records");
            _ = MedicalRecordsVM?.OpenAgendaPanelAsync();
        };
        // Cada uno con su propia cuenta y el mismo cobro: terminar una consulta ya no vacía la venta
        // a medias del mostrador. El POS solo manda a la consulta ("Abrir consulta"), nunca al revés.
        PosVM = new PosViewModel(inventoryService, consultationService, _clinicProfileService, _printingService, Notifications);
        MedicalRecordsVM = new MedicalRecordsViewModel(inventoryService, consultationService, _clinicProfileService, _printingService, consultationDraftStore, Notifications);
        // MedicalRecordsViewModel ya inicia su propia carga en el constructor (antes se cargaba dos veces)
        _ = PosVM.InitializeAsync();

        PosVM.OnBackToHubRequested = () => NavigateTo("MainHub");
        PosVM.OnOpenConsultationRequested = () => NavigateTo("Records");
        MedicalRecordsVM.OnBackToHubRequested = () => NavigateTo("MainHub");

        // Si quedó una consulta sin terminar (cierre o apagón), se abre directo en ella
        _ = OpenRecoveredConsultationAsync();

        DashboardVM = new DashboardViewModel(inventoryService, NavigateTo);
        InventoryVM = new InventoryViewModel(inventoryService);
        BatchesVM = new BatchesViewModel(inventoryService);
        MovementsVM = new MovementsViewModel(inventoryService);
        ClinicProfileVM = new ClinicProfileViewModel(_clinicProfileService);
        SettingsVM = new SettingsViewModel(themeService, updateService, backupService);
        TursoSyncVM = new TursoSyncViewModel(_tursoSyncService, _tursoSettingsService, _clinicProfileService, dataImportService);

        ConfigurationShellVM = new ConfigurationShellViewModel(
            NavigateTo,
            DashboardVM,
            InventoryVM,
            BatchesVM,
            MovementsVM,
            ClinicProfileVM,
            SettingsVM,
            TursoSyncVM,
            AppVersion,
            new DesignSystemViewModel(themeService, Notifications));

        // Suscribir eventos del estado de Turso
        _tursoSyncService.StatusChanged += (st, msg) => UpdateTursoStatus(st, msg);
        UpdateTursoStatus(_tursoSyncService.Status, _tursoSyncService.StatusMessage);

        if (_tursoSettingsService.CurrentSettings.IsSyncEnabled && _tursoSettingsService.CurrentSettings.AutoSyncOnStartup)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500);
                    await _tursoSyncService.SyncAsync(isBackgroundAutoSync: true);
                }
                catch { }
            });
        }

        // Si es el primer arranque en una instalación nueva, abrir el Asistente Inicial
        if (_clinicProfileService.IsFirstRun || (!_clinicProfileService.CurrentProfile.IsInitialSetupCompleted && !_clinicProfileService.CurrentProfile.IsConfigured))
        {
            IsFirstRunWizardOpen = true;
        }

        // Inicializar comandos estáticos y resultados de la paleta
        InitializeStaticCommands();
        UpdatePaletteResults(string.Empty);

        // Iniciar en la Vista Principal (las 3 cards: Nueva Venta, Nueva Consulta y Configuración)
        NavigateTo("MainHub");
    }

    [RelayCommand]
    public async Task CompleteFirstRunWizardAsync()
    {
        if (string.IsNullOrWhiteSpace(WizardDoctorName) && string.IsNullOrWhiteSpace(WizardClinicName))
        {
            WizardErrorMessage = "Por favor capture al menos el Nombre del Médico o del Consultorio.";
            return;
        }

        var profile = new ClinicProfile
        {
            DoctorName = WizardDoctorName?.Trim() ?? string.Empty,
            ProfessionalLicense = WizardDoctorLicense?.Trim() ?? string.Empty,
            Specialty = string.IsNullOrWhiteSpace(WizardDoctorSpecialty) ? "Medicina General" : WizardDoctorSpecialty.Trim(),
            ClinicName = string.IsNullOrWhiteSpace(WizardClinicName) ? "Consultorio Médico" : WizardClinicName.Trim(),
            Address = WizardClinicAddress?.Trim() ?? string.Empty,
            Phone = WizardClinicPhone?.Trim() ?? string.Empty,
            IsConfigured = true,
            IsInitialSetupCompleted = true
        };

        _clinicProfileService.SaveProfile(profile);

        if (WizardSeedDemoData)
        {
            await _inventoryService.SeedDemoDataAsync();
            await MedicalRecordsVM.LoadPatientsAsync();
            await PosVM.LoadCatalogAsync();
        }

        IsFirstRunWizardOpen = false;
        WizardErrorMessage = null;
    }

    private void InitializeStaticCommands()
    {
        _staticCommandItems.Clear();

        // Módulos y navegación principal
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Punto de Venta (POS)",
            Subtitle = "Cobro en caja, venta de medicamentos y ticket",
            Category = "Módulos",
            Icon = "🛒",
            Action = () => NavigateTo("Pos")
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Pacientes",
            Subtitle = "Citas de hoy, búsqueda de pacientes, expedientes y consulta",
            Category = "Módulos",
            Icon = "📋",
            Action = () => NavigateTo("Records")
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Agenda",
            Subtitle = "Citas de la semana y huecos libres para agendar",
            Category = "Módulos",
            Icon = "📅",
            Action = () =>
            {
                NavigateTo("Records");
                _ = MedicalRecordsVM.OpenAgendaPanelAsync();
            }
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Inventario de Medicamentos",
            Subtitle = "Catálogo general de medicamentos, existencias y precios",
            Category = "Módulos",
            Icon = "📦",
            Action = () => NavigateTo("Inventory")
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Control de Lotes y Caducidades",
            Subtitle = "Semáforo FEFO, lotes próximos a vencer y vencidos",
            Category = "Módulos",
            Icon = "⏳",
            Action = () => NavigateTo("Batches")
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Movimientos de Stock",
            Subtitle = "Historial de entradas, salidas, mermas y ajustes",
            Category = "Módulos",
            Icon = "🔄",
            Action = () => NavigateTo("Movements")
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Métricas y Rendimiento",
            Subtitle = "Indicadores clave del consultorio y estadísticas",
            Category = "Módulos",
            Icon = "📊",
            Action = () => NavigateTo("Dashboard")
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Configuración y Temas",
            Subtitle = "Personalización visual, colores y actualizaciones",
            Category = "Módulos",
            Icon = "⚙️",
            Action = () => NavigateTo("Settings")
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Nube y Turso Cloud",
            Subtitle = "Configuración de sincronización multi-dispositivo y plan gratis",
            Category = "Módulos",
            Icon = "☁️",
            Action = OpenTursoSync
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Menú Principal (Hub)",
            Subtitle = "Pantalla inicial con accesos principales",
            Category = "Módulos",
            Icon = "🏠",
            Action = () => NavigateTo("MainHub")
        });

        // Acciones rápidas
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Sincronizar con Turso Cloud",
            Subtitle = "Enviar y recibir cambios con la base en la nube",
            Category = "Acciones Rápidas",
            Icon = "🔄",
            Action = () => _ = SyncTursoNowAsync()
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Iniciar consulta",
            Subtitle = "Con el paciente abierto; si no hay uno, lleva a buscarlo",
            Category = "Acciones Rápidas",
            Icon = "🩺",
            Action = () =>
            {
                NavigateTo("Records");
                // Misma pantalla de consulta que el botón del expediente (antes abría un formulario
                // antiguo con signos vitales de ejemplo prellenados)
                if (!MedicalRecordsVM.HasActiveConsultation) MedicalRecordsVM.StartActiveConsultation();
            }
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Nuevo paciente",
            Subtitle = "Dar de alta un expediente clínico",
            Category = "Acciones Rápidas",
            Icon = "👤",
            Action = () =>
            {
                NavigateTo("Records");
                _ = MedicalRecordsVM.NewPatientAsync();
            }
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Servicios Clínicos y Procedimientos",
            Subtitle = "Explorar y administrar servicios médicos en POS",
            Category = "Acciones Rápidas",
            Icon = "💉",
            Action = () =>
            {
                NavigateTo("Pos");
                _ = PosVM.OpenServicesModalAsync();
            }
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Ver Lotes Próximos a Vencer (≤60 días)",
            Subtitle = "Revisar medicamentos próximos a caducar para venta prioritaria FEFO",
            Category = "Acciones Rápidas",
            Icon = "⚠️",
            Action = () =>
            {
                NavigateTo("Batches");
                BatchesVM.SelectedFilter = "Proximos";
            }
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Ver Lotes Vencidos",
            Subtitle = "Revisar medicamentos expirados para merma o devolución",
            Category = "Acciones Rápidas",
            Icon = "🛑",
            Action = () =>
            {
                NavigateTo("Batches");
                BatchesVM.SelectedFilter = "Vencidos";
            }
        });
    }

    public void UpdatePaletteResults(string query)
    {
        var list = new List<CommandPaletteItem>();

        if (string.IsNullOrWhiteSpace(query))
        {
            list.AddRange(_staticCommandItems);
        }
        else
        {
            // Búsqueda sin distinguir mayúsculas ni acentos ("perez" encuentra "Pérez")
            var q = TextSearch.Normalize(query);

            // 1. Comandos estáticos
            var matchedCommands = _staticCommandItems.Where(c => TextSearch.Matches(q, c.Title, c.Subtitle, c.Category));
            list.AddRange(matchedCommands);

            // 2. Pacientes
            var patients = MedicalRecordsVM.Patients
                .Where(p => TextSearch.Matches(q, p.FullName, p.RecordNumber, p.Phone, p.Email, p.LastDiagnosis))
                .Take(6);

            foreach (var p in patients)
            {
                var hasAllergies = p.HasAllergies;

                list.Add(new CommandPaletteItem
                {
                    Title = p.FullName,
                    Subtitle = $"Exp: {p.RecordNumber} • Tel: {p.FormattedPhone} • {p.AgeDisplay} • Diagnóstico: {p.LastDiagnosis}",
                    Category = "Pacientes",
                    Icon = "👤",
                    Badge = hasAllergies ? "⚠️ Alergias" : p.RecordNumber,
                    Action = () =>
                    {
                        NavigateTo("Records");
                        MedicalRecordsVM.SelectPatient(p);
                    }
                });
            }

            // 3. Productos / Medicamentos
            var productsSource = PosVM.Products.Count > 0 ? PosVM.Products : InventoryVM.Products;
            var products = productsSource
                .Where(p => TextSearch.Matches(q, p.Name, p.GenericName, p.Code, p.Location))
                .Take(6);

            foreach (var prod in products)
            {
                list.Add(new CommandPaletteItem
                {
                    Title = prod.Name,
                    Subtitle = $"Código: {prod.Code} • Stock: {prod.CurrentStock} {prod.Unit} • ${prod.Price:N2}",
                    Category = "Medicamentos",
                    Icon = prod.RequiresRefrigeration ? "❄️" : "💊",
                    Badge = $"${prod.Price:N2}",
                    Action = () =>
                    {
                        NavigateTo("Pos");
                        PosVM.AddToCart(prod);
                    }
                });
            }
        }

        PaletteResults = new ObservableCollection<CommandPaletteItem>(list);
        SelectedPaletteItem = PaletteResults.FirstOrDefault();
    }

    [RelayCommand]
    public void OpenCommandPalette()
    {
        PaletteSearchText = string.Empty;
        UpdatePaletteResults(string.Empty);
        IsCommandPaletteOpen = true;
    }

    [RelayCommand]
    public void CloseCommandPalette()
    {
        IsCommandPaletteOpen = false;
        PaletteSearchText = string.Empty;
    }

    [RelayCommand]
    public void ToggleCommandPalette()
    {
        if (IsCommandPaletteOpen)
        {
            CloseCommandPalette();
        }
        else
        {
            OpenCommandPalette();
        }
    }

    [RelayCommand]
    public void MovePaletteDown()
    {
        if (PaletteResults.Count == 0) return;
        int idx = SelectedPaletteItem != null ? PaletteResults.IndexOf(SelectedPaletteItem) : -1;
        if (idx < 0 || idx >= PaletteResults.Count - 1)
        {
            SelectedPaletteItem = PaletteResults[0];
        }
        else
        {
            SelectedPaletteItem = PaletteResults[idx + 1];
        }
    }

    [RelayCommand]
    public void MovePaletteUp()
    {
        if (PaletteResults.Count == 0) return;
        int idx = SelectedPaletteItem != null ? PaletteResults.IndexOf(SelectedPaletteItem) : -1;
        if (idx <= 0)
        {
            SelectedPaletteItem = PaletteResults[PaletteResults.Count - 1];
        }
        else
        {
            SelectedPaletteItem = PaletteResults[idx - 1];
        }
    }

    [RelayCommand]
    public void ExecuteSelectedPaletteItem()
    {
        var item = SelectedPaletteItem;
        if (item != null)
        {
            CloseCommandPalette();
            item.Action?.Invoke();
        }
    }

    [RelayCommand]
    public void SelectPaletteItem(CommandPaletteItem? item)
    {
        if (item != null)
        {
            CloseCommandPalette();
            item.Action?.Invoke();
        }
    }

    [RelayCommand]
    private async Task OpenRecoveredConsultationAsync()
    {
        try
        {
            await MedicalRecordsVM.InitialLoad;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Fallo la carga inicial de expedientes", ex);
            return;
        }

        if (MedicalRecordsVM.IsConsultationRecovered) NavigateTo("Records");
    }

    /// <summary>Avisos breves de toda la aplicación (esquina inferior derecha).</summary>
    public NotificationService Notifications { get; }

    [RelayCommand]
    public void DismissNotification(Notification? notification) => Notifications.Dismiss(notification);

    public void NavigateTo(string viewName)
    {
        ActiveViewName = viewName;

        switch (viewName)
        {
            case "MainHub":
                CurrentView = MainHubVM;
                _ = MainHubVM.RefreshAlertsAsync();
                break;
            case "Pos":
                CurrentView = PosVM;
                _ = PosVM.InitializeAsync();
                break;
            case "Records":
                CurrentView = MedicalRecordsVM;
                MedicalRecordsVM.EnterPatientsSection();
                break;
            case "Config":
                CurrentView = ConfigurationShellVM;
                break;
            case "Dashboard":
            case "Inventory":
            case "Batches":
            case "Movements":
            case "Settings":
            case "TursoSync":
            case "DesignSystem":
                ConfigurationShellVM.NavigateSubView(viewName);
                CurrentView = ConfigurationShellVM;
                break;
        }
    }

    [RelayCommand]
    public void OpenTursoSync()
    {
        NavigateTo("Config");
        ConfigurationShellVM.NavigateSubView("TursoSync");
    }

    [RelayCommand]
    public async Task SyncTursoNowAsync()
    {
        await _tursoSyncService.SyncAsync();
    }

    private void UpdateTursoStatus(TursoSyncStatus status, string message)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.InvokeAsync(() => UpdateTursoStatus(status, message));
                return;
            }
        }
        catch { }

        switch (status)
        {
            case TursoSyncStatus.NotConfigured:
                TursoStatusText = "Modo Local (SQLite)";
                TursoStatusColorHex = "#64748B"; // Gris pizarra
                TursoStatusToolTip = "Almacenamiento local SQLite (0 latencia). Clic para configurar Turso Cloud y sincronizar con Android.";
                break;
            case TursoSyncStatus.Idle:
                TursoStatusText = "Turso: Conectado";
                TursoStatusColorHex = "#0284C7"; // Azul cielo
                TursoStatusToolTip = $"Base de datos en la nube Turso activa.\n{message}";
                break;
            case TursoSyncStatus.Syncing:
                TursoStatusText = "Turso: Sincronizando...";
                TursoStatusColorHex = "#F59E0B"; // Ámbar
                TursoStatusToolTip = "Sincronizando cambios en segundo plano con Turso Cloud...";
                break;
            case TursoSyncStatus.Success:
                TursoStatusText = "Turso: Sincronizado";
                TursoStatusColorHex = "#10B981"; // Verde esmeralda
                TursoStatusToolTip = $"Datos actualizados en la nube Turso.\n{message}";
                break;
            case TursoSyncStatus.Offline:
            case TursoSyncStatus.Error:
                TursoStatusText = "Turso: Sin conexión";
                TursoStatusColorHex = "#EF4444"; // Rojo suave
                TursoStatusToolTip = $"Operando localmente sin riesgo de pérdida de datos.\n{message}";
                break;
        }
    }
}
