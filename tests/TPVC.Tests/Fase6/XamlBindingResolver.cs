using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TPVC.Tests.Fase6;

/// <summary>Un binding resuelto: vista|línea|atributo|ruta|Dueño.Miembro (una línea del manifiesto).</summary>
internal sealed record ResolvedBinding(string View, int Line, string Attribute, string Path, string Owner)
{
    public string ManifestLine => $"{View}|{Line}|{Attribute}|{Path}|{Owner}";
}

/// <summary>Un binding que no apunta a un miembro válido (o que viola una regla del contrato).</summary>
internal sealed record BindingProblem(string View, int Line, string Attribute, string Expression, string Problem)
{
    public override string ToString() => $"{View}:{Line} {Attribute}=\"{Expression}\" → {Problem}";
}

/// <summary>
/// Un binding que el análisis no puede seguir por reflexión (RelativeSource PreviousData, propiedad de un
/// elemento de WPF, ámbito sin tipo...). Debe estar en la lista revisada binding-allowlist.txt.
/// </summary>
internal sealed record UnresolvedBinding(string View, int Line, string Attribute, string Expression, string Reason)
{
    public override string ToString() => $"{View}:{Line} {Attribute}=\"{Expression}\" ({Reason})";
}

/// <summary>Una vista anidada (views:X) y el DataContext que recibe.</summary>
internal sealed record HostCheck(string View, int Line, string HostedView, Type? Expected, Type? Actual)
{
    public bool Ok => Expected == null || (Actual != null && Expected.IsAssignableFrom(Actual));
}

internal sealed class ViewAnalysis
{
    public string View { get; init; } = string.Empty;
    public Type? RootType { get; init; }
    public List<ResolvedBinding> Bindings { get; } = new();
    public List<HostCheck> HostChecks { get; } = new();
    public List<BindingProblem> Problems { get; } = new();
    public List<UnresolvedBinding> Unresolved { get; } = new();
}

