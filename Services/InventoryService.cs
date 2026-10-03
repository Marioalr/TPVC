using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
}

public class InventoryService
{
    private readonly AppDbContext _context;

    public event Action? OnInventoryChanged;

    public InventoryService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<Product>> GetProductsAsync(
        string? searchText = null, 
        int? categoryId = null, 
        bool onlyLowStock = false, 
        bool onlyExpiring = false)
    {
        var query = _context.Products
            .Include(p => p.Category)
            .Include(p => p.Batches)
            .Where(p => p.IsActive)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var search = searchText.Trim().ToLower();
            query = query.Where(p => 
                p.Name.ToLower().Contains(search) || 
                p.Code.ToLower().Contains(search) || 
                (p.GenericName != null && p.GenericName.ToLower().Contains(search)) ||
                (p.Location != null && p.Location.ToLower().Contains(search))
            );
        }

        if (categoryId.HasValue && categoryId.Value > 0)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        if (onlyLowStock)
        {
            query = query.Where(p => p.CurrentStock <= p.MinStock);
        }

        var products = await query.OrderBy(p => p.Name).ToListAsync();

        if (onlyExpiring)
        {
            products = products
                .Where(p => p.Batches.Any(b => !b.IsDepleted && b.Quantity > 0 && b.DaysRemaining <= 60))
                .ToList();
        }

