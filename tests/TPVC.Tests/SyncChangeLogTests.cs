using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>Para las pruebas: una base como las de antes del paso 8.5.</summary>
internal static class ChangeLogTestSupport
{
    /// <summary>Quita los triggers, el registro de cambios, SyncApplyState y DbMeta (como una base de v1.20.2 o anterior).</summary>
    public static void RemoveChangeLog(AppDbContext db)
    {
        foreach (var name in TriggerNames(db)) db.Database.ExecuteSqlRaw($"DROP TRIGGER \"{name}\";");
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS \"SyncChangeLog\"; DROP TABLE IF EXISTS \"SyncApplyState\"; DROP TABLE IF EXISTS \"DbMeta\";");
    }

    public static List<string> TriggerNames(AppDbContext db) =>
        db.Database.SqlQueryRaw<string>(
                $"SELECT name AS Value FROM sqlite_master WHERE type = 'trigger' AND substr(name, 1, {SyncChangeLog.TriggerPrefix.Length}) = '{SyncChangeLog.TriggerPrefix}'")
            .AsEnumerable().ToList();

    /// <summary>El Seq con que está anotada una fila (null si no está).</summary>
    public static long? SeqOf(AppDbContext db, string entityType, Guid syncId) =>
        db.Database.SqlQueryRaw<long>("SELECT \"Seq\" AS Value FROM \"SyncChangeLog\" WHERE \"EntityType\" = {0} AND \"SyncId\" = {1}",
                entityType, syncId.ToString().ToUpperInvariant())
            .AsEnumerable().Cast<long?>().FirstOrDefault();

    public static long Count(AppDbContext db, string fromWhere) =>
        db.Database.SqlQueryRaw<long>($"SELECT COUNT(*) AS Value FROM {fromWhere}").AsEnumerable().First();
}

/// <summary>Registro de cambios por secuencia: triggers, lápidas y bajada sin anotar (paso 8.5 de la Fase 8).</summary>
[TestClass]
[DoNotParallelize]
public class SyncChangeLogTests
{
    private string _dir = string.Empty;
    private DbContextOptions<AppDbContext> _options = null!;
    private InventoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tpvc_changelog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={Path.Combine(_dir, "consultorio.db")}");
        _options = builder.Options;

        using (var db = NewContext())
        {
            DatabaseInitializer.Initialize(db);
        }
        _service = new InventoryService(new AppDbContextFactory(_options));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AppDbContext NewContext() => new(_options);

    [TestMethod]
    public void EachSyncedTable_HasItsThreeTriggers_AndInstallingAgainChangesNothing()
    {
        using var db = NewContext();
        var triggers = ChangeLogTestSupport.TriggerNames(db);
        var tables = SyncSchema.FromModel(db.Model).Tables;

        Assert.AreEqual(13, tables.Count);
        Assert.AreEqual(tables.Count * 3, triggers.Count);
        foreach (var name in triggers)
        {
            Assert.IsTrue(Regex.IsMatch(name, "^TPVC_sync_[A-Za-z]+_(ins|upd|del)_[0-9a-f]{8}$"), $"El nombre lleva el hash de su definición: {name}");
        }
        Assert.IsNotNull(DbMeta.Get(db, DbMeta.DbInstanceId));

        SyncChangeLog.EnsureInstalled(db);
        CollectionAssert.AreEquivalent(triggers, ChangeLogTestSupport.TriggerNames(db), "Al volver a abrir no se toca nada");
    }

    [TestMethod]
    public async Task EditingARow_LogsItAgainWithAHigherSeq()
    {
        var product = (await _service.GetProductsAsync()).First();
        long before;
        using (var db = NewContext()) before = ChangeLogTestSupport.SeqOf(db, nameof(Product), product.SyncId)!.Value;

        product.Price += 1;
        await _service.SaveProductAsync(product);

        using (var db = NewContext())
        {
            Assert.IsGreaterThan(before, ChangeLogTestSupport.SeqOf(db, nameof(Product), product.SyncId)!.Value);
            Assert.AreEqual(1L, ChangeLogTestSupport.Count(db,
                $"\"SyncChangeLog\" WHERE \"SyncId\" = '{product.SyncId.ToString().ToUpperInvariant()}'"), "Una fila, una anotación");
        }
    }

