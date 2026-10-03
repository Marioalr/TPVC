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
        Assert.IsGreaterThanOrEqualTo(5, categories.Count, "Debe tener al menos 5 categorías médicas");
        Assert.IsTrue(categories.Any(c => c.Name.Contains("Medicamentos")), "Debe existir categoría de Medicamentos");

        var products = await _inventoryService.GetProductsAsync();
        Assert.IsGreaterThanOrEqualTo(5, products.Count, "Debe tener productos sembrados");
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
        Assert.IsGreaterThan(0, metrics.TotalProducts);
        Assert.IsGreaterThanOrEqualTo(0, metrics.LowStockCount);
        Assert.IsGreaterThanOrEqualTo(0, metrics.ExpiringSoonCount);
    }

    [TestMethod]
    public void AppUpdateService_HasValidCurrentVersion()
    {
        var service = new AppUpdateService();
        Assert.IsFalse(string.IsNullOrWhiteSpace(service.CurrentVersion));
    }

    [TestMethod]
    public async Task RegisterSaleAsync_DecreasesStockAndCreatesSalidaVentaMovement()
    {
        var product = (await _inventoryService.GetProductsAsync()).First(p => p.CurrentStock > 5);
        int initialStock = product.CurrentStock;
        int qtyToSell = 3;

        await _inventoryService.RegisterSaleAsync(
            new[] { (product.Id, qtyToSell, product.Price) },
            "Paciente Prueba",
            "Efectivo"
        );

        var updated = await _inventoryService.GetProductByIdAsync(product.Id);
        Assert.IsNotNull(updated);
        Assert.AreEqual(initialStock - qtyToSell, updated.CurrentStock);

        var movements = await _inventoryService.GetRecentMovementsAsync(5);
        var saleMovement = movements.FirstOrDefault(m => m.ProductId == product.Id && m.MovementType == MovementType.SalidaVenta);
        Assert.IsNotNull(saleMovement);
        Assert.AreEqual(qtyToSell, saleMovement.Quantity);
        Assert.AreEqual(initialStock, saleMovement.StockBefore);
        Assert.AreEqual(initialStock - qtyToSell, saleMovement.StockAfter);
        StringAssert.Contains(saleMovement.Reason, "Paciente Prueba");
    }

    [TestMethod]
    public async Task PosViewModel_CartCalculationsAndLimitsWorkCorrectly()
    {
        var posVm = new PosViewModel(_inventoryService);
        await posVm.InitializeAsync();

        var product = posVm.Products.First(p => p.CurrentStock > 2);
        posVm.AddToCart(product);

        Assert.HasCount(1, posVm.Cart);
        Assert.AreEqual(1, posVm.TotalCartItems);
        Assert.AreEqual(product.Price, posVm.TotalCartAmount);

        var item = posVm.Cart[0];
        posVm.IncreaseQuantity(item);
        Assert.AreEqual(2, posVm.TotalCartItems);
        Assert.AreEqual(product.Price * 2, posVm.TotalCartAmount);

        posVm.DecreaseQuantity(item);
        Assert.AreEqual(1, posVm.TotalCartItems);

        posVm.ClearCart();
        Assert.IsEmpty(posVm.Cart);
        Assert.AreEqual(0, posVm.TotalCartItems);
        Assert.AreEqual(0m, posVm.TotalCartAmount);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_LoadsPatientsAndDispatchesPrescriptions()
    {
        var medVm = new MedicalRecordsViewModel();
        Assert.IsGreaterThanOrEqualTo(1, medVm.Patients.Count);
        Assert.IsNotNull(medVm.SelectedPatient);

        bool prescriptionSent = false;
        string medicineName = string.Empty;
        int qty = 0;

        medVm.OnSendPrescriptionToPos = (med, q) =>
        {
            prescriptionSent = true;
            medicineName = med;
            qty = q;
        };

        medVm.SendPrescriptionToPos();
        Assert.IsTrue(prescriptionSent);
        Assert.IsFalse(string.IsNullOrWhiteSpace(medicineName));
        Assert.IsGreaterThanOrEqualTo(1, qty);
    }
}
