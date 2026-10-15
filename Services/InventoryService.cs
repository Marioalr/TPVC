using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

public class DashboardMetrics
{
    public int TotalProducts { get; set; }
    public int LowStockCount { get; set; }
    public int ExpiringSoonCount { get; set; }
    public int ExpiredCount { get; set; }
    public int MovementsToday { get; set; }
    public int ConsumedInConsultationToday { get; set; }
    public int SalesTodayCount { get; set; }
    public decimal SalesTodayTotal { get; set; }

    /// <summary>Lo que costó lo que hay en existencia (productos activos con costo; IVA sumado donde no lo incluía).</summary>
    public decimal InventoryValue { get; set; }

    /// <summary>Productos activos con existencias y sin costo: no entran en <see cref="InventoryValue"/>.</summary>
    public int StockedProductsWithoutCost { get; set; }

    /// <summary>Para la tarjeta: "al costo", o cuántos productos con existencias no tienen costo todavía.</summary>
    public string InventoryValueDetail => StockedProductsWithoutCost == 0
        ? "al costo"
        : $"{StockedProductsWithoutCost} sin costo";
}

public class SystemAlertsSummary
{
    public int TodayAppointmentsCount { get; set; }
    public List<PatientAppointment> TodayAppointments { get; set; } = new();

    public int LowStockProductsCount { get; set; }
    public int LowStockCount => LowStockProductsCount;
    public List<Product> LowStockProducts { get; set; } = new();

    public int ExpiredBatchesCount { get; set; }
    public List<ProductBatch> ExpiredBatches { get; set; } = new();

    public int NearExpiryBatchesCount { get; set; }
    public List<ProductBatch> NearExpiryBatches { get; set; } = new();

    public bool HasAlerts => TodayAppointmentsCount > 0 || LowStockProductsCount > 0 || ExpiredBatchesCount > 0 || NearExpiryBatchesCount > 0;
}

/// <summary>
/// Acceso a datos del consultorio. Cada operación usa un DbContext de vida corta
/// (IDbContextFactory): las lecturas son sin tracking y las escrituras son atómicas.
/// Así varias vistas pueden recargar en paralelo sin el error "A second operation was
/// started on this context", la memoria no crece con el uso y los cambios hechos por
/// otro proceso (p.ej. una futura sincronización con servidor) se ven en la siguiente lectura.
/// </summary>
public partial class InventoryService
{
    public const int ExpirationWarningDays = 60;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    /// <summary>
    /// Paso 8.16: el contador de folios de la nube (lo asigna TursoSyncService). Sin él, o con la nube apagada o sin
    /// verificar, EXP y TKT salen del mayor número local + 1, como en v1.19.
    /// </summary>
    public Services.Folios.IFolioSource? Folios { get; set; }

    /// <summary>Cambios en productos, lotes, movimientos, ventas o servicios clínicos.</summary>
    public event Action? OnInventoryChanged;

    /// <summary>Cambios en expedientes de pacientes o notas de consulta.</summary>
    public event Action? OnPatientsChanged;

    /// <summary>Cambios en ventas, tickets, cortes o devoluciones/cancelaciones.</summary>
    public event Action? OnSalesChanged;

    /// <summary>Cambios en la agenda de citas médicas.</summary>
    public event Action? OnAppointmentsChanged;

    /// <summary>Dispara todos los eventos de cambio para sincronización o recargas masivas.</summary>
    public void NotifyAllChanged()
    {
        // Raise aísla cada suscriptor: un error en una vista no impide refrescar las demás
        Raise(OnInventoryChanged, "inventory");
        Raise(OnPatientsChanged, "patients");
        Raise(OnSalesChanged, "sales");
        Raise(OnAppointmentsChanged, "appointments");
    }