    [TestMethod]
    public async Task DeletingASale_LeavesTombstonesForItsItems_StampedAfterEachRow()
    {
        var products = (await _service.GetProductsAsync()).Where(p => p.CurrentStock > 2).Take(2).ToList();
        var sale = await _service.RegisterSaleAsync(products.Select(p => (p.Id, 1, p.Price)).ToArray(), "Público General", "Efectivo");
        Assert.IsNotNull(sale);

        using var db = NewContext();
        var items = db.SaleItems.AsNoTracking().Where(i => i.SaleId == sale.Id).ToList();
        Assert.HasCount(2, items);

        // Una partida llegó de un equipo con el reloj adelantado: su lápida va 1 ms después, no a la hora de aquí
        var ahead = DateTime.UtcNow.AddMinutes(10);
        db.Database.ExecuteSqlRaw("UPDATE \"SaleItems\" SET \"UpdatedAtUtc\" = {0} WHERE \"Id\" = {1}", TursoSyncService.FormatDate(ahead), items[0].Id);
        items[0].UpdatedAtUtc = ahead;

        // La cascada la hace SQLite, sin que EF cargue las partidas: antes no dejaba lápidas
        db.Database.ExecuteSqlRaw("DELETE FROM \"Sales\" WHERE \"Id\" = {0}", sale.Id);

        var tombstones = db.SyncTombstones.AsNoTracking().ToList();
        Assert.IsTrue(tombstones.Any(t => t.EntityType == nameof(Sale) && t.SyncId == sale.SyncId));
        foreach (var item in items)
        {
            var tombstone = tombstones.Single(t => t.EntityType == nameof(SaleItem) && t.SyncId == item.SyncId);
            Assert.IsGreaterThan(item.UpdatedAtUtc, tombstone.DeletedAtUtc);
            Assert.IsNotNull(ChangeLogTestSupport.SeqOf(db, nameof(SaleItem), item.SyncId));
        }
        Assert.AreEqual(ahead.AddMilliseconds(1), tombstones.Single(t => t.SyncId == items[0].SyncId).DeletedAtUtc);
        Assert.AreEqual(2L, ChangeLogTestSupport.Count(db, "\"SyncChangeLog\" WHERE \"EntityType\" = 'SaleItem' AND \"Deleted\" = 1"));
    }

    [TestMethod]
    public void TheTombstoneStamp_UsesTheClockOffsetOfDbMeta()
    {
        using var db = NewContext();
        DbMeta.Set(db, DbMeta.ClockOffsetMs, "600000");
        var category = new Category { Name = "Sin productos" };
        db.Categories.Add(category);
        db.SaveChanges();
        var beforeDelete = DateTime.UtcNow;

        db.Database.ExecuteSqlRaw("DELETE FROM \"Categories\" WHERE \"Id\" = {0}", category.Id);

        // 'now' de SQLite en Windows avanza de a ~16 ms: se compara con un margen de un segundo
        var deletedAt = db.SyncTombstones.AsNoTracking().Single(t => t.SyncId == category.SyncId).DeletedAtUtc;
        Assert.IsGreaterThanOrEqualTo(beforeDelete.AddMinutes(10).AddSeconds(-1), deletedAt, "La hora de la nube: la de aquí más el desfase");
        Assert.IsLessThan(DateTime.UtcNow.AddMinutes(11), deletedAt);
    }

