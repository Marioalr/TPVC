using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TPVC.ViewModels;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 1a): contrato de los bindings de las vistas, verificable en Linux. Cada ruta de
/// binding de Views/*.xaml (y de MainWindow.xaml) se resuelve por reflexión contra el tipo de su
/// DataContext; al mover miembros de MedicalRecordsViewModel a otros ViewModels, una ruta que no se
/// actualizó falla aquí aunque compile. El manifiesto de Pacientes (patients-bindings.txt) deja ver
/// en el diff de cada paso qué bindings cambiaron de dueño.
/// </summary>
[TestClass]
public class XamlBindingContractTests
{
    private const string ManifestAsset = "patients-bindings.txt";
    private const string AllowlistAsset = "binding-allowlist.txt";

    private sealed class Contract
    {
        public required XamlBindingResolver Resolver { get; init; }
        public required List<ViewAnalysis> Analyses { get; init; }
        public required Dictionary<string, Type?> CodeBehindRoots { get; init; }
    }

    private static readonly Lazy<Contract> Shared = new(Build);

    private static Assembly App => typeof(MedicalRecordsViewModel).Assembly;

    private static Contract Build()
    {
        var root = Fase6Assets.RepoRoot();
        var resolver = new XamlBindingResolver(App);
        var views = Directory.GetFiles(Path.Combine(root, "Views"), "*.xaml").OrderBy(f => f, StringComparer.Ordinal).ToList();

        // d:DataContext de cada vista (para el contrato de las vistas anidadas)
        var documents = new List<(string Name, string Text, Type? RootType)>();
        foreach (var file in views)
        {
            var text = File.ReadAllText(file);
            var rootElement = XDocument.Parse(text).Root!;
            var rootType = resolver.DesignDataContextType(rootElement);
            resolver.RegisterView(Path.GetFileNameWithoutExtension(file), rootType);
            documents.Add((Path.GetFileName(file), text, rootType));
        }

        var analyses = new List<ViewAnalysis>();
        var codeBehindRoots = new Dictionary<string, Type?>(StringComparer.Ordinal);
        foreach (var (name, text, rootType) in documents)
        {
            codeBehindRoots[name] = rootType;
            if (rootType?.Namespace?.StartsWith("TPVC.ViewModels", StringComparison.Ordinal) != true) continue;
            analyses.Add(resolver.Analyze(name, text, rootType));
        }

        // MainWindow: su DataContext lo pone el código (DataContext = viewModel del constructor)
        var mainWindowType = MainWindowDataContext(root);
        codeBehindRoots["MainWindow.xaml"] = mainWindowType;
        if (mainWindowType != null)
        {
            analyses.Add(resolver.Analyze("MainWindow.xaml", File.ReadAllText(Path.Combine(root, "MainWindow.xaml")), mainWindowType));
        }

        return new Contract { Resolver = resolver, Analyses = analyses, CodeBehindRoots = codeBehindRoots };
    }

    /// <summary>Tipo del parámetro del constructor de MainWindow que se asigna a DataContext.</summary>
    private static Type? MainWindowDataContext(string root)
    {
        var code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        var ctor = Regex.Match(code, @"public\s+MainWindow\s*\(\s*(\w+)\s+(\w+)\s*\)");
        if (!ctor.Success || !Regex.IsMatch(code, $@"DataContext\s*=\s*{ctor.Groups[2].Value}\s*;")) return null;
        return App.GetTypes().FirstOrDefault(t => t.Name == ctor.Groups[1].Value && t.Namespace?.StartsWith("TPVC.ViewModels", StringComparison.Ordinal) == true);
    }

    /// <summary>Vistas del módulo Pacientes: su DataContext es el coordinador (o una base suya) o un ViewModel de TPVC.ViewModels.Patients.</summary>
    private static bool IsPatientsView(ViewAnalysis analysis)
    {
        var type = analysis.RootType;
        if (type == null) return false;
        if (type.Namespace?.StartsWith("TPVC.ViewModels.Patients", StringComparison.Ordinal) == true) return true;
        return type != typeof(object) && type.IsAssignableFrom(typeof(MedicalRecordsViewModel))
               && type.Namespace?.StartsWith("TPVC.ViewModels", StringComparison.Ordinal) == true;
    }

    // ================================================================== contrato

    [TestMethod]
    public void EveryBindingPath_ResolvesToAPublicMemberOfItsDataContext()
    {
        var contract = Shared.Value;
        var problems = contract.Analyses.SelectMany(a => a.Problems).Select(p => p.ToString()).ToList();

        Assert.IsEmpty(problems, $"Bindings que no apuntan a un miembro válido ({problems.Count}):\n" + string.Join("\n", problems.Take(80)));

        // El análisis no está vacío: si dejara de leer los XAML, esta prueba pasaría sin revisar nada
        var patients = contract.Analyses.Where(IsPatientsView).ToList();
        Assert.IsGreaterThanOrEqualTo(15, patients.Count, "Vistas de Pacientes analizadas: " + string.Join(", ", patients.Select(a => a.View)));
        Assert.IsGreaterThan(500, patients.Sum(a => a.Bindings.Count), "Bindings resueltos en Pacientes");
    }

