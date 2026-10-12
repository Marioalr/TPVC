using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;

namespace TPVC.Services.Folios;

/// <summary>Lo que la pantalla le dice a la regularización (lo implementa la ventana principal; las pruebas, uno falso).</summary>
public interface IFolioRegularizationHost
{
    /// <summary>Consulta en curso, productos en la caja o el alta o edición de un paciente abierta: ahora no.</summary>
    bool IsBusy { get; }

    /// <summary>La copia en memoria del paciente se puede cambiar por la de la base (no está abierta en su ficha ni en una consulta).</summary>
    bool CanReplacePatient(int patientId);
}

public enum FolioRegularizationOutcome
{
    /// <summary>Se regularizó todo lo que había (o no había nada): no vuelve a correr hoy.</summary>
    Done,

    /// <summary>Ya corrió hoy.</summary>
    AlreadyToday,

    /// <summary>Sin la nube activa y verificada.</summary>
    Disabled,

    /// <summary>Solo la PC principal regulariza.</summary>
    NotPrimary,

    /// <summary>Consulta, venta o formulario abiertos (o empezó uno a la mitad): sigue en la siguiente oportunidad.</summary>
    Busy,

    /// <summary>Esta PC todavía no sube todo.</summary>
    PendingHere,

    /// <summary>Otro equipo reporta cambios sin subir: podrían ser ediciones de esos mismos registros.</summary>
    PendingElsewhere,

    /// <summary>Sin conexión al tomar un número.</summary>
    Offline,

    /// <summary>Quedaron pacientes abiertos en pantalla: se intentan en la siguiente oportunidad.</summary>
    Partial
}

public sealed record FolioRegularizationResult(FolioRegularizationOutcome Outcome, int Patients = 0, int Sales = 0)
{
    public int Total => Patients + Sales;
}

/// <summary>
/// Paso 8.17 (respuesta 1 del médico): una vez al día, la PC principal da el número normal a los expedientes y tickets hechos
/// sin conexión (EXP-2026-T1-001 → el siguiente de EXP-2026 en FolioCounters) y guarda el folio con letra en
/// <c>OriginalFolio</c>, que la búsqueda también compara. Corre después de una sincronización buena, solo con la nube al
/// día (nada pendiente aquí ni en otro equipo) y sin consulta, venta ni formulario abiertos. Si un registro ya regularizado
/// vuelve con su folio con letra (otro equipo lo editó sin conexión), recibe el mismo número que ya tenía. Sin WPF.
/// </summary>
public sealed class FolioRegularizationService
{
    /// <summary>La clave de DbMeta con el día (del consultorio) de la última regularización completa.</summary>
    public const string DoneDayKey = "FolioRegularizedDay";

    /// <summary>Prefijo de DbMeta con el número que ya recibió cada folio con letra.</summary>
    public const string MapPrefix = "FolioMap:";

    /// <summary>Un equipo con cambios pendientes que no se ve en este tiempo aparece en el aviso.</summary>
    public static readonly TimeSpan StaleDevice = TimeSpan.FromDays(7);

    /// <summary>Cada cuánto se vuelve a preguntar por los equipos cuando alguno tenía cambios pendientes.</summary>
    public static readonly TimeSpan DeviceRecheck = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly TursoSyncService _sync;
    private int _running;
    private DateTimeOffset? _waitingForDevicesUntil;

    public FolioRegularizationService(IDbContextFactory<AppDbContext> contextFactory, TursoSyncService sync, TimeProvider? clock = null)
    {
        _contextFactory = contextFactory;
        _sync = sync;
        Clock = clock ?? TimeProvider.System;
        _sync.SyncCompleted += result =>
        {
            if (!result.Success || !AutoRun) return;
            _ = Task.Run(async () =>
            {
                try { await RunIfDueAsync(); }
                catch (Exception ex) { AppLogger.Warn($"Folios: la regularización no pudo completarse: {ex.Message}"); }
            });
        };
    }

    public TimeProvider Clock { get; set; }

    /// <summary>La pantalla (consulta, caja, formulario); sin ella se supone libre.</summary>
    public IFolioRegularizationHost? Host { get; set; }

