using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Revisión 18 (panel «Negocio»): el costo al vender (<c>SaleItems.UnitCost</c>) y el de cada entrada o consumo
/// (<c>InventoryMovements.UnitCost</c>) viajan a los otros equipos, y una nube de la revisión 17 recibe las columnas.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    [TestMethod]
    public async Task ACloudFromRevision17_GetsTheCostColumns_AndTheCostAtSaleTravels()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // Como la dejó v1.47.0: sin las columnas del costo y con la huella y la revisión de entonces
        ExecuteCloud("ALTER TABLE SaleItems DROP COLUMN UnitCost; ALTER TABLE InventoryMovements DROP COLUMN UnitCost; " +
                     "UPDATE _tpvc_meta SET Value = 'huella-v1.47' WHERE Key = 'schema'; UPDATE _tpvc_meta SET Value = '17' WHERE Key = 'contract';");

        // La PC ya con v1.48.0 (un arranque nuevo revisa el esquema de la nube): agrega las columnas antes de subir
        var updated = CreateDevice("pc-actualizada", seed: false);
        var result = await updated.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(1L, CloudScalar("SELECT COUNT(*) FROM pragma_table_info('SaleItems') WHERE name = 'UnitCost'"), "La PC agrega la columna");
        Assert.AreEqual(1L, CloudScalar("SELECT COUNT(*) FROM pragma_table_info('InventoryMovements') WHERE name = 'UnitCost'"));
        Assert.AreEqual(SyncContract.Revision.ToString(), CloudScalar("SELECT Value FROM _tpvc_meta WHERE Key = 'contract'"));

        var product = (await updated.Inventory.GetProductsAsync()).First(p => p.CurrentStock > 2 && p.HasCost);
        var sale = await updated.Inventory.RegisterSaleAsync(new[] { (product.Id, 1, product.Price) }, "Paciente", "Efectivo");
        Assert.IsNotNull(sale);
        await updated.Inventory.RegisterQuickConsultationUsageAsync(product.Id, 1, null);
        result = await updated.Sync.SyncAsync();
        Assert.IsTrue(result.Success, result.ErrorMessage);

        var cost = product.EffectiveUnitCost;
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        await using var db = await tablet.Factory.CreateDbContextAsync();
        var item = await db.SaleItems.AsNoTracking().SingleAsync(i => i.Sale!.Folio == sale.Folio);
        Assert.AreEqual(cost, item.UnitCost, "El costo al vender llega al otro equipo");
        var usage = await db.InventoryMovements.AsNoTracking().Where(m => m.MovementType == MovementType.SalidaConsulta).ToListAsync();
        Assert.IsNotEmpty(usage);
        Assert.IsTrue(usage.All(m => m.UnitCost == cost), "Y el del consumo en consulta");
    }
}