    [TestMethod]
    public void UnresolvableExpressions_AreInTheReviewedAllowlist()
    {
        var contract = Shared.Value;
        var allowlist = ReadAllowlist();
        var unresolved = contract.Analyses.SelectMany(a => a.Unresolved).ToList();

        var missing = unresolved
            .Where(u => !allowlist.Any(e => e.Matches(u)))
            .Select(u => u.ToString())
            .ToList();
        Assert.IsEmpty(missing, "Bindings que el análisis no puede resolver y no están en " + AllowlistAsset +
                                " (revisarlos y agregarlos con su motivo):\n" + string.Join("\n", missing));

        var stale = allowlist.Where(e => !unresolved.Any(e.Matches)).Select(e => e.Raw).ToList();
        Assert.IsEmpty(stale, $"Entradas de {AllowlistAsset} que ya no aparecen en ninguna vista (quitarlas):\n" + string.Join("\n", stale));
    }

    [TestMethod]
    public void PatientsBindingManifest_MatchesTheCommittedOne()
    {
        var contract = Shared.Value;
        var lines = contract.Analyses
            .Where(IsPatientsView)
            .SelectMany(a => a.Bindings)
            .Select(b => b.ManifestLine)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l.Split('|')[0], StringComparer.Ordinal)
            .ThenBy(l => int.Parse(l.Split('|')[1]))
            .ThenBy(l => l, StringComparer.Ordinal)
            .ToList();

