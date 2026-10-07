using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// Configuración → Componentes: muestra viva del lenguaje visual común (colores, letra, botones,
/// campos, chips, distintivos, avisos, hojas e iconos) para revisar los temas en claro y oscuro.
/// </summary>
public partial class DesignSystemViewModel : ObservableObject
{
    private readonly ThemeService? _themeService;
    private readonly NotificationService? _notifications;

    public static readonly string[] ColorTokenKeys =
    {
        "ThemeBackground", "ThemeSurface", "ThemeSurfaceSubtle", "ThemeCard", "ThemeBorder", "ThemeBorderStrong",
        "ThemeText", "ThemeTextSecondary", "ThemeAccent", "ThemeAccentStrong", "ThemeAccentForeground", "ThemeFocus",
        "ThemeSelectionBackground", "ThemeSuccess", "ThemeWarning", "ThemeDanger",
        "ThemeSuccessSubtle", "ThemeWarningSubtle", "ThemeDangerSubtle", "ThemeInfoSubtle", "ThemeToastBackground"
    };

    public static readonly string[] IconKeys =
    {
        "IcoSearch", "IcoPlus", "IcoClose", "IcoChevronLeft", "IcoChevronRight", "IcoChevronDown", "IcoChevronUp",
        "IcoArrowLeft", "IcoArrowRight", "IcoMore", "IcoCheck", "IcoCalendar", "IcoClock", "IcoUser", "IcoUserPlus",
        "IcoEdit", "IcoTrash", "IcoPrint", "IcoAlert", "IcoInfo", "IcoPulse", "IcoPill", "IcoSyringe", "IcoClipboard",
        "IcoFile", "IcoHistory", "IcoChart", "IcoPhone", "IcoMessage", "IcoSend", "IcoLock", "IcoHome", "IcoSave",
        "IcoDroplet", "IcoFilter", "IcoRefresh", "IcoTablet", "IcoMonitor", "IcoCart", "IcoSun", "IcoMoon"
    };

    public DesignSystemViewModel(ThemeService? themeService = null, NotificationService? notifications = null)
    {
        _themeService = themeService;
        _notifications = notifications;
        ColorTokens = ColorTokenKeys.ToList();
    }

    /// <summary>Se reemplaza al cambiar de tema para que las muestras se vuelvan a pintar.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _colorTokens;

    public IReadOnlyList<string> Icons => IconKeys;

    public IReadOnlyList<string> SampleFilters { get; } = new[] { "Todo", "Consultas", "Signos", "Citas", "Recetas" };

    [ObservableProperty]
    private string _selectedFilter = "Todo";

    public IReadOnlyList<string> SampleOptions { get; } = new[]
    {
        "1 tableta cada 8 horas por 5 días",
        "1 tableta cada 12 horas por 7 días",
        "5 ml cada 8 horas si hay fiebre",
        "Aplicar en la zona afectada cada 12 horas",
        "1 cápsula al día en ayunas"
    };

    [ObservableProperty]
    private string? _selectedOption;

    [ObservableProperty]
    private string _sampleName = "María Fernanda López Ruiz";

    [ObservableProperty]
    private string _sampleEmpty = string.Empty;

    [ObservableProperty]
    private string _sampleNotes = string.Empty;

    [ObservableProperty]
    private bool _sampleChecked = true;

    [ObservableProperty]
    private bool _sampleOptionA = true;

    [ObservableProperty]
    private bool _isChartVisible;

    [ObservableProperty]
    private bool _isSheetOpen;

    [ObservableProperty]
    private int _openSection = 1;

    public bool IsDark => _themeService?.CurrentSettings.ThemeMode == "Oscuro";

    [RelayCommand]
    public void UseLightTheme() => SetMode("Claro");

    [RelayCommand]
    public void UseDarkTheme() => SetMode("Oscuro");

    private void SetMode(string mode)
    {
        _themeService?.SetMode(mode);
        ColorTokens = ColorTokenKeys.ToList();
        OnPropertyChanged(nameof(IsDark));
    }

    [RelayCommand]
    public void ShowToast(string? kind)
    {
        if (!Enum.TryParse<NotificationKind>(kind, out var parsed)) parsed = NotificationKind.Success;
        var message = parsed switch
        {
            NotificationKind.Info => "La tablet recibió la consulta",
            NotificationKind.Warning => "Sin conexión: la consulta sigue en este equipo",
            NotificationKind.Error => "No se pudo imprimir la receta. Revise la impresora.",
            _ => "Expediente guardado"
        };
        _notifications?.Show(message, parsed);
    }

    [RelayCommand]
    public void OpenSheet() => IsSheetOpen = true;

    [RelayCommand]
    public void CloseSheet() => IsSheetOpen = false;

    [RelayCommand]
    public void ToggleSection(string? section)
    {
        if (!int.TryParse(section, out var number)) return;
        OpenSection = OpenSection == number ? 0 : number;
    }
}
