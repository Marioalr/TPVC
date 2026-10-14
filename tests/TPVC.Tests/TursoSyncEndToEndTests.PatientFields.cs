using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.21 con dos equipos: lo que la tablet escribe en un expediente (alergias, la próxima cita) sobrevive a que la PC
/// guarde una hoja desde la copia que tenía abierta antes de bajarlo. La nube sigue con la última escritura por fila: la
/// PC escribe sobre su fila ya bajada solo lo que cambió y sube la fila con las dos cosas.
/// </summary>
public partial class TursoSyncEndToEndTests
{
    private string? CloudPatientValue(Guid syncId, string column)
    {
        using var held = _server.Lock();
        using var cmd = _server.Connection.CreateCommand();
        cmd.CommandText = $"SELECT \"{column}\" FROM Patients WHERE SyncId = $id";
        cmd.Parameters.AddWithValue("$id", TursoSyncService.FormatGuid(syncId));
        return cmd.ExecuteScalar()?.ToString();
    }

    [TestMethod]
    public async Task AnEditFromTheTablet_SurvivesASheetSavedOnThePcFromTheCopyItHadOpen()
    {
        var pc = CreateDevice("pc", seed: true);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        var tablet = CreateDevice("tablet", seed: false);
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La PC tiene el expediente abierto (su copia en memoria, con su foto)
        var onPc = (await pc.Inventory.GetPatientsAsync()).Single(p => p.RecordNumber == "EXP-2026-002");

        // En la tablet: alergias nuevas y la próxima cita
        var onTablet = (await tablet.Inventory.GetPatientsAsync()).Single(p => p.SyncId == onPc.SyncId);
        onTablet.Allergies = "Penicilina (anafilaxia)";
        await tablet.Inventory.SavePatientAsync(onTablet);
        var date = DateTime.Today.AddDays(10);
        await tablet.Inventory.SaveAppointmentAsync(new PatientAppointment
        {
            PatientRecordId = onTablet.Id, Date = date, Time = "11:15", Reason = "Revisión de estudios"
        });
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);

        // La PC baja con la copia vieja abierta y guarda una hoja desde ella
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);
        Assert.AreNotEqual("Penicilina (anafilaxia)", onPc.Allergies, "La copia abierta es de antes de bajar");
        onPc.ActiveSupplies = new List<PatientSupplyItem>(onPc.ActiveSupplies) { new() { SupplyName = "Gasa estéril", Quantity = 2 } };
        await pc.Inventory.SavePatientAsync(onPc);
        Assert.AreEqual("Penicilina (anafilaxia)", onPc.Allergies, "La copia de la PC queda al día");
        Assert.AreEqual(date, onPc.NextAppointmentDate);
        Assert.IsTrue((await pc.Sync.SyncAsync()).Success);

        // En la nube están las dos cosas
        Assert.AreEqual("Penicilina (anafilaxia)", CloudPatientValue(onPc.SyncId, nameof(PatientRecord.Allergies)), "La edición de la tablet sobrevive");
        Assert.AreEqual("11:15", CloudPatientValue(onPc.SyncId, nameof(PatientRecord.NextAppointmentTime)));
        StringAssert.Contains(CloudPatientValue(onPc.SyncId, nameof(PatientRecord.ActiveSuppliesJson)), "Gasa estéril");

        // Y la tablet las ve al bajar
        Assert.IsTrue((await tablet.Sync.SyncAsync()).Success);
        var back = (await tablet.Inventory.GetPatientsAsync()).Single(p => p.SyncId == onPc.SyncId);
        Assert.AreEqual("Penicilina (anafilaxia)", back.Allergies);
        Assert.AreEqual(date, back.NextAppointmentDate);
        Assert.IsTrue(back.ActiveSupplies.Any(s => s.SupplyName == "Gasa estéril"));
    }
}
