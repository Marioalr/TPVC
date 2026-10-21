using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Unidades por paquete (revisión 17): cancelar un ticket de piezas sueltas regresa exactamente sus piezas (y los paquetes que
/// se completan, con su devolución y su lote), el renglón guarda si vendió piezas y cambiar las piezas por paquete ajusta el
/// paquete abierto en cada camino.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UnitsPerPackageTests
{
    private string _dbPath = string.Empty;
    private DbContextOptions<AppDbContext> _options = null!;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_paquetes_{Guid.NewGuid():N}.db");
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

    /// <summary>Una caja de 10 piezas a $100 con un lote de 5 cajas: 50 piezas.</summary>
    private async Task<(Product Product, ProductBatch Lot)> BoxOfTenAsync(string code = "PAQ-10")
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = code, Name = $"Gasas {code}", CategoryId = category.Id, Price = 100m, UnitsPerPackage = 10 };
        await _service.SaveProductAsync(product);
        var lot = new ProductBatch { ProductId = product.Id, BatchNumber = $"{code}-L1", ExpirationDate = DateTime.Today.AddYears(1), Quantity = 5 };
        await _service.ReceiveBatchAsync(lot, unitsPerPackage: 10);
        return (await ProductAsync(product.Id), lot);
    }

    private async Task<Product> ProductAsync(int id) => (await _service.GetProductsAsync()).Single(p => p.Id == id);

    private async Task<int> LotQuantityAsync(int lotId)
    {
        await using var db = new AppDbContext(_options);
        return (await db.ProductBatches.AsNoTracking().SingleAsync(b => b.Id == lotId)).Quantity;
    }

    private async Task<Sale> SellPiecesAsync(Product product, int pieces) =>
        (await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, pieces, product.IndividualUnitPrice, IsIndividualUnits: true) },
            "Público General", "Efectivo"))!;

    private async Task AssertLedgerCleanAsync()
    {
        await using var db = new AppDbContext(_options);
        var report = await StockLedger.VerifyAsync(db);
        Assert.IsTrue(report.IsClean, "El libro de existencias y los lotes deben cuadrar");
    }

    private static int Pieces(Product p) => p.CurrentStock * p.EffectiveUnitsPerPackage + p.OpenedUnitsRemaining;

    [TestMethod]
    public async Task CancelOlderPieceSale_ReturnsExactlyItsPieces_AndConvertsFullPackagesBack()
    {
        var (product, lot) = await BoxOfTenAsync();
        int before = Pieces(product);
        Assert.AreEqual(50, before);

        var sale1 = await SellPiecesAsync(product, 3); // abre una caja: 4 cajas + 7 sueltas
        var sale2 = await SellPiecesAsync(product, 3); // del paquete abierto: 4 cajas + 4 sueltas
        product = await ProductAsync(product.Id);
        Assert.AreEqual(4, product.CurrentStock);
        Assert.AreEqual(4, product.OpenedUnitsRemaining);

        // Cancelar la primera: regresan sus 3 piezas (antes se perdían al recortar 4 + 3 − 10 a 0)
        await _service.CancelSaleAsync(sale1.Id);
        product = await ProductAsync(product.Id);
        Assert.AreEqual(before - 3, Pieces(product), "Solo falta lo que gastó el segundo ticket");
        Assert.AreEqual(4, product.CurrentStock);
        Assert.AreEqual(7, product.OpenedUnitsRemaining);
        Assert.AreEqual(4, await LotQuantityAsync(lot.Id));
        await AssertLedgerCleanAsync();

        // Cancelar la segunda: el paquete abierto se completa y vuelve a ser una caja, a su lote
        await _service.CancelSaleAsync(sale2.Id);
        product = await ProductAsync(product.Id);
        Assert.AreEqual(before, Pieces(product), "Igual que antes del primer ticket");
        Assert.AreEqual(5, product.CurrentStock);
        Assert.AreEqual(0, product.OpenedUnitsRemaining);
        Assert.AreEqual(5, await LotQuantityAsync(lot.Id));
        await AssertLedgerCleanAsync();

        await using var db = new AppDbContext(_options);
        var item2 = await db.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale2.Id);
        var back = await db.InventoryMovements.AsNoTracking().SingleAsync(m => m.SyncId == StockLedger.PieceReturnSyncId(item2.SyncId));
        Assert.AreEqual(MovementType.EntradaDevolucion, back.MovementType);
        Assert.AreEqual(1, back.Delta);
        Assert.AreEqual(lot.Id, back.BatchId);
        Assert.AreEqual(item2.Id, back.SaleItemId);
    }

    [TestMethod]
    public async Task CancelPieceSaleThatOpenedPackages_ReturnsThemToTheirLot()
    {
        var (product, lot) = await BoxOfTenAsync();
        var sale = await SellPiecesAsync(product, 13); // abre 2 cajas: 3 cajas + 7 sueltas
        product = await ProductAsync(product.Id);
        Assert.AreEqual(3, product.CurrentStock);
        Assert.AreEqual(7, product.OpenedUnitsRemaining);

        await _service.CancelSaleAsync(sale.Id);
        product = await ProductAsync(product.Id);
        Assert.AreEqual(5, product.CurrentStock);
        Assert.AreEqual(0, product.OpenedUnitsRemaining);
        Assert.AreEqual(5, await LotQuantityAsync(lot.Id));
        await AssertLedgerCleanAsync();
    }

    [TestMethod]
    public async Task SaleItem_RecordsIndividualUnits_AndADiscountedPackageIsCancelledAsAPackage()
    {
        var (product, _) = await BoxOfTenAsync();
        await SellPiecesAsync(product, 3); // 4 cajas + 7 sueltas
        // Una caja con descuento: antes, por costar menos que el precio, se cancelaba como piezas y se perdían las 7 sueltas
        var sale = (await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, 1, 80m) }, "Público General", "Efectivo"))!;

        await using (var db = new AppDbContext(_options))
        {
            Assert.IsFalse((await db.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id)).IsIndividualUnits);
        }
        Assert.AreEqual(3, (await ProductAsync(product.Id)).CurrentStock);

        await _service.CancelSaleAsync(sale.Id);
        product = await ProductAsync(product.Id);
        Assert.AreEqual(4, product.CurrentStock);
        Assert.AreEqual(7, product.OpenedUnitsRemaining);
        await AssertLedgerCleanAsync();
    }

    [TestMethod]
    public async Task PieceSale_IsCancelledAsPieces_AfterAPriceChange()
    {
        var (product, _) = await BoxOfTenAsync();
        var sale = await SellPiecesAsync(product, 3); // 3 piezas a $10
        await using (var db = new AppDbContext(_options))
        {
            Assert.IsTrue((await db.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id)).IsIndividualUnits);
        }

        // El paquete baja a $5: el renglón ($10) ya no cuesta menos que el paquete, pero se vendió por piezas
        product = await ProductAsync(product.Id);
        product.Price = 5m;
        await _service.SaveProductAsync(product, product.CurrentStock);

        await _service.CancelSaleAsync(sale.Id);
        product = await ProductAsync(product.Id);
        Assert.AreEqual(5, product.CurrentStock);
        Assert.AreEqual(0, product.OpenedUnitsRemaining);
        await AssertLedgerCleanAsync();
    }

    [TestMethod]
    public async Task OldSaleLineWithoutTheFlag_UsesThePriceRule()
    {
        var (product, _) = await BoxOfTenAsync();
        var sale = await SellPiecesAsync(product, 3);
        await using (var db = new AppDbContext(_options))
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE SaleItems SET IsIndividualUnits = NULL WHERE SaleId = {0}", sale.Id);
        }

        await _service.CancelSaleAsync(sale.Id);
        product = await ProductAsync(product.Id);
        Assert.AreEqual(5, product.CurrentStock);
        Assert.AreEqual(0, product.OpenedUnitsRemaining);
        await AssertLedgerCleanAsync();
    }

    [TestMethod]
    public async Task LoweringUnitsPerPackage_ClampsTheOpenPackage_InEveryPath()
    {
        var (product, _) = await BoxOfTenAsync();
        await SellPiecesAsync(product, 3); // 7 sueltas
        Assert.AreEqual(7, (await ProductAsync(product.Id)).OpenedUnitsRemaining);

        // Recibir un lote con 4 piezas por paquete: el paquete abierto no puede tener más de 3
        await _service.ReceiveBatchAsync(new ProductBatch
        {
            ProductId = product.Id, BatchNumber = "PAQ-10-L2", ExpirationDate = DateTime.Today.AddYears(1), Quantity = 1
        }, unitsPerPackage: 4);
        product = await ProductAsync(product.Id);
        Assert.AreEqual(4, product.UnitsPerPackage);
        Assert.AreEqual(3, product.OpenedUnitsRemaining);

        // Un pedido del proveedor con 2 piezas por paquete: a lo más 1
        var category = (await _service.GetCategoriesAsync()).First();
        await _service.ReceiveSupplierOrderAsync(new SupplierReceipt
        {
            Supplier = "LUIN",
            OrderNumber = "9001",
            CategoryIdForNewProducts = category.Id,
            Lines = new List<SupplierReceiptLine>
            {
                new() { ProductId = product.Id, Sku = "GAS-10", Description = "GASAS C/2", Quantity = 1, UnitCost = 20m, UnitsPerPackage = 2 }
            }
        });
        product = await ProductAsync(product.Id);
        Assert.AreEqual(2, product.UnitsPerPackage);
        Assert.AreEqual(1, product.OpenedUnitsRemaining);

        // Y a una pieza por paquete: ya no hay paquete abierto
        product.SetUnitsPerPackage(1);
        Assert.AreEqual(1, product.UnitsPerPackage);
        Assert.AreEqual(0, product.OpenedUnitsRemaining);
    }
}