        return products;
    }

    public async Task<Product?> GetProductByIdAsync(int id)
    {
        return await _context.Products
            .Include(p => p.Category)
            .Include(p => p.Batches)
            .Include(p => p.Movements)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task SaveProductAsync(Product product)
    {
        product.UpdatedAt = DateTime.Now;

        if (product.Id == 0)
        {
            product.CreatedAt = DateTime.Now;
            _context.Products.Add(product);
            await _context.SaveChangesAsync();

            // Si tiene stock inicial, registrar movimiento de entrada
            if (product.CurrentStock > 0)
            {
                _context.InventoryMovements.Add(new InventoryMovement
                {
                    ProductId = product.Id,
                    MovementType = MovementType.EntradaAjuste,
                    Quantity = product.CurrentStock,
                    StockBefore = 0,
                    StockAfter = product.CurrentStock,
                    Date = DateTime.Now,
                    Reason = "Inventario inicial de alta de producto",
                    Responsible = "Dr. Consulta"
                });
                await _context.SaveChangesAsync();
            }
        }
        else
        {
            _context.Products.Update(product);
            await _context.SaveChangesAsync();
        }

        OnInventoryChanged?.Invoke();
    }

    public async Task DeleteProductAsync(int id)
    {
        var product = await _context.Products.FindAsync(id);
        if (product != null)
        {
            product.IsActive = false; // Borrado lógico
            await _context.SaveChangesAsync();
            OnInventoryChanged?.Invoke();
        }
    }

    public async Task<List<Category>> GetCategoriesAsync()
    {
        return await _context.Categories
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task SaveCategoryAsync(Category category)
    {
        if (category.Id == 0)
        {
            _context.Categories.Add(category);
        }
        else
        {
            _context.Categories.Update(category);
        }
        await _context.SaveChangesAsync();
        OnInventoryChanged?.Invoke();
    }

    public async Task<List<ProductBatch>> GetBatchesAsync(int? productId = null)
    {
        var query = _context.ProductBatches
            .Include(b => b.Product)
            .AsQueryable();

        if (productId.HasValue)
        {
            query = query.Where(b => b.ProductId == productId.Value);
        }

        return await query
            .OrderBy(b => b.ExpirationDate)
            .ToListAsync();
    }

    public async Task SaveBatchAsync(ProductBatch batch)
    {
        if (batch.Id == 0)
        {
            _context.ProductBatches.Add(batch);
        }
        else
        {
            _context.ProductBatches.Update(batch);
        }
        await _context.SaveChangesAsync();
        OnInventoryChanged?.Invoke();
    }

    public async Task DeleteBatchAsync(int id)
    {
        var batch = await _context.ProductBatches.FindAsync(id);
        if (batch != null)
        {
            _context.ProductBatches.Remove(batch);
            await _context.SaveChangesAsync();
            OnInventoryChanged?.Invoke();
        }
    }

    // Registro rápido de consumo en consulta médica
    public async Task RegisterQuickConsultationUsageAsync(int productId, int quantity, string? notes, string? responsible = "Dr. Consulta")
    {
        var product = await _context.Products
            .Include(p => p.Batches)
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product == null) throw new InvalidOperationException("Producto no encontrado");

        int stockBefore = product.CurrentStock;
        int stockAfter = Math.Max(0, stockBefore - quantity);
        product.CurrentStock = stockAfter;
        product.UpdatedAt = DateTime.Now;

        // Descontar del lote más próximo a vencer primero (sistema FEFO: First Expired, First Out)
        var activeBatch = product.Batches
            .Where(b => !b.IsDepleted && b.Quantity > 0)
            .OrderBy(b => b.ExpirationDate)
            .FirstOrDefault();

        int? batchId = null;
        if (activeBatch != null)
        {
            batchId = activeBatch.Id;
            if (activeBatch.Quantity <= quantity)
            {
                activeBatch.Quantity = 0;
                activeBatch.IsDepleted = true;
            }
            else
            {
                activeBatch.Quantity -= quantity;
            }
        }

        _context.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = product.Id,
            BatchId = batchId,
            MovementType = MovementType.SalidaConsulta,
            Quantity = quantity,
            StockBefore = stockBefore,
            StockAfter = stockAfter,
            Date = DateTime.Now,
            Reason = string.IsNullOrWhiteSpace(notes) ? "Uso / aplicación durante consulta médica" : notes,
            Responsible = responsible ?? "Dr. Consulta"
        });

        await _context.SaveChangesAsync();
        OnInventoryChanged?.Invoke();
    }

    // Registro de venta en Punto de Venta (POS) con carrito
    public async Task RegisterSaleAsync(
        IEnumerable<(int ProductId, int Quantity, decimal UnitPrice)> items, 
        string? patientName, 
        string? paymentMethod, 
        string? responsible = "Dr. / Farmacia")
    {
        var itemsList = items.ToList();
        if (itemsList.Count == 0) return;

        foreach (var item in itemsList)
        {
            var product = await _context.Products
                .Include(p => p.Batches)
                .FirstOrDefaultAsync(p => p.Id == item.ProductId);

            if (product == null) continue;

            int stockBefore = product.CurrentStock;
            int stockAfter = Math.Max(0, stockBefore - item.Quantity);
            product.CurrentStock = stockAfter;
            product.UpdatedAt = DateTime.Now;

            // Descontar por FEFO (First Expired, First Out)
            int remainingToDeduct = item.Quantity;
            var activeBatches = product.Batches
                .Where(b => !b.IsDepleted && b.Quantity > 0)
                .OrderBy(b => b.ExpirationDate)
                .ToList();

            int? primaryBatchId = null;
            foreach (var batch in activeBatches)
            {
                if (remainingToDeduct <= 0) break;
                primaryBatchId ??= batch.Id;

                if (batch.Quantity <= remainingToDeduct)
                {
                    remainingToDeduct -= batch.Quantity;
                    batch.Quantity = 0;
                    batch.IsDepleted = true;
                }
                else
                {
                    batch.Quantity -= remainingToDeduct;
                    remainingToDeduct = 0;
                }
            }

            string clientText = string.IsNullOrWhiteSpace(patientName) ? "Público General" : patientName.Trim();
            string methodText = string.IsNullOrWhiteSpace(paymentMethod) ? "Efectivo" : paymentMethod.Trim();

            _context.InventoryMovements.Add(new InventoryMovement
            {
                ProductId = product.Id,
                BatchId = primaryBatchId,
                MovementType = MovementType.SalidaVenta,
                Quantity = item.Quantity,
                StockBefore = stockBefore,
                StockAfter = stockAfter,
                Date = DateTime.Now,
                Reason = $"Venta POS - Paciente/Cliente: {clientText} ({methodText})",
                Responsible = responsible ?? "Dr. / Farmacia"
            });
        }

        await _context.SaveChangesAsync();
        OnInventoryChanged?.Invoke();
    }

    // Registro genérico de movimiento
    public async Task RegisterMovementAsync(InventoryMovement movement)
    {
        var product = await _context.Products.FindAsync(movement.ProductId);
        if (product == null) throw new InvalidOperationException("Producto no encontrado");

        movement.StockBefore = product.CurrentStock;

        if (movement.IsEntry)
        {
            movement.StockAfter = product.CurrentStock + movement.Quantity;
        }
        else
        {
            movement.StockAfter = Math.Max(0, product.CurrentStock - movement.Quantity);
        }

        product.CurrentStock = movement.StockAfter;
        product.UpdatedAt = DateTime.Now;
        movement.Date = DateTime.Now;

        _context.InventoryMovements.Add(movement);
        await _context.SaveChangesAsync();
        OnInventoryChanged?.Invoke();
    }

    public async Task<List<InventoryMovement>> GetRecentMovementsAsync(int count = 50)
    {
        return await _context.InventoryMovements
            .Include(m => m.Product)
            .ThenInclude(p => p!.Category)
            .Include(m => m.Batch)
            .OrderByDescending(m => m.Date)
            .Take(count)
            .ToListAsync();
    }

    public async Task<DashboardMetrics> GetDashboardMetricsAsync()
    {
        var today = DateTime.Today;

        var products = await _context.Products
            .Where(p => p.IsActive)
            .Include(p => p.Batches)
            .ToListAsync();

        var todayMovements = await _context.InventoryMovements
            .Where(m => m.Date >= today)
            .ToListAsync();

        var batches = await _context.ProductBatches
            .Where(b => !b.IsDepleted && b.Quantity > 0)
            .ToListAsync();

        return new DashboardMetrics
        {
            TotalProducts = products.Count,
            LowStockCount = products.Count(p => p.CurrentStock <= p.MinStock),
            ExpiringSoonCount = batches.Count(b => b.DaysRemaining >= 0 && b.DaysRemaining <= 60),
            ExpiredCount = batches.Count(b => b.DaysRemaining < 0),
            MovementsToday = todayMovements.Count,
            ConsumedInConsultationToday = todayMovements
                .Where(m => m.MovementType == MovementType.SalidaConsulta)
                .Sum(m => m.Quantity)
        };
    }
}