    /// <summary>Correr sola después de cada sincronización buena (la app la enciende; las pruebas la llaman a mano).</summary>
    public bool AutoRun { get; set; }

    /// <summary>Equipos con cambios sin subir que no se ven hace 7 días o más (para el aviso de Equipos vinculados).</summary>
    public IReadOnlyList<LinkedDevice> StaleDevices { get; private set; } = Array.Empty<LinkedDevice>();

    public FolioRegularizationResult? LastResult { get; private set; }

    public event Action<FolioRegularizationResult>? Completed;

    private string Today => Clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<FolioRegularizationResult> RunIfDueAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return new(FolioRegularizationOutcome.Busy);
        try
        {
            var result = await RunCoreAsync(ct);
            LastResult = result;
            Completed?.Invoke(result);
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<FolioRegularizationResult> RunCoreAsync(CancellationToken ct)
    {
        using (var db = _contextFactory.CreateDbContext())
        {
            if (await DbMeta.GetAsync(db, DoneDayKey, ct) == Today) return new(FolioRegularizationOutcome.AlreadyToday);
        }
        if (!await _sync.IsFolioCounterEnabledAsync(ct)) return new(FolioRegularizationOutcome.Disabled);
        if (!_sync.IsPrimaryOnCurrentCloud) return new(FolioRegularizationOutcome.NotPrimary);
        if (Host?.IsBusy == true) return new(FolioRegularizationOutcome.Busy);
        if ((await _sync.GetPendingChangesAsync(ct)).Count > 0) return new(FolioRegularizationOutcome.PendingHere);

        var candidates = await FindCandidatesAsync(ct);
        if (candidates.Count == 0)
        {
            await MarkDoneAsync(ct);
            return new(FolioRegularizationOutcome.Done);
        }

        // La nube al día: ningún otro equipo tiene cambios sin subir (podrían ser ediciones de estos mismos registros). Se
        // pregunta cada 15 minutos como máximo, no en cada sincronización
        var now = Clock.GetUtcNow();
        if (_waitingForDevicesUntil is { } until && now < until) return new(FolioRegularizationOutcome.PendingElsewhere);
        var devices = await _sync.GetDevicesAsync(ct);
        var pending = devices.Where(d => !d.IsThisDevice && !d.IsRevoked && !d.IsPendingApproval && d.PendingCount is > 0).ToList();
        StaleDevices = pending.Where(d => d.LastSeenUtc == null || now.UtcDateTime - d.LastSeenUtc.Value >= StaleDevice).ToList();
        if (pending.Count > 0)
        {
            _waitingForDevicesUntil = now + DeviceRecheck;
            AppLogger.Info($"Folios: la regularización espera a {string.Join(", ", pending.Select(d => d.DeviceName))}, con cambios sin subir.");
            return new(FolioRegularizationOutcome.PendingElsewhere);
        }
        _waitingForDevicesUntil = null;

        int patients = 0, sales = 0;
        bool skipped = false;
        var outcome = FolioRegularizationOutcome.Done;
        foreach (var candidate in candidates)
        {
            // Entre un registro y otro: si empezó una consulta o una venta, sigue en la siguiente oportunidad
            if (Host?.IsBusy == true) { outcome = FolioRegularizationOutcome.Busy; break; }
            if (candidate.Lettered.Kind == FolioKind.Patient && Host != null && !Host.CanReplacePatient(candidate.Id)) { skipped = true; continue; }

            var folio = await AssignedFolioAsync(candidate, ct);
            if (folio == null) { outcome = FolioRegularizationOutcome.Offline; break; }
            if (await ApplyAsync(candidate, folio, ct))
            {
                if (candidate.Lettered.Kind == FolioKind.Patient) patients++;
                else sales++;
            }
        }

        if (patients + sales > 0)
        {
            AppLogger.Info($"Folios: se regularizaron {patients} expedientes y {sales} tickets hechos sin conexión.");
            _sync.NotifyFoliosRegularized();
        }
        if (outcome == FolioRegularizationOutcome.Done && skipped) outcome = FolioRegularizationOutcome.Partial;
        if (outcome == FolioRegularizationOutcome.Done) await MarkDoneAsync(ct);
        return new(outcome, patients, sales);
    }

    private sealed record Candidate(int Id, string Folio, LetteredFolio Lettered);

    /// <summary>Los expedientes y tickets con folio de equipo, en el orden en que se crearon.</summary>
    private async Task<List<Candidate>> FindCandidatesAsync(CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        var patients = await db.Patients.AsNoTracking()
            .Where(p => EF.Functions.Like(p.RecordNumber, "EXP-%-%-%"))
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.RecordNumber })
            .ToListAsync(ct);
        var sales = await db.Sales.AsNoTracking()
            .Where(s => EF.Functions.Like(s.Folio, "TKT-%-%-%"))
            .OrderBy(s => s.Date).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.Folio })
            .ToListAsync(ct);
        return patients.Select(p => (p.Id, p.RecordNumber)).Concat(sales.Select(s => (s.Id, s.Folio)))
            .Select(x => FolioFactory.ParseLettered(x.Item2) is { } lettered ? new Candidate(x.Item1, x.Item2.Trim(), lettered) : null)
            .Where(c => c != null)
            .Select(c => c!)
            .ToList();
    }

    /// <summary>El número que ya recibió ese folio con letra, o el siguiente de su serie (null sin conexión).</summary>
    private async Task<string?> AssignedFolioAsync(Candidate candidate, CancellationToken ct)
    {
        var key = MapPrefix + candidate.Folio.ToUpperInvariant();
        using (var db = _contextFactory.CreateDbContext())
        {
            if (await DbMeta.GetAsync(db, key, ct) is { Length: > 0 } mapped) return mapped;
        }

        var series = candidate.Lettered.Series;
        int floor;
        using (var db = _contextFactory.CreateDbContext())
        {
            var existing = candidate.Lettered.Kind == FolioKind.Patient
                ? await db.Patients.AsNoTracking().Where(p => p.RecordNumber.StartsWith(series)).Select(p => p.RecordNumber).ToListAsync(ct)
                : await db.Sales.AsNoTracking().Where(s => s.Folio.StartsWith(series)).Select(s => s.Folio).ToListAsync(ct);
            floor = FolioFactory.MaxIn(series, existing);
        }
        if (await _sync.TryTakeFolioAsync(series, floor, ct) is not int number) return null;

        var folio = FolioFactory.Format(candidate.Lettered.Kind, series, number);
        using (var db = _contextFactory.CreateDbContext())
        {
            // Antes de escribir el registro: si se corta a la mitad, la siguiente vez usa el mismo número
            await DbMeta.SetAsync(db, key, folio, ct);
        }
        return folio;
    }

    /// <summary>
    /// El folio nuevo y el original en el mismo guardado (sube por el ciclo normal); en un expediente, también sus citas que
    /// copian el folio. false si el registro ya cambió de folio mientras tanto.
    /// </summary>
    private async Task<bool> ApplyAsync(Candidate candidate, string folio, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        if (candidate.Lettered.Kind == FolioKind.Patient)
        {
            var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == candidate.Id, ct);
            if (patient == null || !string.Equals(patient.RecordNumber.Trim(), candidate.Folio, StringComparison.OrdinalIgnoreCase)) return false;
            patient.RecordNumber = folio;
            patient.OriginalFolio ??= candidate.Folio;
            foreach (var appointment in await db.Appointments.Where(a => a.PatientRecordId == candidate.Id && a.PatientRecordNumber == candidate.Folio).ToListAsync(ct))
            {
                appointment.PatientRecordNumber = folio;
            }
        }
        else
        {
            var sale = await db.Sales.FirstOrDefaultAsync(s => s.Id == candidate.Id, ct);
            if (sale == null || !string.Equals(sale.Folio.Trim(), candidate.Folio, StringComparison.OrdinalIgnoreCase)) return false;
            sale.Folio = folio;
            sale.OriginalFolio ??= candidate.Folio;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task MarkDoneAsync(CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        await DbMeta.SetAsync(db, DoneDayKey, Today, ct);
    }
}
