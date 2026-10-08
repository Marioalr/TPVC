using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class AccentPaletteOption : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Hex { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Apariencia: modo claro/oscuro, color de acento y colores personalizados. La vista previa se
/// calcula con la misma paleta que la aplicación (<see cref="ThemePalette"/>) antes de aplicar, así
/// que muestra exactamente cómo quedará, incluido el contraste que se corrige solo.
/// </summary>
public partial class AppearanceViewModel : ObservableObject
{
    private readonly ThemeService _themeService;

    public AppearanceViewModel(ThemeService themeService)
    {
        _themeService = themeService;
        LoadCurrentThemeSettings();
        // Cambios hechos desde otra página (p. ej. Componentes): el resumen de Configuración no se queda atrás
        _themeService.ThemeChanged += () =>
        {
            CurrentMode = _themeService.CurrentSettings.ThemeMode;
            CurrentAccentHex = _themeService.CurrentSettings.AccentColor;
        };
    }

    public ObservableCollection<AccentPaletteOption> PaletteOptions { get; } = new()
    {
        new() { Name = "Azul consultorio", Hex = "#0369A1" },
        new() { Name = "Azul clínico", Hex = "#2563EB" },
        new() { Name = "Verde quirúrgico", Hex = "#0D9488" },
        new() { Name = "Esmeralda", Hex = "#059669" },
        new() { Name = "Violeta", Hex = "#7C3AED" },
        new() { Name = "Rosa", Hex = "#E11D48" },
        new() { Name = "Ámbar", Hex = "#D97706" },
        new() { Name = "Grafito", Hex = "#475569" }
    };

    [ObservableProperty]
    private string _currentMode = "Claro";

    [ObservableProperty]
    private string _currentAccentHex = "#0369A1";

    /// <summary>Lo que se escribe en "Personalizado" (todavía sin aplicar).</summary>
    [ObservableProperty]
    private string _customBgHex = "#F8FAFC";

    [ObservableProperty]
    private string _customSurfaceHex = "#FFFFFF";

    [ObservableProperty]
    private string _customAccentHex = "#0369A1";

    [ObservableProperty]
    private string? _customError;

    public bool IsCustomMode => CurrentMode == "Personalizado";

    public string Summary
    {
        get
        {
            var accent = PaletteOptions.FirstOrDefault(p => SameHex(p.Hex, CurrentAccentHex))?.Name ?? "acento propio";
            return $"{CurrentMode} · {accent}";
        }
    }

    public void LoadCurrentThemeSettings()
    {
        var settings = _themeService.CurrentSettings;
        CurrentMode = settings.ThemeMode;
        CurrentAccentHex = settings.AccentColor;
        CustomBgHex = settings.BackgroundColor;
        CustomSurfaceHex = settings.SurfaceColor;
        CustomAccentHex = settings.AccentColor;
        CustomError = null;
        RefreshSelection();
    }

    partial void OnCurrentModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomMode));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnCurrentAccentHexChanged(string value)
    {
        RefreshSelection();
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnCustomBgHexChanged(string value) => RefreshPreview();
    partial void OnCustomSurfaceHexChanged(string value) => RefreshPreview();
    partial void OnCustomAccentHexChanged(string value) => RefreshPreview();

    private void RefreshSelection()
    {
        foreach (var option in PaletteOptions) option.IsSelected = SameHex(option.Hex, CurrentAccentHex);
        RefreshPreview();
    }

    private static bool SameHex(string a, string b) =>
        string.Equals(a?.Trim().TrimStart('#'), b?.Trim().TrimStart('#'), System.StringComparison.OrdinalIgnoreCase);

    public static bool IsValidHex(string? hex)
    {
        var text = hex?.Trim().TrimStart('#') ?? string.Empty;
        return text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    }

    [RelayCommand]
    public void SelectMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        _themeService.SetMode(mode);
        LoadCurrentThemeSettings();
    }

    [RelayCommand]
    public void SelectAccent(string? hex)
    {
        if (!IsValidHex(hex)) return;
        _themeService.SetAccent(hex!);
        CurrentAccentHex = hex!;
        CustomAccentHex = hex!;
    }

    /// <summary>Aplica los colores escritos en "Personalizado" (fondo, superficie y acento).</summary>
    [RelayCommand]
    public void ApplyCustomColors()
    {
        if (!IsValidHex(CustomBgHex) || !IsValidHex(CustomSurfaceHex) || !IsValidHex(CustomAccentHex))
        {
            CustomError = "Escriba los colores como #RRGGBB (por ejemplo #F8FAFC).";
            return;
        }

        // Un fondo oscuro con tarjetas claras (o al revés) deja textos y avisos sin contraste en uno de los dos
        bool darkBackground = ThemeColor.Parse(Normalize(CustomBgHex), ThemePalette.White).Luminance < 0.2;
        bool darkSurface = ThemeColor.Parse(Normalize(CustomSurfaceHex), ThemePalette.White).Luminance < 0.2;
        if (darkBackground != darkSurface)
        {
            CustomError = "El fondo y las tarjetas deben ser los dos claros o los dos oscuros.";
            return;
        }

        CustomError = null;
        var accent = Normalize(CustomAccentHex);
        _themeService.SetCustomBackground(Normalize(CustomBgHex), Normalize(CustomSurfaceHex));
        _themeService.SetAccent(accent);
        LoadCurrentThemeSettings();
    }

    private static string Normalize(string hex) => "#" + hex.Trim().TrimStart('#').ToUpperInvariant();

    // ================================================================== vista previa

    [ObservableProperty]
    private ThemePreview _preview = ThemePreview.From(new ThemeSettings());

    /// <summary>La vista previa usa lo escrito en "Personalizado" (o el tema actual si no es válido).</summary>
    private void RefreshPreview()
    {
        var current = _themeService.CurrentSettings;
        // Lo mismo que hará "Aplicar colores" (modo personalizado con texto y bordes del lado del fondo)
        var pending = ThemeService.WithCustomColors(
            new ThemeSettings { AccentColor = IsValidHex(CustomAccentHex) ? Normalize(CustomAccentHex) : current.AccentColor },
            IsValidHex(CustomBgHex) ? Normalize(CustomBgHex) : current.BackgroundColor,
            IsValidHex(CustomSurfaceHex) ? Normalize(CustomSurfaceHex) : current.SurfaceColor);
        Preview = ThemePreview.From(pending);
    }
}

