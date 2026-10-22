using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Panel «Negocio» (pedido del médico del 2026-10-22): las reglas puras (costo guardado o estimado, canceladas, piezas sueltas,
/// el honorario, los periodos en la zona del consultorio y la comparación con el anterior), el costo que guardan la venta, las
/// compras y el consumo (revisión 18), la lectura de la base y el ViewModel.
/// </summary>
[TestClass]
[DoNotParallelize]
public class BusinessReportTests
{
    private static readonly BusinessPeriod October = new(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1));

    private static Product Med(int id, decimal price, decimal cost, int unitsPerPackage = 1) => new()
    {
        Id = id, Code = $"MED-{id}", Name = $"Medicamento {id}", Price = price, UnitCost = cost, UnitsPerPackage = unitsPerPackage,
        Category = new Category { Name = "Medicamentos y Fármacos" }
    };

    private static Product Supply(int id, decimal price, decimal cost, bool excludesVat = false, int unitsPerPackage = 1) => new()
    {
        Id = id, Code = $"CUR-{id}", Name = $"Insumo {id}", Price = price, UnitCost = cost, CostExcludesVat = excludesVat, UnitsPerPackage = unitsPerPackage,
        Category = new Category { Name = "Material de Curación" }
    };

    private static BusinessSaleLine ProductLine(int saleId, DateTime date, int productId, int quantity, decimal unitPrice, decimal? unitCost,
        bool? individual = false, bool cancelled = false) =>
        new(saleId, date, cancelled, productId, null, false, $"P-{productId}", $"Producto {productId}", quantity, unitPrice, unitPrice * quantity, unitCost, individual);

    private static BusinessSaleLine ServiceLine(int saleId, DateTime date, int? serviceId, string code, string name, decimal price, bool cancelled = false) =>
        new(saleId, date, cancelled, null, serviceId, true, code, name, 1, price, price, null, null);

    private static readonly ClinicalService Fee = new() { Id = 1, Code = "SERV-001", Name = "Consulta Médica General", Category = "Consulta" };
    private static readonly ClinicalService Curacion = new() { Id = 2, Code = "SERV-002", Name = "Curación Menor y Asepsia", Category = "Curaciones" };

    // ================================================================== reglas

    [TestMethod]
    public void TheCost_IsTheRecordedOne_OrTodaysMarkedAsEstimated()
    {
        var product = Med(1, 45m, 25m); // hoy cuesta 25; al vender costaba 20
        var day = new DateTime(2026, 10, 5, 10, 0, 0);
        var lines = BusinessReportRules.Lines(new[]
        {
            ProductLine(1, day, 1, 2, 45m, 20m),
            ProductLine(2, day, 1, 1, 45m, null, individual: null) // venta de antes de la revisión 18
        }, new[] { product }, Array.Empty<ClinicalService>(), October);

        Assert.AreEqual(40m, lines[0].Cost, "El costo guardado al vender, aunque el producto cueste más hoy");
        Assert.AreEqual(BusinessCostSource.Recorded, lines[0].CostSource);
        Assert.AreEqual(25m, lines[1].Cost, "Sin costo guardado: el de hoy");
        Assert.AreEqual(BusinessCostSource.Estimated, lines[1].CostSource);

        var summary = BusinessReportRules.Summarize(lines);
        Assert.AreEqual(135m, summary.Sales);
        Assert.AreEqual(65m, summary.Cost);
        Assert.AreEqual(70m, summary.Profit);
        Assert.AreEqual(51.9m, summary.MarginPercent, "70 / 135 = 51.85… → 51.9");
        Assert.AreEqual(1, summary.EstimatedCostLines);
        StringAssert.Contains(BusinessReportRules.EstimatedCostNote(summary.EstimatedCostLines, summary.MissingCostLines), "costo estimado");

        // Sin costo hoy tampoco: cuenta 0 y se dice aparte
        var missing = BusinessReportRules.LineCost(ProductLine(3, day, 1, 1, 45m, null), Med(1, 45m, 0m));
        Assert.AreEqual((0m, BusinessCostSource.Missing), missing);
        Assert.AreEqual((0m, BusinessCostSource.NotApplicable), BusinessReportRules.LineCost(ServiceLine(4, day, 1, "SERV-001", "Consulta", 300m), null));
    }

    [TestMethod]
    public void CancelledSales_AndSalesOutsideThePeriod_DoNotCount()
    {
        var product = Med(1, 100m, 60m);
        var lines = BusinessReportRules.Lines(new[]
        {
            ProductLine(1, new DateTime(2026, 10, 3, 9, 0, 0), 1, 1, 100m, 60m),
            ProductLine(2, new DateTime(2026, 10, 3, 9, 30, 0), 1, 5, 100m, 60m, cancelled: true),
            ProductLine(3, new DateTime(2026, 9, 30, 23, 59, 0), 1, 1, 100m, 60m),
            ProductLine(4, new DateTime(2026, 11, 1, 0, 0, 0), 1, 1, 100m, 60m),
            ProductLine(5, new DateTime(2026, 10, 31, 23, 59, 0), 1, 1, 100m, 60m)
        }, new[] { product }, Array.Empty<ClinicalService>(), October);

        var summary = BusinessReportRules.Summarize(lines);
        Assert.AreEqual(2, summary.Tickets, "Solo el 3 y el 31 de octubre, sin la cancelada");
        Assert.AreEqual(200m, summary.Sales);
        Assert.AreEqual(100m, summary.AverageTicket);
    }

    [TestMethod]
    public void LoosePieces_CostPerPiece_AndCountAsPieces()
    {
        // Gasas: 30 sin IVA → 34.80 el paquete de 10 → 3.48 la pieza
        var gauze = Supply(3, 60m, 30m, excludesVat: true, unitsPerPackage: 10);
        Assert.AreEqual(3.48m, BusinessReportRules.CostToRecord(gauze, individualUnits: true));
        Assert.AreEqual(34.80m, BusinessReportRules.CostToRecord(gauze, individualUnits: false));
        Assert.IsNull(BusinessReportRules.CostToRecord(Supply(4, 8m, 0m), individualUnits: false), "Sin costo no se guarda un 0");

        var day = new DateTime(2026, 10, 6, 12, 0, 0);
        var lines = BusinessReportRules.Lines(new[]
        {
            ProductLine(1, day, 3, 4, 6m, 3.48m, individual: true),
            ProductLine(2, day, 3, 1, 60m, 34.80m, individual: false),
            ProductLine(3, day, 3, 2, 6m, null, individual: null) // de antes: a menos del precio del paquete → piezas
        }, new[] { gauze }, Array.Empty<ClinicalService>(), October);

        Assert.IsTrue(lines[0].IsLoosePieces);
        Assert.IsFalse(lines[1].IsLoosePieces);
        Assert.IsTrue(lines[2].IsLoosePieces, "La regla vieja: precio menor al del paquete");
        Assert.AreEqual(6.96m, lines[2].Cost, "Estimado por pieza: 2 × 3.48");

        var top = BusinessReportRules.TopProducts(lines, BusinessLineType.Insumos, BusinessRanking.Quantity);
        Assert.HasCount(2, top, "Las piezas sueltas van aparte de los paquetes");
        Assert.AreEqual(6, top[0].Quantity);
        Assert.AreEqual("6 piezas sueltas", top[0].QuantityText);
        Assert.AreEqual("1", top[1].QuantityText);
    }

    [TestMethod]
    public void TheConsultationFee_IsItsOwnType_AndServicesAndFreeChargesAreSeparated()
    {
        var day = new DateTime(2026, 10, 7, 10, 0, 0);
        var products = new[] { Med(1, 45m, 20m), Supply(2, 10m, 5m) };
        var services = new[] { Fee, Curacion };
        BusinessLineType TypeOf(BusinessSaleLine line) =>
            BusinessReportRules.Lines(new[] { line }, products, services, October).Single().Type;

        Assert.AreEqual(BusinessLineType.Consultas, TypeOf(ServiceLine(1, day, 1, "SERV-001", "Consulta Médica General", 300m)));
        Assert.AreEqual(BusinessLineType.Consultas, TypeOf(ServiceLine(1, day, null, "SERV-001", "Honorario", 300m)), "Sin servicio en el catálogo: por su código");
        Assert.AreEqual(BusinessLineType.Consultas, TypeOf(ServiceLine(1, day, 9, "SERV-009", "Consulta de seguimiento", 200m)), "Un servicio borrado con 'Consulta' en el nombre");
        Assert.AreEqual(BusinessLineType.Servicios, TypeOf(ServiceLine(1, day, 2, "SERV-002", "Curación", 250m)));
        Assert.AreEqual(BusinessLineType.Servicios, TypeOf(ServiceLine(1, day, 77, "SERV-077", "Retiro de puntos", 150m)), "Un servicio borrado sigue siendo servicio");
        Assert.AreEqual(BusinessLineType.Otros, TypeOf(ServiceLine(1, day, null, "CARGO", "Aplicación", 50m)));
        Assert.AreEqual(BusinessLineType.Insumos, TypeOf(ServiceLine(1, day, null, "INSUMO", "Venda", 35m)));
        Assert.AreEqual(BusinessLineType.Medicamentos, TypeOf(ProductLine(1, day, 1, 1, 45m, 20m)));
        Assert.AreEqual(BusinessLineType.Insumos, TypeOf(ProductLine(1, day, 2, 1, 10m, 5m)));
        Assert.AreEqual(BusinessLineType.Otros, TypeOf(ProductLine(1, day, 99, 1, 15m, null)), "Un producto que ya no existe");

        // Las mismas pruebas que el honorario de la consulta
        Assert.IsTrue(ConsultationAccountRules.IsConsultationFee("serv-001", "Otro", "Otra"));
        Assert.IsTrue(ConsultationAccountRules.IsConsultationFee("X", "Otro", "consulta"));
        Assert.IsFalse(ConsultationAccountRules.IsConsultationFee("SERV-002", "Curación", "Curaciones"));
    }

    [TestMethod]
    public void ThePeriod_UsesTheClinicDay_InMexicoCityAndInUtc()
    {
        // 05:30 UTC del 1 de noviembre: en el consultorio todavía es 31 de octubre (UTC−6)
        var instant = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var mexico = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");

        DateTime TodayIn(TimeZoneInfo zone)
        {
            using var _ = ClinicClock.Use(new ZoneProvider(instant, zone));
            return ClinicClock.Today;
        }

        var mexicoMonth = BusinessReportRules.PeriodFor(BusinessPeriodKind.EsteMes, TodayIn(mexico));
        var utcMonth = BusinessReportRules.PeriodFor(BusinessPeriodKind.EsteMes, TodayIn(TimeZoneInfo.Utc));
        Assert.AreEqual(October, mexicoMonth, "En el consultorio sigue siendo octubre");
        Assert.AreEqual(new BusinessPeriod(new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)), utcMonth);

        // La venta de las 23:30 del 31 (hora del consultorio) es de octubre; la de las 00:10 del 1, no
        var product = Med(1, 10m, 5m);
        var lines = BusinessReportRules.Lines(new[]
        {
            ProductLine(1, new DateTime(2026, 10, 31, 23, 30, 0), 1, 1, 10m, 5m),
            ProductLine(2, new DateTime(2026, 11, 1, 0, 10, 0), 1, 1, 10m, 5m)
        }, new[] { product }, Array.Empty<ClinicalService>(), BusinessReportRules.PeriodFor(BusinessPeriodKind.Hoy, TodayIn(mexico)));
        Assert.AreEqual(1, lines.Single().Source.SaleId);

        // La semana empieza el lunes; un rango al revés se voltea
        Assert.AreEqual(new DateTime(2026, 10, 26), BusinessReportRules.PeriodFor(BusinessPeriodKind.EstaSemana, new DateTime(2026, 10, 31)).From);
        Assert.AreEqual(new BusinessPeriod(new DateTime(2026, 10, 11), new DateTime(2026, 10, 21)),
            BusinessReportRules.PeriodFor(BusinessPeriodKind.Personalizado, new DateTime(2026, 10, 31), new DateTime(2026, 10, 20), new DateTime(2026, 10, 11)));
        Assert.AreEqual(new DateTime(2026, 10, 15), BusinessReportRules.ParseDate("15/10/2026"));
        Assert.IsNull(BusinessReportRules.ParseDate("15/10/20"));
    }

    [TestMethod]
    public void TheComparison_IsAgainstThePreviousEqualPeriod()
    {
        var today = new DateTime(2026, 10, 12);
        Assert.AreEqual(new BusinessPeriod(new DateTime(2026, 10, 11), today),
            BusinessReportRules.PreviousPeriod(BusinessPeriodKind.Hoy, BusinessReportRules.PeriodFor(BusinessPeriodKind.Hoy, today)));
        Assert.AreEqual(new BusinessPeriod(new DateTime(2026, 9, 1), new DateTime(2026, 9, 13)),
            BusinessReportRules.PreviousPeriod(BusinessPeriodKind.EsteMes, BusinessReportRules.PeriodFor(BusinessPeriodKind.EsteMes, today)),
            "Este mes al día 12: septiembre del 1 al 12");
        Assert.AreEqual(new BusinessPeriod(new DateTime(2026, 8, 1), new DateTime(2026, 9, 1)),
            BusinessReportRules.PreviousPeriod(BusinessPeriodKind.MesAnterior, BusinessReportRules.PeriodFor(BusinessPeriodKind.MesAnterior, today)));
        Assert.AreEqual(new BusinessPeriod(new DateTime(2027, 2, 1), new DateTime(2027, 3, 1)),
            BusinessReportRules.PreviousPeriod(BusinessPeriodKind.EsteMes, BusinessReportRules.PeriodFor(BusinessPeriodKind.EsteMes, new DateTime(2027, 3, 31))),
            "El 31 de marzo contra febrero completo");

        var product = Med(1, 100m, 50m);
        var lines = new[]
        {
            ProductLine(1, new DateTime(2026, 10, 2), 1, 3, 100m, 50m),
            ProductLine(2, new DateTime(2026, 9, 2), 1, 2, 100m, 50m)
        };
        var current = BusinessReportRules.Summarize(BusinessReportRules.Lines(lines, new[] { product }, Array.Empty<ClinicalService>(), October));
        var previous = BusinessReportRules.Summarize(BusinessReportRules.Lines(lines, new[] { product }, Array.Empty<ClinicalService>(),
            BusinessReportRules.PreviousPeriod(BusinessPeriodKind.MesAnterior, October)));
        var comparison = BusinessReportRules.Compare(current, previous);
        Assert.AreEqual(50.0m, comparison.SalesChangePercent);
        Assert.AreEqual(50.0m, comparison.ProfitChangePercent);
        Assert.AreEqual("▲ 50.0 %", BusinessReportRules.ChangeText(comparison.SalesChangePercent));
        Assert.AreEqual("▼ 33.3 %", BusinessReportRules.ChangeText(BusinessReportRules.Change(200m, 300m)));
        Assert.IsNull(BusinessReportRules.Change(10m, 0m), "Sin ventas antes no hay con qué comparar");
        Assert.AreEqual("sin comparación", BusinessReportRules.ChangeText(null));
        Assert.AreEqual(12.2m, BusinessReportRules.Percent(49m, 400m), "12.25 al par");
    }

    [TestMethod]
    public void TheTimeline_GoesByDayOrByMonth_WithEmptyDaysInZero()
    {
        var product = Med(1, 10m, 4m);
        var lines = BusinessReportRules.Lines(new[] { ProductLine(1, new DateTime(2026, 10, 3, 9, 0, 0), 1, 2, 10m, 4m) },
            new[] { product }, Array.Empty<ClinicalService>(), October);
        var daily = BusinessReportRules.Timeline(lines, October);
        Assert.HasCount(31, daily);
        Assert.AreEqual("03/10", daily[2].Label);
        Assert.AreEqual(20m, daily[2].Sales);
        Assert.AreEqual(12m, daily[2].Profit);
        Assert.AreEqual(0m, daily[0].Sales);

        var year = new BusinessPeriod(new DateTime(2026, 1, 1), new DateTime(2027, 1, 1));
        var monthly = BusinessReportRules.Timeline(BusinessReportRules.Lines(lines.Select(l => l.Source), new[] { product }, Array.Empty<ClinicalService>(), year), year);
        Assert.HasCount(12, monthly);
        Assert.AreEqual("10/2026", monthly[9].Label);
        Assert.AreEqual(20m, monthly[9].Sales);
    }

    [TestMethod]
    public void Purchases_GroupBySupplier_AndConsumptionCountsWhatConsultationsUsed()
    {
        var med = Med(1, 45m, 20m);
        var gauze = Supply(2, 60m, 30m, excludesVat: true, unitsPerPackage: 10);
        var day = new DateTime(2026, 10, 2, 9, 0, 0);
        var movements = new[]
        {
            new BusinessStockMovement(1, day, MovementType.EntradaCompra, 10, 18m, "Pedido LUIN #1"),
            new BusinessStockMovement(2, day, MovementType.EntradaCompra, 5, null, "Entrada de lote G-1"),
            new BusinessStockMovement(2, day, MovementType.SalidaConsulta, 1, 34.80m, "Uso"),
            new BusinessStockMovement(1, day, MovementType.SalidaVenta, 3, null, "Venta POS")
        };
        var purchases = BusinessStockRules.Purchases(movements, new[] { med, gauze }, October);
        Assert.AreEqual(2, purchases.Entries);
        Assert.AreEqual(1, purchases.Orders);
        Assert.AreEqual(180m + 174m, purchases.Cost, "10 × 18 guardado + 5 × 34.80 estimado");
        Assert.AreEqual(1, purchases.EstimatedCostLines);
        CollectionAssert.AreEqual(new[] { "LUIN", "Entrada sin pedido" }, purchases.BySupplier.Select(s => s.Supplier).ToArray());

        var lines = BusinessReportRules.Lines(new[]
        {
            ServiceLine(7, day, 2, "SERV-002", "Curación", 250m),
            ProductLine(7, day, 2, 3, 6m, 3.48m, individual: true),
            ProductLine(8, day, 2, 1, 60m, 34.80m) // venta de mostrador sin servicio: no es consumo
        }, new[] { med, gauze }, new[] { Fee, Curacion }, October);
        var consumption = BusinessStockRules.SupplyConsumption(movements, lines, new[] { med, gauze }, October);
        Assert.HasCount(2, consumption.Rows);
        var used = consumption.Rows.Single(r => !r.IsLoosePieces);
        Assert.AreEqual(1, used.UsedInConsultation);
        var charged = consumption.Rows.Single(r => r.IsLoosePieces);
        Assert.AreEqual(3, charged.ChargedWithServices);
        Assert.AreEqual(10.44m, charged.Cost);
        Assert.AreEqual(34.80m + 10.44m, consumption.Cost);
    }

    private sealed class ZoneProvider(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    // ================================================================== base de datos

    private string _dbPath = string.Empty;
    private AppDbContext _context = null!;
    private InventoryService _inventory = null!;
    private string _exportDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tpvc_negocio_{Guid.NewGuid():N}.db");
        _exportDir = Path.Combine(Path.GetTempPath(), $"tpvc_negocio_csv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_exportDir);
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        DatabaseInitializer.Initialize(_context, seedDemoData: false);
        _inventory = new InventoryService(_context);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _context?.Database.EnsureDeleted(); } catch { }
        _context?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_exportDir, recursive: true); } catch { }
    }

    private AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private async Task<Product> AddProductAsync(string code, string categoryWord, decimal price, decimal cost, int stock, bool excludesVat = false, int unitsPerPackage = 1)
    {
        var category = (await _inventory.GetCategoriesAsync()).First(c => c.Name.Contains(categoryWord));
        await _inventory.SaveProductAsync(new Product
        {
            Code = code, Name = $"Producto {code}", CategoryId = category.Id, Price = price, UnitCost = cost, CostExcludesVat = excludesVat,
            CurrentStock = stock, MinStock = 0, OptimalStock = 0, UnitsPerPackage = unitsPerPackage
        });
        return (await _inventory.GetProductsAsync(searchText: code)).Single(p => p.Code == code);
    }

    [TestMethod]
    public async Task RegisteringASale_KeepsTheCostAtThatMoment_PerPieceForLoosePieces()
    {
        var med = await AddProductAsync("NEG-MED", "Medicamentos", 45m, 20m, stock: 10);
        var gauze = await AddProductAsync("NEG-GAS", "Curación", 60m, 30m, stock: 5, excludesVat: true, unitsPerPackage: 10);
        var noCost = await AddProductAsync("NEG-JER", "Insumos", 8m, 0m, stock: 5);
        var fee = (await _inventory.GetClinicalServicesAsync()).Single(s => s.Code == "SERV-001");

        var sale = await _inventory.RegisterSaleAsync(new[]
        {
            new SaleLineRequest(-fee.Id, 1, 300m),
            new SaleLineRequest(med.Id, 2, 45m),
            new SaleLineRequest(gauze.Id, 4, 6m, IsIndividualUnits: true),
            new SaleLineRequest(noCost.Id, 1, 8m)
        }, "Paciente", "Efectivo");
        Assert.IsNotNull(sale);

        await using (var db = NewContext())
        {
            var items = await db.SaleItems.AsNoTracking().Where(i => i.SaleId == sale.Id).ToListAsync();
            Assert.IsNull(items.Single(i => i.IsService).UnitCost, "El honorario no tiene costo propio");
            Assert.AreEqual(20m, items.Single(i => i.ProductId == med.Id).UnitCost);
            Assert.AreEqual(3.48m, items.Single(i => i.ProductId == gauze.Id).UnitCost, "34.80 con IVA entre 10 piezas");
            Assert.IsNull(items.Single(i => i.ProductId == noCost.Id).UnitCost, "Sin costo no se guarda un 0");
        }

        // El costo del producto cambia después: la utilidad de esa venta no
        med.UnitCost = 30m;
        await _inventory.SaveProductAsync(med);
        var data = await _inventory.LoadBusinessReportAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(2));
        var lines = BusinessReportRules.Lines(data.SaleLines, data.Products, data.Services, new BusinessPeriod(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(2)));
        var summary = BusinessReportRules.Summarize(lines);
        Assert.AreEqual(300m + 90m + 24m + 8m, summary.Sales);
        Assert.AreEqual(40m + 13.92m, summary.Cost, "El costo guardado al vender");
        Assert.AreEqual(0, summary.EstimatedCostLines);
        Assert.AreEqual(1, summary.MissingCostLines, "La jeringa sin costo");
        Assert.AreEqual(BusinessLineType.Consultas, lines.Single(l => l.Source.IsService).Type);

        // Cancelado ya no cuenta
        Assert.IsTrue(await _inventory.CancelSaleAsync(sale.Id));
        data = await _inventory.LoadBusinessReportAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(2));
        Assert.IsEmpty(data.SaleLines, "La lectura ya no trae las canceladas");
    }

    [TestMethod]
    public async Task Entries_AndConsultationUsage_KeepTheirCost()
    {
        var med = await AddProductAsync("NEG-M2", "Medicamentos", 120m, 70m, stock: 0);
        var gauze = await AddProductAsync("NEG-G2", "Curación", 60m, 30m, stock: 0, excludesVat: true);
        var category = (await _inventory.GetCategoriesAsync()).First(c => c.Name.Contains("Medicamentos"));

        await _inventory.ReceiveSupplierOrderAsync(new SupplierReceipt
        {
            Supplier = "LUIN", OrderNumber = "5175", CategoryIdForNewProducts = category.Id,
            Lines = new List<SupplierReceiptLine>
            {
                new() { ProductId = med.Id, Sku = "WAN014", Description = "MED", Quantity = 3, UnitCost = 72.50m },
                new() { ProductId = gauze.Id, Sku = "GAS01", Description = "GASA", Quantity = 10, UnitCost = 30m }
            }
        });
        await _inventory.ReceiveBatchAsync(new ProductBatch { ProductId = med.Id, BatchNumber = "L-1", ExpirationDate = DateTime.Today.AddYears(1), Quantity = 2 });
        await _inventory.RegisterMovementAsync(new InventoryMovement { ProductId = gauze.Id, MovementType = MovementType.EntradaCompra, Quantity = 1, Reason = "Compra en farmacia" });
        await _inventory.RegisterQuickConsultationUsageAsync(gauze.Id, 2, null);
        await _inventory.RegisterMovementAsync(new InventoryMovement { ProductId = gauze.Id, MovementType = MovementType.SalidaMerma, Quantity = 1, Reason = "Se rompió" });

        await using (var db = NewContext())
        {
            var movements = await db.InventoryMovements.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
            Assert.AreEqual(72.50m, movements.Single(m => m.Reason == "Pedido LUIN #5175" && m.ProductId == med.Id).UnitCost, "El costo del renglón del pedido");
            Assert.AreEqual(34.80m, movements.Single(m => m.Reason == "Pedido LUIN #5175" && m.ProductId == gauze.Id).UnitCost, "Con el IVA que suma el producto");
            Assert.AreEqual(72.50m, movements.Single(m => m.Reason == "Entrada de lote L-1").UnitCost, "El lote recibido: el costo del producto en ese momento");
            Assert.AreEqual(34.80m, movements.Single(m => m.Reason == "Compra en farmacia").UnitCost);
            Assert.IsTrue(movements.Where(m => m.MovementType == MovementType.SalidaConsulta).All(m => m.UnitCost == 34.80m), "Cada parte del consumo");
            Assert.IsNull(movements.Single(m => m.MovementType == MovementType.SalidaMerma).UnitCost, "Los demás tipos no lo guardan");
        }

        var period = new BusinessPeriod(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(2));
        var data = await _inventory.LoadBusinessReportAsync(period.From, period.To);
        Assert.IsTrue(data.Movements.All(m => m.Type is MovementType.EntradaCompra or MovementType.SalidaConsulta), "Solo lo que usan las compras y el consumo");
        var purchases = BusinessStockRules.Purchases(data.Movements, data.Products, period);
        Assert.AreEqual(4, purchases.Entries);
        Assert.AreEqual(1, purchases.Orders);
        Assert.AreEqual(3 * 72.50m + 10 * 34.80m + 2 * 72.50m + 34.80m, purchases.Cost);
        Assert.AreEqual(0, purchases.EstimatedCostLines);
        CollectionAssert.AreEqual(new[] { "LUIN", "Entrada sin pedido" }, purchases.BySupplier.Select(s => s.Supplier).ToArray());

        var consumption = BusinessStockRules.SupplyConsumption(data.Movements, Array.Empty<BusinessLine>(), data.Products, period);
        Assert.AreEqual(2, consumption.Rows.Single().UsedInConsultation);
        Assert.AreEqual(69.60m, consumption.Cost);
    }

    [TestMethod]
    public async Task ThePanel_LoadsFiltersByTypeAndExportsCsv()
    {
        var med = await AddProductAsync("NEG-M3", "Medicamentos", 50m, 20m, stock: 10);
        var gauze = await AddProductAsync("NEG-G3", "Curación", 10m, 4m, stock: 10);
        var fee = (await _inventory.GetClinicalServicesAsync()).Single(s => s.Code == "SERV-001");
        await _inventory.RegisterSaleAsync(new[] { new SaleLineRequest(-fee.Id, 1, 300m), new SaleLineRequest(gauze.Id, 2, 10m) }, "Paciente", "Efectivo");
        await _inventory.RegisterSaleAsync(new[] { new SaleLineRequest(med.Id, 3, 50m) }, null, null);

        var vm = new BusinessViewModel(_inventory) { ExportFolder = _exportDir };
        Assert.AreEqual("Este mes", vm.PeriodName, "Abre en este mes");
        Assert.AreEqual("Medicamentos", vm.TopProductsName, "Los más vendidos empiezan en medicamentos");

        vm.PeriodName = "Personalizado";
        vm.CustomFromText = DateTime.Today.AddDays(-1).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        vm.CustomToText = DateTime.Today.AddDays(1).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        await vm.ApplyCustomRangeAsync();

        Assert.IsNull(vm.ErrorMessage);
        Assert.AreEqual(470m, vm.Summary.Sales);
        Assert.AreEqual(68m, vm.Summary.Cost, "3 × 20 + 2 × 4");
        Assert.AreEqual(2, vm.Summary.Tickets);
        Assert.AreEqual(235m, vm.Summary.AverageTicket);
        Assert.IsFalse(vm.HasCostNote, "Todo con costo guardado");
        CollectionAssert.AreEqual(new[] { BusinessLineType.Consultas, BusinessLineType.Medicamentos, BusinessLineType.Insumos }, vm.ByType.Select(r => r.Row.Type).ToArray());
        Assert.AreEqual(med.Id, vm.TopProducts.Single().ProductId);
        Assert.AreEqual("Consulta Médica General", vm.TopServices.Single().Name);
        Assert.HasCount(3, vm.ChartBars, "Un día antes, hoy y mañana");
        Assert.AreEqual(BusinessViewModel.ChartHeight, vm.ChartBars.Max(b => b.SalesHeight));

        // Un clic en «Insumos» filtra el resto; otro clic lo quita
        vm.SelectType("Insumos");
        Assert.AreEqual(BusinessLineType.Insumos, vm.TypeFilter);
        Assert.AreEqual(20m, vm.Summary.Sales);
        Assert.AreEqual(1, vm.Summary.Tickets);
        Assert.AreEqual(gauze.Id, vm.TopProducts.Single().ProductId, "Más vendidos sigue al filtro");
        Assert.IsFalse(vm.CanChooseTopProducts);
        Assert.AreEqual(BusinessLineType.Insumos, vm.ByType.Single(r => r.IsSelected).Row.Type);
        Assert.HasCount(1, vm.Consumption.Rows, "Las gasas cobradas en el ticket de la consulta");
        vm.SelectType("Insumos");
        Assert.AreEqual(BusinessViewModel.AllTypes, vm.TypeName);

        await vm.ExportCsvAsync();
        var file = Directory.GetFiles(_exportDir, "Negocio_*.csv").Single();
        var csv = await File.ReadAllTextAsync(file);
        StringAssert.Contains(csv, "PANEL DEL NEGOCIO");
        StringAssert.Contains(csv, "Ventas,470.00");
        StringAssert.Contains(csv, "Consultas,300.00,0.00,300.00,100.0");
        StringAssert.Contains(vm.StatusMessage!, file);

        // Un rango sin fechas completas no se aplica
        vm.CustomToText = "12/1";
        await vm.ApplyCustomRangeAsync();
        Assert.IsNotNull(vm.CustomRangeError);
    }

    [TestMethod]
    public async Task AnOlderDatabase_GetsTheCostColumns_AndItsRowsAreEstimated()
    {
        var med = await AddProductAsync("NEG-OLD", "Medicamentos", 45m, 20m, stock: 5);
        await _inventory.RegisterSaleAsync(new[] { (med.Id, 1, 45m) }, null, null);

        // Como la dejó v1.47.0 (revisión 17): sin las columnas del costo y sin la huella de ahora
        using (var db = NewContext())
        {
            ChangeLogTestSupport.RemoveChangeLog(db);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE SaleItems DROP COLUMN UnitCost; ALTER TABLE InventoryMovements DROP COLUMN UnitCost;");
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 0;");
        }
        using (var db = NewContext()) DatabaseInitializer.Initialize(db, seedDemoData: false);

        using (var db = NewContext())
        {
            var columns = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('SaleItems')").AsEnumerable().ToList();
            CollectionAssert.Contains(columns, "UnitCost");
            columns = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('InventoryMovements')").AsEnumerable().ToList();
            CollectionAssert.Contains(columns, "UnitCost");
            Assert.IsNull(db.SaleItems.AsNoTracking().Single().UnitCost, "La fila de antes queda sin costo");
        }

        var period = new BusinessPeriod(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(2));
        var data = await _inventory.LoadBusinessReportAsync(period.From, period.To);
        var summary = BusinessReportRules.Summarize(BusinessReportRules.Lines(data.SaleLines, data.Products, data.Services, period));
        Assert.AreEqual(20m, summary.Cost, "El costo de hoy");
        Assert.AreEqual(1, summary.EstimatedCostLines, "… marcado como estimado");
    }

    [TestMethod]
    public void TheBusinessReports_AreAllowedOnThePcThePhoneAndTheTablet_AndTheSectionShowsOnThePc()
    {
        Assert.IsTrue(DevicePermissions.CanViewBusinessReports(DeviceRole.WindowsDesktop));
        Assert.IsTrue(DevicePermissions.CanViewBusinessReports(DeviceRole.AndroidMobileAdmin));
        Assert.AreEqual(DevicePermissions.CanViewDashboard(DeviceRole.AndroidTablet), DevicePermissions.CanViewBusinessReports(DeviceRole.AndroidTablet),
            "La tablet sigue la regla del panel del consultorio");

        var main = new MainViewModel(_inventory, new ConsultationService(seedDemoPatients: false), new ThemeService(Path.Combine(_exportDir, "theme.json")),
            new AppUpdateService());
        var shell = main.ConfigurationShellVM;
        Assert.IsTrue(shell.HasBusinessSection);
        Assert.AreEqual("Resumen", shell.Sections.Single(s => s.Key == ConfigurationShellViewModel.SectionBusiness).Group);
        main.NavigateTo("Business");
        Assert.AreSame(shell.BusinessVM, shell.CurrentSubView);
        Assert.AreEqual(ConfigurationShellViewModel.SectionBusiness, ConfigurationShellViewModel.SearchSettings("utilidad").First().SectionKey);
    }
}
