using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.15: anular los tokens (Turso no anula uno solo), el 401 que nunca borra nada, «Perdí este equipo» y la marca
/// _tpvc_meta.tokensRotatedAtUtc con que la lista pide el token nuevo a los equipos que no se han conectado.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private static void PasteToken(Device device, string token)
    {
        var settings = device.Settings.CurrentSettings;
        settings.AuthToken = token;
        device.Settings.SaveSettings(settings);
    }

    [TestMethod]
    public async Task AfterTheTokensAreInvalidated_ThePcStopsWithoutLosingAnything_AndGoesOnWithTheNewOne()
    {
        var pc = await CloudWithIdentityAsync();
        var laptop = CreateDevice("laptop", seed: false);
        Assert.IsTrue((await laptop.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);

        // turso db tokens invalidate: solo sirve el nuevo
        _server.AcceptedTokens = new HashSet<string> { "token-nuevo" };
        var patient = await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Escrito Con El Token Anulado", Age = 40 });
        var rejected = await pc.Sync.SyncAsync();
        Assert.IsFalse(rejected.Success);
        StringAssert.Contains(rejected.ErrorMessage, "401");
        Assert.AreEqual(TursoSyncPauseReason.TokenRejected, pc.Sync.PauseReason);
        Assert.AreEqual(TursoSyncStatus.Paused, pc.Sync.Status);
        StringAssert.Contains(pc.Sync.TokenRejectedDetail, "No se borra nada");
        Assert.IsTrue(pc.Settings.IsTokenRotationExpected, "El token que funcionaba dejó de servir: se anularon");

        // Lo automático ya no llama a Turso; lo pendiente se queda
        var requests = _server.RequestCount;
        Assert.IsFalse((await pc.Sync.SyncAsync(isBackgroundAutoSync: true)).Success);
        Assert.AreEqual(requests, _server.RequestCount, "Sin token nuevo no vuelve a intentar sola");
        Assert.IsGreaterThan(0, (await pc.Sync.GetPendingChangesAsync()).Count);
        using (var db = pc.Factory.CreateDbContext()) Assert.IsTrue(db.Patients.Any(p => p.SyncId == patient.SyncId), "Un 401 no borra nada");

        // Con el token nuevo sigue sola y sube lo pendiente
        PasteToken(pc, "token-nuevo");
        Assert.AreEqual(TursoSyncPauseReason.None, pc.Sync.PauseReason);
        var resumed = await pc.Sync.SyncAsync();
        Assert.IsTrue(resumed.Success, resumed.ErrorMessage);
        Assert.AreEqual(1L, CloudPatients(patient));
        Assert.AreEqual(0, (await pc.Sync.GetPendingChangesAsync()).Count);
        var rotatedAt = CloudMeta(TursoSyncService.TokensRotatedKey);
        Assert.IsNotNull(rotatedAt, "El primer token nuevo que funciona anota la rotación");
        Assert.IsFalse(pc.Settings.IsTokenRotationExpected);

        // La laptop no se ha conectado desde entonces: la lista le pide el token nuevo
        var laptopRow = (await pc.Sync.GetDevicesAsync()).Single(d => d.DeviceId == "device-laptop");
        Assert.IsTrue(laptopRow.NeedsNewToken);
        StringAssert.StartsWith(laptopRow.NewTokenText, "Pegue el token nuevo en esa computadora");
        Assert.IsFalse((await pc.Sync.GetDevicesAsync()).Single(d => d.IsThisDevice).NeedsNewToken);

        // La laptop: 401 sin borrar nada; con el token nuevo sigue, no mueve la marca y deja de pedirlo
        var laptopPatient = await laptop.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente De La Laptop Sin Token", Age = 25 });
        Assert.IsFalse((await laptop.Sync.SyncAsync()).Success);
        Assert.AreEqual(TursoSyncPauseReason.TokenRejected, laptop.Sync.PauseReason);
        PasteToken(laptop, "token-nuevo");
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.AreEqual(1L, CloudPatients(laptopPatient), "Lo que la laptop escribió sin token sube con el nuevo");
        Assert.AreEqual(rotatedAt, CloudMeta(TursoSyncService.TokensRotatedKey), "La misma rotación no se anota dos veces");
        Assert.IsFalse((await pc.Sync.GetDevicesAsync()).Single(d => d.DeviceId == "device-laptop").NeedsNewToken);
    }

    [TestMethod]
    public async Task AWrongTokenThatNeverWorked_StopsTheSync_ButIsNotARotation()
    {
        var pc = await CloudWithIdentityAsync();
        _server.AcceptedTokens = new HashSet<string> { "test-token" };

        // Pegado de otra base (o mal copiado): Turso lo rechaza, pero los tokens no se anularon
        PasteToken(pc, "token-de-otra-base");
        Assert.IsFalse((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(TursoSyncPauseReason.TokenRejected, pc.Sync.PauseReason);
        Assert.IsFalse(pc.Settings.IsTokenRotationExpected);

        PasteToken(pc, "test-token");
        Assert.AreEqual(TursoSyncPauseReason.None, pc.Sync.PauseReason, "Con otro token, la pausa se quita sola");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsNull(CloudMeta(TursoSyncService.TokensRotatedKey));
        Assert.IsNull(pc.Settings.CurrentSettings.RejectedTokenHash);
    }

    [TestMethod]
    public async Task ReportingALostDevice_RevokesIt_AndTheFirstNewTokenRecordsTheRotation()
    {
        var pc = await CloudWithIdentityAsync();
        AndroidHeartbeat("android-tab-3f9a2c", "Galaxy Tab S9", DeviceRole.AndroidTablet);
        var tablet = (await pc.Sync.GetDevicesAsync()).Single(d => d.DeviceId == "android-tab-3f9a2c");
        Assert.IsFalse(tablet.CanReportLost, "Uno que espera la aprobación se rechaza");
        Assert.IsTrue((await pc.Sync.ApproveDeviceAsync(tablet, DeviceRole.AndroidTablet)).Ok);
        tablet = (await pc.Sync.GetDevicesAsync()).Single(d => d.DeviceId == "android-tab-3f9a2c");
        Assert.IsTrue(tablet.CanReportLost);
        Assert.IsFalse((await pc.Sync.GetDevicesAsync()).Single(d => d.IsThisDevice).CanReportLost);

        var lost = await pc.Sync.ReportLostDeviceAsync(tablet);
        Assert.IsTrue(lost.Ok, lost.Message);
        Assert.AreEqual(TursoSyncService.RevokedLostReason, RegistryCell("android-tab-3f9a2c", "RevokedReason"));
        Assert.AreEqual("T1", RegistryCell("android-tab-3f9a2c", "DeviceCode"));
        Assert.IsTrue(pc.Settings.IsTokenRotationExpected);
        Assert.IsFalse((await pc.Sync.GetDevicesAsync()).Any(d => d.DeviceId == "android-tab-3f9a2c"), "Sale de la lista");

        // Mientras no se anulen los tokens, esta PC sigue con el mismo y no anota nada
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsNull(CloudMeta(TursoSyncService.TokensRotatedKey));
        Assert.IsTrue(pc.Settings.IsTokenRotationExpected);

        // El médico anula los tokens y pega el nuevo antes de que esta PC vea un 401
        _server.AcceptedTokens = new HashSet<string> { "token-nuevo" };
        PasteToken(pc, "token-nuevo");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsNotNull(CloudMeta(TursoSyncService.TokensRotatedKey));
        Assert.IsFalse(pc.Settings.IsTokenRotationExpected);
    }

    private sealed class RecordingUrlLauncher : IUrlLauncher
    {
        public List<string> Opened { get; } = new();
        public void Open(string url) => Opened.Add(url);
    }

    [TestMethod]
    public async Task TheCutAccessSteps_StayUntilTheNewTokenSyncs()
    {
        var pc = await CloudWithIdentityAsync();
        var laptop = CreateDevice("laptop", seed: false);
        Assert.IsTrue((await laptop.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);

        var urls = new RecordingUrlLauncher();
        var vm = new TursoSyncViewModel(pc.Sync, pc.Settings) { Urls = urls, RefreshDevicesOnOpen = false };
        await vm.RefreshDevicesAsync();
        Assert.AreEqual(string.Empty, vm.CutAccessGuideText);

        await vm.ReportLostDeviceAsync(vm.Devices.Single(d => d.DeviceId == "device-laptop"));
        StringAssert.Contains(vm.CutAccessGuideText, "1. Anule los tokens de la base (tpvc-test.turso.io)");
        StringAssert.Contains(vm.CutAccessGuideText, "el equipo perdido conserva el acceso");
        vm.OpenTursoDashboard();
        CollectionAssert.AreEqual(new[] { TursoSyncViewModel.TursoDashboardUrl }, urls.Opened);

        // Se anularon: el primer paso ya está hecho
        _server.AcceptedTokens = new HashSet<string> { "token-nuevo" };
        await vm.SyncNowAsync();
        StringAssert.StartsWith(vm.PauseNoticeText, "Turso rechazó el token de esta computadora el ");
        StringAssert.Contains(vm.CutAccessGuideText, "1. ✓ Los tokens ya se anularon");

        // Con el token nuevo, los pasos se cierran solos
        vm.AuthToken = "token-nuevo";
        await vm.SyncNowAsync();
        Assert.AreEqual(string.Empty, vm.PauseNoticeText);
        Assert.AreEqual(string.Empty, vm.CutAccessGuideText);
        Assert.IsNotNull(CloudMeta(TursoSyncService.TokensRotatedKey));
    }
}
