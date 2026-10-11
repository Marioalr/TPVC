using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;
using TPVC.Services.LabFiles;

namespace TPVC.Services;

/// <summary>Lo que hizo una vuelta de los archivos de laboratorio.</summary>
public sealed class LabTransferReport
{
    public bool Ran { get; set; }
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int Evicted { get; set; }
    public int OrphansDeleted { get; set; }

    /// <summary>Archivos que no se subieron porque la nube llegó a su tope: se quedan en este equipo.</summary>
    public int WaitingForCap { get; set; }

    /// <summary>Por qué se detuvo algo (tope, freno, sin respaldo externo, sin conexión); null si todo salió.</summary>
    public string? Notice { get; set; }
}

/// <summary>
/// Paso 8.12b del plan de la Fase 8: los archivos de los resultados de laboratorio en la nube (LabBlobs y LabBlobChunks),
/// después de cada sincronización y fuera de su candado (un archivo grande no la detiene):
/// <list type="bullet">
/// <item>Sube los archivos de esta PC que la nube no tiene, en trozos de 512 KiB, uno por petición; el último va en la misma
/// transacción que el cierre (V23). Un corte sigue después con los trozos que faltan.</item>
/// <item>Baja a su archivo los que le faltan (cada PC guarda todo), de 4 trozos por petición, y revisa su SHA-256.</item>
/// <item>Tope de 2 GB: nadie sube lo que no cabe. La PC principal, una vez al día, mide la nube, borra lo que ningún
/// resultado nombra hace 30 días y, si tiene respaldo externo, desaloja los más viejos que tiene verificados en su archivo
/// y en el respaldo hasta el 90% del tope (V20: borrar sí libera la cuota de Turso).</item>
/// <item>Freno de bytes por día (<see cref="TursoConsumptionBrake.MaxLabBytesDownPerDay"/>); abrir un resultado no se frena.</item>
/// </list>
/// </summary>
public partial class TursoSyncService : ILabFileFetcher
{
    /// <summary>El tope de los resultados de laboratorio en la nube: 2 × (L + 0.1 GB) ≤ 5 GB mientras conviva una restauración.</summary>
    public const long LabCloudCapBytes = 2_000_000_000L;

    /// <summary>Si toda la base pasa de esto (80% de los 5 GB del plan gratuito), no se sube ningún archivo.</summary>
    public const long CloudSpaceStopBytes = 4_000_000_000L;

    /// <summary>Días que la nube guarda un archivo que ningún resultado nombra (un resultado borrado, Reemplazar la nube).</summary>
    public const int LabOrphanDays = 30;

    private const int LabChunksPerRead = 4;

    /// <summary>Para las pruebas: otro tope (el aviso es el 80% y el desalojo deja el 90%).</summary>
    internal long? LabCloudCapOverride { get; set; }

    /// <summary>Para las pruebas: otro freno de bytes por día (subida, bajada).</summary>
    internal (long Up, long Down)? LabBytesBrakeOverride { get; set; }

    public long LabCloudCap => LabCloudCapOverride ?? LabCloudCapBytes;

    /// <summary>Arriba de esto se avisa (1.6 GB).</summary>
    public long LabCloudWarn => WarnBytesFor(LabCloudCap);

    /// <summary>Hasta aquí desaloja la PC principal (1.8 GB).</summary>
    public long LabCloudEvictTarget => EvictTargetFor(LabCloudCap);

    public static long WarnBytesFor(long cap) => cap / 10 * 8;

    public static long EvictTargetFor(long cap) => cap / 10 * 9;

    private LabResultService? _labResults;
    private readonly SemaphoreSlim _labLock = new(1, 1);
    private int _labRerun;

    // Archivos que no se pueden bajar en esta sesión (desalojados o dañados): no se reintentan hasta reiniciar
    private readonly HashSet<string> _unavailableLabHashes = new(StringComparer.Ordinal);

    /// <summary>Subir y bajar archivos después de cada sincronización (las pruebas lo apagan y lo llaman a mano).</summary>
    public bool AutoLabTransfers { get; set; } = true;

    public LabTransferReport? LastLabTransfer { get; private set; }

    public event Action<LabTransferReport>? LabTransfersCompleted;

    // =========================================================================
    // SQL (literal en contracts/golden/lab-files.json)
    // =========================================================================

