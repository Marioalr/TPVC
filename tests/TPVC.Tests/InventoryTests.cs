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
        if (!OperatingSystem.IsWindows()) return;
        RunBoolToVisibilityConverterTest();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunBoolToVisibilityConverterTest()
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
        if (!OperatingSystem.IsWindows()) return;
        RunComparisonToVisibilityConverterTest();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunComparisonToVisibilityConverterTest()
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

        // Procesar la venta final genera el comprobante y sale de todo (sin dejar modal abierta)
        await posVm.ProcessSaleAsync();
        Assert.IsFalse(posVm.IsTicketModalOpen, "Al cobrar desde consulta médica, se sale de todo y no se bloquea en la modal de ticket");
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

        // Al dar en cerrar mientras se visualiza el expediente, debe cerrar la modal por completo
        recordsVm.CloseRecordsViewerModal();
        Assert.IsFalse(recordsVm.IsRecordsViewerModalOpen, "Cerrar debe cerrar la modal por completo sin volver a la lista general");
        Assert.IsNull(recordsVm.ViewingRecordPatient, "El paciente visualizado debe limpiarse");

        // Al usar explícitamente BackToRecordsListCommand debe regresar a la lista de expedientes
        recordsVm.OpenRecordsViewerModal();
        recordsVm.ViewPatientRecordDetail(targetPatient);
        recordsVm.BackToRecordsListCommand.Execute(null);
        Assert.IsTrue(recordsVm.IsRecordsViewerModalOpen, "BackToRecordsList debe mantener abierta la modal en la lista general");
        Assert.IsNull(recordsVm.ViewingRecordPatient, "El paciente visualizado debe ser nulo tras volver a la lista");
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

    [TestMethod]
    public async Task MedicalRecordsViewModel_ScheduleAppointmentModal_SaveAndCancelWorksCorrectly()
    {
        var recordsVm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());
        await recordsVm.LoadInitialDataAsync();

        var patient = new PatientRecord
        {
            FullName = "Roberto Gómez",
            RecordNumber = "EXP-TEST-APT",
            Age = 42
        };
        recordsVm.SelectedPatient = patient;

        // 1. Abrir modal de agendar consulta
        recordsVm.OpenScheduleAppointmentModal();
        Assert.IsTrue(recordsVm.IsScheduleAppointmentModalOpen);

        // 2. Configurar datos de la cita
        var targetDate = DateTime.Today.AddDays(10);
        recordsVm.AppointmentDate = targetDate;
        recordsVm.AppointmentTime = "11:30 AM";
        recordsVm.SetAppointmentReasonPreset("Revisión de Estudios de Laboratorio");
        recordsVm.AppointmentNotes = "Presentarse con 8 horas de ayuno";

        // 3. Guardar la cita
        await recordsVm.SaveScheduledAppointmentAsync();
        Assert.IsFalse(recordsVm.IsScheduleAppointmentModalOpen);
        Assert.IsTrue(recordsVm.IsInfoModalVisible);

        // 4. Validar persistencia en el paciente
        Assert.IsTrue(patient.HasScheduledAppointment);
        Assert.AreEqual(targetDate.Date, patient.NextAppointmentDate?.Date);
        Assert.AreEqual("11:30 AM", patient.NextAppointmentTime);
        Assert.AreEqual("Revisión de Estudios de Laboratorio", patient.NextAppointmentReason);
        Assert.AreEqual("Presentarse con 8 horas de ayuno", patient.NextAppointmentNotes);
        Assert.IsTrue(patient.FormattedNextAppointment.Contains("11:30 AM"));

        // 5. Reabrir modal y cancelar la cita
        recordsVm.OpenScheduleAppointmentModal();
        Assert.IsTrue(recordsVm.IsScheduleAppointmentModalOpen);

        await recordsVm.CancelScheduledAppointmentAsync();
        Assert.IsFalse(recordsVm.IsScheduleAppointmentModalOpen);
        Assert.IsFalse(patient.HasScheduledAppointment);
        Assert.IsNull(patient.NextAppointmentDate);
        Assert.AreEqual(string.Empty, patient.NextAppointmentTime);
    }

    [TestMethod]
    public async Task SavePatientAsync_PreservesAppointmentsSuppliesAndServices_AcrossDatabaseReloads()
    {
        var patient = new PatientRecord
        {
            FullName = "Carlos Santana",
            RecordNumber = "EXP-TEST-PERSIST",
            Age = 45,
            NextAppointmentDate = DateTime.Today.AddDays(5),
            NextAppointmentTime = "09:00 AM",
            NextAppointmentReason = "Retiro de puntos",
            NextAppointmentNotes = "Verificar cicatrización",
            ActiveSupplies = new List<PatientSupplyItem>
            {
                new PatientSupplyItem { SupplyName = "Gasas Estériles", Quantity = 4, UnitPrice = 15m }
            },
            ActiveServices = new List<PatientServiceItem>
            {
                new PatientServiceItem { ServiceName = "Curación Básica", Price = 150m }
            }
        };

        var saved = await _inventoryService.SavePatientAsync(patient);
        Assert.IsTrue(saved.Id > 0);

        var retrieved = await _inventoryService.GetPatientByIdAsync(saved.Id);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual(DateTime.Today.AddDays(5).Date, retrieved.NextAppointmentDate?.Date);
        Assert.AreEqual("09:00 AM", retrieved.NextAppointmentTime);
        Assert.AreEqual("Retiro de puntos", retrieved.NextAppointmentReason);
        Assert.AreEqual("Verificar cicatrización", retrieved.NextAppointmentNotes);
        Assert.HasCount(1, retrieved.ActiveSupplies);
        Assert.AreEqual("Gasas Estériles", retrieved.ActiveSupplies[0].SupplyName);
        Assert.AreEqual(4, retrieved.ActiveSupplies[0].Quantity);
        Assert.HasCount(1, retrieved.ActiveServices);
        Assert.AreEqual("Curación Básica", retrieved.ActiveServices[0].ServiceName);

        // Actualizar y verificar que la persistencia se actualiza
        retrieved.NextAppointmentTime = "10:30 AM";
        retrieved.ActiveSupplies.Add(new PatientSupplyItem { SupplyName = "Venda Elástica", Quantity = 2 });
        await _inventoryService.SavePatientAsync(retrieved);

        var retrievedAgain = await _inventoryService.GetPatientByIdAsync(saved.Id);
        Assert.IsNotNull(retrievedAgain);
        Assert.AreEqual("10:30 AM", retrievedAgain.NextAppointmentTime);
        Assert.HasCount(2, retrievedAgain.ActiveSupplies);
        Assert.IsTrue(retrievedAgain.ActiveSupplies.Any(s => s.SupplyName == "Venda Elástica"));
    }

    [TestMethod]
    public async Task RegisterQuickConsultationUsageAsync_MultiBatchFefo_DeductsAcrossMultipleBatchesCorrectly()
    {
        var category = (await _inventoryService.GetCategoriesAsync()).First();
        var product = new Product
        {
            Code = "PRD-FEFO-MULTI",
            Name = "Solución Antiséptica FEFO",
            CategoryId = category.Id,
            Price = 45m,
            Unit = "pza",
            CurrentStock = 15,
            IsActive = true
        };
        await _inventoryService.SaveProductAsync(product);

        var batch1 = new ProductBatch
        {
            ProductId = product.Id,
            BatchNumber = "LOT-FEFO-01",
            ExpirationDate = DateTime.Today.AddDays(5),
            Quantity = 5
        };
        var batch2 = new ProductBatch
        {
            ProductId = product.Id,
            BatchNumber = "LOT-FEFO-02",
            ExpirationDate = DateTime.Today.AddDays(25),
            Quantity = 10
        };
        await _inventoryService.SaveBatchAsync(batch1);
        await _inventoryService.SaveBatchAsync(batch2);

        // Descontar 8 unidades: debe agotar batch1 (5) y tomar 3 de batch2
        await _inventoryService.RegisterQuickConsultationUsageAsync(
            product.Id,
            8,
            "Desinfección quirúrgica de paciente",
            "Dr. Especialista"
        );

        var updatedProduct = await _inventoryService.GetProductByIdAsync(product.Id);
        Assert.IsNotNull(updatedProduct);
        Assert.AreEqual(7, updatedProduct.CurrentStock, "Stock restante debe ser 15 - 8 = 7");

        var batches = await _inventoryService.GetBatchesAsync(product.Id);
        var b1 = batches.FirstOrDefault(b => b.BatchNumber == "LOT-FEFO-01");
        var b2 = batches.FirstOrDefault(b => b.BatchNumber == "LOT-FEFO-02");

        Assert.IsNotNull(b1);
        Assert.AreEqual(0, b1.Quantity, "El lote más próximo a vencer debe agotarse primero (0)");
        Assert.IsNotNull(b2);
        Assert.AreEqual(7, b2.Quantity, "El segundo lote debe tener 10 - 3 = 7 unidades");

        var movements = await _inventoryService.GetRecentMovementsAsync(5);
        var mov = movements.FirstOrDefault(m => m.ProductId == product.Id);
        Assert.IsNotNull(mov);
        Assert.AreEqual(MovementType.SalidaConsulta, mov.MovementType);
        Assert.AreEqual(8, mov.Quantity);
    }

    [TestMethod]
    public async Task PosViewModel_AddPrescriptionItem_WithQuantityGreaterThanOne_AddsCorrectQuantityToCart()
    {
        var posVm = new PosViewModel(_inventoryService, new ConsultationService());
        await posVm.InitializeAsync();

        var med = posVm.Products.First(p => p.CurrentStock >= 10);
        posVm.AddPrescriptionItem(med.Name, 4);

        Assert.HasCount(1, posVm.Cart);
        Assert.AreEqual(med.Id, posVm.Cart[0].ProductId);
        Assert.AreEqual(4, posVm.Cart[0].Quantity);

        // Agregar 2 más de la misma receta
        posVm.AddPrescriptionItem(med.Name, 2);
        Assert.HasCount(1, posVm.Cart);
        Assert.AreEqual(6, posVm.Cart[0].Quantity);
    }

    [TestMethod]
    public void MainViewModel_CommandPalette_OpensAndClosesAndFiltersCorrectly()
    {
        var mainVm = new MainViewModel(_inventoryService, new ConsultationService(), new ThemeService(), new AppUpdateService());
        
        Assert.IsFalse(mainVm.IsCommandPaletteOpen);
        Assert.IsTrue(mainVm.PaletteResults.Count > 0, "Default navigation commands should be pre-populated");

        mainVm.OpenCommandPalette();
        Assert.IsTrue(mainVm.IsCommandPaletteOpen);
        Assert.AreEqual(string.Empty, mainVm.PaletteSearchText);

        // Filtrar comando de navegación
        mainVm.PaletteSearchText = "POS";
        Assert.IsTrue(mainVm.PaletteResults.Any(r => r.Title.Contains("POS")));

        // Ciclar con flechas arriba y abajo
        var initialItem = mainVm.SelectedPaletteItem;
        mainVm.MovePaletteDown();
        Assert.AreNotEqual(initialItem, mainVm.SelectedPaletteItem);
        mainVm.MovePaletteUp();
        Assert.AreEqual(initialItem, mainVm.SelectedPaletteItem);

        // Ejecutar acción
        mainVm.SelectedPaletteItem = mainVm.PaletteResults.First(r => r.Title.Contains("POS"));
        mainVm.ExecuteSelectedPaletteItem();
        Assert.IsFalse(mainVm.IsCommandPaletteOpen);
        Assert.AreEqual("Pos", mainVm.ActiveViewName);
    }

    [TestMethod]
    public async Task MainViewModel_CommandPalette_SearchesPatientsAndNavigates()
    {
        var patient = new PatientRecord
        {
            FullName = "Guillermo Del Toro",
            RecordNumber = "EXP-PALETTE-01",
            Age = 58,
            Allergies = "Penicilina"
        };
        await _inventoryService.SavePatientAsync(patient);

        var mainVm = new MainViewModel(_inventoryService, new ConsultationService(), new ThemeService(), new AppUpdateService());
        await mainVm.MedicalRecordsVM.LoadPatientsAsync();

        mainVm.OpenCommandPalette();
        mainVm.PaletteSearchText = "Guillermo";

        var patientMatch = mainVm.PaletteResults.FirstOrDefault(r => r.Category == "Pacientes" && r.Title.Contains("Guillermo"));
        Assert.IsNotNull(patientMatch, "Debe encontrar el paciente por nombre en la Paleta de Comandos");
        Assert.AreEqual("⚠️ Alergias", patientMatch.Badge);

        mainVm.SelectPaletteItem(patientMatch);
        Assert.IsFalse(mainVm.IsCommandPaletteOpen);
        Assert.AreEqual("Records", mainVm.ActiveViewName);
        Assert.AreEqual(patient.Id, mainVm.MedicalRecordsVM.SelectedPatient?.Id);
    }

    [TestMethod]
    public async Task MedicalRecords_FinalizeConsultation_ProcessSale_ExitsEverythingAndOpensSelectPatientModal()
    {
        var consultService = new ConsultationService();
        var mainVm = new MainViewModel(_inventoryService, consultService, new ThemeService(), new AppUpdateService());
        await mainVm.MedicalRecordsVM.LoadPatientsAsync();

        var patient = mainVm.MedicalRecordsVM.Patients.First();
        mainVm.MedicalRecordsVM.SelectPatient(patient);
        Assert.AreEqual(patient.Id, mainVm.MedicalRecordsVM.SelectedPatient?.Id);

        // Iniciar consulta activa
        mainVm.MedicalRecordsVM.StartActiveConsultation();
        Assert.IsTrue(consultService.HasActiveConsultation);
        Assert.IsTrue(mainVm.MedicalRecordsVM.HasActiveConsultation);

        // Finalizar consulta: abre el modal del POS
        await mainVm.MedicalRecordsVM.FinalizeConsultationAsync();
        Assert.IsTrue(mainVm.MedicalRecordsVM.IsPosModalOpen, "Debe abrirse la ventana modal del POS");
        Assert.IsNotNull(mainVm.MedicalRecordsVM.PosVM);
        Assert.IsTrue(mainVm.MedicalRecordsVM.PosVM.Cart.Count > 0, "Debe incluir al menos la Consulta Médica General");

        // Al cobrar en el POS:
        await mainVm.MedicalRecordsVM.PosVM.ProcessSaleAsync();

        // 1. Debe salir de todo:
        Assert.IsFalse(mainVm.MedicalRecordsVM.IsPosModalOpen, "La ventana modal del POS debe cerrarse");
        Assert.IsFalse(mainVm.MedicalRecordsVM.PosVM.IsTicketModalOpen, "El modal de ticket debe cerrarse al cobrar desde consulta");
        Assert.IsFalse(consultService.HasActiveConsultation, "La consulta activa debe cerrarse");
        Assert.IsFalse(mainVm.MedicalRecordsVM.HasActiveConsultation, "No debe haber consulta activa en curso");
        Assert.IsNull(mainVm.MedicalRecordsVM.SelectedPatient, "El paciente debe deseleccionarse para volver a la vista inicial");

        // 2. Debe abrir inmediatamente la ventana modal de agregar paciente:
        Assert.IsTrue(mainVm.MedicalRecordsVM.IsSelectPatientModalOpen, "Debe abrirse inmediatamente el modal de agregar paciente");
    }

    [TestMethod]
    public async Task MedicalRecords_FinalizeConsultation_SavesConsultationWithStartedAtAndEndedAtInDatabase()
    {
        var consultService = new ConsultationService();
        var mainVm = new MainViewModel(_inventoryService, consultService, new ThemeService(), new AppUpdateService());
        await mainVm.MedicalRecordsVM.LoadPatientsAsync();

        var patient = mainVm.MedicalRecordsVM.Patients.First();
        mainVm.MedicalRecordsVM.SelectPatient(patient);

        var startTime = DateTime.Now.AddMinutes(-20);
        mainVm.MedicalRecordsVM.StartActiveConsultation();
        Assert.IsTrue(consultService.HasActiveConsultation);
        Assert.IsNotNull(consultService.CurrentConsultation);

        // Ajustar hora de inicio simulada
        consultService.CurrentConsultation.StartedAt = startTime;
        mainVm.MedicalRecordsVM.ConsultationChiefComplaint = "Cefalea tensional recurrente";
        mainVm.MedicalRecordsVM.ConsultationDiagnosis = "Cefalea tensional episódica (G44.2)";
        mainVm.MedicalRecordsVM.ConsultationTreatmentPlan = "Reposo, hidratación y analgésico según necesidad";

        // Finalizar consulta: debe guardar la consulta en base de datos con StartedAt y EndedAt
        await mainVm.MedicalRecordsVM.FinalizeConsultationAsync();

        // Verificar en base de datos
        var dbPatients = await _inventoryService.GetPatientsAsync();
        var updatedPatient = dbPatients.First(p => p.Id == patient.Id);

        var savedConsultation = updatedPatient.Consultations.FirstOrDefault(c => c.ChiefComplaint == "Cefalea tensional recurrente");
        Assert.IsNotNull(savedConsultation, "La consulta debe haberse guardado en la base de datos");
        Assert.IsNotNull(savedConsultation.StartedAt, "StartedAt debe estar registrado en BD");
        Assert.IsNotNull(savedConsultation.EndedAt, "EndedAt debe estar registrado en BD");
        Assert.AreEqual(startTime.Minute, savedConsultation.StartedAt.Value.Minute);
        Assert.IsTrue(savedConsultation.EndedAt.Value >= savedConsultation.StartedAt.Value);
        Assert.IsTrue(savedConsultation.Duration.HasValue);
        Assert.IsTrue(savedConsultation.Duration.Value.TotalMinutes >= 19.9);
        Assert.IsFalse(string.IsNullOrWhiteSpace(savedConsultation.FormattedScheduleTime));
    }

    [TestMethod]
    public void PatientConsultation_DurationAndFormattedScheduleTime_CalculatesCorrectly()
    {
        var start = new DateTime(2026, 10, 4, 10, 0, 0);
        var end = new DateTime(2026, 10, 4, 10, 25, 30);

        var consult = new PatientConsultation
        {
            Folio = "CNS-20261004-001",
            Date = start.Date,
            StartedAt = start,
            EndedAt = end,
            ChiefComplaint = "Control general"
        };

        Assert.IsNotNull(consult.Duration);
        Assert.AreEqual(25, (int)consult.Duration.Value.TotalMinutes);
        Assert.AreEqual(30, consult.Duration.Value.Seconds);
        Assert.IsTrue(consult.FormattedDuration.Contains("25 min 30 s"));
        Assert.IsTrue(consult.FormattedScheduleTime.Contains("10:00 - 10:25"));
    }

    [TestMethod]
    public void MainViewModel_NavigateToRecords_WithoutPatient_OpensSelectPatientModalImmediately()
    {
        var mainVm = new MainViewModel(_inventoryService, new ConsultationService(), new ThemeService(), new AppUpdateService());
        Assert.IsNull(mainVm.MedicalRecordsVM.SelectedPatient);

        mainVm.NavigateTo("Records");

        Assert.AreEqual("Records", mainVm.ActiveViewName);
        Assert.IsTrue(mainVm.MedicalRecordsVM.IsSelectPatientModalOpen, "Debe abrir inmediatamente el modal de agregar paciente al entrar a Records");
    }

    [TestMethod]
    public async Task SavePatientAsync_PersistsNom004Fields_IncludingAGO_Pediatric_And_APNP()
    {
        var patient = new PatientRecord
        {
            RecordNumber = "EXP-NOM004-TEST",
            FullName = "María Elena Rosas García",
            Age = 28,
            Gender = "Femenino",
            Phone = "5551234567",
            Email = "maria.rosas@example.com",
            Address = "Av. Insurgentes Sur 123, CDMX",
            BloodType = "O+",
            EmergencyContactName = "Juan Rosas",
            EmergencyContactPhone = "5557654321",
            Occupation = "Ingeniera de Software",
            Religion = "Católica",
            FamilyHistory = "Madre con DM2 controlada, Padre con HTA, Abuela materna con CA de mama",
            Surgeries = "Apendicectomía laparoscópica en 2018",
            Transfusions = "Negadas",
            Hospitalizations = "1 hospitalización por apendicitis (2018)",
            Allergies = "Penicilina",
            ImmunizationSchedule = "Esquema completo",
            MenarcheAge = "12 años",
            SexualOnsetAge = "19 años",
            SexualPartnersCount = "2",
            PeriodDuration = "4 días cada 28 días",
            IsPeriodHeavy = "No",
            PapSmear = "Negativo realizado hace 8 meses",
            PregnanciesCount = 1,
            CesareansCount = 1,
            AbortionsCount = 0,
            IsCurrentlyPregnant = true,
            GestationalWeeks = "14 semanas por FUM",
            DevelopmentalMilestones = "Desarrollo psicomotor normal sin antecedentes perinatales de importancia",
            Diet = "Buena en calidad y cantidad, 3 comidas al día balanceadas",
            HousingConditions = "Casa propia, cuenta con todos los servicios intradomiciliarios",
            Zoonosis = "2 gatos vacunados y desparasitados",
            ClinicalNotes = "Paciente en control prenatal regular"
        };

        await _inventoryService.SavePatientAsync(patient);

        var patients = await _inventoryService.GetPatientsAsync();
        var retrieved = patients.FirstOrDefault(p => p.RecordNumber == "EXP-NOM004-TEST");

        Assert.IsNotNull(retrieved, "El paciente debe guardarse y recuperarse correctamente");
        Assert.AreEqual("Ingeniera de Software", retrieved.Occupation);
        Assert.AreEqual("Católica", retrieved.Religion);
        Assert.IsTrue(retrieved.FamilyHistory.Contains("DM2"));
        Assert.IsTrue(retrieved.Surgeries.Contains("Apendicectomía"));
        Assert.AreEqual("Negadas", retrieved.Transfusions);
        Assert.AreEqual("Penicilina", retrieved.Allergies);
        Assert.AreEqual("12 años", retrieved.MenarcheAge);
        Assert.AreEqual("19 años", retrieved.SexualOnsetAge);
        Assert.AreEqual("2", retrieved.SexualPartnersCount);
        Assert.AreEqual("4 días cada 28 días", retrieved.PeriodDuration);
        Assert.AreEqual("No", retrieved.IsPeriodHeavy);
        Assert.IsTrue(retrieved.PapSmear.Contains("Negativo"));
        Assert.AreEqual(1, retrieved.PregnanciesCount);
        Assert.AreEqual(1, retrieved.CesareansCount);
        Assert.AreEqual(0, retrieved.AbortionsCount);
        Assert.IsTrue(retrieved.IsCurrentlyPregnant);
        Assert.AreEqual("14 semanas por FUM", retrieved.GestationalWeeks);
        Assert.IsTrue(retrieved.DevelopmentalMilestones.Contains("normal"));
        Assert.IsTrue(retrieved.Diet.Contains("3 comidas"));
        Assert.IsTrue(retrieved.HousingConditions.Contains("servicios"));
        Assert.IsTrue(retrieved.Zoonosis.Contains("gatos"));
        Assert.IsTrue(retrieved.IsFemale);
        Assert.IsFalse(retrieved.IsPediatric);
    }

    [TestMethod]
    public async Task AddConsultationNoteAsync_PersistsSystemsReview_And_AllClinicalFields()
    {
        var patient = new PatientRecord
        {
            RecordNumber = "EXP-SR-TEST",
            FullName = "Carlos Santana López",
            Age = 45,
            Gender = "Masculino"
        };
        await _inventoryService.SavePatientAsync(patient);

        var consultation = new PatientConsultation
        {
            Folio = "CNS-20261005-001",
            Date = DateTime.Today,
            ChiefComplaint = "Tos seca y dolor torácico leve",
            SystemsReview = "Aparato respiratorio: Tos seca no productiva, sin sibilancias. Cardiovascular: Sin palpitaciones. Digestivo: Asintomático.",
            PhysicalExamination = "Faringe ligeramente hiperémica, campos pulmonares bien ventilados sin ruidos agregados.",
            Diagnosis = "Faringitis aguda viral (J02.9)",
            TreatmentPlan = "Paracetamol 500mg c/8h por 3 días, abundantes líquidos y reposo.",
            VitalSigns = "TA: 120/80 mmHg, FC: 74 lpm, FR: 18 rpm, Temp: 36.6 °C, Glucosa: 95 mg/dL, Peso: 75 kg, Talla: 172 cm, IMC: 25.35 kg/m²"
        };

        await _inventoryService.AddConsultationNoteAsync(patient.Id, consultation);

        var patients = await _inventoryService.GetPatientsAsync();
        var retrieved = patients.First(p => p.Id == patient.Id);
        var savedConsultation = retrieved.Consultations.FirstOrDefault(c => c.Folio == "CNS-20261005-001");

        Assert.IsNotNull(savedConsultation, "La consulta debe encontrarse en la lista de consultas del paciente");
        Assert.AreEqual("Tos seca y dolor torácico leve", savedConsultation.ChiefComplaint);
        Assert.IsTrue(savedConsultation.SystemsReview.Contains("Aparato respiratorio: Tos seca"));
        Assert.IsTrue(savedConsultation.PhysicalExamination.Contains("Faringe"));
        Assert.IsTrue(savedConsultation.Diagnosis.Contains("Faringitis"));
        Assert.IsTrue(savedConsultation.TreatmentPlan.Contains("Paracetamol"));
        Assert.IsTrue(savedConsultation.VitalSigns.Contains("120/80"));
    }

    [TestMethod]
    public void MedicalRecordsViewModel_ReactiveAgeAndGender_UpdatesPediatricAndFemaleFlags()
    {
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());

        // Test Gender reactivity
        vm.FormGender = "Femenino";
        Assert.IsTrue(vm.FormIsFemale, "Al seleccionar Femenino, FormIsFemale debe ser true");

        vm.FormGender = "Masculino";
        Assert.IsFalse(vm.FormIsFemale, "Al seleccionar Masculino, FormIsFemale debe ser false");

        // Test Age reactivity
        vm.FormAge = 9;
        Assert.IsTrue(vm.FormIsPediatric, "A los 9 años debe considerarse pediátrico (< 18 años)");

        vm.FormAge = 17;
        Assert.IsTrue(vm.FormIsPediatric, "A los 17 años debe considerarse pediátrico (< 18 años)");

        vm.FormAge = 18;
        Assert.IsFalse(vm.FormIsPediatric, "A los 18 años ya no se considera pediátrico");

        vm.FormAge = 45;
        Assert.IsFalse(vm.FormIsPediatric, "A los 45 años no es pediátrico");
    }

    [TestMethod]
    public void MedicalRecordsViewModel_StepNavigationCommands_CycleTabsCorrectly()
    {
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());

        Assert.AreEqual("Identificacion", vm.FormPatientActiveTab);

        // Next -> Patologicos
        vm.NextFormPatientStepCommand.Execute(null);
        Assert.AreEqual("Patologicos", vm.FormPatientActiveTab);

        // Next -> GinecoHitos
        vm.NextFormPatientStepCommand.Execute(null);
        Assert.AreEqual("GinecoHitos", vm.FormPatientActiveTab);

        // Next -> NoPatologicos
        vm.NextFormPatientStepCommand.Execute(null);
        Assert.AreEqual("NoPatologicos", vm.FormPatientActiveTab);

        // Next at end stays at NoPatologicos
        vm.NextFormPatientStepCommand.Execute(null);
        Assert.AreEqual("NoPatologicos", vm.FormPatientActiveTab);

        // Previous -> GinecoHitos
        vm.PreviousFormPatientStepCommand.Execute(null);
        Assert.AreEqual("GinecoHitos", vm.FormPatientActiveTab);

        // Previous -> Patologicos
        vm.PreviousFormPatientStepCommand.Execute(null);
        Assert.AreEqual("Patologicos", vm.FormPatientActiveTab);

        // Direct tab set
        vm.SetFormPatientStepTabCommand.Execute("NoPatologicos");
        Assert.AreEqual("NoPatologicos", vm.FormPatientActiveTab);
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_SaveActiveConsultationNoteCommand_PersistsDirectlyWithoutOpeningPOS()
    {
        var consultService = new ConsultationService();
        var mainVm = new MainViewModel(_inventoryService, consultService, new ThemeService(), new AppUpdateService());
        await mainVm.MedicalRecordsVM.LoadPatientsAsync();

        var patient = mainVm.MedicalRecordsVM.Patients.First();
        mainVm.MedicalRecordsVM.SelectPatient(patient);

        mainVm.MedicalRecordsVM.StartActiveConsultation();
        Assert.IsTrue(mainVm.MedicalRecordsVM.HasActiveConsultation);

        mainVm.MedicalRecordsVM.ConsultationChiefComplaint = "Cefalea tensional recurrente";
        mainVm.MedicalRecordsVM.ConsultationSystemsReview = "Neurológico: Sin focalización ni alteraciones de pares craneales. Oftalmológico: Fondo de ojo sin alteraciones.";
        mainVm.MedicalRecordsVM.ConsultationPhysicalExam = "Músculos trapecios y cervicales posteriores con contractura dolorosa bilateral.";
        mainVm.MedicalRecordsVM.ConsultationDiagnosis = "Cefalea tensional episódica (G44.2)";
        mainVm.MedicalRecordsVM.ConsultationTreatmentPlan = "Ibuprofeno 400mg c/8h por 3 días + calor local y ejercicios de relajación";

        // Guardar nota directa (sin cobrar en POS)
        await mainVm.MedicalRecordsVM.SaveActiveConsultationNoteCommand.ExecuteAsync(null);

        // La consulta activa se mantiene abierta para seguir trabajando, persistiendo los datos sin abrir el POS
        Assert.IsTrue(mainVm.MedicalRecordsVM.HasActiveConsultation, "La consulta activa debe permanecer abierta tras guardar la nota");
        Assert.IsFalse(mainVm.MedicalRecordsVM.IsPosModalOpen, "No debe abrirse el POS cuando solo se guarda la nota médica");

        // Verificar en base de datos
        var patients = await _inventoryService.GetPatientsAsync();
        var updatedPatient = patients.First(p => p.Id == patient.Id);
        var savedNote = updatedPatient.Consultations.FirstOrDefault(c => c.ChiefComplaint == "Cefalea tensional recurrente");

        Assert.IsNotNull(savedNote, "La nota médica debe estar registrada en el expediente del paciente");
        Assert.IsTrue(savedNote.SystemsReview.Contains("Neurológico: Sin focalización"));
        Assert.IsTrue(savedNote.PhysicalExamination.Contains("contractura"));
        Assert.IsTrue(savedNote.Diagnosis.Contains("G44.2"));
        Assert.IsTrue(savedNote.TreatmentPlan.Contains("Ibuprofeno"));
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_SaveActiveConsultationNote_DoesNotEndConsultation_AllowsIncrementalEditsWithoutDuplication()
    {
        var consultationService = new ConsultationService();
        var mainVm = new MainViewModel(_inventoryService, consultationService, new ThemeService(), new AppUpdateService());
        await mainVm.MedicalRecordsVM.LoadPatientsAsync();
        var patient = mainVm.MedicalRecordsVM.Patients.First();
        mainVm.MedicalRecordsVM.SelectPatient(patient);

        mainVm.MedicalRecordsVM.StartActiveConsultation();
        Assert.IsTrue(mainVm.MedicalRecordsVM.HasActiveConsultation);

        mainVm.MedicalRecordsVM.ConsultationChiefComplaint = "Dolor lumbar agudo";
        mainVm.MedicalRecordsVM.ConsultationDiagnosis = "Lumbalgia mecánica (M54.5)";
        mainVm.MedicalRecordsVM.ConsultationTreatmentPlan = "Reposo relativo por 48 horas";

        // Primer guardado de nota
        await mainVm.MedicalRecordsVM.SaveActiveConsultationNoteCommand.ExecuteAsync(null);

        // La consulta debe permanecer activa y con los campos intactos para continuar trabajando
        Assert.IsTrue(mainVm.MedicalRecordsVM.HasActiveConsultation, "La consulta debe mantenerse activa");
        Assert.AreEqual("Dolor lumbar agudo", mainVm.MedicalRecordsVM.ConsultationChiefComplaint);
        Assert.AreEqual("Lumbalgia mecánica (M54.5)", mainVm.MedicalRecordsVM.ConsultationDiagnosis);

        // Modificar plan y agregar indicaciones adicionales
        mainVm.MedicalRecordsVM.ConsultationTreatmentPlan += " + Paracetamol 500mg cada 8 horas";

        // Segundo guardado de nota (mismo folio)
        await mainVm.MedicalRecordsVM.SaveActiveConsultationNoteCommand.ExecuteAsync(null);

        Assert.IsTrue(mainVm.MedicalRecordsVM.HasActiveConsultation, "La consulta continúa activa tras el segundo guardado");

        // Verificar en BD que se actualizó en lugar de duplicarse
        var patients = await _inventoryService.GetPatientsAsync();
        var updated = patients.First(p => p.Id == patient.Id);
        var lumbarNotes = updated.Consultations.Where(c => c.ChiefComplaint == "Dolor lumbar agudo").ToList();

        Assert.AreEqual(1, lumbarNotes.Count, "No debe duplicarse la nota con el mismo folio al guardar avance");
        Assert.IsTrue(lumbarNotes[0].TreatmentPlan.Contains("Paracetamol 500mg"), "El plan debe reflejar la actualización guardada");
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_SelectAndAddDirectly_AddsItemsAndResetsFormProperly()
    {
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());
        await vm.LoadPatientsAsync();
        var patient = vm.Patients.First();
        patient.Allergies = "Ninguna";
        patient.ActivePrescription = new List<PatientPrescriptionItem>();
        patient.ActiveSupplies = new List<PatientSupplyItem>();
        patient.ActiveServices = new List<PatientServiceItem>();
        vm.SelectPatient(patient);

        // 1. Receta médica: seleccionar producto y agregar
        vm.OpenPrescriptionModal();
        Assert.IsTrue(vm.IsPrescriptionModalOpen);
        Assert.IsNotNull(vm.SelectedPrescriptionProduct);
        var chosenMedicine = vm.SelectedPrescriptionProduct;
        vm.PrescriptionDosage = "1 cada 12 horas por 3 días";
        vm.PrescriptionQuantity = 1;

        await vm.SelectAndAddPrescriptionProductAsync(chosenMedicine);

        Assert.AreEqual(1, patient.ActivePrescription.Count);
        Assert.IsTrue(patient.ActivePrescription[0].MedicineName.Contains(chosenMedicine.Name));
        Assert.AreEqual(string.Empty, vm.PrescriptionSearchText);
        Assert.AreEqual(string.Empty, vm.PrescriptionDosage);

        // 2. Insumos: seleccionar producto y agregar
        vm.OpenSuppliesModal();
        Assert.IsTrue(vm.IsSuppliesModalOpen);
        Assert.IsNotNull(vm.SelectedSupplyProduct);
        var chosenSupply = vm.SelectedSupplyProduct;
        vm.SupplyInstructions = "Uso durante curación";
        vm.SupplyQuantity = 3;

        await vm.SelectAndAddSupplyProductAsync(chosenSupply);

        Assert.AreEqual(1, patient.ActiveSupplies.Count);
        Assert.IsTrue(patient.ActiveSupplies[0].SupplyName.Contains(chosenSupply.Name));
        Assert.AreEqual(3, patient.ActiveSupplies[0].Quantity);
        Assert.AreEqual(string.Empty, vm.SupplySearchText);

        // 3. Servicios: seleccionar servicio y agregar
        vm.OpenServicesModal();
        Assert.IsTrue(vm.IsServicesModalOpen);
        Assert.IsNotNull(vm.SelectedServiceModalItem);
        var chosenService = vm.SelectedServiceModalItem;
        vm.ServiceModalNotes = "Aplicación inmediata";
        vm.ServiceModalQuantity = 1;

        await vm.SelectAndAddServiceModalSuggestionAsync(chosenService);

        Assert.AreEqual(1, patient.ActiveServices.Count);
        Assert.AreEqual(chosenService.Name, patient.ActiveServices[0].ServiceName);
        Assert.AreEqual(string.Empty, vm.ServiceModalSearchText);
    }

    [TestMethod]
    public void MedicalRecordsViewModel_ViewSelectedPatientRecordCommand_OpensModalForSelectedPatientWithoutAffectingActiveConsultation()
    {
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());

        // Si no hay paciente seleccionado, no debe abrir el modal
        vm.SelectedPatient = null;
        vm.ViewSelectedPatientRecordCommand.Execute(null);
        Assert.IsFalse(vm.IsRecordsViewerModalOpen, "No debe abrir el modal de expediente si no hay paciente seleccionado");

        // Con paciente seleccionado y consulta activa en curso
        var patient = new PatientRecord
        {
            Id = 999,
            FullName = "Paciente Prueba Expediente",
            Age = 35,
            Gender = "Femenino"
        };
        vm.SelectedPatient = patient;

        // Iniciar consulta activa y cargar datos en progreso
        vm.StartActiveConsultationCommand.Execute(null);
        Assert.IsTrue(vm.HasActiveConsultation);
        vm.ConsultationDiagnosis = "Cefalea en estudio";
        vm.ConsultationChiefComplaint = "Dolor pulsátil";

        // Ejecutar comando Ver Expediente
        vm.ViewSelectedPatientRecordCommand.Execute(null);

        // Validaciones: Modal abierto y paciente asignado correctamente
        Assert.IsTrue(vm.IsRecordsViewerModalOpen, "Debe abrir el modal de expediente");
        Assert.AreSame(patient, vm.ViewingRecordPatient, "El paciente en el visor debe coincidir con SelectedPatient");
        Assert.AreEqual("Resumen", vm.ViewingRecordTab, "La pestaña inicial debe ser Resumen");

        // Comprobar que no se alteró la consulta activa en curso
        Assert.IsTrue(vm.HasActiveConsultation, "La consulta activa debe permanecer activa");
        Assert.AreEqual("Cefalea en estudio", vm.ConsultationDiagnosis, "El diagnóstico en curso no debe verse afectado");
        Assert.AreEqual("Dolor pulsátil", vm.ConsultationChiefComplaint, "El motivo de consulta en curso no debe verse afectado");

        // Cerrar visor: clic en cerrar o tecla Esc sale por completo
        vm.CloseRecordsViewerModalCommand.Execute(null);
        Assert.IsFalse(vm.IsRecordsViewerModalOpen, "El modal de expediente debe cerrarse completamente");
        Assert.IsNull(vm.ViewingRecordPatient, "El paciente visualizado debe ser nulo");
        Assert.IsTrue(vm.HasActiveConsultation, "La consulta activa sigue en pie después de cerrar el visor");
    }

    [TestMethod]
    public async Task InventoryService_CancelSale_RestoresStockAndCreatesEntradaDevolucionMovement()
    {
        var product = (await _inventoryService.GetProductsAsync()).First(p => p.CurrentStock >= 5);
        int initialStock = product.CurrentStock;

        // Registrar una venta
        var saleLines = new List<SaleLineRequest>
        {
            new(product.Id, 2, product.Price, product.Code, product.Name)
        };
        var sale = await _inventoryService.RegisterSaleAsync(saleLines, "Paciente Prueba Cancelación", "Efectivo", "Dr. Titular");
        Assert.IsNotNull(sale);
        Assert.IsFalse(sale.IsCancelled);

        // Verificar que el stock disminuyó
        var productAfterSale = await _inventoryService.GetProductByIdAsync(product.Id);
        Assert.IsNotNull(productAfterSale);
        Assert.AreEqual(initialStock - 2, productAfterSale.CurrentStock);

        // Cancelar el ticket
        bool ok = await _inventoryService.CancelSaleAsync(sale.Id, "Paciente solicitó devolución de medicamento");
        Assert.IsTrue(ok);

        // Comprobar que el ticket quedó cancelado
        var cancelledSale = await _inventoryService.GetSaleByFolioAsync(sale.Folio);
        Assert.IsNotNull(cancelledSale);
        Assert.IsTrue(cancelledSale.IsCancelled);
        Assert.AreEqual("Paciente solicitó devolución de medicamento", cancelledSale.CancellationReason);

        // Comprobar que el stock volvió al valor original
        var productAfterCancel = await _inventoryService.GetProductByIdAsync(product.Id);
        Assert.IsNotNull(productAfterCancel);
        Assert.AreEqual(initialStock, productAfterCancel.CurrentStock);

        // Comprobar que se registró movimiento de Kardex EntradaDevolucion
        var movements = await _inventoryService.GetRecentMovementsAsync(10);
        var movement = movements
            .Where(m => m.ProductId == product.Id && m.MovementType == MovementType.EntradaDevolucion)
            .OrderByDescending(m => m.Date)
            .FirstOrDefault();

        Assert.IsNotNull(movement);
        Assert.AreEqual(2, movement.Quantity);
        Assert.IsTrue(movement.Reason.Contains(sale.Folio));
    }

    [TestMethod]
    public async Task InventoryService_GetDailyCashCut_CalculatesBreakdownAccurately()
    {
        var product = (await _inventoryService.GetProductsAsync()).First(p => p.CurrentStock >= 5);
        var services = await _inventoryService.GetClinicalServicesAsync();
        var service = services.First();

        // 1. Venta en efectivo con producto
        await _inventoryService.RegisterSaleAsync(new List<SaleLineRequest>
        {
            new(product.Id, 1, 100m, product.Code, product.Name)
        }, "Paciente Efectivo", "Efectivo");

        // 2. Venta con tarjeta de débito con servicio
        await _inventoryService.RegisterSaleAsync(new List<SaleLineRequest>
        {
            new(-service.Id, 1, 300m, service.Code, service.Name)
        }, "Paciente Tarjeta", "Tarjeta de Débito");

        // 3. Venta con transferencia que luego se cancelará
        var saleToCancel = await _inventoryService.RegisterSaleAsync(new List<SaleLineRequest>
        {
            new(product.Id, 1, 50m, product.Code, product.Name)
        }, "Paciente Cancelar", "Transferencia SPEI");
        Assert.IsNotNull(saleToCancel);
        await _inventoryService.CancelSaleAsync(saleToCancel.Id, "Error en cobro");

        // Calcular corte del día
        var profile = new ClinicProfile
        {
            DoctorName = "Dr. Test Especialista",
            ClinicName = "Clínica Test"
        };
        var cut = await _inventoryService.GetDailyCashCutAsync(DateTime.Today, profile);

        Assert.IsNotNull(cut);
        Assert.AreEqual(100m, cut.CashRevenue);
        Assert.AreEqual(300m, cut.DebitCardRevenue);
        Assert.AreEqual(0m, cut.TransferRevenue); // Cancelada no suma a netos
        Assert.AreEqual(400m, cut.TotalRevenue);
        Assert.AreEqual(50m, cut.CancelledRevenue);
        Assert.AreEqual(1, cut.CancelledTicketsCount);
        Assert.AreEqual(100m, cut.ProductsRevenue);
        Assert.AreEqual(300m, cut.ServicesRevenue);

        // Exportar a CSV
        string csv = _inventoryService.ExportDailyCashCutToCsv(cut, profile);
        Assert.IsNotNull(csv);
        Assert.IsTrue(csv.Contains("CORTE DE CAJA DIARIO,Clínica Test"));
        Assert.IsTrue(csv.Contains("Dr. Test Especialista"));
        Assert.IsTrue(csv.Contains("Total Ventas Netas,$400.00"));
        Assert.IsFalse(csv.StartsWith("\uFEFF", StringComparison.Ordinal), "El BOM lo agrega Encoding.UTF8 al guardar (no duplicarlo)");
    }

    [TestMethod]
    public void ClinicProfileViewModel_EditSaveToggle_BehavesCorrectly()
    {
        string tempProfilePath = Path.Combine(Path.GetTempPath(), $"tpvc_profile_test_{Guid.NewGuid():N}.json");
        try
        {
            var profileService = new ClinicProfileService(tempProfilePath);
            var vm = new ClinicProfileViewModel(profileService);

            // Al inicio, como no hay datos cargados, debe estar en modo edición con botón Guardar
            Assert.IsTrue(vm.IsEditing, "Los campos deben estar desbloqueados para primer ingreso");
            Assert.AreEqual("Guardar", vm.ActionButtonText, "El botón inicial debe decir Guardar");

            // Configurar datos y guardar
            vm.DoctorName = "Dr. Alejandro Gómez";
            vm.ProfessionalLicense = "CED-998877";
            vm.Specialty = "Medicina Familiar";
            vm.ClinicName = "Centro Médico Integral";
            vm.Address = "Av. Siempre Viva 123";
            vm.Phone = "555-123-4567";

            // Ejecutar Guardar
            vm.ToggleEditOrSaveCommand.Execute(null);

            // Después de guardar, los campos se bloquean y el botón cambia a "Editar"
            Assert.IsFalse(vm.IsEditing, "Los campos deben quedar bloqueados después de guardar");
            Assert.AreEqual("Editar", vm.ActionButtonText, "El botón debe decir Editar");
            Assert.IsTrue(vm.IsSuccess);

            // Al hacer clic en "Editar", los campos se desbloquean y el botón cambia a "Guardar"
            vm.ToggleEditOrSaveCommand.Execute(null);
            Assert.IsTrue(vm.IsEditing, "Los campos deben desbloquearse al hacer clic en Editar");
            Assert.AreEqual("Guardar", vm.ActionButtonText, "El botón debe cambiar a Guardar");
        }
        finally
        {
            if (File.Exists(tempProfilePath))
            {
                try { File.Delete(tempProfilePath); } catch { }
            }
        }
    }

    [TestMethod]
    public void DatabaseInitializer_SeedDemoDataFalse_InitializesWithoutDemoData()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:empty_init_test_{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;

        using var db = new AppDbContext(options);
        db.Database.OpenConnection();
        DatabaseInitializer.Initialize(db, seedDemoData: false);

        // Debe tener categorías y servicios clínicos predefinidos
        Assert.IsTrue(db.Categories.Any(), "Debe inicializar categorías");
        Assert.IsTrue(db.ClinicalServices.Any(), "Debe inicializar servicios");

        // NO debe tener pacientes ni productos ficticios
        Assert.AreEqual(0, db.Patients.Count(), "No debe sembrar pacientes demo");
        Assert.AreEqual(0, db.Products.Count(), "No debe sembrar productos demo");

        // Si después se solicita sembrar demostración explícitamente:
        DatabaseInitializer.SeedDemoData(db);
        Assert.AreEqual(4, db.Patients.Count(), "Debe sembrar los 4 pacientes de demostración");
        Assert.AreEqual(9, db.Products.Count(), "Debe sembrar los 9 productos de demostración");
    }

    [TestMethod]
    public async Task PosViewModel_TicketHistoryAndCashCut_WorkSeamlessly()
    {
        string tempProfilePath = Path.Combine(Path.GetTempPath(), $"tpvc_profile_test_{Guid.NewGuid():N}.json");
        try
        {
            var profileService = new ClinicProfileService(tempProfilePath);
            var printingService = new PrintingService();
            var posVm = new PosViewModel(_inventoryService, new ConsultationService(), profileService, printingService);
            await posVm.InitializeAsync();

            // Probar apertura de modal de corte de caja
            await posVm.OpenCashCutModalAsync();
            Assert.IsTrue(posVm.IsCashCutModalOpen);
            Assert.IsNotNull(posVm.CurrentCashCut);
            posVm.CloseCashCutModal();
            Assert.IsFalse(posVm.IsCashCutModalOpen);

            // Probar apertura de historial de tickets
            await posVm.OpenTicketHistoryModalAsync();
            Assert.IsTrue(posVm.IsTicketHistoryModalOpen);
            Assert.IsNotNull(posVm.RecentSales);
            posVm.CloseTicketHistoryModal();
            Assert.IsFalse(posVm.IsTicketHistoryModalOpen);
        }
        finally
        {
            if (File.Exists(tempProfilePath))
            {
                try { File.Delete(tempProfilePath); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_Modals_InitialDropdownClosedUntilTyping()
    {
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());
        await vm.LoadInitialDataAsync();

        var patient = vm.Patients.First();
        vm.SelectedPatient = patient;

        // 1. Receta médica: el menú desplegable NO debe estar abierto al inicio
        await vm.OpenPrescriptionModalAsync();
        Assert.IsTrue(vm.IsPrescriptionModalOpen);
        Assert.IsFalse(vm.IsPrescriptionDropdownOpen, "El menú de sugerencias no debe desplegarse al entrar");
        vm.PrescriptionSearchText = "Para";
        Assert.IsTrue(vm.IsPrescriptionDropdownOpen, "El menú debe desplegarse únicamente al escribir");
        Assert.IsTrue(vm.PrescriptionProductSuggestions.Count > 0, "Debe tener sugerencias filtradas de medicamentos");
        Assert.IsTrue(vm.PrescriptionProductSuggestions.All(MedicalRecordsViewModel.IsProductMedication), "Solo debe tener medicamentos");
        vm.ClosePrescriptionModal();

        // 2. Insumos: el menú desplegable NO debe estar abierto al inicio
        await vm.OpenSuppliesModalAsync();
        Assert.IsTrue(vm.IsSuppliesModalOpen);
        Assert.IsFalse(vm.IsSupplyDropdownOpen, "El menú de insumos no debe desplegarse al entrar");
        vm.SupplySearchText = "Gas";
        Assert.IsTrue(vm.IsSupplyDropdownOpen, "El menú debe desplegarse únicamente al escribir");
        Assert.IsTrue(vm.SupplyProductSuggestions.Count > 0, "Debe tener sugerencias filtradas de insumos");
        Assert.IsTrue(vm.SupplyProductSuggestions.All(MedicalRecordsViewModel.IsProductSupply), "Solo debe tener insumos");
        vm.CloseSuppliesModal();

        // 3. Servicios: el menú desplegable NO debe estar abierto al inicio
        await vm.OpenServicesModalAsync();
        Assert.IsTrue(vm.IsServicesModalOpen);
        Assert.IsFalse(vm.IsServiceModalDropdownOpen, "El menú de servicios no debe desplegarse al entrar");
        vm.ServiceModalSearchText = "Cons";
        Assert.IsTrue(vm.IsServiceModalDropdownOpen, "El menú debe desplegarse únicamente al escribir");
        Assert.IsTrue(vm.ServiceModalSuggestions.Count > 0, "Debe tener sugerencias filtradas de servicios");
        vm.CloseServicesModal();
    }

    [TestMethod]
    public async Task MedicalRecordsViewModel_CatalogBrowser_OpensFilteredCatalogsAndAddsItemsDirectly()
    {
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());
        await vm.LoadInitialDataAsync();

        var patient = vm.Patients.First();
        patient.Allergies = "Ninguna";
        patient.ActivePrescription = new List<PatientPrescriptionItem>();
        patient.ActiveSupplies = new List<PatientSupplyItem>();
        patient.ActiveServices = new List<PatientServiceItem>();
        vm.SelectedPatient = patient;

        // 1. Catálogo desde Receta Médica -> Sólo Medicamentos
        await vm.OpenPrescriptionCatalogAsync();
        Assert.IsTrue(vm.IsCatalogBrowserModalOpen);
        Assert.AreEqual("Medications", vm.CatalogBrowserMode);
        Assert.IsTrue(vm.CatalogBrowserTitle.Contains("Medicamentos"));
        Assert.IsTrue(vm.CatalogBrowserFilteredProducts.Count > 0);
        Assert.IsTrue(vm.CatalogBrowserFilteredProducts.All(MedicalRecordsViewModel.IsProductMedication), "El catálogo debe mostrar únicamente fármacos/medicamentos");

        // Agregar medicamento desde catálogo
        var chosenMed = vm.CatalogBrowserFilteredProducts.First();
        await vm.AddProductFromCatalogBrowserAsync(chosenMed);
        Assert.IsFalse(vm.IsCatalogBrowserModalOpen, "Debe cerrarse la ventana de catálogo tras agregar");
        Assert.AreEqual(1, patient.ActivePrescription.Count);
        Assert.IsTrue(patient.ActivePrescription[0].MedicineName.Contains(chosenMed.Name));

        // 2. Catálogo desde Insumos -> Sólo Insumos
        await vm.OpenSupplyCatalogAsync();
        Assert.IsTrue(vm.IsCatalogBrowserModalOpen);
        Assert.AreEqual("Supplies", vm.CatalogBrowserMode);
        Assert.IsTrue(vm.CatalogBrowserTitle.Contains("Insumos"));
        Assert.IsTrue(vm.CatalogBrowserFilteredProducts.Count > 0);
        Assert.IsTrue(vm.CatalogBrowserFilteredProducts.All(MedicalRecordsViewModel.IsProductSupply), "El catálogo debe mostrar únicamente insumos y material de curación");

        // Agregar insumo desde catálogo
        var chosenSupply = vm.CatalogBrowserFilteredProducts.First();
        await vm.AddProductFromCatalogBrowserAsync(chosenSupply);
        Assert.IsFalse(vm.IsCatalogBrowserModalOpen);
        Assert.AreEqual(1, patient.ActiveSupplies.Count);
        Assert.IsTrue(patient.ActiveSupplies[0].SupplyName.Contains(chosenSupply.Name));

        // 3. Catálogo desde Servicios -> Procedimientos clínicos
        await vm.OpenServiceCatalogAsync();
        Assert.IsTrue(vm.IsCatalogBrowserModalOpen);
        Assert.AreEqual("Services", vm.CatalogBrowserMode);
        Assert.IsTrue(vm.CatalogBrowserTitle.Contains("Servicios"));
        Assert.IsTrue(vm.CatalogBrowserFilteredServices.Count > 0);

        // Agregar servicio desde catálogo
        var chosenService = vm.CatalogBrowserFilteredServices.First();
        await vm.AddServiceFromCatalogBrowserAsync(chosenService);
        Assert.IsFalse(vm.IsCatalogBrowserModalOpen);
        Assert.AreEqual(1, patient.ActiveServices.Count);
        Assert.AreEqual(chosenService.Name, patient.ActiveServices[0].ServiceName);
    }

    [TestMethod]
    public async Task Appointments_Save_GetByDay_GetByWeek_UpdateStatus_Delete_WorksCorrectly()
    {
        var patient = (await _inventoryService.GetPatientsAsync()).First();
        var today = DateTime.Today;

        var appt1 = new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            PatientPhone = patient.Phone,
            Date = today,
            Time = "10:30",
            Reason = "Revisión general de control",
            Notes = "Paciente en ayuno",
            Status = "Programada"
        };

        var appt2 = new PatientAppointment
        {
            PatientRecordId = patient.Id,
            PatientName = patient.FullName,
            PatientPhone = patient.Phone,
            Date = today.AddDays(2),
            Time = "16:00",
            Reason = "Seguimiento de glucosa",
            Status = "Programada"
        };

        var saved1 = await _inventoryService.SaveAppointmentAsync(appt1);
        var saved2 = await _inventoryService.SaveAppointmentAsync(appt2);

        Assert.IsTrue(saved1.Id > 0);
        Assert.IsTrue(saved2.Id > 0);

        // Day view
        var dayAppointments = await _inventoryService.GetAppointmentsByDayAsync(today);
        Assert.IsTrue(dayAppointments.Any(a => a.Id == saved1.Id));
        Assert.IsFalse(dayAppointments.Any(a => a.Id == saved2.Id));

        // Week view
        var weekAppointments = await _inventoryService.GetAppointmentsByWeekAsync(today);
        Assert.IsTrue(weekAppointments.Count >= 2);

        // Update status
        await _inventoryService.UpdateAppointmentStatusAsync(saved1.Id, "Confirmada");
        var refreshed = (await _inventoryService.GetAppointmentsAsync()).First(a => a.Id == saved1.Id);
        Assert.AreEqual("Confirmada", refreshed.Status);

        // Delete
        await _inventoryService.DeleteAppointmentAsync(saved2.Id);
        var allAfterDelete = await _inventoryService.GetAppointmentsAsync();
        Assert.IsFalse(allAfterDelete.Any(a => a.Id == saved2.Id));
    }

    [TestMethod]
    public void VitalSigns_EnsureNumericParsing_AndEvolutionChart_WorksCorrectly()
    {
        var consultation = new PatientConsultation
        {
            Date = DateTime.Today,
            VitalSigns = "TA: 120/80 mmHg, FC: 72 lpm, Temp: 36.5 °C, Peso: 74.5 kg, Talla: 1.70 m, Glucosa: 95 mg/dL"
        };

        consultation.EnsureNumericVitalSigns();

        Assert.AreEqual(74.5m, consultation.Weight);
        Assert.AreEqual(120m, consultation.BloodPressureSystolic);
        Assert.AreEqual(80m, consultation.BloodPressureDiastolic);
        Assert.AreEqual(95m, consultation.Glucose);
        Assert.AreEqual(72m, consultation.HeartRate);
        Assert.AreEqual(36.5m, consultation.Temperature);
        Assert.AreEqual(1.70m, consultation.Height);
        Assert.IsTrue(consultation.BMI.HasValue);
        Assert.IsTrue(Math.Abs((double)consultation.BMI.Value - 25.77) < 0.1);

        // PatientRecord evolution
        var patient = new PatientRecord
        {
            FullName = "Paciente Test",
            Consultations = new List<PatientConsultation>
            {
                new() { Date = DateTime.Today.AddDays(-30), Weight = 78.0m, BloodPressureSystolic = 135m, BloodPressureDiastolic = 85m, Glucose = 110m },
                new() { Date = DateTime.Today.AddDays(-15), Weight = 76.2m, BloodPressureSystolic = 125m, BloodPressureDiastolic = 82m, Glucose = 102m },
                new() { Date = DateTime.Today, Weight = 74.5m, BloodPressureSystolic = 120m, BloodPressureDiastolic = 80m, Glucose = 95m }
            }
        };

        var evolution = patient.GetVitalSignsEvolution();
        Assert.AreEqual(3, evolution.Count);
        Assert.AreEqual(78.0m, evolution[0].Weight);
        Assert.AreEqual(74.5m, evolution[2].Weight);
    }

    [TestMethod]
    public async Task MedicationAllergy_CheckAndModalOverride_WorksCorrectly()
    {
        var patient = new PatientRecord
        {
            FullName = "Juan Pérez",
            Allergies = "Penicilina (urticaria grave), Aspirina"
        };

        var amoxi = new Product { Name = "Amoxicilina con Ácido Clavulánico", GenericName = "Amoxicilina / Clavulanato", Presentation = "Tabletas 500mg/125mg" };
        var aspirina = new Product { Name = "Aspirina Protect", GenericName = "Ácido Acetilsalicílico", Presentation = "100mg" };
        var paracetamol = new Product { Name = "Tempra", GenericName = "Paracetamol", Presentation = "500mg" };

        // Test InventoryService.CheckMedicationAllergy
        var checkAmoxi = InventoryService.CheckMedicationAllergy(patient, amoxi, amoxi.Name);
        Assert.IsTrue(checkAmoxi.HasConflict);
        Assert.IsTrue(checkAmoxi.WarningDetails.Contains("penicilina", StringComparison.OrdinalIgnoreCase));

        var checkAsp = InventoryService.CheckMedicationAllergy(patient, aspirina, aspirina.Name);
        Assert.IsTrue(checkAsp.HasConflict);

        var checkPara = InventoryService.CheckMedicationAllergy(patient, paracetamol, paracetamol.Name);
        Assert.IsFalse(checkPara.HasConflict);

        // Test MedicalRecordsViewModel allergy alert interception & override
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());
        patient.ActivePrescription = new List<PatientPrescriptionItem>();
        vm.SelectPatient(patient);

        // Attempting to prescribe Amoxicilina should trigger alert modal
        await vm.SelectAndAddPrescriptionProductAsync(amoxi);
        Assert.IsTrue(vm.IsAllergyAlertOpen, "La alerta de alergia debe abrirse para un medicamento en conflicto");
        Assert.AreEqual(0, patient.ActivePrescription.Count, "No debe añadirse a la receta mientras esté pendiente la alerta");

        // Doctor confirms override under clinical discretion
        await vm.ConfirmOverrideAllergyPrescriptionAsync();
        Assert.IsFalse(vm.IsAllergyAlertOpen, "El modal debe cerrarse tras confirmar el override");
        Assert.IsTrue(patient.ActivePrescription[0].MedicineName.Contains(amoxi.Name));
    }

    [TestMethod]
    public void Cie10Catalog_SearchAndAutocomplete_WorksCorrectly()
    {
        // Search by code
        var resCode = Cie10Diagnosis.Search("I10").ToList();
        Assert.IsTrue(resCode.Count > 0);
        Assert.IsTrue(resCode.Any(d => d.Code == "I10" && d.Name.Contains("Hipertensión")));

        // Search by normalized word
        var resText = Cie10Diagnosis.Search("faringitis").ToList();
        Assert.IsTrue(resText.Count > 0);
        Assert.IsTrue(resText.All(d => d.Name.Contains("faringitis", StringComparison.OrdinalIgnoreCase) || d.Category.Contains("faringitis", StringComparison.OrdinalIgnoreCase)));

        // Empty search returns common diagnoses
        var resEmpty = Cie10Diagnosis.Search("").ToList();
        Assert.IsTrue(resEmpty.Count > 0);

        // Autocomplete in MedicalRecordsViewModel
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());
        vm.ConsultationDiagnosis = "diab";
        Assert.IsTrue(vm.IsCie10DropdownOpen);
        Assert.IsTrue(vm.Cie10Suggestions.Count > 0);

        var chosen = vm.Cie10Suggestions.First();
        vm.SelectCie10Suggestion(chosen);
        Assert.IsFalse(vm.IsCie10DropdownOpen);
        Assert.AreEqual($"{chosen.Code} - {chosen.Name}", vm.ConsultationDiagnosis);
    }

    [TestMethod]
    public async Task SystemAlerts_AndSuggestedPurchases_CSV_AndPrint_WorksCorrectly()
    {
        var summary = await _inventoryService.GetSystemAlertsSummaryAsync();
        Assert.IsNotNull(summary);

        var purchases = await _inventoryService.GetSuggestedPurchaseListAsync();
        Assert.IsNotNull(purchases);

        var hubVm = new MainHubViewModel(s => { }, _inventoryService);
        await hubVm.RefreshAlertsAsync();
        Assert.IsNotNull(hubVm.AlertsSummary);

        await hubVm.OpenSuggestedPurchasesAsync();
        Assert.IsTrue(hubVm.IsSuggestedPurchasesOpen);
        Assert.IsNotNull(hubVm.SuggestedPurchases);

        // Test CSV export
        await hubVm.ExportSuggestedPurchasesCsvAsync();
        Assert.IsNotNull(hubVm.SuggestedPurchasesStatusMessage);
        Assert.IsTrue(hubVm.SuggestedPurchasesStatusMessage.Contains("Exportado") || hubVm.SuggestedPurchasesStatusMessage.Contains("No hay"));

        // Test Print report text generation
        hubVm.PrintSuggestedPurchases();
        Assert.IsNotNull(hubVm.SuggestedPurchasesStatusMessage);

        hubVm.CloseSuggestedPurchases();
        Assert.IsFalse(hubVm.IsSuggestedPurchasesOpen);
    }

    [TestMethod]
    public void Agenda_GoogleCalendarAndWhatsApp_TodoPlan_WorksCorrectly()
    {
        var vm = new MedicalRecordsViewModel(_inventoryService, new ConsultationService());
        var appt = new PatientAppointment
        {
            Id = 42,
            PatientName = "Pedro Sánchez",
            PatientPhone = "5512345678",
            Date = DateTime.Today.AddDays(1),
            Time = "11:00",
            Reason = "Control de presión arterial"
        };

        // Google Calendar TODO
        vm.OpenGoogleCalendarTodo(appt);
        Assert.IsTrue(vm.IsGoogleCalendarTodoModalOpen);
        Assert.IsTrue(vm.GoogleCalendarUrl.Contains("calendar.google.com"));
        Assert.IsTrue(vm.GoogleCalendarDetails.Contains("Google Calendar API"));
        vm.CloseGoogleCalendarTodo();
        Assert.IsFalse(vm.IsGoogleCalendarTodoModalOpen);

        // WhatsApp Reminder TODO
        vm.OpenWhatsAppTodo(appt);
        Assert.IsTrue(vm.IsWhatsAppTodoModalOpen);
        Assert.IsTrue(vm.WhatsAppUrl.Contains("wa.me"));
        Assert.IsTrue(vm.WhatsAppMessagePreview.Contains("Pedro"));
        vm.CloseWhatsAppTodo();
        Assert.IsFalse(vm.IsWhatsAppTodoModalOpen);
    }
}









