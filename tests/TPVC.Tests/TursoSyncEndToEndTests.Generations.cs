using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.9 del plan de la Fase 8: restaurar un respaldo con la nube activa. «Reemplazar la nube con este respaldo»,
/// «Desconectar esta PC», la cerca de generación y lo que hacen los demás equipos (guardar, esperar, volver a bajar y
/// «Volver a aplicar»).
/// </summary>
public partial class TursoSyncEndToEndTests
{
    /// <summary>
    /// La PC principal con la nube verificada y la tablet unida a ella; la PC guarda un respaldo. Después del respaldo la
    /// tablet registra un paciente y la PC cambia un precio, y las dos sincronizan.
    /// </summary>
    private async Task<(Device Pc, Device Tablet, string Backup, PatientRecord AfterBackup, Product Edited, decimal OriginalPrice)> CloudWithChangesAfterABackupAsync()
    {
        var pc = await CloudWithIdentityAsync();
        var tablet = CreateDevice("tablet", seed: false);
        var join = await tablet.Sync.JoinCloudAsync();
        Assert.IsTrue(join.Ok, join.Message);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        var backup = new BackupService(pc.DbPath).CreateNamedBackup("prueba-paso-8-9");

        var patient = await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Registrado Después Del Respaldo", Age = 30 });
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var product = (await pc.Inventory.GetProductsAsync()).First();
        var original = product.Price;
        product.Price = 999.5m;
        await pc.Inventory.SaveProductAsync(product);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        return (pc, tablet, backup, patient, product, original);
    }

    private static async Task RestoreAsync(Device device, string backup)
    {
        var import = new DataImportService(device.Factory, new BackupService(device.DbPath), device.Profile, device.Inventory, device.Settings);
        var restored = await import.ImportDatabaseFileAsync(backup);
        Assert.IsTrue(restored.Success, restored.Message);
        Assert.IsTrue(restored.CloudDecisionNeeded, "Con la nube configurada hay que elegir qué hacer con ella");
    }

    private string? CloudPrice(Product product) => CloudScalar($"SELECT Price FROM Products WHERE SyncId = '{TursoSyncService.FormatGuid(product.SyncId)}'")?.ToString();

    private long CloudPatients(PatientRecord patient) =>
        (long)CloudScalar($"SELECT COUNT(*) FROM Patients WHERE SyncId = '{TursoSyncService.FormatGuid(patient.SyncId)}'")!;

