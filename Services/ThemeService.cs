using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using TPVC.Models;

namespace TPVC.Services;

public class ThemeService
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TPVC",
        "theme.json"
    );

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
        CurrentSettings.ThemeMode = "Personalizado";
        CurrentSettings.BackgroundColor = bgHex;
        CurrentSettings.SurfaceColor = surfaceHex;
        SaveTheme(CurrentSettings);
    }

    public void ApplyTheme(ThemeSettings settings)
    {
        if (Application.Current == null) return;

        var resources = Application.Current.Resources;

        var bg = ParseColor(settings.BackgroundColor, Colors.White);
        var surface = ParseColor(settings.SurfaceColor, Colors.White);
        var text = ParseColor(settings.TextColor, Colors.Black);
        var secText = ParseColor(settings.SecondaryTextColor, Colors.Gray);
        var border = ParseColor(settings.BorderColor, Color.FromRgb(226, 232, 240));
        var accent = ParseColor(settings.AccentColor, Color.FromRgb(2, 132, 199));

        // Hover & Pressed accents
        var accentHover = ChangeColorBrightness(accent, 0.15f);
        var accentPressed = ChangeColorBrightness(accent, -0.15f);

        // Surface subtle (zebra rows, toolbars)
        var surfaceSubtle = settings.ThemeMode == "Oscuro" 
            ? Color.FromRgb(30, 41, 59) 
            : Color.FromRgb(241, 245, 249);

        // Set DynamicResource Brushes
        resources["ThemeBackground"] = new SolidColorBrush(bg);
        resources["ThemeSurface"] = new SolidColorBrush(surface);
        resources["ThemeSurfaceSubtle"] = new SolidColorBrush(surfaceSubtle);
        resources["ThemeText"] = new SolidColorBrush(text);
        resources["ThemeTextSecondary"] = new SolidColorBrush(secText);
        resources["ThemeBorder"] = new SolidColorBrush(border);
        resources["ThemeAccent"] = new SolidColorBrush(accent);
        resources["ThemeAccentHover"] = new SolidColorBrush(accentHover);
        resources["ThemeAccentPressed"] = new SolidColorBrush(accentPressed);
        resources["ThemeAccentText"] = new SolidColorBrush(Colors.White);

        resources["ThemeCard"] = new SolidColorBrush(surface);

        // Selección y acento secundario: siguen al acento y al modo (en oscuro, el azul claro fijo
        // dejaba texto claro sobre fondo claro)
        bool isDark = settings.ThemeMode == "Oscuro" || Luminance(bg) < 0.4;
        resources["ThemePrimary"] = new SolidColorBrush(accent);
        resources["ThemePrimaryTransparent"] = new SolidColorBrush(Color.FromArgb(0x15, accent.R, accent.G, accent.B));
        resources["ThemeSelectionBackground"] = new SolidColorBrush(Blend(surface, accent, isDark ? 0.35 : 0.14));

        // Semaphores / Statuses
        var danger = Color.FromRgb(239, 68, 68);
        resources["ThemeSuccess"] = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        resources["ThemeWarning"] = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        resources["ThemeDanger"] = new SolidColorBrush(danger);
        resources["ThemeError"] = new SolidColorBrush(danger);
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Blend(Color baseColor, Color overlay, double amount) => Color.FromRgb(
        (byte)Math.Round(baseColor.R + (overlay.R - baseColor.R) * amount),
        (byte)Math.Round(baseColor.G + (overlay.G - baseColor.G) * amount),
        (byte)Math.Round(baseColor.B + (overlay.B - baseColor.B) * amount));

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            var obj = ColorConverter.ConvertFromString(hex);
            return obj is Color c ? c : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Color ChangeColorBrightness(Color color, float factor)
    {
        float r = color.R;
        float g = color.G;
        float b = color.B;

        if (factor > 0)
        {
            r += (255 - r) * factor;
            g += (255 - g) * factor;
            b += (255 - b) * factor;
        }
        else
        {
            r += r * factor;
            g += g * factor;
            b += b * factor;
        }

        return Color.FromArgb(color.A, (byte)Math.Clamp((int)r, 0, 255), (byte)Math.Clamp((int)g, 0, 255), (byte)Math.Clamp((int)b, 0, 255));
    }
}
