using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private readonly List<CommandPaletteItem> _staticCommandItems = new();

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _activeViewName = "MainHub";

    [ObservableProperty]
    private string _appTitle = "TPVC - Consultorio Médico";

    [ObservableProperty]
    private string _appVersion = "1.4.0";

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
    public DashboardViewModel DashboardVM { get; }
    public InventoryViewModel InventoryVM { get; }
    public BatchesViewModel BatchesVM { get; }
    public MovementsViewModel MovementsVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public MainViewModel(
        InventoryService inventoryService, 
        ConsultationService consultationService,
        ThemeService themeService, 
        AppUpdateService updateService)
    {
        _inventoryService = inventoryService;
        _consultationService = consultationService;
        _themeService = themeService;
        _updateService = updateService;

        AppVersion = $"v{_updateService.CurrentVersion.TrimStart('v', 'V')}";

        MainHubVM = new MainHubViewModel(NavigateTo);
        PosVM = new PosViewModel(inventoryService, consultationService);
        MedicalRecordsVM = new MedicalRecordsViewModel(inventoryService, consultationService, PosVM);
        _ = MedicalRecordsVM.LoadInitialDataAsync();
        _ = PosVM.InitializeAsync();

        // Enlace para volver a la Vista Principal desde el POS o Expedientes
        PosVM.OnBackToHubRequested = () => NavigateTo("MainHub");
        PosVM.OnCloseModalRequested = () => MedicalRecordsVM.ClosePosModal();
        MedicalRecordsVM.OnBackToHubRequested = () => NavigateTo("MainHub");

        // Enlace para enviar receta médica directamente al carrito de venta del POS
        MedicalRecordsVM.OnSendPrescriptionToPos = (medicine, qty) =>
        {
            PosVM.PatientName = MedicalRecordsVM.SelectedPatient?.FullName ?? "Paciente de Consulta";
            PosVM.AddPrescriptionItem(medicine, qty);
        };

        // Enlace para liquidar consulta activa en POS
        MedicalRecordsVM.OnCheckoutConsultationRequested = (consultation) =>
        {
            PosVM.IsConsultationCheckout = true;
            PosVM.CheckoutActiveConsultation();
            NavigateTo("Pos");
        };

        PosVM.OnSaleCompletedFromConsultation = () =>
        {
            MedicalRecordsVM.HandleSaleCompletedFromConsultation();
        };

        MedicalRecordsVM.OnNavigateToRecordsRequested = () =>
        {
            NavigateTo("Records");
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

        // Inicializar comandos estáticos y resultados de la paleta
        InitializeStaticCommands();
        UpdatePaletteResults(string.Empty);

        // Iniciar en la Vista Principal (las 3 cards: Nueva Venta, Nueva Consulta y Configuración)
        NavigateTo("MainHub");
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
            Title = "Expedientes y Consultas",
            Subtitle = "Historial clínico NOM-004-SSA3, signos vitales y recetas",
            Category = "Módulos",
            Icon = "📋",
            Action = () => NavigateTo("Records")
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
            Title = "Menú Principal (Hub)",
            Subtitle = "Pantalla inicial con accesos principales",
            Category = "Módulos",
            Icon = "🏠",
            Action = () => NavigateTo("MainHub")
        });

        // Acciones rápidas
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Nueva Consulta Médica",
            Subtitle = "Iniciar formulario de atención clínica NOM-004-SSA3",
            Category = "Acciones Rápidas",
            Icon = "🩺",
            Action = () =>
            {
                NavigateTo("Records");
                MedicalRecordsVM.NewConsultation();
            }
        });
        _staticCommandItems.Add(new CommandPaletteItem
        {
            Title = "Registrar Nuevo Paciente",
            Subtitle = "Abrir formulario para dar de alta un nuevo expediente clínico",
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
            var q = query.Trim().ToLowerInvariant();

            // 1. Comandos estáticos
            var matchedCommands = _staticCommandItems.Where(c =>
                c.Title.ToLowerInvariant().Contains(q) ||
                c.Subtitle.ToLowerInvariant().Contains(q) ||
                c.Category.ToLowerInvariant().Contains(q)
            );
            list.AddRange(matchedCommands);

            // 2. Pacientes
            var patients = MedicalRecordsVM.Patients
                .Where(p =>
                    p.FullName.ToLowerInvariant().Contains(q) ||
                    p.RecordNumber.ToLowerInvariant().Contains(q) ||
                    p.Phone.Contains(q) ||
                    (!string.IsNullOrEmpty(p.Email) && p.Email.ToLowerInvariant().Contains(q)) ||
                    (!string.IsNullOrEmpty(p.LastDiagnosis) && p.LastDiagnosis.ToLowerInvariant().Contains(q))
                )
                .Take(6);

            foreach (var p in patients)
            {
                var hasAllergies = !string.IsNullOrWhiteSpace(p.Allergies) &&
                                   !p.Allergies.Equals("Ninguna", StringComparison.OrdinalIgnoreCase) &&
                                   !p.Allergies.Equals("Ninguna conocida", StringComparison.OrdinalIgnoreCase);

                list.Add(new CommandPaletteItem
                {
                    Title = p.FullName,
                    Subtitle = $"Exp: {p.RecordNumber} • Tel: {p.FormattedPhone} • {p.Age} años • Diagnóstico: {p.LastDiagnosis}",
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
                .Where(p =>
                    p.Name.ToLowerInvariant().Contains(q) ||
                    (!string.IsNullOrEmpty(p.GenericName) && p.GenericName.ToLowerInvariant().Contains(q)) ||
                    p.Code.ToLowerInvariant().Contains(q) ||
                    (!string.IsNullOrEmpty(p.Location) && p.Location.ToLowerInvariant().Contains(q))
                )
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
                if (MedicalRecordsVM.SelectedPatient == null && !MedicalRecordsVM.IsSelectPatientModalOpen)
                {
                    MedicalRecordsVM.OpenSelectPatientModal();
                }
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
