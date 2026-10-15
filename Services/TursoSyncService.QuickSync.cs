using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

/// <summary>
/// Paso 8.23 (H2, H3, H11): la sincronización rápida antes de cobrar y de agendar, y la revisión de lo que llegó de otro
/// equipo (citas empalmadas y posibles expedientes duplicados) después de cada bajada.
/// <list type="bullet">
/// <item><b>Sincronización rápida.</b> Es la sincronización incremental de siempre (con nada que subir, una sola petición: la
/// foto de la bajada), que quien cobra o agenda espera <see cref="QuickSyncWait"/> como máximo. Si no termina, sigue en
/// segundo plano (no se cancela a la mitad) y el cobro o la cita siguen con los datos locales. No se intenta con la nube
/// apagada, con la sincronización en pausa (cuota agotada, freno de consumo, token rechazado, otra nube) ni antes de la
/// primera sincronización buena (la primera descarga es larga y la empieza el arranque). Comparte el candado de la
/// sincronización (nunca corre a la vez que otra) y dos seguidas comparten la misma; no deja nada que dispare otra.</item>
/// <item><b>Red que no responde.</b> Mientras una sincronización rápida que no terminó a tiempo siga esperando a la nube, el
/// folio del ticket o del expediente no espera otros 2 s: sale con la letra del equipo (paso 8.16). Así cobrar con una red
/// que no contesta tarda ~3 s, no 5.</item>
/// </list>
/// </summary>
public partial class TursoSyncService : IQuickSync
{
    /// <summary>Cuánto espera quien cobra o agenda a la sincronización rápida.</summary>
    public static readonly TimeSpan QuickSyncWait = TimeSpan.FromSeconds(3);

    /// <summary>Para las pruebas: otra espera.</summary>
    internal TimeSpan QuickSyncBudget { get; set; } = QuickSyncWait;

    private readonly object _quickSyncGate = new();
    private Task<TursoSyncResult>? _quickSync;

    // La sincronización rápida que no terminó a tiempo (mientras siga corriendo, la red no responde)
    private Task<TursoSyncResult>? _lateQuickSync;

    /// <summary>Una sincronización rápida no terminó a tiempo y sigue esperando a la nube.</summary>
    internal bool IsQuickSyncHanging => Volatile.Read(ref _lateQuickSync) is { IsCompleted: false };

