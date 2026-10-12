using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Services.Folios;

namespace TPVC.Services;

/// <summary>
/// Paso 8.16 (respuesta 1 del médico): un solo consecutivo por serie desde <c>FolioCounters</c> en Turso. Con conexión, EXP y
/// TKT toman el siguiente número con una sola instrucción (V16); sin conexión (sin red, sin respuesta en 2 s, cuota agotada
/// o token rechazado), el folio lleva el código del equipo y su consecutivo guardado fuera de la base. Con la nube apagada o
/// sin verificar, la base local numera como en v1.19. Los índices de RecordNumber y Folio ya no son únicos: un folio
/// repetido se conserva y aparece en un aviso.
/// </summary>
public partial class TursoSyncService : IFolioSource
{
    /// <summary>Cuánto se espera el número de la nube al guardar; después, el folio sale con la letra del equipo.</summary>
    public static readonly TimeSpan FolioWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// El siguiente número de una serie, en una sola instrucción atómica (V16, 2026-10-11). El piso es el mayor número de la
    /// serie en la base local del equipo: así nunca regresa uno que el equipo ya tiene (una PC que numeró sola, o un folio
    /// tecleado a mano). Argumentos: la serie, el piso y otra vez el piso.
    /// </summary>
    public const string TakeFolioSql =
        "INSERT INTO FolioCounters (Series, LastNumber) VALUES (?, ? + 1) " +
        "ON CONFLICT(Series) DO UPDATE SET LastNumber = max(LastNumber, ?) + 1 RETURNING LastNumber;";

    /// <summary>Sube el contador hasta un número que ya existe (la siembra de la PC y los folios tecleados a mano).</summary>
    public const string RaiseFolioSql =
        "INSERT INTO FolioCounters (Series, LastNumber) VALUES (?, ?) " +
        "ON CONFLICT(Series) DO UPDATE SET LastNumber = max(LastNumber, excluded.LastNumber);";

    public const string NoDeviceCodeMessage =
        "Sin conexión a la nube, esta computadora todavía no tiene su código de equipo (P1, P2…) para numerar sin repetir folios. " +
        "Sincronice una vez con internet; si es una computadora nueva, únala a la nube o márquela como la que reemplaza a la principal.";

    private OfflineFolioStore? _offlineFolios;

    /// <summary>folios.json junto a turso_config.json.</summary>
    public OfflineFolioStore OfflineFolios => _offlineFolios ??= new OfflineFolioStore(Path.Combine(_settingsService.ConfigFolder, "folios.json"));

    // La URL donde ya se sembraron los contadores en esta sesión
    private string? _foliosSeededUrl;