/// <summary>
/// Lee los XAML de Views/ como XML y resuelve cada ruta de binding por reflexión contra el tipo de su
/// DataContext. Modela los ámbitos que usan las vistas: la raíz (d:DataContext), DataContext="{Binding P}"
/// en un elemento, el tipo de los elementos de ItemsSource para ItemTemplate / ItemContainerStyle /
/// columnas, estilos en línea y sus DataTrigger, RelativeSource AncestorType (UserControl, Window,
/// ItemsControl, ContextMenu), PlacementTarget.Tag de los ContextMenu (a través del binding de Tag),
/// MenuOnClick (el menú toma el DataContext del botón), &lt;Binding Path&gt; dentro de MultiBinding,
/// recursos con clave usados con StaticResource y DataTemplates con vistas anidadas.
///
/// Además de que cada miembro exista: los bindings TwoWay requieren un setter público, ningún miembro de
/// la ruta puede llevar un atributo llamado NotForBindingAttribute, y cada vista anidada debe recibir un
/// DataContext asignable al d:DataContext de esa vista (contrato del contenedor).
/// No usa tipos de WPF: funciona en Linux.
/// </summary>
internal sealed class XamlBindingResolver
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace DesignNs = "http://schemas.microsoft.com/expression/blend/2008";
    private static readonly XNamespace CompatNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private static readonly HashSet<string> ItemsControlTags = new(StringComparer.Ordinal)
    {
        "ItemsControl", "ListBox", "ListView", "ComboBox", "DataGrid", "TreeView", "TabControl", "Menu", "ContextMenu",
        "StatusBar", "ToolBar", "Selector", "MultiSelector", "HeaderedItemsControl", "MenuItem", "TreeViewItem"
    };

    private readonly Assembly _app;
    private readonly Dictionary<string, Type?> _viewRoots = new(StringComparer.Ordinal);

    public XamlBindingResolver(Assembly appAssembly)
    {
        _app = appAssembly;
    }

    /// <summary>d:DataContext de cada vista conocida (para el contrato de las vistas anidadas).</summary>
    public IReadOnlyDictionary<string, Type?> ViewRoots => _viewRoots;

    public void RegisterView(string viewClassName, Type? rootType) => _viewRoots[viewClassName] = rootType;

    // ================================================================== tipos raíz

    /// <summary>Tipo de d:DataContext="{d:DesignInstance Type=...}" del elemento raíz, o null.</summary>
    public Type? DesignDataContextType(XElement root)
    {
        var attr = root.Attribute(DesignNs + "DataContext");
        if (attr == null) return null;
        var ext = MarkupExtension.TryParse(attr.Value);
        if (ext == null) return null;
        var typeName = ext.Named.GetValueOrDefault("Type") ?? ext.Positional.FirstOrDefault();
        return typeName == null ? null : ResolveTypeName(root, typeName);
    }

    /// <summary>Resuelve "prefijo:Nombre" con los xmlns clr-namespace del archivo.</summary>
    public Type? ResolveTypeName(XElement context, string prefixedName)
    {
        prefixedName = prefixedName.Trim();
        var x = MarkupExtension.TryParse(prefixedName);
        if (x != null && x.Name is "x:Type") prefixedName = x.Named.GetValueOrDefault("TypeName") ?? x.Positional.FirstOrDefault() ?? string.Empty;

        var colon = prefixedName.IndexOf(':');
        if (colon < 0) return null; // tipo de WPF (TextBlock, Border...)
        var prefix = prefixedName[..colon];
        var name = prefixedName[(colon + 1)..];
        var ns = context.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        return ns == null ? null : ResolveClrType(ns, name);
    }

    private Type? ResolveClrType(string xmlNamespace, string name)
    {
        if (!xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal)) return null;
        var spec = xmlNamespace["clr-namespace:".Length..];
        var parts = spec.Split(';');
        var clrNamespace = parts[0];
        var assemblyName = parts.Skip(1).Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?["assembly=".Length..];
        var fullName = $"{clrNamespace}.{name}";
        if (string.IsNullOrEmpty(assemblyName) || assemblyName == _app.GetName().Name) return _app.GetType(fullName);
        return Type.GetType($"{fullName}, {assemblyName}");
    }

    /// <summary>Nombre de la clase de vista de un elemento (views:PatientsHomeView → PatientsHomeView) si es una vista registrada.</summary>
    private string? ViewClassOf(XElement element)
    {
        var ns = element.Name.NamespaceName;
        if (!ns.StartsWith("clr-namespace:TPVC.Views", StringComparison.Ordinal)) return null;
        return _viewRoots.ContainsKey(element.Name.LocalName) ? element.Name.LocalName : null;
    }

    // ================================================================== análisis de una vista

    private sealed class Frame
    {
        public required XElement Element { get; init; }
        public Type? Scope { get; init; }
        public Frame? Parent { get; init; }
        public Type? ItemType { get; set; }
        public bool ItemTypeKnown { get; set; }

        /// <summary>El ContextMenu (o ToolTip) vive en otro árbol visual: FindAncestor no pasa de aquí.</summary>
        public bool IsPopupBoundary { get; init; }

        /// <summary>Para un ContextMenu: el elemento al que pertenece (su PlacementTarget).</summary>
        public Frame? PlacementOwner { get; init; }

        public string Tag => Element.Name.LocalName;
    }

    private sealed class Session
    {
        public required string View { get; init; }
        public required XElement Root { get; init; }
        public required Type? RootType { get; init; }
        public required ViewAnalysis Result { get; init; }
        public Dictionary<string, XElement> Resources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AnalyzedResources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UsedResources { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Analiza el texto de una vista. Sin <paramref name="rootType"/> se toma su d:DataContext.</summary>
    public ViewAnalysis Analyze(string viewName, string xamlText, Type? rootType = null)
    {
        var doc = XDocument.Parse(xamlText, LoadOptions.SetLineInfo);
        var root = doc.Root!;
        rootType ??= DesignDataContextType(root);
        var result = new ViewAnalysis { View = viewName, RootType = rootType };
        var session = new Session { View = viewName, Root = root, RootType = rootType, Result = result };

        // Recursos con clave de todo el archivo (se analizan donde se usan)
        foreach (var dictionary in root.DescendantsAndSelf().Where(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)))
        {
            foreach (var item in dictionary.Elements())
            {
                var key = item.Attribute(XamlNs + "Key")?.Value;
                if (key != null) session.Resources[key] = item;
            }
        }

        Visit(session, root, rootType, parent: null);

        // Recursos sin uso en el archivo: los DataTemplate con DataType tienen su tipo; el resto queda
        // sin ámbito (sus bindings tendrían que estar en la lista revisada)
        foreach (var (key, element) in session.Resources)
        {
            if (session.UsedResources.Contains(key)) continue;
            var dataType = element.Attribute("DataType")?.Value is { } dt ? ResolveTypeName(element, dt) : null;
            AnalyzeResource(session, key, dataType, null);
        }

        // Plantillas implícitas (DataTemplate con DataType y sin clave)
        foreach (var dictionary in root.DescendantsAndSelf().Where(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)))
        {
            foreach (var item in dictionary.Elements().Where(e => e.Attribute(XamlNs + "Key") == null))
            {
                var dataType = item.Attribute("DataType")?.Value is { } dt ? ResolveTypeName(item, dt) : null;
                VisitObject(session, item, dataType, null);
            }
        }

        return result;
    }

    private void AnalyzeResource(Session session, string key, Type? scope, Frame? usage)
    {
        if (!session.Resources.TryGetValue(key, out var element)) return;
        if (!session.AnalyzedResources.Add($"{key}|{scope?.FullName ?? "?"}")) return;

        // Un estilo hereda los bindings de su BasedOn (con clave en el mismo archivo)
        if (element.Attribute("BasedOn")?.Value is { } basedOn && MarkupExtension.TryParse(basedOn) is { Name: "StaticResource" or "DynamicResource" } b
            && b.Positional.FirstOrDefault() is { } baseKey && session.Resources.ContainsKey(baseKey))
        {
            session.UsedResources.Add(baseKey);
            AnalyzeResource(session, baseKey, scope, usage);
        }

        VisitObject(session, element, scope, usage);
    }

    private void Visit(Session session, XElement element, Type? scope, Frame? parent)
    {
        if (IsPropertyElement(element)) return; // no ocurre en la raíz
        VisitObject(session, element, scope, parent);
    }

    private static bool IsPropertyElement(XElement element) => element.Name.LocalName.Contains('.');

    private void VisitObject(Session session, XElement element, Type? inheritedScope, Frame? parent)
    {
        var tag = element.Name.LocalName;
        var scope = inheritedScope;
        var preliminary = new Frame { Element = element, Scope = inheritedScope, Parent = parent };

        // DataContext propio: se resuelve contra el DataContext heredado
        var dataContextAttr = element.Attribute("DataContext");
        if (dataContextAttr != null)
        {
            var dc = MarkupExtension.TryParse(dataContextAttr.Value);
            if (dc is { Name: "Binding" })
            {
                scope = ResolveAttributeBinding(session, preliminary, dataContextAttr, dc, recordTwoWay: false);
            }
            else
            {
                scope = null;
                session.Result.Unresolved.Add(new UnresolvedBinding(session.View, LineOf(dataContextAttr), "DataContext", dataContextAttr.Value, "DataContext que no es un binding"));
            }
        }

        // Plantilla: DataType da el tipo si no se conoce; si se conoce, debe coincidir
        if (tag is "DataTemplate" or "HierarchicalDataTemplate" && element.Attribute("DataType") is { } dataTypeAttr)
        {
            var declared = ResolveTypeName(element, dataTypeAttr.Value);
            if (declared != null)
            {
                if (scope == null) scope = declared;
                else if (!declared.IsAssignableFrom(scope))
                {
                    session.Result.Problems.Add(new BindingProblem(session.View, LineOf(dataTypeAttr), "DataType", dataTypeAttr.Value,
                        $"la plantilla declara {declared.Name} pero recibe {scope.Name}"));
                }
            }
        }

        var frame = new Frame { Element = element, Scope = scope, Parent = parent };

        // Vista anidada: su d:DataContext debe aceptar lo que recibe
        if (ViewClassOf(element) is { } viewClass)
        {
            var expected = _viewRoots[viewClass];
            session.Result.HostChecks.Add(new HostCheck(session.View, LineOf(element), viewClass, expected, scope));
            if (expected != null)
            {
                if (scope == null)
                {
                    session.Result.Problems.Add(new BindingProblem(session.View, LineOf(element), viewClass, "(DataContext)",
                        $"no se pudo saber qué DataContext recibe {viewClass} (espera {expected.Name})"));
                }
                else if (!expected.IsAssignableFrom(scope))
                {
                    session.Result.Problems.Add(new BindingProblem(session.View, LineOf(element), viewClass, "(DataContext)",
                        $"{viewClass} espera {expected.Name} pero recibe {scope.Name}"));
                }
            }
        }

        // ItemsSource primero: define el ámbito de las plantillas de los elementos
        if (element.Attribute("ItemsSource") is { } itemsSource && MarkupExtension.TryParse(itemsSource.Value) is { Name: "Binding" } itemsBinding)
        {
            var collection = ResolveAttributeBinding(session, frame, itemsSource, itemsBinding, recordTwoWay: false);
            frame.ItemType = collection == null ? null : ElementTypeOf(collection);
            frame.ItemTypeKnown = true;
        }

        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            var ns = attr.Name.Namespace;
            if (ns == XamlNs || ns == DesignNs || ns == CompatNs) continue;
            var name = attr.Name.LocalName;
            if (name is "DataContext" or "ItemsSource") continue;

            if (name is "DisplayMemberPath" or "SelectedValuePath")
            {
                CheckMemberPath(session, frame, attr);
                continue;
            }

            var ext = MarkupExtension.TryParse(attr.Value);
            if (ext == null) continue;

            switch (ext.Name)
            {
                case "Binding":
                    ResolveAttributeBinding(session, frame, attr, ext, recordTwoWay: true);
                    break;
                case "StaticResource" or "DynamicResource":
                    UseResource(session, frame, name, ext.Positional.FirstOrDefault());
                    break;
            }
        }

        foreach (var child in element.Elements())
        {
            if (IsPropertyElement(child)) VisitPropertyElement(session, frame, child);
            else if (child.Name.LocalName == "Binding") ResolveBindingElement(session, frame, child, element.Name.LocalName);
            else if (child.Name.LocalName == "MultiBinding") VisitMultiBinding(session, frame, child, element.Name.LocalName);
            else VisitObject(session, child, frame.Scope, frame);
        }
    }

    private void VisitPropertyElement(Session session, Frame owner, XElement propertyElement)
    {
        var property = propertyElement.Name.LocalName[(propertyElement.Name.LocalName.IndexOf('.') + 1)..];
        if (property == "Resources") return; // se analizan donde se usan

        Type? scope = owner.Scope;
        Frame? placementOwner = null;
        bool popup = false;
        switch (property)
        {
            case "ItemTemplate" or "ItemContainerStyle" or "Columns" or "ItemTemplateSelector":
                scope = ItemScope(owner);
                break;
            case "ContentTemplate":
                scope = ContentScope(session, owner);
                break;
            case "ContextMenu":
                placementOwner = owner;
                popup = true;
                break;
            case "ToolTip":
                popup = true;
                break;
        }

        foreach (var child in propertyElement.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Binding":
                    ResolveBindingElement(session, owner, child, property);
                    break;
                case "MultiBinding":
                    VisitMultiBinding(session, owner, child, property);
                    break;
                default:
                    if (popup) VisitPopupChild(session, child, scope, owner, placementOwner);
                    else VisitObject(session, child, scope, owner);
                    break;
            }
        }
    }

    /// <summary>
    /// ContextMenu o ToolTip: vive en otro árbol visual. Toma el DataContext de su dueño (el
    /// PlacementTarget; MenuOnClick también le pone el del botón) y la búsqueda de ancestros se
    /// detiene en él.
    /// </summary>
    private void VisitPopupChild(Session session, XElement child, Type? scope, Frame owner, Frame? placementOwner)
    {
        var frame = new Frame
        {
            Element = child,
            Scope = scope,
            Parent = owner,
            IsPopupBoundary = true,
            PlacementOwner = placementOwner
        };
        foreach (var attr in child.Attributes())
        {
            if (attr.IsNamespaceDeclaration || attr.Name.Namespace == XamlNs || attr.Name.Namespace == DesignNs) continue;
            if (MarkupExtension.TryParse(attr.Value) is { Name: "Binding" } ext) ResolveAttributeBinding(session, frame, attr, ext, recordTwoWay: true);
        }
        foreach (var grandChild in child.Elements())
        {
            if (IsPropertyElement(grandChild)) VisitPropertyElement(session, frame, grandChild);
            else VisitObject(session, grandChild, scope, frame);
        }
    }

    private void VisitMultiBinding(Session session, Frame owner, XElement multi, string property)
    {
        var mode = multi.Attribute("Mode")?.Value;
        foreach (var binding in multi.Elements().Where(e => e.Name.LocalName == "Binding"))
        {
            ResolveBindingElement(session, owner, binding, property, inheritedMode: mode);
        }
    }

    /// <summary>&lt;Binding Path="X" .../&gt; como elemento (en MultiBinding o como valor de una propiedad).</summary>
    private void ResolveBindingElement(Session session, Frame owner, XElement bindingElement, string property, string? inheritedMode = null)
    {
        var ext = new MarkupExtension("Binding");
        foreach (var attr in bindingElement.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            ext.Named[attr.Name.LocalName] = attr.Value;
        }
        if (!ext.Named.ContainsKey("Mode") && inheritedMode != null) ext.Named["Mode"] = inheritedMode;
        var expression = "<Binding " + string.Join(" ", bindingElement.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => $"{a.Name.LocalName}=\"{a.Value}\"")) + "/>";
        Resolve(session, owner, property, LineOf(bindingElement), ext, expression, twoWayByDefault: IsTwoWayByDefault(owner.Tag, property));
    }

    private void UseResource(Session session, Frame frame, string attribute, string? key)
    {
        if (key == null || !session.Resources.TryGetValue(key, out _)) return;
        session.UsedResources.Add(key);
        var scope = attribute switch
        {
            "ItemContainerStyle" or "ItemTemplate" or "ItemTemplateSelector" => ItemScope(frame),
            "ContentTemplate" => ContentScope(session, frame),
            _ => frame.Scope
        };
        AnalyzeResource(session, key, scope, frame);
    }

    /// <summary>Tipo de los elementos del ItemsSource del dueño (null si no tiene un binding con tipo).</summary>
    private static Type? ItemScope(Frame owner) => owner.ItemTypeKnown ? owner.ItemType : null;

    private Type? ContentScope(Session session, Frame owner)
    {
        var content = owner.Element.Attribute("Content");
        if (content == null) return owner.Scope;
        var ext = MarkupExtension.TryParse(content.Value);
        if (ext is not { Name: "Binding" }) return null;
        return ResolveQuiet(session, owner, ext);
    }

    // ================================================================== resolución

    private Type? ResolveAttributeBinding(Session session, Frame frame, XAttribute attr, MarkupExtension ext, bool recordTwoWay)
    {
        var tag = frame.Tag;
        var property = attr.Name.LocalName;
        bool twoWay = recordTwoWay && IsTwoWayByDefault(tag, property);

        // Setter de un estilo: la propiedad la dice el atributo Property y el elemento el TargetType del estilo
        if (tag == "Setter" && property == "Value")
        {
            var target = frame.Element.Attribute("Property")?.Value ?? "?";
            var styleTag = StyleTargetTag(frame.Element);
            twoWay = IsTwoWayByDefault(styleTag, target.Contains('.') ? target[(target.LastIndexOf('.') + 1)..] : target);
            property = $"Setter({target})";
        }
        else if (tag is "DataTrigger" && property == "Binding")
        {
            property = "DataTrigger.Binding";
            twoWay = false;
        }
        else if (tag.StartsWith("DataGrid", StringComparison.Ordinal) && property == "Binding")
        {
            twoWay = false;
        }

        return Resolve(session, frame, property, LineOf(attr), ext, attr.Value, twoWay);
    }

    private static string StyleTargetTag(XElement setter)
    {
        var style = setter.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Style");
        var target = style?.Attribute("TargetType")?.Value ?? string.Empty;
        var ext = MarkupExtension.TryParse(target);
        if (ext != null) target = ext.Positional.FirstOrDefault() ?? ext.Named.GetValueOrDefault("TypeName") ?? string.Empty;
        return target.Contains(':') ? target[(target.IndexOf(':') + 1)..] : target;
    }

    /// <summary>Resuelve sin registrar nada (para Content, Tag...).</summary>
    private Type? ResolveQuiet(Session session, Frame frame, MarkupExtension ext)
    {
        var scratch = new ViewAnalysis();
        var quiet = new Session { View = session.View, Root = session.Root, RootType = session.RootType, Result = scratch };
        foreach (var (k, v) in session.Resources) quiet.Resources[k] = v;
        return Resolve(quiet, frame, "(interno)", 0, ext, string.Empty, twoWayByDefault: false);
    }

    private Type? Resolve(Session session, Frame frame, string attribute, int line, MarkupExtension ext, string expression, bool twoWayByDefault)
    {
        var path = (ext.Named.GetValueOrDefault("Path") ?? ext.Positional.FirstOrDefault() ?? string.Empty).Trim();
        var mode = ext.Named.GetValueOrDefault("Mode")?.Trim();
        bool requiresSetter = mode is "TwoWay" or "OneWayToSource" || (mode == null && twoWayByDefault);

        void Unresolved(string reason) =>
            session.Result.Unresolved.Add(new UnresolvedBinding(session.View, line, attribute, expression, reason));

        if (ext.Named.ContainsKey("Source") || ext.Named.ContainsKey("ElementName"))
        {
            Unresolved(ext.Named.ContainsKey("Source") ? "Source" : "ElementName");
            return null;
        }

        Type? start = frame.Scope;
        string rest = path;
        string scopeDescription = "DataContext";

        if (ext.Named.TryGetValue("RelativeSource", out var relativeText))
        {
            var relative = MarkupExtension.TryParse(relativeText);
            var relMode = relative?.Named.GetValueOrDefault("Mode") ?? relative?.Positional.FirstOrDefault();
            var ancestorTypeText = relative?.Named.GetValueOrDefault("AncestorType");
            if (ancestorTypeText == null)
            {
                Unresolved($"RelativeSource {relMode ?? "?"}");
                return null;
            }
            var ancestorTag = LocalTypeName(ancestorTypeText);
            int level = int.TryParse(relative!.Named.GetValueOrDefault("AncestorLevel"), out var l) ? l : 1;
            var ancestor = FindAncestor(frame, ancestorTag, level);
            if (ancestor == null)
            {
                session.Result.Problems.Add(new BindingProblem(session.View, line, attribute, expression,
                    $"no hay un ancestro {ancestorTag} alcanzable (un ContextMenu o ToolTip no ve el árbol de su dueño)"));
                return null;
            }

            var segments = SplitPath(path);
            if (segments.Count > 0 && segments[0] == "DataContext")
            {
                start = ancestor.Scope;
                rest = string.Join(".", segments.Skip(1));
                scopeDescription = $"DataContext del {ancestorTag}";
            }
            else if (ancestor.PlacementOwner != null && segments.Count > 1 && segments[0] == "PlacementTarget")
            {
                var owner = ancestor.PlacementOwner;
                if (segments[1] == "DataContext")
                {
                    start = owner.Scope;
                    scopeDescription = "DataContext del PlacementTarget";
                }
                else if (segments[1] == "Tag")
                {
                    var tagAttr = owner.Element.Attribute("Tag");
                    var tagExt = tagAttr == null ? null : MarkupExtension.TryParse(tagAttr.Value);
                    if (tagExt is not { Name: "Binding" })
                    {
                        session.Result.Problems.Add(new BindingProblem(session.View, line, attribute, expression,
                            "PlacementTarget.Tag sin un binding en Tag del elemento dueño del menú"));
                        return null;
                    }
                    start = ResolveQuiet(session, owner, tagExt);
                    scopeDescription = "Tag del PlacementTarget";
                }
                else
                {
                    Unresolved("propiedad del PlacementTarget (elemento de WPF)");
                    return null;
                }
                rest = string.Join(".", segments.Skip(2));
            }
            else
            {
                Unresolved($"propiedad del {ancestorTag} (elemento de WPF)");
                return null;
            }
        }

        if (start == null)
        {
            Unresolved($"{scopeDescription} sin tipo conocido");
            return null;
        }

        var resolution = ResolvePath(start, rest);
        if (resolution.Error != null)
        {
            if (resolution.Unresolvable) Unresolved(resolution.Error);
            else session.Result.Problems.Add(new BindingProblem(session.View, line, attribute, expression, resolution.Error));
            return null;
        }

        if (resolution.NotForBinding != null)
        {
            session.Result.Problems.Add(new BindingProblem(session.View, line, attribute, expression,
                $"{resolution.NotForBinding} lleva [NotForBinding]: es de la fachada en C#, no se enlaza desde XAML"));
        }

        if (requiresSetter && resolution.Property != null && resolution.Property.SetMethod?.IsPublic != true)
        {
            session.Result.Problems.Add(new BindingProblem(session.View, line, attribute, expression,
                $"binding TwoWay a {resolution.Owner} que no tiene setter público"));
        }

        session.Result.Bindings.Add(new ResolvedBinding(session.View, line, attribute, path.Length == 0 ? "." : path, resolution.Owner));
        return resolution.Type;
    }

    /// <summary>
    /// RelativeSource FindAncestor: empieza en el padre del elemento destino y sube; un ContextMenu o
    /// ToolTip se puede encontrar a sí mismo como ancestro de sus elementos, pero no ve más arriba.
    /// </summary>
    private Frame? FindAncestor(Frame frame, string ancestorTag, int level)
    {
        int found = 0;
        var f = frame.IsPopupBoundary ? null : frame.Parent;
        while (f != null)
        {
            if (Matches(f, ancestorTag) && ++found == level) return f;
            if (f.IsPopupBoundary) return null;
            f = f.Parent;
        }
        return null;
    }

    private bool Matches(Frame f, string ancestorTag)
    {
        var tag = f.Tag;
        if (tag == ancestorTag) return true;
        if (ancestorTag == "ItemsControl" && ItemsControlTags.Contains(tag)) return true;
        // Una vista anidada (views:XView) también es un UserControl
        return ancestorTag == "UserControl" && ViewClassOf(f.Element) != null;
    }

    private static string LocalTypeName(string typeText)
    {
        var ext = MarkupExtension.TryParse(typeText);
        if (ext != null) typeText = ext.Positional.FirstOrDefault() ?? ext.Named.GetValueOrDefault("TypeName") ?? typeText;
        typeText = typeText.Trim();
        return typeText.Contains(':') ? typeText[(typeText.IndexOf(':') + 1)..] : typeText;
    }

    private void CheckMemberPath(Session session, Frame frame, XAttribute attr)
    {
        if (string.IsNullOrWhiteSpace(attr.Value)) return;
        var line = LineOf(attr);
        if (!frame.ItemTypeKnown || frame.ItemType == null)
        {
            session.Result.Unresolved.Add(new UnresolvedBinding(session.View, line, attr.Name.LocalName, attr.Value, "elementos de ItemsSource sin tipo conocido"));
            return;
        }
        var resolution = ResolvePath(frame.ItemType, attr.Value);
        if (resolution.Error != null)
        {
            if (resolution.Unresolvable) session.Result.Unresolved.Add(new UnresolvedBinding(session.View, line, attr.Name.LocalName, attr.Value, resolution.Error));
            else session.Result.Problems.Add(new BindingProblem(session.View, line, attr.Name.LocalName, attr.Value, resolution.Error));
            return;
        }
        session.Result.Bindings.Add(new ResolvedBinding(session.View, line, attr.Name.LocalName, attr.Value, resolution.Owner));
    }

    // ================================================================== rutas

    internal sealed record PathResolution(Type? Type, PropertyInfo? Property, string Owner, string? Error, bool Unresolvable, string? NotForBinding);

    private static List<string> SplitPath(string path)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        foreach (var c in path)
        {
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == '.' && depth == 0)
            {
                segments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0 || segments.Count > 0) segments.Add(current.ToString().Trim());
        return segments.Where(s => s.Length > 0).ToList();
    }

    /// <summary>Sigue una ruta de propiedades públicas de instancia (con índices [n] en listas).</summary>
    internal static PathResolution ResolvePath(Type start, string path)
    {
        var segments = SplitPath(path);
        if (segments.Count == 0 || path.Trim() == ".")
        {
            return new PathResolution(start, null, $"{FriendlyName(start)}.", null, false, null);
        }

        var type = start;
        PropertyInfo? property = null;
        string? notForBinding = null;
        foreach (var raw in segments)
        {
            var segment = raw;
            if (segment.StartsWith('('))
            {
                return new PathResolution(null, null, string.Empty, $"propiedad adjunta {segment}", true, null);
            }

            string? index = null;
            var bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                index = segment[bracket..];
                segment = segment[..bracket];
            }

            if (type == typeof(object))
            {
                return new PathResolution(null, null, string.Empty, $"'{segment}' sobre un valor de tipo object", true, null);
            }

            if (segment.Length > 0)
            {
                property = FindProperty(type, segment);
                if (property == null)
                {
                    return new PathResolution(null, null, string.Empty, $"{FriendlyName(type)} no tiene la propiedad pública '{segment}'", false, null);
                }
                if (notForBinding == null && property.CustomAttributes.Any(a => a.AttributeType.Name == "NotForBindingAttribute"))
                {
                    notForBinding = $"{FriendlyName(property.DeclaringType!)}.{property.Name}";
                }
                type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            }

            if (index != null)
            {
                var element = ElementTypeOf(type);
                if (element == null)
                {
                    return new PathResolution(null, null, string.Empty, $"índice {index} sobre {FriendlyName(type)}", true, null);
                }
                type = element;
                property = null;
            }
        }

        var owner = property != null ? $"{FriendlyName(property.DeclaringType!)}.{property.Name}" : $"{FriendlyName(type)}[]";
        return new PathResolution(type, property, owner, null, false, notForBinding);
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        // La más derivada si una propiedad oculta a otra (new)
        for (var t = type; t != null; t = t.BaseType)
        {
            var p = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(x => x.Name == name && x.GetIndexParameters().Length == 0);
            if (p != null) return p;
        }
        if (type.IsInterface)
        {
            foreach (var i in type.GetInterfaces())
            {
                var p = i.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p;
            }
        }
        return null;
    }

    internal static Type? ElementTypeOf(Type collection)
    {
        if (collection == typeof(string)) return null;
        if (collection.IsArray) return collection.GetElementType();
        if (collection.IsGenericType && collection.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return collection.GetGenericArguments()[0];
        var enumerable = collection.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    internal static string FriendlyName(Type type)
    {
        if (!type.IsGenericType) return type.Name;
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FriendlyName))}>";
    }

    // ================================================================== TwoWay

    /// <summary>Propiedades de WPF que enlazan TwoWay si no se indica Mode (FrameworkPropertyMetadata.BindsTwoWayByDefault).</summary>
    internal static bool IsTwoWayByDefault(string elementTag, string property)
    {
        var tag = elementTag.Contains(':') ? elementTag[(elementTag.IndexOf(':') + 1)..] : elementTag;
        if (property.EndsWith("PasswordBoxHelper.BoundPassword", StringComparison.Ordinal)) return true;
        if (property.Contains('.')) property = property[(property.LastIndexOf('.') + 1)..];
        return property switch
        {
            "Text" => tag is "TextBox" or "ComboBox" or "DatePicker" or "Run",
            "IsChecked" => tag is "CheckBox" or "RadioButton" or "ToggleButton" or "MenuItem",
            "SelectedItem" or "SelectedValue" or "SelectedIndex" => tag is "ListBox" or "ComboBox" or "ListView" or "TabControl" or "DataGrid" or "Selector",
            "SelectedDate" or "DisplayDate" => tag is "DatePicker" or "Calendar",
            "Value" => tag is "Slider" or "ScrollBar" or "ProgressBar" or "RangeBase",
            "IsExpanded" => tag is "Expander" or "TreeViewItem",
            "IsOpen" => tag is "Popup" or "ContextMenu" or "ToolTip",
            "IsDropDownOpen" => tag is "ComboBox",
            "IsSubmenuOpen" => tag is "MenuItem",
            "IsSelected" => tag is "ListBoxItem" or "TabItem" or "TreeViewItem" or "ComboBoxItem" or "ListViewItem",
            _ => false
        };
    }

    private static int LineOf(XObject obj) => obj is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
}

