using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Documents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Receta (v1.23.0): el medicamento en el orden del Reglamento de Insumos para la Salud (genérico, concentración y
/// forma, la marca entre paréntesis y la presentación aparte), carta cuando no cabe en una media carta y las letras
/// que elige cada equipo.
/// </summary>
[TestClass]
public class PrescriptionNormTests
{
    [TestMethod]
    [DataRow("Paracetamol 500 mg", "Paracetamol", "Caja con 20 tabletas", "Paracetamol 500 mg tabletas", DisplayName = "Genérico: sin marca")]
    [DataRow("Tempra 500 mg", "Paracetamol", "Caja con 20 tabletas", "Paracetamol 500 mg tabletas (Tempra)", DisplayName = "De marca")]
    [DataRow("VALCLAN 875/125MG C/14 TABS", "Amoxicilina/Ácido clavulánico", "Caja con 14 tabletas",
        "Amoxicilina/Ácido clavulánico 875/125 mg tabletas (Valclan)", DisplayName = "Del correo de LUIN")]
    [DataRow("Amoxicilina / Ácido Clavulánico 500/125 mg", "Amoxicilina / Clavulanato", "Caja con 14 tabletas",
        "Amoxicilina / Clavulanato 500/125 mg tabletas", DisplayName = "Nombre genérico distinto al del campo")]
    [DataRow("SUPRATEX JARABE 600MG/100 ML 120ML", "Ambroxol", null, "Ambroxol 600 mg/100 ml jarabe (Supratex)", DisplayName = "Jarabe con concentración por volumen")]
    [DataRow("DOLPRIN SUSPENSION 2G/100 ML 120 ML", "Paracetamol", null, "Paracetamol 2 g/100 ml suspensión (Dolprin)", DisplayName = "Suspensión en gramos")]
    [DataRow("NAVONTEC INY 8MG C/3 AMP", "Ondansetrón", "Caja con 3 ampolletas", "Ondansetrón 8 mg solución inyectable (Navontec)", DisplayName = "Inyectable")]
    [DataRow("Gasa Estéril 10x10 cm", null, "Paquete con 10 piezas", "Gasa Estéril 10x10 cm", DisplayName = "Sin genérico: el nombre tal cual")]
    [DataRow("Ibuprofeno 400 mg", "Ibuprofeno 400 mg", "Caja con 10 tabletas", "Ibuprofeno 400 mg tabletas", DisplayName = "El genérico ya trae la concentración")]
    public void TheMedicine_IsWrittenInTheMexicanNormOrder(string name, string? generic, string? presentation, string expected)
    {
        var product = new Product { Name = name, GenericName = generic, Presentation = presentation };
        Assert.AreEqual(expected, PrescriptionNaming.MedicineLine(product));
        Assert.AreEqual(presentation ?? string.Empty, PrescriptionNaming.Presentation(product));
    }

    [TestMethod]
    public void TheStrength_IsReadWithItsUnit_AndAVolumeAloneIsNotOne()
    {
        Assert.AreEqual("500 mg", PrescriptionNaming.Strength("Paracetamol 500mg"));
        Assert.AreEqual("7/1.5 mg/ml", PrescriptionNaming.Strength("ATROXOLAM JARABE 7/1.5MG/ML 150ML"));
        Assert.AreEqual("0.9 %", PrescriptionNaming.Strength("Solución Fisiológica 0.9%"));
        Assert.AreEqual("100000 UI", PrescriptionNaming.Strength("Vitamina A 100000 UI"));
        Assert.IsNull(PrescriptionNaming.Strength("BIOBEND SOLUCION BUCAL 360ML"), "Un volumen solo es la presentación");
        Assert.IsNull(PrescriptionNaming.Strength("Caja C/14 TABS"));
    }

    [TestMethod]
    public void TheSheet_PutsThePresentationNextToTheName_WithoutQuantities()
    {
        var items = new List<PatientPrescriptionItem>
        {
            new() { MedicineName = "Paracetamol 500 mg tabletas (Tempra)", Presentation = "Caja con 20 tabletas", Dosage = "1 tableta vía oral cada 8 horas por 5 días", RecommendedQuantity = 2 },
            new() { MedicineName = "Espabion suspensión", Dosage = "5 ml vía oral cada 8 horas" }
        };
        var sheet = PrescriptionSheet.Create(new PatientRecord { FullName = "Ana López" }, items, new ClinicProfile(), null, new DateTime(2026, 10, 10));

        Assert.AreEqual(" · Caja con 20 tabletas", sheet.Medicines[0].Detail, "Sin «Surtir»: la receta no lleva cantidades");
        Assert.AreEqual(string.Empty, sheet.Medicines[1].Detail, "Las recetas de antes no traen presentación");
        Assert.AreEqual(PrescriptionFonts.DefaultBody, sheet.BodyFont);
        Assert.AreEqual(PrescriptionPaper.HalfLetter, sheet.Paper, "Sin medir, media carta");
    }

    [TestMethod]
    public void TheHtml_UsesTheChosenFonts_TheLetterPaper_AndShowsThePresentation()
    {
        var items = new List<PatientPrescriptionItem>
        {
            new() { MedicineName = "Paracetamol 500 mg tabletas", Presentation = "Caja con 20 tabletas", Dosage = "1 tableta cada 8 horas" }
        };
        var sheet = PrescriptionSheet.Create(new PatientRecord { FullName = "Ana López" }, items, new ClinicProfile { DoctorName = "Dra. Prueba" }, null,
            new DateTime(2026, 10, 10), PrescriptionFonts.From("Georgia", "Lucida Handwriting"));
        sheet.Paper = PrescriptionPaper.Letter;

        var html = PrintingService.BuildPrescriptionHtml(sheet);
        StringAssert.Contains(html, "@page { size: 8.5in 11in;");
        StringAssert.Contains(html, "min-height: 11in;");
        StringAssert.Contains(html, "font-family: 'Georgia', 'Segoe UI', 'Arial', sans-serif;");
        StringAssert.Contains(html, "font-family: 'Lucida Handwriting', 'Segoe Script', 'Segoe UI', cursive;");
        StringAssert.Contains(html, "<div class=\"med-name\">Paracetamol 500 mg tabletas<span class=\"med-detail\"> &#183; Caja con 20 tabletas</span></div>", "El punto medio va codificado");

        sheet.Paper = PrescriptionPaper.HalfLetter;
        StringAssert.Contains(PrintingService.BuildPrescriptionHtml(sheet), "@page { size: 8.5in 5.5in;");
    }

    [TestMethod]
    public void TheChosenFonts_KeepTheUsualOnesBehind()
    {
        Assert.AreEqual(PrescriptionFonts.Default, PrescriptionFonts.From(null, " "));
        var fonts = PrescriptionFonts.From("Georgia", "Brush Script MT");
        Assert.AreEqual("Georgia, Segoe UI, Arial", fonts.Body);
        Assert.AreEqual("Brush Script MT, Segoe Script, Segoe UI", fonts.Doctor);

        var preferences = new LocalPreferencesStore(Path.Combine(Path.GetTempPath(), $"tpvc_pref_{Guid.NewGuid():N}.json"));
        preferences.Current.PrescriptionBodyFont = "Georgia";
        var printing = new PrintingService(preferences);
        Assert.AreEqual("Georgia, Segoe UI, Arial", printing.PrescriptionFonts.Body);
        Assert.AreEqual(PrescriptionFonts.DefaultDoctor, printing.PrescriptionFonts.Doctor);
    }

    // ================================================================== papel (WPF: solo en Windows)

    private static PrescriptionSheet SheetWith(int medicines, string posology = "1 tableta vía oral cada 8 horas por 5 días") =>
        PrescriptionSheet.Create(new PatientRecord { FullName = "Ana López" },
            Enumerable.Range(1, medicines).Select(i => new PatientPrescriptionItem
            {
                MedicineName = $"Medicamento {i} 500 mg tabletas",
                Presentation = "Caja con 20 tabletas",
                Dosage = posology
            }).ToList(),
            new ClinicProfile { DoctorName = "Dra. Prueba" }, "Reposo relativo.", new DateTime(2026, 10, 10));

    [TestMethod]
    [TestCategory("Windows")]
    public void APrescriptionThatFitsInAHalfLetter_StaysInHalfLetter()
    {
        Assert.AreEqual(PrescriptionPaper.HalfLetter, PrintingService.MeasurePaper(SheetWith(3)));
    }

    [TestMethod]
    [TestCategory("Windows")]
    public void APrescriptionThatDoesNotFit_GoesToLetter_AndFitsInOneLetterPage()
    {
        var sheet = SheetWith(9, "1 tableta vía oral cada 8 horas por 7 días; si persiste el dolor, tomar otra a las 4 horas sin pasar de 4 al día. Suspender si hay ronchas.");
        Assert.AreEqual(PrescriptionPaper.Letter, PrintingService.MeasurePaper(sheet));

        // En carta cabe en una sola hoja
        int pages = 0;
        var thread = new System.Threading.Thread(() =>
        {
            var doc = PrintingService.BuildPrescriptionDocument(sheet, PrescriptionSheet.PageWidth, PrescriptionSheet.LetterPageHeight);
            var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
            paginator.PageSize = new System.Windows.Size(PrescriptionSheet.PageWidth, PrescriptionSheet.LetterPageHeight);
            paginator.ComputePageCount();
            pages = paginator.PageCount;
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.AreEqual(1, pages);

        var (width, height) = PrintingService.PrescriptionPageSize(816, 1056, PrescriptionPaper.Letter);
        Assert.AreEqual((816.0, 1056.0), (width, height));
    }

    [TestMethod]
    [TestCategory("Windows")]
    public void TheProfileScreen_ListsTheInstalledFonts_AndSavesTheChoiceOnThisComputer()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tpvc_letras_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var profile = new ClinicProfileService(Path.Combine(folder, "perfil.json"));
            var preferencesFile = Path.Combine(folder, "preferencias.json");
            var vm = new ClinicProfileViewModel(profile, preferences: new LocalPreferencesStore(preferencesFile));

            Assert.AreEqual("La de siempre", vm.SelectedBodyFont.Label);
            Assert.AreEqual(PrescriptionFonts.DefaultBody, vm.BodyFontFamily);
            var installed = vm.BodyFontChoices.First(c => c.Family != null);

            vm.SelectedBodyFont = installed;
            StringAssert.StartsWith(vm.BodyFontFamily, installed.Family + ",");

            var again = new ClinicProfileViewModel(profile, preferences: new LocalPreferencesStore(preferencesFile));
            Assert.AreEqual(installed, again.SelectedBodyFont, "Se guardó en las preferencias del equipo");
            Assert.AreEqual(installed.Family, new LocalPreferencesStore(preferencesFile).Current.PrescriptionBodyFont);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
