using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TPVC.Tests;

/// <summary>
/// Fase 7 (7.6): la ventana de confirmaciones de la app conserva lo que hacía el MessageBox con "No" por
/// defecto. Se revisa el XAML (corre también en Linux); la prueba de pantallas de Windows la contesta de
/// verdad (UiKeyboardPass.ThemedConfirmations) y la dibuja en los 4 temas (capturas 26 y 26b).
/// </summary>
[TestClass]
public class ConfirmDialogTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Dialog() =>
        XDocument.Load(Path.Combine(Fase6.Fase6Assets.RepoRoot(), "Views", "ConfirmDialog.xaml")).Root!;

    private static string? Attr(XElement element, XName name) => element.Attribute(name)?.Value;

    [TestMethod]
    public void SafeAnswer_IsWhatEnterAndEscapePress_AndYesMustBeChosen()
    {
        var buttons = Dialog().Descendants(Wpf + "Button").ToList();
        Assert.HasCount(2, buttons, "La confirmación lleva dos botones");

        // Esc (IsCancel) y Enter (IsDefault) tocan el mismo botón: "No", como el MessageBox con No por defecto
        var safe = buttons.Single(b => Attr(b, "IsCancel") == "True");
        Assert.AreEqual("True", Attr(safe, "IsDefault"), "Enter debe contestar lo seguro");
        Assert.AreEqual("SafeButton", Attr(safe, X + "Name"), "El código detrás pone el foco inicial en SafeButton");
        Assert.AreEqual("No", Attr(safe, "Content"));
        Assert.AreEqual("{StaticResource ButtonSecondary}", Attr(safe, "Style"));
        // Sin manejador: con IsCancel, WPF cierra la ventana modal sin confirmar (como Esc)
        Assert.IsNull(Attr(safe, "Click"), "El botón seguro no necesita manejador (IsCancel cierra sin confirmar)");

        var yes = buttons.Single(b => !ReferenceEquals(b, safe));
        Assert.AreEqual("Sí", Attr(yes, "Content"));
        Assert.AreEqual("{StaticResource ButtonDanger}", Attr(yes, "Style"), "Todas las confirmaciones protegen algo que no se puede deshacer");
        Assert.IsNull(Attr(yes, "IsDefault"), "\"Sí\" no puede ser el de Enter");
        Assert.IsNull(Attr(yes, "IsCancel"));
        Assert.AreEqual("OnConfirmClick", Attr(yes, "Click"));
    }

    [TestMethod]
    public void Window_IsAModalSheetOverTheMainWindow()
    {
        var window = Dialog();
        Assert.AreEqual(Wpf + "Window", window.Name);
        Assert.AreEqual("False", Attr(window, "ShowInTaskbar"));
        Assert.AreEqual("CenterOwner", Attr(window, "WindowStartupLocation"));
        Assert.AreEqual("None", Attr(window, "WindowStyle"), "Sin marco de Windows (no sigue el tema)");

        var card = window.Elements().Single();
        Assert.AreEqual("{StaticResource SheetCard}", Attr(card, "Style"), "La tarjeta es la de las hojas de la app (fondo, borde y sombra del tema)");
    }
}
