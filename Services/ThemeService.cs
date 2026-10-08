using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using TPVC.Models;

namespace TPVC.Services;

public class ThemeService
{
    private static readonly string DefaultSettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TPVC",
        "theme.json"
    );

    private readonly string SettingsFile;

    /// <param name="settingsFile">Dónde guardar el tema (las pruebas usan una carpeta temporal).</param>
    public ThemeService(string? settingsFile = null)
    {
        SettingsFile = settingsFile ?? DefaultSettingsFile;
    }

    public ThemeSettings CurrentSettings { get; private set; } = new();

    public event Action? ThemeChanged;

    public void LoadTheme()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<ThemeSettings>(json);
                if (loaded != null)
                {
                    CurrentSettings = loaded;
                }
            }
        }
        catch
        {
            CurrentSettings = new ThemeSettings();
        }

        ApplyTheme(CurrentSettings);
    }

    public void SaveTheme(ThemeSettings settings)
    {
        CurrentSettings = settings;
        ApplyTheme(settings);

        try
        {
            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Silencioso en caso de error de escritura temporal
        }

        ThemeChanged?.Invoke();
    }

    public void SetMode(string mode)
    {
        CurrentSettings.ThemeMode = mode;
        if (mode == "Claro")
        {
            CurrentSettings.BackgroundColor = "#F8FAFC";
            CurrentSettings.SurfaceColor = "#FFFFFF";
            CurrentSettings.TextColor = "#0F172A";
            CurrentSettings.SecondaryTextColor = "#64748B";
            CurrentSettings.BorderColor = "#E2E8F0";
        }
        else if (mode == "Oscuro")
        {
            CurrentSettings.BackgroundColor = "#0F172A";
            CurrentSettings.SurfaceColor = "#1E293B";
            CurrentSettings.TextColor = "#F8FAFC";
            CurrentSettings.SecondaryTextColor = "#94A3B8";
            CurrentSettings.BorderColor = "#334155";
        }
        SaveTheme(CurrentSettings);
    }

    public void SetAccent(string accentHex)
    {
        CurrentSettings.AccentColor = accentHex;
        SaveTheme(CurrentSettings);
    }

    public void SetCustomBackground(string bgHex, string surfaceHex)
    {
        WithCustomColors(CurrentSettings, bgHex, surfaceHex);
        SaveTheme(CurrentSettings);
    }

    /// <summary>
    /// Colores propios: el texto y los bordes se toman del lado (claro u oscuro) del fondo nuevo, no
    /// del modo anterior. La vista previa de Apariencia usa lo mismo para mostrar exactamente el resultado.
    /// </summary>
    public static ThemeSettings WithCustomColors(ThemeSettings settings, string bgHex, string surfaceHex)
    {
        settings.ThemeMode = "Personalizado";
        settings.BackgroundColor = bgHex;
        settings.SurfaceColor = surfaceHex;
        bool dark = ThemePalette.IsDark(settings);
        settings.TextColor = dark ? "#F8FAFC" : "#0F172A";
        settings.SecondaryTextColor = dark ? "#94A3B8" : "#64748B";
        settings.BorderColor = dark ? "#334155" : "#E2E8F0";
        return settings;
    }

    /// <summary>
    /// Publica la paleta completa (ver <see cref="ThemePalette"/>) como pinceles congelados: las
    /// pantallas usan solo estos colores con DynamicResource y cambian al instante con el tema.
    /// </summary>
    public void ApplyTheme(ThemeSettings settings)
    {
        if (Application.Current == null) return;

        var resources = Application.Current.Resources;
        foreach (var (key, color) in ThemePalette.Build(settings))
        {
            var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();
            resources[key] = brush;
        }
    }
}
