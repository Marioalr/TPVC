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
    private static readonly string[] Accents = { "#0369A1", "#0284C7", "#7C3AED", "#10B981", "#F59E0B", "#EC4899", "#FDE047", "#E11D48" };

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
            foreach (var surface in new[] { "ThemeBackground", "ThemeSurface", "ThemeSurfaceSubtle" })
            {
                AssertContrast(name, p, "ThemeAccentForeground", surface, 4.5);
            }
            AssertContrast(name, p, "ThemeAccentText", "ThemeAccentStrong", 4.5);
            AssertContrast(name, p, "ThemeAccentText", "ThemeAccentHover", 4.5);
            AssertContrast(name, p, "ThemeFocus", "ThemeSurface", 3.0);
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
