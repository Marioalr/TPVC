using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

/// <summary>Paso 8.23: cómo terminó la sincronización rápida antes de cobrar o de agendar.</summary>
public enum QuickSyncOutcome
{
    /// <summary>La nube está apagada o sin configurar: no se preguntó nada.</summary>
    Off,

    /// <summary>No se intentó: sincronización en pausa (cuota, freno, token, otra nube) o sin ninguna sincronización buena todavía.</summary>
    Skipped,

    /// <summary>Terminó bien a tiempo: los datos locales están al día con la nube.</summary>
    Synced,

    /// <summary>Terminó con un error (sin red, por ejemplo): se sigue con los datos locales.</summary>
    Failed,

    /// <summary>No terminó en 3 s: sigue en segundo plano y se sigue con los datos locales.</summary>
    TimedOut
}

/// <summary>Paso 8.23: una sincronización normal que quien cobra o agenda espera 3 s como máximo (la da <see cref="TursoSyncService"/>).</summary>
public interface IQuickSync
{
    Task<QuickSyncOutcome> QuickSyncAsync(CancellationToken ct = default);
}

/// <summary>
/// Paso 8.23 (H2, H3, H11): lo que se revisa contra los datos de este equipo antes de agendar y al crear un expediente, la
/// sincronización rápida que va antes (con la nube activa) y el aviso de lo que llegó de otro equipo.
/// </summary>
public partial class InventoryService
{
    /// <summary>La sincronización rápida (la asigna TursoSyncService). Sin ella, cobrar y agendar no esperan nada.</summary>
    public IQuickSync? QuickSync { get; set; }

    /// <summary>
    /// Antes de cobrar y de agendar: con la nube activa, una sincronización que se espera 3 s como máximo; sin red o con la
    /// sincronización en pausa no espera. Nunca lanza: lo que sigue usa los datos locales de todos modos.
    /// </summary>
    public async Task<QuickSyncOutcome> QuickSyncAsync()
    {
        if (QuickSync == null) return QuickSyncOutcome.Off;
        try
        {
            return await QuickSync.QuickSyncAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"La sincronización rápida no pudo completarse: {ex.Message}");
            return QuickSyncOutcome.Failed;
        }
    }

    /// <summary>Las citas guardadas con que se empalmaría <paramref name="candidate"/> (<see cref="AppointmentRules"/>), por hora.</summary>
    public async Task<List<PatientAppointment>> FindOverlappingAppointmentsAsync(PatientAppointment candidate)
    {
        var day = candidate.Date.Date;
        var next = day.AddDays(1);
        await using var db = CreateContext();
        var sameDay = await db.Appointments.AsNoTracking()
            .Where(a => a.Date >= day && a.Date < next && a.Status != AppointmentRules.Cancelled)
            .ToListAsync();
        var rule = AppointmentRules.Of(candidate);
        var bySyncId = sameDay.ToDictionary(a => a.SyncId);
        return AppointmentRules.OverlapsWith(rule, sameDay.Where(a => a.Id != candidate.Id || candidate.Id == 0).Select(AppointmentRules.Of))
            .Select(a => bySyncId[a.SyncId])
            .ToList();
    }

    /// <summary>Los expedientes guardados que pueden ser <paramref name="candidate"/> (<see cref="DuplicatePatientRules"/>).</summary>
    public async Task<List<(PatientRecord Patient, DuplicatePatientRules.Match Match)>> FindDuplicatePatientsAsync(PatientRecord candidate)
    {
        var rule = DuplicatePatientRules.Of(candidate);
        if (!DuplicatePatientRules.Counts(rule)) return new List<(PatientRecord, DuplicatePatientRules.Match)>();
        var patients = await LoadDuplicateCandidatesAsync();
        var bySyncId = patients.ToDictionary(p => p.SyncId);
        return DuplicatePatientRules.DuplicatesOf(rule, patients.Where(p => p.Id != candidate.Id || candidate.Id == 0).Select(DuplicatePatientRules.Of))
            .Select(d => (bySyncId[d.Patient.SyncId], d.Match))
            .ToList();
    }

    /// <summary>Lo que la regla de duplicados necesita de cada expediente (sin las hojas ni las notas).</summary>
    private async Task<List<PatientRecord>> LoadDuplicateCandidatesAsync()
    {
        await using var db = CreateContext();
        var rows = await db.Patients.AsNoTracking()
            .Where(p => p.IsActive && p.MergedIntoSyncId == null)
            .Select(p => new { p.Id, p.SyncId, p.FullName, p.RecordNumber, p.BirthDate, p.Phone })
            .ToListAsync();
        return rows.Select(p => new PatientRecord
        {
            Id = p.Id, SyncId = p.SyncId, FullName = p.FullName, RecordNumber = p.RecordNumber, BirthDate = p.BirthDate, Phone = p.Phone
        }).ToList();
    }

    /// <summary>Paso 8.23: lo que una bajada trajo y choca con lo de este equipo (empalmes y posibles duplicados).</summary>
    public event Action<SyncArrivals>? OnSyncArrivals;

    /// <summary>La sincronización avisa lo que llegó (en el hilo de la interfaz).</summary>
    public void NotifySyncArrivals(SyncArrivals arrivals)
    {
        if (arrivals.IsEmpty || OnSyncArrivals == null) return;
        foreach (var handler in OnSyncArrivals.GetInvocationList().Cast<Action<SyncArrivals>>())
        {
            try
            {
                handler(arrivals);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error avisando lo que llegó de otro equipo", ex);
            }
        }
    }
}
