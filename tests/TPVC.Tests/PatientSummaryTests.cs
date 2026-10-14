using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;
using TPVC.Tests.Fase6;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.22: el resumen derivado del expediente (la próxima cita en orden clínico y la última visita) y el sello de las
/// marcas por campo en la base de la PC: cada guardado marca solo lo que cambió, el resumen no se marca ni sube por sí solo.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PatientSummaryTests
{
    private static PatientSummaryRules.Appointment Appt(int day, string time, string status = "Programada", string reason = "Control", int id = 1) =>
        new(new Guid($"c0000000-0000-4000-8000-00000000000{id}"), new DateTime(2026, 10, 11).AddDays(day), time, status, reason, string.Empty);

    [TestMethod]
    public void TheNextAppointment_FollowsClinicalOrder_NotTheOrderItWasSaved()
    {
        var today = new DateTime(2026, 10, 11);
        var appointments = new[]
        {
            Appt(10, "09:00", id: 1),
            Appt(3, "12:00", id: 2),
            Appt(1, "08:00", "Atendida", id: 3),
            Appt(2, "08:00", "Cancelada", id: 4),
            Appt(3, "10:30", reason: "Revisión", id: 5),
            Appt(-1, "10:00", id: 6)
        };
        var next = PatientSummaryRules.Next(appointments, today);
        Assert.AreEqual(today.AddDays(3), next.Date);
        Assert.AreEqual("10:30", next.Time);
        Assert.AreEqual("Revisión", next.Reason);

        Assert.AreEqual(today, PatientSummaryRules.Next(new[] { Appt(0, "07:00") }, today).Date, "Hoy todavía cuenta");
        Assert.AreEqual(PatientSummaryRules.NextAppointment.None, PatientSummaryRules.Next(new[] { Appt(-1, "07:00") }, today));

        // Misma fecha y hora: por SyncId, igual en todos los equipos
        var tie = PatientSummaryRules.Next(new[] { Appt(1, "09:00", reason: "B", id: 8), Appt(1, "09:00", reason: "A", id: 7) }, today);
        Assert.AreEqual("A", tie.Reason);
    }

    [TestMethod]
    public void TheAppointmentTime_IsReadTheSameWayInKotlin()
    {
        Assert.AreEqual(new TimeSpan(9, 5, 0), PatientSummaryRules.TimeOfDay("9:05"));
        Assert.AreEqual(new TimeSpan(16, 0, 0), PatientSummaryRules.TimeOfDay("16:00"));
        Assert.AreEqual(new TimeSpan(16, 0, 30), PatientSummaryRules.TimeOfDay("16:00:30"));
        Assert.AreEqual(new TimeSpan(9, 0, 0), PatientSummaryRules.TimeOfDay("09:00 AM"));
        Assert.AreEqual(new TimeSpan(21, 15, 0), PatientSummaryRules.TimeOfDay("9:15 p. m."));
        Assert.AreEqual(new TimeSpan(0, 30, 0), PatientSummaryRules.TimeOfDay("12:30 am"));
        Assert.AreEqual(new TimeSpan(12, 30, 0), PatientSummaryRules.TimeOfDay("12:30 PM"));
        Assert.AreEqual(PatientSummaryRules.DefaultTime, PatientSummaryRules.TimeOfDay("mediodía"));
        Assert.AreEqual(PatientSummaryRules.DefaultTime, PatientSummaryRules.TimeOfDay("25:00"));
        Assert.AreEqual(PatientSummaryRules.DefaultTime, PatientSummaryRules.TimeOfDay(null));
    }

    [TestMethod]
    public void TheLastVisit_IsTheLatestConsultation_OrNothingToCompute()
    {
        Assert.AreEqual(new DateTime(2026, 10, 9, 18, 30, 0),
            PatientSummaryRules.LastVisit(new[] { new DateTime(2026, 10, 1, 9, 0, 0), new DateTime(2026, 10, 9, 18, 30, 0), new DateTime(2026, 9, 1) }));
        Assert.IsNull(PatientSummaryRules.LastVisit(Array.Empty<DateTime>()));
    }

    // ================================================================== en la base

    private static PatientRecord Stored(Fase6Clinic clinic, int id)
    {
        using var db = new AppDbContext(clinic.Options);
        return db.Patients.AsNoTracking().Single(p => p.Id == id);
    }

    private static long? Seq(Fase6Clinic clinic, Guid syncId)
    {
        using var db = new AppDbContext(clinic.Options);
        return ChangeLogTestSupport.SeqOf(db, nameof(PatientRecord), syncId);
    }

    [TestMethod]
    public async Task BookingAndCancelling_RecomputeTheSummary_WithoutWritingThePatient()
    {
        using var clinic = new Fase6Clinic();
        var inventory = clinic.Inventory;
        var patient = await inventory.SavePatientAsync(new PatientRecord { FullName = "Paciente de Agenda", Phone = "5512345678" });
        var before = Stored(clinic, patient.Id);
        Assert.IsNull(before.NextAppointmentDate);
        var seq = Seq(clinic, patient.SyncId);
        var today = DateTime.Today;

        var later = await inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = patient.Id, Date = today.AddDays(10), Time = "09:00", Reason = "Control" });
        var sooner = await inventory.SaveAppointmentAsync(new PatientAppointment { PatientRecordId = patient.Id, Date = today.AddDays(4), Time = "11:00", Reason = "Estudios" });
        var stored = Stored(clinic, patient.Id);
        Assert.AreEqual(today.AddDays(4), stored.NextAppointmentDate, "La más próxima, aunque se guardó después");
        Assert.AreEqual("Estudios", stored.NextAppointmentReason);
        Assert.AreEqual(before.UpdatedAtUtc, stored.UpdatedAtUtc, "Agendar no escribe el expediente");
        Assert.AreEqual(before.FieldStampsJson, stored.FieldStampsJson);
        Assert.AreEqual(seq, Seq(clinic, patient.SyncId), "Recalcular el resumen no deja nada que subir");

        await inventory.UpdateAppointmentStatusAsync(sooner.Id, "Atendida");
        Assert.AreEqual(today.AddDays(10), Stored(clinic, patient.Id).NextAppointmentDate, "Una atendida ya no es la próxima");
        await inventory.UpdateAppointmentStatusAsync(later.Id, "Cancelada");
        stored = Stored(clinic, patient.Id);
        Assert.IsNull(stored.NextAppointmentDate, "Sin citas pendientes");
        Assert.AreEqual(string.Empty, stored.NextAppointmentTime);
        Assert.AreEqual(seq, Seq(clinic, patient.SyncId));
    }

    [TestMethod]
    public async Task TheLastVisit_FollowsTheConsultations_AndANoteStampsOnlyWhatItChanges()
    {
        using var clinic = new Fase6Clinic();
        var inventory = clinic.Inventory;
        var patient = (await inventory.GetPatientsAsync()).Single(p => p.RecordNumber == Fase6Scenarios.ScriptedRecord);
        var previous = Stored(clinic, patient.Id).LastVisit;

        var note = await inventory.AddConsultationNoteAsync(patient.Id, new PatientConsultation
        {
            Date = DateTime.Now.AddMinutes(-20), ChiefComplaint = "Tos", Diagnosis = "J00 Rinofaringitis", VitalSigns = "TA: 120/80"
        });
        var stored = Stored(clinic, patient.Id);
        Assert.AreEqual(note.Date, stored.LastVisit);
        var stamps = PatientFieldStamps.Parse(stored.FieldStampsJson);
        Assert.AreEqual(PatientFieldStamps.Format(stored.UpdatedAtUtc), stamps[nameof(PatientRecord.LastDiagnosis)], "El resumen de la nota se marca");
        Assert.AreEqual(PatientFieldStamps.Format(stored.UpdatedAtUtc), stamps[nameof(PatientRecord.VitalSigns)]);
        Assert.IsNull(stamps[nameof(PatientRecord.LastVisit)], "La última visita es derivada: no se marca");
        Assert.IsNull(stamps[nameof(PatientRecord.Phone)], "Lo que no cambió tampoco");
        Assert.AreEqual(stored.UpdatedAtUtc, stamps.RowStamp);

        // Se descarta la consulta: la nota se borra y la última visita vuelve a la consulta anterior
        await inventory.DeleteConsultationNoteAsync(patient.Id, note.Folio);
        Assert.AreEqual(previous, Stored(clinic, patient.Id).LastVisit);
    }

    [TestMethod]
    public async Task ASave_StampsOnlyTheEditedFields_AndKeepsUnknownKeys()
    {
        using var clinic = new Fase6Clinic();
        var inventory = clinic.Inventory;
        var patient = (await inventory.GetPatientsAsync()).Single(p => p.RecordNumber == Fase6Scenarios.ScriptedRecord);
        using (var db = new AppDbContext(clinic.Options))
        {
            db.ApplyingRemoteChanges = true;
            var row = db.Patients.Single(p => p.Id == patient.Id);
            row.FieldStampsJson = "{\"ColumnaNueva\":\"2026-01-01 00:00:00.0000000\"}";
            db.SaveChanges();
        }
        patient = (await inventory.GetPatientsAsync()).Single(p => p.Id == patient.Id);

        patient.Phone = "5511223344";
        patient.ActiveSupplies = new() { new PatientSupplyItem { SupplyName = "Gasa", Quantity = 1 } };
        await inventory.SavePatientAsync(patient);
        var stored = Stored(clinic, patient.Id);
        var stamps = PatientFieldStamps.Parse(stored.FieldStampsJson);
        var stamp = PatientFieldStamps.Format(stored.UpdatedAtUtc);
        Assert.AreEqual(stamp, stamps[nameof(PatientRecord.Phone)]);
        Assert.AreEqual(stamp, stamps[PatientSheetRows.SuppliesColumn], "Cambió qué renglones tiene la hoja");
        Assert.AreEqual(stamp, stamps[PatientFieldStamps.RowKey(PatientSheetRows.SuppliesColumn, stored.ActiveSupplies.Single().RowId!.Value)]);
        Assert.IsNull(stamps[nameof(PatientRecord.Allergies)]);
        Assert.AreEqual("2026-01-01 00:00:00.0000000", stamps["ColumnaNueva"], "Lo desconocido se queda");

        // Un guardado sin cambios no escribe nada (paso 8.21) y tampoco mueve las marcas
        await inventory.SavePatientAsync(patient);
        Assert.AreEqual(stored.FieldStampsJson, Stored(clinic, patient.Id).FieldStampsJson);
    }
}
