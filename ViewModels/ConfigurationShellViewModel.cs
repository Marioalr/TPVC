using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>Sección del menú de Configuración con su resumen de una línea.</summary>
public sealed partial class ConfigSectionItem : ObservableObject
{
    public ConfigSectionItem(string key, string group, string title, string iconKey)
    {
        Key = key;
        Group = group;
        Title = title;
        IconKey = iconKey;
    }

    public string Key { get; }
    public string Group { get; }
    public string Title { get; }

    /// <summary>Clave del icono en DesignSystem.xaml (Ico…).</summary>
    public string IconKey { get; }

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>Grupo del menú (Resumen, Inventario, Consultorio, Sistema).</summary>
public sealed record ConfigSectionGroup(string Name, IReadOnlyList<ConfigSectionItem> Items);

/// <summary>Un ajuste concreto que se encuentra escribiendo ("modo oscuro", "cédula"…).</summary>
public sealed record SettingEntry(string Title, string Description, string SectionKey, string Keywords = "");

/// <summary>
/// Configuración: secciones agrupadas (Resumen, Inventario, Consultorio, Sistema), cada una con un
/// resumen de una línea, y un buscador que lleva directo a cualquier ajuste.
/// </summary>
public partial class ConfigurationShellViewModel : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");
    private readonly Action<string> _navigateMain;

    public ConfigurationShellViewModel(
        Action<string> navigateMain,
        DashboardViewModel dashboardVM,
        InventoryViewModel inventoryVM,
        BatchesViewModel batchesVM,
        MovementsViewModel movementsVM,
        ClinicProfileViewModel clinicProfileVM,
        AppearanceViewModel appearanceVM,
        DataManagementViewModel dataVM,
        UpdatesViewModel updatesVM,
        TursoSyncViewModel tursoSyncVM,
        ServicesCatalogViewModel servicesVM,
        string appVersion,
        DesignSystemViewModel? designSystemVM = null)
    {
        _navigateMain = navigateMain;
        DashboardVM = dashboardVM;
        InventoryVM = inventoryVM;
        BatchesVM = batchesVM;
        MovementsVM = movementsVM;
        ClinicProfileVM = clinicProfileVM;
        AppearanceVM = appearanceVM;
        DataVM = dataVM;
        UpdatesVM = updatesVM;
        TursoSyncVM = tursoSyncVM;
        ServicesVM = servicesVM;
        DesignSystemVM = designSystemVM ?? new DesignSystemViewModel();
        AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "v1.20.1" : appVersion;

        Sections = new ObservableCollection<ConfigSectionItem>(new[]
        {
            new ConfigSectionItem(SectionDashboard, "Resumen", "Panel del consultorio", "IcoChart"),
            new ConfigSectionItem(SectionInventory, "Inventario", "Productos", "IcoPill"),
            new ConfigSectionItem(SectionBatches, "Inventario", "Lotes y caducidades", "IcoCalendar"),
            new ConfigSectionItem(SectionMovements, "Inventario", "Kardex", "IcoHistory"),
            new ConfigSectionItem(SectionServices, "Consultorio", "Servicios", "IcoSyringe"),
            new ConfigSectionItem(SectionClinic, "Consultorio", "Médico y consultorio", "IcoUser"),
            new ConfigSectionItem(SectionAppearance, "Sistema", "Apariencia", "IcoSun"),
            new ConfigSectionItem(SectionData, "Sistema", "Respaldos y datos", "IcoSave"),
            new ConfigSectionItem(SectionCloud, "Sistema", "Nube y dispositivos", "IcoTablet"),
            new ConfigSectionItem(SectionUpdates, "Sistema", "Actualizaciones", "IcoRefresh"),
            new ConfigSectionItem(SectionDesignSystem, "Sistema", "Componentes", "IcoInfo")
        });

        SectionGroups = Sections
            .GroupBy(s => s.Group)
            .Select(g => new ConfigSectionGroup(g.Key, g.ToList()))
            .ToList();

        // Los resúmenes siguen a su sección sin esperar a cambiar de página (por ejemplo, el
        // "Último respaldo" al pulsar "Respaldar ahora")
        FollowSummary(ServicesVM, nameof(ServicesCatalogViewModel.Summary), SectionServices, () => ServicesVM.Summary);
        FollowSummary(ClinicProfileVM, nameof(ClinicProfileViewModel.Summary), SectionClinic, () => ClinicProfileVM.Summary);
        FollowSummary(AppearanceVM, nameof(AppearanceViewModel.Summary), SectionAppearance, () => AppearanceVM.Summary);
        FollowSummary(DataVM, nameof(DataManagementViewModel.LastBackupText), SectionData, () => DataVM.LastBackupText);
        FollowSummary(TursoSyncVM, nameof(TursoSyncViewModel.StatusMessage), SectionCloud, () => TursoSyncVM.StatusMessage);

        // Iniciar en el panel del consultorio
        NavigateSubView(SectionDashboard);
    }

