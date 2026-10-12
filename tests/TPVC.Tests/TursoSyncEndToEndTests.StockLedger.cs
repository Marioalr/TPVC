using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.19, Kardex completo con dos equipos: las devoluciones deterministas (cancelar el mismo ticket en los dos deja una
/// sola), la venta cuyos movimientos no han llegado y el movimiento que llega después de cerrar su lote.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    /// <summary>Un producto con un lote que vence pronto (3) y otro después (10), y una venta de 5 que sale de los dos.</summary>
    private static async Task<(Product Product, ProductBatch Soon, ProductBatch Later, Sale Sale)> SaleFromTwoLotsAsync(Device device)
    {
        var category = (await device.Inventory.GetCategoriesAsync()).First();
        var product = new Product { Code = "LIBRO-1", Name = "Producto del libro", CategoryId = category.Id, Price = 10m };
        await device.Inventory.SaveProductAsync(product);
        var soon = new ProductBatch { ProductId = product.Id, BatchNumber = "L-PRONTO", ExpirationDate = DateTime.Today.AddMonths(2), Quantity = 3 };
        var later = new ProductBatch { ProductId = product.Id, BatchNumber = "L-DESPUES", ExpirationDate = DateTime.Today.AddMonths(12), Quantity = 10 };
        await device.Inventory.ReceiveBatchAsync(soon);
        await device.Inventory.ReceiveBatchAsync(later);
        var sale = await device.Inventory.RegisterSaleAsync(new[] { (product.Id, 5, 10m) }, "Paciente", "Efectivo");
        return (product, soon, later, sale!);
    }

    private static async Task<int> LocalIdAsync<T>(Device device, Guid syncId) where T : class, ISyncEntity
    {
        using var db = device.Factory.CreateDbContext();
        return (int)db.Entry(await db.Set<T>().AsNoTracking().SingleAsync(e => e.SyncId == syncId)).Property("Id").CurrentValue!;
    }

    private static void ResetClockOffset(Device device)
    {
        using var db = device.Factory.CreateDbContext();
        DbMeta.Set(db, DbMeta.ClockOffsetMs, "0");
        SyncClock.Default.Remember(db, TimeSpan.Zero);
    }

    private static async Task<StockLedgerReport> VerifyLedgerAsync(Device device)
    {
        using var db = device.Factory.CreateDbContext();
        return await StockLedger.VerifyAsync(db);
    }

    [TestMethod]
    public async Task CancellingTheSameTicketOnTwoDevices_LeavesOneReturnPerExit()
    {
        var pc = CreateDevice("pc", seed: true);
        var (product, soon, later, sale) = await SaleFromTwoLotsAsync(pc);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Los dos cancelan el mismo ticket antes de sincronizar
        Assert.IsTrue(await pc.Inventory.CancelSaleAsync(sale.Id, "Devolución en la PC"));
        Assert.IsTrue(await tablet.Inventory.CancelSaleAsync(await LocalIdAsync<Sale>(tablet, sale.SyncId), "Devolución en la tablet"));

        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Las dos escribieron las mismas filas (un SyncId por salida): la nube conserva una devolución por salida
        Assert.AreEqual(2L, CloudScalar($"SELECT COUNT(*) FROM InventoryMovements WHERE MovementType = {(int)MovementType.EntradaDevolucion}"));
        foreach (var device in new[] { pc, tablet })
        {
            using var db = device.Factory.CreateDbContext();
            var productId = db.Products.Single(p => p.SyncId == product.SyncId).Id;
            var exits = db.InventoryMovements.Where(m => m.ProductId == productId && m.MovementType == MovementType.SalidaVenta).ToList();
            var returns = db.InventoryMovements.Where(m => m.ProductId == productId && m.MovementType == MovementType.EntradaDevolucion).ToList();
            Assert.HasCount(2, exits);
            CollectionAssert.AreEquivalent(exits.Select(e => StockLedger.ReturnSyncId(e.SyncId)).ToList(), returns.Select(r => r.SyncId).ToList());
            Assert.AreEqual(13, db.Products.Single(p => p.Id == productId).CurrentStock, "Las piezas se devolvieron una sola vez");
            Assert.AreEqual(3, db.ProductBatches.Single(b => b.SyncId == soon.SyncId).Quantity);
            Assert.AreEqual(10, db.ProductBatches.Single(b => b.SyncId == later.SyncId).Quantity);
            Assert.IsTrue(db.Sales.Single(s => s.SyncId == sale.SyncId).IsCancelled);
            Assert.IsTrue((await VerifyLedgerAsync(device)).IsClean);
        }
    }

    [TestMethod]
    public async Task ReturnsThatAlreadyArrived_CountTheCancellationAsDone()
    {
        var pc = CreateDevice("pc", seed: true);
        var (product, _, _, sale) = await SaleFromTwoLotsAsync(pc);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La PC cancela; a la tablet le llegan las devoluciones y las existencias, pero todavía no el ticket cancelado
        Assert.IsTrue(await pc.Inventory.CancelSaleAsync(sale.Id));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        HoldCloudRow("Sales", sale.SyncId);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        int tabletSaleId = await LocalIdAsync<Sale>(tablet, sale.SyncId);
        int tabletProductId = await LocalIdAsync<Product>(tablet, product.SyncId);
        Assert.AreEqual(13, (await tablet.Inventory.GetProductByIdAsync(tabletProductId))!.CurrentStock);

        // El médico la cancela también en la tablet: cuenta como hecha y no se devuelve dos veces
        Assert.IsTrue(await tablet.Inventory.CancelSaleAsync(tabletSaleId, "Ya estaba"));
        Assert.AreEqual(13, (await tablet.Inventory.GetProductByIdAsync(tabletProductId))!.CurrentStock);
        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.AreEqual(2, db.InventoryMovements.Count(m => m.MovementType == MovementType.EntradaDevolucion));
            Assert.IsTrue(db.Sales.Single(s => s.Id == tabletSaleId).IsCancelled);
        }
        Assert.IsTrue((await VerifyLedgerAsync(tablet)).IsClean);

        ReleaseHeldRow("Sales");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(2L, CloudScalar($"SELECT COUNT(*) FROM InventoryMovements WHERE MovementType = {(int)MovementType.EntradaDevolucion}"));
    }

    [TestMethod]
    public async Task SaleWhoseMovementsHaveNotArrived_IsNotCancelled()
    {
        var pc = CreateDevice("pc", seed: true);
        var (product, _, later, sale) = await SaleFromTwoLotsAsync(pc);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // A la tablet le llega el ticket con una sola de sus dos salidas
        var laterExit = Guid.Parse((string)CloudScalar(
            $"SELECT SyncId FROM InventoryMovements WHERE MovementType = {(int)MovementType.SalidaVenta} AND BatchSyncId = '{TursoSyncService.FormatGuid(later.SyncId)}'")!);
        HoldCloudRow("InventoryMovements", laterExit);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        int tabletSaleId = await LocalIdAsync<Sale>(tablet, sale.SyncId);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => tablet.Inventory.CancelSaleAsync(tabletSaleId));
        StringAssert.Contains(ex.Message, "Sincronice primero");
        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.IsFalse(db.Sales.Single(s => s.Id == tabletSaleId).IsCancelled);
            Assert.IsFalse(db.InventoryMovements.Any(m => m.MovementType == MovementType.EntradaDevolucion));
        }

        // Ya completa, se cancela
        ReleaseHeldRow("InventoryMovements");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue(await tablet.Inventory.CancelSaleAsync(tabletSaleId));
        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.AreEqual(2, db.InventoryMovements.Count(m => m.MovementType == MovementType.EntradaDevolucion));
            Assert.AreEqual(13, db.Products.Single(p => p.SyncId == product.SyncId).CurrentStock);
        }
    }

    [TestMethod]
    public async Task MovementThatArrivesAfterItsBatchWasClosed_StaysInThatBatch()
    {
        var pc = CreateDevice("pc", seed: true);
        var category = (await pc.Inventory.GetCategoriesAsync()).First();
        var product = new Product { Code = "LIBRO-2", Name = "Producto del lote cerrado", CategoryId = category.Id, Price = 10m };
        await pc.Inventory.SaveProductAsync(product);
        var soon = new ProductBatch { ProductId = product.Id, BatchNumber = "L-CIERRA", ExpirationDate = DateTime.Today.AddMonths(2), Quantity = 3 };
        await pc.Inventory.ReceiveBatchAsync(soon);
        await pc.Inventory.ReceiveBatchAsync(new ProductBatch { ProductId = product.Id, BatchNumber = "L-OTRO", ExpirationDate = DateTime.Today.AddMonths(12), Quantity = 10 });
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La tablet vende 2 del lote que vence pronto sin sincronizar; después la PC cierra ese lote. Hasta el paso 8.20 los
        // dos cambian la fila del lote (su cantidad viaja como valor) y gana la marca más nueva: sin desfase medido, la del
        // cierre, que va después (con el desfase de cada uno, medido contra la misma hora, podrían quedar al revés)
        ResetClockOffset(pc);
        ResetClockOffset(tablet);
        await tablet.Inventory.RegisterSaleAsync(new[] { (await LocalIdAsync<Product>(tablet, product.SyncId), 2, 10m) }, null, null);
        await pc.Inventory.DeleteBatchAsync(soon.Id);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        using var db = pc.Factory.CreateDbContext();
        var late = db.InventoryMovements.Single(m => m.ProductId == product.Id && m.MovementType == MovementType.SalidaVenta);
        Assert.AreEqual(soon.Id, late.BatchId, "La venta tardía sigue en su lote: antes, con el lote borrado, quedaba sin lote");
        var batch = db.ProductBatches.Single(b => b.Id == soon.Id);
        Assert.IsNotNull(batch.ClosedAtUtc);

        // Su libro queda en −2 (en el paso 8.20 se verá en negativo); hoy la verificación lo reporta
        var entry = (await VerifyLedgerAsync(pc)).NewDifferences.Single(e => e.BatchId == soon.Id);
        Assert.AreEqual(-2, entry.Ledger);
        Assert.IsTrue(entry.BatchClosed);
    }

    /// <summary>Un producto con un solo lote de 5 piezas.</summary>
    private static async Task<(Product Product, ProductBatch Batch)> ProductWithOneLotAsync(Device device, string code)
    {
        var category = (await device.Inventory.GetCategoriesAsync()).First();
        var product = new Product { Code = code, Name = $"Producto {code}", CategoryId = category.Id, Price = 10m };
        await device.Inventory.SaveProductAsync(product);
        var batch = new ProductBatch { ProductId = product.Id, BatchNumber = $"{code}-L", ExpirationDate = DateTime.Today.AddMonths(6), Quantity = 5 };
        await device.Inventory.ReceiveBatchAsync(batch);
        return (product, batch);
    }

    [TestMethod]
    public async Task ClosingTheSameLotOnTwoDevices_LeavesOneCloseMovement()
    {
        var pc = CreateDevice("pc", seed: true);
        var (product, batch) = await ProductWithOneLotAsync(pc, "CIERRE-2");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Los dos eliminan el mismo lote antes de sincronizar: escriben el mismo movimiento (su SyncId sale del lote)
        await pc.Inventory.DeleteBatchAsync(batch.Id);
        await tablet.Inventory.DeleteBatchAsync(await LocalIdAsync<ProductBatch>(tablet, batch.SyncId));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        var lot = TursoSyncService.FormatGuid(batch.SyncId);
        Assert.AreEqual(1L, CloudScalar($"SELECT COUNT(*) FROM InventoryMovements WHERE BatchSyncId = '{lot}' AND MovementType = {(int)MovementType.AjusteInventario}"));
        Assert.AreEqual(TursoSyncService.FormatGuid(StockLedger.CloseSyncId(batch.SyncId)),
            CloudScalar($"SELECT SyncId FROM InventoryMovements WHERE BatchSyncId = '{lot}' AND MovementType = {(int)MovementType.AjusteInventario}"));
        foreach (var device in new[] { pc, tablet })
        {
            var report = await VerifyLedgerAsync(device);
            Assert.IsTrue(report.IsClean, report.Summary);
            Assert.IsFalse(report.Entries.Any(e => e.BatchSyncId == batch.SyncId), "El libro del lote quedó en 0, no en −5");
            using var db = device.Factory.CreateDbContext();
            Assert.AreEqual(0, db.Products.Single(p => p.SyncId == product.SyncId).CurrentStock);
        }
    }

    [TestMethod]
    public async Task LotClosedBeforeTheFirstSyncAfterUpdating_KeepsItsClosedAtUtc()
    {
        var pc = CreateDevice("pc", seed: true);
        var (_, batch) = await ProductWithOneLotAsync(pc, "CIERRE-3");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Como una PC que se acaba de actualizar: bajó ProductBatches sin ClosedAtUtc, y en la nube la columna está en nulo
        // (así la deja el ALTER). Cierra un lote antes de su primera sincronización
        using (var db = pc.Factory.CreateDbContext())
        {
            var key = DbMeta.PullColumnsPrefix + "ProductBatches";
            DbMeta.Set(db, key, string.Join(",", DbMeta.Get(db, key)!.Split(',').Where(c => c != nameof(ProductBatch.ClosedAtUtc))));
        }
        await pc.Inventory.DeleteBatchAsync(batch.Id);

        var result = await pc.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        using (var db = pc.Factory.CreateDbContext()) Assert.IsNotNull(db.ProductBatches.Single(b => b.Id == batch.Id).ClosedAtUtc);
        Assert.IsTrue(CloudScalar($"SELECT ClosedAtUtc FROM ProductBatches WHERE SyncId = '{TursoSyncService.FormatGuid(batch.SyncId)}'") is string,
            "El nulo de la nube no pisó el cierre");
    }

    [TestMethod]
    public async Task MovementParkedWhileDownloadingNewColumns_IsAppliedWhenItsSaleItemArrives()
    {
        var pc = CreateDevice("pc", seed: true);
        var (product, _, _, sale) = await SaleFromTwoLotsAsync(pc);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La tablet como una PC anterior que se actualiza: bajó las salidas de la venta sin Delta ni renglón (con la misma
        // marca que en la nube), y el renglón todavía no le llega
        var item = sale.Items.Single();
        using (var db = tablet.Factory.CreateDbContext())
        {
            int itemId = db.SaleItems.Single(i => i.SyncId == item.SyncId).Id;
            db.Database.ExecuteSqlRaw($"INSERT OR IGNORE INTO \"{SyncChangeLog.ApplyStateTable}\" (\"Applying\") VALUES (1);");
            db.Database.ExecuteSqlRaw("UPDATE \"InventoryMovements\" SET \"Delta\" = 0, \"SaleItemId\" = NULL WHERE \"SaleItemId\" = {0}", itemId);
            db.Database.ExecuteSqlRaw("DELETE FROM \"SaleItems\" WHERE \"Id\" = {0}", itemId);
            db.Database.ExecuteSqlRaw($"DELETE FROM \"{SyncChangeLog.ApplyStateTable}\";");
            var key = DbMeta.PullColumnsPrefix + "InventoryMovements";
            DbMeta.Set(db, key, string.Join(",", DbMeta.Get(db, key)!.Split(',').Where(c => c is not ("Delta" or "SaleItemSyncId"))));
        }
        HoldCloudRow("SaleItems", item.SyncId);

        var first = await tablet.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        Assert.AreEqual(2, first.ParkedRows, "Las dos salidas esperan su renglón, también después de releerlas y reintentarlas");

        ReleaseHeldRow("SaleItems");
        var second = await tablet.Sync.SyncAsync();
        Assert.IsTrue(second.Success, second.ErrorMessage);
        Assert.AreEqual(0, second.ParkedRows);
        int tabletSaleId = await LocalIdAsync<Sale>(tablet, sale.SyncId);
        using (var db = tablet.Factory.CreateDbContext())
        {
            var productId = db.Products.Single(p => p.SyncId == product.SyncId).Id;
            var exits = db.InventoryMovements.Where(m => m.ProductId == productId && m.MovementType == MovementType.SalidaVenta).ToList();
            Assert.HasCount(2, exits);
            Assert.IsTrue(exits.All(m => m.Delta < 0 && m.SaleItemId != null), "Las salidas tienen su Delta y su renglón");
        }
        Assert.IsTrue(await tablet.Inventory.CancelSaleAsync(tabletSaleId), "Ya no pide sincronizar primero");
    }

    [TestMethod]
    public async Task SaleOfTwoLinesFromAnotherDevice_ShowsOneKardexRowPerLine()
    {
        var pc = CreateDevice("pc", seed: true);
        var (first, _) = await ProductWithOneLotAsync(pc, "LINEA-A");
        await pc.Inventory.ReceiveBatchAsync(new ProductBatch { ProductId = first.Id, BatchNumber = "LINEA-A-2", ExpirationDate = DateTime.Today.AddMonths(9), Quantity = 5 });
        var (second, _) = await ProductWithOneLotAsync(pc, "LINEA-B");
        await pc.Inventory.ReceiveBatchAsync(new ProductBatch { ProductId = second.Id, BatchNumber = "LINEA-B-2", ExpirationDate = DateTime.Today.AddMonths(9), Quantity = 5 });
        var sale = await pc.Inventory.RegisterSaleAsync(new[] { (first.Id, 7, 10m), (second.Id, 8, 10m) }, null, null);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue(await pc.Inventory.CancelSaleAsync(sale!.Id));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // En la tablet las filas llegan en el orden de la nube (por SyncId), no en el que se escribieron
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        foreach (var device in new[] { pc, tablet })
        {
            var kardex = await device.Inventory.GetKardexAsync(20);
            foreach (var type in new[] { MovementType.SalidaVenta, MovementType.EntradaDevolucion })
            {
                CollectionAssert.AreEquivalent(new[] { 7, 8 }, kardex.Where(m => m.MovementType == type).Select(m => m.Quantity).ToArray(),
                    $"{type}: un renglón por renglón del ticket");
            }
        }
    }
}
