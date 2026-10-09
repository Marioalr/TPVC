using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TPVC.ViewModels;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Fase 6 (paso 1c): cada aviso de cambio que se levanta en ViewModels/** nombra una propiedad de la
/// clase que lo levanta (o de su base). Al mover un miembro a otro ViewModel, un
/// OnPropertyChanged(nameof(X)) que se quedó en el coordinador avisaría en el objeto equivocado y la
/// pantalla no se actualizaría; con nameof eso compila solo si X sigue existiendo en la clase, pero
/// no si X está en la fachada [NotForBinding] (que nunca se avisa).
/// </summary>
[TestClass]
public class NotificationTargetsTests
{
    private static readonly Regex Raise = new(
        @"\bOnPropertyChang(?:ed|ing)\s*\(\s*nameof\s*\(\s*([\w.]+)\s*\)\s*\)|\[\s*NotifyPropertyChangedFor\s*\(\s*nameof\s*\(\s*([\w.]+)\s*\)",
        RegexOptions.Compiled);

    private sealed record Raised(string File, int Line, string TypeName, string Member, Type? Type);

    private static List<Raised> ScanViewModels()
    {
        var root = Fase6Assets.RepoRoot();
        var app = typeof(MedicalRecordsViewModel).Assembly;
        var result = new List<Raised>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "ViewModels"), "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var code = CSharpSource.Sanitize(File.ReadAllText(file));
            var ns = CSharpSource.Namespace(code);
            var spans = CSharpSource.TypeSpans(code);
            foreach (Match m in Raise.Matches(code))
            {
                var member = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Split('.').Last();
                var typeName = CSharpSource.TypeAt(spans, m.Index) ?? "?";
                var type = ns == null ? null : app.GetType($"{ns}.{typeName}");
                result.Add(new Raised(Path.GetRelativePath(root, file).Replace('\\', '/'), CSharpSource.LineOf(code, m.Index), typeName, member, type));
            }
        }
        return result;
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        return null;
    }

    [TestMethod]
    public void EveryRaisedName_IsAPropertyOfTheRaisingClass_AndNotAFacadeMember()
    {
        var raised = ScanViewModels();
        var problems = new List<string>();
        foreach (var r in raised)
        {
            if (r.Type == null)
            {
                problems.Add($"{r.File}:{r.Line} no se encontró el tipo {r.TypeName}");
                continue;
            }
            var property = FindProperty(r.Type, r.Member);
            if (property == null)
            {
                problems.Add($"{r.File}:{r.Line} {r.TypeName} avisa \"{r.Member}\", que no es una propiedad suya ni de su base");
            }
            else if (property.CustomAttributes.Any(a => a.AttributeType.Name == "NotForBindingAttribute"))
            {
                problems.Add($"{r.File}:{r.Line} {r.TypeName} avisa \"{r.Member}\", que es de la fachada [NotForBinding] (nunca se avisa)");
            }
        }

        Assert.IsEmpty(problems, string.Join("\n", problems));

        // La revisión no está vacía (hoy hay unos 160 avisos; casi 100 en el coordinador)
        Assert.IsGreaterThan(120, raised.Count, "Avisos encontrados en ViewModels/**");
        Assert.IsTrue(raised.Any(r => r.TypeName == nameof(MedicalRecordsViewModel)), "Se revisa el coordinador");
        Assert.IsTrue(raised.Any(r => r.TypeName == nameof(CheckoutViewModel)), "También se revisan los demás ViewModels");
    }

    [TestMethod]
    public void Scanner_FindsTheClassOfEachRaise()
    {
        // El lector de código ubica bien la clase aunque haya cadenas con llaves, comentarios y tipos anidados
        const string code = """
            namespace TPVC.ViewModels;
            public partial class A
            {
                private string Text => $"{(true ? "{" : "}")} // no es comentario";
                // OnPropertyChanged(nameof(Comentado));
                void M() { OnPropertyChanged(nameof(Uno)); }
                private sealed class B
                {
                    void N() { var c = '{'; OnPropertyChanged(nameof(Dos)); }
                }
                void P() => OnPropertyChanged(nameof(Tres));
            }
            """;
        var sanitized = CSharpSource.Sanitize(code);
        Assert.AreEqual(code.Length, sanitized.Length);
        var spans = CSharpSource.TypeSpans(sanitized);
        var found = Raise.Matches(sanitized).Select(m => $"{CSharpSource.TypeAt(spans, m.Index)}:{m.Groups[1].Value}").ToList();
        CollectionAssert.AreEqual(new[] { "A:Uno", "A+B:Dos", "A:Tres" }, found);
    }
}