    internal static readonly string LabRegisterSql =
        $"INSERT INTO {SyncSchema.LabBlobsTable} (Sha256, SizeBytes, ChunkSize, ChunkCount, MimeType, CreatedAtUtc, OriginDeviceId) " +
        $"VALUES (?, ?, ?, ?, ?, {ServerNowSql}, ?) ON CONFLICT(Sha256) DO NOTHING;";

    internal static readonly string LabStateSql =
        $"SELECT SizeBytes, ChunkSize, ChunkCount, MimeType, OriginDeviceId, CompletedAtUtc, ArchivedAtUtc, EvictedAtUtc FROM {SyncSchema.LabBlobsTable} WHERE Sha256 = ?;";

    internal static readonly string LabExistingChunksSql = $"SELECT ChunkIndex FROM {SyncSchema.LabBlobChunksTable} WHERE Sha256 = ?;";

    internal static readonly string LabInsertChunkSql =
        $"INSERT INTO {SyncSchema.LabBlobChunksTable} (Sha256, ChunkIndex, Data) VALUES (?, ?, ?) ON CONFLICT(Sha256, ChunkIndex) DO NOTHING;";

    /// <summary>Args: Sha256, Sha256. Solo cierra si ya están todos sus trozos.</summary>
    internal static readonly string LabCloseSql =
        $"UPDATE {SyncSchema.LabBlobsTable} SET CompletedAtUtc = {ServerNowSql} WHERE Sha256 = ? AND CompletedAtUtc IS NULL " +
        $"AND (SELECT COUNT(*) FROM {SyncSchema.LabBlobChunksTable} WHERE Sha256 = ?) = ChunkCount;";

    /// <summary>Args: Sha256, el primer ChunkIndex que falta.</summary>
    internal static readonly string LabReadChunksSql =
        $"SELECT ChunkIndex, Data FROM {SyncSchema.LabBlobChunksTable} WHERE Sha256 = ? AND ChunkIndex >= ? ORDER BY ChunkIndex LIMIT {LabChunksPerRead};";

    internal static readonly string LabUsageSql =
        $"SELECT COUNT(*) AS Files, ifnull(SUM(SizeBytes), 0) AS Bytes FROM {SyncSchema.LabBlobsTable} WHERE EvictedAtUtc IS NULL;";

    /// <summary>El espacio de toda la base, como lo cuenta Turso (V20): (page_count − freelist_count) × page_size.</summary>
    internal static readonly string[] CloudSpaceSql = { "PRAGMA page_count;", "PRAGMA freelist_count;", "PRAGMA page_size;" };

    internal static readonly string LabArchivedSql =
        $"UPDATE {SyncSchema.LabBlobsTable} SET ArchivedAtUtc = {ServerNowSql} WHERE Sha256 = ? AND ArchivedAtUtc IS NULL;";

    /// <summary>
    /// Lo que se puede desalojar, del resultado más viejo al más nuevo (por la fecha del resultado más viejo que lo nombra;
    /// primero los que ninguno nombra), con el índice de LabResults.ContentSha256.
    /// </summary>
    internal static readonly string LabEvictionCandidatesSql =
        $"SELECT b.Sha256, b.SizeBytes, (SELECT min(r.\"ResultDate\") FROM \"LabResults\" AS r WHERE r.\"ContentSha256\" = b.Sha256) AS ResultDate " +
        $"FROM {SyncSchema.LabBlobsTable} AS b WHERE b.EvictedAtUtc IS NULL AND b.CompletedAtUtc IS NOT NULL ORDER BY 3, b.CreatedAtUtc, b.Sha256;";

    internal static readonly string LabEvictChunksSql = $"DELETE FROM {SyncSchema.LabBlobChunksTable} WHERE Sha256 = ?;";

    internal static readonly string LabEvictSql =
        $"UPDATE {SyncSchema.LabBlobsTable} SET EvictedAtUtc = {ServerNowSql} WHERE Sha256 = ? AND EvictedAtUtc IS NULL;";

    private static readonly string NamedByAResult = $"(SELECT 1 FROM \"LabResults\" WHERE \"ContentSha256\" = {SyncSchema.LabBlobsTable}.Sha256)";
    private static readonly string OrphanCutoff = $"strftime('%Y-%m-%d %H:%M:%f','now','-{LabOrphanDays} days')";