    public InventoryService(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Compatibilidad: crea contextos nuevos con las mismas opciones del contexto recibido.
    /// </summary>
    public InventoryService(AppDbContext context)
        : this(new AppDbContextFactory((DbContextOptions<AppDbContext>)context.GetService<IDbContextOptions>()))
    {
    }

    private AppDbContext CreateContext() => _contextFactory.CreateDbContext();

    private void NotifyInventoryChanged() => Raise(OnInventoryChanged, "inventory");

    private void NotifyPatientsChanged() => Raise(OnPatientsChanged, "patients");

    private void NotifySalesChanged() => Raise(OnSalesChanged, "sales");

    private static void Raise(Action? handlers, string area)
    {
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Error notificando cambios de {area}", ex);
            }
        }
    }

    // =========================================================================
    // PRODUCTOS Y CATEGORÍAS
    // =========================================================================

    public async Task<List<Product>> GetProductsAsync(
        string? searchText = null,
        int? categoryId = null,
        bool onlyLowStock = false,
        bool onlyExpiring = false)
    {
        await using var db = CreateContext();
        var query = db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Batches)
            .Where(p => p.IsActive);

        if (categoryId.HasValue && categoryId.Value > 0)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        if (onlyLowStock)
        {
            query = query.Where(p => p.MinStock > 0 && p.CurrentStock <= p.MinStock);
        }

        var products = await query.OrderBy(p => p.Name).ToListAsync();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var q = TextSearch.Normalize(searchText);
            products = products
                .Where(p => TextSearch.Matches(q, p.Name, p.Code, p.GenericName, p.Location))
                .ToList();
        }

        if (onlyExpiring)
        {
            products = products
                .Where(p => p.Batches.Any(b => b.IsAvailable && b.Quantity > 0 && b.DaysRemaining <= ExpirationWarningDays))
                .ToList();
        }

        return products;
    }

    public async Task<Product?> GetProductByIdAsync(int id)
    {
        await using var db = CreateContext();
        return await db.Products
            .AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Category)
            .Include(p => p.Batches)
            .Include(p => p.Movements)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<bool> IsProductCodeTakenAsync(string code, int excludeProductId = 0)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var normalized = code.Trim().ToUpper();
        await using var db = CreateContext();
        return await db.Products
            .AnyAsync(p => p.IsActive && p.Id != excludeProductId && p.Code.ToUpper() == normalized);
    }

    /// <param name="originalStock">
    /// Paso 8.19: la existencia que mostró el diálogo del producto al abrirse. Si el campo no cambió, la existencia no se
    /// toca: una venta que bajó mientras el diálogo estaba abierto no se revierte. Sin ella se compara con la guardada,
    /// como antes.
    /// </param>
    public async Task SaveProductAsync(Product product, int? originalStock = null)
    {
        product.Code = product.Code.Trim();
        product.Name = product.Name.Trim();

        if (await IsProductCodeTakenAsync(product.Code, product.Id))
        {
            throw new InvalidOperationException($"El código '{product.Code}' ya está registrado en el inventario. Debe ser único.");
        }

        await using var db = CreateContext();
        var now = DateTime.Now;
        product.UpdatedAt = now;

        if (product.Id == 0)
        {
            product.CreatedAt = now;
            product.Category = null;
            db.Products.Add(product);

            // Si tiene stock inicial, registrar movimiento de entrada (mismo guardado atómico)
            if (product.CurrentStock != 0)
            {
                db.InventoryMovements.Add(new InventoryMovement
                {
                    Product = product,
                    MovementType = product.CurrentStock > 0 ? MovementType.EntradaAjuste : MovementType.AjusteInventario,
                    Quantity = Math.Abs(product.CurrentStock),
                    Delta = product.CurrentStock,
                    StockBefore = 0,
                    StockAfter = product.CurrentStock,
                    Date = now,
                    Reason = "Inventario inicial de alta de producto",
                    Responsible = "Dr. Consulta"
                });
            }
        }
        else
        {
            var existing = await db.Products.FirstOrDefaultAsync(p => p.Id == product.Id);
            if (existing == null)
            {
                throw new InvalidOperationException("El producto ya no existe en la base de datos.");
            }

            // Un cambio manual de existencias se registra en el Kardex como ajuste, sin lote. Solo si el campo cambió: si
            // no, lo guardado (con lo que se vendió mientras tanto) se queda
            bool typed = product.CurrentStock != (originalStock ?? existing.CurrentStock);
            int delta = product.CurrentStock - existing.CurrentStock;
            if (typed && delta != 0)
            {
                db.InventoryMovements.Add(new InventoryMovement
                {
                    ProductId = existing.Id,
                    MovementType = MovementType.AjusteInventario,
                    Quantity = Math.Abs(delta),
                    Delta = delta,
                    StockBefore = existing.CurrentStock,
                    StockAfter = product.CurrentStock,
                    Date = now,
                    Reason = "Ajuste manual de existencias desde la ficha del producto",
                    Responsible = "Dr. Consulta"
                });
                existing.CurrentStock = product.CurrentStock;
            }

            existing.Code = product.Code;
            existing.Name = product.Name;
            existing.GenericName = product.GenericName;
            existing.Presentation = product.Presentation;
            existing.CategoryId = product.CategoryId;
            existing.Unit = product.Unit;
            existing.Price = product.Price;
            existing.UnitCost = product.UnitCost;
            existing.CostExcludesVat = product.CostExcludesVat;
            existing.SupplierCode = product.SupplierCode;
            existing.MinStock = product.MinStock;
            existing.OptimalStock = product.OptimalStock;
            existing.Location = product.Location;
            existing.RequiresRefrigeration = product.RequiresRefrigeration;
            existing.Notes = product.Notes;
            existing.UpdatedAt = now;
            existing.IsActive = product.IsActive;
        }

        await db.SaveChangesAsync();
        NotifyInventoryChanged();
    }

    public async Task DeleteProductAsync(int id)
    {
        await using var db = CreateContext();
        var product = await db.Products.FindAsync(id);
        if (product != null)
        {
            product.IsActive = false; // Borrado lógico
            product.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            NotifyInventoryChanged();
        }
    }

    public async Task<List<Category>> GetCategoriesAsync()
    {
        await using var db = CreateContext();
        return await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    // =========================================================================
    // LOTES (FEFO)
    // =========================================================================

    public async Task<List<ProductBatch>> GetBatchesAsync(int? productId = null)
    {
        await using var db = CreateContext();
        var query = db.ProductBatches
            .AsNoTracking()
            .Include(b => b.Product)
            .Where(b => b.Product == null || b.Product.IsActive);

        if (productId.HasValue)
        {
            query = query.Where(b => b.ProductId == productId.Value);
        }

        return await query
            .OrderBy(b => b.ExpirationDate)
            .ToListAsync();
    }

    /// <summary>
    /// Recepción de un lote nuevo: crea el lote, suma existencias y registra la entrada
    /// en el Kardex en una sola transacción.
    /// </summary>
    public async Task ReceiveBatchAsync(ProductBatch batch, string? responsible = "Dr. Consulta")
    {
        if (batch.Quantity <= 0) throw new InvalidOperationException("La cantidad del lote debe ser mayor a cero.");

        await using var db = CreateContext();
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == batch.ProductId)
            ?? throw new InvalidOperationException("Producto no encontrado");

        batch.Product = null;
        batch.IsDepleted = false;
        db.Entry(batch).State = EntityState.Added;

        int stockBefore = product.CurrentStock;
        product.CurrentStock += batch.Quantity;
        product.UpdatedAt = DateTime.Now;

        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = product.Id,
            Batch = batch,
            MovementType = MovementType.EntradaCompra,
            Quantity = batch.Quantity,
            Delta = batch.Quantity,
            StockBefore = stockBefore,
            StockAfter = product.CurrentStock,
            Date = DateTime.Now,
            Reason = $"Entrada de lote {batch.BatchNumber}",
            Responsible = responsible ?? "Dr. Consulta"
        });

        await db.SaveChangesAsync();
        NotifyInventoryChanged();
    }

    /// <summary>
    /// «Eliminar lote» (paso 8.19): el lote ya no se borra, se cierra (<see cref="ProductBatch.ClosedAtUtc"/>, con su
    /// cantidad en 0). Las piezas que mostraba salen de las existencias del producto, como antes sin dejarlas en negativo, y
    /// un movimiento con su lote lleva su libro a 0 (Delta = −libro del lote). Los movimientos del cierre llevan SyncId
    /// deterministas: dos equipos que cierran el mismo lote escriben las mismas filas. Un movimiento de otro equipo que
    /// llegue después sigue en este lote.
    /// </summary>
    public async Task DeleteBatchAsync(int id)
    {
        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var batch = await db.ProductBatches.Include(b => b.Product).FirstOrDefaultAsync(b => b.Id == id);
        if (batch?.Product == null || batch.IsClosed) return;

        int ledger = (await db.InventoryMovements
                .Where(m => m.BatchId == id)
                .Select(m => new { m.Delta, m.MovementType, m.Quantity, m.StockBefore, m.StockAfter })
                .ToListAsync())
            .Sum(m => StockLedger.EffectiveDelta(m.Delta, m.MovementType, m.Quantity, m.StockBefore, m.StockAfter));

        var lotPartId = StockLedger.CloseSyncId(batch.SyncId);
        var restPartId = StockLedger.CloseRestSyncId(batch.SyncId);
        var parts = new List<Guid> { lotPartId, restPartId };
        var written = (await db.InventoryMovements.Where(m => parts.Contains(m.SyncId)).Select(m => m.SyncId).ToListAsync()).ToHashSet();

        var now = DateTime.Now;
        var product = batch.Product;

        void Close(Guid syncId, int? batchId, int delta)
        {
            // Otro equipo ya cerró este lote y su movimiento llegó: esa parte no se vuelve a escribir ni a descontar
            if (delta == 0 || written.Contains(syncId)) return;
            int before = product.CurrentStock;
            product.CurrentStock = before + delta;
            product.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement
            {
                SyncId = syncId,
                ProductId = product.Id,
                BatchId = batchId,
                MovementType = MovementType.AjusteInventario,
                Quantity = Math.Abs(delta),
                Delta = delta,
                StockBefore = before,
                StockAfter = product.CurrentStock,
                Date = now,
                Reason = $"Eliminación del lote {batch.BatchNumber}",
                Responsible = "Dr. Consulta"
            });
        }

        // Lo que sale del producto es lo que el lote mostraba, sin pasar de lo que tiene (como antes del paso 8.19: un lote
        // de antes puede mostrar más que el producto). El libro del lote va a 0 con su lote, y lo que falte para que el libro
        // del producto baje lo mismo va sin lote (un lote de antes del Kardex completo puede mostrar otra cantidad que su
        // libro: FEFO anotaba solo el primer lote, SaveBatchAsync no dejaba movimiento). En el Kardex es un renglón
        int removed = Math.Min(batch.Quantity, Math.Max(product.CurrentStock, 0));
        Close(lotPartId, batch.Id, -ledger);
        Close(restPartId, null, -(removed - ledger));

        batch.Quantity = 0;
        batch.IsDepleted = true;
        batch.ClosedAtUtc = db.SyncClock.UtcNow + db.SyncClock.OffsetFor(db);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        NotifyInventoryChanged();
    }

    /// <summary>Lo que sale de un lote, o con <see cref="Batch"/> null de las piezas sin lote (paso 8.19).</summary>
    internal readonly record struct BatchAllocation(ProductBatch? Batch, int Quantity);

    /// <summary>
    /// Descuenta piezas de los lotes abiertos con piezas (FEFO: primero el que caduca antes; si se indica un lote
    /// preferido, primero ese) y regresa cuánto salió de cada uno, más lo que no alcanzó con los lotes, sin lote.
    /// </summary>
    internal static List<BatchAllocation> DeductFromBatches(Product product, int quantity, int? preferredBatchId = null)
    {
        int remaining = quantity;
        var activeBatches = product.Batches
            .Where(b => b.IsAvailable && b.Quantity > 0)
            .OrderBy(b => b.Id == preferredBatchId ? 0 : 1)
            .ThenBy(b => b.ExpirationDate)
            .ToList();

        var allocations = new List<BatchAllocation>();
        foreach (var batch in activeBatches)
        {
            if (remaining <= 0) break;
            int taken = Math.Min(batch.Quantity, remaining);
            batch.Quantity -= taken;
            if (batch.Quantity == 0) batch.IsDepleted = true;
            remaining -= taken;
            allocations.Add(new BatchAllocation(batch, taken));
        }
        if (remaining > 0) allocations.Add(new BatchAllocation(null, remaining));
        return allocations;
    }

    /// <summary>
    /// Paso 8.19: una salida deja un movimiento por cada lote del que salió y otro sin lote para el resto, con la existencia
    /// del producto bajando renglón por renglón. <paramref name="template"/> trae el tipo, la fecha, el motivo, el
    /// responsable y el renglón del ticket; no se guarda.
    /// </summary>
    private static void AddOutgoingMovements(AppDbContext db, Product product, IEnumerable<BatchAllocation> allocations, InventoryMovement template)
    {
        foreach (var allocation in allocations)
        {
            int before = product.CurrentStock;
            product.CurrentStock = before - allocation.Quantity;
            db.InventoryMovements.Add(new InventoryMovement
            {
                ProductId = product.Id,
                BatchId = allocation.Batch?.Id,
                SaleItem = template.SaleItem,
                MovementType = template.MovementType,
                Quantity = allocation.Quantity,
                Delta = -allocation.Quantity,
                StockBefore = before,
                StockAfter = product.CurrentStock,
                Date = template.Date,
                Reason = template.Reason,
                Responsible = template.Responsible
            });
        }
    }

    private static void EnsureStock(Product product, int quantity)
    {
        if (quantity <= 0)
        {
            throw new InvalidOperationException("La cantidad debe ser mayor a cero.");
        }
        if (quantity > product.CurrentStock)
        {
            throw new InvalidOperationException(
                $"Stock insuficiente para '{product.Name}': solicitado {quantity}, disponible {product.CurrentStock}.");
        }
    }

    // Registro rápido de consumo en consulta médica
    public async Task RegisterQuickConsultationUsageAsync(int productId, int quantity, string? notes, string? responsible = "Dr. Consulta")
    {
        await using var db = CreateContext();
        var product = await db.Products
            .Include(p => p.Batches)
            .FirstOrDefaultAsync(p => p.Id == productId)
            ?? throw new InvalidOperationException("Producto no encontrado");

        EnsureStock(product, quantity);

        var now = DateTime.Now;
        product.UpdatedAt = now;
        AddOutgoingMovements(db, product, DeductFromBatches(product, quantity), new InventoryMovement
        {
            MovementType = MovementType.SalidaConsulta,
            Date = now,
            Reason = string.IsNullOrWhiteSpace(notes) ? "Uso / aplicación durante consulta médica" : notes,
            Responsible = responsible ?? "Dr. Consulta"
        });

        await db.SaveChangesAsync();
        NotifyInventoryChanged();
    }

    // =========================================================================
    // VENTAS (POS)
    // =========================================================================

    // Registro de venta en Punto de Venta (POS) con carrito
    public Task<Sale?> RegisterSaleAsync(
        IEnumerable<(int ProductId, int Quantity, decimal UnitPrice)> items,
        string? patientName,
        string? paymentMethod,
        string? responsible = "Dr. / Farmacia")
    {
        return RegisterSaleAsync(
            items.Select(i => new SaleLineRequest(i.ProductId, i.Quantity, i.UnitPrice)),
            patientName,
            paymentMethod,
            responsible);
    }

    /// <summary>
    /// Registra una venta completa (productos y servicios) en una transacción: valida
    /// existencias, descuenta lotes por FEFO, genera movimientos de Kardex y persiste el
    /// ticket con folio consecutivo. Si algún producto no alcanza, no se guarda nada.
    /// </summary>
    public async Task<Sale?> RegisterSaleAsync(
        IEnumerable<SaleLineRequest> lines,
        string? patientName,
        string? paymentMethod,
        string? responsible = "Dr. / Farmacia")
    {
        var lineList = lines.Where(l => l.Quantity > 0).ToList();
        if (lineList.Count == 0) return null;

        string clientText = string.IsNullOrWhiteSpace(patientName) ? "Público General" : patientName.Trim();
        string methodText = string.IsNullOrWhiteSpace(paymentMethod) ? "Efectivo" : paymentMethod.Trim();
        string responsibleText = responsible ?? "Dr. / Farmacia";

        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var productIds = lineList.Where(l => l.ProductId > 0).Select(l => l.ProductId).Distinct().ToList();
        var serviceIds = lineList.Where(l => l.ProductId < 0).Select(l => -l.ProductId).Distinct().ToList();

        var products = await db.Products
            .Include(p => p.Batches)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);
        var services = await db.ClinicalServices
            .AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id);

        // Validar todo antes de modificar nada
        foreach (var group in lineList.Where(l => l.ProductId > 0).GroupBy(l => l.ProductId))
        {
            if (!products.TryGetValue(group.Key, out var product) || !product.IsActive)
            {
                var name = group.First().Description ?? $"#{group.Key}";
                throw new InvalidOperationException($"El producto '{name}' ya no existe en el inventario.");
            }
            EnsureStock(product, group.Sum(l => l.Quantity));
        }

        var now = DateTime.Now;
        var sale = new Sale
        {
            Folio = await NextSaleFolioAsync(db, now),
            LedgerVersion = Sale.CurrentLedgerVersion,
            Date = now,
            CustomerName = clientText,
            PaymentMethod = methodText,
            Cashier = responsibleText
        };

        foreach (var line in lineList)
        {
            if (line.ProductId > 0)
            {
                var product = products[line.ProductId];
                product.UpdatedAt = now;
                var item = new SaleItem
                {
                    ProductId = product.Id,
                    Code = line.Code ?? product.Code,
                    Description = line.Description ?? product.Name,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Subtotal = line.Quantity * line.UnitPrice
                };
                sale.Items.Add(item);

                // Paso 8.19: un movimiento por lote (y sin lote para el resto), cada uno con su renglón del ticket
                AddOutgoingMovements(db, product, DeductFromBatches(product, line.Quantity), new InventoryMovement
                {
                    SaleItem = item,
                    MovementType = MovementType.SalidaVenta,
                    Date = now,
                    Reason = SaleMovementReason(sale.Folio, clientText, methodText),
                    Responsible = responsibleText
                });
            }
            else
            {
                services.TryGetValue(-line.ProductId, out var service);
                sale.Items.Add(new SaleItem
                {
                    ClinicalServiceId = service?.Id,
                    IsService = true,
                    Code = line.Code ?? service?.Code ?? "SERV",
                    Description = line.Description ?? service?.Name ?? "Servicio clínico",
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Subtotal = line.Quantity * line.UnitPrice
                });
            }
        }

        sale.Total = sale.Items.Sum(i => i.Subtotal);
        db.Sales.Add(sale);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        NotifyInventoryChanged();
        NotifySalesChanged();
        return sale;
    }

    /// <summary>El motivo de la salida de una venta en el Kardex: "Venta POS {folio} - Paciente/Cliente: … (Efectivo)".</summary>
    private static string SaleMovementReason(string folio, string client, string method) =>
        $"{SaleMovementReasonPrefix(folio)}Paciente/Cliente: {client} ({method})";

    /// <summary>Así empieza el motivo de las salidas de una venta (desde v1.5.0): con él una venta anterior al paso 8.19 encuentra las suyas.</summary>
    private static string SaleMovementReasonPrefix(string folio) => $"Venta POS {folio} - ";

    /// <summary>
    /// El folio del ticket (paso 8.16): del contador de la nube (2 s como máximo, dentro de la transacción de la venta) o,
    /// sin conexión, con la letra del equipo; con la nube apagada, el mayor del día + 1.
    /// </summary>
    private async Task<string> NextSaleFolioAsync(AppDbContext db, DateTime date)
    {
        var series = Services.Folios.FolioFactory.Series(Services.Folios.FolioKind.Sale, date);
        var existing = await db.Sales
            .Where(s => s.Folio.StartsWith(series))
            .Select(s => s.Folio)
            .ToListAsync();
        return (Folios == null ? null : await Folios.NextAsync(Services.Folios.FolioKind.Sale, date, existing))
               ?? Services.Folios.FolioFactory.Local(Services.Folios.FolioKind.Sale, date, existing);
    }

    public async Task<List<Sale>> GetSalesAsync(DateTime from, DateTime to)
    {
        await using var db = CreateContext();
        return await db.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .Where(s => s.Date >= from && s.Date < to)
            .OrderByDescending(s => s.Date)
            .ToListAsync();
    }

    public async Task<Sale?> GetSaleByFolioAsync(string folio)
    {
        if (string.IsNullOrWhiteSpace(folio)) return null;
        var cleanFolio = folio.Trim();
        await using var db = CreateContext();
        // Paso 8.17: también por el folio con letra con que se cobró sin conexión
        var lower = cleanFolio.ToLower();
        return await db.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Folio.ToLower() == lower || (s.OriginalFolio != null && s.OriginalFolio.ToLower() == lower));
    }

    public async Task<List<Sale>> GetSalesHistoryAsync(DateTime? fromDate = null, DateTime? toDate = null, string? searchFolio = null, int limit = 150)
    {
        await using var db = CreateContext();
        var query = db.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .AsQueryable();

        if (fromDate.HasValue)
        {
            var from = fromDate.Value.Date;
            query = query.Where(s => s.Date >= from);
        }

        if (toDate.HasValue)
        {
            var to = toDate.Value.Date.AddDays(1);
            query = query.Where(s => s.Date < to);
        }

        if (!string.IsNullOrWhiteSpace(searchFolio))
        {
            var needle = searchFolio.Trim().ToLower();
            query = query.Where(s => s.Folio.ToLower().Contains(needle) || (s.OriginalFolio != null && s.OriginalFolio.ToLower().Contains(needle))
                                     || s.CustomerName.ToLower().Contains(needle));
        }

        return await query.OrderByDescending(s => s.Date).Take(limit).ToListAsync();
    }

    /// <summary>
    /// Cancela o devuelve un ticket previamente cobrado, reintegrando de forma atómica las existencias de sus productos con
    /// movimientos de devolución. Paso 8.19: cada salida vuelve a su propio lote, con un SyncId determinista: dos equipos
    /// que cancelan el mismo ticket escriben la misma fila, y si la devolución ya está aquí la cancelación cuenta como hecha.
    /// Una venta con <see cref="Sale.LedgerVersion"/> 1 cuyos movimientos no han llegado completos no se cancela
    /// ("Sincronice primero").
    /// </summary>
    public async Task<bool> CancelSaleAsync(int saleId, string reason = "Devolución / Cancelación de ticket", string responsible = "Dr. Titular")
    {
        if (saleId <= 0) return false;
        string reasonText = string.IsNullOrWhiteSpace(reason) ? "Devolución / Cancelación de ticket" : reason.Trim();
        string responsibleText = string.IsNullOrWhiteSpace(responsible) ? "Dr. / Farmacia" : responsible.Trim();

        await using var db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var sale = await db.Sales
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == saleId);

        if (sale == null)
        {
            throw new InvalidOperationException($"No se encontró la venta con ID {saleId}.");
        }

        if (sale.IsCancelled)
        {
            throw new InvalidOperationException($"El ticket {sale.Folio} ya se encuentra cancelado.");
        }

        var returns = await PlanSaleReturnsAsync(db, sale);

        // Las devoluciones que ya están aquí (este ticket se canceló en otro equipo y llegaron antes que el ticket
        // cancelado) cuentan como hechas: no se vuelven a sumar
        var returnIds = returns.Select(r => r.SyncId).ToList();
        var alreadyReturned = (await db.InventoryMovements.Where(m => returnIds.Contains(m.SyncId)).Select(m => m.SyncId).ToListAsync()).ToHashSet();

        var now = DateTime.Now;
        sale.IsCancelled = true;
        sale.CancelledAt = now;
        sale.CancellationReason = reasonText;

        var pending = returns.Where(r => !alreadyReturned.Contains(r.SyncId)).ToList();
        var productIds = pending.Select(r => r.ProductId).Distinct().ToList();
        var products = await db.Products
            .Include(p => p.Batches)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        foreach (var planned in pending)
        {
            if (!products.TryGetValue(planned.ProductId, out var product)) continue;
            int stockBefore = product.CurrentStock;
            product.CurrentStock = stockBefore + planned.Quantity;
            product.UpdatedAt = now;

            // A su propio lote. Un lote agotado vuelve a contar para FEFO; uno cerrado se queda cerrado
            if (planned.BatchId is int batchId && product.Batches.FirstOrDefault(b => b.Id == batchId) is { } batch)
            {
                batch.Quantity += planned.Quantity;
                if (!batch.IsClosed && batch.Quantity > 0) batch.IsDepleted = false;
            }

            db.InventoryMovements.Add(new InventoryMovement
            {
                SyncId = planned.SyncId,
                ProductId = product.Id,
                BatchId = planned.BatchId,
                SaleItemId = planned.SaleItemId,
                MovementType = MovementType.EntradaDevolucion,
                Quantity = planned.Quantity,
                Delta = planned.Quantity,
                StockBefore = stockBefore,
                StockAfter = product.CurrentStock,
                Date = now,
                Reason = $"Devolución por cancelación de ticket {sale.Folio}: {reasonText}",
                Responsible = responsibleText
            });
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        NotifyInventoryChanged();
        NotifySalesChanged();
        return true;
    }

    /// <summary>Una devolución por escribir al cancelar un ticket: su SyncId determinista, adónde regresa y cuántas piezas.</summary>
    private sealed record PlannedReturn(Guid SyncId, int ProductId, int? BatchId, int? SaleItemId, int Quantity);

    /// <summary>
    /// Las devoluciones de un ticket, con la regla de su <see cref="Sale.LedgerVersion"/> (no según lo que encuentre):
    /// <list type="bullet">
    /// <item>1: una por cada salida del ticket (por su renglón, <see cref="InventoryMovement.SaleItemId"/>), a su lote, con
    /// SyncId uuid5("return:" + SyncId de la salida). Si a un renglón le faltan salidas (la suma no da su cantidad), se niega.</item>
    /// <item>0 (venta anterior): una por producto, con lo que suman sus renglones, al lote de la primera salida que diga
    /// "Venta POS {folio}" (o su folio sin conexión, paso 8.17) o sin lote si no hay, con SyncId uuid5("return:" + SyncId de la
    /// venta + ":" + SyncId del producto).</item>
    /// </list>
    /// </summary>
    private static async Task<List<PlannedReturn>> PlanSaleReturnsAsync(AppDbContext db, Sale sale)
    {
        var productItems = sale.Items.Where(i => !i.IsService && i.ProductId.HasValue && i.Quantity > 0).ToList();
        if (productItems.Count == 0) return new List<PlannedReturn>();

        if (sale.LedgerVersion >= Sale.CurrentLedgerVersion)
        {
            var itemIds = productItems.Select(i => i.Id).ToList();
            var exits = await db.InventoryMovements.AsNoTracking()
                .Where(m => m.SaleItemId != null && itemIds.Contains(m.SaleItemId.Value) && m.MovementType == MovementType.SalidaVenta)
                .ToListAsync();
            foreach (var item in productItems)
            {
                if (exits.Where(m => m.SaleItemId == item.Id).Sum(m => Math.Abs(m.Delta)) != item.Quantity)
                {
                    throw new InvalidOperationException(
                        $"Este equipo todavía no tiene todos los movimientos del ticket {sale.Folio} ({item.Description}). Sincronice primero y vuelva a intentarlo.");
                }
            }
            // Las de un renglón juntas: en el Kardex son una operación
            return exits
                .OrderBy(m => m.Date).ThenBy(m => m.SaleItemId).ThenBy(m => m.SyncId)
                .Select(m => new PlannedReturn(StockLedger.ReturnSyncId(m.SyncId), m.ProductId, m.BatchId, m.SaleItemId, Math.Abs(m.Delta)))
                .ToList();
        }

        var productIds = productItems.Select(i => i.ProductId!.Value).Distinct().ToList();
        var productSyncIds = await db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.SyncId);
        var prefixes = new[] { sale.Folio, sale.OriginalFolio }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => SaleMovementReasonPrefix(f!))
            .ToList();
        var exitsByProduct = (await db.InventoryMovements.AsNoTracking()
                .Where(m => productIds.Contains(m.ProductId) && m.MovementType == MovementType.SalidaVenta && m.Reason != null
                            && m.Reason.StartsWith("Venta POS "))
                .Select(m => new { m.ProductId, m.BatchId, m.Reason, m.Date, m.SyncId })
                .ToListAsync())
            .Where(m => prefixes.Any(prefix => m.Reason!.StartsWith(prefix, StringComparison.Ordinal)))
            .GroupBy(m => m.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Date).ThenBy(m => m.SyncId).First().BatchId);

        return productItems
            .GroupBy(i => i.ProductId!.Value)
            .Where(g => productSyncIds.ContainsKey(g.Key))
            .Select(g => new PlannedReturn(StockLedger.LegacyReturnSyncId(sale.SyncId, productSyncIds[g.Key]), g.Key,
                exitsByProduct.GetValueOrDefault(g.Key), null, g.Sum(i => i.Quantity)))
            .ToList();
    }

    /// <summary>
    /// Calcula el corte de caja diario con desglose de métodos de pago y servicios vs productos.
    /// </summary>
    public async Task<DailyCashCut> GetDailyCashCutAsync(DateTime date, ClinicProfile? profile = null)
    {
        var day = date.Date;
        var nextDay = day.AddDays(1);

        await using var db = CreateContext();
        var sales = await db.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .Where(s => s.Date >= day && s.Date < nextDay)
            .OrderByDescending(s => s.Date)
            .ToListAsync();

        // Paso 8.11: las cuentas viven en CashCutRules (sin base ni WPF), la referencia del corte del celular
        return CashCutRules.Compute(sales, day);
    }

    /// <summary>
    /// Exporta el corte de caja a formato CSV compatible con Microsoft Excel (UTF-8 con BOM).
    /// </summary>
    public string ExportDailyCashCutToCsv(DailyCashCut cut, ClinicProfile? profile = null)
    {
        // Separador "," (configuración regional de México) y sin BOM manual: File.WriteAllText con
        // Encoding.UTF8 ya escribe el BOM; antes se duplicaba y Excel mostraba basura en la primera celda
        var sb = new StringBuilder();
        void Row(params object?[] cells) => sb.AppendLine(string.Join(",", cells.Select(c => CsvCell(c))));

        string clinic = profile != null && !string.IsNullOrWhiteSpace(profile.ClinicName) ? profile.ClinicName : "Consultorio Médico";
        string doctor = profile != null && !string.IsNullOrWhiteSpace(profile.DoctorName) ? profile.DoctorName : "Dr. Titular";
        string license = profile != null && !string.IsNullOrWhiteSpace(profile.ProfessionalLicense) ? profile.ProfessionalLicense : "N/D";

        Row("CORTE DE CAJA DIARIO", clinic);
        Row("Fecha de Corte", $"{cut.Date:dd/MM/yyyy}");
        Row("Médico / Titular", doctor);
        Row("Cédula Profesional", license);
        Row("Generado el", $"{DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine();

        Row("RESUMEN DE INGRESOS");
        Row("Total Ventas Netas", Money(cut.TotalSalesAmount));
        Row("Tickets Cobrados", cut.ActiveTicketsCount);
        Row("Tickets Cancelados", cut.CancelledTicketsCount, "Importe Cancelado", Money(cut.TotalCancelledAmount));
        sb.AppendLine();

        Row("DESGLOSE POR MÉTODO DE PAGO");
        Row("Efectivo", Money(cut.TotalCash));
        Row("Tarjeta de Débito", Money(cut.TotalDebitCard));
        Row("Tarjeta de Crédito", Money(cut.TotalCreditCard));
        Row("Transferencia Electrónica", Money(cut.TotalTransfer));
        if (cut.TotalOtherPaymentMethods > 0)
        {
            Row("Otros Métodos", Money(cut.TotalOtherPaymentMethods));
        }
        sb.AppendLine();

        Row("DESGLOSE SERVICIOS CLÍNICOS VS PRODUCTOS DE FARMACIA");
        Row("Servicios Médicos (Consultas/Procedimientos)", Money(cut.TotalServicesAmount), "Servicios Realizados", cut.TotalServicesCount);
        Row("Productos de Inventario / Farmacia", Money(cut.TotalProductsAmount), "Piezas Despachadas", cut.TotalProductsUnitsSold);
        sb.AppendLine();

        Row("DETALLE DE TICKETS DEL DÍA");
        Row("Folio", "Hora", "Paciente / Cliente", "Método de Pago", "Productos ($)", "Servicios ($)", "Total ($)", "Estado", "Motivo Cancelación");

        foreach (var sale in cut.Sales.OrderBy(s => s.Date))
        {
            decimal prodSub = sale.Items.Where(i => !i.IsService).Sum(i => i.Subtotal);
            decimal servSub = sale.Items.Where(i => i.IsService).Sum(i => i.Subtotal);
            Row(sale.Folio, $"{sale.Date:HH:mm:ss}", sale.CustomerName, sale.PaymentMethod, Money(prodSub), Money(servSub), Money(sale.Total),
                sale.IsCancelled ? "CANCELADO" : "COBRADO", sale.IsCancelled ? sale.CancellationReason : string.Empty);
        }

        return sb.ToString();
    }

    private static string Money(decimal amount) => "$" + amount.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Celda CSV: se entrecomilla si contiene coma, comillas o saltos de línea.</summary>
    private static string CsvCell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{CsvEscape(text)}\"" : text;
    }

    private static string CsvEscape(string? text) => (text ?? string.Empty).Replace("\"", "\"\"");

    /// <summary>
    /// Siembra pacientes y productos de demostración a solicitud explícita (asistente de inicio).
    /// </summary>
    public async Task SeedDemoDataAsync()
    {
        await using var db = CreateContext();
        DatabaseInitializer.SeedDemoData(db);
        NotifyInventoryChanged();
        NotifyPatientsChanged();
        NotifySalesChanged();
    }

    /// <summary>
    /// Registro genérico de movimiento. Una entrada suma sus piezas (a su lote, si trae uno); una salida descuenta por FEFO,
    /// empezando por el lote que traiga, y desde el paso 8.19 deja un movimiento por lote y otro sin lote para el resto.
    /// </summary>
    public async Task RegisterMovementAsync(InventoryMovement movement)
    {
        if (movement.Quantity <= 0) throw new InvalidOperationException("La cantidad debe ser mayor a cero.");

        await using var db = CreateContext();
        var product = await db.Products
            .Include(p => p.Batches)
            .FirstOrDefaultAsync(p => p.Id == movement.ProductId)
            ?? throw new InvalidOperationException("Producto no encontrado");

        movement.Product = null;
        movement.Batch = null;
        movement.SaleItem = null;
        movement.StockBefore = product.CurrentStock;

        var now = DateTime.Now;
        movement.Date = now;
        product.UpdatedAt = now;

        if (movement.IsEntry)
        {
            if (movement.BatchId is int batchId)
            {
                var batch = product.Batches.FirstOrDefault(b => b.Id == batchId)
                    ?? throw new InvalidOperationException("El lote no es de este producto.");
                batch.Quantity += movement.Quantity;
                if (!batch.IsClosed) batch.IsDepleted = false;
            }
            movement.Delta = movement.Quantity;
            movement.StockAfter = product.CurrentStock + movement.Quantity;
            product.CurrentStock = movement.StockAfter;
            db.Entry(movement).State = EntityState.Added;
        }
        else
        {
            EnsureStock(product, movement.Quantity);
            AddOutgoingMovements(db, product, DeductFromBatches(product, movement.Quantity, movement.BatchId), movement);
        }

        await db.SaveChangesAsync();
        NotifyInventoryChanged();
    }

    public async Task<List<InventoryMovement>> GetRecentMovementsAsync(int count = 50)
    {
        await using var db = CreateContext();
        return await db.InventoryMovements
            .AsNoTracking()
            .Include(m => m.Product)
            .ThenInclude(p => p!.Category)
            .Include(m => m.Batch)
            .OrderByDescending(m => m.Date)
            .ThenByDescending(m => m.Id)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// El Kardex como lo ve el médico (paso 8.19): las <paramref name="count"/> operaciones más recientes, con los
    /// movimientos por lote de cada una unidos en un renglón (<see cref="KardexRows"/>).
    /// </summary>
    public async Task<List<InventoryMovement>> GetKardexAsync(int count = 50)
    {
        await using var db = CreateContext();
        var query = db.InventoryMovements
            .AsNoTracking()
            .Include(m => m.Product)
            .ThenInclude(p => p!.Category)
            .Include(m => m.Batch)
            .OrderByDescending(m => m.Date)
            .ThenByDescending(m => m.Id);

        // Las partes de una operación tienen la misma hora: la última hora leída puede seguir en lo que no se leyó, así que se
        // deja fuera y se leen más renglones hasta juntar las operaciones completas que se muestran
        for (int take = count + 16; ; take *= 2)
        {
            var rows = await query.Take(take).ToListAsync();
            bool all = rows.Count < take;
            if (!all)
            {
                var lastDate = rows[^1].Date;
                rows = rows.Where(r => r.Date != lastDate).ToList();
            }
            var operations = KardexRows.Group(rows);
            if (all || operations.Count >= count) return operations.Take(count).ToList();
        }
    }

    /// <summary>Paso 8.19: lo guardado contra el libro de existencias, sin cambiar nada (<see cref="StockLedger.VerifyAsync"/>).</summary>
    public async Task<StockLedgerReport> VerifyStockLedgerAsync()
    {
        await using var db = CreateContext();
        return await StockLedger.VerifyAsync(db);
    }

    public async Task<DashboardMetrics> GetDashboardMetricsAsync()
    {
        var today = ClinicClock.Today;
        var tomorrow = today.AddDays(1);
        var expiringLimit = today.AddDays(ExpirationWarningDays + 1);

        await using var db = CreateContext();

        // Agregados calculados en SQLite: no se materializan catálogos completos
        var activeProducts = db.Products.Where(p => p.IsActive);
        var activeBatches = db.ProductBatches
            .Where(ProductBatch.IsAvailableExpression)
            .Where(b => b.Quantity > 0 && (b.Product == null || b.Product.IsActive));
        var todayMovements = db.InventoryMovements.Where(m => m.Date >= today);
        var todaySales = db.Sales.Where(s => s.Date >= today && s.Date < tomorrow);

        // SQLite guarda los decimales como texto: el valor del inventario se suma en memoria (solo lo que tiene existencias)
        var stocked = await activeProducts.Where(p => p.CurrentStock > 0)
            .Select(p => new Product { CurrentStock = p.CurrentStock, UnitCost = p.UnitCost, CostExcludesVat = p.CostExcludesVat })
            .ToListAsync();

        return new DashboardMetrics
        {
            InventoryValue = stocked.Sum(p => p.InventoryValue),
            StockedProductsWithoutCost = stocked.Count(p => !p.HasCost),
            TotalProducts = await activeProducts.CountAsync(),
            LowStockCount = await activeProducts.CountAsync(p => p.MinStock > 0 && p.CurrentStock <= p.MinStock),
            ExpiringSoonCount = await activeBatches.CountAsync(b => b.ExpirationDate >= today && b.ExpirationDate < expiringLimit),
            ExpiredCount = await activeBatches.CountAsync(b => b.ExpirationDate < today),
            // Operaciones, no renglones del libro (paso 8.19): las partes de una operación comparten producto, tipo, hora,
            // renglón del ticket, motivo y responsable (KardexRows.SameOperation); una fila anterior es su propia operación
            MovementsToday = await todayMovements.CountAsync(m => m.Delta == 0)
                             + await todayMovements.Where(m => m.Delta != 0)
                                 .Select(m => new { m.ProductId, m.MovementType, m.Date, m.SaleItemId, m.Reason, m.Responsible })
                                 .Distinct()
                                 .CountAsync(),
            ConsumedInConsultationToday = await todayMovements
                .Where(m => m.MovementType == MovementType.SalidaConsulta)
                .SumAsync(m => (int?)m.Quantity) ?? 0,
            SalesTodayCount = await todaySales.CountAsync(),
            // SQLite no suma decimal en servidor: se suman en memoria los totales del día
            SalesTodayTotal = (await todaySales.Select(s => s.Total).ToListAsync()).Sum()
        };
    }

    // ==========================================
    // SERVICIOS CLÍNICOS Y PAQUETES DE INSUMOS
    // ==========================================
    /// <summary>Servicios para cobrar (solo los activos) o, en Configuración, también los desactivados.</summary>
    public async Task<List<ClinicalService>> GetClinicalServicesAsync(bool includeInactive = false)
    {
        await using var db = CreateContext();
        return await db.ClinicalServices
            .AsNoTracking()
            .Where(s => includeInactive || s.IsActive)
            .Include(s => s.DefaultProducts)
                .ThenInclude(dp => dp.Product)
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<ClinicalService?> GetClinicalServiceByIdAsync(int id)
    {
        await using var db = CreateContext();
        return await db.ClinicalServices
            .AsNoTracking()
            .Include(s => s.DefaultProducts)
                .ThenInclude(dp => dp.Product)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<ClinicalService> SaveClinicalServiceAsync(
        ClinicalService service,
        IEnumerable<(int ProductId, int Quantity, string? Instructions)> defaultProducts)
    {
        var defaults = defaultProducts
            .Select(d => new ServiceDefaultProduct
            {
                ProductId = d.ProductId,
                Quantity = Math.Max(1, d.Quantity),
                Instructions = d.Instructions
            })
            .ToList();

        await using var db = CreateContext();

        if (service.Id == 0)
        {
            service.CreatedAt = DateTime.Now;
            // Solo FKs: evita que EF intente insertar productos ya existentes del grafo
            service.DefaultProducts = defaults;
            db.Entry(service).State = EntityState.Added;
            foreach (var dp in defaults)
            {
                dp.ClinicalService = service;
                db.Entry(dp).State = EntityState.Added;
            }
        }
        else
        {
            var existing = await db.ClinicalServices
                .Include(s => s.DefaultProducts)
                .FirstOrDefaultAsync(s => s.Id == service.Id)
                ?? throw new InvalidOperationException("El servicio clínico ya no existe.");

            existing.Code = service.Code;
            existing.Name = service.Name;
            existing.Description = service.Description;
            existing.Category = service.Category;
            existing.Price = service.Price;
            existing.EstimatedDurationMinutes = service.EstimatedDurationMinutes;
            existing.IsActive = service.IsActive;

            // Por producto, sin borrar y volver a insertar: cada renglón conserva su SyncId, solo sube lo que cambió
            // y no se generan lápidas de lo que sigue igual (paso 8.4)
            var wanted = defaults.GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.Last());
            foreach (var current in existing.DefaultProducts.ToList())
            {
                if (!wanted.Remove(current.ProductId, out var desired))
                {
                    db.ServiceDefaultProducts.Remove(current);
                    continue;
                }
                if (current.Quantity != desired.Quantity) current.Quantity = desired.Quantity;
                if (current.Instructions != desired.Instructions) current.Instructions = desired.Instructions;
            }
            foreach (var dp in wanted.Values)
            {
                dp.ClinicalServiceId = existing.Id;
                db.ServiceDefaultProducts.Add(dp);
            }
        }

        await db.SaveChangesAsync();
        NotifyInventoryChanged();
        return service;
    }

    // =========================================================================
    // EXPEDIENTES CLÍNICOS Y NOTAS DE EVOLUCIÓN (NOM-004-SSA3)
    // =========================================================================

    public async Task<List<PatientRecord>> GetPatientsAsync(string? searchText = null, bool activeOnly = true)
    {
        await using var db = CreateContext();
        var query = db.Patients
            .AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Consultations)
            .AsQueryable();

        if (activeOnly)
        {
            query = query.Where(p => p.IsActive);
        }

        var patients = await query.OrderBy(p => p.FullName).ToListAsync();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var q = TextSearch.Normalize(searchText);
            patients = patients
                .Where(p => TextSearch.Matches(q, p.FullName, p.RecordNumber, p.OriginalFolio ?? string.Empty, p.Phone, p.LastDiagnosis, p.Email))
                .ToList();
        }

        // Paso 8.21: cada instancia lleva la foto con que se guarda solo lo que cambió
        PatientFields.Track(patients);
        return patients;
    }

    /// <summary>
    /// Listado ligero de expedientes (sin el texto de las consultas). Solo trae cuántas consultas
    /// tiene cada paciente; las notas se cargan al abrir el expediente con <see cref="GetPatientConsultationsAsync"/>.
    /// Evita cargar en memoria años de historia clínica de todos los pacientes en cada guardado.
    /// </summary>
    public async Task<List<PatientRecord>> GetPatientSummariesAsync(bool activeOnly = true)
    {
        await using var db = CreateContext();
        var query = db.Patients.AsNoTracking();
        if (activeOnly) query = query.Where(p => p.IsActive);

        var rows = await query
            .OrderBy(p => p.FullName)
            .Select(p => new { Patient = p, Count = p.Consultations.Count })
            .ToListAsync();

        foreach (var row in rows)
        {
            row.Patient.ConsultationCount = row.Count;
            PatientFields.Track(row.Patient);
        }
        return rows.Select(r => r.Patient).ToList();
    }

    public async Task<List<PatientConsultation>> GetPatientConsultationsAsync(int patientId)
    {
        await using var db = CreateContext();
        return await db.PatientConsultations
            .AsNoTracking()
            .Where(c => c.PatientRecordId == patientId)
            .OrderByDescending(c => c.Date)
            .ToListAsync();
    }

    /// <summary>
    /// Corrige la receta de una consulta ya terminada (desde v1.24.0). Si es la última consulta del paciente y no hay
    /// otra en curso, la receta vigente del expediente cambia igual (es la que se reimprime desde la ficha). Lo que se
    /// cobró no cambia: la cuenta de esa consulta ya se cerró.
    /// </summary>
    /// <param name="updateActivePrescription">También la receta vigente del expediente (la consulta es la última).</param>
    /// <returns>
    /// La receta vigente tal como quedó en el expediente (con sus <c>RowId</c>), o null si no se cambió. Paso 8.21: la ficha
    /// pone esos mismos renglones en la instancia abierta y los marca como guardados.
    /// </returns>
    public async Task<List<PatientPrescriptionItem>?> UpdateConsultationPrescriptionAsync(int consultationId, IReadOnlyList<PatientPrescriptionItem> items, bool updateActivePrescription)
    {
        await using var db = CreateContext();
        var consultation = await db.PatientConsultations.FirstOrDefaultAsync(c => c.Id == consultationId)
            ?? throw new InvalidOperationException("La consulta ya no existe.");
        // Copias sin "Se surte aquí": la receta corregida no vuelve a cobrar nada
        var saved = items.Select(i => new PatientPrescriptionItem
        {
            MedicineName = i.MedicineName.Trim(),
            Dosage = i.Dosage.Trim(),
            RecommendedQuantity = Math.Max(1, i.RecommendedQuantity),
            Presentation = string.IsNullOrWhiteSpace(i.Presentation) ? null : i.Presentation.Trim()
        }).Where(i => i.MedicineName.Length > 0).ToList();
        consultation.Prescriptions = saved;

        List<PatientPrescriptionItem>? active = null;
        if (updateActivePrescription)
        {
            var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == consultation.PatientRecordId);
            if (patient != null)
            {
                patient.ActivePrescription = saved.Select(i => new PatientPrescriptionItem
                {
                    MedicineName = i.MedicineName, Dosage = i.Dosage, RecommendedQuantity = i.RecommendedQuantity, Presentation = i.Presentation
                }).ToList();
                active = patient.ActivePrescription;
            }
        }

        await db.SaveChangesAsync();
        NotifyPatientsChanged();
        return active;
    }

    /// <summary>
    /// Los estudios de laboratorio que el consultorio ya pidió y no están en el catálogo, los más pedidos primero (desde
    /// v1.25.0): vuelven a sugerirse en las consultas siguientes.
    /// </summary>
    public async Task<IReadOnlyList<LabOrderStudy>> GetCustomLabStudiesAsync()
    {
        await using var db = CreateContext();
        var orders = await db.PatientConsultations
            .AsNoTracking()
            .Where(c => c.LabOrderJson != "")
            .Select(c => c.LabOrderJson)
            .ToListAsync();
        return orders
            .SelectMany(json => LabOrder.FromJson(json).Studies)
            .Where(s => LabCatalog.Find(s.Name) == null)
            .GroupBy(s => TextSearch.Normalize(s.Name))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.First().Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public async Task<PatientRecord?> GetPatientByIdAsync(int id)
    {
        await using var db = CreateContext();
        var patient = await db.Patients
            .AsNoTracking()
            .Include(p => p.Consultations.OrderByDescending(c => c.Date))
            .FirstOrDefaultAsync(p => p.Id == id);
        return patient == null ? null : PatientFields.Track(patient);
    }

    public async Task<bool> IsRecordNumberTakenAsync(string recordNumber, int excludePatientId = 0)
    {
        if (string.IsNullOrWhiteSpace(recordNumber)) return false;
        var normalized = recordNumber.Trim().ToUpper();
        await using var db = CreateContext();
        // Paso 8.17: también el folio con letra de uno ya regularizado
        return await db.Patients
            .AnyAsync(p => p.IsActive && p.Id != excludePatientId
                           && (p.RecordNumber.ToUpper() == normalized || (p.OriginalFolio != null && p.OriginalFolio.ToUpper() == normalized)));
    }

    /// <summary>
    /// El siguiente folio probable (el mayor de la base + 1), para mostrarlo al abrir el formulario. Con la nube, el número
    /// definitivo se toma al guardar (paso 8.16): otro equipo pudo usar este mientras tanto.
    /// </summary>
    public async Task<string> GenerateNextRecordNumberAsync()
    {
        var today = DateTime.Today;
        return Services.Folios.FolioFactory.Local(Services.Folios.FolioKind.Patient, today, await RecordNumbersInSeriesAsync(today));
    }

    private async Task<List<string>> RecordNumbersInSeriesAsync(DateTime today)
    {
        var series = Services.Folios.FolioFactory.Series(Services.Folios.FolioKind.Patient, today);
        await using var db = CreateContext();
        return await db.Patients
            .Where(p => p.RecordNumber.StartsWith(series))
            .Select(p => p.RecordNumber)
            .ToListAsync();
    }

    /// <summary>El folio de un expediente nuevo sin folio (paso 8.16): del contador de la nube, con la letra del equipo o local.</summary>
    private async Task<string> AssignRecordNumberAsync()
    {
        var today = DateTime.Today;
        var existing = await RecordNumbersInSeriesAsync(today);
        return (Folios == null ? null : await Folios.NextAsync(Services.Folios.FolioKind.Patient, today, existing))
               ?? Services.Folios.FolioFactory.Local(Services.Folios.FolioKind.Patient, today, existing);
    }

    /// <summary>
    /// Guarda el expediente. Uno nuevo sin folio recibe el siguiente al guardar (paso 8.16: el contador de la nube, o la
    /// letra del equipo sin conexión). Un folio tecleado a mano que ya usa otro expediente activo de esta base se rechaza.
    /// Paso 8.21: escribe solo lo que la instancia cambió desde su foto (<see cref="PatientFields"/>) y la deja al día con
    /// la fila guardada; sin foto (una instancia hecha en memoria) la escribe completa, como antes.
    /// </summary>
    /// <returns>La misma instancia, ya al día.</returns>
    public async Task<PatientRecord> SavePatientAsync(PatientRecord patient)
    {
        await SavePatientAsync(patient, patient.StoredSnapshot);
        return patient;
    }

    /// <summary>
    /// Paso 8.21, la forma del plan: copia sobre la fila recién leída solo los campos donde <paramref name="edited"/> difiere
    /// de <paramref name="baseline"/> (la foto tomada al cargar; sin ella, todos: ver <see cref="PatientFields.ToWrite"/>) y
    /// deja a <paramref name="edited"/> al día con la fila guardada (<see cref="PatientFields.Refresh"/>) antes de avisar a
    /// las pantallas. Un campo que no está en la foto (<see cref="PatientFieldSnapshot.Without"/>) se escribe aunque no
    /// haya cambiado. Un expediente nuevo (<c>Id</c> 0) se da de alta completo, como siempre.
    /// <list type="bullet">
    /// <item>El folio se revisa (que no lo use otro expediente) y se anota en la nube solo si se escribe: una copia con un
    /// folio viejo ya no impide guardar una hoja. Si el folio que quedaría guardado está vacío, recibe uno.</item>
    /// <item>Sin cambios no escribe nada: la marca de la fila no se mueve y no hay nada que subir.</item>
    /// </list>
    /// </summary>
    /// <returns>La fila tal como quedó en la base (otra instancia, con su propia foto).</returns>
    public async Task<PatientRecord> SavePatientAsync(PatientRecord edited, PatientFieldSnapshot? baseline)
    {
        edited.RecordNumber = edited.RecordNumber.Trim();
        edited.FullName = edited.FullName.Trim();
        edited.SyncPrescriptionJson();
        edited.SyncSuppliesJson();
        edited.SyncServicesJson();

        await using var db = CreateContext();
        PatientRecord? existing = null;
        List<PatientFields.Field> write = new();
        bool writesFolio = true;
        bool typed = !string.IsNullOrWhiteSpace(edited.RecordNumber);
        if (edited.Id == 0)
        {
            if (!typed) edited.RecordNumber = await AssignRecordNumberAsync();
        }
        else
        {
            existing = await db.Patients.FirstOrDefaultAsync(p => p.Id == edited.Id)
                       ?? throw new InvalidOperationException("El expediente ya no existe en la base de datos.");
            write = PatientFields.ToWrite(edited, baseline, existing);
            var folioField = PatientFields.All.Single(f => f.Name == nameof(PatientRecord.RecordNumber));
            writesFolio = write.Contains(folioField);
            // El folio que quedaría guardado (el de la instancia si se escribe, si no el de la base) vacío: recibe uno
            if (string.IsNullOrWhiteSpace(writesFolio ? edited.RecordNumber : existing.RecordNumber))
            {
                edited.RecordNumber = await AssignRecordNumberAsync();
                if (!writesFolio) write.Add(folioField);
                writesFolio = true;
                typed = false;
            }
        }

        if (writesFolio && await IsRecordNumberTakenAsync(edited.RecordNumber, edited.Id))
        {
            throw new InvalidOperationException($"El folio de expediente '{edited.RecordNumber}' ya está registrado.");
        }

        bool newNumber = typed;
        if (existing == null)
        {
            edited.CreatedAt = DateTime.Now;
            db.Patients.Add(edited);
        }
        else
        {
            newNumber = typed && writesFolio && !string.Equals(existing.RecordNumber, edited.RecordNumber, StringComparison.OrdinalIgnoreCase);
            var entry = db.Entry(existing);
            foreach (var field in write)
            {
                // Paso 8.22: una hoja editada desde una copia con foto lleva sus ediciones por renglón sobre la de la base (un
                // renglón que bajó de otro equipo mientras tanto se queda); sin foto de esa hoja se escribe completa, como antes
                entry.Property(field.Name).CurrentValue = field.IsSheet && baseline != null && baseline.Has(field)
                    ? Services.Rules.PatientSheetCanonical.Rebase(field.Name, baseline[field] as string, field.Get(edited) as string,
                        field.Get(existing) as string, existing.SyncId)
                    : field.Get(edited);
            }
        }

        await db.SaveChangesAsync();
        var fresh = PatientFields.Track(await db.Patients.AsNoTracking().FirstAsync(p => p.Id == edited.Id));
        // La instancia queda al día con la fila antes del aviso: la recarga de las pantallas ve la versión guardada y
        // distingue una descarga posterior de otro equipo de lo que este mismo equipo acaba de guardar
        PatientFields.Refresh(edited, fresh, baseline);
        NotifyPatientsChanged();

        // Paso 8.16: un folio tecleado a mano mayor que el contador de la nube lo sube (sin esperar: ya se guardó)
        if (newNumber && Folios is { } folios)
        {
            var folio = edited.RecordNumber;
            var today = DateTime.Today;
            _ = Task.Run(async () =>
            {
                try { await folios.NoteManualAsync(folio, today); }
                catch (Exception ex) { AppLogger.Warn($"No se pudo anotar el folio tecleado {folio} en la nube: {ex.Message}"); }
            });
        }
        return fresh;
    }

    // =========================================================================
    // CONSULTA ENVIADA ENTRE EQUIPOS (identificadores que valen en cualquier equipo)
    // =========================================================================

    /// <summary>Versión guardada del expediente (para que el otro equipo sepa si ya la tiene).</summary>
    public async Task<DateTime?> GetPatientUpdatedAtAsync(int patientId)
    {
        await using var db = CreateContext();
        return await db.Patients.AsNoTracking().Where(p => p.Id == patientId).Select(p => (DateTime?)p.UpdatedAtUtc).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Paso 8.22: la versión guardada del expediente con sus marcas por campo (para la copia que viaja y para revisar la que
    /// llega); null si no existe.
    /// </summary>
    public async Task<(DateTime UpdatedAtUtc, string FieldStampsJson)?> GetPatientVersionAsync(int patientId)
    {
        await using var db = CreateContext();
        var row = await db.Patients.AsNoTracking().Where(p => p.Id == patientId)
            .Select(p => new { p.UpdatedAtUtc, p.FieldStampsJson })
            .FirstOrDefaultAsync();
        return row == null ? null : (row.UpdatedAtUtc, row.FieldStampsJson ?? string.Empty);
    }

    /// <summary>Avance de nota ya guardado con ese folio, si existe.</summary>
    public async Task<Guid?> FindConsultationNoteSyncIdAsync(int patientId, string folio) =>
        (await FindConsultationNoteVersionAsync(patientId, folio))?.SyncId;

    /// <summary>Paso 8.22: el avance de nota guardado con ese folio y su marca, si existe.</summary>
    public async Task<(Guid SyncId, DateTime UpdatedAtUtc)?> FindConsultationNoteVersionAsync(int patientId, string folio)
    {
        if (string.IsNullOrWhiteSpace(folio)) return null;
        await using var db = CreateContext();
        var row = await db.PatientConsultations.AsNoTracking()
            .Where(c => c.PatientRecordId == patientId && c.Folio == folio)
            .Select(c => new { c.SyncId, c.UpdatedAtUtc })
            .FirstOrDefaultAsync();
        return row == null ? null : (row.SyncId, row.UpdatedAtUtc);
    }

    public async Task<bool> ConsultationNoteExistsAsync(Guid syncId) => await GetConsultationNoteUpdatedAtAsync(syncId) != null;

    /// <summary>Paso 8.22: la marca de una nota por su SyncId; null si todavía no llega.</summary>
    public async Task<DateTime?> GetConsultationNoteUpdatedAtAsync(Guid syncId)
    {
        await using var db = CreateContext();
        return await db.PatientConsultations.AsNoTracking().Where(c => c.SyncId == syncId)
            .Select(c => (DateTime?)c.UpdatedAtUtc).FirstOrDefaultAsync();
    }

    /// <summary>Productos y servicios (también los dados de baja) con sus identificadores.</summary>
    public async Task<List<CatalogRef>> GetCatalogRefsAsync()
    {
        await using var db = CreateContext();
        var products = await db.Products.AsNoTracking()
            .Select(p => new CatalogRef(CatalogRefKind.Product, p.Id, p.SyncId, p.Code, p.Name, p.IsActive))
            .ToListAsync();
        var services = await db.ClinicalServices.AsNoTracking()
            .Select(s => new CatalogRef(CatalogRefKind.Service, s.Id, s.SyncId, s.Code, s.Name, s.IsActive))
            .ToListAsync();
        products.AddRange(services);
        return products;
    }

    public async Task<Guid?> GetAppointmentSyncIdAsync(int appointmentId)
    {
        await using var db = CreateContext();
        return await db.Appointments.AsNoTracking().Where(a => a.Id == appointmentId).Select(a => (Guid?)a.SyncId).FirstOrDefaultAsync();
    }

    public async Task<int?> FindAppointmentIdAsync(Guid syncId)
    {
        await using var db = CreateContext();
        return await db.Appointments.AsNoTracking().Where(a => a.SyncId == syncId).Select(a => (int?)a.Id).FirstOrDefaultAsync();
    }

    public async Task DeletePatientAsync(int id)
    {
        await using var db = CreateContext();
        var patient = await db.Patients.FindAsync(id);
        if (patient != null)
        {
            patient.IsActive = false; // Borrado lógico
            await db.SaveChangesAsync();
            NotifyPatientsChanged();
        }
    }

    public async Task<PatientConsultation> AddConsultationNoteAsync(int patientId, PatientConsultation consultation)
    {
        await using var db = CreateContext();
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == patientId)
            ?? throw new InvalidOperationException($"No se encontró el paciente con ID {patientId}");

        if (string.IsNullOrWhiteSpace(consultation.Folio))
        {
            var day = consultation.Date.Date;
            var nextDay = day.AddDays(1);
            var count = await db.PatientConsultations.CountAsync(c => c.Date >= day && c.Date < nextDay);
            consultation.Folio = $"CNS-{consultation.Date:yyyyMMdd}-{(count + 1):D3}";
        }

        consultation.PatientRecordId = patientId;
        consultation.PatientRecord = null;
        consultation.PrescriptionsJson = System.Text.Json.JsonSerializer.Serialize(consultation.Prescriptions);

        if (!consultation.StartedAt.HasValue)
        {
            consultation.StartedAt = consultation.Date;
        }
        if (!consultation.EndedAt.HasValue)
        {
            consultation.EndedAt = DateTime.Now;
        }

        // Actualizar datos del paciente con esta última consulta (la consulta en curso refleja lo mismo en la instancia
        // abierta con la misma regla: PatientRecord.ApplyConsultationSummary)
        patient.ApplyConsultationSummary(consultation);

        // La nota existente se busca dentro del MISMO paciente: un folio repetido en otro
        // expediente (p. ej. importado) nunca debe sobrescribir la consulta de otra persona
        var existing = !string.IsNullOrWhiteSpace(consultation.Folio)
            ? await db.PatientConsultations.FirstOrDefaultAsync(c => c.Folio == consultation.Folio && c.PatientRecordId == patientId)
            : null;

        consultation.EnsureNumericVitalSigns();

        if (existing != null)
        {
            existing.EndedAt = consultation.EndedAt;
            existing.ChiefComplaint = consultation.ChiefComplaint;
            existing.VitalSigns = consultation.VitalSigns;
            existing.BloodPressureSystolic = consultation.BloodPressureSystolic;
            existing.BloodPressureDiastolic = consultation.BloodPressureDiastolic;
            existing.HeartRate = consultation.HeartRate;
            existing.RespiratoryRate = consultation.RespiratoryRate;
            existing.Temperature = consultation.Temperature;
            existing.Weight = consultation.Weight;
            existing.Height = consultation.Height;
            existing.BMI = consultation.BMI;
            existing.Glucose = consultation.Glucose;
            existing.PhysicalExamination = consultation.PhysicalExamination;
            existing.SystemsReview = consultation.SystemsReview;
            existing.Diagnosis = consultation.Diagnosis;
            existing.TreatmentPlan = consultation.TreatmentPlan;
            existing.Notes = consultation.Notes;
            existing.PrescriptionsJson = consultation.PrescriptionsJson;
            existing.LabOrderJson = consultation.LabOrderJson;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            consultation.Id = existing.Id;
        }
        else
        {
            db.Entry(consultation).State = EntityState.Added;
        }

        await db.SaveChangesAsync();

        NotifyPatientsChanged();
        return consultation;
    }

    /// <summary>Elimina la nota de una consulta (avance guardado de una consulta que se descartó).</summary>
    public async Task<bool> DeleteConsultationNoteAsync(int patientId, string folio)
    {
        if (string.IsNullOrWhiteSpace(folio)) return false;
        await using var db = CreateContext();
        var note = await db.PatientConsultations.FirstOrDefaultAsync(c => c.PatientRecordId == patientId && c.Folio == folio);
        if (note == null) return false;
        db.PatientConsultations.Remove(note);
        await db.SaveChangesAsync();
        NotifyPatientsChanged();
        return true;
    }

    private void NotifyAppointmentsChanged() => Raise(OnAppointmentsChanged, "appointments");

    // =========================================================================
    // AGENDA Y CONTROL DE CITAS MÉDICAS
    // =========================================================================

    public async Task<List<PatientAppointment>> GetAppointmentsAsync(DateTime? fromDate = null, DateTime? toDate = null)
    {
        await using var db = CreateContext();
        var query = db.Appointments
            .AsNoTracking()
            .Include(a => a.PatientRecord)
            .AsQueryable();

        if (fromDate.HasValue)
        {
            var from = fromDate.Value.Date;
            query = query.Where(a => a.Date >= from);
        }
        if (toDate.HasValue)
        {
            var to = toDate.Value.Date.AddDays(1);
            query = query.Where(a => a.Date < to);
        }

        return await query.OrderBy(a => a.Date).ThenBy(a => a.Time).ToListAsync();
    }

    public async Task<List<PatientAppointment>> GetAppointmentsByDayAsync(DateTime day)
    {
        var targetDate = day.Date;
        var nextDate = targetDate.AddDays(1);
        await using var db = CreateContext();
        return await db.Appointments
            .AsNoTracking()
            .Include(a => a.PatientRecord)
            .Where(a => a.Date >= targetDate && a.Date < nextDate)
            .OrderBy(a => a.Time)
            .ToListAsync();
    }

    public async Task<List<PatientAppointment>> GetAppointmentsByWeekAsync(DateTime startOfWeek)
    {
        var from = startOfWeek.Date;
        var to = from.AddDays(7);
        await using var db = CreateContext();
        return await db.Appointments
            .AsNoTracking()
            .Include(a => a.PatientRecord)
            .Where(a => a.Date >= from && a.Date < to)
            .OrderBy(a => a.Date)
            .ThenBy(a => a.Time)
            .ToListAsync();
    }

    public async Task<PatientAppointment> SaveAppointmentAsync(PatientAppointment appointment)
    {
        await using var db = CreateContext();

        if (appointment.PatientRecordId > 0 && string.IsNullOrWhiteSpace(appointment.PatientName))
        {
            var patient = await db.Patients.FindAsync(appointment.PatientRecordId);
            if (patient != null)
            {
                appointment.PatientName = patient.FullName;
                appointment.PatientRecordNumber = patient.RecordNumber;
                appointment.PatientPhone = patient.Phone;
            }
        }

        if (appointment.Id == 0)
        {
            appointment.CreatedAt = DateTime.Now;
            db.Appointments.Add(appointment);
        }
        else
        {
            var existing = await db.Appointments.FindAsync(appointment.Id);
            if (existing != null)
            {
                existing.PatientRecordId = appointment.PatientRecordId;
                existing.PatientName = appointment.PatientName;
                existing.PatientRecordNumber = appointment.PatientRecordNumber;
                existing.PatientPhone = appointment.PatientPhone;
                existing.Date = appointment.Date;
                existing.Time = appointment.Time;
                existing.DurationMinutes = appointment.DurationMinutes;
                existing.Reason = appointment.Reason;
                existing.Notes = appointment.Notes;
                existing.Status = appointment.Status;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                db.Appointments.Add(appointment);
            }
        }

        // Paso 8.22: agendar ya no escribe el expediente. La próxima cita es un resumen derivado (en orden clínico, no la
        // última que se guardó): el guardado la recalcula sin marca ni nada que subir (PatientSummary)
        await db.SaveChangesAsync();
        NotifyAppointmentsChanged();
        NotifyPatientsChanged();
        return appointment;
    }

    public async Task UpdateAppointmentStatusAsync(int appointmentId, string status)
    {
        await using var db = CreateContext();
        var appt = await db.Appointments.FindAsync(appointmentId);
        if (appt != null)
        {
            appt.Status = status;
            appt.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            NotifyAppointmentsChanged();
        }
    }

    // =========================================================================
    // ALERTAS DEL SISTEMA Y LISTA DE COMPRAS SUGERIDA
    // =========================================================================

    public async Task<SystemAlertsSummary> GetSystemAlertsSummaryAsync()
    {
        var summary = new SystemAlertsSummary();
        var today = ClinicClock.Today;
        var tomorrow = today.AddDays(1);
        var in30Days = today.AddDays(StockAlertRules.NearExpiryDays);

        await using var db = CreateContext();

        // 1. Citas del día
        summary.TodayAppointments = await db.Appointments
            .AsNoTracking()
            .Include(a => a.PatientRecord)
            .Where(a => a.Date >= today && a.Date < tomorrow && a.Status != "Cancelada")
            .OrderBy(a => a.Time)
            .ToListAsync();
        summary.TodayAppointmentsCount = summary.TodayAppointments.Count;

        // 2. Stock bajo (filtrado en SQLite: solo productos con mínimo configurado)
        summary.LowStockProducts = await db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.IsActive && p.MinStock > 0 && p.CurrentStock <= p.MinStock)
            .OrderBy(p => p.CurrentStock)
            .ToListAsync();
        summary.LowStockProductsCount = summary.LowStockProducts.Count;

        // 3. Lotes vencidos y por vencer (solo lotes con piezas de productos activos)
        var in31Days = in30Days.AddDays(1);
        var batches = await db.ProductBatches
            .AsNoTracking()
            .Include(b => b.Product)
            .Where(ProductBatch.IsAvailableExpression)
            .Where(b => b.Quantity > 0 && b.ExpirationDate < in31Days && (b.Product == null || b.Product.IsActive))
            .OrderBy(b => b.ExpirationDate)
            .ToListAsync();

        // Paso 8.11: la división vive en StockAlertRules (sin base ni WPF), la referencia de las alertas del celular
        (summary.ExpiredBatches, summary.NearExpiryBatches) = StockAlertRules.SplitByExpiration(batches, today);
        summary.ExpiredBatchesCount = summary.ExpiredBatches.Count;
        summary.NearExpiryBatchesCount = summary.NearExpiryBatches.Count;

        return summary;
    }

    /// <summary>Sin costo capturado, la lista de compras estima el costo como esta parte del precio de venta.</summary>
    public const decimal EstimatedCostShareOfPrice = SuggestedPurchaseRules.EstimatedCostShareOfPrice;

    public async Task<List<SuggestedPurchaseItem>> GetSuggestedPurchaseListAsync()
    {
        await using var db = CreateContext();
        var products = await db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.IsActive && p.MinStock > 0 && p.CurrentStock <= p.MinStock)
            .ToListAsync();

        // Paso 8.11: la lista sale de SuggestedPurchaseRules (sin base ni WPF), la referencia de la compra sugerida del celular
        return SuggestedPurchaseRules.Build(products);
    }

    // =========================================================================
    // PLANTILLAS DE RECETA (POSOLOGÍA, MEDIDAS GENERALES) Y DOSIS PEDIÁTRICA
    // =========================================================================

    public async Task<List<PrescriptionTemplate>> GetPrescriptionTemplatesAsync(string kind)
    {
        await using var db = CreateContext();
        return await db.PrescriptionTemplates
            .AsNoTracking()
            .Where(t => t.Kind == kind && t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Title)
            .ToListAsync();
    }

    public async Task<List<PediatricDoseRule>> GetPediatricDoseRulesAsync()
    {
        await using var db = CreateContext();
        return await db.PediatricDoseRules
            .AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Medication)
            .ToListAsync();
    }

    // =========================================================================
    // DETECCIÓN DE ALERGIAS FARMACOLÓGICAS
    // =========================================================================

    /// <summary>Paso 8.26: la regla vive en <see cref="TPVC.Services.Rules.AllergyRules.CheckMedication"/> (vectores <c>allergy-check.json</c>).</summary>
    public static (bool HasConflict, string MatchedAllergy, string WarningDetails) CheckMedicationAllergy(
        PatientRecord? patient, Product? product, string? medicineName = null) =>
        TPVC.Services.Rules.AllergyRules.CheckMedication(patient, product, medicineName);
}

public enum CatalogRefKind
{
    Product,
    Service
}

/// <summary>Producto o servicio del catálogo con su Id local y su identificador de sincronización.</summary>
public sealed record CatalogRef(CatalogRefKind Kind, int Id, Guid SyncId, string Code, string Name, bool IsActive);
