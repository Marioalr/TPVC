using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.LabFiles;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.12b del plan de la Fase 8: los resultados de laboratorio. La fila de LabResults viaja como cualquier tabla; el
/// archivo sube en trozos de 512 KiB a LabBlobs y LabBlobChunks, cada PC lo guarda en su archivo, la nube tiene un tope y
/// la PC principal desaloja lo más viejo solo si lo tiene en su archivo y en el respaldo externo.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    /// <summary>Un "PDF" de prueba: la firma de PDF y bytes al azar (lo que se revisa es la firma, el tamaño y el hash).</summary>
    private static byte[] FakePdf(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        "%PDF-1.4\n"u8.CopyTo(bytes);
        return bytes;
    }

    private static PreparedLabFile Pdf(byte[] content) => new(content, LabResult.PdfMimeType, 1, "resultado.pdf");

    private static async Task<LabResult> AddResultAsync(Device device, int patientId, string title, byte[] content, DateTime? date = null) =>
        await device.Labs.AddAsync(patientId, new LabResultDraft(title, "Laboratorio Central", date ?? new DateTime(2026, 10, 1), string.Empty), Pdf(content));

    private object? LabBlob(string sha, string column) =>
        CloudScalar($"SELECT {column} FROM LabBlobs WHERE Sha256 = '{sha}'") is { } value and not DBNull ? value : null;

    private long CloudLabChunks(string sha) => (long)CloudScalar($"SELECT COUNT(*) FROM LabBlobChunks WHERE Sha256 = '{sha}'")!;

    private int LabChunkInserts() =>
        _server.Requests.SelectMany(r => r.Statements).Count(s => s.Sql.StartsWith("INSERT INTO LabBlobChunks", StringComparison.Ordinal));

    private int LabStatements() =>
        _server.Requests.SelectMany(r => r.Statements).Count(s => s.Sql.Contains("LabBlob", StringComparison.Ordinal));

    private static async Task<int> PatientIdOnAsync(Device device, Guid syncId) =>
        (await device.Inventory.GetPatientsAsync()).Single(p => p.SyncId == syncId).Id;

    [TestMethod]
    public async Task AResult_TravelsAsMetadata_ItsFileUploadsInChunks_AndAnotherPcArchivesIt()
    {
        var pc = CreateDevice("pc", seed: true);
        var patient = (await pc.Inventory.GetPatientsAsync()).First();
        var pdf = FakePdf(1_200_000, 1);
        var added = await AddResultAsync(pc, patient.Id, "Biometría hemática", pdf);
        Assert.IsTrue(pc.Labs.HasLocalFile(added), "Se guarda en el archivo de la PC antes que nada");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual("Biometría hemática", (string?)CloudScalar($"SELECT Title FROM LabResults WHERE ContentSha256 = '{added.ContentSha256}'"),
            "La fila viaja con la sincronización");
        Assert.AreEqual(0L, (long)CloudScalar("SELECT COUNT(*) FROM LabBlobs")!, "El archivo no: va en la vuelta de los archivos");

        _server.ClearRecording();
        var report = await pc.Sync.RunLabTransfersAsync();
        Assert.AreEqual(1, report.Uploaded);
        Assert.IsNull(report.Notice);
        Assert.AreEqual(3L, CloudLabChunks(added.ContentSha256), "1.2 MB en trozos de 512 KiB");
        Assert.AreEqual(3, LabChunkInserts());
        Assert.IsNotNull(LabBlob(added.ContentSha256, "CompletedAtUtc"));
        Assert.AreEqual(1_200_000L, LabBlob(added.ContentSha256, "SizeBytes"));
        Assert.AreEqual("device-pc", LabBlob(added.ContentSha256, "OriginDeviceId"));
        Assert.IsTrue(_server.Requests.Any(r => r.Statements.Any(s => s.Sql.StartsWith("INSERT INTO LabBlobChunks", StringComparison.Ordinal))
                                               && r.Statements.Any(s => s.Sql.StartsWith("UPDATE LabBlobs SET CompletedAtUtc", StringComparison.Ordinal))),
            "El último trozo y el cierre van en la misma transacción (V23)");

        // Ya está: la siguiente vuelta no le pregunta nada a la nube
        _server.ClearRecording();
        Assert.IsFalse((await pc.Sync.RunLabTransfersAsync()).Ran);
        Assert.AreEqual(0, LabStatements());

        // Otra PC recibe la fila y baja el archivo a su archivo
        var laptop = CreateDevice("laptop", seed: false);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        var arrived = (await laptop.Labs.GetForPatientAsync(await PatientIdOnAsync(laptop, patient.SyncId))).Single();
        Assert.AreEqual(added.ContentSha256, arrived.ContentSha256);
        Assert.AreEqual(new DateTime(2026, 10, 1), arrived.ResultDate);
        Assert.IsFalse(laptop.Labs.HasLocalFile(arrived));

        report = await laptop.Sync.RunLabTransfersAsync();
        Assert.AreEqual(1, report.Downloaded);
        CollectionAssert.AreEqual(pdf, laptop.Labs.Store.Read(added.ContentSha256));
        _server.ClearRecording();
        Assert.IsFalse((await laptop.Sync.RunLabTransfersAsync()).Ran, "Lo que bajó de esta nube no se vuelve a subir");
    }

    [TestMethod]
    public async Task AnUploadCutInTheMiddle_ContinuesWithTheMissingChunks()
    {
        var pc = CreateDevice("pc", seed: true);
        var patient = (await pc.Inventory.GetPatientsAsync()).First();
        var added = await AddResultAsync(pc, patient.Id, "Química sanguínea", FakePdf(1_200_000, 2));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        int inserts = 0;
        _server.FailWhen = sql => sql.StartsWith("INSERT INTO LabBlobChunks", StringComparison.Ordinal) && ++inserts == 2;
        var report = await pc.Sync.RunLabTransfersAsync();
        Assert.AreEqual(0, report.Uploaded);
        Assert.IsNotNull(report.Notice, "Se sigue después");
        Assert.AreEqual(1L, CloudLabChunks(added.ContentSha256));
        Assert.IsNull(LabBlob(added.ContentSha256, "CompletedAtUtc"), "Sin todos sus trozos no se cierra");

        _server.FailWhen = null;
        _server.ClearRecording();
        report = await pc.Sync.RunLabTransfersAsync();
        Assert.AreEqual(1, report.Uploaded);
        Assert.AreEqual(2, LabChunkInserts(), "Solo los trozos que faltaban");
        Assert.AreEqual(3L, CloudLabChunks(added.ContentSha256));
        Assert.IsNotNull(LabBlob(added.ContentSha256, "CompletedAtUtc"));
    }

    [TestMethod]
    public async Task OpeningAResult_OnAnotherDevice_DownloadsIt_AndOneThatArrivesBrokenIsNotSaved()
    {
        var pc = CreateDevice("pc", seed: true);
        var patient = (await pc.Inventory.GetPatientsAsync()).First();
        var good = FakePdf(700_000, 3);
        var broken = FakePdf(600_000, 4);
        var first = await AddResultAsync(pc, patient.Id, "Examen general de orina", good);
        var second = await AddResultAsync(pc, patient.Id, "Perfil tiroideo", broken);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(2, (await pc.Sync.RunLabTransfersAsync()).Uploaded);
        ExecuteCloud($"UPDATE LabBlobChunks SET Data = X'00' WHERE Sha256 = '{second.ContentSha256}' AND ChunkIndex = 1;");
        var third = await AddResultAsync(pc, patient.Id, "Glucosa", FakePdf(1000, 5));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success); // la fila sube; el archivo, todavía no

        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var results = await tablet.Labs.GetForPatientAsync(await PatientIdOnAsync(tablet, patient.SyncId));
        Assert.HasCount(3, results);

        var opened = await tablet.Labs.GetContentAsync(results.Single(r => r.SyncId == first.SyncId));
        Assert.IsTrue(opened.Ok, opened.Message);
        CollectionAssert.AreEqual(good, opened.Content);
        Assert.IsTrue(tablet.Labs.Store.Contains(first.ContentSha256), "Queda en este equipo");

        var damaged = await tablet.Labs.GetContentAsync(results.Single(r => r.SyncId == second.SyncId));
        Assert.AreEqual(LabFetchStatus.Broken, damaged.Status);
        Assert.IsFalse(tablet.Labs.Store.Contains(second.ContentSha256), "Uno dañado no se guarda");

        var pending = await tablet.Labs.GetContentAsync(results.Single(r => r.SyncId == third.SyncId));
        Assert.AreEqual(LabFetchStatus.NotInCloud, pending.Status);
        StringAssert.Contains(pending.Message, "todavía no llega a la nube");
    }

    [TestMethod]
    public async Task TheCloudCap_KeepsNewFilesHere_UntilThePrimaryPcHasAnExternalBackup_ThenEvictsTheOldest()
    {
        var pc = CreateDevice("pc", seed: true);
        pc.Sync.LabCloudCapOverride = 3_000_000;
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        Assert.IsTrue(pc.Sync.Registration?.IsPrimary, "La primera PC con la nube verificada es la principal");

        var patient = (await pc.Inventory.GetPatientsAsync()).First();
        var january = await AddResultAsync(pc, patient.Id, "Enero", FakePdf(1_000_000, 11), new DateTime(2026, 1, 15));
        var february = await AddResultAsync(pc, patient.Id, "Febrero", FakePdf(1_000_000, 12), new DateTime(2026, 2, 15));
        var march = await AddResultAsync(pc, patient.Id, "Marzo", FakePdf(1_000_000, 13), new DateTime(2026, 3, 15));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var report = await pc.Sync.RunLabTransfersAsync();
        Assert.AreEqual(3, report.Uploaded, "3 MB caben justo");
        Assert.AreEqual(3_000_000L, pc.Sync.LabCloud?.Bytes);

        // Uno más no cabe y, sin respaldo externo, la principal no desaloja nada: se queda en la PC
        var april = await AddResultAsync(pc, patient.Id, "Abril", FakePdf(1_000_000, 14), new DateTime(2026, 4, 15));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        report = await pc.Sync.RunLabTransfersAsync();
        Assert.AreEqual(0, report.Uploaded);
        Assert.AreEqual(1, report.WaitingForCap);
        StringAssert.Contains(report.Notice, "no hay respaldo externo");
        Assert.IsNull(LabBlob(april.ContentSha256, "Sha256"), "Ni siquiera se registra");
        Assert.IsTrue(pc.Labs.HasLocalFile(april));

        // Con el respaldo externo: desaloja el más viejo (enero) y sube abril
        var backup = Path.Combine(Path.GetTempPath(), $"tpvc_labbackup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        _dirs.Add(backup);
        pc.Labs.BackupFolder = backup;
        Assert.AreEqual(4, (await pc.Labs.BackUpAsync(backup)).Copied);
        report = await pc.Sync.RunLabTransfersAsync();
        Assert.AreEqual(1, report.Evicted);
        Assert.AreEqual(1, report.Uploaded);
        Assert.IsNotNull(LabBlob(january.ContentSha256, "EvictedAtUtc"));
        Assert.AreEqual(0L, CloudLabChunks(january.ContentSha256), "Sus trozos se borraron de la nube");
        Assert.IsNotNull(LabBlob(april.ContentSha256, "CompletedAtUtc"));
        Assert.IsNull(LabBlob(february.ContentSha256, "EvictedAtUtc"));
        Assert.IsTrue(pc.Labs.HasLocalFile(january), "Sigue en el archivo de la PC");
        Assert.AreEqual(3_000_000L, pc.Sync.LabCloud?.Bytes);
        Assert.IsNotNull(LabBlob(march.ContentSha256, "ArchivedAtUtc"), "La principal anota lo que ya tiene en su archivo");

        // En otro equipo (se une a la nube, que ya tiene identidad), el de enero ya no se puede bajar
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var onTablet = (await tablet.Labs.GetForPatientAsync(await PatientIdOnAsync(tablet, patient.SyncId))).Single(r => r.SyncId == january.SyncId);
        var fetched = await tablet.Labs.GetContentAsync(onTablet);
        Assert.AreEqual(LabFetchStatus.Evicted, fetched.Status);
        StringAssert.Contains(fetched.Message, "PC principal");
    }

    [TestMethod]
    public async Task AFileNoResultNames_LeavesTheCloudAfter30Days_ButStaysInThePcArchive()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        var patient = (await pc.Inventory.GetPatientsAsync()).First();
        var added = await AddResultAsync(pc, patient.Id, "Por error", FakePdf(800_000, 21));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(1, (await pc.Sync.RunLabTransfersAsync()).Uploaded);

        await pc.Labs.DeleteAsync(added.Id);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(0L, (long)CloudScalar("SELECT COUNT(*) FROM LabResults")!, "La lápida borra la fila de la nube");

        var report = await pc.Sync.RunLabTransfersAsync(forceMaintenance: true);
        Assert.AreEqual(0, report.OrphansDeleted);
        Assert.IsNotNull(LabBlob(added.ContentSha256, "OrphanSinceUtc"), "Se anota que ningún resultado lo nombra");

        _server.Clock = () => DateTime.UtcNow.AddDays(31);
        report = await pc.Sync.RunLabTransfersAsync(forceMaintenance: true);
        Assert.AreEqual(1, report.OrphansDeleted);
        Assert.AreEqual(0L, (long)CloudScalar("SELECT COUNT(*) FROM LabBlobs")!);
        Assert.AreEqual(0L, CloudLabChunks(added.ContentSha256));
        Assert.IsTrue(pc.Labs.Store.Contains(added.ContentSha256), "El archivo de la PC no se toca");
    }

    [TestMethod]
    public async Task TheDailyByteBrake_StopsTheArchive_ButOpeningAResultStillWorks()
    {
        var pc = CreateDevice("pc", seed: true);
        var patient = (await pc.Inventory.GetPatientsAsync()).First();
        var first = await AddResultAsync(pc, patient.Id, "Uno", FakePdf(1_200_000, 31), new DateTime(2026, 5, 2));
        var second = await AddResultAsync(pc, patient.Id, "Dos", FakePdf(1_200_000, 32), new DateTime(2026, 5, 1));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(2, (await pc.Sync.RunLabTransfersAsync()).Uploaded);

        var laptop = CreateDevice("laptop", seed: false);
        laptop.Sync.LabBytesBrakeOverride = (10_000_000, 1_000_000);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        var report = await laptop.Sync.RunLabTransfersAsync();
        Assert.AreEqual(1, report.Downloaded, "El primero pasa el tope del día; el segundo espera a mañana");
        StringAssert.Contains(report.Notice, "tope de bajada");
        Assert.IsTrue(laptop.Labs.Store.Contains(first.ContentSha256), "Primero el más reciente");
        Assert.IsGreaterThanOrEqualTo(1_600_000L, laptop.Settings.GetLabBytesToday().Down, "Cuenta lo que viaja (base64)");

        var onLaptop = (await laptop.Labs.GetForPatientAsync(await PatientIdOnAsync(laptop, patient.SyncId))).Single(r => r.SyncId == second.SyncId);
        Assert.IsTrue((await laptop.Labs.GetContentAsync(onLaptop)).Ok, "Lo que abre el médico no se frena");
    }

    [TestMethod]
    public async Task ResultsOfAPatient_AreDeletedWithThePatient_AndTheirConsultationLinkIsCleared()
    {
        var pc = CreateDevice("pc", seed: true);
        var patient = (await pc.Inventory.GetPatientsAsync()).First();
        var consultation = await pc.Inventory.AddConsultationNoteAsync(patient.Id, new PatientConsultation { ChiefComplaint = "Control" });
        var added = await pc.Labs.AddAsync(patient.Id,
            new LabResultDraft("Hemoglobina glucosilada", string.Empty, new DateTime(2026, 9, 30), "HbA1c 6.1 %", consultation!.Id), Pdf(FakePdf(5000, 41)));

        using (var db = pc.Factory.CreateDbContext())
        {
            db.PatientConsultations.Remove(await db.PatientConsultations.SingleAsync(c => c.Id == consultation.Id));
            await db.SaveChangesAsync();
            Assert.IsNull((await db.LabResults.SingleAsync(r => r.Id == added.Id)).PatientConsultationId, "Borrar la consulta no borra el resultado");

            db.Patients.Remove(await db.Patients.SingleAsync(p => p.Id == patient.Id));
            await db.SaveChangesAsync();
            Assert.AreEqual(0, await db.LabResults.CountAsync(), "Borrar el expediente sí");
            Assert.IsTrue(await db.SyncTombstones.AnyAsync(t => t.EntityType == nameof(LabResult) && t.SyncId == added.SyncId),
                "Con su lápida, para que se borre en los demás equipos");
        }
    }
}
