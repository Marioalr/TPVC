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
/// Pruebas de la capa de datos v1.5.0: contextos de vida corta, ventas persistidas,
/// integridad de stock, migración aditiva y metadatos de sincronización.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DataLayerTests
{
    private string _dbPath = string.Empty;
    private DbContextOptions<AppDbContext> _options = null!;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_data_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={_dbPath}");
        _options = builder.Options;

        using (var db = new AppDbContext(_options))
        {
            DatabaseInitializer.Initialize(db);
        }
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
        var backups = Path.Combine(Path.GetDirectoryName(_dbPath)!, "backups");
        // Solo se limpian respaldos creados por estas pruebas
        if (Directory.Exists(backups))
        {
            foreach (var f in Directory.GetFiles(backups, "consultorio-*.db")) { try { File.Delete(f); } catch { } }
        }
    }

    private AppDbContext NewContext() => new(_options);

    [TestMethod]
    public async Task ConcurrentReads_DoNotThrowSecondOperationError()
    {
        // Antes compartían un único DbContext y varias recargas simultáneas fallaban
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await Task.Yield();
            await _service.GetProductsAsync();
            await _service.GetDashboardMetricsAsync();
            await _service.GetBatchesAsync();
            await _service.GetRecentMovementsAsync(20);
            await _service.GetPatientsAsync();
        });

        await Task.WhenAll(tasks);
    }

    [TestMethod]
    public async Task Initialize_EnablesWalJournalMode()
    {
        using var db = NewContext();
        await db.Database.OpenConnectionAsync();
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await cmd.ExecuteScalarAsync();
        Assert.AreEqual("wal", mode?.ToLowerInvariant());
    }

    [TestMethod]
    public async Task RegisterSaleAsync_PersistsTicketWithSequentialFolioAndServiceLines()
    {
        var product = (await _service.GetProductsAsync()).First(p => p.CurrentStock >= 3);
        var service = (await _service.GetClinicalServicesAsync()).First();

        var sale1 = await _service.RegisterSaleAsync(new[]
        {
            new SaleLineRequest(product.Id, 2, product.Price),
            new SaleLineRequest(-service.Id, 1, service.Price, service.Code, service.Name)
        }, "Paciente Ticket", "Tarjeta");

        var sale2 = await _service.RegisterSaleAsync(new[] { (product.Id, 1, product.Price) }, null, null);

        Assert.IsNotNull(sale1);
        Assert.IsNotNull(sale2);
        var prefix = $"TKT-{DateTime.Now:yyyyMMdd}-";
        Assert.AreEqual($"{prefix}0001", sale1.Folio);
        Assert.AreEqual($"{prefix}0002", sale2.Folio);

        var sales = await _service.GetSalesAsync(DateTime.Today, DateTime.Today.AddDays(1));
        var stored = sales.Single(s => s.Folio == sale1.Folio);
        Assert.HasCount(2, stored.Items);
        Assert.IsTrue(stored.Items.Any(i => i.IsService && i.ClinicalServiceId == service.Id));
        Assert.AreEqual(product.Price * 2 + service.Price, stored.Total);
        Assert.AreEqual("Tarjeta", stored.PaymentMethod);

        var updated = await _service.GetProductByIdAsync(product.Id);
        Assert.AreEqual(product.CurrentStock - 3, updated!.CurrentStock);

        var metrics = await _service.GetDashboardMetricsAsync();
        Assert.AreEqual(2, metrics.SalesTodayCount);
        Assert.AreEqual(stored.Total + product.Price, metrics.SalesTodayTotal);
    }

    [TestMethod]
    public async Task RegisterSaleAsync_InsufficientStock_ThrowsAndSavesNothing()
    {
        var products = await _service.GetProductsAsync();
        var ok = products.First(p => p.CurrentStock >= 2);
        var scarce = products.First(p => p.Id != ok.Id && p.CurrentStock > 0);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.RegisterSaleAsync(new[]
        {
            (ok.Id, 1, ok.Price),
            (scarce.Id, scarce.CurrentStock + 5, scarce.Price)
        }, "Paciente", "Efectivo"));

        // Transacción completa: ni el producto válido se descontó ni se creó ticket
        Assert.AreEqual(ok.CurrentStock, (await _service.GetProductByIdAsync(ok.Id))!.CurrentStock);
        Assert.AreEqual(scarce.CurrentStock, (await _service.GetProductByIdAsync(scarce.Id))!.CurrentStock);
        Assert.IsEmpty(await _service.GetSalesAsync(DateTime.Today, DateTime.Today.AddDays(1)));
    }

    [TestMethod]
    public async Task ReceiveBatchAndDeleteBatch_KeepStockAndKardexAligned()
    {
        var product = (await _service.GetProductsAsync()).First();
        int initial = product.CurrentStock;

        var batch = new ProductBatch
        {
            ProductId = product.Id,
            BatchNumber = "LOT-REC-01",
            ExpirationDate = DateTime.Today.AddMonths(6),
            Quantity = 7
        };
        await _service.ReceiveBatchAsync(batch);

        var afterReceive = await _service.GetProductByIdAsync(product.Id);
        Assert.AreEqual(initial + 7, afterReceive!.CurrentStock);
        var entry = (await _service.GetRecentMovementsAsync(5)).First(m => m.ProductId == product.Id);
        Assert.AreEqual(MovementType.EntradaCompra, entry.MovementType);
        Assert.AreEqual(batch.Id, entry.BatchId);

        await _service.DeleteBatchAsync(batch.Id);

        var afterDelete = await _service.GetProductByIdAsync(product.Id);
        Assert.AreEqual(initial, afterDelete!.CurrentStock, "Eliminar un lote con piezas debe descontarlas");
        var adjust = (await _service.GetRecentMovementsAsync(5)).First(m => m.ProductId == product.Id);
        Assert.AreEqual(MovementType.AjusteInventario, adjust.MovementType);
        Assert.AreEqual(7, adjust.Quantity);

        using var db = NewContext();
        Assert.IsTrue(await db.SyncTombstones.AnyAsync(t => t.EntityType == nameof(ProductBatch)),
            "El borrado físico debe quedar registrado para sincronización");
    }

    [TestMethod]
    public async Task SaveProductAsync_ManualStockChange_RegistersAdjustmentMovement()
    {
        var product = (await _service.GetProductsAsync()).First();
        int before = product.CurrentStock;
        product.CurrentStock = before + 4;

        await _service.SaveProductAsync(product);

        var movement = (await _service.GetRecentMovementsAsync(5)).First(m => m.ProductId == product.Id);
        Assert.AreEqual(MovementType.AjusteInventario, movement.MovementType);
        Assert.AreEqual(before, movement.StockBefore);
        Assert.AreEqual(before + 4, movement.StockAfter);
        Assert.AreEqual(4, movement.Quantity);
    }

    [TestMethod]
    public async Task RegisterQuickConsultationUsage_MoreThanStock_Throws()
    {
        var product = (await _service.GetProductsAsync()).First(p => p.CurrentStock > 0);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.RegisterQuickConsultationUsageAsync(product.Id, product.CurrentStock + 1, null));
    }

    [TestMethod]
    public async Task SyncMetadata_IsStampedOnInsertAndUpdate()
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = "SYNC-01", Name = "Producto Sync", CategoryId = category.Id, Price = 10m };
        await _service.SaveProductAsync(product);

        Assert.AreNotEqual(Guid.Empty, product.SyncId);
        var firstStamp = product.UpdatedAtUtc;
        Assert.IsTrue(firstStamp > DateTime.UtcNow.AddMinutes(-1));

        await Task.Delay(20);
        product.Price = 12m;
        await _service.SaveProductAsync(product);

        using var db = NewContext();
        var stored = await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id);
        Assert.AreEqual(product.SyncId, stored.SyncId, "El SyncId no debe cambiar al editar");
        Assert.IsTrue(stored.UpdatedAtUtc > firstStamp);

        // Todos los registros sembrados tienen SyncId único
        var ids = await db.Patients.Select(p => p.SyncId).ToListAsync();
        Assert.IsTrue(ids.All(id => id != Guid.Empty));
        Assert.AreEqual(ids.Count, ids.Distinct().Count());
    }

    [TestMethod]
    public async Task EnsureSchema_UpgradesLegacyDatabase_AddingMissingTablesColumnsAndBackfillingSyncIds()
    {
        // Simular una base v1.4.0: sin tabla de ventas ni columnas de sincronización en Products
        using (var db = NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE SaleItems; DROP TABLE Sales;");
            await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_Products_SyncId; DROP INDEX IX_Products_UpdatedAtUtc;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Products DROP COLUMN SyncId; ALTER TABLE Products DROP COLUMN UpdatedAtUtc;");
            // Las bases anteriores a v1.5.0 no tienen huella de esquema
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 0;");
        }

        using (var db = NewContext())
        {
            DatabaseInitializer.Initialize(db);
        }

        using (var db = NewContext())
        {
            var products = await db.Products.AsNoTracking().ToListAsync();
            Assert.IsNotEmpty(products);
            Assert.IsTrue(products.All(p => p.SyncId != Guid.Empty), "Debe asignar SyncId a registros existentes");
            Assert.AreEqual(products.Count, products.Select(p => p.SyncId).Distinct().Count());
            Assert.IsTrue(products.All(p => p.UpdatedAtUtc.Year > 2000));
            Assert.AreEqual(0, await db.Sales.CountAsync(), "La tabla de ventas debe recrearse");
        }

        // Y la venta funciona sobre la base migrada
        var product = (await _service.GetProductsAsync()).First(p => p.CurrentStock > 0);
        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 1, product.Price) }, null, null);
        Assert.IsNotNull(sale);
    }

    [TestMethod]
    public async Task Initialize_IsIdempotentOnExistingDatabase()
    {
        using (var db = NewContext())
        {
            DatabaseInitializer.Initialize(db);
            DatabaseInitializer.Initialize(db);
        }
        Assert.IsNotEmpty(await _service.GetProductsAsync());
    }

    [TestMethod]
    public async Task GetProductsAsync_SearchIgnoresAccentsAndCase()
    {
        var results = await _service.GetProductsAsync(searchText: "acido clavulanico");
        Assert.IsTrue(results.Any(p => p.Name.Contains("Ácido Clavulánico")), "Debe encontrar 'Ácido' buscando 'acido'");

        var patients = await _service.GetPatientsAsync(searchText: "rodriguez");
        Assert.IsTrue(patients.Any(p => p.FullName.StartsWith("Rodríguez")));
    }

    [TestMethod]
    public void TextSearch_NormalizesDiacritics()
    {
        Assert.AreEqual("perez nunez", TextSearch.Normalize("  Pérez NÚÑEZ "));
        Assert.IsTrue(TextSearch.Matches(TextSearch.Normalize("jose"), null, "José María"));
        Assert.IsFalse(TextSearch.Matches(TextSearch.Normalize("xyz"), "José"));
    }

    [TestMethod]
    public async Task BackupService_CreatesDailyBackupOnceAndManualBackups()
    {
        var backup = new BackupService(_dbPath);
        var first = backup.EnsureDailyBackup();
        var second = backup.EnsureDailyBackup();

        Assert.IsNotNull(first);
        Assert.IsTrue(File.Exists(first));
        Assert.IsNull(second, "Solo un respaldo automático por día");

        var manual = await backup.CreateBackupNowAsync();
        Assert.IsTrue(File.Exists(manual));

        // El respaldo es una base válida con los datos
        using var conn = new SqliteConnection($"Data Source={manual};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Products";
        Assert.IsGreaterThan(0L, (long)cmd.ExecuteScalar()!);
    }

    [TestMethod]
    public async Task FinalizeConsultationAndCheckout_SavesSingleConsultationNote()
    {
        // Regresión: al recargar expedientes tras guardar la nota, el cobro volvía a guardarla
        var consultService = new ConsultationService();
        var mainVm = new MainViewModel(_service, consultService, new ThemeService(), new AppUpdateService());
        var records = mainVm.MedicalRecordsVM;
        await records.LoadPatientsAsync();

        var patient = records.Patients.First();
        records.SelectPatient(patient);
        records.StartActiveConsultation();
        records.ConsultationChiefComplaint = "Control sin duplicados";
        records.ConsultationDiagnosis = "Sano";

        await records.FinalizeConsultationAsync();
        Assert.IsTrue(records.IsPosModalOpen);

        await mainVm.PosVM.ProcessSaleAsync();

        var stored = (await _service.GetPatientByIdAsync(patient.Id))!;
        Assert.AreEqual(1, stored.Consultations.Count(c => c.ChiefComplaint == "Control sin duplicados"),
            "La nota de consulta debe guardarse una sola vez");

        var sales = await _service.GetSalesAsync(DateTime.Today, DateTime.Today.AddDays(1));
        Assert.HasCount(1, sales);
        Assert.IsTrue(sales[0].Items.Any(i => i.IsService), "La consulta cobrada debe quedar registrada en el ticket");
    }

    [TestMethod]
    public void ServiceRegistration_ResolvesCoreServicesWithoutAmbiguity()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddTpvcCoreServices($"Data Source={_dbPath}");
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
            services, new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.IsNotNull(provider.GetService(typeof(InventoryService)));
        Assert.IsNotNull(provider.GetService(typeof(BackupService)));
        var consultations = (ConsultationService)provider.GetService(typeof(ConsultationService))!;
        Assert.IsEmpty(consultations.Patients, "La app no debe sembrar pacientes de demostración en memoria");
    }

    [TestMethod]
    public async Task EntityEquality_UsesIdAcrossReloads()
    {
        var a = (await _service.GetProductsAsync()).First();
        var b = (await _service.GetProductsAsync()).First(p => p.Id == a.Id);

        Assert.AreNotSame(a, b);
        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.AreNotEqual(new Product(), new Product(), "Entidades nuevas (Id 0) solo son iguales a sí mismas");
    }
}
