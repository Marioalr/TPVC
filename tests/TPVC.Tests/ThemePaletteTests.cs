using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paleta del tema (fase 1): contraste garantizado en claro, oscuro y con cualquier acento, y los
/// valores por defecto del XAML iguales a los que calcula la app.
/// </summary>
[TestClass]
public class ThemePaletteTests
{
    // Los 8 acentos de Apariencia y algunos extremos (muy claros o saturados)
    private static readonly string[] Accents =
    {
        "#0369A1", "#2563EB", "#0D9488", "#059669", "#7C3AED", "#E11D48", "#D97706", "#475569",
        "#0284C7", "#10B981", "#F59E0B", "#EC4899", "#FDE047"
    };

    private static ThemeSettings Light(string accent) => new() { AccentColor = accent };

    private static ThemeSettings Dark(string accent) => new()
    {
        ThemeMode = "Oscuro",
        AccentColor = accent,
        BackgroundColor = "#0F172A",
        SurfaceColor = "#1E293B",
        TextColor = "#F8FAFC",
        SecondaryTextColor = "#94A3B8",
        BorderColor = "#334155"
    };

    /// <summary>Personalizado como queda tras "Aplicar colores" desde el modo indicado (texto y bordes del modo anterior).</summary>
    private static ThemeSettings Custom(ThemeSettings from, string background, string surface, string accent)
    {
        from.ThemeMode = "Personalizado";
        from.BackgroundColor = background;
        from.SurfaceColor = surface;
        from.AccentColor = accent;
        return from;
    }

    private static IEnumerable<(string Name, ThemeSettings Settings)> Themes()
    {
        foreach (var accent in Accents)
        {
            yield return ($"Claro {accent}", new ThemeSettings { AccentColor = accent });
            yield return ($"Oscuro {accent}", new ThemeSettings
            {
                ThemeMode = "Oscuro",
                AccentColor = accent,
                BackgroundColor = "#0F172A",
                SurfaceColor = "#1E293B",
                TextColor = "#F8FAFC",
                SecondaryTextColor = "#94A3B8",
                BorderColor = "#334155"
            });
        }
        yield return ("Personalizado crema", new ThemeSettings { ThemeMode = "Personalizado", BackgroundColor = "#FFF7ED", SurfaceColor = "#FFFBF5", AccentColor = "#C2410C" });
        foreach (var (label, from) in new (string, Func<ThemeSettings>)[] { ("desde Claro", () => Light("#0369A1")), ("desde Oscuro", () => Dark("#0369A1")) })
        {
            yield return ($"Crema {label}", Custom(from(), "#FFF7ED", "#FFFFFF", "#0D9488"));
            yield return ($"Menta {label}", Custom(from(), "#ECFDF5", "#FFFFFF", "#059669"));
            yield return ($"Pizarra {label}", Custom(from(), "#111827", "#1F2937", "#D97706"));
        }
    }

    private static void AssertContrast(string theme, IReadOnlyDictionary<string, ThemeColor> p, string fg, string bg, double min)
    {
        var ratio = ThemeColor.Contrast(p[fg], p[bg]);
        Assert.IsGreaterThanOrEqualTo(min, ratio, $"{theme}: {fg} sobre {bg} = {ratio:F2}:1 ({p[fg].ToHex()} / {p[bg].ToHex()})");
    }

    [TestMethod]
    public void EveryTheme_KeepsTextReadable()
    {
        foreach (var (name, settings) in Themes())
        {
            var p = ThemePalette.Build(settings);
            foreach (var surface in new[] { "ThemeBackground", "ThemeSurface", "ThemeSurfaceSubtle", "ThemeSelectionBackground" })
            {
                AssertContrast(name, p, "ThemeText", surface, 4.5);
                AssertContrast(name, p, "ThemeTextSecondary", surface, 4.5);
            }
            foreach (var surface in new[] { "ThemeBackground", "ThemeSurface", "ThemeSurfaceSubtle", "ThemeSelectionBackground" })
            {
                AssertContrast(name, p, "ThemeAccentForeground", surface, 4.5);
            }
            AssertContrast(name, p, "ThemeAccentText", "ThemeAccentStrong", 4.5);
            AssertContrast(name, p, "ThemeAccentText", "ThemeAccentHover", 4.5);
            // Foco y bordes de estado (3:1, WCAG 1.4.11) en todo lo que puede rodear
            foreach (var surface in new[] { "ThemeBackground", "ThemeSurface", "ThemeSurfaceSubtle", "ThemeSelectionBackground",
                         "ThemeSuccessSubtle", "ThemeWarningSubtle", "ThemeDangerSubtle", "ThemeInfoSubtle" })
            {
                AssertContrast(name, p, "ThemeFocus", surface, 3.0);
            }
            foreach (var surface in new[] { "ThemeBackground", "ThemeSurface", "ThemeSurfaceSubtle" })
            {
                AssertContrast(name, p, "ThemeControlBorder", surface, 3.0);
            }
            AssertContrast(name, p, "ThemeToastText", "ThemeToastBackground", 4.5);
            // Puntos de estado de la sincronización: se ven sobre la tarjeta y el gris suave
            foreach (var dot in new[] { "ThemeStatusSuccess", "ThemeStatusWarning", "ThemeStatusDanger" })
            {
                AssertContrast(name, p, dot, "ThemeSurface", 3.0);
                AssertContrast(name, p, dot, "ThemeSurfaceSubtle", 3.0);
            }
            // Lo seleccionado se distingue de la tarjeta (además lleva barra o borde de foco)
            Assert.IsGreaterThan(1.1, ThemeColor.Contrast(p["ThemeSelectionBackground"], p["ThemeSurface"]), $"{name}: la selección es igual a la tarjeta");
            // El texto principal y el secundario se distinguen (antes, en personalizado, quedaban iguales)
            Assert.IsGreaterThan(1.5, ThemeColor.Contrast(p["ThemeText"], p["ThemeTextSecondary"]), $"{name}: texto y texto secundario iguales");
            foreach (var solid in new[] { "ThemeSuccess", "ThemeWarning", "ThemeDanger", "ThemeError" })
            {
                AssertContrast(name, p, "ThemeAccentText", solid, 4.5);
            }
            foreach (var kind in new[] { "Success", "Warning", "Danger", "Info" })
            {
                AssertContrast(name, p, $"Theme{kind}Text", $"Theme{kind}Subtle", 4.5);
            }
            AssertContrast(name, p, "ThemeToastText", "ThemeToastBackground", 4.5);
            Assert.AreNotEqual(p["ThemeSurface"], p["ThemeSurfaceSubtle"], $"{name}: el gris suave debe distinguirse de la superficie");
        }
    }

