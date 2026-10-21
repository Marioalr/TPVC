using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TPVC.Models;
using TPVC.Services;
using TPVC.Tests.Fase6;
using TPVC.ViewModels.Patients;

namespace TPVC.Tests;

/// <summary>
/// Revisión 17 (unidades por paquete): un equipo anterior ignora el <c>IsIndividualUnits</c> de los cargos de la copia y
/// cobraría cada pieza suelta como un paquete completo. «Enviar a…» lo avisa junto al equipo y no le manda una consulta cuya
/// cuenta tiene alguna (como <see cref="SyncContract.ActiveIngredientRevision"/> con la receta).
/// </summary>
[TestClass]
[DoNotParallelize]
public class IndividualUnitsHandoffTests
{
    [TestMethod]
    public void AnOlderDevice_IsMarked_AndAnAccountWithLoosePiecesIsNotSentToIt()
    {
        static Dictionary<string, object?> Heartbeat(string id, int? revision) => new()
        {
            ["DeviceId"] = id, ["DeviceName"] = id, ["DeviceRole"] = nameof(DeviceRole.WindowsDesktop), ["AppVersion"] = "1.46.0",
            ["Platform"] = "Windows", ["LastSeenUtc"] = TursoSyncService.FormatDate(DateTime.UtcNow.AddMinutes(-1)), ["HandoffProtocol"] = 1L,
            ["ContractRevision"] = revision is int r ? (long)r : null
        };
        var devices = ConsultationHandoffService.MergeDevices(new[]
        {
            Heartbeat("pc-v146", SyncContract.IndividualUnitsRevision - 1),
            Heartbeat("pc-al-dia", SyncContract.IndividualUnitsRevision),
            Heartbeat("pc-sin-revision", null),
            Heartbeat("esta-pc", SyncContract.IndividualUnitsRevision - 1),
        }, Array.Empty<Dictionary<string, object?>>(), primaryDeviceId: "esta-pc", DateTime.UtcNow, thisDeviceId: "esta-pc");
        LinkedDevice Device(string id) => devices.Single(d => d.DeviceId == id);

        Assert.IsTrue(Device("pc-v146").IgnoresIndividualUnits, "Revisión 16: anterior a IndividualUnitsRevision");
        Assert.IsFalse(Device("pc-v146").IgnoresActiveIngredient, "La revisión 16 sí conoce la receta de hoy");
        StringAssert.Contains(Device("pc-v146").IndividualUnitsText, "piezas sueltas de un paquete");
        Assert.IsTrue(Device("pc-sin-revision").IgnoresIndividualUnits, "Sin revisión: una versión anterior");
        Assert.IsFalse(Device("pc-al-dia").IgnoresIndividualUnits);
        Assert.AreEqual(string.Empty, Device("pc-al-dia").IndividualUnitsText);
        Assert.IsFalse(Device("esta-pc").IgnoresIndividualUnits, "Este equipo no");

        var package = new ConsultationProductItem { ProductId = 15, Name = "Gasa", Quantity = 1, UnitPrice = 35m };
        var pieces = new ConsultationProductItem { ProductId = 15, Name = "Gasa", Quantity = 3, UnitPrice = 3.5m, IsIndividualUnits = true };
        StringAssert.Contains(ConsultationHandoffViewModel.IndividualUnitsBlock(Device("pc-v146"), new[] { package, pieces }), "No se puede enviar a pc-v146");
        Assert.IsNull(ConsultationHandoffViewModel.IndividualUnitsBlock(Device("pc-v146"), new[] { package }), "Sin piezas sueltas, sí se envía");
        Assert.IsNull(ConsultationHandoffViewModel.IndividualUnitsBlock(Device("pc-al-dia"), new[] { pieces }));
        Assert.IsNull(ConsultationHandoffViewModel.IndividualUnitsBlock(Device("pc-v146"), null));
    }

    [TestMethod]
    public async Task SendingToARevision16Device_WithLoosePiecesInTheAccount_IsBlocked_AndTheConsultationStaysHere()
    {
        using var pc = new Fase6Clinic();
        await pc.ConnectCloudAsync();
        using var tablet = new Fase6Clinic(seed: false);
        await tablet.ConnectCloudAsync(pc.Cloud, "device-tablet-sala", "Tablet de la sala");
        // La tablet todavía tiene una versión de la revisión 16
        using (pc.Cloud!.Lock())
        using (var cmd = pc.Cloud.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE RegisteredDevices SET ContractRevision = 16 WHERE DeviceId = 'device-tablet-sala'";
            Assert.AreEqual(1, cmd.ExecuteNonQuery());
        }
        Assert.IsTrue((await pc.Sync!.SyncAsync()).Success);

        var consultations = new ConsultationService(seedDemoPatients: false);
        var records = await pc.OpenAsync(consultations);
        records.OpenPatientChart(Fase6Scenarios.ScriptedPatient(records));
        records.ChartPrimaryAction();
        Assert.IsNotNull(consultations.CurrentConsultation, "La consulta empezó");

        // Un insumo de un servicio, cobrado por pieza de un paquete de 10
        var product = records.Catalog.AvailableProducts.First(p => p.Id > 0);
        Assert.IsTrue(consultations.AddCartItemToActiveConsultation(product.Id, product.Code, product.Name, product.GenericName,
            product.Presentation, "Piezas ind.", 3.5m, 3, isIndividualUnits: true));

        await records.Handoff.OpenSendConsultationSheetAsync();
        var target = records.Handoff.HandoffDevices.Single(d => d.DeviceId == "device-tablet-sala");
        Assert.IsTrue(target.IsAvailable, target.StatusText);
        Assert.IsTrue(target.IgnoresIndividualUnits, "La hoja lo avisa junto al equipo");
        Assert.IsFalse(target.IgnoresActiveIngredient);
        records.Handoff.SelectedHandoffDevice = target;
        await records.Handoff.SendConsultationToSelectedDeviceAsync();

        Assert.IsFalse(records.Handoff.IsConsultationHandedOff, "No se envió");
        Assert.IsTrue(records.Handoff.IsConsultationEditable);
        StringAssert.Contains(records.Handoff.HandoffSheetError, "piezas sueltas de un paquete");
        using (pc.Cloud.Lock())
        using (var cmd = pc.Cloud.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM ConsultationHandoffs";
            Assert.AreEqual(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        records.Dispose();
    }
}
