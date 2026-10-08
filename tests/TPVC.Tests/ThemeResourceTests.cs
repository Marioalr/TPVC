using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TPVC.Tests;

/// <summary>
/// Revisión automática de los temas: un color de tema que no existe no da error al compilar, solo
/// deja el elemento transparente (así estuvieron ThemeCard y ThemeError hasta v1.11).
/// </summary>
[TestClass]
public class ThemeResourceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TPVC.csproj"))) dir = dir.Parent;
        Assert.IsNotNull(dir, "No se encontró la raíz del repositorio");
        return dir.FullName;
    }

    private static IEnumerable<string> AppXamlFiles(string root) =>
        Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f =>
            {
                var relative = Path.GetRelativePath(root, f).Replace('\\', '/');
                return !relative.StartsWith("tests/", StringComparison.Ordinal)
                       && !relative.Contains("/obj/", StringComparison.Ordinal)
                       && !relative.Contains("/bin/", StringComparison.Ordinal)
                       && !relative.StartsWith("obj/", StringComparison.Ordinal)
                       && !relative.StartsWith("bin/", StringComparison.Ordinal)
                       && !relative.StartsWith("installer/", StringComparison.Ordinal);
            });

    private static HashSet<string> DefinedThemeKeys(string root) =>
        Directory.GetFiles(Path.Combine(root, "Styles"), "*.xaml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "x:Key=\"(Theme\\w+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

    [TestMethod]
    public void EveryThemeBrushUsedInScreens_IsDefined()
    {
        var root = RepoRoot();
        var defined = DefinedThemeKeys(root);
        var missing = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var file in AppXamlFiles(root))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "(?:DynamicResource|StaticResource)\\s+(Theme\\w+)"))
            {
                var key = m.Groups[1].Value;
                if (defined.Contains(key)) continue;
                if (!missing.TryGetValue(key, out var files)) missing[key] = files = new SortedSet<string>(StringComparer.Ordinal);
                files.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.IsEmpty(missing, "Colores de tema sin definir: " +
            string.Join("; ", missing.Select(kv => $"{kv.Key} en {string.Join(", ", kv.Value)}")));
    }

    [TestMethod]
    public void ThemeService_UpdatesEveryColorOfThePalette()
    {
        var root = RepoRoot();
        var dictionary = Regex.Matches(File.ReadAllText(Path.Combine(root, "Styles", "ThemeColors.xaml")), "x:Key=\"(Theme\\w+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // ThemeService publica exactamente ThemePalette.Build: si un color no está ahí, no cambia
        // al pasar a modo oscuro o a otro acento
        foreach (var settings in new[] { new TPVC.Models.ThemeSettings(), new TPVC.Models.ThemeSettings { ThemeMode = "Oscuro", BackgroundColor = "#0F172A", SurfaceColor = "#1E293B" } })
        {
            var palette = TPVC.Services.ThemePalette.Build(settings);
            var notUpdated = dictionary.Where(key => !palette.ContainsKey(key)).ToList();
            Assert.IsEmpty(notUpdated, "No cambian con el tema: " + string.Join(", ", notUpdated));
        }
        Assert.IsNotEmpty(dictionary);
    }
    [TestMethod]
    public void EveryResourceKeyUsedInScreens_IsDefined()
    {
        // Un StaticResource inexistente cierra la pantalla al abrirla (XamlParseException) y no
        // falla al compilar; un DynamicResource inexistente deja el elemento sin estilo
        var root = RepoRoot();
        var shared = Directory.GetFiles(Path.Combine(root, "Styles"), "*.xaml").Append(Path.Combine(root, "App.xaml"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var file in AppXamlFiles(root))
        {
            var text = File.ReadAllText(file);
            var local = Regex.Matches(text, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(text, "\\{(?:StaticResource|DynamicResource)\\s+([^}\\s]+)\\}"))
            {
                var key = m.Groups[1].Value;
                if (key.StartsWith("{", StringComparison.Ordinal) || key.StartsWith("x:", StringComparison.Ordinal)) continue;
                if (!shared.Contains(key) && !local.Contains(key)) missing.Add($"{key} en {Path.GetRelativePath(root, file)}");
            }
        }

        Assert.IsEmpty(missing.Distinct().ToList(), "Recursos sin definir: " + string.Join("; ", missing.Distinct()));
    }
    /// <summary>
    /// Fase 5: el texto solo usa colores hechos para texto. Los de relleno (acento sin ajustar,
    /// sólidos de éxito/aviso/peligro, bordes, fondos) no garantizan contraste: con un acento ámbar
    /// o en modo oscuro quedaban en 2–3:1.
    /// </summary>
    [TestMethod]
    public void TextOnlyUsesTextColors()
    {
        var root = RepoRoot();
        var fillOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "ThemeAccent", "ThemeAccentStrong", "ThemeAccentHover", "ThemeAccentPressed", "ThemePrimary", "ThemePrimaryTransparent",
            "ThemeSuccess", "ThemeWarning", "ThemeDanger", "ThemeError", "ThemeFocus", "ThemeBorder", "ThemeBorderStrong",
            "ThemeOverlay", "ThemeSelectionBackground", "ThemeSurface", "ThemeSurfaceSubtle", "ThemeCard", "ThemeBackground",
            "ThemeSuccessSubtle", "ThemeWarningSubtle", "ThemeDangerSubtle", "ThemeInfoSubtle", "ThemeToastBackground"
        };
        var pattern = new Regex(
            "(?:(?:\\b|\\.)Foreground=\"\\{(?:DynamicResource|StaticResource)\\s+(Theme\\w+)\\}\")" +
            "|(?:Property=\"(?:TextElement\\.)?Foreground\"\\s+Value=\"\\{(?:DynamicResource|StaticResource)\\s+(Theme\\w+)\\}\")");
        var problems = new List<string>();
        foreach (var file in AppXamlFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in pattern.Matches(lines[i]))
                {
                    var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (fillOnly.Contains(key)) problems.Add($"{Path.GetRelativePath(root, file)}:{i + 1} {key}");
                }
            }
        }
        Assert.IsEmpty(problems, "Texto con colores de relleno (use ThemeText, ThemeTextSecondary, ThemeAccentForeground, ThemeAccentText o *Text): " +
            string.Join("; ", problems));
    }

    /// <summary>El acento tal cual lo eligió el usuario solo sirve para la selección de texto.</summary>
    [TestMethod]
    public void RawAccentIsOnlyUsedForTextSelection()
    {
        var root = RepoRoot();
        var problems = new List<string>();
        foreach (var file in AppXamlFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], "(?:DynamicResource|StaticResource)\\s+ThemeAccent\\}")) continue;
                if (lines[i].Contains("SelectionBrush", StringComparison.Ordinal)) continue;
                problems.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
            }
        }
        Assert.IsEmpty(problems, "ThemeAccent sin ajustar (use ThemeAccentStrong de fondo, ThemeAccentForeground de texto o ThemeFocus de borde): " +
            string.Join("; ", problems));
    }

    [TestMethod]
    public void NoBindingUsesAsyncCommandNames()
    {
        // CommunityToolkit genera GuardarCommand para "GuardarAsync": un binding a
        // GuardarAsyncCommand no existe y el botón no hace nada (pasaba con 5 botones)
        var root = RepoRoot();
        var wrong = AppXamlFiles(root)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\\b(\\w+AsyncCommand)\\b").Select(m => $"{m.Groups[1].Value} en {Path.GetRelativePath(root, f)}"))
            .Distinct()
            .ToList();
        Assert.IsEmpty(wrong, "Comandos inexistentes: " + string.Join("; ", wrong));
    }
}