    public async Task<QuickSyncOutcome> QuickSyncAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl) || string.IsNullOrWhiteSpace(settings.AuthToken))
            return QuickSyncOutcome.Off;
        // Pausa (cuota agotada, freno de consumo, token rechazado) u otra nube: no se pregunta a Turso
        if (PauseReason != TursoSyncPauseReason.None) return QuickSyncOutcome.Skipped;
        if (IdentityConflict != null && _identityConflictUrl == settings.DatabaseUrl) return QuickSyncOutcome.Skipped;
        // Sin ninguna sincronización buena, la siguiente sería la primera descarga (larga): la empieza el arranque, no un cobro
        if (settings.LastSyncUtc == null) return QuickSyncOutcome.Skipped;

        Task<TursoSyncResult> run;
        lock (_quickSyncGate)
        {
            // Dos seguidas (cobrar dos veces, agendar y cobrar) comparten la misma
            if (_quickSync is { IsCompleted: false } running) run = running;
            else run = _quickSync = Task.Run(() => RunExclusiveAsync(isBackgroundAutoSync: false, fullResync: false, CancellationToken.None, lockWait: QuickSyncBudget));
        }

        var waited = await Task.WhenAny(run, Task.Delay(QuickSyncBudget, ct)).ConfigureAwait(false);
        if (waited != run)
        {
            Volatile.Write(ref _lateQuickSync, run);
            AppLogger.Warn($"Turso: la sincronización rápida no terminó en {QuickSyncBudget.TotalSeconds:0.#} s; sigue en segundo plano y se usan los datos de este equipo.");
            return QuickSyncOutcome.TimedOut;
        }
        var result = await run.ConfigureAwait(false);
        return result.Success ? QuickSyncOutcome.Synced : QuickSyncOutcome.Failed;
    }

    // =========================================================================
    // LO QUE LLEGÓ DE OTRO EQUIPO
    // =========================================================================

    // Los expedientes y las citas que la bajada de esta sincronización creó o cambió aquí
    private readonly object _arrivalsGate = new();
    private readonly HashSet<Guid> _arrivedPatients = new();
    private readonly HashSet<Guid> _arrivedAppointments = new();
    private SyncArrivals _arrivals = SyncArrivals.None;

    private void ResetArrivals()
    {
        lock (_arrivalsGate)
        {
            _arrivedPatients.Clear();
            _arrivedAppointments.Clear();
            _arrivals = SyncArrivals.None;
        }
    }

    /// <summary>Anota las filas que una página de la bajada creó o cambió (solo expedientes y citas).</summary>
    private void NoteArrivals(Type type, IReadOnlyCollection<Guid> syncIds)
    {
        if (syncIds.Count == 0) return;
        var target = type == typeof(PatientRecord) ? _arrivedPatients : type == typeof(PatientAppointment) ? _arrivedAppointments : null;
        if (target == null) return;
        lock (_arrivalsGate) target.UnionWith(syncIds);
    }

    /// <summary>
    /// Después de bajar: las citas que llegaron (de hoy en adelante, sin cancelar) contra las de su mismo día, y los expedientes
    /// que llegaron contra los de este equipo. Solo lee la base local.
    /// </summary>
    private async Task<SyncArrivals> CheckArrivalsAsync(CancellationToken ct)
    {
        HashSet<Guid> patients, appointments;
        lock (_arrivalsGate)
        {
            patients = new HashSet<Guid>(_arrivedPatients);
            appointments = new HashSet<Guid>(_arrivedAppointments);
        }
        if (patients.Count == 0 && appointments.Count == 0) return SyncArrivals.None;

        using var db = _contextFactory.CreateDbContext();
        var overlaps = new List<AppointmentOverlap>();
        if (appointments.Count > 0)
        {
            var today = ClinicClock.Today;
            var ids = appointments.ToList();
            var dates = (await db.Appointments.AsNoTracking()
                    .Where(a => ids.Contains(a.SyncId) && a.Date >= today && a.Status != AppointmentRules.Cancelled)
                    .Select(a => a.Date)
                    .ToListAsync(ct))
                .Select(d => d.Date)
                .ToHashSet();
            if (dates.Count > 0)
            {
                var from = dates.Min();
                var to = dates.Max().AddDays(1);
                var sameDays = (await db.Appointments.AsNoTracking()
                        .Where(a => a.Date >= from && a.Date < to && a.Status != AppointmentRules.Cancelled)
                        .ToListAsync(ct))
                    .Where(a => dates.Contains(a.Date.Date))
                    .ToList();
                var bySyncId = sameDays.ToDictionary(a => a.SyncId);
                overlaps.AddRange(AppointmentRules.Pairs(sameDays.Select(AppointmentRules.Of), appointments)
                    .Select(p => new AppointmentOverlap(bySyncId[p.First.SyncId], bySyncId[p.Second.SyncId])));
            }
        }

        var duplicates = new List<DuplicatePatientPair>();
        if (patients.Count > 0)
        {
            var rows = await db.Patients.AsNoTracking()
                .Where(p => p.IsActive && p.MergedIntoSyncId == null)
                .Select(p => new { p.Id, p.SyncId, p.FullName, p.RecordNumber, p.BirthDate, p.Phone })
                .ToListAsync(ct);
            var bySyncId = rows.ToDictionary(p => p.SyncId, p => new PatientRecord
            {
                Id = p.Id, SyncId = p.SyncId, FullName = p.FullName, RecordNumber = p.RecordNumber, BirthDate = p.BirthDate, Phone = p.Phone
            });
            duplicates.AddRange(DuplicatePatientRules.Pairs(bySyncId.Values.Select(DuplicatePatientRules.Of), patients)
                .Select(p => new DuplicatePatientPair(bySyncId[p.First.SyncId], bySyncId[p.Second.SyncId], p.Match)));
        }

        if (overlaps.Count > 0) AppLogger.Info($"Turso: {overlaps.Count} empalmes de citas con lo que llegó de otro equipo.");
        if (duplicates.Count > 0) AppLogger.Info($"Turso: {duplicates.Count} posibles expedientes duplicados con lo que llegó de otro equipo.");
        return overlaps.Count == 0 && duplicates.Count == 0 ? SyncArrivals.None : new SyncArrivals(overlaps, duplicates);
    }
}
