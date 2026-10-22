using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Revisión 20, decisión del médico del 2026-10-22: <b>un inventario mal contado nunca impide cobrar</b> (la política de
/// negativos del paso 8.20, adelantada). La venta y el consumo pasan aunque no alcancen las existencias; lo que no alcanza
/// con los lotes sale sin lote (8.19), el libro sigue cuadrando (StockLedger.VerifyAsync sin diferencias nuevas, del que
/// depende el 8.20), cancelar regresa todo exacto, también con piezas sueltas (v1.47.0), y el producto pide revisar el
/// inventario hasta que un conteo o una entrada lo corrijan.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NegativeStockTests
{
    private string _dbPath = string.Empty;
    private DbContextOptions<AppDbContext> _options = null!;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_negativos_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        _options = builder.Options;
        using (var db = new AppDbContext(_options)) DatabaseInitializer.Initialize(db);
        _service = new InventoryService(new AppDbContextFactory(_options));
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

    private AppDbContext NewContext() => new(_options);

    private async Task AssertLedgerCleanAsync(string because)
    {
        await using var db = NewContext();
        var report = await StockLedger.VerifyAsync(db);
        Assert.IsTrue(report.IsClean, $"{because}: {report.Summary}");
        Assert.IsEmpty(report.Entries, $"{because}: ninguna diferencia, ni vieja ({report.Summary})");
    }

    private async Task<Product> ProductAsync(int id)
    {
        await using var db = NewContext();
        return await db.Products.AsNoTracking().Include(p => p.Batches).SingleAsync(p => p.Id == id);
    }

    /// <summary>El producto sin sus lotes, como lo guarda el diálogo del producto (el conteo).</summary>
    private async Task CountAsync(int productId, int counted)
    {
        Product product;
        await using (var db = NewContext()) product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        int original = product.CurrentStock;
        product.CurrentStock = counted;
        await _service.SaveProductAsync(product, original);
    }

    private async Task<List<InventoryMovement>> MovementsOfAsync(int productId)
    {
        await using var db = NewContext();
        return await db.InventoryMovements.AsNoTracking().Where(m => m.ProductId == productId).OrderBy(m => m.Id).ToListAsync();
    }

    /// <summary>Un producto sin piezas sueltas, con un lote que vence pronto (2) y otro después (1): 3 en total, todo en lotes.</summary>
    private async Task<(Product Product, ProductBatch Soon, ProductBatch Later)> ProductWithLotsAsync(string code, int unitsPerPackage = 1)
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = code, Name = $"Producto {code}", CategoryId = category.Id, Price = 10m, UnitsPerPackage = unitsPerPackage };
        await _service.SaveProductAsync(product);
        var soon = new ProductBatch { ProductId = product.Id, BatchNumber = $"{code}-PRONTO", ExpirationDate = DateTime.Today.AddMonths(2), Quantity = 2 };
        var later = new ProductBatch { ProductId = product.Id, BatchNumber = $"{code}-DESPUES", ExpirationDate = DateTime.Today.AddMonths(12), Quantity = 1 };
        await _service.ReceiveBatchAsync(soon);
        await _service.ReceiveBatchAsync(later);
        return (product, soon, later);
    }

    // ================================================================== lotes y libro

    [TestMethod]
    public async Task SaleBeyondTheLots_EmptiesThemByFefo_AndTheRestGoesWithoutLot_TheLedgerStaysClean()
    {
        var (product, soon, later) = await ProductWithLotsAsync("NEG-1");
        await AssertLedgerCleanAsync("Antes de vender");

        var sale = await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, 5, 10m) }, "Paciente", "Efectivo");
        Assert.IsNotNull(sale);
        Assert.AreEqual(new StockShortage(product.Name, 3, 5, false), sale.StockShortages.Single());

        var after = await ProductAsync(product.Id);
        Assert.AreEqual(-2, after.CurrentStock);
        Assert.AreEqual(0, after.Batches.Single(b => b.Id == soon.Id).Quantity, "Los lotes nunca bajan de 0 por una salida");
        Assert.AreEqual(0, after.Batches.Single(b => b.Id == later.Id).Quantity);
        Assert.IsTrue(after.Batches.All(b => b.IsDepleted));

        var exits = (await MovementsOfAsync(product.Id)).Where(m => m.MovementType == MovementType.SalidaVenta).ToList();
        CollectionAssert.AreEqual(new int?[] { soon.Id, later.Id, null }, exits.Select(m => m.BatchId).ToList(), "FEFO y el resto sin lote");
        CollectionAssert.AreEqual(new[] { -2, -1, -2 }, exits.Select(m => m.Delta).ToList());
        CollectionAssert.AreEqual(new[] { 1, 0, -2 }, exits.Select(m => m.StockAfter).ToList(), "La existencia baja renglón por renglón");
        await AssertLedgerCleanAsync("Con el producto en negativo");

        // Otra venta ya en negativo: toda sin lote
        await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, 1, 10m) }, "Paciente", "Efectivo");
        Assert.AreEqual(-3, (await ProductAsync(product.Id)).CurrentStock);
        Assert.IsNull((await MovementsOfAsync(product.Id)).Last().BatchId);
        await AssertLedgerCleanAsync("Otra venta en negativo");

        // Cancelar la primera regresa cada salida a donde salió, exacto
        Assert.IsTrue(await _service.CancelSaleAsync(sale.Id));
        var restored = await ProductAsync(product.Id);
        Assert.AreEqual(2, restored.CurrentStock);
        Assert.AreEqual(2, restored.Batches.Single(b => b.Id == soon.Id).Quantity);
        Assert.AreEqual(1, restored.Batches.Single(b => b.Id == later.Id).Quantity);
        Assert.IsTrue(restored.Batches.All(b => !b.IsDepleted));
        await AssertLedgerCleanAsync("Después de cancelar");
    }

    [TestMethod]
    public async Task ReviewBadge_AndAlert_StayUntilACountOrAnEntryFixesIt()
    {
        var (product, _, _) = await ProductWithLotsAsync("NEG-2");
        await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, 6, 10m) }, "Paciente", "Efectivo");

        var negative = await ProductAsync(product.Id);
        Assert.AreEqual("−3 · revisar inventario", negative.StockBadgeText);
        Assert.IsTrue(negative.NeedsInventoryReview);
        var alerts = await _service.GetSystemAlertsSummaryAsync();
        Assert.AreEqual($"{product.Name}: −3 · revisar inventario", alerts.InventoryReviewLines.Single());

        // Una entrada que no alcanza a cubrirlo: sigue en negativo
        await _service.RegisterMovementAsync(new InventoryMovement { ProductId = product.Id, MovementType = MovementType.EntradaCompra, Quantity = 2, Reason = "Compra" });
        Assert.AreEqual(1, (await _service.GetSystemAlertsSummaryAsync()).InventoryReviewCount);

        // El conteo (cambiar la existencia en el diálogo) lo deja en lo que hay: la alerta se va sola
        await CountAsync(product.Id, 4);
        Assert.AreEqual(0, (await _service.GetSystemAlertsSummaryAsync()).InventoryReviewCount);
        Assert.IsFalse((await ProductAsync(product.Id)).NeedsInventoryReview);
        Assert.AreEqual("Stock bajo: 4", (await ProductAsync(product.Id)).StockBadgeText);
        await AssertLedgerCleanAsync("Después del conteo");
    }

    [TestMethod]
    public async Task ANegativeOpenLot_AsksToReview_EvenIfTheProductTotalIsPositive()
    {
        // Un lote abierto en negativo no lo deja una salida de este equipo (FEFO solo toma lotes con piezas), pero puede llegar
        // cuando las existencias pasen a derivarse del libro (8.20): la alerta ya lo contempla
        var (product, soon, _) = await ProductWithLotsAsync("NEG-3");
        await using (var db = NewContext())
        {
            var lot = await db.ProductBatches.SingleAsync(b => b.Id == soon.Id);
            lot.Quantity = -1;
            await db.SaveChangesAsync();
        }

        var alerts = await _service.GetSystemAlertsSummaryAsync();
        Assert.AreEqual(product.Id, alerts.InventoryReviewProducts.Single().Id);
        Assert.AreEqual($"{product.Name}: 3 · revisar inventario (lote {soon.BatchNumber} −1)", alerts.InventoryReviewLines.Single());
        Assert.IsTrue((await ProductAsync(product.Id)).NeedsInventoryReview);

        // FEFO salta el lote en negativo
        await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, 1, 10m) }, "Paciente", "Efectivo");
        var exit = (await MovementsOfAsync(product.Id)).Last();
        Assert.AreNotEqual(soon.Id, exit.BatchId);
    }

    [TestMethod]
    public async Task QuickConsultationUsage_BeyondTheLots_GoesWithoutLot_AndTheLedgerStaysClean()
    {
        var (product, _, _) = await ProductWithLotsAsync("NEG-4");
        var shortage = await _service.RegisterQuickConsultationUsageAsync(product.Id, 4, null);
        Assert.AreEqual(new StockShortage(product.Name, 3, 4, false), shortage);
        Assert.AreEqual(-1, (await ProductAsync(product.Id)).CurrentStock);
        Assert.IsNull((await MovementsOfAsync(product.Id)).Last().BatchId);
        await AssertLedgerCleanAsync("Consumo en consulta en negativo");
    }

    [TestMethod]
    public async Task AManualWriteOff_BeyondTheStock_IsStillRefused()
    {
        // Una baja a mano no es una venta: se corrige con un conteo
        var (product, _, _) = await ProductWithLotsAsync("NEG-5");
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.RegisterMovementAsync(
            new InventoryMovement { ProductId = product.Id, MovementType = MovementType.SalidaMerma, Quantity = 4, Reason = "Merma" }));
        StringAssert.Contains(ex.Message, "ajuste por conteo");

        // Una salida de venta o de consulta por la misma vía sí pasa
        await _service.RegisterMovementAsync(new InventoryMovement { ProductId = product.Id, MovementType = MovementType.SalidaConsulta, Quantity = 4, Reason = "Consulta" });
        Assert.AreEqual(-1, (await ProductAsync(product.Id)).CurrentStock);
        await AssertLedgerCleanAsync("Salida de consulta por RegisterMovementAsync");
    }

    // ================================================================== piezas sueltas (v1.47.0)

    [TestMethod]
    public async Task LoosePieces_WithNoPackageLeft_OpenOneThatGoesNegative_AndCancellingIsExact()
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = "NEG-PZ", Name = "Jeringa 5 ml", CategoryId = category.Id, Price = 50m, UnitsPerPackage = 10 };
        await _service.SaveProductAsync(product);

        var sale = await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, 3, 5m, IsIndividualUnits: true) }, "Paciente", "Efectivo");
        Assert.IsNotNull(sale);
        Assert.AreEqual(new StockShortage(product.Name, 0, 3, true), sale.StockShortages.Single());
        var after = await ProductAsync(product.Id);
        Assert.AreEqual(-1, after.CurrentStock, "Abrió un paquete que no había");
        Assert.AreEqual(7, after.OpenedUnitsRemaining, "El paquete abierto nunca queda en negativo");
        Assert.AreEqual(-1, (await MovementsOfAsync(product.Id)).Single(m => m.MovementType == MovementType.SalidaVenta).Delta);
        await AssertLedgerCleanAsync("Piezas sin paquete");

        Assert.IsTrue(await _service.CancelSaleAsync(sale.Id));
        var restored = await ProductAsync(product.Id);
        Assert.AreEqual(0, restored.CurrentStock);
        Assert.AreEqual(0, restored.OpenedUnitsRemaining);
        await AssertLedgerCleanAsync("Piezas canceladas");
    }

    [TestMethod]
    public async Task LoosePieces_AcrossALotAndBeyond_ReturnExactly()
    {
        var (product, soon, later) = await ProductWithLotsAsync("NEG-PZ2", unitsPerPackage: 10);
        await using (var db = NewContext())
        {
            var p = await db.Products.SingleAsync(x => x.Id == product.Id);
            p.OpenedUnitsRemaining = 2;
            await db.SaveChangesAsync();
        }

        // 45 piezas: 2 del paquete abierto y 43 de 5 paquetes; hay 3 en lotes, así que 2 salen sin lote
        var sale = await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, 45, 5m, IsIndividualUnits: true) }, "Paciente", "Efectivo");
        Assert.AreEqual(new StockShortage(product.Name, 32, 45, true), sale!.StockShortages.Single());
        var after = await ProductAsync(product.Id);
        Assert.AreEqual(-2, after.CurrentStock);
        Assert.AreEqual(7, after.OpenedUnitsRemaining);
        var exits = (await MovementsOfAsync(product.Id)).Where(m => m.MovementType == MovementType.SalidaVenta).ToList();
        CollectionAssert.AreEqual(new int?[] { soon.Id, later.Id, null }, exits.Select(m => m.BatchId).ToList());
        CollectionAssert.AreEqual(new[] { -2, -1, -2 }, exits.Select(m => m.Delta).ToList());
        await AssertLedgerCleanAsync("Piezas a través de los lotes");

        Assert.IsTrue(await _service.CancelSaleAsync(sale.Id));
        var restored = await ProductAsync(product.Id);
        Assert.AreEqual(3, restored.CurrentStock);
        Assert.AreEqual(2, restored.OpenedUnitsRemaining);
        Assert.AreEqual(2, restored.Batches.Single(b => b.Id == soon.Id).Quantity);
        Assert.AreEqual(1, restored.Batches.Single(b => b.Id == later.Id).Quantity);
        await AssertLedgerCleanAsync("Piezas canceladas a sus lotes");
    }

    // ================================================================== al azar

    [TestMethod]
    public async Task RandomSequences_WithNegatives_KeepStoredEqualToTheLedger()
    {
        // Ventas de paquetes y de piezas de más, consumos, entradas, lotes nuevos, conteos y cancelaciones: después de cada
        // paso, lo guardado es el libro (sin diferencias nuevas ni viejas) y al cancelar todo vuelve lo que había
        foreach (var seed in new[] { 1, 7, 2026 })
        {
            var random = new Random(seed);
            var (product, _, _) = await ProductWithLotsAsync($"AZAR-{seed}", unitsPerPackage: seed % 2 == 0 ? 1 : 6);
            var sales = new List<int>();
            for (int step = 0; step < 25; step++)
            {
                int op = random.Next(6);
                switch (op)
                {
                    case 0:
                    case 1:
                        var current = await ProductAsync(product.Id);
                        bool pieces = current.HasMultipleUnitsPerPackage && random.Next(2) == 0;
                        var sale = await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, random.Next(1, 9), 5m, IsIndividualUnits: pieces) }, "Paciente", "Efectivo");
                        sales.Add(sale!.Id);
                        break;
                    case 2:
                        await _service.RegisterQuickConsultationUsageAsync(product.Id, random.Next(1, 4), null);
                        break;
                    case 3:
                        await _service.ReceiveBatchAsync(new ProductBatch
                        {
                            ProductId = product.Id, BatchNumber = $"AZAR-{seed}-{step}", ExpirationDate = DateTime.Today.AddDays(random.Next(10, 400)), Quantity = random.Next(1, 4)
                        });
                        break;
                    case 4:
                        if (sales.Count > 0)
                        {
                            int index = random.Next(sales.Count);
                            await _service.CancelSaleAsync(sales[index]);
                            sales.RemoveAt(index);
                        }
                        break;
                    default:
                        await CountAsync(product.Id, random.Next(-2, 6));
                        break;
                }
                await AssertLedgerCleanAsync($"Semilla {seed}, paso {step} (operación {op})");
                var now = await ProductAsync(product.Id);
                Assert.AreEqual(now.CurrentStock < 0 || now.Batches.Any(b => !b.IsClosed && b.Quantity < 0), now.NeedsInventoryReview);
                Assert.IsTrue(now.Batches.All(b => b.Quantity >= 0), "Una salida nunca deja un lote en negativo");
                Assert.IsTrue(now.OpenedUnitsRemaining >= 0 && now.OpenedUnitsRemaining < now.EffectiveUnitsPerPackage);
            }
        }
    }

    // ================================================================== pantallas

    [TestMethod]
    public async Task Checkout_ChargesWithoutStock_AndWarnsAfterCharging()
    {
        var (product, _, _) = await ProductWithLotsAsync("NEG-COBRO");
        var notices = new List<(string Text, bool Ok)>();
        SaleReceipt? receipt = null;
        var checkout = new CheckoutViewModel(_service)
        {
            Notify = (text, ok) => notices.Add((text, ok)),
            Completed = r => { receipt = r; return Task.CompletedTask; }
        };

        Assert.IsTrue(checkout.AddProduct(await ProductAsync(product.Id), 5));
        Assert.AreEqual(5, checkout.Cart.Single().Quantity, "Sin tope");
        const string Expected = "Existencias insuficientes: Producto NEG-COBRO (quedan 3, se cobran 5). Revise el inventario.";
        Assert.AreEqual((Expected, false), notices.Last(), "Avisa al agregar");
        Assert.AreEqual(Expected, checkout.StockWarning, "Y junto al botón de cobrar");
        Assert.IsTrue(checkout.CanCharge, "El aviso no deshabilita el cobro");

        await checkout.ChargeAsync();
        Assert.IsNotNull(receipt);
        Assert.AreEqual(Expected, receipt.StockWarning);
        Assert.AreEqual((Expected, false), notices.Last(), "Y después de cobrar");
        Assert.IsFalse(checkout.HasItems);
        Assert.AreEqual(-2, (await ProductAsync(product.Id)).CurrentStock);
        await AssertLedgerCleanAsync("Cobro sin existencias");
    }

    [TestMethod]
    public void StockReviewRules_Texts()
    {
        Assert.AreEqual("−3 · revisar inventario", StockReviewRules.ReviewBadge(-3));
        Assert.AreEqual("−2147483648", StockReviewRules.Number(int.MinValue));
        Assert.IsNull(StockReviewRules.WarningText(Array.Empty<StockShortage>()));
        Assert.AreEqual("Existencias insuficientes: Paracetamol (quedan 2, se cobran 5); Jeringa (quedan −6 pzas, se cobran 5 pzas). Revise el inventario.",
            StockReviewRules.WarningText(new[] { new StockShortage("Paracetamol", 2, 5, false), new StockShortage("Jeringa", -6, 5, true) }));
        Assert.AreEqual(1, CheckoutRules.ClampQuantity(0, false, 5));
        Assert.AreEqual(50, CheckoutRules.ClampQuantity(50, false, 5), "Las existencias ya no topan");
    }
}
