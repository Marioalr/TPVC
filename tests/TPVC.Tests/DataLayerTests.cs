using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
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
        // Antes compartían un único DbContext y varias recargas simultáneas fallaban; después, el pool de
        // SQLite a veces prestaba la misma conexión a dos contextos (ver SqlitePoolOpenGuard)
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
    public async Task ConnectionOpens_AreSerialized_SoThePoolCannotLendOneConnectionTwice()
    {
        // Microsoft.Data.Sqlite 10.0.12: si otra apertura (o ClearAllPools) corre mientras una conexión
        // se está activando, puede tomarla por fugada y prestarla a un segundo contexto
        using var first = NewContext();
        using var second = NewContext();
        using var insideFirstOpen = new ManualResetEventSlim();
        using var releaseFirstOpen = new ManualResetEventSlim();
        // StateChange se dispara al final de SqliteConnection.Open(), aún dentro de la apertura física
        first.Database.GetDbConnection().StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
            {
                insideFirstOpen.Set();
                releaseFirstOpen.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var firstOpen = Task.Run(() => first.Database.OpenConnection());
        Assert.IsTrue(insideFirstOpen.Wait(TimeSpan.FromSeconds(10)), "La primera conexión no llegó a abrirse");

        var secondOpen = Task.Run(() => second.Database.OpenConnectionAsync());
        var clearPools = Task.Run(AppDbContext.ClearConnectionPools);
        var contenders = Task.WhenAny(secondOpen, clearPools);
        var firstToFinish = await Task.WhenAny(contenders, Task.Delay(300));
        releaseFirstOpen.Set();
        await Task.WhenAll(firstOpen, secondOpen, clearPools);

        Assert.AreNotSame(contenders, firstToFinish,
            "Otra apertura o limpieza del pool terminó mientras la primera conexión se activaba");
        Assert.AreNotSame(
            ((SqliteConnection)first.Database.GetDbConnection()).Handle,
            ((SqliteConnection)second.Database.GetDbConnection()).Handle);
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
        records.Consultation.ConsultationChiefComplaint = "Control sin duplicados";
        records.Consultation.ConsultationDiagnosis = "Sano";

        await records.Consultation.FinalizeConsultationAsync();
        Assert.IsTrue(records.Consultation.IsFinishConsultationOpen);

        await records.Consultation.Checkout.ChargeAsync();

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

    [TestMethod]
    public async Task LowStock_IgnoresCatalogItemsWithoutMinimum()
    {
        var category = (await _service.GetCategoriesAsync()).First();
        await _service.SaveProductAsync(new Product { Code = "CAT-0", Name = "Solo receta", CategoryId = category.Id, MinStock = 0, OptimalStock = 0 });

        var metrics = await _service.GetDashboardMetricsAsync();
        var alerts = await _service.GetSystemAlertsSummaryAsync();
        var purchases = await _service.GetSuggestedPurchaseListAsync();
        var lowStock = await _service.GetProductsAsync(onlyLowStock: true);

        Assert.IsFalse(alerts.LowStockProducts.Any(p => p.Code == "CAT-0"));
        Assert.IsFalse(purchases.Any(p => p.ProductCode == "CAT-0"));
        Assert.IsFalse(lowStock.Any(p => p.Code == "CAT-0"));
        Assert.AreEqual(lowStock.Count, metrics.LowStockCount, "Panel, alertas y filtro usan la misma regla");
        Assert.IsFalse(new Product { MinStock = 0, CurrentStock = 0 }.IsLowStock);
        Assert.IsTrue(new Product { MinStock = 5, CurrentStock = 5 }.IsLowStock);
    }

    [TestMethod]
    public void StockBadgeText_SaysTheStateInWordsNotOnlyColor()
    {
        Assert.AreEqual("Existencias: 12", new Product { MinStock = 5, CurrentStock = 12 }.StockBadgeText);
        Assert.AreEqual("Stock bajo: 3", new Product { MinStock = 5, CurrentStock = 3 }.StockBadgeText);
        Assert.AreEqual("Sin existencias", new Product { MinStock = 5, CurrentStock = 0 }.StockBadgeText);
        Assert.AreEqual("Sin existencias", new Product { MinStock = 0, CurrentStock = 0 }.StockBadgeText);
        Assert.AreEqual("Existencias: 2", new Product { MinStock = 0, CurrentStock = 2 }.StockBadgeText, "Sin mínimo no hay alerta");
    }

    [TestMethod]
    public async Task AddConsultationNote_SameFolioOnAnotherPatient_DoesNotOverwriteIt()
    {
        var patients = await _service.GetPatientsAsync();
        var first = patients[0];
        var second = patients[1];

        await _service.AddConsultationNoteAsync(first.Id, new PatientConsultation { Folio = "CNS-20261003-001", ChiefComplaint = "Paciente A" });
        await _service.AddConsultationNoteAsync(second.Id, new PatientConsultation { Folio = "CNS-20261003-001", ChiefComplaint = "Paciente B" });

        var a = await _service.GetPatientConsultationsAsync(first.Id);
        var b = await _service.GetPatientConsultationsAsync(second.Id);
        Assert.IsTrue(a.Any(c => c.ChiefComplaint == "Paciente A"), "La nota del primer paciente se conserva");
        Assert.IsTrue(b.Any(c => c.ChiefComplaint == "Paciente B"));
    }

    [TestMethod]
    public async Task PatientSummaries_CarryCountsWithoutLoadingNotes()
    {
        var full = await _service.GetPatientsAsync();
        var summaries = await _service.GetPatientSummariesAsync();

        Assert.HasCount(full.Count, summaries);
        foreach (var s in summaries)
        {
            Assert.IsEmpty(s.Consultations, "El listado no carga el texto de las consultas");
            Assert.AreEqual(full.Single(f => f.Id == s.Id).Consultations.Count, s.ConsultationCount);
        }
    }

    [TestMethod]
    public async Task MedicalRecords_SelectingPatientFromLightList_LoadsItsHistory()
    {
        var records = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false));
        await records.LoadPatientsAsync();

        var patient = records.Patients.First(p => p.ConsultationCount > 0);
        Assert.IsEmpty(patient.Consultations);

        records.SelectPatient(patient);

        Assert.HasCount(patient.ConsultationCount, records.ConsultationsHistory, "El historial se carga al abrir el expediente");
    }

    [TestMethod]
    public async Task CancelSale_ReactivatesDepletedBatch()
    {
        var category = (await _service.GetCategoriesAsync()).First();
        var product = new Product { Code = "DEP-1", Name = "Lote único", CategoryId = category.Id, Price = 10m };
        await _service.SaveProductAsync(product);
        await _service.ReceiveBatchAsync(new ProductBatch { ProductId = product.Id, BatchNumber = "L1", ExpirationDate = DateTime.Today.AddMonths(6), Quantity = 2 });

        var sale = await _service.RegisterSaleAsync(new[] { (product.Id, 2, 10m) }, null, null);
        Assert.IsTrue((await _service.GetBatchesAsync(product.Id)).Single().IsDepleted);

        await _service.CancelSaleAsync(sale!.Id);

        var batch = (await _service.GetBatchesAsync(product.Id)).Single();
        Assert.AreEqual(2, batch.Quantity);
        Assert.IsFalse(batch.IsDepleted, "El lote vuelve a estar disponible para FEFO");
    }

    [TestMethod]
    public async Task PrescriptionTemplates_ApplyToDosageAndTreatmentPlan()
    {
        using (var db = NewContext())
        {
            db.PrescriptionTemplates.Add(new PrescriptionTemplate { Kind = PrescriptionTemplateKinds.Posology, Title = "Cada 8", Text = "Tomar 1 tableta vía oral cada 8 hrs por 5 días" });
            db.PrescriptionTemplates.Add(new PrescriptionTemplate { Kind = PrescriptionTemplateKinds.GeneralMeasures, Title = "Hidratación", Text = "• Mantener adecuada hidratación" });
            db.PediatricDoseRules.Add(new PediatricDoseRule { Medication = "Paracetamol 3.2 g/100 ml", DoseText = "15 mg/kg/dosis", MlPerKg = 15.0 / 32.0, DropsPerMl = 20 });
            await db.SaveChangesAsync();
        }

        var records = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false));
        await records.LoadInitialDataAsync();

        records.Prescription.SelectedPosologyTemplate = records.Templates.PosologyTemplates.Single();
        Assert.AreEqual("Tomar 1 tableta vía oral cada 8 hrs por 5 días", records.Prescription.PrescriptionDosage);
        Assert.IsNull(records.Prescription.SelectedPosologyTemplate, "El selector se limpia para reutilizar la plantilla");

        records.Consultation.ConsultationTreatmentPlan = string.Empty;
        records.Consultation.SelectedGeneralMeasureTemplate = records.Templates.GeneralMeasureTemplates.Single();
        Assert.AreEqual("• Mantener adecuada hidratación", records.Consultation.ConsultationTreatmentPlan);
        Assert.AreEqual("• Mantener adecuada hidratación", records.Consultation.ConsultationGeneralIndications);

        records.Prescription.SelectedPediatricRule = records.Templates.PediatricDoseRules.Single();
        records.Prescription.PediatricWeightText = "15";
        StringAssert.Contains(records.Prescription.PediatricDoseResult, "7 ml (140 gotas)");
        records.Prescription.ApplyPediatricDose();
        StringAssert.StartsWith(records.Prescription.PrescriptionDosage, "Dar 7 ml (140 gotas) vía oral");
    }

    [TestMethod]
    public void GeneralIndicationPreset_IsVisibleInTreatmentPlan()
    {
        var records = new MedicalRecordsViewModel(_service, new ConsultationService(seedDemoPatients: false));
        records.Consultation.ConsultationTreatmentPlan = "Reposo";
        records.Consultation.AppendGeneralIndicationPreset("Hidratación abundante.");
        Assert.AreEqual("Reposo Hidratación abundante.", records.Consultation.ConsultationTreatmentPlan);
    }
}