/// <summary>Colores de la vista previa (hex) y el contraste del texto, calculados por la paleta.</summary>
public sealed record ThemePreview(
    string Background, string Surface, string Subtle, string Text, string TextSecondary, string Border,
    string Accent, string AccentText, string AccentForeground, string InfoSubtle, string InfoText,
    string DangerSubtle, string DangerText, double TextContrast, double ButtonContrast)
{
    public string ContrastText => $"Texto {TextContrast:0.0}:1 · botón {ButtonContrast:0.0}:1";

    public static ThemePreview From(ThemeSettings settings)
    {
        var p = ThemePalette.Build(settings);
        string Hex(string key) => p[key].ToHex();
        return new ThemePreview(
            Hex("ThemeBackground"), Hex("ThemeSurface"), Hex("ThemeSurfaceSubtle"), Hex("ThemeText"), Hex("ThemeTextSecondary"),
            Hex("ThemeBorder"), Hex("ThemeAccentStrong"), Hex("ThemeAccentText"), Hex("ThemeAccentForeground"),
            Hex("ThemeInfoSubtle"), Hex("ThemeInfoText"), Hex("ThemeDangerSubtle"), Hex("ThemeDangerText"),
            ThemeColor.Contrast(p["ThemeText"], p["ThemeSurface"]),
            ThemeColor.Contrast(p["ThemeAccentText"], p["ThemeAccentStrong"]));
    }
}
