using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Pairing;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.14 (decisión del médico del 2026-10-11): la tablet y el celular reciben la URL y el token y escriben su latido;
/// la PC los aprueba (fila de DeviceRegistry con código T o C) o los rechaza (fila ya revocada, código X). El equipo
/// Android se simula con su latido en la nube.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    /// <summary>Lo que escribe un equipo Android al leer el QR, antes de que lo aprueben.</summary>
    private void AndroidHeartbeat(string deviceId, string name, DeviceRole role) =>
        ExecuteCloud("INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc, Platform, SyncProtocol, HandoffProtocol, " +
                     "ContractRevision, PendingCount) " +
                     $"VALUES ('{deviceId}', '{name}', '{role}', '1.0.0', strftime('%Y-%m-%d %H:%M:%f','now'), 'Android', 3, 1, {SyncContract.Revision}, 0);");

    private string? RegistryCell(string deviceId, string column) =>
        CloudScalar($"SELECT {column} FROM DeviceRegistry WHERE DeviceId = '{deviceId}'") is { } value and not DBNull ? value.ToString() : null;

    [TestMethod]
    public async Task ANewAndroidDevice_WaitsUntilThePcApprovesIt_AsTabletOrPhone()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);

        AndroidHeartbeat("android-tab-3f9a2c", "Galaxy Tab S9", DeviceRole.AndroidTablet);
        AndroidHeartbeat("android-cel-77b01d", "Moto G", DeviceRole.AndroidMobileAdmin);
        var devices = await pc.Sync.GetDevicesAsync();
        var tablet = devices.Single(d => d.DeviceId == "android-tab-3f9a2c");
        Assert.IsTrue(tablet.IsPendingApproval, "Sin fila en DeviceRegistry: espera");
        Assert.IsFalse(tablet.CanRemove, "Se aprueba o se rechaza, no se quita");
        Assert.AreEqual("3F9A2C", tablet.DeviceIdSuffix);
        StringAssert.Contains(tablet.ApprovalText, "la tablet muestre 3F9A2C");

        var (ok, message) = await pc.Sync.ApproveDeviceAsync(tablet, DeviceRole.AndroidTablet);
        Assert.IsTrue(ok, message);
        StringAssert.Contains(message, "código T1");
        Assert.AreEqual("T1", RegistryCell("android-tab-3f9a2c", "DeviceCode"));
        Assert.AreEqual(nameof(DeviceRole.AndroidTablet), RegistryCell("android-tab-3f9a2c", "Role"));
        Assert.IsNull(RegistryCell("android-tab-3f9a2c", "RevokedAtUtc"));
        Assert.IsFalse((await pc.Sync.ApproveDeviceAsync(tablet, DeviceRole.AndroidTablet)).Ok, "Dos veces no cambia nada");

        // El médico decide el rol: el celular pidió entrar como celular, pero también podría quedar como tablet
        var phone = (await pc.Sync.GetDevicesAsync()).Single(d => d.DeviceId == "android-cel-77b01d");
        Assert.IsTrue((await pc.Sync.ApproveDeviceAsync(phone, DeviceRole.AndroidMobileAdmin)).Ok);
        Assert.AreEqual("C1", RegistryCell("android-cel-77b01d", "DeviceCode"));
        Assert.IsFalse((await pc.Sync.ApproveDeviceAsync(phone, DeviceRole.WindowsDesktop)).Ok, "Un equipo Android no se vincula como computadora");

        var listed = await pc.Sync.GetDevicesAsync();
        Assert.IsFalse(listed.Any(d => d.IsPendingApproval));
        Assert.AreEqual("Código T1", listed.Single(d => d.DeviceId == "android-tab-3f9a2c").CodeText);
    }

    [TestMethod]
    public async Task ARejectedDevice_GetsARevokedRow_WithAnXCode_AndLeavesTheList()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        AndroidHeartbeat("android-ajeno-000001", "Equipo desconocido", DeviceRole.AndroidTablet);
        var stranger = (await pc.Sync.GetDevicesAsync()).Single(d => d.DeviceId == "android-ajeno-000001");

        var (ok, message) = await pc.Sync.RejectDeviceAsync(stranger);
        Assert.IsTrue(ok, message);
        StringAssert.Contains(message, "anule los tokens");
        Assert.AreEqual("X1", RegistryCell("android-ajeno-000001", "DeviceCode"));
        Assert.AreEqual(TursoSyncService.RevokedRejectedReason, RegistryCell("android-ajeno-000001", "RevokedReason"));
        Assert.IsNotNull(RegistryCell("android-ajeno-000001", "RevokedAtUtc"));
        Assert.IsFalse((await pc.Sync.GetDevicesAsync()).Any(d => d.DeviceId == "android-ajeno-000001"), "Sale de la lista");
        Assert.IsFalse((await pc.Sync.ApproveDeviceAsync(stranger, DeviceRole.AndroidTablet)).Ok, "Rechazado no se aprueba después");

        // Un rechazo no gasta los códigos de los equipos vinculados
        AndroidHeartbeat("android-tab-000002", "Galaxy Tab", DeviceRole.AndroidTablet);
        var tablet = (await pc.Sync.GetDevicesAsync()).Single(d => d.DeviceId == "android-tab-000002");
        Assert.IsTrue((await pc.Sync.ApproveDeviceAsync(tablet, DeviceRole.AndroidTablet)).Ok);
        Assert.AreEqual("T1", RegistryCell("android-tab-000002", "DeviceCode"));
    }

    [TestMethod]
    public async Task TheLinkQr_NeedsAVerifiedCloud_AndCarriesTheUrlAndToken()
    {
        var pc = CreateDevice("pc", seed: true);
        var (none, problem) = await pc.Sync.GetLinkDataAsync();
        Assert.IsNull(none);
        StringAssert.Contains(problem, "Sincronización completa");

        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);
        pc.Profile.SaveProfile(new ClinicProfile { ClinicName = "Consultorio Médico Peña", DoctorName = "Dra. Prueba" });
        var (data, _) = await pc.Sync.GetLinkDataAsync();
        Assert.AreEqual(new DeviceLinkData(Url, "test-token", "Consultorio Médico Peña"), data);
        var png = DeviceLinkCode.QrPng(DeviceLinkCode.Encode(data!));
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray(), "Un PNG");
    }

    [TestMethod]
    [TestCategory("Windows")]
    public async Task TheLinkQrOnScreen_ClosesItselfAfterTwoMinutes()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.InitialUploadToTursoAsync()).Success);

        // La pantalla (WPF, hilo STA): el QR se cierra solo a los 2 minutos
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new TursoSyncViewModel(pc.Sync, pc.Settings) { LinkClock = clock };
                vm.ShowLinkQrAsync().GetAwaiter().GetResult();
                Assert.IsTrue(vm.IsLinkQrVisible);
                Assert.IsNotNull(vm.LinkQrImage);
                Assert.AreEqual("Se cierra solo en 2:00", vm.LinkQrCountdownText);
                clock.Advance(TimeSpan.FromSeconds(75));
                vm.TickLinkQr();
                Assert.AreEqual("Se cierra solo en 0:45", vm.LinkQrCountdownText);
                clock.Advance(TimeSpan.FromSeconds(45));
                vm.TickLinkQr();
                Assert.IsFalse(vm.IsLinkQrVisible);
                Assert.IsNull(vm.LinkQrImage, "La imagen con el token no se queda");
                StringAssert.Contains(vm.LinkStatusText, "se cerró solo");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }
}
