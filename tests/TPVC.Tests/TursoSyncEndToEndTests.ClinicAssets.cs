using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.12 del plan de la Fase 8: el membrete en la nube. La PC sube cada imagen una sola vez, por su hash y en trozos;
/// los demás equipos la bajan solo cuando cambia, y una sincronización normal no lee ClinicAssets.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private static readonly byte[] CrestPng = PrescriptionSheet.WhatsAppIconPng;
    private static readonly byte[] OtherCrestPng = PrescriptionSheet.LocationIconPng;

    private string? CloudProfileHash(string column) =>
        CloudScalar($"SELECT {column} FROM ClinicProfiles WHERE Id = 1") is { } value and not DBNull ? value.ToString() : null;

    private long CloudAssets() => (long)CloudScalar("SELECT COUNT(*) FROM ClinicAssets")!;

    private long CloudChunks() => (long)CloudScalar("SELECT COUNT(*) FROM ClinicAssetChunks")!;

    /// <summary>Instrucciones que tocaron las tablas del membrete desde la última vez que se limpió la grabación.</summary>
    private int AssetStatements() =>
        _server.Requests.SelectMany(r => r.Statements).Count(s => s.Sql.Contains("ClinicAsset", StringComparison.Ordinal));

    private static byte[]? CrestOf(Device device) =>
        device.Profile.CurrentProfile.CrestImagePath is { } path ? File.ReadAllBytes(path) : null;

    /// <summary>Un PNG grande (la firma de PNG y bytes al azar): lo que se revisa al guardarlo es la firma y el tamaño.</summary>
    private static byte[] BigPng(int size)
    {
        var bytes = new byte[size];
        new Random(812).NextBytes(bytes);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    [TestMethod]
    public async Task ChangingTheCrest_UploadsItOnce_AndTheOthersDownloadItOnlyWhenItChanges()
    {
        var pc = CreateDevice("pc", seed: true);
        pc.Profile.SaveProfile(new ClinicProfile { DoctorName = "Dra. Prueba", ClinicName = "Consultorio" });
        pc.Profile.SaveLetterheadImage(LetterheadImageKind.Crest, CrestPng);
        _server.ClearRecording();
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        var crestHash = ClinicProfileService.HashImage(CrestPng);
        Assert.AreEqual(crestHash, CloudProfileHash("CrestImageHash"));
        Assert.IsNull(CloudProfileHash("HeaderImageHash"), "Sin imagen del encabezado ni elegida aquí: la nube no sabe de ninguna");
        Assert.AreEqual(1L, CloudAssets());
        Assert.AreEqual(1L, CloudChunks(), "Una imagen chica es un solo trozo");
        Assert.AreEqual(1, _server.Requests.SelectMany(r => r.Statements).Count(s => s.Sql.StartsWith("INSERT INTO ClinicAssetChunks", StringComparison.Ordinal)));

        // Una sola vez: la siguiente sincronización, aunque cambie otro dato del perfil, no la vuelve a subir
        _server.ClearRecording();
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(0, AssetStatements());
        var profile = pc.Profile.CurrentProfile;
        profile.Phone = "55 1234 5678";
        pc.Profile.SaveProfile(profile);
        _server.ClearRecording();
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsFalse(_server.Requests.SelectMany(r => r.Statements).Any(s => s.Sql.StartsWith("INSERT INTO ClinicAssetChunks", StringComparison.Ordinal)),
            "Ya estaba completa: solo se pregunta si existe");

        // Otro equipo la baja al sincronizar, y después ya no lee ClinicAssets
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        CollectionAssert.AreEqual(CrestPng, CrestOf(tablet));
        Assert.AreEqual(crestHash, tablet.Profile.CurrentProfile.CrestImageHash);
        Assert.IsFalse(tablet.Profile.CurrentProfile.CrestImageEditedHere, "La bajó; no se eligió en la tablet");
        Assert.AreEqual("Dra. Prueba", tablet.Profile.CurrentProfile.DoctorName);
        _server.ClearRecording();
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.AreEqual(0, AssetStatements(), "Las bajadas normales no leen ClinicAssets");

        // Otro escudo: sube el nuevo, la nube olvida el anterior y el otro equipo baja el nuevo
        pc.Profile.SaveLetterheadImage(LetterheadImageKind.Crest, OtherCrestPng);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(ClinicProfileService.HashImage(OtherCrestPng), CloudProfileHash("CrestImageHash"));
        Assert.AreEqual(1L, CloudAssets(), "El escudo anterior se borró de la nube");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        CollectionAssert.AreEqual(OtherCrestPng, CrestOf(tablet));

        // Quitarlo también viaja
        pc.Profile.ClearLetterheadImage(LetterheadImageKind.Crest);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(string.Empty, CloudProfileHash("CrestImageHash"));
        Assert.AreEqual(0L, CloudAssets());
        Assert.AreEqual(0L, CloudChunks());
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsNull(tablet.Profile.CurrentProfile.CrestImagePath);
        Assert.AreEqual(string.Empty, tablet.Profile.CurrentProfile.CrestImageHash);
    }

    [TestMethod]
    public async Task APcThatJoins_ReceivesTheImages_AndAFreshProfileDoesNotRemoveThem()
    {
        var pc = CreateDevice("pc", seed: true);
        pc.Profile.SaveProfile(new ClinicProfile { DoctorName = "Dra. Prueba" });
        pc.Profile.SaveLetterheadImage(LetterheadImageKind.Crest, CrestPng);
        pc.Profile.SaveLetterheadImage(LetterheadImageKind.Header, OtherCrestPng);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);

        // Una PC nueva que llenó su asistente (su perfil es más nuevo) y no eligió imágenes
        var laptop = CreateDevice("laptop", seed: false);
        await Task.Delay(20);
        laptop.Profile.SaveProfile(new ClinicProfile { DoctorName = "Dra. Prueba", Phone = "55 0000 0000" });
        Assert.IsTrue((await laptop.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);

        Assert.AreEqual("55 0000 0000", (string?)CloudScalar("SELECT Phone FROM ClinicProfiles WHERE Id = 1"), "Su perfil ganó por ser más nuevo");
        Assert.AreEqual(ClinicProfileService.HashImage(CrestPng), CloudProfileHash("CrestImageHash"), "Pero no le quitó el escudo a la nube");
        Assert.AreEqual(ClinicProfileService.HashImage(OtherCrestPng), CloudProfileHash("HeaderImageHash"));
        CollectionAssert.AreEqual(CrestPng, CrestOf(laptop), "La PC que se unió recibió el escudo");
        CollectionAssert.AreEqual(OtherCrestPng, File.ReadAllBytes(laptop.Profile.CurrentProfile.HeaderImagePath!));
    }

    [TestMethod]
    public async Task ACloudFromV128_GetsTheHashColumns_AndThePcPublishesItsImages()
    {
        // El perfil que dejó una PC v1.28 (sin columnas de hash), igual al de esta PC
        var pc = CreateDevice("pc", seed: true);
        pc.Profile.SaveLetterheadImage(LetterheadImageKind.Crest, CrestPng);
        pc.Profile.SaveProfile(new ClinicProfile { DoctorName = "Dra. Prueba" });
        var stamp = TursoSyncService.FormatDate(pc.Profile.CurrentProfile.UpdatedAtUtc!.Value);
        ExecuteCloud("CREATE TABLE ClinicProfiles (Id INTEGER PRIMARY KEY, DoctorName TEXT, ProfessionalLicense TEXT, Specialty TEXT, University TEXT, " +
                     "ClinicName TEXT, Address TEXT, Phone TEXT, Email TEXT, TaxId TEXT, AdditionalNotes TEXT, UpdatedAtUtc TEXT NOT NULL); " +
                     $"INSERT INTO ClinicProfiles (Id, DoctorName, UpdatedAtUtc) VALUES (1, 'Dra. Prueba', '{stamp}');");

        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(ClinicProfileService.HashImage(CrestPng), CloudProfileHash("CrestImageHash"), "La nube no sabía de la imagen: la PC la sube y la anota");
        Assert.IsNull(CloudProfileHash("HeaderImageHash"));
        Assert.AreEqual(1L, CloudAssets());

        // El upsert de una PC v1.28 (sus columnas explícitas) no toca los hashes
        ExecuteCloud("INSERT INTO ClinicProfiles (Id, DoctorName, UpdatedAtUtc) VALUES (1, 'Dra. Vieja', '2099-01-01 00:00:00.0000000') " +
                     "ON CONFLICT(Id) DO UPDATE SET DoctorName = excluded.DoctorName, UpdatedAtUtc = excluded.UpdatedAtUtc;");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual("Dra. Vieja", pc.Profile.CurrentProfile.DoctorName);
        CollectionAssert.AreEqual(CrestPng, CrestOf(pc), "La imagen sigue");
    }

    [TestMethod]
    public async Task ABigImage_TravelsInChunks_AndOneThatArrivesBroken_IsNotUsed()
    {
        var pc = CreateDevice("pc", seed: true);
        var big = BigPng(1_200_000);
        pc.Profile.SaveProfile(new ClinicProfile { DoctorName = "Dra. Prueba" });
        pc.Profile.SaveLetterheadImage(LetterheadImageKind.Header, big);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(3L, CloudChunks(), "1.2 MB en trozos de 512 KiB");
        Assert.AreEqual(1_200_000L, (long)CloudScalar("SELECT ByteCount FROM ClinicAssets")!);

        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        CollectionAssert.AreEqual(big, File.ReadAllBytes(tablet.Profile.CurrentProfile.HeaderImagePath!));

        // Un escudo nuevo cuyo trozo llega dañado: la tablet no lo usa y no lo vuelve a bajar en esta sesión
        pc.Profile.SaveLetterheadImage(LetterheadImageKind.Crest, CrestPng);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        ExecuteCloud("UPDATE ClinicAssetChunks SET Content = X'00' WHERE Hash = '" + ClinicProfileService.HashImage(CrestPng) + "';");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success, "Una imagen dañada no detiene la sincronización");
        Assert.IsNull(tablet.Profile.CurrentProfile.CrestImagePath);
        _server.ClearRecording();
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.AreEqual(0, AssetStatements(), "No se reintenta en la misma sesión");
    }
}
