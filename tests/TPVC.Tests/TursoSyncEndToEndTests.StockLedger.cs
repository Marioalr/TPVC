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
}