    [TestMethod]
    public async Task ARestoredBackup_DoesNotSync_UntilTheCloudIsReplacedWithIt()
    {
        var (pc, _, backup, afterBackup, edited, originalPrice) = await CloudWithChangesAfterABackupAsync();
        var oldGeneration = CloudMeta("generation");
        Assert.IsNotNull(oldGeneration);

        await RestoreAsync(pc, backup);
        Assert.IsTrue(pc.Sync.IsRestoreDecisionPending);
        var blocked = await pc.Sync.SyncAsync();
        Assert.IsFalse(blocked.Success);
        StringAssert.Contains(blocked.ErrorMessage, "Se restauró un respaldo");
        Assert.AreEqual(TursoSyncStatus.Paused, pc.Sync.Status);
        Assert.AreEqual("999.5", CloudPrice(edited), "La nube sigue como estaba: la sincronización no la tocó");

        // Lo que se perdería: el paciente de la tablet y el precio que cambió la PC después del respaldo
        var (ok, message, preview) = await pc.Sync.PreviewCloudReplacementAsync();
        Assert.IsTrue(ok, message);
        Assert.IsNotNull(preview);
        Assert.AreEqual(1, preview.Lines.Single(l => l.Label == "Pacientes").Rows);
        Assert.AreEqual(1, preview.Lines.Single(l => l.Label == "Productos").Rows);
        StringAssert.Contains(preview.Summary, "Pacientes: 1");

        var result = await pc.Sync.ReplaceCloudWithLocalAsync();
        Assert.IsTrue(result.Ok, result.Message);
        AssertSameAsTheCloud(pc);
        Assert.AreEqual(0L, CloudPatients(afterBackup), "La nube quedó igual al respaldo");
        Assert.AreEqual(originalPrice, decimal.Parse(CloudPrice(edited)!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsTrue(result.Report!.IsClean, result.Report.Summary);

        var newGeneration = CloudMeta("generation");
        Assert.AreNotEqual(oldGeneration, newGeneration);
        Assert.AreEqual(newGeneration, CloudMeta("generationReady"));
        Assert.AreEqual(oldGeneration, CloudMeta("generationPrevious"));
        Assert.AreEqual("3", CloudMeta("minClientProtocol"), "Los equipos sin la cerca de generación deben actualizar");

        // Lo que se perdió quedó en un archivo y aquí, con los mismos conteos que se mostraron
        Assert.IsTrue(File.Exists(result.LostFile));
        var saved = SyncSavedChanges.ReadFile(result.LostFile!);
        Assert.AreEqual(preview.LostRows + preview.UndoneDeletes, saved.Count);
        Assert.IsTrue(saved.All(s => s.Source == SyncSavedChanges.SourceCloud && s.Protocol == TursoSyncService.ClientProtocol));
        Assert.AreEqual(saved.Count, pc.Sync.SavedChangesCount);
        Assert.IsFalse(pc.Sync.IsRestoreDecisionPending);
        Assert.IsFalse(pc.Sync.IsCloudReplacementPending);
        using (var db = pc.Factory.CreateDbContext()) Assert.AreEqual(newGeneration, DbMeta.Get(db, DbMeta.Generation));

        // La siguiente sincronización es normal y no vuelve a bajar lo que acaba de subir
        var next = await pc.Sync.SyncAsync();
        Assert.IsTrue(next.Success, next.ErrorMessage);
        Assert.AreEqual(0, next.RowsPulled);
    }

    [TestMethod]
    public async Task TheOtherDevice_SavesWhatItHadNotSent_DownloadsTheNewCloudOnce_AndReappliesIt()
    {
        var (pc, tablet, backup, afterBackup, _, _) = await CloudWithChangesAfterABackupAsync();

        // La tablet registra otro paciente y no alcanza a sincronizar antes del reemplazo
        var unsent = await tablet.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Sin Subir De La Tablet", Age = 41 });
        await RestoreAsync(pc, backup);
        Assert.IsTrue((await pc.Sync.ReplaceCloudWithLocalAsync()).Ok);

        var fullPullsBefore = tablet.Settings.GetQuotaSummary().FullPullsToday;
        var reload = await tablet.Sync.SyncAsync();
        Assert.IsTrue(reload.Success, reload.ErrorMessage);
        AssertSameAsTheCloud(tablet);
        Assert.AreEqual(1, Backups(tablet, "antes-de-bajar-la-nube-reemplazada").Length, "Respalda antes de vaciar");
        Assert.AreEqual(fullPullsBefore + 1, tablet.Settings.GetQuotaSummary().FullPullsToday, "Una descarga completa");
        using (var db = tablet.Factory.CreateDbContext())
        {
            Assert.AreEqual(CloudMeta("generation"), DbMeta.Get(db, DbMeta.Generation));
            Assert.IsFalse(db.Patients.Any(p => p.SyncId == unsent.SyncId), "Lo que no subió no está en la nube reemplazada");
            Assert.IsFalse(db.Patients.Any(p => p.SyncId == afterBackup.SyncId));
        }
        Assert.AreEqual(1, tablet.Sync.SavedChangesCount, "Quedó guardado para volver a aplicarlo");
        var summary = await tablet.Sync.GetSavedChangesSummaryAsync();
        Assert.AreEqual(1, summary.FromThisDevice);
        StringAssert.Contains(summary.Lines.Single(), "Pacientes: 1");

        // Una sola vez: la siguiente sincronización ya es normal
        var again = await tablet.Sync.SyncAsync();
        Assert.IsTrue(again.Success, again.ErrorMessage);
        Assert.AreEqual(fullPullsBefore + 1, tablet.Settings.GetQuotaSummary().FullPullsToday);

        // «Volver a aplicar»: vuelve con marca nueva y sube
        var (ok, message) = await tablet.Sync.ReapplySavedChangesAsync();
        Assert.IsTrue(ok, message);
        Assert.AreEqual(0, tablet.Sync.SavedChangesCount);
        using (var db = tablet.Factory.CreateDbContext())
        {
            var back = db.Patients.AsNoTracking().Single(p => p.SyncId == unsent.SyncId);
            Assert.AreEqual("Paciente Sin Subir De La Tablet", back.FullName);
            Assert.IsGreaterThan(unsent.UpdatedAtUtc, back.UpdatedAtUtc, "Con marca nueva");
        }
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.AreEqual(1L, CloudPatients(unsent));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        using (var db = pc.Factory.CreateDbContext()) Assert.IsTrue(db.Patients.Any(p => p.SyncId == unsent.SyncId), "Llega a la PC");

        // Lo que la PC guardó de la nube (el paciente que la tablet sí había subido) también se puede volver a aplicar
        Assert.IsTrue((await pc.Sync.ReapplySavedChangesAsync()).Ok);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(1L, CloudPatients(afterBackup));
    }

    [TestMethod]
    public async Task AChunkFromTheOldGeneration_IsRejected_AndTheDeviceStops()
    {
        var pc = await CloudWithIdentityAsync();
        var patient = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente De Un Lote Atrasado", Age = 50 });

        // Otra PC reemplaza la nube justo cuando llega el lote: la cerca no deja escribir y la PC se detiene sin avanzar
        _server.BeforeExecuteAsync = sql =>
        {
            if (sql.Contains("INSERT INTO \"Patients\"", StringComparison.Ordinal))
                ExecuteCloud("UPDATE _tpvc_meta SET Value = 'otra-generacion' WHERE Key = 'generation';");
            return Task.CompletedTask;
        };
        var result = await pc.Sync.SyncAsync();
        _server.BeforeExecuteAsync = null;

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "La nube cambió mientras se sincronizaba");
        Assert.AreEqual(0L, CloudPatients(patient), "El lote atrasado no cayó en la nube reemplazada");

        // Mientras la otra PC no termina, esta espera y guarda lo que no subió
        var waiting = await pc.Sync.SyncAsync();
        Assert.IsFalse(waiting.Success);
        StringAssert.Contains(waiting.ErrorMessage, "Otra computadora está reemplazando la nube");
        Assert.IsTrue(pc.Sync.IsWaitingForCloudReplacement);
        Assert.AreEqual(1, pc.Sync.SavedChangesCount);
        Assert.AreEqual(0L, CloudPatients(patient));
    }

    [TestMethod]
    public async Task AReplacementCutHalfway_IsContinued_AndNobodyDownloadsTheIncompleteCloud()
    {
        var (pc, tablet, backup, _, _, _) = await CloudWithChangesAfterABackupAsync();
        long tabletPatients;
        using (var db = tablet.Factory.CreateDbContext()) tabletPatients = db.Patients.Count();
        await RestoreAsync(pc, backup);

        // Se corta al subir los pacientes
        _server.FailWhen = sql => sql.StartsWith("INSERT INTO \"Patients\"", StringComparison.Ordinal);
        var cut = await pc.Sync.ReplaceCloudWithLocalAsync();
        Assert.IsFalse(cut.Ok);
        StringAssert.Contains(cut.Message, "Se continúa en la siguiente sincronización");
        Assert.IsTrue(pc.Sync.IsCloudReplacementPending);
        Assert.AreNotEqual(CloudMeta("generation"), CloudMeta("generationReady"), "La nube incompleta no está lista");

        // La tablet no baja la nube incompleta ni escribe en ella; un equipo nuevo no se puede unir
        var waiting = await tablet.Sync.SyncAsync();
        Assert.IsFalse(waiting.Success);
        StringAssert.Contains(waiting.ErrorMessage, "reemplazando la nube");
        using (var db = tablet.Factory.CreateDbContext()) Assert.AreEqual(tabletPatients, db.Patients.Count(), "Conserva lo suyo mientras espera");
        var newcomer = CreateDevice("laptop", seed: false);
        Assert.IsFalse((await newcomer.Sync.JoinCloudAsync()).Ok);

        // La siguiente sincronización de la PC lo termina (sin volver a guardar lo perdido)
        _server.FailWhen = null;
        var resumed = await pc.Sync.SyncAsync();
        Assert.IsTrue(resumed.Success, resumed.ErrorMessage);
        Assert.IsFalse(pc.Sync.IsCloudReplacementPending);
        Assert.IsFalse(pc.Sync.IsRestoreDecisionPending);
        AssertSameAsTheCloud(pc);
        Assert.AreEqual(CloudMeta("generation"), CloudMeta("generationReady"));
        Assert.AreEqual(1, Directory.GetFiles(new BackupService(pc.DbPath).BackupDirectory, "nube-antes-de-reemplazar-*.db").Length);

        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        AssertSameAsTheCloud(tablet);
    }

    [TestMethod]
    public async Task DisconnectingAfterARestore_LeavesTheCloudAsItWas()
    {
        var (pc, _, backup, afterBackup, edited, _) = await CloudWithChangesAfterABackupAsync();
        await RestoreAsync(pc, backup);

        var (ok, message) = await pc.Sync.DisconnectFromCloudAsync();
        Assert.IsTrue(ok, message);
        Assert.IsFalse(pc.Sync.IsRestoreDecisionPending);
        Assert.AreEqual(string.Empty, pc.Settings.CurrentSettings.DatabaseUrl);
        Assert.IsFalse(pc.Settings.CurrentSettings.IsSyncEnabled);
        Assert.AreEqual(1L, CloudPatients(afterBackup), "La nube y los demás equipos siguen como estaban");
        Assert.AreEqual("999.5", CloudPrice(edited));
        using var db = pc.Factory.CreateDbContext();
        Assert.IsNull(DbMeta.Get(db, DbMeta.ClinicId));
        Assert.IsNull(DbMeta.Get(db, DbMeta.Generation));
    }

    [TestMethod]
    public async Task ADeviceWithoutAGeneration_AdoptsTheCloudOne_UnlessTheCloudWasReplaced()
    {
        var pc = await CloudWithIdentityAsync();
        var generation = CloudMeta("generation");

        // Como al actualizar a esta versión: la base no tiene generación guardada
        using (var db = pc.Factory.CreateDbContext()) DbMeta.Remove(db, DbMeta.Generation);
        await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Al Actualizar", Age = 20 });
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        using (var db = pc.Factory.CreateDbContext()) Assert.AreEqual(generation, DbMeta.Get(db, DbMeta.Generation));

        // Si la nube ya se reemplazó alguna vez, una base sin generación no sabe si lo suyo es de antes: vuelve a bajar
        ExecuteCloud("INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('generationPrevious', 'una-anterior');");
        using (var db = pc.Factory.CreateDbContext()) DbMeta.Remove(db, DbMeta.Generation);
        var reload = await pc.Sync.SyncAsync();
        Assert.IsTrue(reload.Success, reload.ErrorMessage);
        Assert.AreEqual(1, Backups(pc, "antes-de-bajar-la-nube-reemplazada").Length);
        AssertSameAsTheCloud(pc);
    }
}
