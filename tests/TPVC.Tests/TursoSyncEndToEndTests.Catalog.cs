using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Decisión del médico del 2026-10-22: la tablet es una copia de la PC y también edita el catálogo. Dos equipos que dan de
/// alta lo mismo sin verse dejan dos filas (cada una con su SyncId, LWW por fila); después de bajar, el equipo avisa en
/// Productos hasta «Entendido» (<see cref="CatalogDuplicateRules"/>, <see cref="SyncNotices"/>), sin unir nada.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private static async Task<Product> NewProductAsync(Device device, string code, string name, string? presentation = null)
    {
        int categoryId;
        using (var db = device.Factory.CreateDbContext()) categoryId = await db.Categories.AsNoTracking().Select(c => c.Id).FirstAsync();
        var product = new Product { Code = code, Name = name, Presentation = presentation, CategoryId = categoryId, Price = 50m };
        await device.Inventory.SaveProductAsync(product);
        return product;
    }

    private static Task<ClinicalService> NewServiceAsync(Device device, string code, string name) =>
        device.Inventory.SaveClinicalServiceAsync(new ClinicalService { Code = code, Name = name, Category = "Curaciones", Price = 200m },
            Array.Empty<(int, int, string?)>());

    [TestMethod]
    public async Task TheSameCatalogCreatedOnThePcAndTheTablet_IsKeptTwice_AndAPersistentNoticeSaysSo()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();

        // Sin red, cada equipo da de alta lo suyo: el mismo código de producto, el mismo producto con otro código y el
        // siguiente SERV-00N para dos servicios distintos
        _server.IsOffline = true;
        var ketorolacoPc = await NewProductAsync(pc, "MED-900", "Ketorolaco", "Caja 10 tabletas 10 mg");
        var ketorolacoTablet = await NewProductAsync(tablet, "med-900 ", "Ketorolaco trometamina", "Caja 10 tabletas 10 mg");
        var omeprazolPc = await NewProductAsync(pc, "MED-901", "Omeprazol", "Caja 14 cápsulas");
        var omeprazolTablet = await NewProductAsync(tablet, "MED-950", "OMEPRAZOL", "caja 14 capsulas");
        var otherPresentation = await NewProductAsync(tablet, "MED-951", "Omeprazol", "Caja 28 cápsulas");
        var servicePc = await NewServiceAsync(pc, "SERV-006", "Retiro de puntos");
        var serviceTablet = await NewServiceAsync(tablet, "SERV-006", "Lavado de oído");
        _server.IsOffline = false;

        var pcUpload = await pc.Sync.SyncAsync();
        Assert.IsTrue(pcUpload.Success, pcUpload.ErrorMessage);
        Assert.IsEmpty(pcUpload.Arrivals.DuplicateCatalog, "La PC todavía no recibe lo de la tablet");

        var tabletSync = await tablet.Sync.SyncAsync();
        Assert.IsTrue(tabletSync.Success, tabletSync.ErrorMessage);
        Assert.HasCount(3, tabletSync.Arrivals.DuplicateCatalog, "La tablet también lo ve");

        var pcSync = await pc.Sync.SyncAsync();
        Assert.IsTrue(pcSync.Success, pcSync.ErrorMessage);
        var pairs = pcSync.Arrivals.DuplicateCatalog;
        Assert.HasCount(3, pairs);
        Assert.AreEqual(CatalogDuplicateRules.Match.Code, Pair(ketorolacoPc.SyncId, ketorolacoTablet.SyncId).Match, "Mismo código (sin espacios y en mayúsculas)");
        Assert.AreEqual(CatalogDuplicateRules.Match.Name, Pair(omeprazolPc.SyncId, omeprazolTablet.SyncId).Match, "Mismo nombre y presentación");
        Assert.AreEqual(CatalogDuplicateRules.Match.Code, Pair(servicePc.SyncId, serviceTablet.SyncId).Match, "Dos servicios con el mismo SERV-006");
        Assert.IsFalse(pairs.Any(p => p.First.SyncId == otherPresentation.SyncId || p.Second.SyncId == otherPresentation.SyncId),
            "Otra presentación es otro producto");

        DuplicateCatalogPair Pair(Guid a, Guid b) =>
            pairs.Single(p => (p.First.SyncId == a && p.Second.SyncId == b) || (p.First.SyncId == b && p.Second.SyncId == a));

        // Nunca se une, se borra ni se renombra nada: las dos filas siguen con su código
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.AreEqual(2, await db.Products.CountAsync(p => p.Code.ToUpper().Trim() == "MED-900"));
            Assert.AreEqual(2, await db.ClinicalServices.CountAsync(s => s.Code == "SERV-006"));
        }

        // Productos lo muestra hasta «Entendido», también al cerrar y abrir TPVC
        var inventory = new InventoryViewModel(pc.Inventory);
        await inventory.LoadDataAsync();
        Assert.IsTrue(inventory.HasDuplicateCatalogNotice);
        StringAssert.StartsWith(inventory.DuplicateCatalogNoticeText, "Posibles registros repetidos en el catálogo con lo que llegó de otro equipo: ");
        StringAssert.Contains(inventory.DuplicateCatalogNoticeText, "«Ketorolaco (Caja 10 tabletas 10 mg)» (MED-900)");
        StringAssert.Contains(inventory.DuplicateCatalogNoticeText, "mismo código");
        StringAssert.Contains(inventory.DuplicateCatalogNoticeText, "TPVC no los une solo");

        // Se depura al mostrarse: el servicio que se recodifica ya no avisa
        var renamed = await pc.Inventory.GetClinicalServiceByIdAsync(servicePc.Id);
        renamed!.Code = "SERV-007";
        await pc.Inventory.SaveClinicalServiceAsync(renamed, Array.Empty<(int, int, string?)>());
        Assert.HasCount(2, (await pc.Inventory.GetSyncNoticesAsync()).DuplicateCatalog);

        // … y el producto que se desactiva tampoco
        var deactivated = await pc.Inventory.GetProductByIdAsync(omeprazolPc.Id);
        deactivated!.IsActive = false;
        await pc.Inventory.SaveProductAsync(deactivated);
        Assert.AreEqual(ketorolacoPc.SyncId, (await pc.Inventory.GetSyncNoticesAsync()).DuplicateCatalog
            .Select(p => p.First.SyncId == ketorolacoPc.SyncId ? p.First.SyncId : p.Second.SyncId).Single());

        var reopened = new InventoryViewModel(pc.Inventory);
        await reopened.LoadDataAsync();
        Assert.IsTrue(reopened.HasDuplicateCatalogNotice, "Sigue al cerrar y abrir");
        StringAssert.StartsWith(reopened.DuplicateCatalogNoticeText, "Posible registro repetido en el catálogo");
        await reopened.DismissDuplicateCatalogNoticeAsync();
        Assert.IsFalse(reopened.HasDuplicateCatalogNotice);
        Assert.IsEmpty((await pc.Inventory.GetSyncNoticesAsync()).DuplicateCatalog, "Descartado para siempre en este equipo");

        // Una sincronización más no vuelve a avisar lo mismo, y los avisos de pacientes no se tocan
        var again = await pc.Sync.SyncAsync();
        Assert.IsTrue(again.Arrivals.IsEmpty);
        Assert.IsEmpty((await pc.Inventory.GetSyncNoticesAsync()).DuplicatePatients);
    }

    [TestMethod]
    public async Task TheSeededCatalog_IsTheSameRowOnEveryDevice_AndAFirstDownloadDoesNotWarn()
    {
        // Dos bases nuevas que siembran el catálogo (paso 8.8) tienen las mismas filas (SyncId fijos), no duplicados
        var pc = CreateDevice("pc", seed: true, demo: false);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var second = CreateDevice("segunda", seed: true, demo: false);
        var first = await second.Sync.SyncAsync();
        Assert.IsTrue(first.Success, first.ErrorMessage);
        Assert.IsTrue(first.Arrivals.IsEmpty, "En la primera descarga llega todo: no se revisa");

        // La PC edita un servicio sembrado y la otra lo baja: es la misma fila, no un par
        var seeded = (await pc.Inventory.GetClinicalServicesAsync()).First(s => s.Code == TPVC.Data.DatabaseInitializer.SeededServiceCodes[0]);
        seeded.Price += 10m;
        await pc.Inventory.SaveClinicalServiceAsync(seeded, Array.Empty<(int, int, string?)>());
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var next = await second.Sync.SyncAsync();
        Assert.IsTrue(next.Success, next.ErrorMessage);
        Assert.IsEmpty(next.Arrivals.DuplicateCatalog);
        using var db = second.Factory.CreateDbContext();
        Assert.AreEqual(1, await db.ClinicalServices.CountAsync(s => s.Code == TPVC.Data.DatabaseInitializer.SeededServiceCodes[0]));
        Assert.AreEqual(TPVC.Data.DatabaseInitializer.SeededCategories.Length, await db.Categories.CountAsync());
    }
}