/// <summary>Extensión de marcado de XAML: {Nombre posicional, Clave=Valor, ...} con anidamiento y comillas simples.</summary>
internal sealed class MarkupExtension
{
    public MarkupExtension(string name) => Name = name;

    public string Name { get; }
    public List<string> Positional { get; } = new();
    public Dictionary<string, string> Named { get; } = new(StringComparer.Ordinal);

    public static MarkupExtension? TryParse(string? text)
    {
        if (text == null) return null;
        text = text.Trim();
        if (text.Length < 2 || text[0] != '{' || text.StartsWith("{}", StringComparison.Ordinal) || text[^1] != '}') return null;
        var inner = text[1..^1].Trim();
        int i = 0;
        while (i < inner.Length && !char.IsWhiteSpace(inner[i]) && inner[i] != ',') i++;
        var ext = new MarkupExtension(inner[..i]);
        foreach (var arg in SplitTopLevel(inner[i..], ','))
        {
            var a = arg.Trim();
            if (a.Length == 0) continue;
            var eq = IndexOfTopLevel(a, '=');
            if (eq > 0 && Regex.IsMatch(a[..eq].Trim(), @"^[A-Za-z_][\w:.]*$"))
            {
                ext.Named[a[..eq].Trim()] = Unquote(a[(eq + 1)..].Trim());
            }
            else
            {
                ext.Positional.Add(Unquote(a));
            }
        }
        return ext;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' ? value[1..^1] : value;

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        int depth = 0;
        bool quoted = false;
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (c == '\'' ) quoted = !quoted;
            else if (!quoted && c == '{') depth++;
            else if (!quoted && c == '}') depth--;
            if (c == separator && depth == 0 && !quoted)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        yield return current.ToString();
    }

    private static int IndexOfTopLevel(string text, char target)
    {
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'') quoted = !quoted;
            else if (!quoted && c == '{') depth++;
            else if (!quoted && c == '}') depth--;
            else if (c == target && depth == 0 && !quoted) return i;
        }
        return -1;
    }

    /// <summary>Texto de la expresión con los espacios reducidos (para comparar con la lista revisada).</summary>
    public static string Canonical(string expression) => Regex.Replace(expression.Trim(), @"\s+", " ");
}
