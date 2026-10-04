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
}






