using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.10 del plan de la Fase 8: registro de equipos (DeviceRegistry), la PC principal, la compatibilidad de la
/// consulta entre equipos por HandoffProtocol, el latido con sus columnas nuevas y la detección de un DeviceId repetido.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private const string Now = "strftime('%Y-%m-%d %H:%M:%f','now')";

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    /// <summary>El valor de una columna del latido en la nube; null si es NULL o no hay fila.</summary>
    private string? CloudDeviceValue(string column, string deviceId) =>
        CloudScalar($"SELECT {column} FROM RegisteredDevices WHERE DeviceId = '{deviceId}'") is { } value and not DBNull ? value.ToString() : null;

    private string? RegistryValue(string column, string deviceId) =>
        CloudScalar($"SELECT {column} FROM DeviceRegistry WHERE DeviceId = '{deviceId}'") is { } value and not DBNull ? value.ToString() : null;

    private ConsultationHandoffService HandoffOf(Device device) => new(device.Sync, device.Settings) { AutoPoll = false };

    [TestMethod]
    public void HandoffCompatibility_UsesHandoffProtocol_AndOldRowsKeepTheVersionRule()
    {
        var now = DateTime.UtcNow;
        var seen = TursoSyncService.FormatDate(now.AddSeconds(-10));

        // La app de Kotlin numera sus versiones desde 1.0.0: cuenta su HandoffProtocol, no su AppVersion
        var kotlin = Row(("DeviceId", "tab-1"), ("DeviceName", "Galaxy Tab S9"), ("DeviceRole", "AndroidTablet"), ("AppVersion", "0.3.0"),
            ("LastSeenUtc", seen), ("Platform", "Android"), ("HandoffProtocol", 1L));
        var registered = Row(("DeviceId", "tab-1"), ("DeviceCode", "T1"), ("Role", "AndroidTablet"), ("DisplayName", "Tablet del consultorio"));
        var tablet = ConsultationHandoffService.ToDevice(kotlin, now, registered);
        Assert.IsTrue(tablet.IsAvailable, tablet.StatusText);
        Assert.AreEqual(DeviceRole.AndroidTablet, tablet.Role);
        Assert.AreEqual("T1", tablet.DeviceCode);
        Assert.AreEqual("Tablet del consultorio", tablet.DeviceName, "El nombre que le puso la PC al vincularla");
        Assert.AreEqual("En línea", tablet.SeenText);

        // Sin fila en DeviceRegistry, un Android espera que la PC lo vincule
        var pending = ConsultationHandoffService.ToDevice(kotlin, now);
        Assert.IsTrue(pending.IsPendingApproval);
        Assert.IsFalse(pending.IsAvailable);

        // Quitado
        registered["RevokedAtUtc"] = seen;
        var revoked = ConsultationHandoffService.ToDevice(kotlin, now, registered);
        Assert.IsTrue(revoked.IsRevoked);
        Assert.IsFalse(revoked.IsAvailable);

        // Un latido de una PC v1.17–v1.26 (sin las columnas nuevas): la regla de AppVersion de siempre
        var oldPc = Row(("DeviceId", "pc-2"), ("DeviceName", "Recepción"), ("DeviceRole", "WindowsDesktop"), ("AppVersion", "1.19.0"), ("LastSeenUtc", seen));
        Assert.IsTrue(ConsultationHandoffService.ToDevice(oldPc, now).IsAvailable);
        oldPc["AppVersion"] = "1.16.0";
        var tooOld = ConsultationHandoffService.ToDevice(oldPc, now);
        Assert.IsFalse(tooOld.IsAvailable);
        StringAssert.Contains(tooOld.StatusText, "Necesita TPVC 1.17.0");

        // Con HandoffProtocol 0 no recibe aunque su versión sea alta
        var noHandoff = Row(("DeviceId", "pc-3"), ("DeviceName", "Otra"), ("DeviceRole", "WindowsDesktop"), ("AppVersion", "9.0.0"), ("LastSeenUtc", seen),
            ("HandoffProtocol", 0L));
        Assert.IsFalse(ConsultationHandoffService.ToDevice(noHandoff, now).IsAvailable);
    }

    [TestMethod]
    public async Task TheFirstPc_ClaimsThePrimaryAsP1_AnotherGetsP2_AndCanReplaceIt()
    {
        var pc = await CloudWithIdentityAsync();
        Assert.AreEqual("device-pc", CloudMeta(TursoSyncService.PrimaryDeviceKey), "La primera PC con la nube verificada es la principal");
        Assert.AreEqual("P1", RegistryValue("DeviceCode", "device-pc"));
        Assert.AreEqual("WindowsDesktop", RegistryValue("Role", "device-pc"));
        Assert.IsTrue(pc.Sync.Registration!.IsPrimary);
        Assert.AreEqual("P1", pc.Settings.CurrentSettings.DeviceCode, "El código queda también en la configuración");

        // Una PC nueva que recupera la nube recibe P2 y no le quita la principal a nadie
        var laptop = CreateDevice("laptop", seed: false);
        Assert.IsTrue((await laptop.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.AreEqual("P2", laptop.Sync.Registration!.DeviceCode);
        Assert.IsFalse(laptop.Sync.Registration.IsPrimary);
        Assert.AreEqual("device-pc", laptop.Sync.Registration.PrimaryDeviceId);
        Assert.AreEqual("device-pc", CloudMeta(TursoSyncService.PrimaryDeviceKey));

        // La lista desde la laptop: las dos, la principal marcada
        var devices = await laptop.Sync.GetDevicesAsync();
        Assert.HasCount(2, devices);
        Assert.IsTrue(devices[0].IsThisDevice);
        Assert.IsTrue(devices.Single(d => d.DeviceId == "device-pc").IsPrimary);
        Assert.IsFalse(devices.Single(d => d.DeviceId == "device-pc").CanRemove, "La principal no se quita desde la lista");

        // «Esta PC reemplaza a la principal»: toma la clave y quita a la anterior (su fila no se borra)
        var replace = await laptop.Sync.MakeThisThePrimaryAsync();
        Assert.IsTrue(replace.Ok, replace.Message);
        Assert.AreEqual("device-laptop", CloudMeta(TursoSyncService.PrimaryDeviceKey));
        Assert.AreEqual(TursoSyncService.RevokedReplacedReason, RegistryValue("RevokedReason", "device-pc"));
        Assert.IsNotNull(RegistryValue("RevokedAtUtc", "device-pc"));
        Assert.IsTrue(laptop.Sync.Registration!.IsPrimary);
        var after = await laptop.Sync.GetDevicesAsync();
        Assert.IsFalse(after.Any(d => d.DeviceId == "device-pc"), "La PC quitada ya no aparece");

        // En su siguiente sesión, la PC anterior ve que la quitaron y no vuelve a tomar la principal
        var pcAgain = Reopen(pc);
        Assert.IsTrue((await pcAgain.Sync.SyncAsync()).Success);
        Assert.IsTrue(pcAgain.Sync.Registration!.IsRevoked);
        Assert.AreEqual("device-laptop", CloudMeta(TursoSyncService.PrimaryDeviceKey));
    }

    /// <summary>La misma base y la misma configuración con un servicio nuevo, como al volver a abrir TPVC.</summary>
    private Device Reopen(Device device)
    {
        var settings = new TursoSettingsService(device.SettingsPath);
        var sync = new TursoSyncService(device.Factory, settings, device.Profile, device.Inventory)
        {
            ClientFactory = (url, token) => new TursoClient(url, token, new HttpClient(_server, disposeHandler: false))
        };
        return new Device { Factory = device.Factory, Inventory = device.Inventory, Settings = settings, Profile = device.Profile, Sync = sync, DbPath = device.DbPath, SettingsPath = device.SettingsPath };
    }

    [TestMethod]
    public async Task RemovedDevices_DisappearFromTheListAndFromHandoff_AndTheirCodesAreNeverReused()
    {
        var pc = await CloudWithIdentityAsync();
        // Una tablet ya vinculada (como la dejará el paso 8.14), un Android sin aprobar y una PC v1.19 sin fila
        ExecuteCloud(
            "INSERT INTO DeviceRegistry (DeviceId, DeviceCode, Role, DisplayName, Model, PairedAtUtc) VALUES " +
            $"('tab-1', 'T1', 'AndroidTablet', 'Tablet del consultorio', 'SM-X710', {Now}); " +
            "INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc, Platform, HandoffProtocol, PendingCount, OldestPendingUtc) VALUES " +
            $"('tab-1', 'Galaxy Tab S9', 'AndroidTablet', '0.3.0', {Now}, 'Android', 1, 12, strftime('%Y-%m-%d %H:%M:%f','now','-2 hours')), " +
            $"('cel-x', 'Moto G', 'AndroidMobileAdmin', '0.3.0', {Now}, 'Android', 1, 0, NULL), " +
            $"('pc-old', 'Recepción', 'WindowsDesktop', '1.19.0', strftime('%Y-%m-%d %H:%M:%f','now','-1 day'), NULL, NULL, NULL, NULL);");

        var handoff = HandoffOf(pc);
        var targets = await handoff.GetLinkedDevicesAsync();
        CollectionAssert.AreEquivalent(new[] { "tab-1", "pc-old" }, targets.Select(d => d.DeviceId).ToArray(),
            "El Android sin aprobar no recibe consultas");
        Assert.IsTrue(targets.Single(d => d.DeviceId == "tab-1").IsAvailable);

        var list = await pc.Sync.GetDevicesAsync();
        var tablet = list.Single(d => d.DeviceId == "tab-1");
        Assert.AreEqual("T1", tablet.DeviceCode);
        Assert.AreEqual(12, tablet.PendingCount);
        StringAssert.StartsWith(tablet.PendingText, "12 cambios sin subir desde");
        Assert.IsTrue(tablet.CanRemove);

        // Quitar la tablet: la fila se queda con la fecha y el motivo
        var removed = await pc.Sync.RemoveDeviceAsync(tablet);
        Assert.IsTrue(removed.Ok, removed.Message);
        Assert.AreEqual(TursoSyncService.RevokedRemovedReason, RegistryValue("RevokedReason", "tab-1"));
        Assert.AreEqual("T1", RegistryValue("DeviceCode", "tab-1"));
        Assert.IsFalse((await pc.Sync.GetDevicesAsync()).Any(d => d.DeviceId == "tab-1"));
        Assert.IsFalse((await handoff.GetLinkedDevicesAsync()).Any(d => d.DeviceId == "tab-1"));

        // Quitar la PC v1.19 (sin fila): recibe una ya quitada, con el siguiente código P
        var oldPc = list.Single(d => d.DeviceId == "pc-old");
        Assert.IsNull(oldPc.DeviceCode);
        Assert.IsTrue((await pc.Sync.RemoveDeviceAsync(oldPc)).Ok);
        Assert.AreEqual("P2", RegistryValue("DeviceCode", "pc-old"));
        Assert.IsNotNull(RegistryValue("RevokedAtUtc", "pc-old"));

        // La siguiente PC recibe P3: un código nunca se reutiliza
        var laptop = CreateDevice("laptop", seed: false);
        Assert.IsTrue((await laptop.Sync.JoinCloudAsync()).Ok);
        Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
        Assert.AreEqual("P3", laptop.Sync.Registration!.DeviceCode);

        // Ni esta PC ni la principal se pueden quitar
        var self = (await laptop.Sync.GetDevicesAsync()).Single(d => d.IsThisDevice);
        Assert.IsFalse((await laptop.Sync.RemoveDeviceAsync(self)).Ok);
        var primary = (await laptop.Sync.GetDevicesAsync()).Single(d => d.IsPrimary);
        Assert.IsFalse((await laptop.Sync.RemoveDeviceAsync(primary)).Ok);
        Assert.IsNull(RegistryValue("RevokedAtUtc", "device-pc"));
    }

    [TestMethod]
    public async Task ACloudFromV126_GetsTheNewHeartbeatColumns_AndAV119HeartbeatStillWorks()
    {
        // La nube como la dejó una PC v1.19–v1.26: RegisteredDevices sin las columnas nuevas y con una fila vieja
        ExecuteCloud("CREATE TABLE RegisteredDevices (DeviceId TEXT PRIMARY KEY, DeviceName TEXT NOT NULL, DeviceRole TEXT NOT NULL, " +
                     "AppVersion TEXT NOT NULL, LastSeenUtc TEXT NOT NULL); " +
                     $"INSERT INTO RegisteredDevices VALUES ('pc-v119', 'Recepción', 'WindowsDesktop', '1.19.0', {Now});");

        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        using (var held = _server.Lock())
        using (var cmd = _server.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('RegisteredDevices')";
            var columns = new List<string>();
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
            foreach (var (name, _) in SyncSchema.RegisteredDevicesAddedColumns) CollectionAssert.Contains(columns, name);
        }

        // El latido de esta PC con todo lo nuevo
        Assert.AreEqual(TursoSyncService.WindowsPlatform, CloudDeviceValue("Platform", "device-pc"));
        Assert.AreEqual(TursoSyncService.ClientProtocol.ToString(), CloudDeviceValue("SyncProtocol", "device-pc"));
        Assert.AreEqual(ConsultationHandoffService.ProtocolVersion.ToString(), CloudDeviceValue("HandoffProtocol", "device-pc"));
        Assert.AreEqual(SyncContract.Revision.ToString(), CloudDeviceValue("ContractRevision", "device-pc"));
        Assert.AreEqual(pc.Sync.InstanceNonce, CloudDeviceValue("InstanceNonce", "device-pc"));
        Assert.AreEqual("0", CloudDeviceValue("PendingCount", "device-pc"), "Todo subió");
        Assert.IsNotNull(CloudDeviceValue("LastPushUtc", "device-pc"), "La primera sincronización subió todo");

        // El upsert de una PC v1.19 (con sus columnas explícitas) sigue funcionando y no toca las nuevas
        using (var client = new TursoClient(Url, "test-token", new HttpClient(_server, disposeHandler: false)))
        {
            await client.ExecuteAsync(
                $"INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc) VALUES (?, ?, ?, ?, {Now}) " +
                "ON CONFLICT(DeviceId) DO UPDATE SET DeviceName = excluded.DeviceName, DeviceRole = excluded.DeviceRole, " +
                "AppVersion = excluded.AppVersion, LastSeenUtc = excluded.LastSeenUtc",
                new object?[] { "pc-v119", "Recepción", "WindowsDesktop", "1.19.0" });
        }
        Assert.IsNull(CloudDeviceValue("HandoffProtocol", "pc-v119"));
        var targets = await HandoffOf(pc).GetLinkedDevicesAsync();
        var old = targets.Single(d => d.DeviceId == "pc-v119");
        Assert.IsTrue(old.IsAvailable, "Sin HandoffProtocol sigue la regla de AppVersion >= 1.17.0");
        Assert.IsNull(old.Platform);
    }

    [TestMethod]
    public async Task EachPush_WritesLastPushUtcAndWhatIsPending_AndAnIdleSyncDoesNot()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var firstPush = CloudDeviceValue("LastPushUtc", "device-pc");
        Assert.IsNotNull(firstPush);

        // Sin cambios: la marca de la subida no se mueve (los demás equipos no bajan nada por ella)
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(firstPush, CloudDeviceValue("LastPushUtc", "device-pc"));

        // Un cambio sin subir cuenta como pendiente, con su hora
        var before = DateTime.UtcNow.AddSeconds(-5);
        await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Pendiente De Subir", Age = 50 });
        var pending = await pc.Sync.GetPendingChangesAsync();
        Assert.AreEqual(1, pending.Count);
        Assert.IsNotNull(pending.OldestUtc);
        Assert.IsGreaterThan(before, pending.OldestUtc.Value);

        // El latido de la lista lo informa; la siguiente subida lo deja en 0 con otra marca
        await pc.Sync.GetDevicesAsync();
        Assert.AreEqual("1", CloudDeviceValue("PendingCount", "device-pc"));
        Assert.IsNotNull(CloudDeviceValue("OldestPendingUtc", "device-pc"));
        await Task.Delay(20);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual("0", CloudDeviceValue("PendingCount", "device-pc"));
        Assert.IsNull(CloudDeviceValue("OldestPendingUtc", "device-pc"));
        Assert.AreNotEqual(firstPush, CloudDeviceValue("LastPushUtc", "device-pc"));
    }

    [TestMethod]
    public async Task TwoInstallsWithTheSameDeviceId_AreDetected_AndTheOneThatNoticesRedownloadsEverything()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // La carpeta de TPVC copiada a otra computadora (con DPAPI funcionando: por ejemplo una máquina virtual clonada)
        var clone = CreateDevice("clon", seed: false);
        var cs = clone.Settings.CurrentSettings;
        cs.DeviceId = "device-pc";
        clone.Settings.SaveSettings(cs);
        Assert.IsTrue((await clone.Sync.SyncAsync()).Success);

        // El clon registra un paciente; la PC no lo baja porque viene "de ella misma"
        var fromClone = await clone.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente Del Clon", Age = 33 });
        Assert.IsTrue((await clone.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        using (var db = pc.Factory.CreateDbContext()) Assert.IsFalse(db.Patients.Any(p => p.SyncId == fromClone.SyncId), "Con el mismo DeviceId se omite");

        // La PC sube algo: su latido encuentra el InstanceNonce del clon y toma un DeviceId nuevo
        await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente De La PC", Age = 44 });
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var newId = pc.Settings.CurrentSettings.DeviceId;
        Assert.AreNotEqual("device-pc", newId);
        StringAssert.Contains(pc.Settings.CurrentSettings.DeviceIdNotice, "misma identidad");
        Assert.AreEqual(clone.Sync.InstanceNonce, CloudDeviceValue("InstanceNonce", "device-pc"), "Al clon se le regresa su nonce");

        // La siguiente sincronización de la PC baja todo una vez: ahora sí recibe lo del clon
        var fullPulls = pc.Settings.GetQuotaSummary().FullPullsToday;
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreEqual(fullPulls + 1, pc.Settings.GetQuotaSummary().FullPullsToday);
        using (var db = pc.Factory.CreateDbContext())
        {
            Assert.IsTrue(db.Patients.Any(p => p.SyncId == fromClone.SyncId));
            Assert.IsNull(DbMeta.Get(db, DbMeta.RedownloadAll));
        }
        Assert.AreEqual(pc.Sync.InstanceNonce, CloudDeviceValue("InstanceNonce", newId));

        // El clon sigue con el DeviceId y no ve nada raro
        await clone.Inventory.SavePatientAsync(new PatientRecord { FullName = "Otro Paciente Del Clon", Age = 21 });
        Assert.IsTrue((await clone.Sync.SyncAsync()).Success);
        Assert.AreEqual("device-pc", clone.Settings.CurrentSettings.DeviceId);
        Assert.IsNull(clone.Settings.CurrentSettings.DeviceIdNotice);
    }

    [TestMethod]
    public void ATokenThatCannotBeDecrypted_OrAnotherMachine_GivesANewDeviceId()
    {
        // Lo que deja la copia de %LocalAppData%\TPVC en otra computadora: el token cifrado con DPAPI de la original
        var path = TempFile(".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new TursoSettings
        {
            IsSyncEnabled = true,
            DatabaseUrl = Url,
            DeviceId = "device-original",
            ProtectedAuthToken = Convert.ToBase64String(Enumerable.Range(0, 48).Select(i => (byte)i).ToArray()),
            MachineFingerprint = "AAAA"
        }));
        var copied = new TursoSettingsService(path, machineFingerprint: () => "AAAA");
        var s = copied.CurrentSettings;
        Assert.AreNotEqual("device-original", s.DeviceId);
        Assert.AreEqual(string.Empty, s.AuthToken);
        Assert.IsNull(s.ProtectedAuthToken);
        StringAssert.Contains(s.DeviceIdNotice, "vuelva a pegar el token");

        // Una sola vez: al volver a abrir conserva la identidad nueva
        var reopened = new TursoSettingsService(path, machineFingerprint: () => "AAAA");
        Assert.AreEqual(s.DeviceId, reopened.CurrentSettings.DeviceId);
        reopened.DismissDeviceIdNotice();
        Assert.IsNull(new TursoSettingsService(path, machineFingerprint: () => "AAAA").CurrentSettings.DeviceIdNotice);

        // Otra computadora con el token legible (la huella de la máquina cambió)
        var other = new TursoSettingsService(path, machineFingerprint: () => "BBBB");
        Assert.AreNotEqual(s.DeviceId, other.CurrentSettings.DeviceId);
        StringAssert.Contains(other.CurrentSettings.DeviceIdNotice, "se copió de otra computadora");
        Assert.AreEqual("BBBB", other.CurrentSettings.MachineFingerprint);

        // Una configuración de antes de v1.27.0 (sin huella) la adopta sin cambiar de identidad
        var legacyPath = TempFile(".json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new TursoSettings { DeviceId = "device-legacy", MachineFingerprint = null }));
        var legacy = new TursoSettingsService(legacyPath, machineFingerprint: () => "CCCC");
        Assert.AreEqual("device-legacy", legacy.CurrentSettings.DeviceId);
        Assert.AreEqual("CCCC", legacy.CurrentSettings.MachineFingerprint);
        Assert.IsNull(legacy.CurrentSettings.DeviceIdNotice);
        Assert.AreEqual(TursoSettingsService.HashMachineId("ABC"), TursoSettingsService.HashMachineId(" abc "));
    }

    [TestMethod]
    public async Task TheSixHourCleanup_RemovesRevokedAndForgottenHeartbeats()
    {
        var pc = await CloudWithIdentityAsync();
        ExecuteCloud(
            "INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc) VALUES " +
            "('old-1', 'Laptop vieja', 'WindowsDesktop', '1.18.0', strftime('%Y-%m-%d %H:%M:%f','now','-61 days')), " +
            "('recent', 'Recepción', 'WindowsDesktop', '1.19.0', strftime('%Y-%m-%d %H:%M:%f','now','-59 days')), " +
            $"('revoked', 'Tablet perdida', 'AndroidTablet', '0.3.0', {Now}); " +
            "INSERT INTO DeviceRegistry (DeviceId, DeviceCode, Role, DisplayName, PairedAtUtc, RevokedAtUtc, RevokedReason) VALUES " +
            $"('revoked', 'T1', 'AndroidTablet', 'Tablet perdida', {Now}, {Now}, 'perdido');");

        await HandoffOf(pc).PollAsync(outgoingId: null);

        Assert.IsNull(CloudScalar("SELECT DeviceId FROM RegisteredDevices WHERE DeviceId = 'old-1'"));
        Assert.IsNull(CloudScalar("SELECT DeviceId FROM RegisteredDevices WHERE DeviceId = 'revoked'"));
        Assert.IsNotNull(CloudScalar("SELECT DeviceId FROM RegisteredDevices WHERE DeviceId = 'recent'"));
        Assert.IsNotNull(CloudScalar("SELECT DeviceId FROM RegisteredDevices WHERE DeviceId = 'device-pc'"));
        Assert.AreEqual("T1", RegistryValue("DeviceCode", "revoked"), "DeviceRegistry nunca se borra");
    }
}
