using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Archivos de referencia de la fase 6 (separar MedicalRecordsViewModel): se leen del árbol de
/// fuentes (tests/TPVC.Tests/TestAssets/fase6), no de la carpeta de salida, para que regenerarlos
/// escriba en el repositorio. Funciona igual en Linux y en la CI de Windows (las pruebas corren
/// dentro del checkout).
///
/// Regenerar: TPVC_UPDATE_GOLDENS=1 dotnet test ... (solo en el paso 1 o para el manifiesto de
/// bindings; los demás archivos de referencia no deben cambiar durante la fase 6).
/// </summary>
internal static class Fase6Assets
{
    public const string UpdateVariable = "TPVC_UPDATE_GOLDENS";

    public static bool UpdateGoldens => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    private static string? _repoRoot;

    public static string RepoRoot()
    {
        if (_repoRoot != null) return _repoRoot;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TPVC.csproj"))) dir = dir.Parent;
        Assert.IsNotNull(dir, "No se encontró la raíz del repositorio (TPVC.csproj) desde " + AppContext.BaseDirectory);
        return _repoRoot = dir.FullName;
    }

    public static string AssetsDirectory => Path.Combine(RepoRoot(), "tests", "TPVC.Tests", "TestAssets", "fase6");

    public static string AssetPath(string relative) => Path.Combine(AssetsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Texto con saltos de línea \n (los archivos pueden llegar con \r\n en Windows).</summary>
    public static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string ReadAsset(string relative)
    {
        var path = AssetPath(relative);
        Assert.IsTrue(File.Exists(path), $"Falta el archivo de referencia {relative}. Generarlo con {UpdateVariable}=1.");
        return NormalizeNewLines(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// Compara con el archivo de referencia; con TPVC_UPDATE_GOLDENS=1 lo reescribe. El mensaje de
    /// error muestra las primeras líneas distintas.
    /// </summary>
    public static void AssertMatchesGolden(string relative, string actual, string what)
    {
        actual = NormalizeNewLines(actual);
        if (!actual.EndsWith('\n')) actual += "\n";
        var path = AssetPath(relative);

        if (UpdateGoldens)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var current = File.Exists(path) ? NormalizeNewLines(File.ReadAllText(path, Encoding.UTF8)) : null;
            if (current != actual) File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        Assert.IsTrue(File.Exists(path), $"{what}: falta {relative}. Generarlo con {UpdateVariable}=1 y revisar el resultado.");
        var expected = NormalizeNewLines(File.ReadAllText(path, Encoding.UTF8));
        if (expected == actual) return;
        Assert.Fail($"{what}: cambió respecto a {relative}.\n{Diff(expected, actual)}");
    }

    /// <summary>Diferencias línea por línea (las primeras 40), suficiente para revisar en la salida de la prueba.</summary>
    public static string Diff(string expected, string actual)
    {
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        var removed = a.Except(b).ToList();
        var added = b.Except(a).ToList();
        var sb = new StringBuilder();
        if (removed.Count == 0 && added.Count == 0)
        {
            // Mismas líneas en otro orden
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] == b[i]) continue;
                sb.AppendLine($"Primera diferencia en la línea {i + 1}:");
                sb.AppendLine($"- {a[i]}");
                sb.AppendLine($"+ {b[i]}");
                return sb.ToString();
            }
            sb.AppendLine($"Distinto número de líneas: {a.Length} contra {b.Length}");
            return sb.ToString();
        }
        foreach (var line in removed.Take(20)) sb.AppendLine($"- {line}");
        if (removed.Count > 20) sb.AppendLine($"  … y {removed.Count - 20} líneas más que ya no están");
        foreach (var line in added.Take(20)) sb.AppendLine($"+ {line}");
        if (added.Count > 20) sb.AppendLine($"  … y {added.Count - 20} líneas nuevas más");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ normalización de borradores

    private static readonly Regex IsoDate = new(@"""\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?""", RegexOptions.Compiled);
    private static readonly Regex Guid = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex ConsultationFolio = new(@"CNS-\d{8}-\d{6}", RegexOptions.Compiled);

    /// <summary>
    /// JSON de un borrador sin lo que cambia en cada corrida: fechas y horas (cualquier fecha ISO),
    /// identificadores de sincronización (GUID) y folios de consulta con fecha y hora. Lo demás
    /// (textos, Id locales de la base sembrada, cantidades, precios) se compara tal cual.
    /// </summary>
    public static string NormalizeDraftJson(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var pretty = node.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        pretty = IsoDate.Replace(pretty, "\"<fecha>\"");
        pretty = Guid.Replace(pretty, "<guid>");
        pretty = ConsultationFolio.Replace(pretty, "<folio>");
        return NormalizeNewLines(pretty) + "\n";
    }

    /// <summary>Lista de nombres, uno por línea, con encabezados de sección (# …).</summary>
    public static string Lines(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";
}
