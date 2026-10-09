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
        ["ConsultationHandoffViewModel"] = 7,
        // El coordinador (ViewModels/Patients/Workspace, paso 21) crea a todos los hijos: recibe solo la
        // plataforma y los servicios
        ["PatientsWorkspace"] = 8
    };

    /// <summary>
    /// Dependencias dentro del mismo nivel que la tabla del plan (§1.2) declara: los avisos breves
    /// (PatientsFeedback) usan la ventana de aviso (InfoDialogState) cuando no hay servicio de avisos.
    /// </summary>
    private static readonly HashSet<(string Type, string Dependency)> SameTierDependencies = new()
    {
        ("PatientsFeedback", "InfoDialogState")
    };

    private static string PatientsFolder => Path.Combine(Fase6Assets.RepoRoot(), "ViewModels", "Patients");

    /// <summary>
    /// Plataforma (nivel 1) y puertos del coordinador: viven en ViewModels/Patients/Platform y
    /// ViewModels/Patients/Ports con el espacio de nombres TPVC.ViewModels.Patients (el mismo que los
    /// hijos), así que se reconocen por la carpeta de su archivo y no por el espacio de nombres.
    /// </summary>
    private static HashSet<string> PlatformAndPortTypeNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in new[] { "Platform", "Ports" })
        {
            var path = Path.Combine(PatientsFolder, folder);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                var code = CSharpSource.Sanitize(File.ReadAllText(file));
                foreach (Match m in Regex.Matches(code, @"\b(?:class|interface|struct|enum|record(?:\s+(?:class|struct))?)\s+(\w+)"))
                {
                    names.Add(m.Groups[1].Value);
                }
            }
        }
        return names;
    }

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

    /// <summary>
    /// Ningún typeof(MedicalRecordsViewModel) en el programa: la reflexión por nombre de miembro se rompe en
    /// silencio al mover miembros a otro ViewModel. El paso 2c cambió el último (el borrador del alta) por
    /// una tabla explícita.
    /// </summary>
    [TestMethod]
    public void NoReflectionOnMedicalRecordsViewModel()
    {
        var root = Fase6Assets.RepoRoot();
        var found = new List<string>();
        foreach (var file in ProductionSources(root))
        {
            var code = CSharpSource.Sanitize(File.ReadAllText(file));
            var count = Regex.Matches(code, @"typeof\s*\(\s*(?:TPVC\.ViewModels\.)?MedicalRecordsViewModel\s*\)").Count;
            if (count > 0) found.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: {count} typeof(MedicalRecordsViewModel)");
        }

        Assert.IsEmpty(found, "Reflexión sobre MedicalRecordsViewModel (se rompe sin aviso al mover miembros):\n" + string.Join("\n", found));
    }

    private static IEnumerable<string> ProductionSources(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(f =>
        {
            var relative = Path.GetRelativePath(root, f).Replace('\\', '/');
            // .claude/ guarda copias de trabajo de otras ramas (worktrees): no son esta versión
            return !relative.StartsWith("tests/", StringComparison.Ordinal)
                   && !relative.StartsWith(".claude/", StringComparison.Ordinal)
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

        var platformAndPorts = PlatformAndPortTypeNames();
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
                        if (dependencyTier >= tier && !SameTierDependencies.Contains((type.Name, dependency.Name)))
                        {
                            problems.Add($"{type.Name} (nivel {tier}) recibe {dependency.Name} (nivel {dependencyTier}): debe pasar por los puertos del coordinador");
                        }
                    }
                    else if (dependency.Namespace?.StartsWith("TPVC.ViewModels", StringComparison.Ordinal) == true
                             && !(dependency.Namespace == "TPVC.ViewModels.Patients" && platformAndPorts.Contains(dependency.Name))
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
        // Desde el paso 21 el coordinador vive en ViewModels/Patients/Workspace y también se revisa
        if (Directory.Exists(Path.Combine(PatientsFolder, "Workspace")))
        {
            CollectionAssert.Contains(patientsTypes.Select(t => t.Name).ToList(), "PatientsWorkspace");
        }
    }

    /// <summary>
    /// MedicalRecordsViewModel (paso 21) solo arma la plataforma de WPF para PatientsWorkspace: no declara
    /// miembros propios, así que todo lo del apartado se puede usar sin WPF.
    /// </summary>
    [TestMethod]
    public void WpfCoordinator_OnlyBuildsThePlatform()
    {
        var wpf = typeof(MedicalRecordsViewModel);
        Assert.AreEqual("PatientsWorkspace", wpf.BaseType?.Name);
        Assert.AreEqual("TPVC.ViewModels.Patients", wpf.BaseType?.Namespace);
        Assert.IsTrue(wpf.IsSealed);

        var own = wpf.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.MemberType != MemberTypes.Constructor)
            .Select(m => $"{m.MemberType} {m.Name}")
            .ToList();
        Assert.IsEmpty(own, "MedicalRecordsViewModel declara miembros propios (deben vivir en PatientsWorkspace):\n" + string.Join("\n", own));
    }
}