    /// <summary>Los contadores de la nube: activa, con URL y token, sin conflicto de identidad y con la nube verificada.</summary>
    internal async Task<bool> IsFolioCounterEnabledAsync(CancellationToken ct)
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl) || string.IsNullOrWhiteSpace(settings.AuthToken)) return false;
        if (IdentityConflict != null) return false;
        using var db = _contextFactory.CreateDbContext();
        return await DbMeta.GetAsync(db, DbMeta.ClinicId, ct) != null;
    }

    public async Task<string?> NextAsync(FolioKind kind, DateTime localDate, IReadOnlyCollection<string> existing, CancellationToken ct = default)
    {
        if (!await IsFolioCounterEnabledAsync(ct)) return null;

        var series = FolioFactory.Series(kind, localDate);
        if (await TryTakeFolioAsync(series, FolioFactory.MaxIn(series, existing), ct) is int taken)
        {
            return FolioFactory.Online(kind, localDate, taken);
        }

        var code = _settingsService.CurrentSettings.DeviceCode;
        if (!FolioFactory.IsDeviceCode(code)) throw new InvalidOperationException(NoDeviceCodeMessage);
        var lettered = FolioFactory.LetteredSeries(series, code!);
        var offline = FolioFactory.Offline(kind, localDate, code!, existing, OfflineFolios.Last(lettered));
        OfflineFolios.Save(offline.LetteredSeries, offline.Number);
        AppLogger.Info($"Turso: sin conexión, el folio sale con la letra del equipo ({offline.Folio}).");
        return offline.Folio;
    }

    /// <summary>El número de la nube, o null sin conexión, sin respuesta en 2 s, con la sincronización en pausa o con un error.</summary>
    internal async Task<int?> TryTakeFolioAsync(string series, int floor, CancellationToken ct)
    {
        // Cuota agotada, freno de consumo o token rechazado: no se pregunta
        if (PauseReason != Models.TursoSyncPauseReason.None) return null;

        var settings = _settingsService.CurrentSettings;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(FolioWait);
        try
        {
            using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
            // La tabla, por si esta nube todavía no la tiene (la PC la crea con el esquema): sin filas, no cuesta nada
            var results = await client.QueryBatchAsync(new (string, object?[]?)[]
            {
                (SyncSchema.FolioCountersStatement, null),
                (TakeFolioSql, new object?[] { series, floor, floor })
            }, wait.Token);
            AddExternalUsage(rowsRead: 1, rowsWritten: 1);
            var value = results[1].FirstOrDefault()?.GetValueOrDefault("LastNumber");
            return value == null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLogger.Warn($"Turso: el número de {series} no llegó en {FolioWait.TotalSeconds:0} s; el folio sale con la letra del equipo.");
            return null;
        }
        catch (Exception ex) when (ex is TursoException or HttpRequestException or FormatException or InvalidCastException)
        {
            AppLogger.Warn($"Turso: no se pudo tomar el número de {series} ({ex.Message}); el folio sale con la letra del equipo.");
            return null;
        }
    }

    public async Task NoteManualAsync(string folio, DateTime localDate, CancellationToken ct = default)
    {
        foreach (var kind in new[] { FolioKind.Patient, FolioKind.Sale })
        {
            var series = FolioFactory.Series(kind, localDate);
            if (FolioFactory.NumberIn(series, folio) is not int number) continue;
            if (!await IsFolioCounterEnabledAsync(ct) || PauseReason != Models.TursoSyncPauseReason.None) return;

            var settings = _settingsService.CurrentSettings;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(FolioWait);
            try
            {
                using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
                await client.QueryBatchAsync(new (string, object?[]?)[]
                {
                    (SyncSchema.FolioCountersStatement, null),
                    (RaiseFolioSql, new object?[] { series, number })
                }, wait.Token);
                AddExternalUsage(rowsRead: 1, rowsWritten: 1);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TursoException or HttpRequestException)
            {
                // La siembra de la siguiente sesión (o el piso del siguiente que tome número) lo cubre
                AppLogger.Warn($"Turso: no se pudo subir el contador de {series} al folio tecleado {folio}: {ex.Message}");
            }
            return;
        }
    }

    /// <summary>
    /// En la PC principal, una vez por sesión con la nube verificada (y después de cada «Sincronización completa»): sube el
    /// contador del año (EXP) y del día (TKT) hasta el mayor número que ya existe en esta base. Lo que otra PC numeró sola
    /// llega aquí por la sincronización; el piso de cada equipo cubre lo que todavía no llega.
    /// </summary>
    private async Task<int> SeedFolioCountersAsync(TursoClient client, bool force, CancellationToken ct)
    {
        if (!force && _foliosSeededUrl == client.DatabaseUrl) return 0;
        if (Registration is not { IsPrimary: true } || _registryCheckedUrl != client.DatabaseUrl) return 0;
        var today = Models.ClinicClock.Today;
        var statements = new List<(string, object?[]?)>();
        using (var db = _contextFactory.CreateDbContext())
        {
            if (await DbMeta.GetAsync(db, DbMeta.ClinicId, ct) == null) return 0;

            var expSeries = FolioFactory.Series(FolioKind.Patient, today);
            var exp = FolioFactory.MaxIn(expSeries, await db.Patients.AsNoTracking()
                .Where(p => p.RecordNumber.StartsWith(expSeries)).Select(p => p.RecordNumber).ToListAsync(ct));
            if (exp > 0) statements.Add((RaiseFolioSql, new object?[] { expSeries, exp }));

            var tktSeries = FolioFactory.Series(FolioKind.Sale, today);
            var tkt = FolioFactory.MaxIn(tktSeries, await db.Sales.AsNoTracking()
                .Where(s => s.Folio.StartsWith(tktSeries)).Select(s => s.Folio).ToListAsync(ct));
            if (tkt > 0) statements.Add((RaiseFolioSql, new object?[] { tktSeries, tkt }));
        }

        _foliosSeededUrl = client.DatabaseUrl;
        if (statements.Count == 0) return 0;
        await client.ExecuteBatchAsync(statements, ct);
        return statements.Count;
    }

    // =========================================================================
    // REGULARIZACIÓN (paso 8.17)
    // =========================================================================

    /// <summary>Esta PC es la principal de la nube de ahora (según el registro de esta sesión).</summary>
    internal bool IsPrimaryOnCurrentCloud =>
        Registration is { IsPrimary: true } && _registryCheckedUrl == _settingsService.CurrentSettings.DatabaseUrl;

    /// <summary>
    /// Después de regularizar: las pantallas recargan pacientes y ventas (en el hilo de la interfaz) y, como son cambios de
    /// este equipo, la sincronización automática los sube.
    /// </summary>
    internal void NotifyFoliosRegularized() => RunOnUiThread(() => _inventoryService?.NotifyAllChanged());

    // =========================================================================
    // FOLIOS REPETIDOS
    // =========================================================================

    /// <summary>"EXP-2026-005 (2 expedientes)"… según la última revisión; vacío si no hay.</summary>
    public IReadOnlyList<string> DuplicateFolios { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Folios repetidos en esta base: filas que llegaron así de dos equipos que numeraron solos (antes de v1.34.0 se
    /// renombraban con -R1) o un folio tecleado a mano. Los dos registros se conservan.
    /// </summary>
    public async Task<IReadOnlyList<string>> RefreshDuplicateFoliosAsync(CancellationToken ct = default)
    {
        using var db = _contextFactory.CreateDbContext();
        var patients = await db.Patients.AsNoTracking()
            .Where(p => p.IsActive && p.RecordNumber != "")
            .GroupBy(p => p.RecordNumber.ToUpper())
            .Where(g => g.Count() > 1)
            .Select(g => new { Folio = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var sales = await db.Sales.AsNoTracking()
            .Where(s => s.Folio != "")
            .GroupBy(s => s.Folio.ToUpper())
            .Where(g => g.Count() > 1)
            .Select(g => new { Folio = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        DuplicateFolios = patients.OrderBy(p => p.Folio).Select(p => $"{p.Folio} ({p.Count} expedientes)")
            .Concat(sales.OrderBy(s => s.Folio).Select(s => $"{s.Folio} ({s.Count} tickets)"))
            .ToList();
        return DuplicateFolios;
    }
}