    [TestMethod]
    public void AnOldTriggerWithoutANewColumn_IsReplaced_AndThatColumnIsLoggedAgain()
    {
        using var db = NewContext();
        var current = ChangeLogTestSupport.TriggerNames(db).Single(n => n.StartsWith("TPVC_sync_Patients_upd_", StringComparison.Ordinal));

        // La versión anterior del modelo no tenía "Occupation": su trigger no la vigila
        db.Database.ExecuteSqlRaw($"DROP TRIGGER \"{current}\";");
        db.Database.ExecuteSqlRaw("CREATE TRIGGER \"TPVC_sync_Patients_upd_00000000\" AFTER UPDATE OF \"FullName\" ON \"Patients\" BEGIN " +
                                  "DELETE FROM \"SyncChangeLog\" WHERE \"EntityType\" = 'PatientRecord' AND \"SyncId\" = upper(NEW.\"SyncId\"); " +
                                  "INSERT INTO \"SyncChangeLog\" (\"EntityType\", \"SyncId\", \"Deleted\") VALUES ('PatientRecord', upper(NEW.\"SyncId\"), 0); END;");
        var patient = db.Patients.AsNoTracking().First();
        var before = ChangeLogTestSupport.SeqOf(db, nameof(PatientRecord), patient.SyncId);
        db.Database.ExecuteSqlRaw("UPDATE \"Patients\" SET \"Occupation\" = 'Docente' WHERE \"Id\" = {0}", patient.Id);
        Assert.AreEqual(before, ChangeLogTestSupport.SeqOf(db, nameof(PatientRecord), patient.SyncId), "Con el trigger viejo, la columna nueva no se anota");

        // Al abrir la versión nueva: CREATE TRIGGER IF NOT EXISTS lo habría dejado así
        SyncChangeLog.EnsureInstalled(db);

        var names = ChangeLogTestSupport.TriggerNames(db);
        Assert.DoesNotContain("TPVC_sync_Patients_upd_00000000", names);
        Assert.Contains(current, names);
        db.Database.ExecuteSqlRaw("UPDATE \"Patients\" SET \"Occupation\" = 'Enfermera' WHERE \"Id\" = {0}", patient.Id);
        Assert.IsGreaterThan(before!.Value, ChangeLogTestSupport.SeqOf(db, nameof(PatientRecord), patient.SyncId)!.Value);
    }

    [TestMethod]
    public async Task WhatArrivesFromTheCloud_IsNotLogged_UnlessItIsMarkedForUpload()
    {
        using var db = NewContext();
        var syncId = Guid.NewGuid();
        db.ApplyingRemoteChanges = true;
        db.Categories.Add(new Category { Name = "Llegó de la tablet", SyncId = syncId, UpdatedAtUtc = DateTime.UtcNow });
        await SyncChangeLog.SaveRemoteChangesAsync(db);

        Assert.IsNull(ChangeLogTestSupport.SeqOf(db, nameof(Category), syncId), "Lo recibido no se anota para volver a subir");
        Assert.AreEqual(0L, ChangeLogTestSupport.Count(db, "\"SyncApplyState\""), "Los triggers se vuelven a prender al terminar");

        // Lo que se decide al bajar y sí debe subir (una combinación, paso 8.22)
        await SyncChangeLog.MarkForUploadAsync(db, nameof(Category), syncId);
        var marked = ChangeLogTestSupport.SeqOf(db, nameof(Category), syncId);
        Assert.IsNotNull(marked);

        // Un borrado que llega de la nube tampoco deja lápida aquí
        db.Categories.Remove(db.Categories.Single(c => c.SyncId == syncId));
        await SyncChangeLog.SaveRemoteChangesAsync(db);
        Assert.IsFalse(db.SyncTombstones.Any(t => t.SyncId == syncId));
        Assert.AreEqual(marked, ChangeLogTestSupport.SeqOf(db, nameof(Category), syncId));

        // Y lo que se guarda aquí después sí se anota
        db.ApplyingRemoteChanges = false;
        db.Categories.Add(new Category { Name = "Hecha aquí" });
        await db.SaveChangesAsync();
        Assert.IsNotNull(ChangeLogTestSupport.SeqOf(db, nameof(Category), db.Categories.Single(c => c.Name == "Hecha aquí").SyncId));
    }

    [TestMethod]
    public void TheFirstInstallOnADatabaseWithData_MakesABackupFirst()
    {
        using (var db = NewContext()) ChangeLogTestSupport.RemoveChangeLog(db);

        using (var db = NewContext()) DatabaseInitializer.EnsureSchema(db);
        var backups = Path.Combine(_dir, "backups");
        var made = Directory.GetFiles(backups, "consultorio-*-antes-registro-de-cambios.db");
        Assert.HasCount(1, made);

        using (var backup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={made[0]};Pooling=False").Options))
        {
            Assert.IsEmpty(ChangeLogTestSupport.TriggerNames(backup), "El respaldo es la base tal como estaba");
            Assert.IsGreaterThan(0, backup.Products.Count());
        }

        using (var db = NewContext())
        {
            Assert.HasCount(39, ChangeLogTestSupport.TriggerNames(db));
            DatabaseInitializer.EnsureSchema(db);
        }
        Assert.HasCount(1, Directory.GetFiles(backups, "consultorio-*-antes-registro-de-cambios.db"), "Solo la primera vez");
    }
}