    /// <summary>La limpieza diaria de la PC principal, en una transacción: anota y quita huérfanos, y borra los de 30 días.</summary>
    internal static readonly string[] LabOrphanSql =
    {
        $"UPDATE {SyncSchema.LabBlobsTable} SET OrphanSinceUtc = {ServerNowSql} WHERE OrphanSinceUtc IS NULL AND NOT EXISTS {NamedByAResult};",
        $"UPDATE {SyncSchema.LabBlobsTable} SET OrphanSinceUtc = NULL WHERE OrphanSinceUtc IS NOT NULL AND EXISTS {NamedByAResult};",
        $"DELETE FROM {SyncSchema.LabBlobChunksTable} WHERE Sha256 IN (SELECT Sha256 FROM {SyncSchema.LabBlobsTable} WHERE OrphanSinceUtc < {OrphanCutoff});",
        $"DELETE FROM {SyncSchema.LabBlobsTable} WHERE OrphanSinceUtc < {OrphanCutoff};"
    };

    public static int ChunkCountFor(long size) => (int)Math.Max(1, (size + SyncSchema.AssetChunkBytes - 1) / SyncSchema.AssetChunkBytes);

    /// <summary>Lo que viaja de un trozo: su base64.</summary>
    private static long TravelBytes(long bytes) => 4 * ((bytes + 2) / 3);

    // =========================================================================
    // VUELTA DESPUÉS DE CADA SINCRONIZACIÓN
    // =========================================================================

    private void AttachLabResults(LabResultService? labResults)
    {
        _labResults = labResults;
        if (labResults == null) return;
        labResults.Fetcher = this;
        labResults.LocalChanged += OnLocalDataChanged;
    }

