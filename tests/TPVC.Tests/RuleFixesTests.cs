using System;
using System.Globalization;
using System.Linq;
using TPVC.Models;
using TPVC.Services.Rules;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Revisión 20: las reglas que el paso 8.26 dejó documentadas como defectos, cada una con su prueba (además de los casos de
/// sus vectores): la alergia escrita con la marca, el sulfato que no es sulfa, la familia en mayúsculas sin la cultura, los
/// acentos al buscar el producto de un renglón, el IMC con la talla en centímetros, el teléfono con lada del país, la coma
/// decimal del efectivo y el prefijo de cantidad con dígitos de otros alfabetos.
/// </summary>
[TestClass]
public class RuleFixesTests
{
    private static readonly Product[] Catalog =
    {
        new() { Id = 1, Name = "TEMPRA (PARACETAMOL) 500MG C/20 TABS", GenericName = "Paracetamol" },
        new() { Id = 2, Name = "Paracetamol 500 mg", GenericName = "Paracetamol" },
        new() { Id = 3, Name = "Ácido fólico 5 mg", GenericName = "Ácido fólico", Presentation = "Caja con 30 tabletas" },
    };

    // ================================================================== alergias

    [TestMethod]
    public void Allergy_WrittenWithTheBrand_WarnsWithItsGeneric_WhenTheCatalogKnowsIt()
    {
        var patient = new PatientRecord { Allergies = "Tempra" };
        var withCatalog = AllergyRules.CheckMedication(patient, Catalog[1], "Paracetamol 500 mg", catalog: Catalog);
        Assert.IsTrue(withCatalog.HasConflict);
        Assert.AreEqual("Tempra", withCatalog.MatchedAllergy);
        Assert.AreEqual("El paciente registra alergia a 'Tempra' (Paracetamol). El medicamento recetado contiene 'Paracetamol 500 mg'.", withCatalog.WarningDetails);

        Assert.IsFalse(AllergyRules.CheckMedication(patient, Catalog[1], "Paracetamol 500 mg").HasConflict, "Sin catálogo, la regla de antes");
        Assert.IsFalse(AllergyRules.CheckMedication(patient, null, "Loratadina 10 mg", catalog: Catalog).HasConflict);
    }

    [TestMethod]
    public void Allergy_FerrousSulfate_IsNotASulfonamide_ButRealSulfasStillWarn()
    {
        Assert.IsFalse(AllergyRules.CheckMedication(new PatientRecord { Allergies = "Sulfato ferroso" }, null, "Bactrim F").HasConflict);
        Assert.IsFalse(AllergyRules.CheckMedication(new PatientRecord { Allergies = "Sulfas" }, null, "Sulfato ferroso 200 mg").HasConflict);
        Assert.IsFalse(AllergyRules.CheckMedication(new PatientRecord { Allergies = "sulfa" }, null, "Salbutamol sulfato").HasConflict);

        Assert.IsTrue(AllergyRules.CheckMedication(new PatientRecord { Allergies = "Sulfas" }, null, "Bactrim F").HasConflict);
        Assert.IsTrue(AllergyRules.CheckMedication(new PatientRecord { Allergies = "Sulfonamidas" }, null, "Trimetoprim-sulfametoxazol").HasConflict);
        Assert.IsTrue(AllergyRules.CheckMedication(new PatientRecord { Allergies = "sulfa" }, null, "Sulfadiazina de plata").HasConflict);
        Assert.IsTrue(AllergyRules.CheckMedication(new PatientRecord { Allergies = "Sulfato ferroso" }, null, "Sulfato ferroso 200 mg").HasConflict, "Directo, como antes");
    }

    [TestMethod]
    public void Allergy_FamilyIsUppercasedWithoutTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var warning = AllergyRules.CheckMedication(new PatientRecord { Allergies = "AINEs" }, null, "Ibuprofeno 400 mg").WarningDetails;
            StringAssert.Contains(warning, "(grupo AINES)");
            Assert.IsFalse(warning.Contains('İ'), warning);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ================================================================== acentos

    [TestMethod]
    public void FindProductByName_IgnoresAccents_LikeProductMatchesName_SoTheLineIsCharged()
    {
        Assert.AreEqual(3, PatientSheetRows.FindProductByName(Catalog, "Acido folico 5 mg")?.Id);
        Assert.IsTrue(CatalogMatchingRules.ProductMatchesName(Catalog[2], "Acido folico 5 mg"), "Las dos reglas dicen lo mismo");
        Assert.AreEqual(1, ConsultationAccountRules.PrescriptionChargeQuantity(new PatientPrescriptionItem { RecommendedQuantity = 1 },
            PatientSheetRows.FindProductByName(Catalog, "Acido folico 5 mg")), "«Se surte aquí» lo cobra");

        // Nunca "empieza con": otra cosa que empieza igual no es ese producto
        Assert.IsNull(PatientSheetRows.FindProductByName(Catalog, "Acido folico 5 mg cada 24 h"));
        Assert.IsNull(PatientSheetRows.FindProductByName(Catalog, "Acido"));

        // Con acentos gana el que se escribió igual, aunque otro sin acentos vaya antes
        var twin = new Product { Id = 9, Name = "Acido folico 5 mg" };
        Assert.AreEqual(3, PatientSheetRows.FindProductByName(new[] { twin, Catalog[2] }, "Ácido fólico 5 mg")?.Id);
    }

    // ================================================================== IMC, teléfono, efectivo y prefijo

    [TestMethod]
    public void Bmi_WhileTyping_ConvertsCentimetres_LikeTheSavedNoteReader()
    {
        Assert.AreEqual("24.2 kg/m² (Peso normal)", VitalSignsRules.BmiText("70", "170"));
        Assert.AreEqual(VitalSignsRules.BmiText("70", "1.70"), VitalSignsRules.BmiText("70", "170"));

        var note = new PatientConsultation();
        note.ParseAndSetVitalSigns(VitalSignsRules.Format(null, null, null, null, "70", "170", null, null, VitalSignsRules.BmiText("70", "170")));
        Assert.AreEqual(1.70m, note.Height, "El lector usa la misma regla");
        Assert.AreEqual(1.70m, VitalSignsRules.HeightInMeters(170m));
        Assert.AreEqual(3m, VitalSignsRules.HeightInMeters(3m));
    }

    [TestMethod]
    public void Phone_KeepsTheLastTenDigits_LikeTheDuplicateRule()
    {
        Assert.AreEqual("551-234-5678", PatientRecord.FormatPhoneNumber("+52 55 1234 5678"));
        Assert.AreEqual(DuplicatePatientRules.PhoneKey("+52 55 1234 5678"), new string(PatientRecord.FormatPhoneNumber("+52 55 1234 5678").Where(char.IsAsciiDigit).ToArray()));
        Assert.AreEqual("551-234-5678", PatientRecord.FormatPhoneNumber("5512345678"));
    }

    [TestMethod]
    public void CashReceived_ACommaWithOneOrTwoDigits_IsTheDecimalPoint()
    {
        var checkout = new CheckoutViewModel(null) { CashReceivedText = "1,5" };
        Assert.AreEqual(1.5m, checkout.CashReceived, "Antes era 15");
        checkout.CashReceivedText = "150,50";
        Assert.AreEqual(150.50m, checkout.CashReceived);
        checkout.CashReceivedText = "1,500";
        Assert.AreEqual(1500m, checkout.CashReceived, "Tres dígitos: miles");
        checkout.CashReceivedText = "1,500.50";
        Assert.AreEqual(1500.50m, checkout.CashReceived);
        Assert.AreEqual("1,500.50", checkout.CashReceivedText, "Lo escrito no se reformatea");
    }

    [TestMethod]
    public void QuantityPrefix_WithDigitsOfAnotherScript_DoesNotThrow()
    {
        Assert.AreEqual((1, "٣*para"), CatalogSearchRules.ParseQuantityPrefix("٣*para"));
        Assert.AreEqual((1, "３*gasa"), CatalogSearchRules.ParseQuantityPrefix("３*gasa"));
        Assert.AreEqual((3, "para"), CatalogSearchRules.ParseQuantityPrefix("3*para"));
    }
}
