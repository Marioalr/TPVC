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

    [TestMethod]
    public async Task PosViewModel_RealTimeSearchSuggestionsAndAddFirstWorkCorrectly()
    {
        var posVm = new PosViewModel(_inventoryService);
        await posVm.InitializeAsync();

        // 1. Al escribir texto debe abrir el dropdown con sugerencias coincidentes
        posVm.SearchText = "Para";
        Assert.IsTrue(posVm.IsSearchDropdownOpen);
        Assert.IsGreaterThanOrEqualTo(1, posVm.SearchSuggestions.Count);
        Assert.IsTrue(posVm.SearchSuggestions.Any(p => p.Name.Contains("Paracetamol", StringComparison.OrdinalIgnoreCase)));

        // 2. Al seleccionar una sugerencia se agrega al carrito y se limpia el buscador
        var chosen = posVm.SearchSuggestions[0];
        posVm.SelectSuggestion(chosen);

        Assert.AreEqual(1, posVm.TotalCartItems);
        Assert.AreEqual(chosen.Price, posVm.TotalCartAmount);
        Assert.IsTrue(string.IsNullOrEmpty(posVm.SearchText));
        Assert.IsFalse(posVm.IsSearchDropdownOpen);
    }

    [TestMethod]
    public async Task PosViewModel_CatalogModalOpensFiltersAndClosesCorrectly()
    {
        var posVm = new PosViewModel(_inventoryService);
        await posVm.InitializeAsync();

        // 1. Abrir modal
        posVm.OpenCatalogModal();
        Assert.IsTrue(posVm.IsCatalogModalOpen);
        Assert.IsGreaterThanOrEqualTo(1, posVm.CatalogFilteredProducts.Count);

        // 2. Filtrar dentro del modal
        posVm.CatalogSearchText = "Amoxi";
        Assert.IsTrue(posVm.CatalogFilteredProducts.All(p => p.Name.Contains("Amoxi", StringComparison.OrdinalIgnoreCase) || 
                                                            (p.GenericName != null && p.GenericName.Contains("Amoxi", StringComparison.OrdinalIgnoreCase))));

        // 3. Agregar desde el modal
        var product = posVm.CatalogFilteredProducts.First(p => p.CurrentStock > 0);
        posVm.AddToCart(product);
        Assert.AreEqual(1, posVm.TotalCartItems);

        // 4. Cerrar modal
        posVm.CloseCatalogModal();
        Assert.IsFalse(posVm.IsCatalogModalOpen);
    }

    [TestMethod]
    public async Task PosViewModel_ArrowKeyNavigation_NavigatesAndSelectsSuggestionsCorrectly()
    {
        var posVm = new PosViewModel(_inventoryService);
        await posVm.InitializeAsync();

        // 1. Buscar una letra común para obtener múltiples sugerencias (ej. 'o' o 'a')
        posVm.SearchText = "o";
        Assert.IsTrue(posVm.IsSearchDropdownOpen);
        Assert.IsGreaterThanOrEqualTo(2, posVm.SearchSuggestions.Count, "Debe haber al menos 2 sugerencias para probar navegación por flechas");

        // Al iniciar la búsqueda, la primera sugerencia está seleccionada por defecto (índice 0)
        Assert.AreEqual(0, posVm.SelectedSuggestionIndex);
        Assert.IsNotNull(posVm.SelectedSuggestion);
        Assert.AreEqual(posVm.SearchSuggestions[0].Id, posVm.SelectedSuggestion.Id);

        // 2. Presionar flecha hacia abajo (MoveSuggestionDown) -> se selecciona el segundo elemento (índice 1)
        posVm.MoveSuggestionDown();
        Assert.AreEqual(1, posVm.SelectedSuggestionIndex);
        Assert.AreEqual(posVm.SearchSuggestions[1].Id, posVm.SelectedSuggestion.Id);

        // 3. Presionar flecha hacia arriba (MoveSuggestionUp) -> regresa al primer elemento (índice 0)
        posVm.MoveSuggestionUp();
        Assert.AreEqual(0, posVm.SelectedSuggestionIndex);
        Assert.AreEqual(posVm.SearchSuggestions[0].Id, posVm.SelectedSuggestion.Id);

        // 4. Volver a bajar para seleccionar el segundo y confirmar con Enter (ConfirmSelectedSuggestion)
        posVm.MoveSuggestionDown();
        var selectedProduct = posVm.SelectedSuggestion!;
        posVm.ConfirmSelectedSuggestion();

        // Verificar que el producto seleccionado con las flechas fue el agregado al ticket de venta
        Assert.AreEqual(1, posVm.TotalCartItems);
        Assert.HasCount(1, posVm.Cart);
        Assert.AreEqual(selectedProduct.Id, posVm.Cart[0].ProductId);
        Assert.IsTrue(string.IsNullOrEmpty(posVm.SearchText));
        Assert.IsFalse(posVm.IsSearchDropdownOpen);
    }
}


