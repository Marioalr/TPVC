using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Lo que el panel «Negocio» lee de la base (pedido del médico del 2026-10-22). El ViewModel lo recibe inyectado, así una
/// prueba puede darle otros datos; las cuentas son las reglas puras de <c>Services/Rules/BusinessReportRules.cs</c> y
/// <c>BusinessStockRules.cs</c>.
/// </summary>
public interface IBusinessReportQueries
{
    /// <summary>
    /// Los renglones de las ventas no canceladas y las entradas por compra y consumos en consulta con fecha en [<paramref name="from"/>,
    /// <paramref name="to"/>) (hora del consultorio), más el catálogo de productos y servicios.
    /// </summary>
    Task<BusinessReportData> LoadBusinessReportAsync(DateTime from, DateTime to);
}

public partial class InventoryService : IBusinessReportQueries
{
    /// <inheritdoc />
    /// <remarks>
    /// Proyecciones sin seguimiento: solo las columnas que usan las reglas. Las fechas de Sales e InventoryMovements tienen índice
    /// (IX_Sales_Date, IX_InventoryMovements_Date); los decimales se leen como texto y se suman en memoria (SQLite no suma decimal).
    /// </remarks>
    public async Task<BusinessReportData> LoadBusinessReportAsync(DateTime from, DateTime to)
    {
        await using var db = CreateContext();

        var lines = await db.SaleItems
            .AsNoTracking()
            .Where(i => i.Sale != null && !i.Sale.IsCancelled && i.Sale.Date >= from && i.Sale.Date < to)
            .Select(i => new BusinessSaleLine(
                i.SaleId,
                i.Sale!.Date,
                i.Sale.IsCancelled,
                i.ProductId,
                i.ClinicalServiceId,
                i.IsService,
                i.Code,
                i.Description,
                i.Quantity,
                i.UnitPrice,
                i.Subtotal,
                i.UnitCost,
                i.IsIndividualUnits))
            .ToListAsync();

        var movements = await db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.Date >= from && m.Date < to
                        && (m.MovementType == MovementType.EntradaCompra || m.MovementType == MovementType.SalidaConsulta))
            .Select(m => new BusinessStockMovement(m.ProductId, m.Date, m.MovementType, m.Quantity, m.UnitCost, m.Reason))
            .ToListAsync();

        // Todo el catálogo (también lo dado de baja: un renglón viejo lo nombra), con la categoría que decide medicamento o insumo
        var products = await db.Products
            .AsNoTracking()
            .Select(p => new Product
            {
                Id = p.Id,
                Code = p.Code,
                Name = p.Name,
                CategoryId = p.CategoryId,
                Category = p.Category == null ? null : new Category { Id = p.Category.Id, Name = p.Category.Name },
                Price = p.Price,
                UnitCost = p.UnitCost,
                CostExcludesVat = p.CostExcludesVat,
                UnitsPerPackage = p.UnitsPerPackage,
                CurrentStock = p.CurrentStock,
                IsActive = p.IsActive
            })
            .ToListAsync();

        var services = await db.ClinicalServices
            .AsNoTracking()
            .Select(s => new ClinicalService { Id = s.Id, Code = s.Code, Name = s.Name, Category = s.Category, Price = s.Price, IsActive = s.IsActive })
            .ToListAsync();

        return new BusinessReportData { SaleLines = lines, Movements = movements, Products = products, Services = services };
    }
}
