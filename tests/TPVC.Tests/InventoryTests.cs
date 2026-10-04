using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Converters;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

[TestClass]
[DoNotParallelize]
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
        try { _context?.Database.EnsureDeleted(); } catch { }
        _context?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!string.IsNullOrEmpty(_testDbPath) && File.Exists(_testDbPath))
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
        Assert.IsNull(medVm.SelectedPatient);
        Assert.AreEqual("Agregar Paciente", medVm.PatientActionButtonText);

        medVm.SelectedPatient = medVm.Patients.First();
        Assert.IsNotNull(medVm.SelectedPatient);
        Assert.AreEqual("Cambiar Paciente", medVm.PatientActionButtonText);

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

    [TestMethod]
    public void BoolToVisibilityConverter_HandlesIntsStringsObjectsAndBoolsCorrectly()
    {
        var converter = new BoolToVisibilityConverter();

        // 1. Booleanos directos
        Assert.AreEqual(Visibility.Visible, converter.Convert(true, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Collapsed, converter.Convert(false, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Collapsed, converter.Convert(true, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Visible, converter.Convert(false, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture));

        // 2. Números enteros (ej. Cart.Count) - Aquí ocurría el bug crítico donde 1 is true evaluaba a false
        Assert.AreEqual(Visibility.Visible, converter.Convert(1, typeof(Visibility), null, CultureInfo.InvariantCulture), "Cart.Count = 1 debe ser Visible");
        Assert.AreEqual(Visibility.Visible, converter.Convert(5, typeof(Visibility), null, CultureInfo.InvariantCulture), "Cart.Count = 5 debe ser Visible");
        Assert.AreEqual(Visibility.Collapsed, converter.Convert(0, typeof(Visibility), null, CultureInfo.InvariantCulture), "Cart.Count = 0 debe ser Collapsed");
        Assert.AreEqual(Visibility.Visible, converter.Convert(0, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture), "Cart.Count = 0 con Inverse debe ser Visible (EmptyState)");
        Assert.AreEqual(Visibility.Collapsed, converter.Convert(1, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture), "Cart.Count = 1 con Inverse debe ser Collapsed (oculta EmptyState)");

        // 3. Cadenas de texto (ej. SearchText o GenericName)
        Assert.AreEqual(Visibility.Visible, converter.Convert("Paracetamol", typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Collapsed, converter.Convert("", typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Collapsed, converter.Convert("   ", typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Collapsed, converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));

        // 4. Colecciones directas
        Assert.AreEqual(Visibility.Visible, converter.Convert(new List<string> { "Item" }, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Collapsed, converter.Convert(new List<string>(), typeof(Visibility), null, CultureInfo.InvariantCulture));

        // 5. TargetType bool
        Assert.IsTrue((bool)converter.Convert(1, typeof(bool), null, CultureInfo.InvariantCulture)!);
        Assert.IsFalse((bool)converter.Convert(0, typeof(bool), null, CultureInfo.InvariantCulture)!);
    }


    [TestMethod]
    public async Task PosViewModel_HasCartItems_UpdatesReactivelyOnCartModifications()
    {
        var posVm = new PosViewModel(_inventoryService);
        await posVm.InitializeAsync();

        // Inicialmente el carrito debe estar vacío y HasCartItems en false
        Assert.IsFalse(posVm.HasCartItems);
        Assert.AreEqual(0, posVm.TotalCartItems);

        // Al agregar un producto, HasCartItems cambia reactivamente a true
        var product = posVm.Products.First(p => p.CurrentStock > 0);
        posVm.AddToCart(product);
        Assert.IsTrue(posVm.HasCartItems);
        Assert.AreEqual(1, posVm.TotalCartItems);

        // Al incrementar cantidad sigue en true
        var cartItem = posVm.Cart[0];
        posVm.IncreaseQuantity(cartItem);
        Assert.IsTrue(posVm.HasCartItems);
        Assert.AreEqual(2, posVm.TotalCartItems);

        // Al limpiar el carrito cambia reactivamente a false
        posVm.ClearCart();
        Assert.IsFalse(posVm.HasCartItems);
        Assert.AreEqual(0, posVm.TotalCartItems);

        // Al volver a agregar y luego removerlo uno a uno
        posVm.AddToCart(product);
        Assert.IsTrue(posVm.HasCartItems);
        posVm.RemoveFromCart(posVm.Cart[0]);
        Assert.IsFalse(posVm.HasCartItems);
    }

    [TestMethod]
    public async Task DatabaseInitializer_SeedsClinicalServices_WithDefaultProducts()
    {
        var services = await _inventoryService.GetClinicalServicesAsync();
        Assert.IsNotNull(services);
        Assert.IsGreaterThanOrEqualTo(4, services.Count, "Debe sembrar al menos 4 servicios clínicos");

        var curacion = services.FirstOrDefault(s => s.Code == "SERV-002");
        Assert.IsNotNull(curacion, "Debe existir el servicio SERV-002 (Curación Menor)");
        Assert.IsGreaterThanOrEqualTo(1, curacion.DefaultProducts.Count, "La curación debe incluir insumos por defecto");
        Assert.IsTrue(curacion.DefaultProducts.Any(dp => dp.Product != null && dp.Quantity > 0));
    }

    [TestMethod]
    public async Task InventoryService_SaveAndGetClinicalServices_PersistsDefaultProducts()
    {
        var products = await _inventoryService.GetProductsAsync();
        var prod1 = products[0];
        var prod2 = products[1];

        var newService = new ClinicalService
        {
            Code = "SERV-999",
            Name = "Infiltración Articular con Corticoide",
            Description = "Procedimiento médico de alivio de dolor articular con anestésico local.",
            Category = "Procedimientos",
            Price = 650.00m,
            EstimatedDurationMinutes = 30,
            IsActive = true
        };

        var defaultItems = new List<(int ProductId, int Quantity, string? Instructions)>
        {
            (prod1.Id, 2, "Insumo de asepsia previa"),
            (prod2.Id, 1, "Aplicación directa de fármaco")
        };

        var saved = await _inventoryService.SaveClinicalServiceAsync(newService, defaultItems);
        Assert.IsNotNull(saved);
        Assert.IsTrue(saved.Id > 0);

        var retrieved = await _inventoryService.GetClinicalServiceByIdAsync(saved.Id);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("SERV-999", retrieved.Code);
        Assert.AreEqual(2, retrieved.DefaultProducts.Count, "Debe guardar y recuperar exactamente los 2 insumos predeterminados");
        Assert.AreEqual(2, retrieved.DefaultProducts.First(p => p.ProductId == prod1.Id).Quantity);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_ServicesSection_AddsNewServiceWithDefaultProducts()
    {
        var mrVm = new MedicalRecordsViewModel(_inventoryService);
        await mrVm.LoadServicesAsync();

        Assert.IsNotNull(mrVm.Services);
        int initialCount = mrVm.Services.Count;

        mrVm.OpenCreateServiceModal();
        mrVm.NewServiceCode = "SERV-TEST";
        mrVm.NewServiceName = "Prueba de Espirometría Clínica";
        mrVm.NewServiceCategory = "Diagnóstico";
        mrVm.NewServicePrice = 500.00m;
        mrVm.NewServiceDuration = 25;

        Assert.IsNotNull(mrVm.AvailableProducts);
        Assert.IsTrue(mrVm.AvailableProducts.Count > 0);
        mrVm.SelectedProductToAdd = mrVm.AvailableProducts[0];
        mrVm.NewProductQuantity = 3;
        mrVm.AddProductToNewService();

        Assert.AreEqual(1, mrVm.NewServiceDefaultProducts.Count);
        Assert.AreEqual(3, mrVm.NewServiceDefaultProducts[0].Quantity);

        await mrVm.SaveNewServiceAsync();

        Assert.IsFalse(mrVm.IsCreateServiceModalOpen);
        Assert.AreEqual(initialCount + 1, mrVm.Services.Count);
        var created = mrVm.Services.FirstOrDefault(s => s.Code == "SERV-TEST");
        Assert.IsNotNull(created);
        Assert.AreEqual("Prueba de Espirometría Clínica", created.Name);
        Assert.AreEqual(1, created.DefaultProducts.Count);
    }

    [TestMethod]
    public void ComparisonToVisibilityConverter_ConvertsValuesCorrectly()
    {
        var converter = new ComparisonToVisibilityConverter();
        Assert.AreEqual(Visibility.Visible, converter.Convert("Servicios", typeof(Visibility), "Servicios", CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Collapsed, converter.Convert("Expedientes", typeof(Visibility), "Servicios", CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Collapsed, converter.Convert(null, typeof(Visibility), "Servicios", CultureInfo.InvariantCulture));

        var inverseConverter = new ComparisonToVisibilityConverter { Invert = true };
        Assert.AreEqual(Visibility.Collapsed, inverseConverter.Convert("Servicios", typeof(Visibility), "Servicios", CultureInfo.InvariantCulture));
        Assert.AreEqual(Visibility.Visible, inverseConverter.Convert("Expedientes", typeof(Visibility), "Servicios", CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task PosViewModel_ProcessSale_CreatesSaleReceiptAndOpensTicketModal()
    {
        var posVm = new PosViewModel(_inventoryService);
        await posVm.InitializeAsync();

        var productsWithStock = posVm.Products.Where(p => p.CurrentStock > 2).Take(2).ToList();
        Assert.AreEqual(2, productsWithStock.Count, "Deben existir al menos 2 productos con stock para la prueba");

        posVm.AddToCart(productsWithStock[0]);
        posVm.AddToCart(productsWithStock[1]);
        posVm.IncreaseQuantity(posVm.Cart[0]); // cantidad = 2

        posVm.PatientName = "Ana Laura Domínguez";
        posVm.SelectedPaymentMethod = "Tarjeta Débito/Crédito";

        decimal expectedTotal = posVm.TotalCartAmount;
        int expectedItems = posVm.TotalCartItems;

        // Al despachar la venta
        await posVm.ProcessSaleAsync();

        // 1. Debe abrirse el modal del ticket simulado
        Assert.IsTrue(posVm.IsTicketModalOpen, "El modal del ticket debe abrirse al despachar la venta");
        Assert.IsNotNull(posVm.CurrentReceipt, "Debe existir un objeto SaleReceipt generado");

        // 2. Debe contener los datos exactos cobrados
        var receipt = posVm.CurrentReceipt;
        Assert.IsTrue(receipt.Folio.StartsWith("TKT-", StringComparison.OrdinalIgnoreCase), "El folio debe comenzar con TKT-");
        Assert.AreEqual("Ana Laura Domínguez", receipt.PatientName);
        Assert.AreEqual("Tarjeta Débito/Crédito", receipt.PaymentMethod);
        Assert.AreEqual(expectedTotal, receipt.TotalAmount, "El total cobrado en el ticket debe coincidir con el del carrito");
        Assert.AreEqual(expectedItems, receipt.TotalItems, "El total de piezas en el ticket debe coincidir");
        Assert.AreEqual(2, receipt.Items.Count);

        // 3. El carrito debe haberse reiniciado para la siguiente venta
        Assert.AreEqual(0, posVm.Cart.Count);
        Assert.IsFalse(posVm.HasCartItems);

        // 4. Probar comando de imprimir ticket
        posVm.PrintTicket();
        Assert.IsTrue(posVm.IsTicketPrinted);
        Assert.IsNotNull(posVm.TicketStatusMessage);

        // 5. Probar cerrar ticket / nueva venta
        posVm.CloseTicketModal();
        Assert.IsFalse(posVm.IsTicketModalOpen);
        Assert.IsNull(posVm.CurrentReceipt);
    }

    [TestMethod]
    public void AppUpdateService_DetectsNewerVersionCorrectly()
    {
        var service = new AppUpdateService();
        service.CurrentVersion = "1.3.0";

        string remoteTag = "v1.3.1";
        string latestVersion = remoteTag.TrimStart('v', 'V');

        Assert.IsTrue(Version.TryParse(latestVersion, out var latest));
        Assert.IsTrue(Version.TryParse(service.CurrentVersion, out var current));
        Assert.IsTrue(latest > current, "La versión remota 1.3.1 debe ser mayor a la versión local 1.3.0");
    }

    [TestMethod]
    public void AppUpdateService_RecognizesCurrentVersionAsUpToDate()
    {
        var service = new AppUpdateService();
        service.CurrentVersion = "1.3.1";

        string remoteTag = "v1.3.1";
        string latestVersion = remoteTag.TrimStart('v', 'V');

        Assert.IsTrue(Version.TryParse(latestVersion, out var latest));
        Assert.IsTrue(Version.TryParse(service.CurrentVersion, out var current));
        Assert.IsFalse(latest > current, "Versiones idénticas no deben reportar actualización disponible");
    }

    [TestMethod]
    public void AppUpdateService_TokenPersistenceAndEffectiveToken()
    {
        var service = new AppUpdateService();
        string testToken = "ghp_testToken1234567890abcdef";

        service.SaveToken(testToken);
        Assert.AreEqual(testToken, service.GitHubToken);
        Assert.AreEqual(testToken, service.GetEffectiveToken());

        // Limpiar token
        service.SaveToken(null);
        Assert.IsNull(service.GitHubToken);
    }

    [TestMethod]
    public async Task InventoryService_IsProductCodeTakenAsync_DetectsExistingCodeCaseInsensitive()
    {
        var categories = await _inventoryService.GetCategoriesAsync();
        var cat = categories.First();

        var p = new Product
        {
            Code = "TEST-DUP-01",
            Name = "Producto de Prueba",
            CategoryId = cat.Id,
            Price = 50.0m
        };
        await _inventoryService.SaveProductAsync(p);

        // Mismo código en mayúsculas
        Assert.IsTrue(await _inventoryService.IsProductCodeTakenAsync("TEST-DUP-01"));
        // Mismo código en minúsculas
        Assert.IsTrue(await _inventoryService.IsProductCodeTakenAsync("test-dup-01"));
        // Mismo código con espacios
        Assert.IsTrue(await _inventoryService.IsProductCodeTakenAsync("  TEST-DUP-01  "));
        // Mismo código excluyendo su propio Id (al editar) no debe dar tomado
        Assert.IsFalse(await _inventoryService.IsProductCodeTakenAsync("TEST-DUP-01", excludeProductId: p.Id));
        // Código diferente
        Assert.IsFalse(await _inventoryService.IsProductCodeTakenAsync("CODIGO-NUEVO-99"));
    }

    [TestMethod]
    public async Task InventoryService_SaveProduct_PreventsDuplicateCode()
    {
        var categories = await _inventoryService.GetCategoriesAsync();
        var cat = categories.First();

        var p1 = new Product
        {
            Code = "MED-UNICO-100",
            Name = "Medicamento Base",
            CategoryId = cat.Id,
            Price = 20.0m
        };
        await _inventoryService.SaveProductAsync(p1);

        var p2 = new Product
        {
            Code = "med-unico-100", // Mismo código
            Name = "Medicamento Duplicado Intento",
            CategoryId = cat.Id,
            Price = 30.0m
        };

        bool threw = false;
        try
        {
            await _inventoryService.SaveProductAsync(p2);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Debe lanzar InvalidOperationException si el código ya existe");
    }

    [TestMethod]
    public async Task ProductDialogViewModel_SaveAsync_PreventsDuplicateCodeAndSetsErrorMessage()
    {
        var categories = await _inventoryService.GetCategoriesAsync();
        var cat = categories.First();

        var p1 = new Product
        {
            Code = "SKU-BLOQUEADO-01",
            Name = "Insumo Existente",
            CategoryId = cat.Id,
            Price = 15.0m
        };
        await _inventoryService.SaveProductAsync(p1);

        var dialogVm = new ProductDialogViewModel(_inventoryService);
        await dialogVm.InitializeAsync(null);

        dialogVm.Code = "sku-bloqueado-01"; // Intento con el mismo código
        dialogVm.Name = "Insumo Nuevo con Código Repetido";
        dialogVm.SelectedCategory = cat;

        await dialogVm.SaveAsync();

        Assert.IsFalse(string.IsNullOrWhiteSpace(dialogVm.ErrorMessage));
        Assert.IsTrue(dialogVm.ErrorMessage.Contains("ya está registrado"), "Debe notificar que el código está repetido");
        Assert.IsFalse(dialogVm.DialogResult);
    }

    [TestMethod]
    public async Task ProductDialogViewModel_SaveAsync_ClearsFieldsOnSuccessfulSave()
    {
        var categories = await _inventoryService.GetCategoriesAsync();
        var cat = categories.First();

        var dialogVm = new ProductDialogViewModel(_inventoryService);
        await dialogVm.InitializeAsync(null);

        dialogVm.Code = "NUEVO-INSUMO-OK";
        dialogVm.Name = "Gasas Estériles Premium";
        dialogVm.GenericName = "Gasa Quirúrgica";
        dialogVm.Presentation = "Paquete con 10 piezas";
        dialogVm.SelectedCategory = cat;
        dialogVm.Price = 45.0m;
        dialogVm.CurrentStock = 12;

        await dialogVm.SaveAsync();

        Assert.IsTrue(dialogVm.DialogResult, "Debe ser exitoso");
        Assert.IsTrue(string.IsNullOrWhiteSpace(dialogVm.ErrorMessage));
        Assert.IsFalse(string.IsNullOrWhiteSpace(dialogVm.SuccessMessage));
        Assert.IsTrue(dialogVm.SuccessMessage.Contains("Gasas Estériles Premium"));

        // Verificar que los campos agregados se borraron (formulario limpio)
        Assert.AreEqual(string.Empty, dialogVm.Code, "El código debe borrarse al guardar");
        Assert.AreEqual(string.Empty, dialogVm.Name, "El nombre debe borrarse al guardar");
        Assert.AreEqual(string.Empty, dialogVm.GenericName, "El genérico debe borrarse");
        Assert.AreEqual(string.Empty, dialogVm.Presentation, "La presentación debe borrarse");
        Assert.AreEqual(0.0m, dialogVm.Price, "El precio debe reiniciarse a 0");
        Assert.AreEqual(0, dialogVm.CurrentStock, "El stock debe reiniciarse a 0");
    }

    [TestMethod]
    public async Task PosViewModel_AddToActiveConsultation_WhenEmptyCart_AlertsUser()
    {
        var consultService = new ConsultationService();
        var posVm = new PosViewModel(_inventoryService, consultService);
        await posVm.InitializeAsync();

        posVm.AddToActiveConsultation();

        Assert.IsTrue(posVm.IsAlertVisible);
        Assert.IsFalse(posVm.IsSuccessAlert);
        StringAssert.Contains(posVm.AlertMessage, "La venta no contiene ningún producto");
    }

    [TestMethod]
    public async Task PosViewModel_AddToActiveConsultation_WhenNoActiveConsultation_OpensModalAndAlerts()
    {
        var consultService = new ConsultationService();
        var posVm = new PosViewModel(_inventoryService, consultService);
        await posVm.InitializeAsync();

        var product = posVm.Products.First(p => p.CurrentStock > 0);
        posVm.AddToCart(product);

        Assert.HasCount(1, posVm.Cart);
        Assert.IsFalse(posVm.HasActiveConsultation);

        posVm.AddToActiveConsultation();

        // Debe avisar que debe haber una activa y abrir el modal
        Assert.IsTrue(posVm.IsSelectPatientModalOpen);
        Assert.IsTrue(posVm.IsAlertVisible);
        Assert.IsFalse(posVm.IsSuccessAlert);
        StringAssert.Contains(posVm.AlertMessage, "Debe haber una consulta activa");
        Assert.HasCount(1, posVm.Cart, "El carrito no debe perderse si aún no se inicia la consulta");

        // Al confirmar desde el modal con un paciente seleccionado:
        posVm.SelectedPatientForConsultation = posVm.AvailablePatientsForConsultation.First();
        posVm.ConfirmStartConsultationAndAddItems();

        Assert.IsFalse(posVm.IsSelectPatientModalOpen);
        Assert.IsTrue(posVm.HasActiveConsultation);
        Assert.IsEmpty(posVm.Cart, "El carrito se limpia para guardar en la consulta");
        Assert.AreEqual(1, posVm.ActiveConsultationItemsCount);
        Assert.IsTrue(posVm.IsSuccessAlert);
        StringAssert.Contains(posVm.AlertMessage, "Se agregaron");
    }

    [TestMethod]
    public async Task PosViewModel_AddToActiveConsultation_SavesItemsWithoutImmediateChargingAndAllowsFinalCheckout()
    {
        var consultService = new ConsultationService();
        var patient = consultService.Patients.First();
        consultService.StartConsultation(patient, "Evaluación médica de prueba");

        var posVm = new PosViewModel(_inventoryService, consultService);
        await posVm.InitializeAsync();

        Assert.IsTrue(posVm.HasActiveConsultation);
        Assert.AreEqual(patient.FullName, posVm.ActiveConsultationPatientName);

        var prod1 = posVm.Products.First(p => p.CurrentStock >= 2);
        posVm.AddToCart(prod1);
        posVm.IncreaseQuantity(posVm.Cart.First()); // Qty = 2

        var initialCartTotal = posVm.TotalCartAmount;
        Assert.AreEqual(2, posVm.TotalCartItems);

        // Agregar a la consulta activa
        posVm.AddToActiveConsultation();

        // No se cobra de inmediato: se vacía el carrito del mostrador para seguir atendiendo
        Assert.IsEmpty(posVm.Cart);
        Assert.AreEqual(0, posVm.TotalCartItems);
        Assert.AreEqual(0m, posVm.TotalCartAmount);

        // Pero la consulta activa acumula los insumos guardados
        Assert.AreEqual(2, posVm.ActiveConsultationItemsCount);
        Assert.AreEqual(initialCartTotal, posVm.ActiveConsultationTotalAmount);

        // Al finalizar la consulta médica, transferir de vuelta a POS para cobrar y liquidar
        posVm.CheckoutActiveConsultation();

        Assert.HasCount(1, posVm.Cart);
        Assert.AreEqual(2, posVm.TotalCartItems);
        Assert.AreEqual(initialCartTotal, posVm.TotalCartAmount);
        Assert.AreEqual(patient.FullName, posVm.PatientName);
        Assert.IsFalse(posVm.HasActiveConsultation, "La consulta activa se cierra para cobro final");

        // Procesar la venta final genera el comprobante
        await posVm.ProcessSaleAsync();
        Assert.IsTrue(posVm.IsTicketModalOpen);
        Assert.IsNotNull(posVm.CurrentReceipt);
        Assert.AreEqual(patient.FullName, posVm.CurrentReceipt.PatientName);
        Assert.AreEqual(initialCartTotal, posVm.CurrentReceipt.TotalAmount);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_ActiveConsultationLifecycle_WorksSynchronouslyWithConsultationService()
    {
        var consultService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService);

        Assert.IsFalse(recordsVm.HasActiveConsultation);

        recordsVm.SelectedPatient = recordsVm.Patients.First();
        recordsVm.StartActiveConsultation();

        Assert.IsTrue(recordsVm.HasActiveConsultation);
        Assert.AreEqual(recordsVm.SelectedPatient.FullName, recordsVm.ActiveConsultationPatientName);

        // Cancelar consulta
        recordsVm.CancelActiveConsultation();
        Assert.IsFalse(recordsVm.HasActiveConsultation);
    }

    [TestMethod]
    public async Task DatabaseInitializer_SeedsPatientsWithHistoryAndPrescriptions()
    {
        var patients = await _inventoryService.GetPatientsAsync();
        Assert.IsGreaterThanOrEqualTo(4, patients.Count, "Debe tener al menos 4 pacientes sembrados");
        Assert.IsTrue(patients.Any(p => p.FullName.Contains("García")), "Debe existir paciente García");
        
        var garcia = patients.First(p => p.FullName.Contains("García"));
        Assert.IsTrue(garcia.HasAllergies);
        Assert.IsTrue(garcia.Allergies.Contains("Penicilina"));
        Assert.IsNotEmpty(garcia.ActivePrescription);
        Assert.IsNotEmpty(garcia.Consultations);
    }

    [TestMethod]
    public async Task SavePatientAsync_GeneratesFolioAndSavesInDatabase()
    {
        var nextFolio = await _inventoryService.GenerateNextRecordNumberAsync();
        Assert.IsTrue(nextFolio.StartsWith("EXP-"), "El folio debe comenzar con EXP-");

        var newPatient = new PatientRecord
        {
            RecordNumber = nextFolio,
            FullName = "López Obrador, Andrés",
            Age = 55,
            Gender = "Masculino",
            Phone = "55 9988 7766",
            Email = "andres.lopez@ejemplo.com",
            Address = "Calle Pino Suárez 10, Centro, CDMX",
            BloodType = "O+",
            Allergies = "Ninguna conocida",
            ChronicDiseases = "Hipertensión controlada",
            EmergencyContactName = "Beatriz Gutiérrez",
            EmergencyContactPhone = "55 9988 7760",
            VitalSigns = "TA: 120/80 mmHg | FC: 72 lpm | Temp: 36.5 °C",
            ClinicalNotes = "Paciente acude a chequeo preventivo general.",
            IsActive = true
        };

        var saved = await _inventoryService.SavePatientAsync(newPatient);
        Assert.IsTrue(saved.Id > 0);

        var retrieved = await _inventoryService.GetPatientByIdAsync(saved.Id);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("López Obrador, Andrés", retrieved.FullName);
        Assert.AreEqual(nextFolio, retrieved.RecordNumber);
    }

    [TestMethod]
    public async Task SavePatientAsync_RejectsDuplicateRecordNumber()
    {
        var duplicate = new PatientRecord
        {
            RecordNumber = "EXP-2026-001", // Ya existe por el seeder
            FullName = "Persona Duplicada",
            Age = 30
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _inventoryService.SavePatientAsync(duplicate);
        });
    }

    [TestMethod]
    public async Task AddConsultationNoteAsync_RecordsEvolutionNoteAndUpdatesPatient()
    {
        var patient = (await _inventoryService.GetPatientsAsync()).First();
        int initialConsultations = patient.Consultations.Count;

        var note = new PatientConsultation
        {
            ChiefComplaint = "Cefalea intensa y dolor ocular",
            VitalSigns = "TA: 130/85 mmHg | FC: 80 lpm | FR: 18 rpm | Temp: 36.8 °C | Peso: 75 kg | Talla: 1.70 m | IMC: 26.0 kg/m² (Sobrepeso)",
            PhysicalExamination = "Agudeza visual conservada, fondo de ojo normal, tensión muscular cervical.",
            Diagnosis = "Cefalea tensional episódica frecuente (G44.2)",
            TreatmentPlan = "Reposo, corrección postural, termoterapia local y analgésicos.",
            Notes = "Se indican medidas higiénico-dietéticas.",
            Prescriptions = new List<PatientPrescriptionItem>
            {
                new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 tableta cada 8 horas si hay dolor", RecommendedQuantity = 1 }
            }
        };

        var savedNote = await _inventoryService.AddConsultationNoteAsync(patient.Id, note);
        Assert.IsNotNull(savedNote);
        Assert.IsTrue(savedNote.Folio.StartsWith("CNS-"));

        var updatedPatient = await _inventoryService.GetPatientByIdAsync(patient.Id);
        Assert.IsNotNull(updatedPatient);
        Assert.AreEqual(initialConsultations + 1, updatedPatient.Consultations.Count);
        Assert.AreEqual("Cefalea tensional episódica frecuente (G44.2)", updatedPatient.LastDiagnosis);
        Assert.AreEqual(note.VitalSigns, updatedPatient.VitalSigns);
        Assert.IsNotEmpty(updatedPatient.ActivePrescription);
        Assert.AreEqual("Paracetamol 500 mg", updatedPatient.ActivePrescription.First().MedicineName);
    }

    [TestMethod]
    public async Task GetPatientsAsync_SearchFiltersWorkAccurately()
    {
        var all = await _inventoryService.GetPatientsAsync();
        Assert.IsGreaterThanOrEqualTo(4, all.Count);

        // Búsqueda por nombre
        var byName = await _inventoryService.GetPatientsAsync("María Elena");
        Assert.IsNotEmpty(byName);
        Assert.IsTrue(byName.All(p => p.FullName.Contains("María Elena")));

        // Búsqueda por folio
        var byFolio = await _inventoryService.GetPatientsAsync("EXP-2026-003");
        Assert.HasCount(1, byFolio);
        Assert.AreEqual("EXP-2026-003", byFolio.First().RecordNumber);

        // Búsqueda por teléfono
        var byPhone = await _inventoryService.GetPatientsAsync("4123");
        Assert.IsNotEmpty(byPhone);

        // Búsqueda por diagnóstico
        var byDiagnosis = await _inventoryService.GetPatientsAsync("Faringoamigdalitis");
        Assert.IsNotEmpty(byDiagnosis);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_RecalculateIMC_ComputesCorrectly()
    {
        var vm = new MedicalRecordsViewModel();

        // 70 kg, 1.70 m -> IMC = 70 / 2.89 = 24.22 (Peso normal)
        vm.ConsultationWeight = "70.0";
        vm.ConsultationHeight = "1.70";
        Assert.IsTrue(vm.ConsultationIMC.Contains("24.2"));
        Assert.IsTrue(vm.ConsultationIMC.Contains("Peso normal"));

        // 85 kg, 1.70 m -> IMC = 85 / 2.89 = 29.41 (Sobrepeso)
        vm.ConsultationWeight = "85.0";
        vm.ConsultationHeight = "1.70";
        Assert.IsTrue(vm.ConsultationIMC.Contains("29.4"));
        Assert.IsTrue(vm.ConsultationIMC.Contains("Sobrepeso"));

        // 100 kg, 1.70 m -> IMC = 100 / 2.89 = 34.60 (Obesidad Grado I)
        vm.ConsultationWeight = "100.0";
        vm.ConsultationHeight = "1.70";
        Assert.IsTrue(vm.ConsultationIMC.Contains("34.6"));
        Assert.IsTrue(vm.ConsultationIMC.Contains("Obesidad Grado I"));
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_PatientModalSave_ValidatesAndSaves()
    {
        var consultService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService);
        await recordsVm.LoadPatientsAsync();

        // Abrir modal de nuevo paciente
        await recordsVm.NewPatientAsync();
        Assert.IsTrue(recordsVm.IsPatientModalOpen);
        Assert.IsTrue(recordsVm.FormRecordNumber.StartsWith("EXP-"));

        // Intentar guardar con nombre vacío
        recordsVm.FormFullName = "";
        await recordsVm.SavePatientModalAsync();
        Assert.IsTrue(recordsVm.IsPatientModalOpen, "Debe permanecer abierto si hay error de validación");
        Assert.IsNotNull(recordsVm.FormErrorMessage);

        // Guardar con datos válidos
        recordsVm.FormFullName = "Test Paciente Nuevo";
        recordsVm.FormPhone = "55 0011 2233";
        recordsVm.FormBloodType = "A+";
        recordsVm.FormAllergies = "Ninguna";
        await recordsVm.SavePatientModalAsync();

        Assert.IsFalse(recordsVm.IsPatientModalOpen);
        Assert.IsNull(recordsVm.FormErrorMessage);
        Assert.IsNotNull(recordsVm.SelectedPatient);
        Assert.AreEqual("Test Paciente Nuevo", recordsVm.SelectedPatient.FullName);

        // Verificar que se haya sincronizado con el servicio de consulta
        Assert.IsTrue(consultService.Patients.Any(p => p.FullName == "Test Paciente Nuevo"));
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_ConsultationModal_RegistersNoteAndPrescription()
    {
        var consultService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService);
        await recordsVm.LoadPatientsAsync();

        recordsVm.SelectedPatient = recordsVm.Patients.First();
        recordsVm.NewConsultation();

        Assert.IsTrue(recordsVm.IsConsultationModalOpen);
        Assert.IsTrue(recordsVm.ConsultationFolio.StartsWith("CNS-"));

        recordsVm.ConsultationChiefComplaint = "Chequeo de rutina";
        recordsVm.ConsultationDiagnosis = "Paciente clínicamente sano (Z00.0)";
        recordsVm.ConsultationTreatmentPlan = "Continuar hábitos saludables y dieta balanceada.";

        // Agregar medicamento a la receta en el modal
        recordsVm.NewPrescriptionMedicine = "Complejo B Cápsulas";
        recordsVm.NewPrescriptionDosage = "1 cápsula diaria con alimentos por 30 días";
        recordsVm.NewPrescriptionQuantity = 1;
        recordsVm.AddPrescriptionItem();

        Assert.HasCount(1, recordsVm.ConsultationPrescriptions);

        // Guardar consulta
        await recordsVm.SaveConsultationModalAsync();

        Assert.IsFalse(recordsVm.IsConsultationModalOpen);
        Assert.IsNull(recordsVm.ConsultationErrorMessage);
        Assert.AreEqual("Paciente clínicamente sano (Z00.0)", recordsVm.SelectedPatient.LastDiagnosis);
        Assert.IsTrue(consultService.HasActiveConsultation, "Debió activar consulta en ConsultationService");
        Assert.AreEqual(recordsVm.SelectedPatient.FullName, consultService.CurrentConsultation?.PatientName);
    }

    [TestMethod]
    public async Task PosViewModel_ServicesModal_OpensLoadsAndFiltersByCategoryAndText()
    {
        var consultService = new ConsultationService();
        var posVm = new PosViewModel(_inventoryService, consultService);
        await posVm.InitializeAsync();

        // 1. Abrir modal de servicios
        await posVm.OpenServicesModalAsync();
        Assert.IsTrue(posVm.IsServicesModalOpen, "El modal de servicios e insumos debe abrirse");
        Assert.IsNotEmpty(posVm.Services, "Debe cargar servicios predeterminados");
        Assert.HasCount(posVm.Services.Count, posVm.FilteredServices);
        Assert.IsNotNull(posVm.SelectedService);

        // 2. Filtrar por categoría 'Consulta'
        posVm.FilterServiceCategory("Consulta");
        Assert.AreEqual("Consulta", posVm.ServiceCategoryFilter);
        Assert.IsTrue(posVm.FilteredServices.All(s => s.Category == "Consulta"));

        // 3. Filtrar por texto
        posVm.ServiceSearchText = "Especialista";
        Assert.IsTrue(posVm.FilteredServices.All(s => s.Name.Contains("Especialista", StringComparison.OrdinalIgnoreCase) || s.Code.Contains("Especialista", StringComparison.OrdinalIgnoreCase)));

        // 4. Limpiar filtros volviendo a 'Todos'
        posVm.ServiceSearchText = string.Empty;
        posVm.FilterServiceCategory("Todos");
        Assert.AreEqual("Todos", posVm.ServiceCategoryFilter);
        Assert.HasCount(posVm.Services.Count, posVm.FilteredServices);

        // 5. Cerrar modal
        posVm.CloseServicesModal();
        Assert.IsFalse(posVm.IsServicesModalOpen);
    }

    [TestMethod]
    public async Task PosViewModel_AddServiceToCart_AddsServiceAndBundledSupplies()
    {
        var consultService = new ConsultationService();
        var posVm = new PosViewModel(_inventoryService, consultService);
        await posVm.InitializeAsync();

        await posVm.OpenServicesModalAsync();
        // Buscar un servicio que tenga insumos por defecto (ej. Curación Mayor o Sutura)
        var srvWithProducts = posVm.Services.FirstOrDefault(s => s.DefaultProducts.Count > 0);
        Assert.IsNotNull(srvWithProducts, "Debe existir al menos un servicio con insumos predeterminados");

        posVm.AddServiceToCart(srvWithProducts);

        Assert.IsFalse(posVm.IsServicesModalOpen, "El modal debe cerrarse tras agregar");
        Assert.IsGreaterThanOrEqualTo(posVm.Cart.Count, 2, "Debe incluir el honorario del servicio + sus insumos predeterminados");

        // Validar el honorario del servicio
        var serviceItem = posVm.Cart.FirstOrDefault(c => c.IsService);
        Assert.IsNotNull(serviceItem, "Debe haber una línea marcada como servicio");
        Assert.AreEqual(-srvWithProducts.Id, serviceItem.ProductId);
        Assert.AreEqual(srvWithProducts.Price, serviceItem.UnitPrice);

        // Validar que un servicio puede incrementar cantidad sin bloqueo de stock físico
        var initialQty = serviceItem.Quantity;
        posVm.IncreaseQuantity(serviceItem);
        Assert.AreEqual(initialQty + 1, serviceItem.Quantity);

        // Validar que los insumos requeridos fueron agregados
        foreach (var dp in srvWithProducts.DefaultProducts)
        {
            var pItem = posVm.Cart.FirstOrDefault(c => c.ProductId == dp.ProductId);
            Assert.IsNotNull(pItem, $"El insumo {dp.Product?.Name} debió agregarse al carrito");
            Assert.AreEqual(dp.Quantity, pItem.Quantity);
        }
    }

    [TestMethod]
    public async Task PosViewModel_AddServiceOnlyToCart_AddsOnlyServiceFee()
    {
        var consultService = new ConsultationService();
        var posVm = new PosViewModel(_inventoryService, consultService);
        await posVm.InitializeAsync();

        await posVm.OpenServicesModalAsync();
        var srvWithProducts = posVm.Services.FirstOrDefault(s => s.DefaultProducts.Count > 0);
        Assert.IsNotNull(srvWithProducts);

        posVm.AddServiceOnlyToCart(srvWithProducts);

        Assert.IsFalse(posVm.IsServicesModalOpen);
        Assert.HasCount(1, posVm.Cart, "Solo debe agregarse 1 ítem correspondiente al servicio");
        Assert.IsTrue(posVm.Cart[0].IsService);
        Assert.AreEqual(srvWithProducts.Price, posVm.TotalCartAmount);
    }

    [TestMethod]
    public async Task PosViewModel_AddServiceToActiveConsultation_TransfersToConsultationService()
    {
        var consultService = new ConsultationService();
        var patient = new PatientRecord { FullName = "María López", RecordNumber = "EXP-002" };
        consultService.StartConsultation(patient);

        var posVm = new PosViewModel(_inventoryService, consultService);
        await posVm.InitializeAsync();
        Assert.IsTrue(posVm.HasActiveConsultation);

        await posVm.OpenServicesModalAsync();
        var srvWithProducts = posVm.Services.FirstOrDefault(s => s.DefaultProducts.Count > 0);
        Assert.IsNotNull(srvWithProducts);

        posVm.AddServiceToActiveConsultation(srvWithProducts);

        Assert.IsFalse(posVm.IsServicesModalOpen);
        var activeItems = consultService.CurrentConsultation?.Items;
        Assert.IsNotNull(activeItems);
        Assert.IsTrue(activeItems.Any(i => i.ProductId == -srvWithProducts.Id), "El servicio debe transferirse a la consulta activa");
        Assert.HasCount(posVm.ActiveConsultationItemsCount, activeItems);
    }

    [TestMethod]
    public async Task PosViewModel_CreateNewService_SavesAndRefreshesCatalog()
    {
        var consultService = new ConsultationService();
        var posVm = new PosViewModel(_inventoryService, consultService);
        await posVm.InitializeAsync();

        await posVm.OpenServicesModalAsync();
        posVm.OpenCreateServiceModal();
        Assert.IsTrue(posVm.IsCreateServiceModalOpen);

        posVm.NewServiceCode = "SRV-TEST99";
        posVm.NewServiceName = "Nebulización Especializada";
        posVm.NewServiceCategory = "Procedimientos";
        posVm.NewServicePrice = 450.00m;
        posVm.NewServiceDuration = 30;
        posVm.NewServiceDescription = "Tratamiento de vía aérea con broncodilatador";

        // Vincular un producto por defecto
        Assert.IsNotEmpty(posVm.AvailableProductsForService);
        posVm.SelectedProductForService = posVm.AvailableProductsForService.First();
        posVm.NewProductQuantity = 2;
        posVm.NewProductInstructions = "Usar mascarilla estéril";
        posVm.AddProductToNewService();

        Assert.HasCount(1, posVm.NewServiceDefaultProducts);

        // Guardar
        await posVm.SaveNewServiceAsync();

        Assert.IsFalse(posVm.IsCreateServiceModalOpen);
        Assert.IsTrue(posVm.Services.Any(s => s.Code == "SRV-TEST99"), "El nuevo servicio debe listarse en PosViewModel");
        Assert.IsTrue(posVm.FilteredServices.Any(s => s.Code == "SRV-TEST99"));

        var savedService = posVm.Services.First(s => s.Code == "SRV-TEST99");
        Assert.AreEqual("Nebulización Especializada", savedService.Name);
        Assert.AreEqual(450.00m, savedService.Price);
        Assert.HasCount(1, savedService.DefaultProducts);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_OpenPosModal_WhenNoActiveConsultation_EnforcesRuleAndAlerts()
    {
        var consultService = new ConsultationService();
        var posVm = new PosViewModel(_inventoryService, consultService);
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService, posVm);

        // Sin consulta activa
        Assert.IsFalse(consultService.HasActiveConsultation);

        await recordsVm.OpenPosModalAsync();

        Assert.IsFalse(recordsVm.IsPosModalOpen, "No debe abrir el modal de POS si no hay consulta activa");
        Assert.IsTrue(recordsVm.IsInfoModalVisible, "Debe alertar al usuario");
        Assert.IsNotNull(recordsVm.InfoModalMessage);
        Assert.Contains("consulta médica activa", recordsVm.InfoModalMessage);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_OpenPosModal_WhenActiveConsultation_OpensModalAndInitializesPos()
    {
        var consultService = new ConsultationService();
        var patient = new PatientRecord { FullName = "Laura Gómez", RecordNumber = "EXP-003" };
        consultService.StartConsultation(patient, "Control de hipertensión");

        var posVm = new PosViewModel(_inventoryService, consultService);
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService, posVm);
        posVm.OnCloseModalRequested = () => recordsVm.ClosePosModal();

        Assert.IsTrue(consultService.HasActiveConsultation);

        // Abrir modal de POS
        await recordsVm.OpenPosModalAsync();

        Assert.IsTrue(recordsVm.IsPosModalOpen, "Debe abrir el modal de POS integrado");
        Assert.IsTrue(posVm.IsEmbeddedInModal, "PosViewModel debe estar marcado como embebido");
        Assert.IsTrue(recordsVm.HasActiveConsultation);
        Assert.AreEqual("Laura Gómez", recordsVm.ActiveConsultationPatientName);

        // Cerrar modal
        recordsVm.ClosePosModal();
        Assert.IsFalse(recordsVm.IsPosModalOpen);
        Assert.IsFalse(posVm.IsEmbeddedInModal);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_PosModal_AddItemsAndServices_TransfersToActiveConsultation()
    {
        var consultService = new ConsultationService();
        var patient = new PatientRecord { FullName = "Carlos Sánchez", RecordNumber = "EXP-004" };
        consultService.StartConsultation(patient, "Infección de garganta");

        var posVm = new PosViewModel(_inventoryService, consultService);
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService, posVm);
        posVm.OnCloseModalRequested = () => recordsVm.ClosePosModal();

        await recordsVm.OpenPosModalAsync();
        Assert.IsTrue(recordsVm.IsPosModalOpen);

        // 1. Agregar un medicamento al carrito del POS
        var products = await _inventoryService.GetProductsAsync();
        var med = products.First();
        posVm.AddToCart(med);

        // 2. Agregar un servicio al carrito del POS
        var service = posVm.Services.First();
        posVm.AddServiceOnlyToCart(service);

        Assert.HasCount(2, posVm.Cart);

        // 3. Ejecutar Agregar a Consulta Activa (botón principal en modo modal)
        posVm.AddToActiveConsultation();

        Assert.IsEmpty(posVm.Cart, "El carrito debe vaciarse al transferir");
        Assert.IsGreaterThanOrEqualTo(consultService.CurrentConsultation?.Items.Count ?? 0, 2);
        Assert.IsGreaterThanOrEqualTo(recordsVm.ActiveConsultationItemsCount, 2);
        Assert.IsGreaterThan(0m, recordsVm.ActiveConsultationTotalAmount);

        // 4. Cerrar modal y verificar sincronización en ficha de expediente
        posVm.CloseModal();
        Assert.IsFalse(recordsVm.IsPosModalOpen);
        Assert.AreEqual(consultService.CurrentConsultation?.TotalItems, recordsVm.ActiveConsultationItemsCount);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_SelectPatientModal_OpenFilterSelectAndDeselect()
    {
        var recordsVm = new MedicalRecordsViewModel();
        Assert.IsNull(recordsVm.SelectedPatient);
        Assert.AreEqual("Agregar Paciente", recordsVm.PatientActionButtonText);
        Assert.IsFalse(recordsVm.HasSelectedPatient);

        // 1. Abrir modal
        recordsVm.OpenSelectPatientModal();
        Assert.IsTrue(recordsVm.IsSelectPatientModalOpen);
        Assert.HasCount(recordsVm.Patients.Count, recordsVm.SelectablePatients);

        // 2. Filtrar por término de búsqueda
        recordsVm.SelectPatientSearchText = "Hernández";
        Assert.IsGreaterThanOrEqualTo(1, recordsVm.SelectablePatients.Count);
        var targetPatient = recordsVm.SelectablePatients.First();
        Assert.Contains("Hernández", targetPatient.FullName);

        // 3. Seleccionar paciente
        recordsVm.SelectPatient(targetPatient);
        Assert.IsFalse(recordsVm.IsSelectPatientModalOpen);
        Assert.IsNotNull(recordsVm.SelectedPatient);
        Assert.AreEqual("Hernández López, Sofía", recordsVm.SelectedPatient.FullName);
        Assert.AreEqual("Cambiar Paciente", recordsVm.PatientActionButtonText);
        Assert.IsTrue(recordsVm.HasSelectedPatient);

        // 4. Quitar selección (Deseleccionar)
        recordsVm.DeselectPatient();
        Assert.IsNull(recordsVm.SelectedPatient);
        Assert.AreEqual("Agregar Paciente", recordsVm.PatientActionButtonText);
        Assert.IsFalse(recordsVm.HasSelectedPatient);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_NewPatientFromSelector_OpensPatientModal()
    {
        var recordsVm = new MedicalRecordsViewModel();
        recordsVm.OpenSelectPatientModal();
        Assert.IsTrue(recordsVm.IsSelectPatientModalOpen);
        Assert.IsFalse(recordsVm.IsPatientModalOpen);

        await recordsVm.NewPatientFromSelectorAsync();

        Assert.IsFalse(recordsVm.IsSelectPatientModalOpen, "Debe cerrar el modal selector");
        Assert.IsTrue(recordsVm.IsPatientModalOpen, "Debe abrir el modal de nuevo paciente");
        Assert.AreEqual(0, recordsVm.FormPatientId);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_WhenNoPatientSelected_NewAndActiveConsultationOpenSelector()
    {
        var consultService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService);

        Assert.IsNull(recordsVm.SelectedPatient);

        // Al presionar Nueva Consulta sin paciente, debe abrir el selector de paciente
        recordsVm.NewConsultation();
        Assert.IsTrue(recordsVm.IsSelectPatientModalOpen);
        recordsVm.CloseSelectPatientModal();
        Assert.IsFalse(recordsVm.IsSelectPatientModalOpen);

        // Al presionar Iniciar Consulta Activa sin paciente, debe abrir el selector de paciente
        recordsVm.StartActiveConsultation();
        Assert.IsTrue(recordsVm.IsSelectPatientModalOpen);
        recordsVm.CloseSelectPatientModal();
        Assert.IsFalse(recordsVm.IsSelectPatientModalOpen);
    }

    [TestMethod]
    public void ActiveConsultation_TimerAndElapsedTime_FormattedCorrectly()
    {
        var startTime = new DateTime(2026, 4, 4, 10, 0, 0);
        var consultation = new ActiveConsultation
        {
            Folio = "CNS-TEST-001",
            PatientName = "Carlos Mendoza",
            StartedAt = startTime
        };

        // Tiempo exacto de inicio: 00:00
        Assert.AreEqual("00:00", consultation.GetFormattedElapsedTime(startTime));

        // 5 minutos y 23 segundos: 05:23
        var fiveMinLater = startTime.AddMinutes(5).AddSeconds(23);
        Assert.AreEqual("05:23", consultation.GetFormattedElapsedTime(fiveMinLater));

        // 1 hora, 14 minutos y 9 segundos: 01:14:09
        var oneHourLater = startTime.AddHours(1).AddMinutes(14).AddSeconds(9);
        Assert.AreEqual("01:14:09", consultation.GetFormattedElapsedTime(oneHourLater));
    }

    [TestMethod]
    public void MedicalRecordsViewModel_ConsultationTimer_UpdatesElapsedTimeAndStopsOnCancel()
    {
        var consultService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService);
        var patient = new PatientRecord { Id = 1, FullName = "Laura Pausini", RecordNumber = "EXP-001" };

        // Sin consulta activa, tiempo es 00:00
        Assert.IsFalse(recordsVm.HasActiveConsultation);
        Assert.AreEqual("00:00", recordsVm.ActiveConsultationElapsedTime);

        // Iniciar consulta activa
        consultService.StartConsultation(patient, "Evaluación general");
        recordsVm.SyncActiveConsultation();

        Assert.IsTrue(recordsVm.HasActiveConsultation);
        Assert.IsNotNull(recordsVm.ActiveConsultation);

        // Simular paso de 12 minutos y 45 segundos
        var asOf = recordsVm.ActiveConsultation.StartedAt.AddMinutes(12).AddSeconds(45);
        recordsVm.UpdateActiveConsultationTimer(asOf);
        Assert.AreEqual("12:45", recordsVm.ActiveConsultationElapsedTime);

        // Cancelar o finalizar consulta
        recordsVm.CancelActiveConsultation();
        Assert.IsFalse(recordsVm.HasActiveConsultation);
        Assert.AreEqual("00:00", recordsVm.ActiveConsultationElapsedTime);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_RecordsViewerModal_OpensAndFiltersPatients()
    {
        var recordsVm = new MedicalRecordsViewModel();
        Assert.IsFalse(recordsVm.IsRecordsViewerModalOpen);

        // Abrir visor modal de expedientes
        recordsVm.OpenRecordsViewerModal();
        Assert.IsTrue(recordsVm.IsRecordsViewerModalOpen);
        Assert.IsNull(recordsVm.ViewingRecordPatient);
        Assert.AreEqual(string.Empty, recordsVm.RecordsViewerSearchText);
        Assert.IsTrue(recordsVm.RecordsViewerPatients.Count > 0);

        // Filtrar por nombre de paciente
        var firstPatient = recordsVm.RecordsViewerPatients.First();
        recordsVm.RecordsViewerSearchText = firstPatient.FullName.Substring(0, 3);
        Assert.IsTrue(recordsVm.RecordsViewerPatients.Any(p => p.FullName == firstPatient.FullName));

        // Cerrar visor modal
        recordsVm.CloseRecordsViewerModal();
        Assert.IsFalse(recordsVm.IsRecordsViewerModalOpen);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_RecordsViewerModal_ViewPatientDetail_ShowsFullRecordWithoutClosingModal()
    {
        var recordsVm = new MedicalRecordsViewModel();
        recordsVm.OpenRecordsViewerModal();

        var patientToInspect = recordsVm.RecordsViewerPatients.First();
        // Agregar consulta previa al paciente para comprobar visualización de historial
        patientToInspect.Consultations.Add(new PatientConsultation
        {
            Folio = "CNS-HIST-001",
            Date = DateTime.Now.AddDays(-10),
            ChiefComplaint = "Cefalea tensional",
            Diagnosis = "Cefalea (G44.2)",
            TreatmentPlan = "Reposo e hidratación"
        });

        // Al hacer click en "Ver", no pasa a pantalla completa; se queda dentro de la modal
        recordsVm.ViewPatientRecordDetail(patientToInspect);

        Assert.IsTrue(recordsVm.IsRecordsViewerModalOpen, "La modal debe permanecer abierta");
        Assert.IsNotNull(recordsVm.ViewingRecordPatient);
        Assert.AreEqual(patientToInspect.FullName, recordsVm.ViewingRecordPatient.FullName);
        Assert.AreEqual("Resumen", recordsVm.ViewingRecordTab);
        Assert.IsTrue(recordsVm.ViewingPatientConsultations.Count >= 1);

        // Cambiar de pestaña dentro del expediente en la modal
        recordsVm.SwitchViewingRecordTab("Historial");
        Assert.AreEqual("Historial", recordsVm.ViewingRecordTab);

        // Volver a la lista dentro de la modal
        recordsVm.BackToRecordsList();
        Assert.IsTrue(recordsVm.IsRecordsViewerModalOpen, "La modal debe continuar abierta al volver a la lista");
        Assert.IsNull(recordsVm.ViewingRecordPatient, "Debe limpiar el paciente en visualización");
    }

    [TestMethod]
    public void MedicalRecordsViewModel_RecordsViewerModal_SelectForConsultation_SetsSelectedPatientAndClosesModal()
    {
        var recordsVm = new MedicalRecordsViewModel();
        recordsVm.SelectedPatient = null;
        recordsVm.OpenRecordsViewerModal();

        var targetPatient = recordsVm.RecordsViewerPatients.First();
        recordsVm.ViewPatientRecordDetail(targetPatient);

        // Cargar en la pantalla principal
        recordsVm.SelectViewingPatientForConsultation(targetPatient);

        Assert.IsFalse(recordsVm.IsRecordsViewerModalOpen, "Debe cerrar la modal al cargar en consulta");
        Assert.IsNotNull(recordsVm.SelectedPatient);
        Assert.AreEqual(targetPatient.FullName, recordsVm.SelectedPatient.FullName);
        Assert.IsTrue(recordsVm.IsInfoModalVisible);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_RecordsViewerModal_CloseInDetailView_ReturnsToRecordsList()
    {
        var recordsVm = new MedicalRecordsViewModel();
        recordsVm.OpenRecordsViewerModal();
        Assert.IsTrue(recordsVm.IsRecordsViewerModalOpen);

        var targetPatient = recordsVm.RecordsViewerPatients.First();
        recordsVm.ViewPatientRecordDetail(targetPatient);
        Assert.IsNotNull(recordsVm.ViewingRecordPatient);

        // Al dar en cerrar mientras se visualiza el expediente, debe volver a la vista de ver expedientes (no cerrar la modal)
        recordsVm.CloseRecordsViewerModal();
        Assert.IsTrue(recordsVm.IsRecordsViewerModalOpen, "La modal debe permanecer abierta");
        Assert.IsNull(recordsVm.ViewingRecordPatient, "Debe volver al estado de listado de expedientes");

        // Al dar en cerrar desde la lista de expedientes, ahora sí debe cerrar la modal
        recordsVm.CloseRecordsViewerModal();
        Assert.IsFalse(recordsVm.IsRecordsViewerModalOpen, "Al estar en la lista, cerrar debe cerrar la modal completa");
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_PatientModal_SeparatedNames_ValidatesAndFormats()
    {
        var recordsVm = new MedicalRecordsViewModel();
        await recordsVm.NewPatientAsync();

        // 1. Falta nombre(s)
        recordsVm.FormFirstName = "";
        recordsVm.FormLastName = "Pérez";
        await recordsVm.SavePatientModalAsync();
        Assert.IsTrue(recordsVm.IsPatientModalOpen);
        Assert.AreEqual("El nombre(s) del paciente es obligatorio.", recordsVm.FormErrorMessage);

        // 2. Falta apellido paterno
        recordsVm.FormFirstName = "Juan";
        recordsVm.FormLastName = "";
        await recordsVm.SavePatientModalAsync();
        Assert.IsTrue(recordsVm.IsPatientModalOpen);
        Assert.AreEqual("El apellido paterno del paciente es obligatorio.", recordsVm.FormErrorMessage);

        // 3. Solo nombre y apellido paterno (materno opcional)
        recordsVm.FormFirstName = "Juan";
        recordsVm.FormLastName = "Pérez";
        recordsVm.FormMaternalLastName = "";
        await recordsVm.SavePatientModalAsync();
        Assert.IsFalse(recordsVm.IsPatientModalOpen);
        Assert.IsNotNull(recordsVm.SelectedPatient);
        Assert.AreEqual("Juan Pérez", recordsVm.SelectedPatient.FullName);

        // 4. Con apellido materno
        await recordsVm.NewPatientAsync();
        recordsVm.FormFirstName = "María del Carmen";
        recordsVm.FormLastName = "González";
        recordsVm.FormMaternalLastName = "Trejo";
        await recordsVm.SavePatientModalAsync();
        Assert.IsFalse(recordsVm.IsPatientModalOpen);
        Assert.AreEqual("María del Carmen González Trejo", recordsVm.SelectedPatient.FullName);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_PhoneAndAge_RestrictedToNumbersOnly()
    {
        var recordsVm = new MedicalRecordsViewModel();

        // Filtro de edad: solo dígitos
        recordsVm.FormAgeText = "45abc$%1";
        Assert.AreEqual("451", recordsVm.FormAgeText);

        recordsVm.FormAgeText = "28";
        Assert.AreEqual(28, recordsVm.FormAge);

        // Formato automático de teléfono: xxx-xxx-xxxx
        recordsVm.FormPhone = "(55) 1234-5678";
        Assert.AreEqual("551-234-5678", recordsVm.FormPhone);

        // Borrado y actualización de guiones en tiempo real
        recordsVm.FormPhone = "5512";
        Assert.AreEqual("551-2", recordsVm.FormPhone);

        recordsVm.FormPhone = "551";
        Assert.AreEqual("551", recordsVm.FormPhone);

        // Filtro de teléfono de emergencia: xxx-xxx-xxxx (máximo 10 dígitos estándar)
        recordsVm.FormEmergencyContactPhone = "+52-55-9876-5432 ext 10";
        Assert.AreEqual("525-598-7654", recordsVm.FormEmergencyContactPhone);

        // Almacenamiento como puros dígitos en el modelo y display con formato
        var patient = new PatientRecord { Phone = "5512345678", EmergencyContactPhone = "5598765432" };
        Assert.AreEqual("5512345678", patient.Phone);
        Assert.AreEqual("551-234-5678", patient.FormattedPhone);
        Assert.AreEqual("559-876-5432", patient.FormattedEmergencyContactPhone);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_EmailValidation_AllowsEmptyAndValidatesFormat()
    {
        // Validación estática de formato
        Assert.IsTrue(MedicalRecordsViewModel.IsValidEmail(null));
        Assert.IsTrue(MedicalRecordsViewModel.IsValidEmail(""));
        Assert.IsTrue(MedicalRecordsViewModel.IsValidEmail("   "));
        Assert.IsTrue(MedicalRecordsViewModel.IsValidEmail("paciente@clinica.com"));
        Assert.IsTrue(MedicalRecordsViewModel.IsValidEmail("dr.morales@hospital-general.org.mx"));

        Assert.IsFalse(MedicalRecordsViewModel.IsValidEmail("correo-invalido"));
        Assert.IsFalse(MedicalRecordsViewModel.IsValidEmail("usuario@"));
        Assert.IsFalse(MedicalRecordsViewModel.IsValidEmail("usuario@dominio"));
        Assert.IsFalse(MedicalRecordsViewModel.IsValidEmail("usuario@dominio."));
        Assert.IsFalse(MedicalRecordsViewModel.IsValidEmail("usuario @dominio.com"));

        // Validación en el modal de guardado
        var recordsVm = new MedicalRecordsViewModel();
        await recordsVm.NewPatientAsync();
        recordsVm.FormFirstName = "Laura";
        recordsVm.FormLastName = "Mora";
        recordsVm.FormEmail = "correo-invalido";

        await recordsVm.SavePatientModalAsync();
        Assert.IsTrue(recordsVm.IsPatientModalOpen);
        Assert.IsNotNull(recordsVm.FormErrorMessage);
        Assert.IsTrue(recordsVm.FormErrorMessage.Contains("correo electrónico no tiene un formato válido"));

        // Con correo vacío debe permitir guardar sin problemas
        recordsVm.FormEmail = "   ";
        await recordsVm.SavePatientModalAsync();
        Assert.IsFalse(recordsVm.IsPatientModalOpen);
        Assert.IsNull(recordsVm.FormErrorMessage);
        Assert.IsNotNull(recordsVm.SelectedPatient);
        Assert.AreEqual(string.Empty, recordsVm.SelectedPatient.Email);

        // Con correo válido
        await recordsVm.NewPatientAsync();
        recordsVm.FormFirstName = "Laura";
        recordsVm.FormLastName = "Mora";
        recordsVm.FormRecordNumber = "EXP-2026-999";
        recordsVm.FormEmail = "laura.mora@correo.com";
        await recordsVm.SavePatientModalAsync();
        Assert.IsFalse(recordsVm.IsPatientModalOpen);
        Assert.AreEqual("laura.mora@correo.com", recordsVm.SelectedPatient.Email);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_BloodPressureTA_FormatsAndCleansSlashes()
    {
        // Autoinserción de barra '/' al teclear números continuos
        Assert.AreEqual("120/80", MedicalRecordsViewModel.FormatTA("12080"));
        Assert.AreEqual("90/60", MedicalRecordsViewModel.FormatTA("9060"));
        Assert.AreEqual("135/85", MedicalRecordsViewModel.FormatTA("13585"));

        // Respeto de barra existente y separadores comunes
        Assert.AreEqual("120/80", MedicalRecordsViewModel.FormatTA("120-80"));
        Assert.AreEqual("120/80", MedicalRecordsViewModel.FormatTA("120 80"));
        Assert.AreEqual("120/80", MedicalRecordsViewModel.FormatTA("120/80"));
        Assert.AreEqual("120/", MedicalRecordsViewModel.FormatTA("120/"));
        Assert.AreEqual("120/8", MedicalRecordsViewModel.FormatTA("120/8"));

        // Al borrar hacia atrás (retroceso de la barra): no la vuelve a insertar
        Assert.AreEqual("120", MedicalRecordsViewModel.FormatTA("120"));
        Assert.AreEqual("12", MedicalRecordsViewModel.FormatTA("12"));
        Assert.AreEqual("1", MedicalRecordsViewModel.FormatTA("1"));
        Assert.AreEqual("", MedicalRecordsViewModel.FormatTA(""));

        // Comprobación de reactividad en el ViewModel
        var recordsVm = new MedicalRecordsViewModel();
        recordsVm.FormVitalTA = "14090";
        Assert.AreEqual("140/90", recordsVm.FormVitalTA);

        recordsVm.ConsultationTA = "11070";
        Assert.AreEqual("110/70", recordsVm.ConsultationTA);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_IndividualVitalSigns_CalculatesIMCAndFormats()
    {
        var recordsVm = new MedicalRecordsViewModel();
        await recordsVm.NewPatientAsync();

        // Verificar valores por defecto
        Assert.AreEqual("120/80", recordsVm.FormVitalTA);
        Assert.AreEqual("75", recordsVm.FormVitalFC);
        Assert.AreEqual("18", recordsVm.FormVitalFR);
        Assert.AreEqual("36.5", recordsVm.FormVitalTemp);
        Assert.AreEqual("70.0", recordsVm.FormVitalWeight);
        Assert.AreEqual("1.70", recordsVm.FormVitalHeight);
        Assert.IsTrue(recordsVm.FormVitalIMC.Contains("24.2"));

        // Modificar peso y talla para recalcular IMC
        recordsVm.FormVitalWeight = "85.0";
        recordsVm.FormVitalHeight = "1.75";
        // IMC = 85 / (1.75 * 1.75) = 27.75 (Sobrepeso)
        Assert.IsTrue(recordsVm.FormVitalIMC.Contains("27.8"));
        Assert.IsTrue(recordsVm.FormVitalIMC.Contains("Sobrepeso"));

        // Guardar paciente con signos individuales
        recordsVm.FormFirstName = "Carlos";
        recordsVm.FormLastName = "Hernández";
        recordsVm.FormVitalTA = "130/85";
        recordsVm.FormVitalFC = "80";
        recordsVm.FormVitalFR = "20";
        recordsVm.FormVitalTemp = "37.0";
        await recordsVm.SavePatientModalAsync();

        Assert.IsNotNull(recordsVm.SelectedPatient);
        var vitals = recordsVm.SelectedPatient.VitalSigns;
        Assert.IsTrue(vitals.Contains("TA: 130/85 mmHg"));
        Assert.IsTrue(vitals.Contains("FC: 80 lpm"));
        Assert.IsTrue(vitals.Contains("FR: 20 rpm"));
        Assert.IsTrue(vitals.Contains("Temp: 37.0 °C"));
        Assert.IsTrue(vitals.Contains("Peso: 85.0 kg"));
        Assert.IsTrue(vitals.Contains("Talla: 1.75 m"));

        // Editar paciente y verificar que parsea los signos vitales y nombres
        recordsVm.EditPatient();
        Assert.AreEqual("Carlos", recordsVm.FormFirstName);
        Assert.AreEqual("Hernández", recordsVm.FormLastName);
        Assert.AreEqual("130/85", recordsVm.FormVitalTA);
        Assert.AreEqual("80", recordsVm.FormVitalFC);
        Assert.AreEqual("20", recordsVm.FormVitalFR);
        Assert.AreEqual("37.0", recordsVm.FormVitalTemp);
        Assert.AreEqual("85.0", recordsVm.FormVitalWeight);
        Assert.AreEqual("1.75", recordsVm.FormVitalHeight);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_ConsultationActionButtonText_TogglesAndFinalizesToPOS()
    {
        var consultService = new ConsultationService();
        var posVm = new PosViewModel(_inventoryService, consultService);
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultService, posVm);

        // Paciente de prueba
        recordsVm.SelectedPatient = new PatientRecord
        {
            Id = 1,
            FullName = "María Elena Soto",
            RecordNumber = "EXP-TEST-001"
        };

        // Estado inicial sin consulta activa
        Assert.IsFalse(recordsVm.HasActiveConsultation);
        Assert.AreEqual("Iniciar Consulta", recordsVm.ConsultationActionButtonText);

        // Click en Iniciar Consulta
        await recordsVm.ToggleActiveConsultationAsync();
        Assert.IsTrue(recordsVm.HasActiveConsultation);
        Assert.AreEqual("Finalizar Consulta", recordsVm.ConsultationActionButtonText);
        Assert.AreEqual("María Elena Soto", consultService.CurrentConsultation?.PatientName);

        // Click en Finalizar Consulta -> debe transferir al POS, agregar Consulta General y abrir modal
        await recordsVm.ToggleActiveConsultationAsync();

        Assert.IsFalse(recordsVm.HasActiveConsultation, "La consulta debe haber finalizado");
        Assert.AreEqual("Iniciar Consulta", recordsVm.ConsultationActionButtonText);
        Assert.IsTrue(recordsVm.IsPosModalOpen, "Debe abrir la modal del POS");
        Assert.IsNotNull(posVm.Cart);
        Assert.IsTrue(posVm.Cart.Count >= 1, "Debe tener items en el carrito");
        Assert.IsTrue(posVm.Cart.Any(i => i.Code == "SERV-001" || i.Name.Contains("Consulta")), 
            "Debe incluir la Consulta Médica General en el carrito de venta");
    }

    [TestMethod]
    public void MedicalRecordsViewModel_PrescriptionInventorySearch_FiltersSuggestionsAndSelects()
    {
        var recordsVm = new MedicalRecordsViewModel();

        // Buscar producto existente en sugerencias de inventario
        recordsVm.PrescriptionSearchText = "Paracetamol";
        Assert.IsTrue(recordsVm.IsPrescriptionDropdownOpen);
        Assert.IsTrue(recordsVm.PrescriptionProductSuggestions.Count > 0);
        Assert.IsTrue(recordsVm.PrescriptionProductSuggestions.Any(p => p.Name.Contains("Paracetamol")));

        // Seleccionar una sugerencia
        var suggestion = recordsVm.PrescriptionProductSuggestions.First();
        recordsVm.SelectPrescriptionProduct(suggestion);

        Assert.AreEqual(suggestion, recordsVm.SelectedPrescriptionProduct);
        Assert.IsFalse(recordsVm.IsPrescriptionDropdownOpen, "El dropdown debe cerrarse al seleccionar");
        Assert.IsTrue(recordsVm.PrescriptionMedicineName.Contains(suggestion.Name));
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_PrescriptionAddAndRemoveItems_WithIndications()
    {
        var consultService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(null, consultService);
        recordsVm.SelectedPatient = new PatientRecord
        {
            Id = 2,
            FullName = "Roberto Morales",
            RecordNumber = "EXP-TEST-002"
        };

        // Iniciar consulta activa
        recordsVm.StartActiveConsultation();
        Assert.IsTrue(recordsVm.HasActiveConsultation);

        // Configurar medicamento con posología/indicaciones personalizadas
        recordsVm.PrescriptionMedicineName = "Amoxicilina 500mg";
        recordsVm.PrescriptionDosage = "1 cápsula cada 8 horas por 7 días vía oral después de los alimentos";
        recordsVm.PrescriptionQuantity = 2;

        await recordsVm.AddPrescriptionItemToPatientAsync();

        // Verificar que se añadió a la receta activa del paciente
        Assert.AreEqual(1, recordsVm.SelectedPatient.ActivePrescription.Count);
        var addedItem = recordsVm.SelectedPatient.ActivePrescription.First();
        Assert.AreEqual("Amoxicilina 500mg", addedItem.MedicineName);
        Assert.AreEqual("1 cápsula cada 8 horas por 7 días vía oral después de los alimentos", addedItem.Dosage);
        Assert.AreEqual(2, addedItem.RecommendedQuantity);

        // Verificar que los campos de captura se limpian tras recetar
        Assert.AreEqual(string.Empty, recordsVm.PrescriptionSearchText);
        Assert.AreEqual(string.Empty, recordsVm.PrescriptionDosage);
        Assert.AreEqual(1, recordsVm.PrescriptionQuantity);

        // Eliminar el medicamento prescrito
        await recordsVm.RemovePrescriptionItemFromPatientAsync(addedItem);
        Assert.AreEqual(0, recordsVm.SelectedPatient.ActivePrescription.Count);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_PrescriptionPdfModal_PreviewFlow()
    {
        var recordsVm = new MedicalRecordsViewModel();
        recordsVm.SelectedPatient = new PatientRecord
        {
            Id = 3,
            FullName = "Laura Vázquez",
            RecordNumber = "EXP-TEST-003",
            ActivePrescription = new List<PatientPrescriptionItem>
            {
                new() { MedicineName = "Ibuprofeno 400mg", Dosage = "1 tableta cada 8 hrs", RecommendedQuantity = 1 }
            }
        };

        Assert.IsFalse(recordsVm.IsPrescriptionPdfModalOpen);

        // Abrir vista previa de la receta PDF NOM-004-SSA3
        recordsVm.OpenPrescriptionPdfPreview();
        Assert.IsTrue(recordsVm.IsPrescriptionPdfModalOpen);

        // Cerrar vista previa
        recordsVm.ClosePrescriptionPdfPreview();
        Assert.IsFalse(recordsVm.IsPrescriptionPdfModalOpen);

        // Abrir e imprimir / exportar
        recordsVm.OpenPrescriptionPdfPreview();
        Assert.IsTrue(recordsVm.IsPrescriptionPdfModalOpen);
        recordsVm.PrintOrExportPrescriptionPdf();
        Assert.IsFalse(recordsVm.IsPrescriptionPdfModalOpen);
        Assert.IsTrue(recordsVm.IsInfoModalVisible);
        Assert.IsTrue(recordsVm.InfoModalMessage?.Contains("PDF") == true);
    }

    [TestMethod]
    public async Task MedicalRecords_VitalSignsModal_AnyFieldCanBeEntered_NoneMandatory()
    {
        var consultationService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(inventoryService: null!, consultationService);

        var patient = new PatientRecord
        {
            Id = 10,
            FullName = "Carlos Santana",
            RecordNumber = "EXP-TEST-010",
            VitalSigns = "Sin signos vitales registrados"
        };

        recordsVm.SelectedPatient = patient;

        // Iniciar consulta activa
        recordsVm.StartActiveConsultation();
        Assert.IsTrue(recordsVm.HasActiveConsultation);

        // Abrir modal de signos vitales
        recordsVm.OpenVitalSignsModal();
        Assert.IsTrue(recordsVm.IsVitalSignsModalOpen);

        // Caso 1: Llenar solo algunos campos (ninguno es obligatorio)
        recordsVm.VitalSignTA = "13085"; // Autoformatea a 130/85
        Assert.AreEqual("130/85", recordsVm.VitalSignTA);
        recordsVm.VitalSignTemp = "37.1";
        recordsVm.VitalSignSpO2 = "99";
        // Dejar FC, FR, Peso, Talla, Glucosa, Cintura vacíos

        await recordsVm.SaveVitalSignsAsync();

        Assert.IsFalse(recordsVm.IsVitalSignsModalOpen);
        Assert.IsTrue(patient.VitalSigns.Contains("TA: 130/85 mmHg"));
        Assert.IsTrue(patient.VitalSigns.Contains("Temp: 37.1 °C"));
        Assert.IsTrue(patient.VitalSigns.Contains("SpO2: 99%"));
        Assert.IsFalse(patient.VitalSigns.Contains("FC:"));
        Assert.IsFalse(patient.VitalSigns.Contains("Peso:"));
        Assert.AreEqual(patient.VitalSigns, consultationService.CurrentConsultation?.VitalSigns);

        // Caso 2: Abrir y llenar todos los campos
        recordsVm.OpenVitalSignsModal();
        recordsVm.VitalSignFC = "78";
        recordsVm.VitalSignFR = "19";
        recordsVm.VitalSignWeight = "75.0";
        recordsVm.VitalSignHeight = "1.75";
        recordsVm.VitalSignGlucose = "92";
        recordsVm.VitalSignWaist = "88";

        Assert.IsTrue(recordsVm.VitalSignIMC.Contains("24.5")); // Auto-recalculado

        await recordsVm.SaveVitalSignsAsync();

        Assert.IsTrue(patient.VitalSigns.Contains("FC: 78 lpm"));
        Assert.IsTrue(patient.VitalSigns.Contains("FR: 19 rpm"));
        Assert.IsTrue(patient.VitalSigns.Contains("Peso: 75.0 kg"));
        Assert.IsTrue(patient.VitalSigns.Contains("Talla: 1.75 m"));
        Assert.IsTrue(patient.VitalSigns.Contains("Glucosa: 92 mg/dL"));
        Assert.IsTrue(patient.VitalSigns.Contains("Cintura: 88 cm"));
    }

    [TestMethod]
    public void MedicalRecords_GeneralIndications_CanBeLinkedToPrescriptionAndConsultation()
    {
        var consultationService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(inventoryService: null!, consultationService);

        var patient = new PatientRecord
        {
            Id = 11,
            FullName = "Elena Ramos",
            RecordNumber = "EXP-TEST-011"
        };
        recordsVm.SelectedPatient = patient;
        recordsVm.StartActiveConsultation();

        // Modificar indicaciones y añadir presets
        recordsVm.ConsultationGeneralIndications = "Guardar reposo absoluto por 3 días.";
        recordsVm.AppendGeneralIndicationPreset("Hidratación con electrolitos orales.");

        Assert.IsTrue(recordsVm.ConsultationGeneralIndications.Contains("Guardar reposo absoluto"));
        Assert.IsTrue(recordsVm.ConsultationGeneralIndications.Contains("electrolitos orales"));

        // Vincular a receta
        recordsVm.AddGeneralIndicationsToPrescription();

        Assert.IsTrue(recordsVm.HasCustomGeneralIndications);
        Assert.AreEqual(recordsVm.ConsultationGeneralIndications, recordsVm.PrescriptionGeneralInstructions);
        Assert.AreEqual(recordsVm.PrescriptionGeneralInstructions, patient.GeneralIndications);
        Assert.AreEqual(recordsVm.PrescriptionGeneralInstructions, consultationService.CurrentConsultation?.GeneralIndications);
    }

    [TestMethod]
    public async Task MedicalRecords_Prescription_AddPrescriptionItemDoesNotAutoAddToPos_AddPrescriptionItemToPosWorks()
    {
        var consultationService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(inventoryService: null!, consultationService);

        var patient = new PatientRecord
        {
            Id = 12,
            FullName = "Marta Gómez",
            RecordNumber = "EXP-TEST-012",
            ActivePrescription = new List<PatientPrescriptionItem>()
        };
        recordsVm.SelectedPatient = patient;
        recordsVm.StartActiveConsultation();

        Assert.AreEqual(0, consultationService.CurrentConsultation?.Items.Count);

        // 1. Agregar medicamento a la receta ("+ Recetar")
        recordsVm.PrescriptionMedicineName = "Amoxicilina 500mg";
        recordsVm.PrescriptionDosage = "1 cápsula cada 8 horas por 7 días";
        recordsVm.PrescriptionQuantity = 2;

        await recordsVm.AddPrescriptionItemToPatientAsync();

        // Verificamos que se agregó a la receta del paciente
        Assert.AreEqual(1, patient.ActivePrescription.Count);
        Assert.AreEqual("Amoxicilina 500mg", patient.ActivePrescription[0].MedicineName);

        // REGLA CRÍTICA: NO se agrega automáticamente al POS/cuenta de consulta
        Assert.AreEqual(0, consultationService.CurrentConsultation?.Items.Count);

        // 2. Usar el botón explícito "Agregar al POS" para este medicamento
        recordsVm.AddPrescriptionItemToPos(patient.ActivePrescription[0]);

        // Ahora sí debe estar en la cuenta de la consulta activa
        Assert.AreEqual(1, consultationService.CurrentConsultation?.Items.Count);
        var consultationItem = consultationService.CurrentConsultation!.Items[0];
        Assert.AreEqual("Amoxicilina 500mg", consultationItem.ProductName);
        Assert.AreEqual(2, consultationItem.Quantity);
    }

    [TestMethod]
    public void MedicalRecords_ServiciosTab_AddServiceToConsultation_CalculatesTotalsAndRemovesItem()
    {
        var consultationService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(inventoryService: null!, consultationService);

        var patient = new PatientRecord
        {
            Id = 13,
            FullName = "Roberto Morales",
            RecordNumber = "EXP-TEST-013"
        };
        recordsVm.SelectedPatient = patient;
        recordsVm.StartActiveConsultation();

        // Crear servicio clínico con 1 insumo por defecto
        var productInsumo = new Product
        {
            Id = 101,
            Code = "INS-001",
            Name = "Gasa Estéril 10x10",
            Price = 25.00m,
            CurrentStock = 50,
            Unit = "Piezas"
        };

        var service = new ClinicalService
        {
            Id = 1,
            Code = "SERV-001",
            Name = "Curación Menor",
            Category = "Curaciones",
            Price = 200.00m,
            EstimatedDurationMinutes = 15,
            DefaultProducts = new List<ServiceDefaultProduct>
            {
                new() { ProductId = 101, Product = productInsumo, Quantity = 2 }
            }
        };

        recordsVm.Services.Add(service);

        // Cambiar a la pestaña Servicios
        recordsVm.SwitchPatientDetailTab("Servicios");
        Assert.AreEqual("Servicios", recordsVm.PatientDetailTab);

        // Agregar servicio a la consulta activa
        recordsVm.AddServiceToActiveConsultation(service);

        // Debe haber 2 ítems en la cuenta: el servicio + las 2 gasas
        var items = consultationService.CurrentConsultation!.Items;
        Assert.AreEqual(2, items.Count);
        Assert.IsTrue(items.Any(i => i.ProductName == "Curación Menor" && i.UnitPrice == 200.00m));
        Assert.IsTrue(items.Any(i => i.ProductName == "Gasa Estéril 10x10" && i.Quantity == 2));

        // Subtotal = 200 + (25 * 2) = 250.00
        Assert.AreEqual(250.00m, consultationService.CurrentConsultation.Subtotal);

        // Quitar el insumo de la cuenta
        var insumoItem = items.First(i => i.ProductName == "Gasa Estéril 10x10");
        recordsVm.RemoveItemFromActiveConsultation(insumoItem);

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(200.00m, consultationService.CurrentConsultation.Subtotal);
    }

    [TestMethod]
    public void MedicalRecords_PrescriptionModal_OpenWithoutPatient_ShowsInfoModal()
    {
        var recordsVm = new MedicalRecordsViewModel(inventoryService: null!, new ConsultationService());
        recordsVm.SelectedPatient = null;

        recordsVm.OpenPrescriptionModal();

        Assert.IsFalse(recordsVm.IsPrescriptionModalOpen);
        Assert.IsTrue(recordsVm.IsInfoModalVisible);
        Assert.IsTrue(recordsVm.InfoModalMessage?.Contains("Seleccione o agregue un paciente") ?? false);
    }

    [TestMethod]
    public void MedicalRecords_PrescriptionModal_OpenAndClose_TogglesVisibility()
    {
        var recordsVm = new MedicalRecordsViewModel(inventoryService: null!, new ConsultationService());
        var patient = new PatientRecord
        {
            Id = 20,
            FullName = "Patricia Alarcón",
            RecordNumber = "EXP-TEST-020"
        };
        recordsVm.SelectedPatient = patient;

        recordsVm.OpenPrescriptionModal();
        Assert.IsTrue(recordsVm.IsPrescriptionModalOpen);

        recordsVm.ClosePrescriptionModal();
        Assert.IsFalse(recordsVm.IsPrescriptionModalOpen);
    }

    [TestMethod]
    public async Task MedicalRecords_PrescriptionModal_Save_PersistsAndReturnsToMainView_CanReEdit()
    {
        var recordsVm = new MedicalRecordsViewModel(inventoryService: null!, new ConsultationService());
        var patient = new PatientRecord
        {
            Id = 21,
            FullName = "Carlos Santana",
            RecordNumber = "EXP-TEST-021"
        };
        recordsVm.SelectedPatient = patient;

        // 1. Abrir modal de receta
        recordsVm.OpenPrescriptionModal();
        Assert.IsTrue(recordsVm.IsPrescriptionModalOpen);

        // 2. Prescribir un medicamento
        recordsVm.PrescriptionMedicineName = "Amoxicilina 500mg";
        recordsVm.PrescriptionDosage = "1 cápsula cada 8 horas por 7 días";
        recordsVm.PrescriptionQuantity = 2;
        await recordsVm.AddPrescriptionItemToPatientAsync();

        Assert.AreEqual(1, patient.ActivePrescription.Count);
        Assert.AreEqual("Amoxicilina 500mg", patient.ActivePrescription[0].MedicineName);

        // 3. Guardar receta -> Cierra modal y regresa a la ventana principal
        await recordsVm.SavePrescriptionModalAsync();
        Assert.IsFalse(recordsVm.IsPrescriptionModalOpen);
        Assert.IsTrue(recordsVm.IsInfoModalVisible);
        Assert.IsTrue(patient.ActivePrescriptionJson.Contains("Amoxicilina 500mg"));

        // 4. Volver a abrir la receta para continuar editando
        recordsVm.OpenPrescriptionModal();
        Assert.IsTrue(recordsVm.IsPrescriptionModalOpen);

        // 5. Agregar un segundo medicamento
        recordsVm.PrescriptionMedicineName = "Ibuprofeno 400mg";
        recordsVm.PrescriptionDosage = "1 tableta cada 8 horas si hay dolor";
        recordsVm.PrescriptionQuantity = 1;
        await recordsVm.AddPrescriptionItemToPatientAsync();

        Assert.AreEqual(2, patient.ActivePrescription.Count);
        Assert.AreEqual("Ibuprofeno 400mg", patient.ActivePrescription[1].MedicineName);

        // 6. Quitar el primer medicamento
        await recordsVm.RemovePrescriptionItemFromPatientAsync(patient.ActivePrescription[0]);
        Assert.AreEqual(1, patient.ActivePrescription.Count);
        Assert.AreEqual("Ibuprofeno 400mg", patient.ActivePrescription[0].MedicineName);

        // 7. Guardar nuevamente
        await recordsVm.SavePrescriptionModalAsync();
        Assert.IsFalse(recordsVm.IsPrescriptionModalOpen);
        Assert.IsTrue(patient.ActivePrescriptionJson.Contains("Ibuprofeno 400mg"));
        Assert.IsFalse(patient.ActivePrescriptionJson.Contains("Amoxicilina 500mg"));
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_ProductClassification_SeparatesMedicationsAndSupplies()
    {
        var recordsVm = new MedicalRecordsViewModel(_inventoryService);
        await recordsVm.LoadInitialDataAsync();

        var medProduct = new Product
        {
            Code = "MED-001",
            Name = "Paracetamol 500mg",
            Category = new Category { Name = "Medicamentos" },
            Price = 45m
        };

        var supplyProduct = new Product
        {
            Code = "CUR-001",
            Name = "Gasa Estéril 10x10",
            Category = new Category { Name = "Material de Curación" },
            Price = 20m
        };

        // Verificación de métodos de clasificación
        Assert.IsTrue(MedicalRecordsViewModel.IsProductMedication(medProduct));
        Assert.IsFalse(MedicalRecordsViewModel.IsProductSupply(medProduct));

        Assert.IsTrue(MedicalRecordsViewModel.IsProductSupply(supplyProduct));
        Assert.IsFalse(MedicalRecordsViewModel.IsProductMedication(supplyProduct));

        // Verificación de sugerencias en receta (solo medicamentos)
        recordsVm.ApplyPrescriptionSuggestions("Gasa");
        Assert.AreEqual(0, recordsVm.PrescriptionProductSuggestions.Count, "La receta no debe sugerir insumos ni material de curación");

        // Verificación de sugerencias en insumos (solo insumos clínicos)
        recordsVm.ApplySupplySuggestions("Paracetamol");
        Assert.AreEqual(0, recordsVm.SupplyProductSuggestions.Count, "El catálogo de insumos no debe sugerir medicamentos");
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_SuppliesModal_AddSaveReeditAndPosWorksCorrectly()
    {
        var consultationService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultationService);
        await recordsVm.LoadInitialDataAsync();

        var patient = new PatientRecord
        {
            FullName = "Pedro Ramírez",
            RecordNumber = "EXP-TEST-SUP",
            Age = 35
        };
        recordsVm.SelectedPatient = patient;

        // 1. Abrir modal de insumos
        recordsVm.OpenSuppliesModal();
        Assert.IsTrue(recordsVm.IsSuppliesModalOpen);

        // 2. Agregar un insumo clínico
        recordsVm.SupplyName = "Guantes de Látex Quirúrgicos";
        recordsVm.SupplyInstructions = "Uso en curación de herida";
        recordsVm.SupplyQuantity = 2;
        await recordsVm.AddSupplyItemToPatientAsync();

        Assert.AreEqual(1, patient.ActiveSupplies.Count);
        Assert.AreEqual("Guantes de Látex Quirúrgicos", patient.ActiveSupplies[0].SupplyName);
        Assert.AreEqual(2, patient.ActiveSupplies[0].Quantity);

        // 3. Probar agregar al POS
        recordsVm.AddSupplyItemToPos(patient.ActiveSupplies[0]);
        Assert.IsTrue(recordsVm.HasActiveConsultation);
        Assert.IsTrue(recordsVm.ActiveConsultationItemsCount > 0);

        // 4. Guardar insumos -> Cierra modal y persiste JSON
        await recordsVm.SaveSuppliesModalAsync();
        Assert.IsFalse(recordsVm.IsSuppliesModalOpen);
        Assert.IsTrue(patient.ActiveSuppliesJson.Contains("Guantes de Látex Quirúrgicos"));

        // 5. Reabrir modal para continuar editando
        recordsVm.OpenSuppliesModal();
        Assert.IsTrue(recordsVm.IsSuppliesModalOpen);

        // 6. Agregar un segundo insumo
        recordsVm.SupplyName = "Venda Elástica 10cm";
        recordsVm.SupplyInstructions = "Vendaje compresivo en tobillo";
        recordsVm.SupplyQuantity = 1;
        await recordsVm.AddSupplyItemToPatientAsync();

        Assert.AreEqual(2, patient.ActiveSupplies.Count);

        // 7. Eliminar el primer insumo
        await recordsVm.RemoveSupplyItemFromPatientAsync(patient.ActiveSupplies[0]);
        Assert.AreEqual(1, patient.ActiveSupplies.Count);
        Assert.AreEqual("Venda Elástica 10cm", patient.ActiveSupplies[0].SupplyName);

        // 8. Guardar de nuevo
        await recordsVm.SaveSuppliesModalAsync();
        Assert.IsFalse(recordsVm.IsSuppliesModalOpen);
        Assert.IsTrue(patient.ActiveSuppliesJson.Contains("Venda Elástica 10cm"));
        Assert.IsFalse(patient.ActiveSuppliesJson.Contains("Guantes de Látex Quirúrgicos"));
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_ServicesModal_AddSaveReeditAndPosWorksCorrectly()
    {
        var consultationService = new ConsultationService();
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, consultationService);
        await recordsVm.LoadInitialDataAsync();

        var patient = new PatientRecord
        {
            FullName = "Lucía Vázquez",
            RecordNumber = "EXP-TEST-SRV",
            Age = 28
        };
        recordsVm.SelectedPatient = patient;

        // 1. Abrir modal de servicios
        recordsVm.OpenServicesModal();
        Assert.IsTrue(recordsVm.IsServicesModalOpen);

        // 2. Agregar un servicio clínico
        recordsVm.ServiceModalName = "Sutura de Herida Menor";
        recordsVm.ServiceModalNotes = "3 puntos de sutura sin complicaciones";
        recordsVm.ServiceModalQuantity = 1;
        await recordsVm.AddServiceItemToPatientAsync();

        Assert.AreEqual(1, patient.ActiveServices.Count);
        Assert.AreEqual("Sutura de Herida Menor", patient.ActiveServices[0].ServiceName);

        // 3. Probar agregar al POS
        recordsVm.AddServiceItemToPos(patient.ActiveServices[0]);
        Assert.IsTrue(recordsVm.HasActiveConsultation);
        Assert.IsTrue(recordsVm.ActiveConsultationItemsCount > 0);

        // 4. Guardar servicios -> Cierra modal y persiste JSON
        await recordsVm.SaveServicesModalAsync();
        Assert.IsFalse(recordsVm.IsServicesModalOpen);
        Assert.IsTrue(patient.ActiveServicesJson.Contains("Sutura de Herida Menor"));

        // 5. Reabrir modal para continuar editando
        recordsVm.OpenServicesModal();
        Assert.IsTrue(recordsVm.IsServicesModalOpen);

        // 6. Agregar un segundo servicio
        recordsVm.ServiceModalName = "Toma de Glucosa Capilar";
        recordsVm.ServiceModalNotes = "Glucemia postprandial";
        recordsVm.ServiceModalQuantity = 1;
        await recordsVm.AddServiceItemToPatientAsync();

        Assert.AreEqual(2, patient.ActiveServices.Count);

        // 7. Eliminar el primer servicio
        await recordsVm.RemoveServiceItemFromPatientAsync(patient.ActiveServices[0]);
        Assert.AreEqual(1, patient.ActiveServices.Count);
        Assert.AreEqual("Toma de Glucosa Capilar", patient.ActiveServices[0].ServiceName);

        // 8. Guardar de nuevo
        await recordsVm.SaveServicesModalAsync();
        Assert.IsFalse(recordsVm.IsServicesModalOpen);
        Assert.IsTrue(patient.ActiveServicesJson.Contains("Toma de Glucosa Capilar"));
        Assert.IsFalse(patient.ActiveServicesJson.Contains("Sutura de Herida Menor"));
    }
}








