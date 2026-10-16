using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Folios;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.16 (respuesta 1 del médico): un solo consecutivo por serie desde FolioCounters en Turso; sin conexión, la letra del
/// equipo con su consecutivo guardado fuera de la base; con la nube apagada, como en v1.19.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    // El día del consultorio (ClinicClock), el mismo que usan los marcadores de los escenarios
    private static string ExpSeries => FolioFactory.Series(FolioKind.Patient, ClinicClock.Today);

    private long? CloudCounter(string series) => CloudScalar($"SELECT LastNumber FROM FolioCounters WHERE Series = '{series}'") as long?;

    private static Task<PatientRecord> NewPatientAsync(Device device, string name) =>
        device.Inventory.SavePatientAsync(new PatientRecord { FullName = name, Age = 30 });

    private static async Task<Sale> SellOneAsync(Device device)
    {
        var batch = (await device.Inventory.GetBatchesAsync()).First(b => !b.IsDepleted && b.Quantity > 2);
        var product = (await device.Inventory.GetProductsAsync()).Single(p => p.Id == batch.ProductId);
        return (await device.Inventory.RegisterSaleAsync(new[] { (product.Id, 1, product.Price) }, "Público General", "Efectivo"))!;
    }

    /// <summary>La PC con la nube verificada (P1) y una laptop unida (P2).</summary>
    private async Task<(Device Pc, Device Laptop)> TwoVerifiedPcsAsync()
    {
        var pc = await CloudWithIdentityAsync();
        var laptop = CreateDevice("laptop", seed: false);
        Assert.IsTrue((await laptop.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.AreEqual("P1", pc.Settings.CurrentSettings.DeviceCode);
        Assert.AreEqual("P2", laptop.Settings.CurrentSettings.DeviceCode);
        return (pc, laptop);
    }

    [TestMethod]
    public async Task TwoDevicesOnline_TakeConsecutiveNumbers_WithoutRepeating()
    {
        var (pc, laptop) = await TwoVerifiedPcsAsync();
        int start = FolioFactory.MaxIn(ExpSeries, (await pc.Inventory.GetPatientsAsync()).Select(p => p.RecordNumber));
        Assert.AreEqual((long)start, CloudCounter(ExpSeries) ?? 0, "La «Sincronización completa» sembró el contador con los expedientes de la PC");

        // Ninguno sincroniza entre uno y otro: el contador de la nube es el que no deja repetir
        var a = await NewPatientAsync(pc, "Paciente Uno En La PC");
        var b = await NewPatientAsync(laptop, "Paciente Dos En La Laptop");
        var c = await NewPatientAsync(pc, "Paciente Tres En La PC");
        CollectionAssert.AreEqual(
            new[] { start + 1, start + 2, start + 3 }.Select(n => FolioFactory.Format(FolioKind.Patient, ExpSeries, n)).ToArray(),
            new[] { a.RecordNumber, b.RecordNumber, c.RecordNumber });
        Assert.AreEqual((long)start + 3, CloudCounter(ExpSeries));

        var t1 = await SellOneAsync(laptop);
        var t2 = await SellOneAsync(pc);
        var tktSeries = FolioFactory.Series(FolioKind.Sale, DateTime.Today);
        var first = FolioFactory.NumberIn(tktSeries, t1.Folio);
        Assert.IsNotNull(first, t1.Folio);
        Assert.AreEqual(FolioFactory.Format(FolioKind.Sale, tktSeries, first.Value + 1), t2.Folio);

        // Al sincronizar no hay nada repetido
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsEmpty(pc.Sync.DuplicateFolios);
    }

    [TestMethod]
    public async Task WithoutConnection_EachDeviceUsesItsCode_AndARestoreDoesNotRepeatAPrintedFolio()
    {
        var (pc, laptop) = await TwoVerifiedPcsAsync();
        _server.IsOffline = true;

        var p1 = await NewPatientAsync(pc, "Paciente Sin Red Uno");
        var p2 = await NewPatientAsync(pc, "Paciente Sin Red Dos");
        var l1 = await NewPatientAsync(laptop, "Paciente Sin Red De La Laptop");
        Assert.AreEqual($"{ExpSeries}-P1-001", p1.RecordNumber);
        Assert.AreEqual($"{ExpSeries}-P1-002", p2.RecordNumber);
        Assert.AreEqual($"{ExpSeries}-P2-001", l1.RecordNumber);
        var ticket = await SellOneAsync(pc);
        Assert.AreEqual($"{FolioFactory.Series(FolioKind.Sale, DateTime.Today)}-P1-0001", ticket.Folio);
        Assert.AreEqual(2, pc.Sync.OfflineFolios.Last($"{ExpSeries}-P1"), "El número va también fuera de la base");

        // Los lectores ignoran los folios con letra: el siguiente probable sigue siendo el de la serie normal
        int max = FolioFactory.MaxIn(ExpSeries, (await pc.Inventory.GetPatientsAsync()).Select(p => p.RecordNumber));
        Assert.AreEqual(FolioFactory.Format(FolioKind.Patient, ExpSeries, max + 1), await pc.Inventory.GenerateNextRecordNumberAsync());

        // Un respaldo de antes (aquí: la base sin esos dos expedientes) no repite el EXP con letra que ya se imprimió
        using (var db = pc.Factory.CreateDbContext())
        {
            db.Patients.RemoveRange(db.Patients.Where(p => p.RecordNumber.Contains("-P1-")));
            await db.SaveChangesAsync();
        }
        Assert.AreEqual($"{ExpSeries}-P1-003", (await NewPatientAsync(pc, "Paciente Después Del Respaldo")).RecordNumber);

        // Con red otra vez, todo sube sin choques y con el número normal de nuevo
        _server.IsOffline = false;
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.IsEmpty(pc.Sync.DuplicateFolios);
        var online = await NewPatientAsync(laptop, "Paciente Con Red Otra Vez");
        Assert.IsNotNull(FolioFactory.NumberIn(ExpSeries, online.RecordNumber), $"Con red, el folio vuelve a la serie normal ({online.RecordNumber})");
    }

    [TestMethod]
    public async Task ACounterThatDoesNotAnswerIn2Seconds_GivesAFolioWithTheDeviceCode()
    {
        var pc = await CloudWithIdentityAsync();
        _server.BeforeExecuteAsync = sql => sql.Contains("FolioCounters", StringComparison.Ordinal) ? Task.Delay(TimeSpan.FromSeconds(2.6)) : Task.CompletedTask;
        var clock = Stopwatch.StartNew();
        var patient = await NewPatientAsync(pc, "Paciente Con Red Lenta");
        clock.Stop();
        _server.BeforeExecuteAsync = null;
        Assert.AreEqual($"{ExpSeries}-P1-001", patient.RecordNumber);
        Assert.IsLessThan(2.5, clock.Elapsed.TotalSeconds, "Se esperan 2 s, no más");
    }

    [TestMethod]
    public async Task WithTheCloudOffOrUnverified_FoliosAreAsBefore()
    {
        // Sin «Sincronización completa» (nube sin verificar): numera la base, y la nube no recibe contadores
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        int max = FolioFactory.MaxIn(ExpSeries, (await pc.Inventory.GetPatientsAsync()).Select(p => p.RecordNumber));
        var requests = _server.RequestCount;
        Assert.AreEqual(FolioFactory.Format(FolioKind.Patient, ExpSeries, max + 1), (await NewPatientAsync(pc, "Paciente Nube Sin Verificar")).RecordNumber);
        Assert.AreEqual(requests, _server.RequestCount, "No se preguntó a la nube");

        // Con la nube apagada, lo mismo
        var settings = pc.Settings.CurrentSettings;
        settings.IsSyncEnabled = false;
        pc.Settings.SaveSettings(settings);
        Assert.AreEqual(FolioFactory.Format(FolioKind.Patient, ExpSeries, max + 2), (await NewPatientAsync(pc, "Paciente Nube Apagada")).RecordNumber);
        Assert.AreEqual(requests, _server.RequestCount);
    }

    [TestMethod]
    public async Task TheCounterSkipsNumbersThisDeviceAlreadyHas_AndATypedNumberRaisesIt()
    {
        var (pc, laptop) = await TwoVerifiedPcsAsync();
        int start = (int)(CloudCounter(ExpSeries) ?? 0);

        // Un expediente que la PC numeró sola (una versión vieja): el piso local lo salta
        using (var db = pc.Factory.CreateDbContext())
        {
            db.Patients.Add(new PatientRecord { FullName = "Paciente De Una Version Vieja", RecordNumber = FolioFactory.Format(FolioKind.Patient, ExpSeries, start + 5) });
            await db.SaveChangesAsync();
        }
        Assert.AreEqual(FolioFactory.Format(FolioKind.Patient, ExpSeries, start + 6), (await NewPatientAsync(pc, "Paciente Despues Del Salto")).RecordNumber);

        // Un número tecleado a mano mayor que el contador lo sube: la laptop sigue después de él
        var typed = FolioFactory.Format(FolioKind.Patient, ExpSeries, start + 50);
        await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Con Folio Tecleado", Age = 40, RecordNumber = typed });
        for (int i = 0; i < 60 && CloudCounter(ExpSeries) < start + 50; i++) await Task.Delay(50);
        Assert.AreEqual((long)start + 50, CloudCounter(ExpSeries));
        Assert.AreEqual(FolioFactory.Format(FolioKind.Patient, ExpSeries, start + 51), (await NewPatientAsync(laptop, "Paciente De La Laptop")).RecordNumber);
    }

    [TestMethod]
    public async Task WithoutConnection_APcWithoutItsCode_DoesNotCreateFolios()
    {
        var pc = await CloudWithIdentityAsync();
        var settings = pc.Settings.CurrentSettings;
        settings.DeviceCode = null;
        pc.Settings.SaveSettings(settings);
        _server.IsOffline = true;
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => NewPatientAsync(pc, "Paciente Sin Codigo"));
        Assert.AreEqual(TursoSyncService.NoDeviceCodeMessage, error.Message);
    }

    [TestMethod]
    public async Task ADatabaseWithTheOldUniqueIndexes_GetsNormalOnes()
    {
        var pc = CreateDevice("pc", seed: true);
        using (var db = pc.Factory.CreateDbContext())
        {
            // Como una base de v1.33 o anterior
            db.Database.ExecuteSqlRaw("DROP INDEX IX_Patients_RecordNumber; CREATE UNIQUE INDEX IX_Patients_RecordNumber ON Patients (RecordNumber); " +
                                      "DROP INDEX IX_Sales_Folio; CREATE UNIQUE INDEX IX_Sales_Folio ON Sales (Folio); PRAGMA user_version = 1;");
        }
        using (var db = pc.Factory.CreateDbContext())
        {
            TPVC.Data.DatabaseInitializer.EnsureSchema(db);
            var unique = db.Database.SqlQueryRaw<string>(
                "SELECT il.name AS Value FROM sqlite_master AS m, pragma_index_list(m.name) AS il WHERE m.type = 'table' AND il.\"unique\" = 1").ToList();
            CollectionAssert.DoesNotContain(unique, "IX_Patients_RecordNumber");
            CollectionAssert.DoesNotContain(unique, "IX_Sales_Folio");
            Assert.AreEqual(1, db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE name = 'IX_Patients_RecordNumber'").Single(),
                "El índice sigue, ahora normal");
        }
    }
}
