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
        var palette = Regex.Matches(File.ReadAllText(Path.Combine(root, "Styles", "ThemeColors.xaml")), "x:Key=\"(Theme\\w+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
        var service = File.ReadAllText(Path.Combine(root, "Services", "ThemeService.cs"));

        // Si un color no se actualiza al cambiar a modo oscuro o de acento, conserva el del modo claro
        var notUpdated = palette.Where(key => !service.Contains($"resources[\"{key}\"]", StringComparison.Ordinal)).ToList();

        Assert.IsNotEmpty(palette);
        Assert.IsEmpty(notUpdated, "No cambian con el tema: " + string.Join(", ", notUpdated));
    }
}
