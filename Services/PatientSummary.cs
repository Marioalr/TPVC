using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services.Rules;

// El SQL dinámico de este archivo solo usa nombres de columnas del modelo de EF; los datos van como parámetros.
#pragma warning disable EF1002, EF1003

namespace TPVC.Services;

/// <summary>
/// Paso 8.22: escribe en la base el resumen derivado del expediente (<see cref="PatientSummaryRules"/>: la próxima cita en
/// orden clínico y la última visita). Corre después de cada guardado que toca un expediente, una consulta o una cita
/// (<c>AppDbContext.SaveChanges</c>, también al aplicar lo que bajó de la nube) y completo al abrir la base
/// (<c>DatabaseInitializer.EnsureSchema</c>, así el día que pasó también cuenta). Escribe con SQL directo solo esas columnas:
/// no pone marca ni deja nada en el registro de cambios (los triggers no las vigilan), así que recalcular nunca sube nada;
/// el valor viaja como información con la siguiente subida de la fila.
/// </summary>
public static class PatientSummary
{
    /// <summary>Los expedientes cuyo resumen puede cambiar con lo que el contexto está por guardar.</summary>
    public sealed class Pending
    {
        internal readonly HashSet<int> Ids = new();
        internal readonly List<PatientRecord> Added = new();

        public bool IsEmpty => Ids.Count == 0 && Added.Count == 0;
    }

    /// <summary>Antes de guardar: qué expedientes tocan las altas, cambios y borrados de expedientes, consultas y citas.</summary>
    public static Pending Collect(AppDbContext db)
    {
        var pending = new Pending();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            switch (entry.Entity)
            {
                case PatientRecord patient when entry.State == EntityState.Added:
                    pending.Added.Add(patient);
                    break;
                case PatientRecord patient when entry.State == EntityState.Modified:
                    pending.Ids.Add(patient.Id);
                    break;
                case PatientConsultation or PatientAppointment:
                    var property = entry.Property(nameof(PatientConsultation.PatientRecordId));
                    if (property.CurrentValue is int current && current > 0) pending.Ids.Add(current);
                    if (property.OriginalValue is int original && original > 0) pending.Ids.Add(original);
                    break;
            }
        }
        return pending;
    }

    /// <summary>Después de guardar: recalcula los expedientes de <paramref name="pending"/>. Un error se anota y no detiene el guardado.</summary>
    public static void Apply(AppDbContext db, Pending pending)
    {
        if (pending.IsEmpty) return;
        foreach (var patient in pending.Added)
        {
            if (patient.Id > 0) pending.Ids.Add(patient.Id);
        }
        if (pending.Ids.Count == 0) return;
        try
        {
            Recompute(db, pending.Ids);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo recalcular el resumen de {pending.Ids.Count} expedientes: {ex.Message}");
        }
    }

    /// <summary>
    /// Recalcula el resumen de los expedientes dados (todos con null) con "hoy" del consultorio y escribe solo los que
    /// cambiaron. Devuelve cuántos escribió.
    /// </summary>
    public static int Recompute(AppDbContext db, IReadOnlyCollection<int>? patientIds = null)
    {
        var today = ClinicClock.Today;
        var ids = patientIds?.ToList();

        var patients = (ids == null ? db.Patients.AsNoTracking() : db.Patients.AsNoTracking().Where(p => ids.Contains(p.Id)))
            .Select(p => new
            {
                p.Id, p.LastVisit, p.NextAppointmentDate, p.NextAppointmentTime, p.NextAppointmentReason, p.NextAppointmentNotes
            })
            .ToList();
        if (patients.Count == 0) return 0;

        var appointmentsQuery = db.Appointments.AsNoTracking().Where(a => a.Date >= today);
        if (ids != null) appointmentsQuery = appointmentsQuery.Where(a => ids.Contains(a.PatientRecordId));
        var appointments = appointmentsQuery
            .Select(a => new { a.PatientRecordId, a.SyncId, a.Date, a.Time, a.Status, a.Reason, a.Notes })
            .ToList()
            .ToLookup(a => a.PatientRecordId, a => new PatientSummaryRules.Appointment(a.SyncId, a.Date, a.Time, a.Status, a.Reason, a.Notes));

        var consultationsQuery = db.PatientConsultations.AsNoTracking();
        if (ids != null) consultationsQuery = consultationsQuery.Where(c => ids.Contains(c.PatientRecordId));
        var visits = consultationsQuery
            .Select(c => new { c.PatientRecordId, c.Date })
            .ToList()
            .ToLookup(c => c.PatientRecordId, c => c.Date);

        int written = 0;
        foreach (var p in patients)
        {
            var next = PatientSummaryRules.Next(appointments[p.Id], today);
            var lastVisit = PatientSummaryRules.LastVisit(visits[p.Id]) ?? p.LastVisit;
            if (next.Date == p.NextAppointmentDate && next.Time == (p.NextAppointmentTime ?? string.Empty)
                && next.Reason == (p.NextAppointmentReason ?? string.Empty) && next.Notes == (p.NextAppointmentNotes ?? string.Empty)
                && lastVisit == p.LastVisit)
            {
                continue;
            }

            db.Database.ExecuteSqlRaw(
                $"UPDATE \"Patients\" SET \"{nameof(PatientRecord.NextAppointmentDate)}\" = @date, \"{nameof(PatientRecord.NextAppointmentTime)}\" = @time, " +
                $"\"{nameof(PatientRecord.NextAppointmentReason)}\" = @reason, \"{nameof(PatientRecord.NextAppointmentNotes)}\" = @notes, " +
                $"\"{nameof(PatientRecord.LastVisit)}\" = @visit WHERE \"Id\" = @id;",
                new SqliteParameter("@date", (object?)next.Date ?? DBNull.Value),
                new SqliteParameter("@time", next.Time),
                new SqliteParameter("@reason", next.Reason),
                new SqliteParameter("@notes", next.Notes),
                new SqliteParameter("@visit", lastVisit),
                new SqliteParameter("@id", p.Id));
            written++;
        }
        return written;
    }
}