    public const string SectionDashboard = "Dashboard";
    public const string SectionInventory = "Inventory";
    public const string SectionBatches = "Batches";
    public const string SectionMovements = "Movements";
    public const string SectionServices = "Services";
    public const string SectionClinic = "ClinicProfile";
    public const string SectionAppearance = "Appearance";
    public const string SectionData = "Data";
    public const string SectionCloud = "TursoSync";
    public const string SectionUpdates = "Updates";
    public const string SectionDesignSystem = "DesignSystem";

    public DashboardViewModel DashboardVM { get; }
    public InventoryViewModel InventoryVM { get; }
    public BatchesViewModel BatchesVM { get; }
    public MovementsViewModel MovementsVM { get; }
    public ClinicProfileViewModel ClinicProfileVM { get; }
    public AppearanceViewModel AppearanceVM { get; }
    public DataManagementViewModel DataVM { get; }
    public UpdatesViewModel UpdatesVM { get; }
    public TursoSyncViewModel TursoSyncVM { get; }
    public ServicesCatalogViewModel ServicesVM { get; }
    public DesignSystemViewModel DesignSystemVM { get; }

    public ObservableCollection<ConfigSectionItem> Sections { get; }

    public IReadOnlyList<ConfigSectionGroup> SectionGroups { get; }

    [ObservableProperty]
    private object? _currentSubView;

    [ObservableProperty]
    private string _activeSubViewName = SectionDashboard;

    [ObservableProperty]
    private string _appVersion = "v1.20.1";

    /// <summary>Nombres anteriores (paleta de comandos, accesos del inicio) → sección actual.</summary>
    public static string NormalizeSection(string? viewName) => viewName switch
    {
        "Settings" => SectionAppearance,
        "Backups" => SectionData,
        _ => string.IsNullOrWhiteSpace(viewName) ? SectionDashboard : viewName
    };

    [RelayCommand]
    public void NavigateSubView(string viewName)
    {
        viewName = NormalizeSection(viewName);
        ActiveSubViewName = viewName;
        foreach (var section in Sections) section.IsActive = section.Key == viewName;

        switch (viewName)
        {
            case SectionDashboard:
                CurrentSubView = DashboardVM;
                _ = DashboardVM.RefreshAsync();
                break;
            case SectionInventory:
                CurrentSubView = InventoryVM;
                _ = InventoryVM.LoadDataAsync();
                break;
            case SectionBatches:
                CurrentSubView = BatchesVM;
                _ = BatchesVM.LoadBatchesAsync();
                break;
            case SectionMovements:
                CurrentSubView = MovementsVM;
                _ = MovementsVM.LoadMovementsAsync();
                break;
            case SectionServices:
                CurrentSubView = ServicesVM;
                _ = ServicesVM.LoadAsync();
                break;
            case SectionClinic:
                CurrentSubView = ClinicProfileVM;
                ClinicProfileVM.LoadFromProfile();
                break;
            case SectionAppearance:
                CurrentSubView = AppearanceVM;
                AppearanceVM.LoadCurrentThemeSettings();
                break;
            case SectionData:
                CurrentSubView = DataVM;
                DataVM.RefreshBackups();
                break;
            case SectionCloud:
                CurrentSubView = TursoSyncVM;
                TursoSyncVM.LoadFromSettings();
                break;
            case SectionUpdates:
                CurrentSubView = UpdatesVM;
                break;
            case SectionDesignSystem:
                CurrentSubView = DesignSystemVM;
                break;
        }

        SettingsSearchText = string.Empty;
        _ = RefreshSummariesAsync();
    }