    [TestMethod]
    public void DarkCustom_WithMidGreyCards_KeepsTheSelectionVisible()
    {
        // Con tarjetas gris medio la selección oscura se aclaraba hasta ser igual a la tarjeta
        foreach (var accent in new[] { "#FDE047", "#0369A1", "#475569" })
        {
            var p = ThemePalette.Build(Custom(Dark("#0369A1"), "#111827", "#4B5563", accent));
            Assert.IsGreaterThan(1.1, ThemeColor.Contrast(p["ThemeSelectionBackground"], p["ThemeSurface"]), $"{accent}: la selección es igual a la tarjeta");
            AssertContrast(accent, p, "ThemeText", "ThemeSelectionBackground", 4.5);
            AssertContrast(accent, p, "ThemeFocus", "ThemeSelectionBackground", 3.0);
        }
    }

    [TestMethod]
    public void CustomColors_TakeTextAndBordersFromTheirOwnSide()
    {
        // De Oscuro a un personalizado claro: el texto y los bordes son los del modo claro
        var fromDark = ThemePalette.Build(Custom(Dark("#0369A1"), "#FFF7ED", "#FFFFFF", "#0D9488"));
        Assert.AreEqual("#0F172A", fromDark["ThemeText"].ToHex());
        Assert.AreEqual("#E2E8F0", fromDark["ThemeBorder"].ToHex());

        // De Claro a un personalizado oscuro: los del modo oscuro
        var fromLight = ThemePalette.Build(Custom(Light("#0369A1"), "#111827", "#1F2937", "#D97706"));
        Assert.AreEqual("#F8FAFC", fromLight["ThemeText"].ToHex());
        Assert.AreEqual("#334155", fromLight["ThemeBorder"].ToHex());
    }

    [TestMethod]
    public void OldDefaultAccent_IsDarkenedForWhiteText()
    {
        var p = ThemePalette.Build(new ThemeSettings { AccentColor = "#0284C7" });
        Assert.AreEqual("#0284C7", p["ThemeAccent"].ToHex(), "El acento elegido se respeta para bordes e iconos");
        Assert.AreNotEqual(p["ThemeAccent"], p["ThemeAccentStrong"], "Con texto blanco se usa una versión más oscura");
        Assert.IsGreaterThanOrEqualTo(4.5, ThemeColor.Contrast(p["ThemeAccentStrong"], ThemePalette.White));
    }

    [TestMethod]
    public void XamlDefaults_MatchTheLightPalette()
    {
        var root = RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Styles", "ThemeColors.xaml"));
        var defined = Regex.Matches(xaml, "<SolidColorBrush x:Key=\"(?<key>Theme\\w+)\" Color=\"(?<color>#[0-9A-Fa-f]{6,8})\" />")
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["color"].Value.ToUpperInvariant());

        var palette = ThemePalette.Build(new ThemeSettings());
        var expected = new StringBuilder();
        foreach (var (key, color) in palette) expected.AppendLine($"    <SolidColorBrush x:Key=\"{key}\" Color=\"{color.ToHex()}\" />");

        var mismatches = palette.Where(kv => !defined.TryGetValue(kv.Key, out var hex) || hex != kv.Value.ToHex()).Select(kv => kv.Key).ToList();
        var extra = defined.Keys.Except(palette.Keys).ToList();
        Assert.IsTrue(mismatches.Count == 0 && extra.Count == 0,
            $"ThemeColors.xaml no coincide con ThemePalette (distintos: {string.Join(", ", mismatches)}; sobrantes: {string.Join(", ", extra)}). Valores esperados:\n{expected}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TPVC.csproj"))) dir = dir.Parent;
        Assert.IsNotNull(dir);
        return dir.FullName;
    }
}
