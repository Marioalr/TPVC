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
/// Paso 8.19, Kardex completo (A1, primera parte): un movimiento por lote con Delta, devoluciones deterministas a su
/// propio lote, lotes cerrados en lugar de borrados, el diálogo del producto con OriginalStock y el libro de existencias
/// que corre al lado de lo guardado (StockLedger.VerifyAsync). Las pruebas con dos equipos están en
/// TursoSyncEndToEndTests.StockLedger.cs.
/// </summary>
[TestClass]
[DoNotParallelize]
public class StockLedgerTests
{
    private string _dbPath = string.Empty;
    private DbContextOptions<AppDbContext> _options = null!;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_libro_{Guid.NewGuid():N}.db");
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

    private async Task<StockLedgerReport> VerifyAsync()
    {
        await using var db = NewContext();
        return await StockLedger.VerifyAsync(db);
    }

    /// <summary>Un producto con 2 piezas sin lote, un lote que vence pronto (3) y otro después (5): 10 en total.</summary>
    private async Task<(Product Product, ProductBatch Soon, ProductBatch Later)> ProductWithTwoLotsAsync(string code = "LIB-1")
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = code, Name = $"Producto {code}", CategoryId = category.Id, Price = 10m, CurrentStock = 2 };
        await _service.SaveProductAsync(product);
        var soon = new ProductBatch { ProductId = product.Id, BatchNumber = $"{code}-PRONTO", ExpirationDate = DateTime.Today.AddMonths(2), Quantity = 3 };
        var later = new ProductBatch { ProductId = product.Id, BatchNumber = $"{code}-DESPUES", ExpirationDate = DateTime.Today.AddMonths(12), Quantity = 5 };
        await _service.ReceiveBatchAsync(soon);
        await _service.ReceiveBatchAsync(later);
        return (product, soon, later);
    }

    private async Task<List<InventoryMovement>> MovementsOfAsync(int productId)
    {
        await using var db = NewContext();
        return await db.InventoryMovements.AsNoTracking().Where(m => m.ProductId == productId).OrderBy(m => m.Id).ToListAsync();
    }

    // ================================================================== libro y foto

    [TestMethod]
    public async Task NewDatabase_WithDemoData_HasNoDifferences()
    {
        // Los productos demo dejan un movimiento por lote (antes uno sin lote por todo): el libro cuadra desde el principio
        var report = await VerifyAsync();
        Assert.IsTrue(report.IsClean, report.Summary);
        Assert.IsEmpty(report.Entries, report.Summary);
        Assert.IsGreaterThan(0, report.BatchCount);

        using var db = NewContext();
        Assert.IsNotNull(DbMeta.Get(db, DbMeta.StockLedgerBaseline), "La foto se toma al migrar");
        Assert.IsTrue(db.InventoryMovements.All(m => m.Delta != 0), "Todo movimiento nuevo lleva Delta");
    }

    [TestMethod]
    public void EffectiveDelta_FollowsTheOldRule_InLegacyRows()
    {
        Assert.AreEqual(-4, StockLedger.EffectiveDelta(-4, MovementType.EntradaCompra, 4, 0, 0), "Con Delta manda el Delta");
        Assert.AreEqual(5, StockLedger.EffectiveDelta(0, MovementType.EntradaCompra, 5, 0, 5));
        Assert.AreEqual(5, StockLedger.EffectiveDelta(0, MovementType.EntradaAjuste, 5, 0, 5));
        Assert.AreEqual(5, StockLedger.EffectiveDelta(0, MovementType.EntradaDevolucion, 5, 0, 5));
        Assert.AreEqual(-5, StockLedger.EffectiveDelta(0, MovementType.SalidaVenta, 5, 5, 0));
        Assert.AreEqual(-5, StockLedger.EffectiveDelta(0, MovementType.SalidaConsulta, 5, 5, 0));
        Assert.AreEqual(3, StockLedger.EffectiveDelta(0, MovementType.AjusteInventario, 3, 2, 5), "Un ajuste que subió entra");
        Assert.AreEqual(-3, StockLedger.EffectiveDelta(0, MovementType.AjusteInventario, 3, 5, 2), "Un ajuste que bajó sale");
        Assert.IsTrue(new InventoryMovement { MovementType = MovementType.AjusteInventario, Delta = 2, StockBefore = 9, StockAfter = 1 }.IsEntry,
            "El Kardex muestra el signo del Delta");
    }

    [TestMethod]
    public async Task Verify_ReportsOnlyWhatChangedSinceTheBaseline_WithoutChangingAnything()
    {
        var (product, soon, _) = await ProductWithTwoLotsAsync();

        // Una diferencia de antes del Kardex completo: queda en la foto y no cuenta como nueva
        await using (var db = NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"ProductBatches\" SET \"Quantity\" = \"Quantity\" + 1 WHERE \"Id\" = {0}", soon.Id);
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Products\" SET \"CurrentStock\" = \"CurrentStock\" + 1 WHERE \"Id\" = {0}", product.Id);
            await StockLedger.RetakeBaselineAsync(db);
        }
        var before = await VerifyAsync();
        Assert.IsTrue(before.IsClean, before.Summary);
        Assert.AreEqual(2, before.PreviousDifferenceCount, "El lote y el producto");
        Assert.IsTrue(before.Entries.All(e => e.Difference == 1));

        // Una existencia que cambia sin su movimiento (lo que pierde una existencia que viaja como valor): sí es nueva
        await using (var db = NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Products\" SET \"CurrentStock\" = \"CurrentStock\" - 2 WHERE \"Id\" = {0}", product.Id);
        }
        var after = await VerifyAsync();
        Assert.IsFalse(after.IsClean);
        var drift = after.NewDifferences.Single();
        Assert.IsNull(drift.BatchId, "Lo que no cuadra es el producto; sus lotes sí");
        Assert.AreEqual(-2, drift.NewDifference);
        StringAssert.Contains(after.Summary, "1 diferencias nuevas");
        StringAssert.Contains(after.Summary, "Producto LIB-1 (total): guardado 9, libro 10 (antes +1)");

        // Verificar no cambia nada
        Assert.AreEqual(9, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock);
    }

    // ================================================================== salidas por lote

    [TestMethod]
    public async Task SaleAcrossLots_LeavesOneMovementPerLot_WithItsSaleItem_AndOneKardexRow()
    {
        var (product, soon, later) = await ProductWithTwoLotsAsync();
        int operationsBefore = (await _service.GetDashboardMetricsAsync()).MovementsToday;

        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 9, 10m) }, "Paciente", "Efectivo");
        Assert.AreEqual(Sale.CurrentLedgerVersion, sale!.LedgerVersion);

        var exits = (await MovementsOfAsync(product.Id)).Where(m => m.MovementType == MovementType.SalidaVenta).ToList();
        Assert.HasCount(3, exits, "Un movimiento por lote y otro sin lote para lo que no alcanzó");
        var itemId = sale.Items.Single().Id;
        Assert.IsTrue(exits.All(m => m.SaleItemId == itemId));
        CollectionAssert.AreEqual(new int?[] { soon.Id, later.Id, null }, exits.Select(m => m.BatchId).ToArray(), "FEFO: primero el que vence antes");
        CollectionAssert.AreEqual(new[] { -3, -5, -1 }, exits.Select(m => m.Delta).ToArray());
        CollectionAssert.AreEqual(new[] { 3, 5, 1 }, exits.Select(m => m.Quantity).ToArray());
        CollectionAssert.AreEqual(new[] { 10, 7, 2 }, exits.Select(m => m.StockBefore).ToArray(), "La existencia baja renglón por renglón");
        CollectionAssert.AreEqual(new[] { 7, 2, 1 }, exits.Select(m => m.StockAfter).ToArray());

        // En pantalla sigue siendo una operación: un renglón en el Kardex y uno más en "movimientos hoy"
        var row = (await _service.GetKardexAsync(10)).First();
        Assert.AreEqual(product.Id, row.ProductId);
        Assert.AreEqual(9, row.Quantity);
        Assert.AreEqual(10, row.StockBefore);
        Assert.AreEqual(1, row.StockAfter);
        Assert.IsFalse(row.IsEntry);
        Assert.AreEqual(operationsBefore + 1, (await _service.GetDashboardMetricsAsync()).MovementsToday);

        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    [TestMethod]
    public async Task TwoLinesOfTheSameProduct_StayTwoKardexRows()
    {
        var (product, _, _) = await ProductWithTwoLotsAsync();
        await _service.RegisterSaleAsync(new[] { new SaleLineRequest(product.Id, 4, 10m), new SaleLineRequest(product.Id, 2, 8m) }, null, null);

        var rows = (await _service.GetKardexAsync(10)).Where(m => m.MovementType == MovementType.SalidaVenta).ToList();
        CollectionAssert.AreEquivalent(new[] { 4, 2 }, rows.Select(r => r.Quantity).ToArray(), "Cada renglón del ticket es su propia operación");
        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    [TestMethod]
    public void KardexRows_NeverMergeLegacyRows()
    {
        var date = new DateTime(2026, 10, 12, 10, 0, 0);
        InventoryMovement Row(int delta) => new() { ProductId = 1, MovementType = MovementType.SalidaVenta, Quantity = 2, Delta = delta, Date = date, Reason = "Venta POS X - " };

        Assert.HasCount(2, KardexRows.Group(new[] { Row(0), Row(0) }), "Dos filas anteriores con la misma hora siguen siendo dos");
        Assert.HasCount(1, KardexRows.Group(new[] { Row(-2), Row(-2) }));
        Assert.HasCount(2, KardexRows.Group(new[] { Row(-2), Row(0) }));
    }

    [TestMethod]
    public async Task OutgoingMovement_WithAChosenLot_StartsThere_AndSplitsByLot()
    {
        var (product, soon, later) = await ProductWithTwoLotsAsync();
        await _service.RegisterMovementAsync(new InventoryMovement
        {
            ProductId = product.Id, BatchId = later.Id, MovementType = MovementType.SalidaMerma, Quantity = 7, Reason = "Se mojó", Responsible = "Dr."
        });

        var exits = (await MovementsOfAsync(product.Id)).Where(m => m.MovementType == MovementType.SalidaMerma).ToList();
        CollectionAssert.AreEqual(new int?[] { later.Id, soon.Id }, exits.Select(m => m.BatchId).ToArray(), "Primero el lote elegido, luego FEFO");
        CollectionAssert.AreEqual(new[] { -5, -2 }, exits.Select(m => m.Delta).ToArray());
        Assert.IsTrue(exits.All(m => m.Reason == "Se mojó"));
        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    // ================================================================== cancelar

    [TestMethod]
    public async Task CancelSale_ReturnsEachExitToItsOwnLot_WithDeterministicSyncIds()
    {
        var (product, soon, later) = await ProductWithTwoLotsAsync();
        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 9, 10m) }, null, null);
        var exits = (await MovementsOfAsync(product.Id)).Where(m => m.MovementType == MovementType.SalidaVenta).ToList();

        Assert.IsTrue(await _service.CancelSaleAsync(sale!.Id, "Error de cobro"));

        var returns = (await MovementsOfAsync(product.Id)).Where(m => m.MovementType == MovementType.EntradaDevolucion).ToList();
        Assert.HasCount(3, returns);
        foreach (var exit in exits)
        {
            var back = returns.Single(r => r.SyncId == StockLedger.ReturnSyncId(exit.SyncId));
            Assert.AreEqual(exit.BatchId, back.BatchId, "Cada salida vuelve a su propio lote");
            Assert.AreEqual(-exit.Delta, back.Delta);
            Assert.AreEqual(exit.SaleItemId, back.SaleItemId);
        }

        var batches = await _service.GetBatchesAsync(product.Id);
        Assert.AreEqual(3, batches.Single(b => b.Id == soon.Id).Quantity);
        Assert.IsFalse(batches.Single(b => b.Id == soon.Id).IsDepleted, "Un lote agotado vuelve a contar para FEFO");
        Assert.AreEqual(5, batches.Single(b => b.Id == later.Id).Quantity);
        Assert.AreEqual(10, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock);
        Assert.IsTrue((await VerifyAsync()).IsClean);

        // En el Kardex, la devolución es una operación
        var row = (await _service.GetKardexAsync(5)).First();
        Assert.AreEqual(MovementType.EntradaDevolucion, row.MovementType);
        Assert.AreEqual(9, row.Quantity);
        Assert.IsTrue(row.IsEntry);
    }

    [TestMethod]
    public async Task CancelSale_WithIncompleteMovements_IsRefused()
    {
        var (product, _, _) = await ProductWithTwoLotsAsync();
        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 9, 10m) }, null, null);

        // Una de sus salidas todavía no llega a este equipo
        await using (var db = NewContext())
        {
            var missing = await db.InventoryMovements.Where(m => m.SaleItemId != null).OrderBy(m => m.Id).LastAsync();
            db.InventoryMovements.Remove(missing);
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.CancelSaleAsync(sale!.Id));
        StringAssert.Contains(ex.Message, "Sincronice primero");
        Assert.IsFalse((await _service.GetSaleByFolioAsync(sale!.Folio))!.IsCancelled);
        Assert.IsFalse((await MovementsOfAsync(product.Id)).Any(m => m.MovementType == MovementType.EntradaDevolucion));
        Assert.AreEqual(1, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock, "No se devolvió nada");
    }

    [TestMethod]
    public async Task CancelSale_FromBeforeTheLedger_ReturnsByProductAndFolio_ToTheLotOfItsExit()
    {
        var (product, soon, later) = await ProductWithTwoLotsAsync();
        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 5, 10m) }, null, null);

        // Como la dejaba una versión anterior: un solo movimiento con el primer lote de FEFO, sin Delta ni renglón, y el ticket
        // regularizado después (8.17), así que el motivo dice su folio con letra
        const string newFolio = "TKT-20261012-0999";
        await using (var db = NewContext())
        {
            var exits = await db.InventoryMovements.Where(m => m.SaleItemId != null).OrderBy(m => m.Id).ToListAsync();
            Assert.HasCount(2, exits);
            db.InventoryMovements.Remove(exits[1]);
            exits[0].Quantity = 5;
            exits[0].Delta = 0;
            exits[0].SaleItemId = null;
            exits[0].StockAfter = exits[0].StockBefore - 5;
            var stored = await db.Sales.SingleAsync(s => s.Id == sale!.Id);
            stored.LedgerVersion = 0;
            stored.OriginalFolio = stored.Folio;
            stored.Folio = newFolio;
            await db.SaveChangesAsync();
            await StockLedger.RetakeBaselineAsync(db);
        }
        var legacy = await VerifyAsync();
        Assert.IsTrue(legacy.IsClean);
        Assert.AreEqual(2, legacy.PreviousDifferenceCount, "Los dos lotes no cuadran: así quedaban las ventas de varios lotes");

        Assert.IsTrue(await _service.CancelSaleAsync(sale!.Id));

        var back = (await MovementsOfAsync(product.Id)).Single(m => m.MovementType == MovementType.EntradaDevolucion);
        Assert.AreEqual(StockLedger.LegacyReturnSyncId(sale.SyncId, product.SyncId), back.SyncId);
        Assert.AreEqual(soon.Id, back.BatchId, "Al lote de su salida, encontrada por el folio con letra");
        Assert.AreEqual(5, back.Delta);
        Assert.IsNull(back.SaleItemId);
        StringAssert.Contains(back.Reason, newFolio);

        var batches = await _service.GetBatchesAsync(product.Id);
        Assert.AreEqual(5, batches.Single(b => b.Id == soon.Id).Quantity);
        Assert.AreEqual(3, batches.Single(b => b.Id == later.Id).Quantity);
        Assert.AreEqual(10, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock);
        Assert.IsTrue((await VerifyAsync()).IsClean, "Lo guardado y el libro se movieron igual");
    }

    [TestMethod]
    public async Task CancelSale_FromBeforeTheLedger_WithoutItsExits_ReturnsWithoutLot()
    {
        var (product, _, _) = await ProductWithTwoLotsAsync();
        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 2, 10m) }, null, null);
        await using (var db = NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"InventoryMovements\" SET \"Reason\" = 'Venta sin folio', \"Delta\" = 0, \"SaleItemId\" = NULL WHERE \"SaleItemId\" IS NOT NULL");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Sales\" SET \"LedgerVersion\" = 0 WHERE \"Id\" = {0}", sale!.Id);
            await StockLedger.RetakeBaselineAsync(db);
        }

        Assert.IsTrue(await _service.CancelSaleAsync(sale!.Id));
        var back = (await MovementsOfAsync(product.Id)).Single(m => m.MovementType == MovementType.EntradaDevolucion);
        Assert.IsNull(back.BatchId, "Sin salida que diga su folio no se adivina el lote");
        Assert.AreEqual(2, back.Delta);
        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    // ================================================================== lotes cerrados

    [TestMethod]
    public async Task DeleteBatch_ClosesIt_WithMinusItsLedger_AndFefoSkipsIt()
    {
        var (product, soon, later) = await ProductWithTwoLotsAsync();
        await _service.RegisterQuickConsultationUsageAsync(product.Id, 1, null);

        await _service.DeleteBatchAsync(soon.Id);

        var closing = (await MovementsOfAsync(product.Id)).Last();
        Assert.AreEqual(soon.Id, closing.BatchId);
        Assert.AreEqual(-2, closing.Delta, "−libro del lote: 3 que entraron menos 1 que salió");
        Assert.AreEqual(7, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock, "10 − 1 de la consulta − 2 que tenía el lote");
        var batches = await _service.GetBatchesAsync(product.Id);
        var closed = batches.Single(b => b.Id == soon.Id);
        Assert.IsTrue(closed.IsClosed);
        Assert.IsTrue(closed.IsDepleted, "Las pantallas filtran los agotados: el lote ya no se ve");

        // Cerrar otra vez no hace nada
        await _service.DeleteBatchAsync(soon.Id);
        Assert.AreEqual(7, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock);

        // FEFO ya no lo toma, aunque una devolución le regrese piezas
        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 6, 10m) }, null, null);
        var exits = (await MovementsOfAsync(product.Id)).Where(m => m.MovementType == MovementType.SalidaVenta).ToList();
        Assert.IsFalse(exits.Any(m => m.BatchId == soon.Id));
        Assert.IsTrue(await _service.CancelSaleAsync(sale!.Id));
        Assert.IsTrue((await _service.GetBatchesAsync(product.Id)).Single(b => b.Id == soon.Id).IsClosed);
        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    [TestMethod]
    public async Task DeleteBatch_OfALotFromBeforeTheLedger_MovesTheProductLedgerLikeItsStock()
    {
        // Como quedaba un lote guardado con SaveBatchAsync (sin movimiento): las piezas estaban en el libro, pero sin lote
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = "VIEJO-1", Name = "Producto con lotes viejos", CategoryId = category.Id, Price = 10m, CurrentStock = 15 };
        await _service.SaveProductAsync(product);
        int first, second;
        await using (var db = NewContext())
        {
            var a = new ProductBatch { ProductId = product.Id, BatchNumber = "V-1", ExpirationDate = DateTime.Today.AddMonths(3), Quantity = 5 };
            var b = new ProductBatch { ProductId = product.Id, BatchNumber = "V-2", ExpirationDate = DateTime.Today.AddMonths(9), Quantity = 10 };
            db.ProductBatches.AddRange(a, b);
            await db.SaveChangesAsync();
            (first, second) = (a.Id, b.Id);
            await StockLedger.RetakeBaselineAsync(db);
        }
        Assert.AreEqual(2, (await VerifyAsync()).PreviousDifferenceCount, "Los dos lotes guardan piezas que su libro no tiene");

        await _service.DeleteBatchAsync(first);

        // Su libro era 0: lo que mostraba (5) sale de lo sin lote, así el producto y su libro bajan lo mismo
        var closing = (await MovementsOfAsync(product.Id)).Where(m => m.Reason == "Eliminación del lote V-1").ToList();
        Assert.IsNull(closing.Single().BatchId);
        Assert.AreEqual(-5, closing.Single().Delta);
        Assert.AreEqual(10, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock);
        var row = (await _service.GetKardexAsync(5)).First();
        Assert.AreEqual(5, row.Quantity, "En el Kardex, como antes: −5");
        Assert.AreEqual(15, row.StockBefore);
        Assert.AreEqual(10, row.StockAfter);

        // Y uno que también tuvo salidas: −libro con su lote, y el resto sin lote, en un solo renglón del Kardex
        await _service.RegisterQuickConsultationUsageAsync(product.Id, 4, null);
        await _service.DeleteBatchAsync(second);
        var parts = (await MovementsOfAsync(product.Id)).Where(m => m.Reason == "Eliminación del lote V-2").OrderBy(m => m.Id).ToList();
        CollectionAssert.AreEqual(new int?[] { second, null }, parts.Select(m => m.BatchId).ToArray());
        CollectionAssert.AreEqual(new[] { 4, -10 }, parts.Select(m => m.Delta).ToArray(), "El libro del lote (−4) a 0, y lo que mostraba (6) menos eso");
        Assert.AreEqual(0, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock);
        row = (await _service.GetKardexAsync(5)).First();
        Assert.AreEqual(6, row.Quantity);
        Assert.IsFalse(row.IsEntry);
        Assert.AreEqual(6, row.StockBefore);
        Assert.AreEqual(0, row.StockAfter);

        var report = await VerifyAsync();
        Assert.IsTrue(report.IsClean, report.Summary);
        Assert.AreEqual(0, report.PreviousDifferenceCount, "Cerrados, los dos lotes quedan en 0");
    }

    [TestMethod]
    public async Task DeleteBatch_AfterTheDialogLoweredTheStock_TakesTheWholeLot_AndLeavesItNegative()
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = "PISO-1", Name = "Producto con un lote", CategoryId = category.Id, Price = 10m };
        await _service.SaveProductAsync(product);
        var batch = new ProductBatch { ProductId = product.Id, BatchNumber = "PISO-L", ExpirationDate = DateTime.Today.AddMonths(6), Quantity = 10 };
        await _service.ReceiveBatchAsync(batch);

        // El médico corrige la existencia a 8 en la ficha (ajuste sin lote); el lote sigue mostrando 10
        var dialog = new ProductDialogViewModel(_service);
        await dialog.InitializeAsync(await _service.GetProductByIdAsync(product.Id));
        dialog.CurrentStock = 8;
        await dialog.SaveAsync();

        await _service.DeleteBatchAsync(batch.Id);

        // Revisión 20: el conteo dejó −2 fuera de los lotes; cerrar el lote saca sus 10 y el −2 se queda para revisar
        // (antes se topaba en 0 y el negativo se borraba solo)
        var after = (await _service.GetProductByIdAsync(product.Id))!;
        Assert.AreEqual(-2, after.CurrentStock);
        Assert.IsTrue(after.NeedsInventoryReview);
        var parts = (await MovementsOfAsync(product.Id)).Where(m => m.Reason == "Eliminación del lote PISO-L").OrderBy(m => m.Id).ToList();
        Assert.AreEqual(batch.Id, parts.Single().BatchId, "Solo la parte con su lote: el lote cuadraba con su libro");
        Assert.AreEqual(-10, parts.Single().Delta);
        Assert.AreEqual(StockLedger.CloseSyncId(batch.SyncId), parts[0].SyncId);
        var row = (await _service.GetKardexAsync(5)).First();
        Assert.AreEqual(10, row.Quantity);
        Assert.AreEqual(8, row.StockBefore);
        Assert.AreEqual(-2, row.StockAfter);
        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    [TestMethod]
    public async Task ClosedLotThatAnOldPcRefilled_IsNotShownNorAlerted()
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = "OCULTO-1", Name = "Producto del lote reabierto", CategoryId = category.Id, Price = 10m };
        await _service.SaveProductAsync(product);
        var batch = new ProductBatch { ProductId = product.Id, BatchNumber = "OCULTO-L", ExpirationDate = DateTime.Today.AddDays(10), Quantity = 4 };
        await _service.ReceiveBatchAsync(batch);
        int expiringBefore = (await _service.GetDashboardMetricsAsync()).ExpiringSoonCount;
        await _service.DeleteBatchAsync(batch.Id);

        // Una PC anterior a v1.37.0 cancela un ticket y devuelve las piezas a este lote: no conoce ClosedAtUtc, así que
        // queda cerrado pero con piezas y sin marcar como agotado
        await using (var db = NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"ProductBatches\" SET \"Quantity\" = 3, \"IsDepleted\" = 0 WHERE \"Id\" = {0}", batch.Id);
        }

        var batches = new BatchesViewModel(_service);
        await batches.LoadBatchesAsync();
        Assert.IsFalse(batches.Batches.Any(b => b.Id == batch.Id), "Lotes no lo muestra");
        var dashboard = new DashboardViewModel(_service, _ => { });
        await dashboard.RefreshAsync();
        Assert.IsFalse(dashboard.ExpiringBatches.Any(b => b.Id == batch.Id));
        Assert.AreEqual(expiringBefore - 1, dashboard.Metrics.ExpiringSoonCount);
        var alerts = await _service.GetSystemAlertsSummaryAsync();
        Assert.IsFalse(alerts.NearExpiryBatches.Concat(alerts.ExpiredBatches).Any(b => b.Id == batch.Id), "Ni avisa");
        Assert.IsFalse((await _service.GetProductsAsync(onlyExpiring: true)).Any(p => p.Id == product.Id));
        Assert.IsFalse(StockAlertRules.CountsForAlerts((await _service.GetBatchesAsync(product.Id)).Single()));
    }

    [TestMethod]
    public async Task CancelledTicketOfTwoLines_ShowsOneKardexRowPerLine()
    {
        var (first, _, _) = await ProductWithTwoLotsAsync("DOS-A");
        var (second, _, _) = await ProductWithTwoLotsAsync("DOS-B");
        var sale = await _service.RegisterSaleAsync(new[] { (first.Id, 6, 10m), (second.Id, 7, 10m) }, null, null);
        Assert.IsTrue(await _service.CancelSaleAsync(sale!.Id));

        var kardex = await _service.GetKardexAsync(20);
        CollectionAssert.AreEquivalent(new[] { 6, 7 }, kardex.Where(m => m.MovementType == MovementType.EntradaDevolucion).Select(m => m.Quantity).ToArray());
        CollectionAssert.AreEquivalent(new[] { 6, 7 }, kardex.Where(m => m.MovementType == MovementType.SalidaVenta).Select(m => m.Quantity).ToArray());
    }

    [TestMethod]
    public void KardexRows_JoinTheParts_EvenInterleavedAndInAnyOrder()
    {
        var date = new DateTime(2026, 10, 12, 10, 0, 0);
        InventoryMovement Part(int id, int productId, int item, int before, int delta) => new()
        {
            Id = id, ProductId = productId, SaleItemId = item, MovementType = MovementType.EntradaDevolucion, Date = date,
            Quantity = Math.Abs(delta), Delta = delta, StockBefore = before, StockAfter = before + delta, Reason = "Devolución TKT-1"
        };

        // Dos renglones de un ticket, cada uno con dos lotes, intercalados por su Id local (como llegan de otro equipo)
        var rows = KardexRows.Group(new[]
        {
            Part(14, 2, 20, 4, 3), Part(13, 1, 10, 2, 5), Part(12, 2, 20, 1, 3), Part(11, 1, 10, 1, 1),
            new InventoryMovement { Id = 9, ProductId = 1, MovementType = MovementType.SalidaVenta, Date = date.AddMinutes(-1), Quantity = 6, Delta = -6, StockBefore = 7, StockAfter = 1 }
        });

        Assert.HasCount(3, rows);
        Assert.AreEqual(2, rows[0].ProductId, "En el orden en que aparece la primera parte");
        Assert.AreEqual(6, rows[0].Quantity);
        Assert.AreEqual(1, rows[0].StockBefore);
        Assert.AreEqual(7, rows[0].StockAfter);
        Assert.AreEqual(1, rows[1].ProductId);
        Assert.AreEqual(6, rows[1].Quantity);
        Assert.AreEqual(1, rows[1].StockBefore);
        Assert.AreEqual(7, rows[1].StockAfter);
        Assert.AreEqual(9, rows[2].Id);
    }

    [TestMethod]
    public async Task Kardex_ReadsTheWholeLastOperation_WhenItCrossesThePage()
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = "MUCHOS", Name = "Producto de muchos lotes", CategoryId = category.Id, Price = 1m };
        await _service.SaveProductAsync(product);
        for (int i = 0; i < 40; i++)
        {
            await _service.ReceiveBatchAsync(new ProductBatch { ProductId = product.Id, BatchNumber = $"M-{i:00}", ExpirationDate = DateTime.Today.AddDays(30 + i), Quantity = 1 });
        }
        await _service.RegisterQuickConsultationUsageAsync(product.Id, 40, "Todo de una vez");

        var row = (await _service.GetKardexAsync(1)).Single();
        Assert.AreEqual(40, row.Quantity, "Una salida de 40 lotes es un renglón aunque pase de lo que se lee primero");
        Assert.AreEqual(40, row.StockBefore);
        Assert.AreEqual(0, row.StockAfter);
    }

    [TestMethod]
    public async Task CancelSale_IntoAClosedLot_KeepsItClosed()
    {
        var (product, soon, _) = await ProductWithTwoLotsAsync();
        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 2, 10m) }, null, null);
        await _service.DeleteBatchAsync(soon.Id);

        Assert.IsTrue(await _service.CancelSaleAsync(sale!.Id));

        var batch = (await _service.GetBatchesAsync(product.Id)).Single(b => b.Id == soon.Id);
        Assert.IsTrue(batch.IsClosed);
        Assert.IsTrue(batch.IsDepleted, "Sigue sin verse");
        Assert.AreEqual(2, batch.Quantity, "Las piezas regresan a su lote, que sigue cerrado");
        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    // ================================================================== diálogo del producto

    [TestMethod]
    public async Task ProductDialog_ASaleWhileItIsOpen_IsNotReverted()
    {
        var (product, _, _) = await ProductWithTwoLotsAsync();
        var dialog = new ProductDialogViewModel(_service);
        await dialog.InitializeAsync(await _service.GetProductByIdAsync(product.Id));
        Assert.AreEqual(10, dialog.OriginalStock);

        // Mientras el diálogo está abierto se vende (en la caja o en otro equipo)
        await _service.RegisterSaleAsync(new[] { (product.Id, 2, 10m) }, null, null);

        dialog.Price = 12m;
        await dialog.SaveAsync();
        Assert.AreEqual(string.Empty, dialog.ErrorMessage);

        var saved = (await _service.GetProductByIdAsync(product.Id))!;
        Assert.AreEqual(12m, saved.Price);
        Assert.AreEqual(8, saved.CurrentStock, "La venta no se revierte");
        Assert.IsFalse((await MovementsOfAsync(product.Id)).Any(m => m.MovementType == MovementType.AjusteInventario));
        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    [TestMethod]
    public async Task ProductDialog_ATypedStock_IsSetAgainstWhatIsStored()
    {
        var (product, _, _) = await ProductWithTwoLotsAsync();
        var dialog = new ProductDialogViewModel(_service);
        await dialog.InitializeAsync(await _service.GetProductByIdAsync(product.Id));
        await _service.RegisterSaleAsync(new[] { (product.Id, 2, 10m) }, null, null);

        dialog.CurrentStock = 15;
        await dialog.SaveAsync();

        Assert.AreEqual(15, (await _service.GetProductByIdAsync(product.Id))!.CurrentStock, "Lo tecleado es lo que hay");
        var adjust = (await MovementsOfAsync(product.Id)).Single(m => m.MovementType == MovementType.AjusteInventario);
        Assert.AreEqual(7, adjust.Delta, "Contra lo guardado (8), no contra lo que mostró el diálogo (10)");
        Assert.IsNull(adjust.BatchId);
        Assert.IsTrue((await VerifyAsync()).IsClean);
    }

    // ================================================================== secuencias al azar

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task RandomOperations_KeepWhatIsStoredEqualToTheLedger(int seed)
    {
        var random = new Random(seed);
        var category = (await _service.GetCategoriesAsync()).First();
        await _service.SaveProductAsync(new Product { Code = $"AZ-{seed}", Name = "Producto al azar", CategoryId = category.Id, Price = 5m, CurrentStock = 4 });
        var sales = new List<int>();

        for (int step = 0; step < 60; step++)
        {
            var products = await _service.GetProductsAsync();
            var product = products[random.Next(products.Count)];
            var openBatches = (await _service.GetBatchesAsync(product.Id)).Where(b => !b.IsClosed).ToList();
            string operation;
            switch (random.Next(10))
            {
                case 0:
                    operation = "lote";
                    await _service.ReceiveBatchAsync(new ProductBatch
                    {
                        ProductId = product.Id, BatchNumber = $"R{seed}-{step}", ExpirationDate = DateTime.Today.AddDays(random.Next(-10, 400)), Quantity = random.Next(1, 8)
                    });
                    break;
                case 1:
                    operation = "entrada";
                    await _service.RegisterMovementAsync(new InventoryMovement
                    {
                        ProductId = product.Id, MovementType = MovementType.EntradaCompra, Quantity = random.Next(1, 6),
                        BatchId = openBatches.Count > 0 && random.Next(2) == 0 ? openBatches[random.Next(openBatches.Count)].Id : null
                    });
                    break;
                case 2:
                    operation = "consumo";
                    if (product.CurrentStock <= 0) continue;
                    await _service.RegisterQuickConsultationUsageAsync(product.Id, random.Next(1, product.CurrentStock + 1), null);
                    break;
                case 3:
                    operation = "baja";
                    if (product.CurrentStock <= 0) continue;
                    await _service.RegisterMovementAsync(new InventoryMovement
                    {
                        ProductId = product.Id, MovementType = MovementType.SalidaCaducidad, Quantity = random.Next(1, product.CurrentStock + 1),
                        BatchId = openBatches.Count > 0 ? openBatches[random.Next(openBatches.Count)].Id : null
                    });
                    break;
                case 4:
                case 5:
                {
                    operation = "venta";
                    var lines = new List<SaleLineRequest>();
                    var left = products.ToDictionary(p => p.Id, p => p.CurrentStock);
                    for (int l = random.Next(1, 4); l > 0; l--)
                    {
                        var p = products[random.Next(products.Count)];
                        if (left[p.Id] <= 0) continue;
                        int qty = random.Next(1, left[p.Id] + 1);
                        left[p.Id] -= qty;
                        lines.Add(new SaleLineRequest(p.Id, qty, p.Price));
                    }
                    if (lines.Count == 0) continue;
                    sales.Add((await _service.RegisterSaleAsync(lines, null, null))!.Id);
                    break;
                }
                case 6:
                    operation = "cancelar";
                    if (sales.Count == 0) continue;
                    int saleId = sales[random.Next(sales.Count)];
                    sales.Remove(saleId);
                    await _service.CancelSaleAsync(saleId);
                    break;
                case 7:
                    operation = "cerrar lote";
                    if (openBatches.Count == 0) continue;
                    await _service.DeleteBatchAsync(openBatches[random.Next(openBatches.Count)].Id);
                    break;
                case 8:
                {
                    operation = "diálogo";
                    var dialog = new ProductDialogViewModel(_service);
                    await dialog.InitializeAsync(await _service.GetProductByIdAsync(product.Id));
                    if (random.Next(2) == 0 && product.CurrentStock > 0)
                        await _service.RegisterQuickConsultationUsageAsync(product.Id, 1, "Mientras el diálogo está abierto");
                    if (random.Next(2) == 0) dialog.CurrentStock = random.Next(0, 30);
                    dialog.Notes = $"paso {step}";
                    await dialog.SaveAsync();
                    Assert.AreEqual(string.Empty, dialog.ErrorMessage);
                    break;
                }
                default:
                {
                    operation = "pedido";
                    bool withLot = random.Next(2) == 0;
                    await _service.ReceiveSupplierOrderAsync(new SupplierReceipt
                    {
                        Supplier = "LUIN", OrderNumber = $"{seed}-{step}", CategoryIdForNewProducts = category.Id,
                        Lines = new[]
                        {
                            new SupplierReceiptLine
                            {
                                ProductId = product.Id, Sku = $"SKU-{product.Id}", Description = product.Name, Quantity = random.Next(1, 6), UnitCost = 3m,
                                BatchNumber = withLot ? $"P{seed}-{step}" : null, ExpirationDate = withLot ? DateTime.Today.AddMonths(6) : null
                            }
                        }
                    });
                    break;
                }
            }

            var report = await VerifyAsync();
            Assert.IsEmpty(report.Entries, $"Semilla {seed}, paso {step} ({operation}): {report.Summary}");
            var negative = (await _service.GetProductsAsync()).FirstOrDefault(p => p.CurrentStock < 0);
            Assert.IsNull(negative, $"Semilla {seed}, paso {step} ({operation}): {negative?.Name} quedó en {negative?.CurrentStock}");
        }
    }
}
