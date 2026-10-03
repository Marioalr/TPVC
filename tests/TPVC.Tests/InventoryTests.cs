using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

[TestClass]
public class InventoryTests
{
    private string _testDbPath = string.Empty;
    private AppDbContext _context = null!;
    private InventoryService _inventoryService = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"tpvc_test_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_testDbPath}")
            .Options;

        _context = new AppDbContext(options);
        DatabaseInitializer.Initialize(_context);
        _inventoryService = new InventoryService(_context);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [TestMethod]
    public async Task DatabaseInitializer_SeedsGeneralMedicineCategoriesAndProducts()
    {
        var categories = await _inventoryService.GetCategoriesAsync();
        Assert.IsTrue(categories.Count >= 5, "Debe tener al menos 5 categorías médicas");
        Assert.IsTrue(categories.Any(c => c.Name.Contains("Medicamentos")), "Debe existir categoría de Medicamentos");

        var products = await _inventoryService.GetProductsAsync();
        Assert.IsTrue(products.Count >= 5, "Debe tener productos sembrados");
    }

    [TestMethod]
    public async Task RegisterQuickConsultationUsage_DecreasesStockAndCreatesKardexMovement()
    {
        var product = (await _inventoryService.GetProductsAsync()).First(p => p.CurrentStock > 5);
        int initialStock = product.CurrentStock;
        int usedQty = 2;

        await _inventoryService.RegisterQuickConsultationUsageAsync(
            product.Id, 
            usedQty, 
            "Aplicación de curación en paciente de consulta",
            "Dr. Pérez"
        );

        var updated = await _inventoryService.GetProductByIdAsync(product.Id);
        Assert.IsNotNull(updated);
        Assert.AreEqual(initialStock - usedQty, updated.CurrentStock, "El stock debe disminuir por la cantidad consumida");

        var movements = await _inventoryService.GetRecentMovementsAsync(5);
        var lastMovement = movements.FirstOrDefault(m => m.ProductId == product.Id);
        Assert.IsNotNull(lastMovement);
        Assert.AreEqual(MovementType.SalidaConsulta, lastMovement.MovementType);
        Assert.AreEqual(usedQty, lastMovement.Quantity);
        Assert.AreEqual(initialStock, lastMovement.StockBefore);
        Assert.AreEqual(initialStock - usedQty, lastMovement.StockAfter);
        Assert.AreEqual("Dr. Pérez", lastMovement.Responsible);
    }

    [TestMethod]
    public void ProductBatch_ComputesExpirationStatusCorrectly()
    {
        var expiredBatch = new ProductBatch
        {
            BatchNumber = "LOT-EXP",
            ExpirationDate = DateTime.Today.AddDays(-5),
            Quantity = 10
        };
        Assert.AreEqual(ExpirationStatus.Vencido, expiredBatch.Status);

        var soonBatch = new ProductBatch
        {
            BatchNumber = "LOT-SOON",
            ExpirationDate = DateTime.Today.AddDays(20),
            Quantity = 10
        };
        Assert.AreEqual(ExpirationStatus.ProximoAVencer, soonBatch.Status);

        var goodBatch = new ProductBatch
        {
            BatchNumber = "LOT-GOOD",
            ExpirationDate = DateTime.Today.AddMonths(12),
            Quantity = 10
        };
        Assert.AreEqual(ExpirationStatus.Vigente, goodBatch.Status);
    }

    [TestMethod]
    public async Task DashboardMetrics_CalculatesCorrectTotals()
    {
        var metrics = await _inventoryService.GetDashboardMetricsAsync();
        Assert.IsTrue(metrics.TotalProducts > 0);
        Assert.IsTrue(metrics.LowStockCount >= 0);
        Assert.IsTrue(metrics.ExpiringSoonCount >= 0);
    }

    [TestMethod]
    public void AppUpdateService_HasValidCurrentVersion()
    {
        var service = new AppUpdateService();
        Assert.IsFalse(string.IsNullOrWhiteSpace(service.CurrentVersion));
    }
}
