using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>Costo del producto (v1.21.0): valor del inventario al costo, lista de compras y ganancia en la ficha.</summary>
[TestClass]
[DoNotParallelize]
public class ProductCostTests
{
    private string _dbPath = string.Empty;
    private AppDbContext _context = null!;
    private InventoryService _inventory = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_costo_{Guid.NewGuid():N}.db");
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        DatabaseInitializer.Initialize(_context);
        _inventory = new InventoryService(_context);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _context?.Database.EnsureDeleted(); } catch { }
        _context?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<Product> SaveAsync(string code, int stock, decimal unitCost, bool excludesVat = false, decimal price = 100m, int minStock = 5)
    {
        var category = (await _inventory.GetCategoriesAsync()).First();
        await _inventory.SaveProductAsync(new Product
        {
            Code = code, Name = $"Producto {code}", CategoryId = category.Id, Price = price,
            UnitCost = unitCost, CostExcludesVat = excludesVat, CurrentStock = stock, MinStock = minStock, OptimalStock = 20
        });
        return (await _inventory.GetProductsAsync(searchText: code)).Single(p => p.Code == code);
    }

    [TestMethod]
    public void TheRealCost_AddsVatOnlyWhenTheCostDoesNotIncludeIt()
    {
        Assert.AreEqual(6.50m, new Product { UnitCost = 6.50m }.EffectiveUnitCost, "Medicamentos: sin IVA");
        Assert.AreEqual(7.54m, new Product { UnitCost = 6.50m, CostExcludesVat = true }.EffectiveUnitCost, "6.50 + 16 % = 7.54");
        Assert.AreEqual(22.62m, new Product { UnitCost = 6.50m, CostExcludesVat = true, CurrentStock = 3 }.InventoryValue);
        Assert.AreEqual(0m, new Product { UnitCost = 6.50m, CurrentStock = 0 }.InventoryValue);
        Assert.IsFalse(new Product().HasCost, "Sin costo capturado");
        Assert.IsFalse(new Product().CostExcludesVat, "La casilla empieza desmarcada");
    }

    [TestMethod]
    public async Task TheCostIsSaved_AndThePanelAddsUpTheInventoryAtCost()
    {
        var before = await _inventory.GetDashboardMetricsAsync();

        var gauze = await SaveAsync("CST-1", stock: 10, unitCost: 6.50m, excludesVat: true); // 75.40
        await SaveAsync("CST-2", stock: 4, unitCost: 20m);                                   // 80.00
        await SaveAsync("CST-3", stock: 5, unitCost: 0m);                                    // sin costo
        await SaveAsync("CST-4", stock: 0, unitCost: 99m);                                   // sin existencias

        Assert.AreEqual(6.50m, gauze.UnitCost);
        Assert.IsTrue(gauze.CostExcludesVat);

        var after = await _inventory.GetDashboardMetricsAsync();
        Assert.AreEqual(before.InventoryValue + 155.40m, after.InventoryValue);
        Assert.AreEqual(before.StockedProductsWithoutCost + 1, after.StockedProductsWithoutCost);
        StringAssert.Contains(after.InventoryValueDetail, "sin costo");

        // Editar el costo de un producto que ya existe
        gauze.UnitCost = 7m;
        gauze.CostExcludesVat = false;
        await _inventory.SaveProductAsync(gauze);
        var edited = (await _inventory.GetProductsAsync(searchText: "CST-1")).Single();
        Assert.AreEqual(7m, edited.UnitCost);
        Assert.IsFalse(edited.CostExcludesVat);
    }

    [TestMethod]
    public async Task ThePurchaseList_UsesTheRealCost_AndMarksTheEstimates()
    {
        await SaveAsync("CST-5", stock: 1, unitCost: 10m, excludesVat: true, price: 30m);
        await SaveAsync("CST-6", stock: 1, unitCost: 0m, price: 100m);

        var purchases = await _inventory.GetSuggestedPurchaseListAsync();
        var withCost = purchases.Single(p => p.ProductCode == "CST-5");
        Assert.AreEqual(11.60m, withCost.EstimatedUnitCost, "El costo con su IVA");
        Assert.IsFalse(withCost.IsCostEstimated);
        Assert.AreEqual("$11.60", withCost.UnitCostText);

        var estimated = purchases.Single(p => p.ProductCode == "CST-6");
        Assert.AreEqual(65m, estimated.EstimatedUnitCost, "Sin costo: 65 % del precio");
        Assert.IsTrue(estimated.IsCostEstimated);
        StringAssert.StartsWith(estimated.UnitCostText, "≈ ");

        var hub = new MainHubViewModel(_ => { }, _inventory);
        await hub.OpenSuggestedPurchasesAsync();
        StringAssert.Contains(hub.EstimatedCostNote, "sin costo capturado");
        Assert.AreEqual(hub.SuggestedPurchases.Sum(p => p.EstimatedTotalCost), hub.TotalSuggestedCost);
    }

    [TestMethod]
    public async Task TheProductForm_ShowsTheProfit_AndSavesTheCost()
    {
        var form = new ProductDialogViewModel(_inventory);
        await form.InitializeAsync();
        Assert.IsFalse(form.CostExcludesVat, "Desmarcada por omisión");
        Assert.AreEqual(string.Empty, form.ProfitText, "Sin costo no hay ganancia que mostrar");

        form.Code = "CST-7";
        form.Name = "Gasa estéril 10x10";
        form.Price = 12m;
        form.UnitCost = 6.50m;
        Assert.AreEqual("Ganancia: $5.50 por unidad (46 % del precio).", form.ProfitText);

        form.CostExcludesVat = true;
        Assert.AreEqual("Costo con IVA: $7.54 · Ganancia: $4.46 por unidad (37 % del precio).", form.ProfitText);

        form.Price = 7m;
        StringAssert.Contains(form.ProfitText, "abajo del costo");

        form.UnitCost = -1m;
        await form.SaveAsync();
        StringAssert.Contains(form.ErrorMessage, "negativo");

        form.UnitCost = 6.50m;
        form.Price = 12m;
        await form.SaveAsync();
        Assert.AreEqual(string.Empty, form.ErrorMessage);
        var saved = (await _inventory.GetProductsAsync(searchText: "CST-7")).Single();
        Assert.AreEqual(6.50m, saved.UnitCost);
        Assert.IsTrue(saved.CostExcludesVat);

        var edit = new ProductDialogViewModel(_inventory);
        await edit.InitializeAsync(saved);
        Assert.AreEqual(6.50m, edit.UnitCost);
        Assert.IsTrue(edit.CostExcludesVat);
    }

    [TestMethod]
    public async Task TheProductList_SaysWhatItsStockCostAtCost()
    {
        await SaveAsync("CST-8", stock: 2, unitCost: 50m);
        var list = new InventoryViewModel(_inventory) { SearchText = "CST-8" };
        await list.FilterProductsAsync();
        Assert.AreEqual("1 producto · $100 al costo en existencia", list.ResultsSummary);

        list.SearchText = "nada-que-coincida";
        await list.FilterProductsAsync();
        Assert.AreEqual("0 productos", list.ResultsSummary, "Sin valor que mostrar, solo el conteo");
    }

    [TestMethod]
    public async Task TheDemoProducts_HaveCost()
    {
        // Lo que se siembra desde el asistente sirve de ejemplo del valor del inventario
        var metrics = await _inventory.GetDashboardMetricsAsync();
        Assert.IsGreaterThan(0m, metrics.InventoryValue);
        Assert.AreEqual(0, metrics.StockedProductsWithoutCost);
    }
}