        var header = new[]
        {
            "# Bindings de las vistas de Pacientes resueltos por reflexión (XamlBindingContractTests).",
            "# vista|línea|atributo|ruta|Dueño.Miembro — el dueño es el tipo que declara el último miembro de la ruta.",
            "# Regenerar con TPVC_UPDATE_GOLDENS=1; en la fase 6 el diff solo puede cambiar rutas y dueños."
        };
        Fase6Assets.AssertMatchesGolden(ManifestAsset, Fase6Assets.Lines(header.Concat(lines)), "Manifiesto de bindings de Pacientes");
    }

    [TestMethod]
    public void HostedViews_ReceiveADataContextAssignableToTheirDesignDataContext()
    {
        // Contrato del contenedor: cada vista anidada recibe un DataContext asignable a su d:DataContext.
        // Las páginas y hojas de MedicalRecordsView (Content="{Binding ...}" + ContentTemplate), la gráfica
        // de signos en la ficha y la cuenta de la consulta (CartLinesView / PaymentPanelView con Checkout).
        var contract = Shared.Value;
        string Describe(HostCheck c) => $"{c.View}:{c.Line} {c.HostedView} espera {c.Expected?.Name ?? "?"} y recibe {c.Actual?.Name ?? "?"}";

        var all = contract.Analyses.SelectMany(a => a.HostChecks).ToList();
        var broken = all.Where(c => !c.Ok).Select(Describe).ToList();
        Assert.IsEmpty(broken, string.Join("\n", broken));

        var shell = contract.Analyses.Single(a => a.View == "MedicalRecordsView.xaml").HostChecks;
        CollectionAssert.AreEquivalent(
            new[]
            {
                "PatientsHomeView", "PatientChartView", "PatientFormView", "ConsultationWorkspaceView", "AgendaPanelView",
                "PrescriptionSheetView", "SuppliesSheetView", "ServicesSheetView", "CatalogBrowserSheetView",
                "PrescriptionPreviewSheetView", "ConsultationDetailSheetView", "ConsultationCheckoutView", "AllergyAlertView", "InfoDialogView",
                "SendConsultationSheetView"
            },
            shell.Select(c => c.HostedView).ToList(),
            "Cada página y hoja de Pacientes se verifica con el Content de su contenedor");
        Assert.IsTrue(shell.All(c => c.Actual != null), string.Join("\n", shell.Select(Describe)));

        var chart = contract.Analyses.Single(a => a.View == "PatientChartView.xaml").HostChecks;
        Assert.AreEqual("VitalSignsChartView", chart.Single().HostedView);

        var checkout = contract.Analyses.Single(a => a.View == "ConsultationCheckoutView.xaml").HostChecks;
        CollectionAssert.AreEquivalent(new[] { "CartLinesView", "PaymentPanelView" }, checkout.Select(c => c.HostedView).ToList());
        Assert.IsTrue(checkout.All(c => c.Actual == typeof(CheckoutViewModel)), string.Join("\n", checkout.Select(Describe)));
    }

    // ================================================================== código detrás de las vistas

    [TestMethod]
    public void CodeBehind_ViewModelTypes_MatchTheDesignDataContext()
    {
        var contract = Shared.Value;
        var root = Fase6Assets.RepoRoot();
        var viewModelTypes = App.GetTypes()
            .Where(t => t.Namespace?.StartsWith("TPVC.ViewModels", StringComparison.Ordinal) == true && !t.IsNested
                        && !t.Name.Contains('<') && (typeof(System.ComponentModel.INotifyPropertyChanged).IsAssignableFrom(t) || t.IsInterface))
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

        var problems = new List<string>();
        int checkedFiles = 0;
        foreach (var (xaml, rootType) in contract.CodeBehindRoots)
        {
            var codePath = xaml == "MainWindow.xaml"
                ? Path.Combine(root, "MainWindow.xaml.cs")
                : Path.Combine(root, "Views", xaml + ".cs");
            if (!File.Exists(codePath)) continue;
            var code = StripComments(File.ReadAllText(codePath));
            checkedFiles++;

            var mentioned = Regex.Matches(code, @"\b[A-Z]\w*\b").Select(m => m.Value).Distinct().Where(viewModelTypes.ContainsKey).ToList();
            if (mentioned.Count == 0) continue;
            if (rootType == null)
            {
                problems.Add($"{xaml}.cs usa {string.Join(", ", mentioned)} pero la vista no declara d:DataContext");
                continue;
            }

            var children = ReachableViewModels(rootType);
            foreach (var name in mentioned)
            {
                var type = viewModelTypes[name];
                bool isRootOrBase = type.IsAssignableFrom(rootType);
                if (!isRootOrBase && !children.Contains(type))
                {
                    problems.Add($"{xaml}.cs usa {name}, que no es su d:DataContext ({rootType.Name}), una base suya ni un ViewModel al que se llega desde él");
                }
            }

            // Lo que se hace con el DataContext (o el de DataContextChanged) debe ser el d:DataContext o una base
            foreach (Match m in Regex.Matches(code, @"(?:DataContext|NewValue|OldValue)\s+(?:is|as)\s+(?:not\s+)?(\w+)|\(\s*(\w+)\??\s*\)\s*DataContext"))
            {
                var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!viewModelTypes.TryGetValue(name, out var type)) continue;
                if (!type.IsAssignableFrom(rootType))
                {
                    problems.Add($"{xaml}.cs convierte el DataContext a {name}, pero la vista recibe {rootType.Name}");
                }
            }
        }

        Assert.IsGreaterThan(30, checkedFiles, "Archivos de código detrás revisados");
        Assert.IsEmpty(problems, string.Join("\n", problems));
    }

    /// <summary>ViewModels a los que se llega desde el tipo por propiedades públicas de solo lectura (hijos del coordinador).</summary>
    private static HashSet<Type> ReachableViewModels(Type rootType)
    {
        var result = new HashSet<Type>();
        var pending = new Queue<Type>();
        pending.Enqueue(rootType);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var t = p.PropertyType;
                if (p.SetMethod?.IsPublic == true || p.GetIndexParameters().Length > 0) continue;
                if (t.Namespace?.StartsWith("TPVC.ViewModels", StringComparison.Ordinal) != true) continue;
                if (result.Add(t)) pending.Enqueue(t);
            }
        }
        return result;
    }

    internal static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", " ");
    }

    // ================================================================== lista revisada

    private sealed record AllowEntry(string View, string Expression, string Reason, string Raw)
    {
        public bool Matches(UnresolvedBinding u) =>
            (View == "*" || View == u.View) && MarkupExtension.Canonical(Expression) == MarkupExtension.Canonical(u.Expression);
    }

    private static List<AllowEntry> ReadAllowlist()
    {
        var entries = new List<AllowEntry>();
        foreach (var raw in Fase6Assets.ReadAsset(AllowlistAsset).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|');
            Assert.HasCount(3, parts, $"{AllowlistAsset}: se espera vista|expresión|motivo en \"{line}\"");
            Assert.IsFalse(string.IsNullOrWhiteSpace(parts[2]), $"{AllowlistAsset}: falta el motivo en \"{line}\"");
            entries.Add(new AllowEntry(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), line));
        }
        return entries;
    }

    // ================================================================== autoprueba con un XAML roto

    /// <summary>Atributo con el mismo nombre que el de la fachada (se detecta por nombre).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    private sealed class NotForBindingAttribute : Attribute
    {
    }

    /// <summary>
    /// Coordinador con un miembro de fachada: XAML no debe enlazarlo. Hereda de PatientsWorkspace porque
    /// MedicalRecordsViewModel es sealed (paso 21); no se crea, solo se lee su tipo.
    /// </summary>
    private sealed class FacadeProbeViewModel : TPVC.ViewModels.Patients.PatientsWorkspace
    {
        private FacadeProbeViewModel() : base(null!, null)
        {
        }

        [NotForBinding]
        public int FacadeOnly => 0;
    }

    [TestMethod]
    public void BrokenSnippet_IsReported()
    {
        const string snippet = """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:views="clr-namespace:TPVC.Views"
                         xmlns:models="clr-namespace:TPVC.Models">
                <StackPanel>
                    <TextBlock Text="{Binding Session.SelectedPatient.FullName}" />
                    <TextBlock Text="{Binding NoSuchMember}" />
                    <TextBox Text="{Binding Active.ActiveConsultationFolio}" />
                    <TextBox Text="{Binding Active.ActiveConsultationFolio, Mode=OneWay}" />
                    <TextBlock Text="{Binding Session.SelectedPatient.NoSuchField}" />
                    <TextBlock Text="{Binding FacadeOnly}" />
                    <ItemsControl ItemsSource="{Binding Session.Patients}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <StackPanel>
                                    <TextBlock Text="{Binding FullName}" />
                                    <TextBlock Text="{Binding NotAPatientMember}" />
                                    <Button Command="{Binding DataContext.OpenPatientChartCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}" />
                                </StackPanel>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                    <Button Tag="{Binding}">
                        <Button.ContextMenu>
                            <ContextMenu>
                                <MenuItem Command="{Binding PlacementTarget.Tag.BackToHubCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}" />
                                <MenuItem Command="{Binding PlacementTarget.Tag.NoSuchCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}" />
                                <MenuItem Command="{Binding DataContext.BackToHubCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" />
                            </ContextMenu>
                        </Button.ContextMenu>
                    </Button>
                    <TextBlock>
                        <TextBlock.Text>
                            <MultiBinding StringFormat="{}{0} {1}">
                                <Binding Path="Active.ActiveConsultationFolio" />
                                <Binding Path="NoSuchInMultiBinding" />
                            </MultiBinding>
                        </TextBlock.Text>
                    </TextBlock>
                    <views:VitalSignsChartView DataContext="{Binding VitalsChart}" />
                    <views:VitalSignsChartView DataContext="{Binding Consultation.Checkout}" />
                </StackPanel>
            </UserControl>
            """;

        var analysis = Shared.Value.Resolver.Analyze("Roto.xaml", snippet, typeof(FacadeProbeViewModel));
        var problems = analysis.Problems.Select(p => p.ToString()).ToList();
        string Report() => string.Join("\n", problems);

        void Expect(string expression, string fragment) =>
            Assert.IsTrue(analysis.Problems.Any(p => p.Expression.Contains(expression, StringComparison.Ordinal) && p.Problem.Contains(fragment, StringComparison.Ordinal)),
                $"No se reportó '{fragment}' para {expression}. Reportado:\n{Report()}");

        Expect("NoSuchMember", "no tiene la propiedad pública 'NoSuchMember'");
        Expect("{Binding Active.ActiveConsultationFolio}", "TwoWay");
        Expect("Session.SelectedPatient.NoSuchField", "PatientRecord no tiene la propiedad pública 'NoSuchField'");
        Expect("FacadeOnly", "[NotForBinding]");
        Expect("NotAPatientMember", "PatientRecord no tiene");
        Expect("PlacementTarget.Tag.NoSuchCommand", "no tiene la propiedad pública 'NoSuchCommand'");
        Expect("DataContext.BackToHubCommand, RelativeSource={RelativeSource AncestorType=UserControl}", "no hay un ancestro UserControl alcanzable");
        Expect("NoSuchInMultiBinding", "no tiene la propiedad pública 'NoSuchInMultiBinding'");
        Expect("(DataContext)", "VitalSignsChartView espera VitalSignsChartViewModel pero recibe CheckoutViewModel");
        Assert.HasCount(9, problems, "Solo lo roto se reporta:\n" + Report());

        // Lo correcto del mismo XAML sí se resuelve (con su dueño)
        var owners = analysis.Bindings.Select(b => $"{b.Path}|{b.Owner}").ToList();
        CollectionAssert.Contains(owners, "Session.SelectedPatient.FullName|PatientRecord.FullName");
        CollectionAssert.Contains(owners, "FullName|PatientRecord.FullName");
        CollectionAssert.Contains(owners, "DataContext.OpenPatientChartCommand|PatientsWorkspace.OpenPatientChartCommand");
        CollectionAssert.Contains(owners, "PlacementTarget.Tag.BackToHubCommand|PatientsWorkspace.BackToHubCommand");
        CollectionAssert.Contains(owners, ".|PatientRecord.");
        Assert.IsEmpty(analysis.Unresolved, string.Join("\n", analysis.Unresolved));
    }
}
