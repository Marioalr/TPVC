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
public class InventoryService
{
    public const int ExpirationWarningDays = 60;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;

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
                .Where(p => p.Batches.Any(b => !b.IsDepleted && b.Quantity > 0 && b.DaysRemaining <= ExpirationWarningDays))
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

    public async Task SaveProductAsync(Product product)
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
            if (product.CurrentStock > 0)
            {
                db.InventoryMovements.Add(new InventoryMovement
                {
                    Product = product,
                    MovementType = MovementType.EntradaAjuste,
                    Quantity = product.CurrentStock,
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

            // Un cambio manual de existencias se registra en el Kardex como ajuste
            if (existing.CurrentStock != product.CurrentStock)
            {
                db.InventoryMovements.Add(new InventoryMovement
                {
                    ProductId = existing.Id,
                    MovementType = MovementType.AjusteInventario,
                    Quantity = Math.Abs(product.CurrentStock - existing.CurrentStock),
                    StockBefore = existing.CurrentStock,
                    StockAfter = product.CurrentStock,
                    Date = now,
                    Reason = "Ajuste manual de existencias desde la ficha del producto",
                    Responsible = "Dr. Consulta"
                });
            }

            existing.Code = product.Code;
            existing.Name = product.Name;
            existing.GenericName = product.GenericName;
            existing.Presentation = product.Presentation;
            existing.CategoryId = product.CategoryId;
            existing.Unit = product.Unit;
            existing.Price = product.Price;
            existing.CurrentStock = product.CurrentStock;
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
    /// Guarda el lote tal cual (no modifica existencias). Para recepciones de mercancía
    /// usar <see cref="ReceiveBatchAsync"/>, que además suma stock y registra el Kardex.
    /// </summary>
    public async Task SaveBatchAsync(ProductBatch batch)
    {
        await using var db = CreateContext();
        if (batch.Id == 0)
        {
            batch.Product = null;
            db.Entry(batch).State = EntityState.Added;
        }
        else
        {
            var existing = await db.ProductBatches.FindAsync(batch.Id)
                ?? throw new InvalidOperationException("El lote ya no existe.");
            existing.BatchNumber = batch.BatchNumber;
            existing.Quantity = batch.Quantity;
            existing.ExpirationDate = batch.ExpirationDate;
            existing.ReceivedDate = batch.ReceivedDate;
            existing.Notes = batch.Notes;
            existing.IsDepleted = batch.IsDepleted;
        }
        await db.SaveChangesAsync();
        NotifyInventoryChanged();
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
    /// Elimina un lote. Si aún tenía piezas, se descuentan de las existencias del producto
    /// con un ajuste en el Kardex para que stock y lotes no se desalineen.
    /// </summary>
    public async Task DeleteBatchAsync(int id)
    {
        await using var db = CreateContext();
        var batch = await db.ProductBatches.Include(b => b.Product).FirstOrDefaultAsync(b => b.Id == id);
        if (batch == null) return;

        if (!batch.IsDepleted && batch.Quantity > 0 && batch.Product != null)
        {
            var product = batch.Product;
            int stockBefore = product.CurrentStock;
            int removed = Math.Min(batch.Quantity, stockBefore);
            product.CurrentStock = stockBefore - removed;
            product.UpdatedAt = DateTime.Now;

            if (removed > 0)
            {
                db.InventoryMovements.Add(new InventoryMovement
                {
                    ProductId = product.Id,
                    MovementType = MovementType.AjusteInventario,
                    Quantity = removed,
                    StockBefore = stockBefore,
                    StockAfter = product.CurrentStock,
                    Date = DateTime.Now,
                    Reason = $"Eliminación del lote {batch.BatchNumber}",
                    Responsible = "Dr. Consulta"
                });
            }
        }

        db.ProductBatches.Remove(batch);
        await db.SaveChangesAsync();
        NotifyInventoryChanged();
    }

    /// <summary>
    /// Descuenta piezas de los lotes activos (FEFO: primero el que caduca antes) y devuelve
    /// el Id del primer lote afectado. Si se indica un lote preferido, se descuenta de él primero.
    /// </summary>
    private static int? DeductFromBatches(Product product, int quantity, int? preferredBatchId = null)
    {
        int remaining = quantity;
        var activeBatches = product.Batches
            .Where(b => !b.IsDepleted && b.Quantity > 0)
            .OrderBy(b => b.Id == preferredBatchId ? 0 : 1)
            .ThenBy(b => b.ExpirationDate)
            .ToList();

        int? primaryBatchId = null;
        foreach (var batch in activeBatches)
        {
            if (remaining <= 0) break;
            primaryBatchId ??= batch.Id;

            if (batch.Quantity <= remaining)
            {
                remaining -= batch.Quantity;
                batch.Quantity = 0;
                batch.IsDepleted = true;
            }
            else
            {
                batch.Quantity -= remaining;
                remaining = 0;
            }
        }
        return primaryBatchId;
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

        int stockBefore = product.CurrentStock;
        product.CurrentStock = stockBefore - quantity;
        product.UpdatedAt = DateTime.Now;
        int? primaryBatchId = DeductFromBatches(product, quantity);

        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = product.Id,
            BatchId = primaryBatchId,
            MovementType = MovementType.SalidaConsulta,
            Quantity = quantity,
            StockBefore = stockBefore,
            StockAfter = product.CurrentStock,
            Date = DateTime.Now,
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
            Folio = await GenerateNextSaleFolioAsync(db, now),
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
                int stockBefore = product.CurrentStock;
                product.CurrentStock = stockBefore - line.Quantity;
                product.UpdatedAt = now;
                int? primaryBatchId = DeductFromBatches(product, line.Quantity);

                db.InventoryMovements.Add(new InventoryMovement
                {
                    ProductId = product.Id,
                    BatchId = primaryBatchId,
                    MovementType = MovementType.SalidaVenta,
                    Quantity = line.Quantity,
                    StockBefore = stockBefore,
                    StockAfter = product.CurrentStock,
                    Date = now,
                    Reason = $"Venta POS {sale.Folio} - Paciente/Cliente: {clientText} ({methodText})",
                    Responsible = responsibleText
                });

                sale.Items.Add(new SaleItem
                {
                    ProductId = product.Id,
                    Code = line.Code ?? product.Code,
                    Description = line.Description ?? product.Name,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Subtotal = line.Quantity * line.UnitPrice
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

    private static async Task<string> GenerateNextSaleFolioAsync(AppDbContext db, DateTime date)
    {
        var prefix = $"TKT-{date:yyyyMMdd}-";
        var existing = await db.Sales
            .Where(s => s.Folio.StartsWith(prefix))
            .Select(s => s.Folio)
            .ToListAsync();

        int max = 0;
        foreach (var folio in existing)
        {
            if (int.TryParse(folio.AsSpan(prefix.Length), out int n) && n > max) max = n;
        }
        return $"{prefix}{max + 1:D4}";
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
        return await db.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Folio.ToLower() == cleanFolio.ToLower());
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
            query = query.Where(s => s.Folio.ToLower().Contains(needle) || s.CustomerName.ToLower().Contains(needle));
        }

        return await query.OrderByDescending(s => s.Date).Take(limit).ToListAsync();
    }

    /// <summary>
    /// Cancela o devuelve un ticket previamente cobrado, reintegrando de forma atómica
    /// el stock de todos los productos y generando los movimientos de Kardex de devolución.
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

        var now = DateTime.Now;
        sale.IsCancelled = true;
        sale.CancelledAt = now;
        sale.CancellationReason = reasonText;

        // Reintegrar stock de productos al inventario
        var productItemGroups = sale.Items
            .Where(i => !i.IsService && i.ProductId.HasValue && i.Quantity > 0)
            .GroupBy(i => i.ProductId!.Value)
            .ToList();

        if (productItemGroups.Count > 0)
        {
            var productIds = productItemGroups.Select(g => g.Key).ToList();
            var products = await db.Products
                .Include(p => p.Batches)
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id);

            foreach (var group in productItemGroups)
            {
                if (products.TryGetValue(group.Key, out var product))
                {
                    int totalQtyToRestore = group.Sum(i => i.Quantity);
                    int stockBefore = product.CurrentStock;
                    product.CurrentStock = stockBefore + totalQtyToRestore;
                    product.UpdatedAt = now;

                    // Reintegrar a un lote activo si existe
                    // Se reintegra al lote vigente que caduca más tarde (el que se habría vendido al final por FEFO)
                    var targetBatch = product.Batches
                                          .Where(b => b.ExpirationDate.Date >= DateTime.Today)
                                          .OrderByDescending(b => b.ExpirationDate)
                                          .FirstOrDefault()
                                      ?? product.Batches.OrderByDescending(b => b.ExpirationDate).FirstOrDefault();
                    if (targetBatch != null)
                    {
                        targetBatch.Quantity += totalQtyToRestore;
                        targetBatch.IsDepleted = false; // un lote agotado vuelve a contar para FEFO
                    }

                    db.InventoryMovements.Add(new InventoryMovement
                    {
                        ProductId = product.Id,
                        BatchId = targetBatch?.Id,
                        MovementType = MovementType.EntradaDevolucion,
                        Quantity = totalQtyToRestore,
                        StockBefore = stockBefore,
                        StockAfter = product.CurrentStock,
                        Date = now,
                        Reason = $"Devolución por cancelación de ticket {sale.Folio}: {reasonText}",
                        Responsible = responsibleText
                    });
                }
            }
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        NotifyInventoryChanged();
        NotifySalesChanged();
        return true;
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

        var cut = new DailyCashCut
        {
            Date = day,
            Sales = sales,
            TotalTicketsCount = sales.Count,
            ActiveTicketsCount = sales.Count(s => !s.IsCancelled),
            CancelledTicketsCount = sales.Count(s => s.IsCancelled),
            TotalSalesAmount = sales.Where(s => !s.IsCancelled).Sum(s => s.Total),
            TotalCancelledAmount = sales.Where(s => s.IsCancelled).Sum(s => s.Total)
        };

        var activeSales = sales.Where(s => !s.IsCancelled).ToList();

        // Ingresos por método de pago
        foreach (var sale in activeSales)
        {
            var method = (sale.PaymentMethod ?? "").Trim().ToLowerInvariant();
            if (method.Contains("efectivo"))
            {
                cut.TotalCash += sale.Total;
            }
            else if (method.Contains("débito") || method.Contains("debito"))
            {
                cut.TotalDebitCard += sale.Total;
            }
            else if (method.Contains("crédito") || method.Contains("credito"))
            {
                cut.TotalCreditCard += sale.Total;
            }
            else if (method.Contains("transfer"))
            {
                cut.TotalTransfer += sale.Total;
            }
            else
            {
                cut.TotalOtherPaymentMethods += sale.Total;
            }
        }

        // Desglose Servicios vs Productos
        foreach (var item in activeSales.SelectMany(s => s.Items))
        {
            if (item.IsService)
            {
                cut.TotalServicesAmount += item.Subtotal;
                cut.TotalServicesCount += item.Quantity;
            }
            else
            {
                cut.TotalProductsAmount += item.Subtotal;
                cut.TotalProductsUnitsSold += item.Quantity;
            }
        }

        return cut;
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

    // Registro genérico de movimiento
    public async Task RegisterMovementAsync(InventoryMovement movement)
    {
        await using var db = CreateContext();
        var product = await db.Products
            .Include(p => p.Batches)
            .FirstOrDefaultAsync(p => p.Id == movement.ProductId)
            ?? throw new InvalidOperationException("Producto no encontrado");

        movement.Product = null;
        movement.Batch = null;
        movement.StockBefore = product.CurrentStock;

        if (movement.IsEntry)
        {
            movement.StockAfter = product.CurrentStock + movement.Quantity;
        }
        else
        {
            EnsureStock(product, movement.Quantity);
            movement.StockAfter = product.CurrentStock - movement.Quantity;
            movement.BatchId = DeductFromBatches(product, movement.Quantity, movement.BatchId) ?? movement.BatchId;
        }

        product.CurrentStock = movement.StockAfter;
        product.UpdatedAt = DateTime.Now;
        movement.Date = DateTime.Now;

        db.Entry(movement).State = EntityState.Added;
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

    public async Task<DashboardMetrics> GetDashboardMetricsAsync()
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var expiringLimit = today.AddDays(ExpirationWarningDays + 1);

        await using var db = CreateContext();

        // Agregados calculados en SQLite: no se materializan catálogos completos
        var activeProducts = db.Products.Where(p => p.IsActive);
        var activeBatches = db.ProductBatches
            .Where(b => !b.IsDepleted && b.Quantity > 0 && (b.Product == null || b.Product.IsActive));
        var todayMovements = db.InventoryMovements.Where(m => m.Date >= today);
        var todaySales = db.Sales.Where(s => s.Date >= today && s.Date < tomorrow);

        return new DashboardMetrics
        {
            TotalProducts = await activeProducts.CountAsync(),
            LowStockCount = await activeProducts.CountAsync(p => p.MinStock > 0 && p.CurrentStock <= p.MinStock),
            ExpiringSoonCount = await activeBatches.CountAsync(b => b.ExpirationDate >= today && b.ExpirationDate < expiringLimit),
            ExpiredCount = await activeBatches.CountAsync(b => b.ExpirationDate < today),
            MovementsToday = await todayMovements.CountAsync(),
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
                .Where(p => TextSearch.Matches(q, p.FullName, p.RecordNumber, p.Phone, p.LastDiagnosis, p.Email))
                .ToList();
        }

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

    public async Task<PatientRecord?> GetPatientByIdAsync(int id)
    {
        await using var db = CreateContext();
        return await db.Patients
            .AsNoTracking()
            .Include(p => p.Consultations.OrderByDescending(c => c.Date))
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<bool> IsRecordNumberTakenAsync(string recordNumber, int excludePatientId = 0)
    {
        if (string.IsNullOrWhiteSpace(recordNumber)) return false;
        var normalized = recordNumber.Trim().ToUpper();
        await using var db = CreateContext();
        return await db.Patients
            .AnyAsync(p => p.IsActive && p.Id != excludePatientId && p.RecordNumber.ToUpper() == normalized);
    }

    public async Task<string> GenerateNextRecordNumberAsync()
    {
        var year = DateTime.Today.Year;
        var prefix = $"EXP-{year}-";

        await using var db = CreateContext();
        var existingRecords = await db.Patients
            .Where(p => p.RecordNumber.StartsWith(prefix))
            .Select(p => p.RecordNumber)
            .ToListAsync();

        int maxNum = 0;
        foreach (var rec in existingRecords)
        {
            var suffix = rec.Substring(prefix.Length);
            if (int.TryParse(suffix, out int parsed) && parsed > maxNum)
            {
                maxNum = parsed;
            }
        }

        return $"{prefix}{(maxNum + 1):D3}";
    }

    public async Task<PatientRecord> SavePatientAsync(PatientRecord patient)
    {
        patient.RecordNumber = patient.RecordNumber.Trim();
        patient.FullName = patient.FullName.Trim();

        if (string.IsNullOrWhiteSpace(patient.RecordNumber))
        {
            patient.RecordNumber = await GenerateNextRecordNumberAsync();
        }

        if (await IsRecordNumberTakenAsync(patient.RecordNumber, patient.Id))
        {
            throw new InvalidOperationException($"El folio de expediente '{patient.RecordNumber}' ya está registrado.");
        }

        patient.SyncPrescriptionJson();
        patient.SyncSuppliesJson();
        patient.SyncServicesJson();

        await using var db = CreateContext();
        PatientRecord? tracked = null;

        if (patient.Id == 0)
        {
            patient.CreatedAt = DateTime.Now;
            db.Patients.Add(patient);
        }
        else
        {
            var existing = await db.Patients.FirstOrDefaultAsync(p => p.Id == patient.Id);
            tracked = existing;
            if (existing != null)
            {
                // Copia todas las columnas escalares (incluye los JSON de receta/insumos/servicios)
                // conservando los metadatos de sincronización del registro guardado.
                var syncId = existing.SyncId;
                var createdAt = existing.CreatedAt;
                db.Entry(existing).CurrentValues.SetValues(patient);
                existing.SyncId = syncId;
                existing.CreatedAt = createdAt;
            }
            else
            {
                throw new InvalidOperationException("El expediente ya no existe en la base de datos.");
            }
        }

        await db.SaveChangesAsync();
        // La instancia en memoria lleva la versión guardada: así una descarga posterior de otro
        // equipo se distingue de lo que este mismo equipo acaba de guardar
        if (tracked != null) patient.UpdatedAtUtc = tracked.UpdatedAtUtc;
        NotifyPatientsChanged();
        return patient;
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

    /// <summary>Avance de nota ya guardado con ese folio, si existe.</summary>
    public async Task<Guid?> FindConsultationNoteSyncIdAsync(int patientId, string folio)
    {
        if (string.IsNullOrWhiteSpace(folio)) return null;
        await using var db = CreateContext();
        return await db.PatientConsultations.AsNoTracking()
            .Where(c => c.PatientRecordId == patientId && c.Folio == folio)
            .Select(c => (Guid?)c.SyncId)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> ConsultationNoteExistsAsync(Guid syncId)
    {
        await using var db = CreateContext();
        return await db.PatientConsultations.AsNoTracking().AnyAsync(c => c.SyncId == syncId);
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

        // Actualizar datos del paciente con esta última consulta
        patient.LastVisit = consultation.Date;
        if (!string.IsNullOrWhiteSpace(consultation.Diagnosis))
        {
            patient.LastDiagnosis = consultation.Diagnosis;
        }
        if (!string.IsNullOrWhiteSpace(consultation.VitalSigns))
        {
            patient.VitalSigns = consultation.VitalSigns;
        }
        if (!string.IsNullOrWhiteSpace(consultation.Notes) || !string.IsNullOrWhiteSpace(consultation.TreatmentPlan))
        {
            patient.ClinicalNotes = $"{consultation.ChiefComplaint}\n{consultation.Notes}\nPlan: {consultation.TreatmentPlan}".Trim();
        }
        if (consultation.Prescriptions != null && consultation.Prescriptions.Any())
        {
            patient.ActivePrescription = new List<PatientPrescriptionItem>(consultation.Prescriptions);
            patient.SyncPrescriptionJson();
        }

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

        // Sincronizar en PatientRecord si corresponde
        if (appointment.PatientRecordId > 0)
        {
            var pat = await db.Patients.FindAsync(appointment.PatientRecordId);
            if (pat != null && appointment.Status != "Cancelada")
            {
                pat.NextAppointmentDate = appointment.Date;
                pat.NextAppointmentTime = appointment.Time;
                pat.NextAppointmentReason = appointment.Reason;
                pat.NextAppointmentNotes = appointment.Notes;
            }
        }

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
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var in30Days = today.AddDays(30);

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
            .Where(b => b.Quantity > 0 && !b.IsDepleted && b.ExpirationDate < in31Days && (b.Product == null || b.Product.IsActive))
            .OrderBy(b => b.ExpirationDate)
            .ToListAsync();

        summary.ExpiredBatches = batches.Where(b => b.ExpirationDate.Date < today).ToList();
        summary.ExpiredBatchesCount = summary.ExpiredBatches.Count;

        summary.NearExpiryBatches = batches
            .Where(b => b.ExpirationDate.Date >= today && b.ExpirationDate.Date <= in30Days)
            .ToList();
        summary.NearExpiryBatchesCount = summary.NearExpiryBatches.Count;

        return summary;
    }

    public async Task<List<SuggestedPurchaseItem>> GetSuggestedPurchaseListAsync()
    {
        await using var db = CreateContext();
        var products = await db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.IsActive && p.MinStock > 0 && p.CurrentStock <= p.MinStock)
            .ToListAsync();

        var suggestedList = new List<SuggestedPurchaseItem>();

        foreach (var p in products)
        {
            int min = p.MinStock;
            {
                int target = min * 2;
                int qtyToBuy = Math.Max(target - p.CurrentStock, min);
                if (qtyToBuy <= 0) qtyToBuy = 5;

                decimal estimatedCost = p.Price > 0 ? Math.Round(p.Price * 0.65m, 2) : 25.00m;

                string reason = p.CurrentStock == 0 ? "Agotado en consultorio" : $"Stock bajo ({p.CurrentStock} de {min} mín.)";

                suggestedList.Add(new SuggestedPurchaseItem
                {
                    ProductCode = p.Code,
                    ProductName = p.Name,
                    GenericName = p.GenericName ?? string.Empty,
                    Presentation = p.Presentation ?? string.Empty,
                    CategoryName = p.Category?.Name ?? "General",
                    CurrentStock = p.CurrentStock,
                    MinStock = min,
                    SuggestedQuantity = qtyToBuy,
                    EstimatedUnitCost = estimatedCost,
                    Reason = reason
                });
            }
        }

        return suggestedList.OrderBy(s => s.CurrentStock).ThenBy(s => s.ProductName).ToList();
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

    public static (bool HasConflict, string MatchedAllergy, string WarningDetails) CheckMedicationAllergy(
        PatientRecord? patient, Product? product, string? medicineName = null)
    {
        if (patient == null || !patient.HasAllergies || string.IsNullOrWhiteSpace(patient.Allergies))
        {
            return (false, string.Empty, string.Empty);
        }

        var medName = (medicineName ?? product?.Name ?? string.Empty).Trim();
        var generic = (product?.GenericName ?? string.Empty).Trim();
        var medCombined = $"{medName} {generic}".Trim();
        if (string.IsNullOrWhiteSpace(medCombined))
        {
            return (false, string.Empty, string.Empty);
        }

        var normalizedMed = TextSearch.Normalize(medCombined);

        // Separar alergias declaradas por el paciente
        var allergyTokens = patient.Allergies
            .Split(new[] { ',', ';', '/', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrWhiteSpace(a) && !PatientRecord.DeniesAllergies(a))
            .ToList();

        // Familias farmacológicas con reactividad cruzada común
        var drugFamilies = new Dictionary<string, string[]>
        {
            ["penicilina"] = new[] { "penicilina", "amoxicilina", "ampicilina", "dicloxacilina", "bencilpenicilina", "clavulonato", "clavulanico", "clavulánico", "augmentin" },
            ["betalactamico"] = new[] { "penicilina", "amoxicilina", "ampicilina", "cefalexina", "ceftriaxona", "cefotaxima", "cefuroxima" },
            ["cefalosporina"] = new[] { "cefalexina", "ceftriaxona", "cefotaxima", "cefuroxima", "cefixima", "cefazolina" },
            ["aines"] = new[] { "aspirina", "acido acetilsalicilico", "ácido acetilsalicílico", "ibuprofeno", "naproxeno", "ketorolaco", "diclofenaco", "meloxicam", "piroxicam", "indometacina", "clonixinato" },
            ["aspirina"] = new[] { "aspirina", "acido acetilsalicilico", "ácido acetilsalicílico", "aas" },
            ["ibuprofeno"] = new[] { "ibuprofeno", "motrin", "advil" },
            ["sulfa"] = new[] { "sulfa", "sulfas", "sulfametoxazol", "trimetoprima", "bactrim", "sulfadiazina", "sulfasalazina" },
            ["macrolido"] = new[] { "azitromicina", "claritromicina", "eritromicina" },
            ["quinolona"] = new[] { "ciprofloxacino", "levofloxacino", "moxifloxacino" }
        };

        foreach (var allergy in allergyTokens)
        {
            var normAllergy = TextSearch.Normalize(allergy);

            // 1. Coincidencia directa de subcadena
            if (normalizedMed.Contains(normAllergy) || (normAllergy.Length >= 4 && normAllergy.Contains(normalizedMed)))
            {
                return (true, allergy, $"El paciente registra alergia a '{allergy}'. El medicamento recetado contiene '{medName}'.");
            }

            // 2. Coincidencia por familias de fármacos
            foreach (var kvp in drugFamilies)
            {
                var familyKey = kvp.Key;
                var members = kvp.Value;

                bool allergyMatchesFamily = normAllergy.Contains(familyKey) || members.Any(m => normAllergy.Contains(m));
                if (allergyMatchesFamily)
                {
                    bool medMatchesFamily = normalizedMed.Contains(familyKey) || members.Any(m => normalizedMed.Contains(m));
                    if (medMatchesFamily)
                    {
                        return (true, allergy, $"Riesgo de hipersensibilidad: El paciente registra alergia a '{allergy}' (grupo {familyKey.ToUpper()}). El medicamento recetado '{medName}' pertenece o presenta reactividad cruzada con esta familia farmacológica.");
                    }
                }
            }
        }

        return (false, string.Empty, string.Empty);
    }
}

public enum CatalogRefKind
{
    Product,
    Service
}

/// <summary>Producto o servicio del catálogo con su Id local y su identificador de sincronización.</summary>
public sealed record CatalogRef(CatalogRefKind Kind, int Id, Guid SyncId, string Code, string Name, bool IsActive);