    [RelayCommand]
    public void BackToHub() => _navigateMain("MainHub");

    // ================================================================== resúmenes

    /// <summary>Una línea por sección con lo que importa de un vistazo (existencias, último respaldo…).</summary>
    public async Task RefreshSummariesAsync()
    {
        try
        {
            if (DashboardVM.Metrics.TotalProducts == 0) await DashboardVM.RefreshAsync();
            if (!ServicesVM.IsLoaded) await ServicesVM.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron calcular los resúmenes de Configuración", ex);
        }

        var m = DashboardVM.Metrics;
        SetSummary(SectionDashboard, $"Ventas de hoy {m.SalesTodayTotal.ToString("C0", Mexican)}");
        SetSummary(SectionInventory, m.LowStockCount > 0
            ? $"{m.TotalProducts} productos · {m.LowStockCount} con stock bajo"
            : $"{m.TotalProducts} productos");
        SetSummary(SectionBatches, m.ExpiredCount > 0
            ? $"{m.ExpiredCount} vencidos · {m.ExpiringSoonCount} por vencer"
            : m.ExpiringSoonCount > 0 ? $"{m.ExpiringSoonCount} por vencer en 60 días" : "Sin lotes por vencer");
        SetSummary(SectionMovements, m.MovementsToday == 1 ? "1 movimiento hoy" : $"{m.MovementsToday} movimientos hoy");
        SetSummary(SectionServices, ServicesVM.Summary);
        SetSummary(SectionClinic, ClinicProfileVM.Summary);
        SetSummary(SectionAppearance, AppearanceVM.Summary);
        SetSummary(SectionData, DataVM.LastBackupText);
        SetSummary(SectionCloud, TursoSyncVM.StatusMessage);
        SetSummary(SectionUpdates, $"Versión {AppVersion.TrimStart('v', 'V')}");
        SetSummary(SectionDesignSystem, "Muestra de botones, campos y avisos");
    }

