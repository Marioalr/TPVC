using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
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

    // La sincronización que no dejó terminar a la rápida a tiempo: la suya, o la que ocupaba el candado (mientras siga
    // corriendo, la red no responde)
    private Task? _lateQuickSync;

    /// <summary>
    /// La sincronización que tiene el candado ahora (la publica <see cref="RunExclusiveAsync"/> al tomarlo y la termina al
    /// soltarlo); null si no hay ninguna.
    /// </summary>
    private Task? _runInFlight;

    /// <summary>
    /// Una sincronización rápida no terminó a tiempo y la que la detuvo (la suya o una automática que tenía el candado y
    /// esperaba a la red) sigue corriendo.
    /// </summary>
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
            MarkLate(run);
            AppLogger.Warn($"Turso: la sincronización rápida no terminó en {QuickSyncBudget.TotalSeconds:0.#} s; sigue en segundo plano y se usan los datos de este equipo.");
            return QuickSyncOutcome.TimedOut;
        }
        var result = await run.ConfigureAwait(false);
        if (result.Success) return QuickSyncOutcome.Synced;

        // No consiguió el candado en su tiempo: otra sincronización (la automática) lo tiene y sigue esperando a la red. El
        // folio tampoco la espera mientras siga corriendo
        if (Volatile.Read(ref _runInFlight) is { IsCompleted: false })
        {
            MarkLate(run);
            AppLogger.Warn("Turso: otra sincronización tiene ocupada la nube; la rápida no alcanzó a correr y se usan los datos de este equipo.");
            return QuickSyncOutcome.TimedOut;
        }
        return QuickSyncOutcome.Failed;
    }

    /// <summary>
    /// La rápida no terminó a tiempo: mientras sigan corriendo ella o la que tiene el candado (la que la detuvo), la red no
    /// responde. Las dos, porque al vencer la espera la rápida puede soltar su turno un instante después.
    /// </summary>
    private void MarkLate(Task run)
    {
        var busy = Volatile.Read(ref _runInFlight);
        Task late = busy is { IsCompleted: false } && !ReferenceEquals(busy, run) ? Task.WhenAll(run, busy) : run;
        Volatile.Write(ref _lateQuickSync, late);
    }

    // =========================================================================
    // HORAS DE CITA ESCRITAS A MANO (v1.7–v1.13)
    // =========================================================================

    // Citas que la normalización cambió en esta sincronización (las pantallas recargan como después de bajar)
    private int _normalizedTimes;

    /// <summary>Para las pruebas: la pasada de las horas (por omisión, <see cref="AppointmentTimeMigration"/>).</summary>
    internal Func<AppDbContext, int> NormalizeAppointmentTimes { get; set; } = AppointmentTimeMigration.Run;

    /// <summary>
    /// Una sola vez por base (<see cref="AppointmentTimeMigration"/>), dentro de la sincronización y después de bajar: las filas
    /// ya están al día con la nube, así que reescribir una no deshace lo que otro equipo cambió. Lo cambiado sube enseguida.
    /// Nunca detiene la sincronización: si falla, sigue sin ella, no se marca como hecha y esta versión no la repite (una
    /// versión nueva sí).
    /// </summary>
    private async Task<(int Pushed, int RowsRead)> NormalizeAppointmentTimesAsync(TursoClient client, string deviceId, CancellationToken ct)
    {
        int changed;
        using (var db = _contextFactory.CreateDbContext())
        {
            if (await DbMeta.GetAsync(db, AppointmentTimeMigration.DoneKey, ct) == "1") return (0, 0);
            if (await DbMeta.GetAsync(db, AppointmentTimeMigration.FailedVersionKey, ct) == AppVersionText) return (0, 0);
            try
            {
                changed = NormalizeAppointmentTimes(db);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Warn($"Turso: no se pudieron normalizar las horas de las citas; la sincronización sigue sin hacerlo y esta versión no lo vuelve a intentar: {ex.Message}");
                try
                {
                    await DbMeta.SetAsync(db, AppointmentTimeMigration.FailedVersionKey, AppVersionText, ct);
                }
                catch (Exception metaEx)
                {
                    AppLogger.Warn($"Turso: tampoco se pudo anotar el fallo de la normalización: {metaEx.Message}");
                }
                return (0, 0);
            }
        }
        if (changed == 0) return (0, 0);
        Interlocked.Add(ref _normalizedTimes, changed);
        return await PushLocalChangesAsync(client, deviceId, fullResync: false, ct);
    }

    // =========================================================================
    // LO QUE LLEGÓ DE OTRO EQUIPO
    // =========================================================================

    // Los expedientes y las citas que la bajada creó o cambió aquí y que todavía no se revisan: se quedan hasta que una
    // sincronización llega a revisarlas (si una se corta después de aplicarlas, ya pasaron los cursores y no vuelven a bajar)
    private readonly object _arrivalsGate = new();
    private readonly HashSet<Guid> _arrivedPatients = new();
    private readonly HashSet<Guid> _arrivedAppointments = new();

    // Productos, servicios y categorías que llegaron (la tablet edita el catálogo desde el 2026-10-22)
    private readonly HashSet<Guid> _arrivedCatalog = new();
    private SyncArrivals _arrivals = SyncArrivals.None;

    // Esta sincronización revisa lo que llega (no en la primera descarga de una base: ahí llega todo)
    private volatile bool _checkingArrivals;

    private void ResetArrivals()
    {
        lock (_arrivalsGate)
        {
            _arrivedPatients.Clear();
            _arrivedAppointments.Clear();
            _arrivedCatalog.Clear();
            _arrivals = SyncArrivals.None;
        }
    }

    /// <summary>Lo revisado en la última sincronización buena, para su resultado; se entrega una vez.</summary>
    private SyncArrivals TakeArrivals()
    {
        lock (_arrivalsGate)
        {
            var arrivals = _arrivals;
            _arrivals = SyncArrivals.None;
            return arrivals;
        }
    }

    /// <summary>
    /// Anota las filas que una página de la bajada creó o cambió (expedientes, citas y el catálogo: productos, servicios y
    /// categorías), solo si se revisan.
    /// </summary>
    private void NoteArrivals(Type type, IReadOnlyCollection<Guid> syncIds)
    {
        if (syncIds.Count == 0 || !_checkingArrivals) return;
        var target = type == typeof(PatientRecord) ? _arrivedPatients
            : type == typeof(PatientAppointment) ? _arrivedAppointments
            : type == typeof(Product) || type == typeof(ClinicalService) || type == typeof(Category) ? _arrivedCatalog
            : null;
        if (target == null) return;
        lock (_arrivalsGate) target.UnionWith(syncIds);
    }

    /// <summary>
    /// Revisa lo que llegó y lo deja en los avisos de este equipo (<see cref="SyncNotices"/>, se quedan hasta que el médico
    /// los descarta); solo entonces se olvidan esas filas. Si la sincronización se corta antes, la siguiente que revise las
    /// incluye.
    /// </summary>
    private async Task CheckAndKeepArrivalsAsync(CancellationToken ct)
    {
        HashSet<Guid> patients, appointments, catalog;
        lock (_arrivalsGate)
        {
            patients = new HashSet<Guid>(_arrivedPatients);
            appointments = new HashSet<Guid>(_arrivedAppointments);
            catalog = new HashSet<Guid>(_arrivedCatalog);
        }
        var arrivals = await CheckArrivalsAsync(patients, appointments, catalog, ct);
        if (await SyncNotices.AddAsync(_contextFactory, arrivals, ct)) RunOnUiThread(() => _inventoryService?.NotifySyncNoticesChanged());
        lock (_arrivalsGate)
        {
            _arrivedPatients.ExceptWith(patients);
            _arrivedAppointments.ExceptWith(appointments);
            _arrivedCatalog.ExceptWith(catalog);
            _arrivals = arrivals;
        }
    }

    /// <summary>
    /// Después de bajar: las citas que llegaron (de hoy en adelante, sin cancelar) contra las de su mismo día, los expedientes
    /// que llegaron contra los de este equipo y los productos, servicios y categorías que llegaron contra su catálogo. Solo lee
    /// la base local.
    /// </summary>
    private async Task<SyncArrivals> CheckArrivalsAsync(IReadOnlySet<Guid> patients, IReadOnlySet<Guid> appointments,
        IReadOnlySet<Guid> catalog, CancellationToken ct)
    {
        if (patients.Count == 0 && appointments.Count == 0 && catalog.Count == 0) return SyncArrivals.None;

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

        // El catálogo (2026-10-22): lo que llegó contra todo el catálogo de este equipo (CatalogDuplicateRules)
        var duplicateCatalog = new List<DuplicateCatalogPair>();
        if (catalog.Count > 0)
        {
            var items = await SyncNotices.LoadCatalogItemsAsync(db, null, ct);
            duplicateCatalog.AddRange(CatalogDuplicateRules.Pairs(items.Values, catalog)
                .Select(p => new DuplicateCatalogPair(p.First, p.Second, p.Match)));
        }

        if (overlaps.Count > 0) AppLogger.Info($"Turso: {overlaps.Count} empalmes de citas con lo que llegó de otro equipo.");
        if (duplicates.Count > 0) AppLogger.Info($"Turso: {duplicates.Count} posibles expedientes duplicados con lo que llegó de otro equipo.");
        if (duplicateCatalog.Count > 0) AppLogger.Info($"Turso: {duplicateCatalog.Count} posibles registros repetidos del catálogo con lo que llegó de otro equipo.");
        return overlaps.Count == 0 && duplicates.Count == 0 && duplicateCatalog.Count == 0
            ? SyncArrivals.None
            : new SyncArrivals(overlaps, duplicates, duplicateCatalog);
    }
}
