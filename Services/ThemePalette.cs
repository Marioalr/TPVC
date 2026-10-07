using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>Color ARGB sin dependencias de WPF (se puede probar en cualquier sistema).</summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    public static ThemeColor Rgb(byte r, byte g, byte b) => new(255, r, g, b);

    public static ThemeColor Parse(string? hex, ThemeColor fallback)
    {
        var text = hex?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(text)) return fallback;
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return fallback;
        return text.Length switch
        {
            6 => new ThemeColor(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value),
            8 => new ThemeColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value),
            _ => fallback
        };
    }

    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public ThemeColor WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>Luminancia relativa (WCAG 2.x).</summary>
    public double Luminance
    {
        get
        {
            static double Channel(byte c)
            {
                var s = c / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
        }
    }

    public static double Contrast(ThemeColor a, ThemeColor b)
    {
        double la = a.Luminance, lb = b.Luminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static ThemeColor Blend(ThemeColor from, ThemeColor to, double amount) => Rgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}

/// <summary>
/// Paleta completa de un tema a partir de la configuración guardada. Todo color de la interfaz sale
/// de aquí: los que llevan texto encima se ajustan solos hasta tener contraste suficiente (4.5:1),
/// así un acento personalizado claro o el modo oscuro nunca dejan texto ilegible.
/// </summary>
public static class ThemePalette
{
    public static readonly ThemeColor White = ThemeColor.Rgb(255, 255, 255);
    public static readonly ThemeColor Black = ThemeColor.Rgb(0, 0, 0);
    public const double TextContrast = 4.5;
    public const double FocusContrast = 3.0;

    private static readonly ThemeColor DefaultLightSurface = ThemeColor.Rgb(0xFF, 0xFF, 0xFF);
    private static readonly ThemeColor DefaultDarkSurface = ThemeColor.Rgb(0x1E, 0x29, 0x3B);

    public static bool IsDark(ThemeSettings settings) =>
        settings.ThemeMode == "Oscuro" || ThemeColor.Parse(settings.BackgroundColor, White).Luminance < 0.2;

    public static IReadOnlyDictionary<string, ThemeColor> Build(ThemeSettings settings)
    {
        bool dark = IsDark(settings);
        var bg = ThemeColor.Parse(settings.BackgroundColor, dark ? ThemeColor.Rgb(0x0F, 0x17, 0x2A) : ThemeColor.Rgb(0xF8, 0xFA, 0xFC));
        var surface = ThemeColor.Parse(settings.SurfaceColor, dark ? DefaultDarkSurface : DefaultLightSurface);
        var accent = ThemeColor.Parse(settings.AccentColor, ThemeColor.Rgb(0x03, 0x69, 0xA1));

        // Gris suave: en oscuro antes era igual a la superficie y filas/chips no se distinguían
        var subtle = surface == DefaultLightSurface && !dark ? ThemeColor.Rgb(0xF1, 0xF5, 0xF9)
            : surface == DefaultDarkSurface && dark ? ThemeColor.Rgb(0x27, 0x34, 0x49)
            : ThemeColor.Blend(surface, dark ? White : Black, dark ? 0.06 : 0.04);

        var selection = ThemeColor.Blend(surface, accent, dark ? 0.35 : 0.14);
        var textSurfaces = new[] { bg, surface, subtle, selection };

        var text = EnsureContrast(ThemeColor.Parse(settings.TextColor, dark ? ThemeColor.Rgb(0xF8, 0xFA, 0xFC) : ThemeColor.Rgb(0x0F, 0x17, 0x2A)), textSurfaces, TextContrast);
        var textSecondary = EnsureContrast(ThemeColor.Parse(settings.SecondaryTextColor, dark ? ThemeColor.Rgb(0x94, 0xA3, 0xB8) : ThemeColor.Rgb(0x64, 0x74, 0x8B)), textSurfaces, TextContrast);
        var border = ThemeColor.Parse(settings.BorderColor, dark ? ThemeColor.Rgb(0x33, 0x41, 0x55) : ThemeColor.Rgb(0xE2, 0xE8, 0xF0));
        var borderStrong = ThemeColor.Blend(border, text, dark ? 0.18 : 0.14);

        // Botón principal y seleccionados: texto blanco encima → el acento se oscurece si hace falta
        var accentStrong = EnsureContrast(accent, new[] { White }, TextContrast);
        // Texto, iconos y enlaces del color del acento sobre la superficie
        var accentForeground = EnsureContrast(accent, new[] { surface, bg, subtle }, TextContrast);

        // Series de la gráfica de signos: se distinguen entre sí y contrastan 3:1 con la tarjeta y
        // el gris suave en los dos modos (antes eran fijas y el verde quedaba en 2.5:1 sobre blanco)
        var chartSurfaces = new[] { surface, subtle };
        ThemeColor Series(ThemeColor light, ThemeColor darkColor) => EnsureContrast(dark ? darkColor : light, chartSurfaces, FocusContrast);

        var success = ThemeColor.Rgb(0x04, 0x78, 0x57);
        var warning = ThemeColor.Rgb(0xB4, 0x53, 0x09);
        var danger = ThemeColor.Rgb(0xB9, 0x1C, 0x1C);

        var palette = new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
            ["ThemeBackground"] = bg,
            ["ThemeSurface"] = surface,
            ["ThemeCard"] = surface,
            ["ThemeSurfaceSubtle"] = subtle,
            ["ThemeText"] = text,
            ["ThemeTextSecondary"] = textSecondary,
            ["ThemeBorder"] = border,
            ["ThemeBorderStrong"] = borderStrong,
            ["ThemeAccent"] = accent,
            ["ThemeAccentStrong"] = accentStrong,
            ["ThemeAccentHover"] = ThemeColor.Blend(accentStrong, Black, 0.12),
            ["ThemeAccentPressed"] = ThemeColor.Blend(accentStrong, Black, 0.24),
            ["ThemeAccentText"] = White,
            ["ThemeAccentForeground"] = accentForeground,
            ["ThemeFocus"] = EnsureContrast(accent, new[] { surface, bg }, FocusContrast),
            ["ThemePrimary"] = accentStrong,
            ["ThemePrimaryTransparent"] = accent.WithAlpha(0x15),
            ["ThemeSelectionBackground"] = selection,
            ["ThemeOverlay"] = dark ? Black.WithAlpha(0x99) : ThemeColor.Rgb(0x0F, 0x17, 0x2A).WithAlpha(0x73),

            // Sólidos: llevan texto blanco encima (≥ 4.5:1)
            ["ThemeSuccess"] = success,
            ["ThemeWarning"] = warning,
            ["ThemeDanger"] = danger,
            ["ThemeError"] = danger,

            // Cajas de aviso y distintivos: fondo suave + texto del mismo tono, legibles en ambos modos
            ["ThemeSuccessSubtle"] = dark ? ThemeColor.Rgb(0x14, 0x53, 0x2D) : ThemeColor.Rgb(0xDC, 0xFC, 0xE7),
            ["ThemeSuccessText"] = dark ? ThemeColor.Rgb(0xDC, 0xFC, 0xE7) : ThemeColor.Rgb(0x16, 0x65, 0x34),
            ["ThemeWarningSubtle"] = dark ? ThemeColor.Rgb(0x78, 0x35, 0x0F) : ThemeColor.Rgb(0xFE, 0xF3, 0xC7),
            ["ThemeWarningText"] = dark ? ThemeColor.Rgb(0xFE, 0xF3, 0xC7) : ThemeColor.Rgb(0x78, 0x35, 0x0F),
            ["ThemeDangerSubtle"] = dark ? ThemeColor.Rgb(0x7F, 0x1D, 0x1D) : ThemeColor.Rgb(0xFE, 0xE2, 0xE2),
            ["ThemeDangerText"] = dark ? ThemeColor.Rgb(0xFE, 0xE2, 0xE2) : ThemeColor.Rgb(0x99, 0x1B, 0x1B),
            ["ThemeInfoSubtle"] = dark ? ThemeColor.Rgb(0x0C, 0x4A, 0x6E) : ThemeColor.Rgb(0xE0, 0xF2, 0xFE),
            ["ThemeInfoText"] = dark ? ThemeColor.Rgb(0xE0, 0xF2, 0xFE) : ThemeColor.Rgb(0x07, 0x59, 0x85),

            ["ThemeChartWeight"] = Series(ThemeColor.Rgb(0x25, 0x63, 0xEB), ThemeColor.Rgb(0x60, 0xA5, 0xFA)),
            ["ThemeChartSystolic"] = Series(ThemeColor.Rgb(0xDC, 0x26, 0x26), ThemeColor.Rgb(0xF8, 0x71, 0x71)),
            ["ThemeChartDiastolic"] = Series(ThemeColor.Rgb(0xB4, 0x53, 0x09), ThemeColor.Rgb(0xFB, 0xBF, 0x24)),
            ["ThemeChartGlucose"] = Series(ThemeColor.Rgb(0x04, 0x78, 0x57), ThemeColor.Rgb(0x34, 0xD3, 0x99)),

            // Avisos breves (toast): contrastan con todo lo que hay debajo
            ["ThemeToastBackground"] = dark ? ThemeColor.Rgb(0xF8, 0xFA, 0xFC) : ThemeColor.Rgb(0x0F, 0x17, 0x2A),
            ["ThemeToastText"] = dark ? ThemeColor.Rgb(0x0F, 0x17, 0x2A) : ThemeColor.Rgb(0xF8, 0xFA, 0xFC)
        };
        return palette;
    }

    /// <summary>
    /// Acerca el color al negro (fondos claros) o al blanco (fondos oscuros) hasta que contraste
    /// con todos los fondos indicados.
    /// </summary>
    public static ThemeColor EnsureContrast(ThemeColor color, IReadOnlyCollection<ThemeColor> backgrounds, double ratio)
    {
        if (backgrounds.All(b => ThemeColor.Contrast(color, b) >= ratio)) return color;

        var averageLuminance = backgrounds.Average(b => b.Luminance);
        var target = averageLuminance > 0.18 ? Black : White;
        for (int step = 1; step <= 50; step++)
        {
            var candidate = ThemeColor.Blend(color, target, step * 0.02);
            if (backgrounds.All(b => ThemeColor.Contrast(candidate, b) >= ratio)) return candidate;
        }
        return target;
    }
}