    private void FollowSummary(System.ComponentModel.INotifyPropertyChanged source, string property, string key, Func<string> read) =>
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == property) SetSummary(key, read());
        };

    private void SetSummary(string key, string summary)
    {
        var section = Sections.FirstOrDefault(s => s.Key == key);
        if (section != null) section.Summary = summary;
    }

    // ================================================================== buscador de ajustes

    public static IReadOnlyList<SettingEntry> SettingsIndex { get; } = new[]
    {
        new SettingEntry("Ventas y métricas de hoy", "Panel del consultorio", SectionDashboard, "resumen ventas consumo alertas"),
        new SettingEntry("Nuevo producto o medicamento", "Productos", SectionInventory, "alta catalogo farmaco insumo"),
        new SettingEntry("Precios y existencias", "Productos", SectionInventory, "precio stock minimo ubicacion refrigeracion"),
        new SettingEntry("Productos con stock bajo", "Productos", SectionInventory, "reabastecer faltante"),
        new SettingEntry("Registrar un lote", "Lotes y caducidades", SectionBatches, "lote caducidad vencimiento fefo entrada"),
        new SettingEntry("Lotes vencidos o por vencer", "Lotes y caducidades", SectionBatches, "caducados caducidad"),
        new SettingEntry("Entradas, salidas y ajustes", "Kardex", SectionMovements, "movimientos historial auditoria"),
        new SettingEntry("Servicios y honorarios", "Servicios", SectionServices, "consulta curacion inyeccion sutura precio honorario"),
        new SettingEntry("Insumos que usa un servicio", "Servicios", SectionServices, "paquete material servicio"),
        new SettingEntry("Nombre del médico y cédula", "Médico y consultorio", SectionClinic, "doctor cedula profesional especialidad universidad"),
        new SettingEntry("Dirección y teléfono del consultorio", "Médico y consultorio", SectionClinic, "domicilio telefono correo rfc receta ticket"),
        new SettingEntry("Modo claro u oscuro", "Apariencia", SectionAppearance, "tema noche oscuro claro"),
        new SettingEntry("Color de acento", "Apariencia", SectionAppearance, "color tema botones"),
        new SettingEntry("Colores personalizados", "Apariencia", SectionAppearance, "fondo superficie hex"),
        new SettingEntry("Respaldar ahora", "Respaldos y datos", SectionData, "respaldo copia seguridad backup"),
        new SettingEntry("Restaurar un respaldo", "Respaldos y datos", SectionData, "recuperar copia anterior backup"),
        new SettingEntry("Importar catálogo de Excel", "Respaldos y datos", SectionData, "excel xlsx csv receta medicamentos importar"),
        new SettingEntry("Restaurar base de datos (.db)", "Respaldos y datos", SectionData, "sqlite db reemplazar base"),
        new SettingEntry("Sincronización con la nube (Turso)", "Nube y dispositivos", SectionCloud, "turso nube sincronizar token url"),
        new SettingEntry("Vincular tablet o celular", "Nube y dispositivos", SectionCloud, "android tablet celular emparejar"),
        new SettingEntry("Nombre de este equipo", "Nube y dispositivos", SectionCloud, "dispositivo rol"),
        new SettingEntry("Buscar actualizaciones", "Actualizaciones", SectionUpdates, "version nueva descargar instalar"),
        new SettingEntry("Token de GitHub", "Actualizaciones", SectionUpdates, "repositorio privado token"),
        new SettingEntry("Carpeta de registros (logs)", "Actualizaciones", SectionUpdates, "errores diagnostico log"),
        new SettingEntry("Componentes de diseño", "Componentes", SectionDesignSystem, "muestra botones colores")
    };

    [ObservableProperty]
    private string _settingsSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SettingEntry> _settingsSearchResults = new();

    public bool IsSearchingSettings => !string.IsNullOrWhiteSpace(SettingsSearchText);
    public bool HasNoSettingsResults => IsSearchingSettings && SettingsSearchResults.Count == 0;

    partial void OnSettingsSearchTextChanged(string value)
    {
        SettingsSearchResults = new ObservableCollection<SettingEntry>(SearchSettings(value));
        OnPropertyChanged(nameof(IsSearchingSettings));
        OnPropertyChanged(nameof(HasNoSettingsResults));
    }

    /// <summary>Ajustes que coinciden (sin acentos): primero por título, luego por descripción o palabras clave.</summary>
    public static List<SettingEntry> SearchSettings(string? query)
    {
        var q = TextSearch.Normalize(query);
        if (q.Length == 0) return new List<SettingEntry>();
        return SettingsIndex
            .Select(e => (Entry: e, Rank: TextSearch.Normalize(e.Title).Contains(q, StringComparison.Ordinal) ? 0
                : TextSearch.Matches(q, e.Description, e.Keywords) ? 1 : -1))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .Select(x => x.Entry)
            .ToList();
    }

    [RelayCommand]
    public void OpenSetting(SettingEntry? entry)
    {
        if (entry != null) NavigateSubView(entry.SectionKey);
    }

    [RelayCommand]
    public void OpenFirstSetting() => OpenSetting(SettingsSearchResults.FirstOrDefault());
}
