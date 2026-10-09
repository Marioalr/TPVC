using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Punto de venta v1.15: un solo componente de cobro (<see cref="CheckoutViewModel"/>) para el
/// mostrador y "Terminar consulta", cada uno con su propia cuenta; búsqueda con cantidad y
/// teclado.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PosTests
{
    private string _dbPath = string.Empty;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_pos_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        using (var db = new AppDbContext(builder.Options))
        {
            DatabaseInitializer.Initialize(db);
        }
        _service = new InventoryService(new AppDbContextFactory(builder.Options));
        DialogService.ConfirmHandler = (_, _) => true;
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private async Task<PosViewModel> CreatePosAsync(ConsultationService? consultations = null)
    {
        var pos = new PosViewModel(_service, consultations ?? new ConsultationService(seedDemoPatients: false));
        await pos.InitializeAsync();
        return pos;
    }

    private static Product Product(int id, string name, string code = "", string generic = "", int stock = 10, int minStock = 0) => new()
    {
        Id = id,
        Name = name,
        Code = code,
        GenericName = generic,
        CurrentStock = stock,
        MinStock = minStock,
        Price = 10m
    };

    // ------------------------------------------------------------------ búsqueda

    [TestMethod]
    public void ParseQuantityPrefix_ReadsTheNumberBeforeTheAsterisk()
    {
        Assert.AreEqual((3, "para"), PosViewModel.ParseQuantityPrefix("3*para"));
        Assert.AreEqual((12, "gasa esteril"), PosViewModel.ParseQuantityPrefix(" 12 * gasa esteril"));
        Assert.AreEqual((1, "paracetamol"), PosViewModel.ParseQuantityPrefix("paracetamol"));
        Assert.AreEqual((1, "x"), PosViewModel.ParseQuantityPrefix("0*x"), "Cero no tiene sentido: se agrega 1");
        Assert.AreEqual((1, "2xilocaina"), PosViewModel.ParseQuantityPrefix("2xilocaina"), "Solo el asterisco indica cantidad");
    }

    [TestMethod]
    public void RankProducts_ExactCodeFirst_ThenPrefix_Word_AndContains_WithStockFirst()
    {
        var products = new[]
        {
            Product(1, "Ibuprofeno 400 mg", "MED-010"),
            Product(2, "Paracetamol 500 mg", "MED-001", "Paracetamol", stock: 0),
            Product(3, "Paracetamol infantil", "MED-002", "Paracetamol"),
            Product(4, "Gasa con paracetamol", "MED-003"),
            Product(5, "Antigripal", "MED-004", "Paracetamol + clorfenamina"),
            Product(6, "Tempra", "PARA-77")
        };

        var ranked = PosViewModel.RankProducts(products, "para").Select(p => p.Id).ToList();
        CollectionAssert.AreEqual(new[] { 3, 2, 5, 4, 6 }, ranked,
            "Nombre que empieza así (con existencias primero), genérico que empieza así, palabra y al final lo que lo contiene (código)");

        Assert.AreEqual(1, PosViewModel.RankProducts(products, "med-010").First().Id, "El código exacto (lector) va primero");
        Assert.AreEqual(3, PosViewModel.RankProducts(products, "PARACETAMOL INF").Single().Id, "Sin acentos ni mayúsculas");
        Assert.IsEmpty(PosViewModel.RankProducts(products, "  "));
    }

    [TestMethod]
    public async Task Search_EnterAddsFirstSuggestion_AndKeepsSearchReady()
    {
        var pos = await CreatePosAsync();
        var product = pos.Products.First(p => p.CurrentStock > 3);

        pos.SearchText = product.Code;
        Assert.IsNotEmpty(pos.SearchSuggestions);
        Assert.AreEqual(product.Id, pos.SelectedSuggestion?.Id);

        pos.ConfirmSelectedSuggestion();
        Assert.HasCount(1, pos.Cart);
        Assert.AreEqual(product.Id, pos.Checkout.SelectedItem?.ProductId, "El renglón recién agregado queda elegido para + y −");
        Assert.IsFalse(pos.HasSearchText);

        pos.SearchText = "zzzz-nada";
        Assert.IsTrue(pos.HasNoSearchResults);
    }

    // ------------------------------------------------------------------ cuenta

    [TestMethod]
    public void CartItem_QuantityTypedByHand_StaysWithinStock()
    {
        var item = new CartItem { Name = "Gasa", AvailableStock = 5, UnitPrice = 2m };
        item.Quantity = 9;
        Assert.AreEqual(5, item.Quantity, "No más de lo que hay");
        item.Quantity = 0;
        Assert.AreEqual(1, item.Quantity, "Mínimo 1 (para quitarlo está Supr o la ×)");

        var service = new CartItem { Name = "Curación", IsService = true, AvailableStock = 1 };
        service.Quantity = 3;
        Assert.AreEqual(3, service.Quantity, "Los servicios no tienen existencias");
    }

    [TestMethod]
    public void Checkout_KeyboardSelection_AndUndoOnlyRightAfterClearing()
    {
        var checkout = new CheckoutViewModel(null);
        checkout.AddProduct(Product(1, "A"));
        checkout.AddProduct(Product(2, "B"));
        checkout.AddProduct(Product(3, "C"));
        Assert.AreEqual(3, checkout.SelectedItem?.ProductId, "El último agregado queda elegido");

        checkout.SelectNextItem(-1);
        Assert.AreEqual(2, checkout.SelectedItem?.ProductId);
        checkout.IncreaseQuantity(null);
        Assert.AreEqual(2, checkout.Cart.Single(c => c.ProductId == 2).Quantity, "+ sin renglón explícito aplica al elegido");
        checkout.RemoveFromCart(null);
        Assert.AreEqual(3, checkout.SelectedItem?.ProductId, "Al quitar, queda elegido el siguiente");

        checkout.ClearCart();
        Assert.IsTrue(checkout.CanUndoClear);
        checkout.UndoClear();
        Assert.HasCount(2, checkout.Cart);

        checkout.ClearCart();
        checkout.AddProduct(Product(4, "D"));
        Assert.IsFalse(checkout.CanUndoClear, "Al empezar otra cuenta ya no se deshace");
    }

    [TestMethod]
    public void Checkout_CashRules_ExactWhenEmpty_ShortBlocksCharging()
    {
        var checkout = new CheckoutViewModel(null);
        checkout.AddLine(new CartItem { ProductId = -1, Code = "S", Name = "Consulta", IsService = true, UnitPrice = 300m, AvailableStock = 999 });

        Assert.IsTrue(checkout.CanCharge, "Sin recibido escrito se cobra exacto");
        Assert.IsFalse(checkout.HasChange);

        checkout.CashReceivedText = "$200";
        Assert.AreEqual(200m, checkout.CashReceived);
        Assert.IsTrue(checkout.IsCashShort);
        Assert.AreEqual(100m, checkout.MissingAmount);
        Assert.IsFalse(checkout.CanCharge);

        checkout.CashReceivedText = "500.";
        Assert.AreEqual("500.", checkout.CashReceivedText, "Lo escrito no se reformatea mientras se teclea");
        Assert.AreEqual(200m, checkout.ChangeDue);
        Assert.IsTrue(checkout.HasChange);
        Assert.IsTrue(checkout.CanCharge);

        checkout.SelectedPaymentMethod = "Transferencia";
        Assert.IsFalse(checkout.IsCash);
        Assert.IsFalse(checkout.HasChange);

        checkout.SelectedPaymentMethod = CheckoutViewModel.Cash;
        checkout.SetExactCash();
        Assert.AreEqual(300m, checkout.CashReceived);
        Assert.AreEqual("300", checkout.CashReceivedText);
    }

    [TestMethod]
    public void SuggestCashAmounts_OffersTheNextBillsAboveTheTotal()
    {
        CollectionAssert.AreEqual(new[] { 300m, 400m, 500m }, CheckoutViewModel.SuggestCashAmounts(287.5m).ToList());
        CollectionAssert.AreEqual(new[] { 400m, 500m, 1000m }, CheckoutViewModel.SuggestCashAmounts(300m).ToList(), "Exacto ya tiene su botón");
        Assert.IsEmpty(CheckoutViewModel.SuggestCashAmounts(0m));
        Assert.AreEqual(1250.5m, CheckoutViewModel.ParseAmount("$1,250.50"));
        Assert.AreEqual(0m, CheckoutViewModel.ParseAmount("abc"));
    }

    // ------------------------------------------------------------------ cobro

    [TestMethod]
    public async Task ProcessSale_RegistersSale_ShowsFreshTicketWithChange()
    {
        var pos = await CreatePosAsync();
        var product = pos.Products.First(p => p.CurrentStock > 2 && p.Price > 0);
        pos.AddToCartWithQuantity(product, 2);
        var total = pos.TotalCartAmount;
        pos.Checkout.CashReceived = Math.Ceiling(total / 100m) * 100m + 100m;

        await pos.ProcessSaleAsync();

        Assert.IsTrue(pos.IsTicketModalOpen);
        Assert.IsTrue(pos.IsFreshSaleTicket);
        Assert.IsNotNull(pos.CurrentReceipt);
        Assert.AreEqual(total, pos.CurrentReceipt.TotalAmount);
        Assert.AreEqual(pos.CurrentReceipt.CashReceived - total, pos.CurrentReceipt.Change);
        Assert.IsEmpty(pos.Cart, "La cuenta queda lista para la siguiente venta");
        Assert.AreEqual(0m, pos.Checkout.CashReceived);

        var stock = (await _service.GetProductsAsync()).Single(p => p.Id == product.Id).CurrentStock;
        Assert.AreEqual(product.CurrentStock - 2, stock);

        // Reimprimir desde el historial no es una venta nueva
        pos.CloseTicketModal();
        await pos.OpenTicketHistoryModalAsync();
        pos.ReprintSelectedTicket();
        Assert.IsTrue(pos.IsTicketModalOpen);
        Assert.IsFalse(pos.IsFreshSaleTicket);
    }

    [TestMethod]
    public async Task ProcessSale_WhenCashIsShort_DoesNotCharge()
    {
        var pos = await CreatePosAsync();
        pos.AddToCart(pos.Products.First(p => p.CurrentStock > 0 && p.Price > 5));
        pos.Checkout.CashReceived = 1m;

        await pos.ProcessSaleAsync();

        Assert.IsFalse(pos.IsTicketModalOpen);
        Assert.HasCount(1, pos.Cart, "No se pierde la cuenta");
        StringAssert.Contains(pos.AlertMessage, "Faltan");
        Assert.IsEmpty(await _service.GetSalesAsync(DateTime.Today, DateTime.Today.AddDays(1)));
    }

    [TestMethod]
    public async Task Charge_PressedTwice_RegistersOneSale()
    {
        var pos = await CreatePosAsync();
        pos.AddToCart(pos.Products.First(p => p.CurrentStock > 0));

        await Task.WhenAll(pos.ProcessSaleAsync(), pos.ProcessSaleAsync());

        Assert.HasCount(1, await _service.GetSalesAsync(DateTime.Today, DateTime.Today.AddDays(1)), "Un doble clic no cobra dos veces");
    }

    [TestMethod]
    public async Task FinishingAConsultation_DoesNotTouchTheCounterSale()
    {
        // Antes compartían el carrito: terminar una consulta vaciaba la venta a medias del mostrador
        var consultations = new ConsultationService(seedDemoPatients: false);
        var pos = await CreatePosAsync(consultations);
        var records = new MedicalRecordsViewModel(_service, consultations);
        await records.LoadInitialDataAsync();

        var counterProduct = pos.Products.First(p => p.CurrentStock > 0);
        pos.AddToCart(counterProduct);
        pos.Checkout.PatientName = "Cliente de mostrador";

        records.SelectPatient(records.Patients.First());
        records.StartActiveConsultation();
        records.Consultation.ConsultationDiagnosis = "Revisión";
        await records.Consultation.FinalizeConsultationAsync();
        Assert.IsTrue(records.Consultation.Checkout.HasItems);
        Assert.HasCount(1, pos.Cart, "La venta del mostrador sigue intacta");

        await records.Consultation.Checkout.ChargeAsync();

        Assert.IsFalse(consultations.HasActiveConsultation);
        Assert.HasCount(1, pos.Cart);
        Assert.AreEqual(counterProduct.Id, pos.Cart[0].ProductId);
        Assert.AreEqual("Cliente de mostrador", pos.Checkout.PatientName);
        Assert.IsFalse(pos.IsTicketModalOpen, "El cobro de la consulta no abre el ticket del mostrador");
    }

    // ------------------------------------------------------------------ hojas

    [TestMethod]
    public async Task Catalog_CategoryChipsAndEnterAddsFirstWithStock()
    {
        var pos = await CreatePosAsync();
        pos.OpenCatalogModal();
        Assert.AreEqual(0, pos.CatalogSelectedCategory?.Id, "Empieza en Todas");
        int all = pos.CatalogFilteredProducts.Count;

        var category = pos.Categories.First(c => c.Id > 0 && pos.Products.Any(p => p.CategoryId == c.Id));
        pos.CatalogSelectedCategory = category;
        Assert.IsTrue(pos.CatalogFilteredProducts.All(p => p.CategoryId == category.Id));
        Assert.IsLessThan(all, pos.CatalogFilteredProducts.Count);

        pos.AddFirstCatalogProduct();
        Assert.HasCount(1, pos.Cart);
        Assert.AreEqual(category.Id, pos.Products.Single(p => p.Id == pos.Cart[0].ProductId).CategoryId);
    }

    [TestMethod]
    public async Task CashCut_NavigatesDays_ButNotIntoTheFuture()
    {
        var pos = await CreatePosAsync();
        await pos.OpenCashCutModalAsync();
        Assert.IsTrue(pos.IsCashCutToday);
        Assert.IsFalse(pos.CanGoToNextCashCutDay);

        await pos.NextCashCutDayAsync();
        Assert.AreEqual(DateTime.Today, pos.CashCutDate);

        await pos.PreviousCashCutDayAsync();
        Assert.AreEqual(DateTime.Today.AddDays(-1), pos.CashCutDate);
        Assert.IsTrue(pos.CanGoToNextCashCutDay);
        Assert.IsNotNull(pos.CurrentCashCut);

        await pos.TodayCashCutAsync();
        Assert.IsTrue(pos.IsCashCutToday);
    }

    [TestMethod]
    public async Task Services_AddWithSupplies_OrOnlyTheFee()
    {
        var pos = await CreatePosAsync();
        var withSupplies = pos.Services.FirstOrDefault(s => s.DefaultProducts.Any(dp => dp.Product?.CurrentStock > 0));
        Assert.IsNotNull(withSupplies, "El catálogo sembrado trae servicios con insumos");

        pos.AddServiceOnlyToCart(withSupplies);
        Assert.HasCount(1, pos.Cart);
        Assert.IsTrue(pos.Cart[0].IsService);

        pos.ClearCart();
        pos.AddServiceToCart(withSupplies);
        Assert.IsGreaterThan(1, pos.Cart.Count, "Honorario + insumos");
        Assert.IsFalse(pos.IsServicesModalOpen);
    }
}
