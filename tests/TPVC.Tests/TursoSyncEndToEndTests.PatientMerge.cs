using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.22 (H1b) con dos y tres equipos sobre el servidor simulado: la combinación por campo y por renglón al bajar, lo que
/// sube después de combinar (una sola vez), la escritura de una PC anterior (sin marcas) y la actualización desde v1.38 (el
/// piso, y la copia que v1.38 solo bajó), la próxima cita derivada en orden clínico, las llaves desconocidas de FieldStampsJson
/// y el aviso de otra PC sin actualizar.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private const string MergeRecord = "EXP-2026-002";

    private async Task<(Device Pc, Device Tablet, Guid Patient)> PcAndTabletAsync()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var patient = (await pc.Inventory.GetPatientsAsync()).Single(p => p.RecordNumber == MergeRecord);
        return (pc, tablet, patient.SyncId);
    }

    /// <summary>La instancia como la cargan las pantallas (con su foto, paso 8.21).</summary>
    private static async Task<PatientRecord> OpenAsync(Device device, Guid syncId) =>
        (await device.Inventory.GetPatientsAsync()).Single(p => p.SyncId == syncId);

    private static async Task<PatientRecord> StoredAsync(Device device, Guid syncId)
    {
        using var db = device.Factory.CreateDbContext();
        return await db.Patients.AsNoTracking().SingleAsync(p => p.SyncId == syncId);
    }

    private static async Task EditAsync(Device device, Guid syncId, Action<PatientRecord> change)
    {
        var patient = await OpenAsync(device, syncId);
        change(patient);
        await device.Inventory.SavePatientAsync(patient);
    }

    private static Task AddSupplyAsync(Device device, Guid syncId, string name, int quantity = 1) =>
        EditAsync(device, syncId, p => p.ActiveSupplies = new List<PatientSupplyItem>(p.ActiveSupplies) { new() { SupplyName = name, Quantity = quantity } });

    private static List<string> SupplyNames(PatientRecord patient) => patient.ActiveSupplies.Select(s => s.SupplyName).ToList();

    /// <summary>Cada equipo sincroniza hasta que una vuelta completa no sube nada (como mucho 4 vueltas).</summary>
    private static async Task QuiesceAsync(params Device[] devices)
    {
        for (int round = 1; round <= 4; round++)
        {
            int pushed = 0;
            foreach (var device in devices)
            {
                var result = await device.Sync.SyncAsync();
                Assert.IsTrue(result.Success, result.ErrorMessage);
                pushed += result.RowsPushed;
            }
            if (pushed == 0) return;
        }
        Assert.Fail("Los equipos no convergieron en 4 vueltas: algo vuelve a subir en cada sincronización");
    }

    /// <summary>Una vez iguales, otra vuelta de sincronizaciones no sube nada ni deja nada pendiente.</summary>
    private static async Task AssertNoMoreUploadsAsync(params Device[] devices)
    {
        foreach (var device in devices)
        {
            var result = await device.Sync.SyncAsync();
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(0, result.RowsPushed, $"{device.Settings.CurrentSettings.DeviceName} volvió a subir");
            Assert.AreEqual(0, (await device.Sync.GetPendingChangesAsync()).Count, $"{device.Settings.CurrentSettings.DeviceName} dejó algo pendiente");
        }
    }

    private static async Task<string> MeaningAsync(Device device, Guid syncId)
    {
        using var db = device.Factory.CreateDbContext();
        var row = await db.Patients.AsNoTracking().SingleAsync(p => p.SyncId == syncId);
        var version = PatientFieldMerge.LocalVersion(row, PatientFieldMerge.PatientsMap(db.Model));
        return PatientMerge.Meaning(version, PatientFieldMerge.ColumnNames(db.Model));
    }

    // ================================================================== por campo

    [TestMethod]
    public async Task AnOfflineAllergyEditOnTheTablet_AndAPhoneChangeOnThePc_BothSurvive_InEitherOrder()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();

        // 1. La tablet edita sin conexión; la PC edita después y sube primero: la subida de la tablet se rechaza (su fila es
        //    más vieja), la vuelve a leer, combina y sube el resultado en la misma sincronización
        _server.IsOffline = true;
        await EditAsync(tablet, patient, p => p.Allergies = "Penicilina (anafilaxia)");
        Assert.IsFalse((await tablet.Sync.SyncAsync()).Success);
        _server.IsOffline = false;
        await EditAsync(pc, patient, p => p.Phone = "5599887766");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.AreEqual("Penicilina (anafilaxia)", CloudPatientValue(patient, nameof(PatientRecord.Allergies)), "La nube ya tiene las dos");
        Assert.AreEqual("5599887766", CloudPatientValue(patient, nameof(PatientRecord.Phone)));
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        foreach (var device in new[] { pc, tablet })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual("Penicilina (anafilaxia)", stored.Allergies);
            Assert.AreEqual("5599887766", stored.Phone);
        }

        // 2. Al revés: la tablet edita después sin haber bajado y su fila gana en la nube (LWW por fila); la PC la combina con
        //    lo suyo y la repara
        await EditAsync(pc, patient, p => p.Occupation = "Docente");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        await EditAsync(tablet, patient, p => p.Religion = "Católica");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.AreNotEqual("Docente", CloudPatientValue(patient, nameof(PatientRecord.Occupation)), "La nube sigue con LWW por fila");
        await QuiesceAsync(pc, tablet);
        Assert.AreEqual("Docente", CloudPatientValue(patient, nameof(PatientRecord.Occupation)), "La siguiente combinación la repara");
        foreach (var device in new[] { pc, tablet })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual("Docente", stored.Occupation);
            Assert.AreEqual("Católica", stored.Religion);
            Assert.AreEqual("Penicilina (anafilaxia)", stored.Allergies);
        }
        Assert.AreEqual(await MeaningAsync(pc, patient), await MeaningAsync(tablet, patient), "Los dos equipos quedan iguales");
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    // ================================================================== hojas por renglón

    [TestMethod]
    public async Task ARowAddedOnThePc_AndAnotherOnTheTablet_ToTheSameSheet_BothSurvive()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();

        await AddSupplyAsync(pc, patient, "Gasa estéril");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        await AddSupplyAsync(tablet, patient, "Venda elástica 10 cm");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        await QuiesceAsync(pc, tablet);

        foreach (var device in new[] { pc, tablet })
        {
            var names = SupplyNames(await StoredAsync(device, patient));
            CollectionAssert.Contains(names, "Gasa estéril");
            CollectionAssert.Contains(names, "Venda elástica 10 cm");
        }
        CollectionAssert.AreEqual(SupplyNames(await StoredAsync(pc, patient)), SupplyNames(await StoredAsync(tablet, patient)), "En el mismo orden");
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    [TestMethod]
    public async Task ARemovalWinsOverAnOlderEdit_AndANewerEditWinsOverAnOlderRemoval()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();
        await AddSupplyAsync(pc, patient, "Gasa estéril");
        await AddSupplyAsync(pc, patient, "Venda elástica 10 cm");
        await QuiesceAsync(pc, tablet);

        // La PC le cambia la cantidad a la venda y agrega alcohol; después, sin haberlo bajado, la tablet quita la venda: gana
        // quitarla, y el alcohol de la PC se queda (con el LWW por fila, la hoja de la tablet lo habría tirado)
        await EditAsync(pc, patient, p => p.ActiveSupplies.Single(s => s.SupplyName == "Venda elástica 10 cm").Quantity = 3);
        await AddSupplyAsync(pc, patient, "Alcohol 70%");
        await EditAsync(tablet, patient, p => p.ActiveSupplies = p.ActiveSupplies.Where(s => s.SupplyName != "Venda elástica 10 cm").ToList());
        await QuiesceAsync(pc, tablet);
        foreach (var device in new[] { pc, tablet })
        {
            var names = SupplyNames(await StoredAsync(device, patient));
            CollectionAssert.DoesNotContain(names, "Venda elástica 10 cm");
            CollectionAssert.Contains(names, "Alcohol 70%");
        }

        // Ahora la tablet quita la gasa y agrega una jeringa, y después, sin haberlo bajado, la PC le cambia la cantidad a la
        // gasa: gana la edición, y la jeringa se queda
        await EditAsync(tablet, patient, p => p.ActiveSupplies = p.ActiveSupplies.Where(s => s.SupplyName != "Gasa estéril").ToList());
        await AddSupplyAsync(tablet, patient, "Jeringa 5 ml");
        await EditAsync(pc, patient, p => p.ActiveSupplies.Single(s => s.SupplyName == "Gasa estéril").Quantity = 5);
        await QuiesceAsync(pc, tablet);
        foreach (var device in new[] { pc, tablet })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual(5, stored.ActiveSupplies.Single(s => s.SupplyName == "Gasa estéril").Quantity);
            CollectionAssert.Contains(SupplyNames(stored), "Jeringa 5 ml");

            // Un lector de antes (cada elemento del arreglo es un renglón) solo ve renglones vivos
            var plain = JsonSerializer.Deserialize<List<PatientSupplyItem>>(stored.ActiveSuppliesJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            CollectionAssert.AreEqual(SupplyNames(stored), plain.Select(s => s.SupplyName).ToList());
            StringAssert.Contains(stored.FieldStampsJson, "/quitado", "Lo quitado vive en las marcas");
        }
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    // ================================================================== escritura sin marcas

    [TestMethod]
    public async Task AWriteFromAPcBeforeTheFieldStamps_WinsWithItsUpdatedAtUtc()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();
        await EditAsync(pc, patient, p => p.Allergies = "Penicilina");
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var stampsBefore = CloudPatientValue(patient, nameof(PatientRecord.FieldStampsJson));

        // Sin subir todavía, la PC cambia la ocupación; después una PC v1.38 sube la fila completa con lo que tenía (no conoce
        // FieldStampsJson: la deja como estaba)
        await EditAsync(pc, patient, p => p.Occupation = "Docente");
        await Task.Delay(5);
        ExecuteCloud($"UPDATE Patients SET Allergies = 'Ninguna', Occupation = 'Comerciante', Phone = '5500000000', " +
                     $"UpdatedAtUtc = '{TursoSyncService.FormatDate(DateTime.UtcNow)}', SyncedAtUtc = {TursoSyncService.ServerNowSql}, " +
                     $"OriginDeviceId = 'device-pc-v1-38' WHERE SyncId = '{TursoSyncService.FormatGuid(patient)}'");
        Assert.AreEqual(stampsBefore, CloudPatientValue(patient, nameof(PatientRecord.FieldStampsJson)));

        await QuiesceAsync(pc, tablet);
        foreach (var device in new[] { pc, tablet })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual("Ninguna", stored.Allergies, "Lo que difiere cuenta con la marca de la fila de la PC vieja: es más nueva");
            Assert.AreEqual("Comerciante", stored.Occupation, "También le gana a la edición de aquí, que es anterior (como el LWW)");
            Assert.AreEqual("5500000000", stored.Phone);
        }

        // Lo que se edita después vuelve a combinarse campo por campo
        await EditAsync(tablet, patient, p => p.Religion = "Ninguna");
        await EditAsync(pc, patient, p => p.Occupation = "Docente");
        await QuiesceAsync(pc, tablet);
        var final = await StoredAsync(tablet, patient);
        Assert.AreEqual("Docente", final.Occupation);
        Assert.AreEqual("Ninguna", final.Religion);
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    // ================================================================== al actualizar desde v1.38

    /// <summary>
    /// Deja la nube y los equipos como estaban con v1.38: ningún expediente tiene marcas por campo y la última bajada de
    /// Patients no tenía FieldStampsJson (la primera sincronización de cada equipo con esta versión baja la tabla completa
    /// antes de subir, como al actualizar). Sin triggers: no queda nada por subir.
    /// </summary>
    private async Task BackToV138Async(params Device[] devices)
    {
        ExecuteCloud("UPDATE Patients SET FieldStampsJson = ''");
        foreach (var device in devices)
        {
            using var db = device.Factory.CreateDbContext();
            db.ApplyingRemoteChanges = true;
            foreach (var row in await db.Patients.ToListAsync()) row.FieldStampsJson = string.Empty;
            await SyncChangeLog.SaveRemoteChangesAsync(db);

            var key = DbMeta.PullColumnsPrefix + "Patients";
            var pulled = (await DbMeta.GetAsync(db, key))!.Split(',').Where(c => c != nameof(PatientRecord.FieldStampsJson));
            await DbMeta.SetAsync(db, key, string.Join(",", pulled));
        }
    }

    /// <summary>
    /// Una edición de una PC que todavía tiene v1.38: la fila completa con una marca nueva y sin tocar FieldStampsJson (no la
    /// conoce). Con <paramref name="upload"/> la sube como lo hacía v1.38 (sin esa columna: en la nube se queda la que había);
    /// si no, queda pendiente en su registro de cambios.
    /// </summary>
    private async Task EditAsV138Async(Device device, Guid syncId, Action<PatientRecord> change, bool upload)
    {
        using var db = device.Factory.CreateDbContext();
        db.ApplyingRemoteChanges = true;
        var row = await db.Patients.SingleAsync(p => p.SyncId == syncId);
        change(row);
        row.SyncSuppliesJson();
        row.UpdatedAtUtc = DateTime.UtcNow;
        await SyncChangeLog.SaveRemoteChangesAsync(db);
        if (!upload)
        {
            await SyncChangeLog.MarkForUploadAsync(db, nameof(PatientRecord), syncId);
            return;
        }

        string[] columns =
        {
            nameof(PatientRecord.Allergies), nameof(PatientRecord.Phone), nameof(PatientRecord.Occupation), nameof(PatientRecord.ActiveSuppliesJson)
        };
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Patients SET " + string.Join(", ", columns.Select(c => $"\"{c}\" = ${c}")) +
                          $", UpdatedAtUtc = $updated, SyncedAtUtc = {TursoSyncService.ServerNowSql}, OriginDeviceId = $device WHERE SyncId = $id";
        foreach (var column in columns) cmd.Parameters.AddWithValue("$" + column, typeof(PatientRecord).GetProperty(column)!.GetValue(row));
        cmd.Parameters.AddWithValue("$updated", TursoSyncService.FormatDate(row.UpdatedAtUtc));
        cmd.Parameters.AddWithValue("$device", device.Settings.CurrentSettings.DeviceId);
        cmd.Parameters.AddWithValue("$id", TursoSyncService.FormatGuid(syncId));
        Assert.AreEqual(1, cmd.ExecuteNonQuery());
    }

    [TestMethod]
    public async Task TheFirstSyncAfterUpdatingFromV138_UploadsNothing_ForPatientsThatAreTheSameOnBothSides()
    {
        var (pc, tablet, _) = await PcAndTabletAsync();
        for (int i = 1; i <= 5; i++)
        {
            await pc.Inventory.SavePatientAsync(new PatientRecord { FullName = $"Paciente de prueba {i}", Phone = $"55000000{i:00}", Allergies = "Negadas" });
        }
        await QuiesceAsync(pc, tablet);
        var count = (long)CloudScalar("SELECT COUNT(*) FROM Patients")!;
        Assert.IsGreaterThanOrEqualTo(6L, count);
        await BackToV138Async(pc, tablet);

        // Cada equipo se actualiza: baja la tabla completa (las mismas filas sin marcas) y no sube ninguna
        foreach (var device in new[] { pc, tablet })
        {
            var result = await device.Sync.SyncAsync();
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(0, result.RowsPushed, $"{device.Settings.CurrentSettings.DeviceName} subió {result.RowsPushed} de {count} expedientes iguales");
            Assert.AreEqual(0, (await device.Sync.GetPendingChangesAsync()).Count);
            using var db = device.Factory.CreateDbContext();
            Assert.IsTrue(await db.Patients.AllAsync(p => p.FieldStampsJson == string.Empty), "Se queda la fila de la nube tal cual");
        }
        Assert.AreEqual(0L, (long)CloudScalar("SELECT COUNT(*) FROM Patients WHERE FieldStampsJson <> ''")!);
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    [TestMethod]
    public async Task TwoPcsUpdatedFromV138_OneAfterTheOther_TheStaleOneDoesNotUndoWhatTheFirstWroteBefore()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var other = CreateDevice("pc2", seed: false);
        Assert.IsTrue((await other.Sync.SyncAsync()).Success);
        var patient = (await pc.Inventory.GetPatientsAsync()).Single(p => p.RecordNumber == MergeRecord).SyncId;
        await AddSupplyAsync(pc, patient, "Gasa estéril");
        await AddSupplyAsync(pc, patient, "Venda elástica 10 cm");
        await EditAsync(pc, patient, p => p.Allergies = "Negadas");
        await QuiesceAsync(pc, other);
        await BackToV138Async(pc, other);

        // 1. Todavía con v1.38, la PC cambia las alergias y quita la venda, y sube la fila completa; la otra PC no baja
        await Task.Delay(5);
        await EditAsV138Async(pc, patient, p =>
        {
            p.Allergies = "Látex";
            p.ActiveSupplies = p.ActiveSupplies.Where(s => s.SupplyName != "Venda elástica 10 cm").ToList();
        }, upload: true);

        // 2. La PC se actualiza (su primera sincronización no sube nada) y cambia el teléfono: se marca solo el teléfono, y lo
        //    de antes conserva su hora como piso
        var upgrade = await pc.Sync.SyncAsync();
        Assert.IsTrue(upgrade.Success, upgrade.ErrorMessage);
        Assert.AreEqual(0, upgrade.RowsPushed);
        var v138 = (await StoredAsync(pc, patient)).UpdatedAtUtc;
        await EditAsync(pc, patient, p => p.Phone = "5599887766");
        var stamps = PatientFieldStamps.Parse((await StoredAsync(pc, patient)).FieldStampsJson);
        Assert.AreEqual(PatientFieldStamps.Format(v138), stamps[PatientFieldStamps.FloorKey]);
        Assert.IsNull(stamps[nameof(PatientRecord.Allergies)]);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // 3. La otra PC se actualiza con su copia de antes de todo eso (sin marcas): no deshace nada ni sube nada
        var stale = await other.Sync.SyncAsync();
        Assert.IsTrue(stale.Success, stale.ErrorMessage);
        Assert.AreEqual(0, stale.RowsPushed, "Lo que bajó ya incluye su copia vieja");
        foreach (var device in new[] { pc, other })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual("Látex", stored.Allergies, "La alergia de la PC v1.38 sobrevive");
            CollectionAssert.AreEqual(new[] { "Gasa estéril" }, SupplyNames(stored), "La venda quitada no revive");
            Assert.AreEqual("5599887766", stored.Phone);
        }
        Assert.AreEqual("Látex", CloudPatientValue(patient, nameof(PatientRecord.Allergies)));
        Assert.AreEqual(await MeaningAsync(pc, patient), await MeaningAsync(other, patient));
        await AssertNoMoreUploadsAsync(pc, other);
    }

    [TestMethod]
    public async Task AnOfflineEditOnV138_ThenTheUpdate_AndAStampedEditBeforeSyncing_KeepsTheV138Edit()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();
        await EditAsync(pc, patient, p => p.Allergies = "Negadas");
        await QuiesceAsync(pc, tablet);
        await BackToV138Async(pc, tablet);
        var upgrade = await tablet.Sync.SyncAsync();
        Assert.IsTrue(upgrade.Success, upgrade.ErrorMessage);
        Assert.AreEqual(0, upgrade.RowsPushed, "La tablet se actualiza primero: no sube nada");

        // 1. La PC, sin conexión y todavía con v1.38, cambia las alergias y agrega un renglón (queda pendiente, sin marcas)
        await Task.Delay(5);
        await EditAsV138Async(pc, patient, p =>
        {
            p.Allergies = "Látex";
            p.ActiveSupplies = new List<PatientSupplyItem>(p.ActiveSupplies) { new() { SupplyName = "Alcohol 70%", Quantity = 1 } };
        }, upload: false);
        var v138 = (await StoredAsync(pc, patient)).UpdatedAtUtc;

        // 2. Mientras, la tablet (ya actualizada) cambia la ocupación y sube
        await Task.Delay(5);
        await EditAsync(tablet, patient, p => p.Occupation = "Docente");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // 3. La PC se actualiza y, antes de sincronizar, cambia el teléfono: lo de v1.38 conserva su hora como piso
        await Task.Delay(5);
        await EditAsync(pc, patient, p => p.Phone = "5511223344");
        var stamps = PatientFieldStamps.Parse((await StoredAsync(pc, patient)).FieldStampsJson);
        Assert.AreEqual(PatientFieldStamps.Format(v138), stamps[PatientFieldStamps.FloorKey]);
        await QuiesceAsync(pc, tablet);

        foreach (var device in new[] { pc, tablet })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual("Látex", stored.Allergies, "La edición de v1.38 es más nueva que lo que tenía la tablet (con el mismo valor ordinal perdería)");
            CollectionAssert.Contains(SupplyNames(stored), "Alcohol 70%");
            Assert.AreEqual("Docente", stored.Occupation);
            Assert.AreEqual("5511223344", stored.Phone);
        }
        Assert.AreEqual(await MeaningAsync(pc, patient), await MeaningAsync(tablet, patient));
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    /// <summary>
    /// Una bajada de una PC que todavía tiene v1.38: la fila de la nube por LWW, sin FieldStampsJson (no la conoce: la suya se
    /// queda vacía). Sin triggers: no queda nada por subir.
    /// </summary>
    private async Task PullAsV138Async(Device device, Guid syncId)
    {
        string[] columns =
        {
            nameof(PatientRecord.Allergies), nameof(PatientRecord.Phone), nameof(PatientRecord.Occupation), nameof(PatientRecord.ActiveSuppliesJson)
        };
        var cloud = new Dictionary<string, object?>();
        using (var held = _server.Lock())
        using (var cmd = _server.Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(c => $"\"{c}\""))}, UpdatedAtUtc FROM Patients WHERE SyncId = $id";
            cmd.Parameters.AddWithValue("$id", TursoSyncService.FormatGuid(syncId));
            using var reader = cmd.ExecuteReader();
            Assert.IsTrue(reader.Read());
            for (int i = 0; i <= columns.Length; i++) cloud[reader.GetName(i)] = reader.GetValue(i);
        }

        using var db = device.Factory.CreateDbContext();
        db.ApplyingRemoteChanges = true;
        var row = await db.Patients.SingleAsync(p => p.SyncId == syncId);
        foreach (var column in columns) typeof(PatientRecord).GetProperty(column)!.SetValue(row, cloud[column]?.ToString() ?? string.Empty);
        row.UpdatedAtUtc = TursoSyncService.ParseDate(cloud["UpdatedAtUtc"]);
        await SyncChangeLog.SaveRemoteChangesAsync(db);
        Assert.AreEqual(string.Empty, (await StoredAsync(device, syncId)).FieldStampsJson);
    }

    /// <summary>Tres PC con v1.38 y el expediente sin marcas; dos se actualizan (sin subir nada) y la tercera sigue con v1.38.</summary>
    private async Task<(Device A, Device B, Device S, Guid Patient)> ThreePcsFromV138Async()
    {
        var a = CreateDevice("pc", seed: true);
        Assert.IsTrue((await a.Sync.SyncAsync()).Success);
        var b = CreateDevice("pc2", seed: false);
        Assert.IsTrue((await b.Sync.SyncAsync()).Success);
        var s = CreateDevice("pc3", seed: false);
        Assert.IsTrue((await s.Sync.SyncAsync()).Success);
        var patient = (await a.Inventory.GetPatientsAsync()).Single(p => p.RecordNumber == MergeRecord).SyncId;
        await EditAsync(a, patient, p => p.Allergies = "Negadas");
        await QuiesceAsync(a, b, s);
        await BackToV138Async(a, b, s);
        foreach (var device in new[] { a, b })
        {
            var upgrade = await device.Sync.SyncAsync();
            Assert.IsTrue(upgrade.Success, upgrade.ErrorMessage);
            Assert.AreEqual(0, upgrade.RowsPushed);
        }
        return (a, b, s, patient);
    }

    [TestMethod]
    public async Task APcUpdatedFromV138_WithACopyItOnlyPulled_TakesTheCloudRow_AndDoesNotUndoAnEarlierAllergy()
    {
        var (a, b, s, patient) = await ThreePcsFromV138Async();

        // B (ya actualizada) cambia las alergias sin conexión; después A cambia el teléfono y sube (V1, sin la alergia)
        await Task.Delay(5);
        await EditAsync(b, patient, p => p.Allergies = "Látex");
        await Task.Delay(5);
        await EditAsync(a, patient, p => p.Phone = "5599887766");
        Assert.IsTrue((await a.Sync.SyncAsync()).Success);

        // S, todavía con v1.38, baja V1 por LWW (sin sus marcas); B se conecta y la nube pasa a la combinación con su alergia
        await PullAsV138Async(s, patient);
        Assert.IsTrue((await b.Sync.SyncAsync()).Success);
        Assert.AreEqual("Látex", CloudPatientValue(patient, nameof(PatientRecord.Allergies)));

        // S se actualiza: su copia solo se bajó, así que toma la fila de la nube y no sube nada
        var upgrade = await s.Sync.SyncAsync();
        Assert.IsTrue(upgrade.Success, upgrade.ErrorMessage);
        Assert.AreEqual(0, upgrade.RowsPushed, "Una copia que solo se bajó no es una escritura de la fila completa");
        Assert.AreEqual(CloudPatientValue(patient, nameof(PatientRecord.FieldStampsJson)), (await StoredAsync(s, patient)).FieldStampsJson);

        await QuiesceAsync(a, b, s);
        foreach (var device in new[] { a, b, s })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual("Látex", stored.Allergies, $"{device.Settings.CurrentSettings.DeviceName}: la alergia de B sobrevive");
            Assert.AreEqual("5599887766", stored.Phone);
        }
        await AssertNoMoreUploadsAsync(a, b, s);
    }

    [TestMethod]
    public async Task APcUpdatedFromV138_WhileTheCloudIsStillAtThePulledVersion_UploadsNothing_AndALaterAllergyFromAnotherPcWins()
    {
        var (a, b, s, patient) = await ThreePcsFromV138Async();
        await Task.Delay(5);
        await EditAsync(b, patient, p => p.Allergies = "Látex");
        await Task.Delay(5);
        await EditAsync(a, patient, p => p.Phone = "5599887766");
        Assert.IsTrue((await a.Sync.SyncAsync()).Success);

        // S baja V1 con v1.38 y se actualiza mientras la nube sigue en V1: la misma versión, con sus marcas
        await PullAsV138Async(s, patient);
        var upgrade = await s.Sync.SyncAsync();
        Assert.IsTrue(upgrade.Success, upgrade.ErrorMessage);
        Assert.AreEqual(0, upgrade.RowsPushed);
        Assert.AreEqual(CloudPatientValue(patient, nameof(PatientRecord.FieldStampsJson)), (await StoredAsync(s, patient)).FieldStampsJson);

        // B se conecta: su alergia (anterior a V1, pero posterior al piso de V1) sobrevive en los tres
        await QuiesceAsync(b, a, s);
        foreach (var device in new[] { a, b, s })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual("Látex", stored.Allergies, $"{device.Settings.CurrentSettings.DeviceName}: la alergia de B sobrevive");
            Assert.AreEqual("5599887766", stored.Phone);
        }
        await AssertNoMoreUploadsAsync(a, b, s);
    }

    [TestMethod]
    public async Task ARowRemovedOrEditedOnThePc_AndAnotherRowEditedOnTheTablet_AllThreeEditsSurvive()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();
        await AddSupplyAsync(pc, patient, "Gasa estéril");
        await AddSupplyAsync(pc, patient, "Venda elástica 10 cm");
        await AddSupplyAsync(pc, patient, "Alcohol 70%");
        await QuiesceAsync(pc, tablet);

        // La PC quita la gasa y le cambia la cantidad al alcohol; después, sin haberlo bajado, la tablet solo le cambia la
        // cantidad a la venda: guardar la hoja marca solo el renglón que cambió
        await EditAsync(pc, patient, p =>
        {
            p.ActiveSupplies = p.ActiveSupplies.Where(s => s.SupplyName != "Gasa estéril").ToList();
            p.ActiveSupplies.Single(s => s.SupplyName == "Alcohol 70%").Quantity = 4;
        });
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        await Task.Delay(5);
        await EditAsync(tablet, patient, p => p.ActiveSupplies.Single(s => s.SupplyName == "Venda elástica 10 cm").Quantity = 2);
        var onTablet = await StoredAsync(tablet, patient);
        var stamp = PatientFieldStamps.Format(onTablet.UpdatedAtUtc);
        var stamped = PatientFieldStamps.Parse(onTablet.FieldStampsJson).Entries.Where(e => e.Value == stamp).Select(e => e.Key).ToList();
        var venda = onTablet.ActiveSupplies.Single(s => s.SupplyName == "Venda elástica 10 cm").RowId!.Value;
        CollectionAssert.AreEquivalent(new[] { PatientFieldStamps.RowKey(PatientSheetRows.SuppliesColumn, venda), PatientFieldStamps.RowStampKey }, stamped);

        await QuiesceAsync(tablet, pc);
        foreach (var device in new[] { pc, tablet })
        {
            var stored = await StoredAsync(device, patient);
            CollectionAssert.DoesNotContain(SupplyNames(stored), "Gasa estéril", "Lo quitó la PC");
            Assert.AreEqual(4, stored.ActiveSupplies.Single(s => s.SupplyName == "Alcohol 70%").Quantity, "Lo cambió la PC");
            Assert.AreEqual(2, stored.ActiveSupplies.Single(s => s.SupplyName == "Venda elástica 10 cm").Quantity, "Lo cambió la tablet");
        }
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    // ================================================================== llaves desconocidas

    [TestMethod]
    public async Task UnknownFieldStampKeys_SurviveAMergeAndALocalEdit()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();
        await EditAsync(tablet, patient, p => p.Allergies = "Látex");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // Un equipo con una versión más nueva sube la fila con la marca de una columna que esta versión no conoce
        const string future = "ColumnaDeUnaVersionNueva";
        await Task.Delay(5);
        var stamp = TursoSyncService.FormatDate(DateTime.UtcNow);
        ExecuteCloud($"UPDATE Patients SET FieldStampsJson = json_set(FieldStampsJson, '$.{future}', '{stamp}', '$.UpdatedAtUtc', '{stamp}'), " +
                     $"UpdatedAtUtc = '{stamp}', SyncedAtUtc = {TursoSyncService.ServerNowSql}, OriginDeviceId = 'device-version-nueva' " +
                     $"WHERE SyncId = '{TursoSyncService.FormatGuid(patient)}'");

        // La PC la baja y la combina (conserva la llave), después edita aquí (el sello también la conserva) y sube
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        StringAssert.Contains((await StoredAsync(pc, patient)).FieldStampsJson, future, "La combinación la conserva");
        await EditAsync(pc, patient, p => p.Phone = "5512121212");
        StringAssert.Contains((await StoredAsync(pc, patient)).FieldStampsJson, future, "El sello también");
        await QuiesceAsync(pc, tablet);

        StringAssert.Contains(CloudPatientValue(patient, nameof(PatientRecord.FieldStampsJson)), future, "Sigue en la nube");
        foreach (var device in new[] { pc, tablet })
        {
            StringAssert.Contains((await StoredAsync(device, patient)).FieldStampsJson, future);
        }
        Assert.AreEqual("Látex", CloudPatientValue(patient, nameof(PatientRecord.Allergies)));
        Assert.AreEqual("5512121212", CloudPatientValue(patient, nameof(PatientRecord.Phone)));
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    [TestMethod]
    public async Task FieldStampsInTheFuture_AreRepairedWithTheRow()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();
        var future = DateTime.UtcNow.AddHours(3);
        using (var db = pc.Factory.CreateDbContext())
        {
            // Un reloj que se adelantó 3 h sin conexión: la fila y la marca de las alergias quedaron en el futuro
            db.ApplyingRemoteChanges = true;
            var row = db.Patients.Single(p => p.SyncId == patient);
            row.Allergies = "Del futuro";
            row.UpdatedAtUtc = future;
            var stamps = new PatientFieldStamps { ["Allergies"] = PatientFieldStamps.Format(future), RowStamp = future };
            row.FieldStampsJson = stamps.ToJson();
            await SyncChangeLog.SaveRemoteChangesAsync(db);
            await SyncChangeLog.MarkForUploadAsync(db, nameof(PatientRecord), patient);
        }

        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var stored = await StoredAsync(pc, patient);
        Assert.IsLessThan(future.AddHours(-1), stored.UpdatedAtUtc, "La fila se reparó con la hora del servidor");
        var repaired = PatientFieldStamps.Parse(stored.FieldStampsJson);
        Assert.IsTrue(repaired.Entries.Values.All(v => string.CompareOrdinal(v, PatientFieldStamps.Format(future.AddHours(-1))) < 0),
            "Y sus marcas por campo también: " + stored.FieldStampsJson);

        // Una edición real de las alergias en la tablet, posterior, le gana
        await EditAsync(tablet, patient, p => p.Allergies = "Penicilina");
        await QuiesceAsync(pc, tablet);
        Assert.AreEqual("Penicilina", (await StoredAsync(pc, patient)).Allergies);
    }

    // ================================================================== resumen derivado

    [TestMethod]
    public async Task TheNextAppointment_IsTheSameOnBothDevices_AndFollowsClinicalOrder()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();
        var onTablet = await OpenAsync(tablet, patient);
        var today = DateTime.Today;

        // En la tablet, en este orden: una en 10 días, una en 3 días a las 12:00, una mañana ya atendida y una pasado mañana cancelada
        await tablet.Inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = onTablet.Id, Date = today.AddDays(10), Time = "09:00", Reason = "Control" });
        await tablet.Inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = onTablet.Id, Date = today.AddDays(3), Time = "12:00", Reason = "Estudios" });
        var attended = await tablet.Inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = onTablet.Id, Date = today.AddDays(1), Time = "08:00", Reason = "Curación" });
        await tablet.Inventory.UpdateAppointmentStatusAsync(attended.Id, "Atendida");
        await tablet.Inventory.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = onTablet.Id, Date = today.AddDays(2), Time = "08:00", Reason = "Cancelada", Status = "Cancelada"
        });
        var tabletRow = await StoredAsync(tablet, patient);
        Assert.AreEqual(today.AddDays(3), tabletRow.NextAppointmentDate, "La primera pendiente, no la última que se guardó");
        Assert.AreEqual("12:00", tabletRow.NextAppointmentTime);
        Assert.AreEqual(onTablet.UpdatedAtUtc, tabletRow.UpdatedAtUtc, "Agendar ya no escribe el expediente");

        // En la PC, el mismo día de la de las 12:00 pero más temprano
        var onPc = await OpenAsync(pc, patient);
        await pc.Inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = onPc.Id, Date = today.AddDays(3), Time = "10:30", Reason = "Revisión" });
        await QuiesceAsync(pc, tablet, pc);

        foreach (var device in new[] { pc, tablet })
        {
            var stored = await StoredAsync(device, patient);
            Assert.AreEqual(today.AddDays(3), stored.NextAppointmentDate);
            Assert.AreEqual("10:30", stored.NextAppointmentTime);
            Assert.AreEqual("Revisión", stored.NextAppointmentReason);
        }

        // Cancelar la de las 10:30 en la tablet: la siguiente en los dos
        var appointmentId = (await tablet.Inventory.GetAppointmentsAsync(today.AddDays(3), today.AddDays(3))).Single(a => a.Time == "10:30").Id;
        await tablet.Inventory.UpdateAppointmentStatusAsync(appointmentId, "Cancelada");
        await QuiesceAsync(tablet, pc);
        foreach (var device in new[] { pc, tablet })
        {
            Assert.AreEqual("12:00", (await StoredAsync(device, patient)).NextAppointmentTime);
        }
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    // ================================================================== tres equipos al azar

    [TestMethod]
    public async Task ThreeDevices_RandomEditsAndSyncs_Converge_WithoutRepeatedUploads()
    {
        foreach (var seed in new[] { 7, 22 })
        {
            Cleanup();
            Setup();
            var (pc, tablet, patient) = await PcAndTabletAsync();
            var laptop = CreateDevice("laptop", seed: false);
            Assert.IsTrue((await laptop.Sync.SyncAsync()).Success);
            var devices = new[] { pc, tablet, laptop };
            var random = new Random(seed);
            string[] words = { "Penicilina", "Sulfas", "Ácido", "Agua", "Negadas" };

            for (int step = 0; step < 18; step++)
            {
                var device = devices[random.Next(devices.Length)];
                switch (random.Next(6))
                {
                    case 0:
                        await EditAsync(device, patient, p => p.Allergies = words[random.Next(words.Length)]);
                        break;
                    case 1:
                        await EditAsync(device, patient, p => p.Phone = $"55{random.Next(10_000_000, 99_999_999)}");
                        break;
                    case 2:
                        await AddSupplyAsync(device, patient, $"Insumo {step}");
                        break;
                    case 3:
                        await EditAsync(device, patient, p =>
                        {
                            if (p.ActiveSupplies.Count > 0) p.ActiveSupplies = p.ActiveSupplies.Skip(1).ToList();
                        });
                        break;
                    default:
                        Assert.IsTrue((await device.Sync.SyncAsync()).Success);
                        break;
                }
            }

            await QuiesceAsync(devices);
            var meaning = await MeaningAsync(pc, patient);
            Assert.AreEqual(meaning, await MeaningAsync(tablet, patient), $"Semilla {seed}: la tablet no quedó igual");
            Assert.AreEqual(meaning, await MeaningAsync(laptop, patient), $"Semilla {seed}: la laptop no quedó igual");
            await AssertNoMoreUploadsAsync(devices);
        }
    }

    // ================================================================== la copia abierta (paso 8.21)

    [TestMethod]
    public async Task AMergeIntoAnOpenCopy_IsNotClobberedByItsNextSave_AndRowsFromBothDevicesSurvive()
    {
        var (pc, tablet, patient) = await PcAndTabletAsync();

        // La PC tiene el expediente abierto (su copia, con su foto)
        var copy = await OpenAsync(pc, patient);

        // La tablet agrega un renglón y cambia las alergias; la PC baja con la copia abierta
        await AddSupplyAsync(tablet, patient, "Alcohol 70%");
        await EditAsync(tablet, patient, p => p.Allergies = "Yodo");
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        CollectionAssert.DoesNotContain(SupplyNames(copy), "Alcohol 70%", "La copia abierta todavía no lo ve");

        // Desde la copia vieja, la PC agrega su renglón: se pone sobre la hoja de la base, no la reemplaza
        copy.ActiveSupplies = new List<PatientSupplyItem>(copy.ActiveSupplies) { new() { SupplyName = "Gasa estéril", Quantity = 2 } };
        await pc.Inventory.SavePatientAsync(copy);
        var stored = await StoredAsync(pc, patient);
        CollectionAssert.IsSubsetOf(new[] { "Alcohol 70%", "Gasa estéril" }, SupplyNames(stored));
        Assert.AreEqual("Yodo", stored.Allergies);
        Assert.IsFalse(PatientFieldStamps.Parse(stored.FieldStampsJson).Entries.Keys.Any(k => k.EndsWith("/quitado", StringComparison.Ordinal)),
            "El renglón que la copia no ve no cuenta como quitado");

        // La misma copia vuelve a editar la hoja: el renglón de la tablet sigue
        copy.ActiveSupplies = new List<PatientSupplyItem>(copy.ActiveSupplies) { new() { SupplyName = "Venda", Quantity = 1 } };
        await pc.Inventory.SavePatientAsync(copy);
        CollectionAssert.IsSubsetOf(new[] { "Alcohol 70%", "Gasa estéril", "Venda" }, SupplyNames(await StoredAsync(pc, patient)));

        await QuiesceAsync(pc, tablet);
        CollectionAssert.IsSubsetOf(new[] { "Alcohol 70%", "Gasa estéril", "Venda" }, SupplyNames(await StoredAsync(tablet, patient)));
        Assert.AreEqual(await MeaningAsync(pc, patient), await MeaningAsync(tablet, patient));
        await AssertNoMoreUploadsAsync(pc, tablet);
    }

    // ================================================================== otra PC sin actualizar

    [TestMethod]
    public async Task AnotherPcWithAnOlderContract_SeenInTheLast30Days_GetsTheWholeRowWarning()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        string Seen(int daysAgo) => TursoSyncService.FormatDate(DateTime.UtcNow.AddDays(-daysAgo));
        ExecuteCloud("INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc, Platform, HandoffProtocol, ContractRevision) VALUES " +
                     $"('pc-v138', 'PC de recepción', 'WindowsDesktop', '1.38.0', '{Seen(5)}', 'Windows', 1, 11), " +
                     $"('pc-v119', 'PC vieja', 'WindowsDesktop', '1.19.0', '{Seen(2)}', NULL, NULL, NULL), " +
                     $"('pc-v139', 'PC al día', 'WindowsDesktop', '1.39.0', '{Seen(1)}', 'Windows', 1, {SyncContract.Revision}), " +
                     $"('pc-retirada', 'PC que ya no se usa', 'WindowsDesktop', '1.38.0', '{Seen(40)}', 'Windows', 1, 11), " +
                     $"('tablet-1', 'Tablet', 'AndroidTablet', '0.3.0', '{Seen(1)}', 'Android', 1, 3)");

        var devices = await pc.Sync.GetDevicesAsync();
        Assert.IsTrue(devices.Single(d => d.DeviceId == "pc-v138").MergesAsWholeRow);
        StringAssert.Contains(devices.Single(d => d.DeviceId == "pc-v138").WholeRowMergeText, "fila completa");
        Assert.IsTrue(devices.Single(d => d.DeviceId == "pc-v119").MergesAsWholeRow, "Sin plataforma ni revisión: una PC v1.19–v1.26");
        Assert.IsFalse(devices.Single(d => d.DeviceId == "pc-v139").MergesAsWholeRow);
        Assert.IsFalse(devices.Single(d => d.DeviceId == "pc-retirada").MergesAsWholeRow, "No se ve hace más de 30 días");
        Assert.IsFalse(devices.Single(d => d.DeviceId == "tablet-1").MergesAsWholeRow, "Solo las PC con Windows");
        Assert.IsFalse(devices.Single(d => d.IsThisDevice).MergesAsWholeRow);
    }
}
