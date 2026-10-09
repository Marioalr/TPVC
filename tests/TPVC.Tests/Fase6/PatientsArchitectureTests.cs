using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TPVC.ViewModels;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 1c): reglas de arquitectura de ViewModels/Patients (plan, §1.2 y regla 7). Mientras la
/// carpeta no exista las revisiones de esa carpeta no tienen nada que revisar; quedan listas para los
/// pasos que la llenan.
/// </summary>
[TestClass]
public class PatientsArchitectureTests
{
    /// <summary>Lo que no puede aparecer en ViewModels/Patients/** (sin WPF, sin diálogos ni impresión directos).</summary>
    private static readonly string[] ForbiddenTokens =
    {
        "System.Windows", "Microsoft.Win32", "Application.Current", "Dispatcher", "DialogService", "PrintingService",
        "Process.Start", "MessageBox", "MedicalRecordsViewModel"
    };

    /// <summary>
    /// typeof(MedicalRecordsViewModel) que todavía existen (reflexión por nombre de miembro, que se rompe en
    /// silencio al mover miembros). El paso 2c los cambia por una tabla explícita; después esta lista queda vacía.
    /// </summary>
    private static readonly Dictionary<string, int> PendingTypeofMedicalRecords = new(StringComparer.Ordinal)
    {
        ["ViewModels/MedicalRecordsViewModel.PatientFormDraft.cs"] = 2
    };

    /// <summary>Niveles de dependencia (plan, §1.2): un objeto solo depende de niveles anteriores.</summary>
    private static readonly Dictionary<string, int> Tiers = new(StringComparer.Ordinal)
    {
        ["InfoDialogState"] = 2, ["PatientsFeedback"] = 2, ["PatientsNavigation"] = 2, ["ClinicInfo"] = 2, ["CatalogStore"] = 2,
        ["PrescriptionTemplateStore"] = 2, ["PatientSession"] = 2, ["ActiveConsultationSession"] = 2,
        ["ConsultationRecordState"] = 3, ["ConsultationAccount"] = 3,
        ["VitalSignsChartViewModel"] = 4, ["CatalogBrowserViewModel"] = 4, ["PrescriptionPreviewViewModel"] = 4,
        ["AgendaPanelViewModel"] = 5, ["PatientFormViewModel"] = 5, ["PatientsHomeViewModel"] = 5, ["PatientChartViewModel"] = 5,
        ["SuppliesSheetViewModel"] = 5, ["ClinicalServicesSheetViewModel"] = 5, ["PrescriptionSheetViewModel"] = 5, ["ConsultationViewModel"] = 5,
        ["ConsultationAutosave"] = 6,
        ["ConsultationHandoffViewModel"] = 7
    };

    private static string PatientsFolder => Path.Combine(Fase6Assets.RepoRoot(), "ViewModels", "Patients");

    [TestMethod]
    public void PatientsFolder_HasNoWpfOrCoordinatorReferences()
    {
        var problems = new List<string>();
        var files = Directory.Exists(PatientsFolder)
            ? Directory.GetFiles(PatientsFolder, "*.cs", SearchOption.AllDirectories)
            : Array.Empty<string>();
        foreach (var file in files)
        {
            var code = CSharpSource.Sanitize(File.ReadAllText(file));
            foreach (var token in ForbiddenTokens)
            {
                foreach (Match m in Regex.Matches(code, $@"(?<![\w.]){Regex.Escape(token)}(?!\w)"))
                {
                    problems.Add($"{Path.GetRelativePath(Fase6Assets.RepoRoot(), file)}:{CSharpSource.LineOf(code, m.Index)} usa {token}");
                }
            }
        }
        Assert.IsEmpty(problems, "ViewModels/Patients debe poder compilarse sin WPF (plan, regla 7):\n" + string.Join("\n", problems));
    }

    [TestMethod]
    public void NoReflectionOnMedicalRecordsViewModel_OutsideTheKnownPendingSites()
    {
        var root = Fase6Assets.RepoRoot();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in ProductionSources(root))
        {
            var code = CSharpSource.Sanitize(File.ReadAllText(file));
            var count = Regex.Matches(code, @"typeof\s*\(\s*(?:TPVC\.ViewModels\.)?MedicalRecordsViewModel\s*\)").Count;
            if (count > 0) found[Path.GetRelativePath(root, file).Replace('\\', '/')] = count;
        }

        var unexpected = found
            .Where(kv => kv.Value > PendingTypeofMedicalRecords.GetValueOrDefault(kv.Key))
            .Select(kv => $"{kv.Key}: {kv.Value} typeof(MedicalRecordsViewModel)")
            .ToList();
        Assert.IsEmpty(unexpected, "Reflexión sobre MedicalRecordsViewModel (se rompe sin aviso al mover miembros):\n" + string.Join("\n", unexpected));
    }

    private static IEnumerable<string> ProductionSources(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(f =>
        {
            var relative = Path.GetRelativePath(root, f).Replace('\\', '/');
            return !relative.StartsWith("tests/", StringComparison.Ordinal)
                   && !relative.StartsWith("installer/", StringComparison.Ordinal)
                   && !relative.StartsWith("publish/", StringComparison.Ordinal)
                   && !relative.Contains("/obj/", StringComparison.Ordinal) && !relative.StartsWith("obj/", StringComparison.Ordinal)
                   && !relative.Contains("/bin/", StringComparison.Ordinal) && !relative.StartsWith("bin/", StringComparison.Ordinal);
        });

    [TestMethod]
    public void PatientsTypes_DependOnlyOnEarlierTiers()
    {
        var app = typeof(MedicalRecordsViewModel).Assembly;
        var patientsTypes = app.GetTypes()
            .Where(t => t.Namespace?.StartsWith("TPVC.ViewModels.Patients", StringComparison.Ordinal) == true && Tiers.ContainsKey(t.Name))
            .ToList();

        var problems = new List<string>();
        foreach (var type in patientsTypes)
        {
            var tier = Tiers[type.Name];
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    var dependency = parameter.ParameterType;
                    if (Tiers.TryGetValue(dependency.Name, out var dependencyTier))
                    {
                        if (dependencyTier >= tier)
                        {
                            problems.Add($"{type.Name} (nivel {tier}) recibe {dependency.Name} (nivel {dependencyTier}): debe pasar por los puertos del coordinador");
                        }
                    }
                    else if (dependency.Namespace?.StartsWith("TPVC.ViewModels", StringComparison.Ordinal) == true
                             && dependency.Namespace?.StartsWith("TPVC.ViewModels.Patients.Platform", StringComparison.Ordinal) != true
                             && dependency.Namespace?.StartsWith("TPVC.ViewModels.Patients.Ports", StringComparison.Ordinal) != true)
                    {
                        problems.Add($"{type.Name} recibe {dependency.FullName}, que no está en la tabla de niveles (plan, §1.2)");
                    }
                }
            }
        }

        Assert.IsEmpty(problems, string.Join("\n", problems));
        // Sin la carpeta todavía no hay nada que revisar: la prueba queda lista para el paso 3 en adelante
        if (!Directory.Exists(PatientsFolder)) Assert.IsEmpty(patientsTypes);
    }
}