    /// <summary>Al terminar una sincronización buena: la vuelta de los archivos, en segundo plano.</summary>
    private void ScheduleLabTransfers()
    {
        if (_labResults == null || !AutoLabTransfers) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunLabTransfersAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Laboratorio: la vuelta de archivos no pudo completarse: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Sube lo que falta en la nube y baja lo que falta aquí. Si ya hay una vuelta en curso, esa se repite al terminar.
    /// <paramref name="forceMaintenance"/>: la revisión diaria de la PC principal aunque ya se haya hecho hoy.
    /// </summary>
    public async Task<LabTransferReport> RunLabTransfersAsync(bool forceMaintenance = false, CancellationToken ct = default)
    {
        var report = new LabTransferReport();
        var settings = _settingsService.CurrentSettings;
        if (_labResults == null || !settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl)) return report;
        if (PauseReason != TursoSyncPauseReason.None)
        {
            report.Notice = PauseMessage;
            return report;
        }
        if (!await _labLock.WaitAsync(0, ct))
        {
            Interlocked.Exchange(ref _labRerun, 1);
            return report;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _labRerun, 0);
                await RunLabTransfersCoreAsync(settings, report, forceMaintenance, ct);
                forceMaintenance = false;
            }
            while (Volatile.Read(ref _labRerun) == 1 && report.Notice == null);
        }
        catch (Exception ex) when (ex is TursoException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            report.Notice = $"Los archivos de laboratorio se siguen después: {ex.Message}";
            AppLogger.Warn($"Laboratorio: {ex.Message}");
        }
        finally
        {
            _labLock.Release();
        }

        if (report.Ran)
        {
            _settingsService.RecordUsage(Interlocked.Exchange(ref _externalRowsRead, 0), Interlocked.Exchange(ref _externalRowsWritten, 0));
            AppLogger.Info($"Laboratorio: {report.Uploaded} subidos, {report.Downloaded} bajados, {report.Evicted} desalojados, " +
                           $"{report.OrphansDeleted} huérfanos borrados{(report.Notice != null ? $" ({report.Notice})" : string.Empty)}.");
        }
        LastLabTransfer = report;
        LabTransfersCompleted?.Invoke(report);
        if (report.Downloaded > 0) _labResults.NotifyChanged();
        return report;
    }

    private sealed record LabWork(List<(string Sha, string ResultDate)> Upload, List<string> Download, List<string> MarkArchived);

    /// <summary>Lo que hay que hacer, solo con lo de aquí: sin preguntarle nada a la nube.</summary>
    private async Task<LabWork> LoadLabWorkAsync(string url, bool isPrimary, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        var results = await db.LabResults.AsNoTracking()
            .Select(r => new { r.ContentSha256, r.ResultDate, r.CreatedAt })
            .ToListAsync(ct);
        var states = await db.LabFileStates.AsNoTracking().ToDictionaryAsync(s => s.Sha256, StringComparer.Ordinal, ct);

        var work = new LabWork(new List<(string, string)>(), new List<string>(), new List<string>());
        // Los más recientes primero: si la nube llega a su tope, lo que se queda afuera es lo más viejo. La fecha de un
        // archivo es la del resultado más viejo que lo nombra, como la ordena el desalojo en la nube
        foreach (var file in results
                     .Where(r => LabFileStore.IsValidHash(r.ContentSha256))
                     .GroupBy(r => r.ContentSha256, StringComparer.Ordinal)
                     .Select(g => (Sha: g.Key, Date: g.Min(r => r.ResultDate), Created: g.Max(r => r.CreatedAt)))
                     .OrderByDescending(f => f.Date).ThenByDescending(f => f.Created))
        {
            var sha = file.Sha;
            var state = states.GetValueOrDefault(sha);
            bool here = _labResults!.Store.Contains(sha);
            if (here && state?.UploadedTo != url) work.Upload.Add((sha, FormatDate(file.Date)));
            if (!here && !IsUnavailable(sha)) work.Download.Add(sha);
        }
        if (isPrimary)
        {
            work.MarkArchived.AddRange(states.Values
                .Where(s => s.UploadedTo == url && s.ArchiveMarkedIn != url && _labResults!.Store.Contains(s.Sha256))
                .Select(s => s.Sha256));
        }
        return work;
    }

    private bool IsUnavailable(string sha)
    {
        lock (_unavailableLabHashes) return _unavailableLabHashes.Contains(sha);
    }

    private void MarkUnavailable(string sha)
    {
        lock (_unavailableLabHashes) _unavailableLabHashes.Add(sha);
    }

    /// <summary>Lo que se sabe del uso de la nube durante una vuelta (se ajusta con lo que se sube y se desaloja).</summary>
    private sealed class LabUsage
    {
        public int Files;
        public long Bytes;
        public long? Space;
    }

    private async Task RunLabTransfersCoreAsync(TursoSettings settings, LabTransferReport report, bool forceMaintenance, CancellationToken ct)
    {
        var url = settings.DatabaseUrl;
        // La registración es la de esta nube: la revisa la sincronización una vez por sesión
        bool isPrimary = Registration?.IsPrimary == true && _registryCheckedUrl == url;
        bool maintenanceDue = isPrimary && (forceMaintenance || settings.LabCloud?.MeasuredDay != _settingsService.Today);
        var work = await LoadLabWorkAsync(url, isPrimary, ct);
        if (work.Upload.Count == 0 && work.Download.Count == 0 && work.MarkArchived.Count == 0 && !maintenanceDue) return;

        using var client = ClientFactory(url, settings.AuthToken);
        await EnsureRemoteSchemaAsync(client, ct);
        report.Ran = true;

        LabUsage? usage = null;
        if (work.Upload.Count > 0 || maintenanceDue) usage = await MeasureLabCloudAsync(client, maintenanceDue, ct);
        if (maintenanceDue) await RunLabMaintenanceAsync(client, usage!, report, ct);

        foreach (var (sha, resultDate) in work.Upload)
        {
            if (LabBrakeReached(upload: true, out var notice))
            {
                report.Notice = notice;
                break;
            }
            await UploadLabFileAsync(client, url, sha, resultDate, usage!, isPrimary, report, ct);
        }
        if (usage != null && report.Uploaded > 0) SaveLabCloud(usage, daily: false);

        foreach (var sha in work.Download)
        {
            if (LabBrakeReached(upload: false, out var notice))
            {
                report.Notice ??= notice;
                break;
            }
            var fetched = await DownloadLabFileAsync(client, url, sha, background: true, ct);
            if (fetched.Ok) report.Downloaded++;
            else if (fetched.Status is LabFetchStatus.Evicted or LabFetchStatus.Broken) MarkUnavailable(sha);
        }

        // Lo que esta PC, como principal, ya tiene en su archivo (también lo que acaba de subir o bajar)
        if (isPrimary)
        {
            var marked = (await LoadLabWorkAsync(url, isPrimary: true, ct)).MarkArchived;
            foreach (var batch in marked.Chunk(50))
            {
                await client.TransactionAsync(batch.Select(sha => (LabArchivedSql, (object?[]?)new object?[] { sha })), ct);
                AddExternalUsage(batch.Length, batch.Length);
                await SaveLabFileStatesAsync(batch, s => s.ArchiveMarkedIn = url, ct);
            }
        }
    }

    // =========================================================================
    // SUBIR
    // =========================================================================

    private static bool IsCompleted(Dictionary<string, object?> state) => state.GetValueOrDefault("CompletedAtUtc") is string { Length: > 0 };

    private static bool IsEvicted(Dictionary<string, object?> state) => state.GetValueOrDefault("EvictedAtUtc") is string { Length: > 0 };

    private static long LongOf(Dictionary<string, object?> row, string column) =>
        Convert.ToInt64(row.GetValueOrDefault(column) ?? 0L, CultureInfo.InvariantCulture);

    /// <summary>
    /// Sube un archivo de esta PC si la nube no lo tiene completo: lo registra (si cabe), sube los trozos que faltan y lo
    /// cierra junto con el último. Uno que la nube desalojó a propósito no se vuelve a subir.
    /// </summary>
    private async Task UploadLabFileAsync(TursoClient client, string url, string sha, string resultDate, LabUsage usage, bool isPrimary,
        LabTransferReport report, CancellationToken ct)
    {
        var content = _labResults!.Store.ReadVerified(sha);
        if (content == null)
        {
            AppLogger.Warn($"Laboratorio: el archivo {sha} de esta PC está dañado; no se sube.");
            return;
        }

        var before = await client.QueryBatchAsync(new (string, object?[]?)[]
        {
            (LabStateSql, new object?[] { sha }),
            (LabExistingChunksSql, new object?[] { sha })
        }, ct);
        AddExternalUsage(1 + before[1].Count, 0);
        var state = before[0].FirstOrDefault();
        if (state != null && (IsEvicted(state) || IsCompleted(state)))
        {
            // Ya está (lo subió otro equipo) o se desalojó a propósito: no se gasta la nube otra vez
            await SaveLabFileStatesAsync(new[] { sha }, s => s.UploadedTo = url, ct);
            return;
        }

        int chunkCount = ChunkCountFor(content.LongLength);
        if (state == null)
        {
            if (!FitsInCloud(usage, content.LongLength) && isPrimary) await EvictAsync(client, usage, content.LongLength, resultDate, report, ct);
            if (!FitsInCloud(usage, content.LongLength))
            {
                report.WaitingForCap++;
                report.Notice ??= usage.Space > CloudSpaceStopBytes - content.LongLength
                    ? $"La nube está casi llena ({LabFileImporter.Megabytes(usage.Space ?? 0)} de 5 GB): los resultados nuevos se quedan en este equipo."
                    : $"La nube de laboratorio llegó a su tope ({LabFileImporter.Megabytes(LabCloudCap)}): los resultados nuevos se quedan en este " +
                      "equipo. La PC principal libera espacio si tiene un respaldo externo (Configuración › Respaldos y datos).";
                return;
            }
            await client.ExecuteAsync(LabRegisterSql,
                new object?[] { sha, content.LongLength, SyncSchema.AssetChunkBytes, chunkCount, MimeTypeOf(sha), _settingsService.CurrentSettings.DeviceId }, ct);
            AddExternalUsage(1, 2);
            usage.Files++;
            usage.Bytes += content.LongLength;
        }
        else if (LongOf(state, "SizeBytes") != content.LongLength || LongOf(state, "ChunkCount") != chunkCount)
        {
            AppLogger.Warn($"Laboratorio: la nube registró {sha} con otro tamaño; no se sube.");
            return;
        }

        var existing = before[1].Select(r => (int)LongOf(r, "ChunkIndex")).ToHashSet();
        var missing = Enumerable.Range(0, chunkCount).Where(i => !existing.Contains(i)).ToList();
        int closed;
        if (missing.Count == 0)
        {
            closed = await client.ExecuteAsync(LabCloseSql, new object?[] { sha, sha }, ct);
            AddExternalUsage(chunkCount + 1, closed);
        }
        else
        {
            closed = 0;
            for (int k = 0; k < missing.Count; k++)
            {
                int index = missing[k];
                int offset = index * SyncSchema.AssetChunkBytes;
                var piece = content.AsSpan(offset, (int)Math.Min(SyncSchema.AssetChunkBytes, content.LongLength - offset)).ToArray();
                var insert = (LabInsertChunkSql, (object?[]?)new object?[] { sha, index, piece });
                if (k == missing.Count - 1)
                {
                    var results = await client.TransactionAsync(new[] { insert, (LabCloseSql, (object?[]?)new object?[] { sha, sha }) }, ct);
                    closed = results[1].AffectedRows;
                    AddExternalUsage(chunkCount + 2, 2 + closed);
                }
                else
                {
                    await client.ExecuteAsync(insert.Item1, insert.Item2, ct);
                    AddExternalUsage(1, 2);
                }
                _settingsService.RecordLabBytes(TravelBytes(piece.Length), 0);

                if (k < missing.Count - 1 && LabBrakeReached(upload: true, out var notice))
                {
                    report.Notice = notice;
                    return;
                }
            }
        }

        if (closed == 0)
        {
            var after = (await client.QueryAsync(LabStateSql, new object?[] { sha }, ct)).FirstOrDefault();
            AddExternalUsage(1, 0);
            if (after == null || !IsCompleted(after))
            {
                AppLogger.Warn($"Laboratorio: {sha} no quedó completo en la nube; se sigue en la siguiente vuelta.");
                return;
            }
        }

        await SaveLabFileStatesAsync(new[] { sha }, s => s.UploadedTo = url, ct);
        report.Uploaded++;
        AppLogger.Info($"Laboratorio: se subió {sha} ({content.Length:N0} bytes, {missing.Count} de {chunkCount} trozos).");
    }

    private bool FitsInCloud(LabUsage usage, long size) =>
        usage.Bytes + size <= LabCloudCap && (usage.Space == null || usage.Space + size <= CloudSpaceStopBytes);

    /// <summary>El tipo del archivo según su resultado (todos los que lo nombran tienen el mismo).</summary>
    private string MimeTypeOf(string sha)
    {
        using var db = _contextFactory.CreateDbContext();
        return db.LabResults.AsNoTracking().Where(r => r.ContentSha256 == sha).Select(r => r.MimeType).FirstOrDefault() ?? LabResult.PdfMimeType;
    }

    private async Task SaveLabFileStatesAsync(IEnumerable<string> hashes, Action<LabFileState> change, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        foreach (var sha in hashes)
        {
            var state = await db.LabFileStates.FindAsync(new object[] { sha }, ct);
            if (state == null)
            {
                state = new LabFileState { Sha256 = sha };
                db.LabFileStates.Add(state);
            }
            change(state);
            state.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    // =========================================================================
    // BAJAR
    // =========================================================================

    /// <summary>
    /// Trae un archivo que no está aquí (al abrir un resultado). Lo pide el médico: no lo detiene el freno de bytes del día.
    /// </summary>
    public async Task<LabFetchResult> FetchLabFileAsync(string sha256, CancellationToken ct = default)
    {
        if (_labResults?.Store.ReadVerified(sha256) is { } local) return new LabFetchResult(LabFetchStatus.Ok, string.Empty, local);
        var settings = _settingsService.CurrentSettings;
        if (_labResults == null || !settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl))
            return new LabFetchResult(LabFetchStatus.NotInCloud, "El archivo no está en este equipo y la sincronización con la nube no está activa.");

        try
        {
            using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
            await EnsureRemoteSchemaAsync(client, ct);
            var fetched = await DownloadLabFileAsync(client, settings.DatabaseUrl, sha256, background: false, ct);
            _settingsService.RecordUsage(Interlocked.Exchange(ref _externalRowsRead, 0), Interlocked.Exchange(ref _externalRowsWritten, 0));
            if (fetched.Ok) _labResults.NotifyChanged();
            return fetched;
        }
        catch (Exception ex) when (ex is TursoException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            AppLogger.Warn($"Laboratorio: no se pudo bajar {sha256}: {ex.Message}");
            return new LabFetchResult(LabFetchStatus.Failed, $"No se pudo bajar el resultado de la nube: {ex.Message}");
        }
    }

    /// <summary>Baja un archivo completo (de 4 trozos en 4), revisa su tamaño y su SHA-256, y lo guarda en el archivo de aquí.</summary>
    private async Task<LabFetchResult> DownloadLabFileAsync(TursoClient client, string url, string sha, bool background, CancellationToken ct)
    {
        var state = (await client.QueryAsync(LabStateSql, new object?[] { sha }, ct)).FirstOrDefault();
        AddExternalUsage(1, 0);
        if (state == null)
            return new LabFetchResult(LabFetchStatus.NotInCloud,
                "Este resultado todavía no llega a la nube: el equipo donde se agregó lo sube al sincronizar.");
        if (IsEvicted(state))
            return new LabFetchResult(LabFetchStatus.Evicted,
                "Este resultado ya no está en la nube (se liberó espacio): está en el archivo de la PC principal y en su respaldo externo.");
        if (!IsCompleted(state))
            return new LabFetchResult(LabFetchStatus.Uploading, "Este resultado se está subiendo desde otro equipo; intente en unos minutos.");

        long size = LongOf(state, "SizeBytes");
        int chunkCount = (int)LongOf(state, "ChunkCount");
        using var content = new MemoryStream((int)Math.Min(size, int.MaxValue));
        for (int first = 0; first < chunkCount; first += LabChunksPerRead)
        {
            if (background && first > 0 && LabBrakeReached(upload: false, out var notice))
                return new LabFetchResult(LabFetchStatus.Failed, notice);

            var rows = await client.QueryAsync(LabReadChunksSql, new object?[] { sha, first }, ct);
            AddExternalUsage(Math.Max(1, rows.Count), 0);
            int expected = Math.Min(LabChunksPerRead, chunkCount - first);
            long travelled = 0;
            for (int i = 0; i < expected; i++)
            {
                if (i >= rows.Count || LongOf(rows[i], "ChunkIndex") != first + i || rows[i].GetValueOrDefault("Data") is not byte[] piece)
                {
                    AppLogger.Warn($"Laboratorio: a {sha} le falta el trozo {first + i} en la nube.");
                    return new LabFetchResult(LabFetchStatus.Broken, "El archivo de este resultado está incompleto en la nube.");
                }
                content.Write(piece);
                travelled += TravelBytes(piece.Length);
            }
            _settingsService.RecordLabBytes(0, travelled);
        }

        var bytes = content.ToArray();
        if (bytes.LongLength != size || !_labResults!.Store.SaveVerified(sha, bytes))
        {
            AppLogger.Warn($"Laboratorio: {sha} llegó con otro tamaño o contenido; no se guarda.");
            return new LabFetchResult(LabFetchStatus.Broken, "El archivo de este resultado llegó dañado de la nube.");
        }

        await SaveLabFileStatesAsync(new[] { sha }, s => s.UploadedTo = url, ct);
        return new LabFetchResult(LabFetchStatus.Ok, string.Empty, bytes);
    }

    private bool LabBrakeReached(bool upload, out string notice)
    {
        var (up, down) = _settingsService.GetLabBytesToday();
        var (maxUp, maxDown) = LabBytesBrakeOverride ?? (TursoConsumptionBrake.MaxLabBytesUpPerDay, TursoConsumptionBrake.MaxLabBytesDownPerDay);
        long used = upload ? up : down, max = upload ? maxUp : maxDown;
        notice = used >= max
            ? $"Se llegó al tope de {(upload ? "subida" : "bajada")} de resultados de laboratorio de hoy ({LabFileImporter.Megabytes(max)}); sigue mañana."
            : string.Empty;
        return used >= max;
    }

    // =========================================================================
    // TOPE, DESALOJO Y HUÉRFANOS (la PC principal)
    // =========================================================================

    /// <summary>Lo último que se midió en la nube de laboratorio (también al reabrir el programa).</summary>
    public LabCloudSummary? LabCloud => _settingsService.CurrentSettings.LabCloud;

    private async Task<LabUsage> MeasureLabCloudAsync(TursoClient client, bool daily, CancellationToken ct)
    {
        var statements = new List<(string, object?[]?)> { (LabUsageSql, null) };
        statements.AddRange(CloudSpaceSql.Select(s => (s, (object?[]?)null)));
        var results = await client.QueryBatchAsync(statements, ct);

        long Scalar(int i) => results[i].FirstOrDefault()?.Values.FirstOrDefault() is { } value
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : 0;
        var usage = new LabUsage
        {
            Files = (int)LongOf(results[0].FirstOrDefault() ?? new(), "Files"),
            Bytes = LongOf(results[0].FirstOrDefault() ?? new(), "Bytes")
        };
        long pageSize = Scalar(3);
        if (pageSize > 0) usage.Space = (Scalar(1) - Scalar(2)) * pageSize;
        AddExternalUsage(usage.Files + 3, 0);
        SaveLabCloud(usage, daily);
        return usage;
    }

    private void SaveLabCloud(LabUsage usage, bool daily)
    {
        var previous = _settingsService.CurrentSettings.LabCloud;
        _settingsService.SaveLabCloud(new LabCloudSummary(usage.Files, usage.Bytes, usage.Space, DateTime.UtcNow,
            daily ? _settingsService.Today : previous?.MeasuredDay));
    }

    /// <summary>La revisión diaria de la PC principal: los huérfanos de 30 días y, si hace falta, el desalojo.</summary>
    private async Task RunLabMaintenanceAsync(TursoClient client, LabUsage usage, LabTransferReport report, CancellationToken ct)
    {
        var results = await client.TransactionAsync(LabOrphanSql.Select(s => (s, (object?[]?)null)), ct);
        report.OrphansDeleted = results[3].AffectedRows;
        AddExternalUsage(usage.Files * 2L + 2, results.Sum(r => r.AffectedRows));
        if (report.OrphansDeleted > 0)
        {
            AppLogger.Info($"Laboratorio: se borraron de la nube {report.OrphansDeleted} archivos que ningún resultado nombra hace {LabOrphanDays} días.");
            var measured = await MeasureLabCloudAsync(client, daily: true, ct);
            (usage.Files, usage.Bytes, usage.Space) = (measured.Files, measured.Bytes, measured.Space);
        }
        await EvictAsync(client, usage, 0, null, report, ct);
    }

    /// <summary>
    /// Desaloja de la nube los archivos más viejos que esta PC tiene verificados en su archivo y en el respaldo externo,
    /// hasta dejarla en el 90% del tope (y con lugar para <paramref name="needed"/>). Sin respaldo externo no desaloja nada.
    /// </summary>
    private async Task EvictAsync(TursoClient client, LabUsage usage, long needed, string? olderThan, LabTransferReport report, CancellationToken ct)
    {
        long target = Math.Min(LabCloudEvictTarget, LabCloudCap - needed);
        if (usage.Bytes <= target) return;

        var folder = _labResults!.BackupFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            report.Notice ??= string.IsNullOrWhiteSpace(folder)
                ? "La nube de laboratorio está casi llena y no hay respaldo externo: elija una carpeta en Configuración › Respaldos y datos para liberar espacio."
                : "La nube de laboratorio está casi llena y la carpeta del respaldo externo no está conectada: no se liberó espacio.";
            return;
        }

        var candidates = await client.QueryAsync(LabEvictionCandidatesSql, null, ct);
        AddExternalUsage(Math.Max(1, candidates.Count), 0);
        bool reachedNewer = false;
        foreach (var row in candidates)
        {
            if (usage.Bytes <= target) break;
            // Para subir un archivo no se desaloja uno más nuevo: si es el más viejo, el que se queda afuera es él
            if (olderThan != null && row.GetValueOrDefault("ResultDate") is string date && string.CompareOrdinal(date, olderThan) > 0)
            {
                reachedNewer = true;
                break;
            }
            var sha = row.GetValueOrDefault("Sha256")?.ToString() ?? string.Empty;
            if (!LabFileStore.IsValidHash(sha) || _labResults.Store.ReadVerified(sha) == null || !_labResults.Store.IsBackedUp(folder, sha)) continue;

            var results = await client.TransactionAsync(new (string, object?[]?)[]
            {
                (LabEvictChunksSql, new object?[] { sha }),
                (LabEvictSql, new object?[] { sha })
            }, ct);
            AddExternalUsage(2 + results[0].AffectedRows, results.Sum(r => r.AffectedRows));
            if (results[1].AffectedRows == 0) continue;
            usage.Bytes -= LongOf(row, "SizeBytes");
            usage.Files--;
            report.Evicted++;
        }
        SaveLabCloud(usage, daily: false);
        if (report.Evicted > 0) AppLogger.Info($"Laboratorio: se desalojaron de la nube {report.Evicted} archivos (están en esta PC y en el respaldo).");
        if (usage.Bytes > target && !reachedNewer)
            report.Notice ??= "La nube de laboratorio está casi llena y faltan archivos en el respaldo externo: pulse «Respaldar ahora» en Respaldos y datos.";
    }
}
