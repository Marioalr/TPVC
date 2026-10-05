using System;
using System.Collections.Generic;
using System.Linq;
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
            query = query.Where(p => p.CurrentStock <= p.MinStock);
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

    public async Task SaveCategoryAsync(Category category)
    {
        await using var db = CreateContext();
        if (category.Id == 0)
        {
            db.Entry(category).State = EntityState.Added;
        }
        else
        {
            var existing = await db.Categories.FindAsync(category.Id)
                ?? throw new InvalidOperationException("La categoría ya no existe.");
            existing.Name = category.Name;
            existing.Description = category.Description;
            existing.Icon = category.Icon;
        }
        await db.SaveChangesAsync();
        NotifyInventoryChanged();
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
            LowStockCount = await activeProducts.CountAsync(p => p.CurrentStock <= p.MinStock),
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
    public async Task<List<ClinicalService>> GetClinicalServicesAsync()
    {
        await using var db = CreateContext();
        return await db.ClinicalServices
            .AsNoTracking()
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

            db.ServiceDefaultProducts.RemoveRange(existing.DefaultProducts);
            foreach (var dp in defaults)
            {
                dp.ClinicalServiceId = existing.Id;
                db.ServiceDefaultProducts.Add(dp);
            }
        }

        await db.SaveChangesAsync();
        NotifyInventoryChanged();
        return service;
    }

    public async Task DeleteClinicalServiceAsync(int id)
    {
        await using var db = CreateContext();
        var service = await db.ClinicalServices
            .Include(s => s.DefaultProducts)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (service != null)
        {
            db.ClinicalServices.Remove(service);
            await db.SaveChangesAsync();
            NotifyInventoryChanged();
        }
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

        if (patient.Id == 0)
        {
            patient.CreatedAt = DateTime.Now;
            db.Patients.Add(patient);
        }
        else
        {
            var existing = await db.Patients.FirstOrDefaultAsync(p => p.Id == patient.Id);
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
        NotifyPatientsChanged();
        return patient;
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

        db.Entry(consultation).State = EntityState.Added;
        await db.SaveChangesAsync();

        NotifyPatientsChanged();
        return consultation;
    }
}
