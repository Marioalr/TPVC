using System.Collections.Generic;
using System.Linq;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Pedido del consultorio del 2026-10-14: al recetar, los medicamentos con existencias van primero
/// (<see cref="MedicineSearchRules"/>). Antes se tomaban los primeros 20 que coincidían en el orden del catálogo y un
/// medicamento con existencias podía quedar fuera.
/// </summary>
[TestClass]
public class MedicineSearchRulesTests
{
    private static Product Med(string code, string name, string? generic = null, int stock = 0, bool active = true, string? presentation = null) =>
        new() { Code = code, Name = name, GenericName = generic, CurrentStock = stock, IsActive = active, Presentation = presentation };

    private static List<string> Codes(IEnumerable<Product> products) => products.Select(p => p.Code).ToList();

    [TestMethod]
    public void InStockGoesFirst_ThenMatchQuality_ThenName()
    {
        var catalog = new[]
        {
            Med("MED-001", "Amoxicilina 500 mg", "Amoxicilina", stock: 0),                       // nombre empieza, sin existencias
            Med("MED-002", "Clavulin", "Amoxicilina con ácido clavulánico", stock: 5),          // genérico empieza
            Med("MED-003", "Suspensión de amoxicilina", stock: 3),                               // una palabra empieza
            Med("MED-004", "Xamoxil", stock: 10),                                                // la contiene
            Med("MED-005", "Amoxil", "Amoxicilina", stock: 20, active: false),                   // inactivo: no cuenta como existencia
            Med("MED-006", "Paracetamol 500 mg", "Paracetamol", stock: 40)                       // no coincide
        };

        CollectionAssert.AreEqual(
            new[] { "MED-002", "MED-003", "MED-004", "MED-001", "MED-005" },
            Codes(MedicineSearchRules.Rank(catalog, "amox")));
    }

    [TestMethod]
    public void MatchTiers_FollowThePosRanking()
    {
        var product = Med("AMX-500", "Amoxicilina 500 mg", "Amoxicilina trihidratada", presentation: "Caja con 12 cápsulas");
        Assert.AreEqual(0, MedicineSearchRules.MatchTier(product, "amx-500"), "Código igual");
        Assert.AreEqual(1, MedicineSearchRules.MatchTier(product, "amoxicilina 5"), "El nombre empieza así");
        Assert.AreEqual(3, MedicineSearchRules.MatchTier(product, "500"), "Una palabra del nombre empieza así");
        Assert.AreEqual(3, MedicineSearchRules.MatchTier(product, "trihid"), "Una palabra del genérico empieza así");
        Assert.AreEqual(4, MedicineSearchRules.MatchTier(product, "xicilina"), "Lo contiene");
        Assert.AreEqual(4, MedicineSearchRules.MatchTier(product, "amx"), "El código lo contiene");
        Assert.AreEqual(MedicineSearchRules.NoMatch, MedicineSearchRules.MatchTier(product, "capsulas"), "La presentación solo cuenta si se pide");
        Assert.AreEqual(4, MedicineSearchRules.MatchTier(product, "capsulas", includePresentation: true));
        Assert.AreEqual(0, MedicineSearchRules.MatchTier(product, string.Empty), "Sin texto, todos empatan");

        var byGeneric = Med("X", "Tempra", "Paracetamol");
        Assert.AreEqual(2, MedicineSearchRules.MatchTier(byGeneric, "parac"), "El genérico empieza así");
    }

    [TestMethod]
    public void TheLimit_NoLongerCutsOffWhatIsInStock()
    {
        // 25 sin existencias antes en el catálogo y la única con existencias al final: antes quedaba fuera de las 20
        var catalog = Enumerable.Range(1, 25).Select(i => Med($"MED-{i:000}", $"Ibuprofeno genérico {i:00}", "Ibuprofeno"))
            .Append(Med("MED-999", "Ibuprofeno 400 mg", "Ibuprofeno", stock: 7))
            .ToList();

        var ranked = MedicineSearchRules.Rank(catalog, "ibup");
        Assert.HasCount(MedicineSearchRules.SuggestionLimit, ranked);
        Assert.AreEqual("MED-999", ranked[0].Code, "La que hay en el consultorio, primero");
    }

    [TestMethod]
    public void WithoutText_AllCandidates_InStockFirst_AndByNameWithoutAccents()
    {
        var catalog = new[]
        {
            Med("B", "Omeprazol 20 mg", stock: 0),
            Med("C", "Ácido fólico 5 mg", stock: 0),
            Med("D", "Naproxeno 250 mg", stock: 4),
            Med("E", "Bromhexina 8 mg", stock: 2)
        };

        CollectionAssert.AreEqual(new[] { "E", "D", "C", "B" }, Codes(MedicineSearchRules.Rank(catalog, "  ", limit: int.MaxValue)),
            "Con existencias por nombre, luego las demás; «Ácido» se ordena como «acido»");
    }

    [TestMethod]
    public void IgnoresAccentsAndCase()
    {
        var catalog = new[] { Med("A", "Ácido acetilsalicílico 100 mg", "Ácido acetilsalicílico", stock: 3) };
        Assert.HasCount(1, MedicineSearchRules.Rank(catalog, "ACIDO ACETIL"));
        Assert.HasCount(1, MedicineSearchRules.Rank(catalog, "salicilico"));
    }

    [TestMethod]
    public void Ties_AreStable_ByCodeAndThenByInputOrder()
    {
        var first = Med("Z-2", "Loratadina 10 mg", stock: 1);
        var second = Med("Z-1", "Loratadina 10 mg", stock: 1);
        var twinA = Med("Z-3", "Loratadina 10 mg", stock: 1);
        var twinB = Med("Z-3", "Loratadina 10 mg", stock: 1);

        var ranked = MedicineSearchRules.Rank(new[] { first, twinA, second, twinB }, "lorat");
        CollectionAssert.AreEqual(new[] { "Z-1", "Z-2", "Z-3", "Z-3" }, Codes(ranked), "Mismo nombre: por código");
        Assert.AreSame(twinA, ranked[2], "Mismo nombre y código: en el orden en que llegaron");
        Assert.AreSame(twinB, ranked[3]);
    }
}
