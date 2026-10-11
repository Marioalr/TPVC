using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;

// El SQL dinámico de este archivo solo usa nombres de tablas y columnas del modelo de EF;
// los datos van siempre como parámetros.
#pragma warning disable EF1002, EF1003

namespace TPVC.Services;

/// <summary>Lo que se perdería de una tabla al reemplazar la nube con un respaldo.</summary>
public sealed record CloudLossLine(string Label, int Rows, int UndoneDeletes)
{
    private string Undone => UndoneDeletes == 1 ? "1 borrado que se deshace" : $"{UndoneDeletes:N0} borrados que se deshacen";

    public string Text => (Rows, UndoneDeletes) switch
    {
        (> 0, > 0) => $"{Label}: {Rows:N0} (y {Undone})",
        (> 0, _) => $"{Label}: {Rows:N0}",
        _ => $"{Label}: {Undone}"
    };
}

/// <summary>Lo que la nube tiene y el respaldo restaurado no: se perdería al reemplazarla (paso 8.9).</summary>
public sealed class CloudReplacementPreview
{
    public IReadOnlyList<CloudLossLine> Lines { get; init; } = Array.Empty<CloudLossLine>();
    public int LostRows => Lines.Sum(l => l.Rows);
    public int UndoneDeletes => Lines.Sum(l => l.UndoneDeletes);
    public bool LosesNothing => LostRows == 0 && UndoneDeletes == 0;

    public string Summary => LosesNothing
        ? "La nube no tiene nada más nuevo que este respaldo: no se pierde nada."
        : "La nube tiene cambios que este respaldo no tiene (de esta y de otras computadoras). Al reemplazarla se guardan en un " +
          "archivo y aquí quedan para «Volver a aplicar»:\n• " + string.Join("\n• ", Lines.Select(l => l.Text));
}

/// <summary>Cómo terminó «Reemplazar la nube con este respaldo».</summary>
public sealed class CloudReplacementResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>El archivo con lo que la nube tenía y el respaldo no (null si no había nada).</summary>
    public string? LostFile { get; init; }
    public CloudReplacementPreview? Lost { get; init; }
    public TursoFullSyncReport? Report { get; init; }
}

/// <summary>Los cambios guardados para «Volver a aplicar» (paso 8.9).</summary>
public sealed record SavedChangesSummary(int FromThisDevice, int FromCloud, int FromFiles, IReadOnlyList<string> Lines)
{
    public int Total => FromThisDevice + FromCloud + FromFiles;
}

/// <summary>
/// Paso 8.9 del plan de la Fase 8: restaurar un respaldo con la nube activa. La nube tiene una generación
/// (_tpvc_meta.generation): «Reemplazar la nube con este respaldo» escribe una nueva, la vacía y sube el respaldo, y al
/// final escribe generationReady. Cada escritura de cualquier equipo lleva la cerca de generación
/// (<see cref="SyncSchema.GenerationFenceSql"/>) y cada petición termina leyéndola. Un equipo que ve otra generación guarda
/// lo que no había subido, espera a generationReady, vuelve a bajar todo y ofrece «Volver a aplicar N cambios».
/// </summary>
public partial class TursoSyncService
{
    /// <summary>Otra PC reemplazó la nube mientras este equipo subía o bajaba: se detuvo sin escribir.</summary>
    public const string GenerationChangedCode = "GENERATION_CHANGED";

    /// <summary>Otra PC está reemplazando la nube con un respaldo y todavía no termina: se espera.</summary>
    public const string GenerationPendingCode = "GENERATION_PENDING";

    /// <summary>Se restauró un respaldo con la nube configurada y no se ha elegido qué hacer con ella.</summary>
    public const string RestorePendingCode = "RESTORE_PENDING";

    public const string RestorePendingMessage =
        "Se restauró un respaldo en esta computadora y la nube sigue con los datos de antes. Para no mezclarlos no se sincroniza: " +
        "en Respaldos y datos elija «Reemplazar la nube con este respaldo» o «Desconectar esta PC de la nube».";

    private const string NoGeneration = "none";

    // La generación de la nube con que escribe esta sincronización (cerca de generación) y si ya se revisó
    private string? _syncGeneration;
    private bool _generationChecked;

    /// <summary>La nube cambió de generación y ya está completa: hay que vaciar esta base y volver a bajar todo.</summary>
    private sealed class GenerationReloadException(CloudHandshake handshake) : Exception("La nube se reemplazó con un respaldo.")
    {
        public CloudHandshake Handshake { get; } = handshake;
    }

    /// <summary>¿Hay un respaldo restaurado esperando que se elija qué hacer con la nube?</summary>
    public bool IsRestoreDecisionPending
    {
        get
        {
            try
            {
                using var db = _contextFactory.CreateDbContext();
                return DbMeta.Get(db, DbMeta.RestoredWithCloud) == "1";
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>¿Hay un «Reemplazar la nube» que se cortó y se termina en la siguiente sincronización?</summary>
    public bool IsCloudReplacementPending
    {
        get
        {
            try
            {
                using var db = _contextFactory.CreateDbContext();
                return DbMeta.Get(db, DbMeta.ReplacingCloud) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// La sincronización está en espera porque otra PC reemplaza la nube (<see cref="GenerationPendingCode"/>); se
    /// actualiza con cada sincronización.
    /// </summary>
    public bool IsWaitingForCloudReplacement { get; private set; }

    // =========================================================================
    // LA GENERACIÓN EN CADA SINCRONIZACIÓN
    // =========================================================================

    private static string? NormalizeGeneration(string? generation) =>
        string.IsNullOrWhiteSpace(generation) || generation == NoGeneration ? null : generation;

    /// <summary>
    /// Con la primera instrucción de la sincronización (paso 8.9). La misma generación: se escribe con su cerca. Sin
    /// generación guardada aquí (la primera vez con esta versión) se adopta la de la nube si nunca se reemplazó. Otra
    /// generación: se guarda lo que no subió y, si el reemplazo ya terminó, se vuelve a bajar todo (si no, se espera).
    /// </summary>
    private async Task CheckGenerationAsync(AppDbContext db, CloudHandshake handshake, CancellationToken ct)
    {
        var cloud = NormalizeGeneration(handshake.Generation);
        var local = await DbMeta.GetAsync(db, DbMeta.Generation, ct);
        IsWaitingForCloudReplacement = false;

        if (cloud == null || local == cloud)
        {
            _syncGeneration = cloud;
            _generationChecked = true;
            return;
        }

        if (local == null && NormalizeGeneration(handshake.GenerationPrevious) == null && cloud == handshake.GenerationReady)
        {
            // Primera sincronización con el paso 8.9, o la identidad recién escrita: la nube nunca se reemplazó
            await DbMeta.SetAsync(db, DbMeta.Generation, cloud, ct);
            _syncGeneration = cloud;
            _generationChecked = true;
            return;
        }

        // Otra PC reemplazó la nube con un respaldo: nada de aquí se escribe en ella
        int saved = await StashPendingChangesAsync(db, local ?? NormalizeGeneration(handshake.GenerationPrevious), ct);
        if (cloud != handshake.GenerationReady)
        {
            IsWaitingForCloudReplacement = true;
            throw new TursoException(
                "Otra computadora está reemplazando la nube con un respaldo. La sincronización sigue sola cuando termine" +
                (saved > 0 ? $"; los {saved:N0} cambios de aquí que no habían subido quedaron guardados para «Volver a aplicar»." : "."),
                GenerationPendingCode);
        }
        throw new GenerationReloadException(handshake);
    }

    /// <summary>La generación que regresó la última instrucción de una petición: si no es la de esta sincronización, se detiene.</summary>
    private void EnsureSameGeneration(List<Dictionary<string, object?>> rows) =>
        EnsureSameGeneration(rows.FirstOrDefault()?.GetValueOrDefault("generation")?.ToString());

    private void EnsureSameGeneration(string? cloudGeneration)
    {
        if (!_generationChecked || NormalizeGeneration(cloudGeneration) == _syncGeneration) return;
        throw new TursoException(
            "La nube cambió mientras se sincronizaba: otra computadora la reemplazó con un respaldo. No se escribió nada más; " +
            "la siguiente sincronización lo resuelve.", GenerationChangedCode);
    }

    /// <summary>
    /// Guarda en <see cref="SyncSavedChanges"/> lo que este equipo no alcanzó a subir (lo anotado después del cursor y lo
    /// apartado al subir): cada fila completa con el formato de la nube y el protocolo con que se escribió, o su borrado.
    /// Se puede llamar varias veces: cada registro se guarda una vez, con su versión más reciente.
    /// </summary>
    private async Task<int> StashPendingChangesAsync(AppDbContext db, string? generation, CancellationToken ct)
    {
        var cursor = await SyncChangeLog.ReadPushCursorAsync(db, ct);
        if (cursor == null)
        {
            // Nunca subió con el registro de cambios: lo que tiene vino de la nube o se sube completo, no hay pendientes
            return 0;
        }

        var keys = (await SyncChangeLog.ReadSinceAsync(db, cursor.Value, ct))
            .Select(e => (e.EntityType, SyncId: Guid.TryParse(e.SyncId, out var g) ? g : Guid.Empty))
            .Concat((await db.SyncParkedRows.AsNoTracking().Where(p => p.Direction == SyncParkedRow.Push).ToListAsync(ct))
                .Select(p => (p.EntityType, p.SyncId)))
            .Where(k => k.SyncId != Guid.Empty)
            .Distinct()
            .ToList();
        if (keys.Count == 0) return 0;

        var changes = new List<SavedChange>();
        var idMaps = new Dictionary<Type, Dictionary<int, Guid>>();
        var savedAt = FormatDate(DateTime.UtcNow);
        foreach (var map in GetTableMaps())
        {
            var ids = keys.Where(k => k.EntityType == map.ClrType.Name).Select(k => k.SyncId).ToList();
            if (ids.Count == 0) continue;

            var rows = await LoadRowsAsRemoteAsync(db, map, ids, idMaps, ct);
            foreach (var id in ids)
            {
                if (rows.TryGetValue(id, out var row))
                {
                    changes.Add(SavedChangeOf(SyncSavedChanges.SourceThisDevice, generation, map.ClrType.Name, id, SyncSavedChanges.KindRow, SerializeRemoteRow(row), savedAt));
                    continue;
                }
                var deletedAt = await db.SyncTombstones.AsNoTracking()
                    .Where(t => t.EntityType == map.ClrType.Name && t.SyncId == id)
                    .OrderByDescending(t => t.DeletedAtUtc)
                    .Select(t => (DateTime?)t.DeletedAtUtc)
                    .FirstOrDefaultAsync(ct);
                changes.Add(SavedChangeOf(SyncSavedChanges.SourceThisDevice, generation, map.ClrType.Name, id, SyncSavedChanges.KindTombstone,
                    FormatDate(deletedAt ?? DateTime.UtcNow), savedAt));
            }
        }

        await SyncSavedChanges.SaveAsync(db, changes, ct);
        RefreshSavedChangesCount();
        AppLogger.Warn($"Turso: la nube cambió de generación; {changes.Count} cambios de este equipo que no habían subido quedaron guardados para volver a aplicarlos.");
        return changes.Count;
    }

    private static SavedChange SavedChangeOf(string source, string? generation, string entityType, Guid syncId, string kind, string payload, string savedAt) => new()
    {
        Source = source,
        Generation = generation,
        EntityType = entityType,
        SyncId = FormatGuid(syncId),
        Kind = kind,
        Payload = payload,
        Protocol = ClientProtocol,
        Contract = SyncContract.Revision,
        SavedAtUtc = savedAt
    };

    /// <summary>Las filas locales con esos SyncId en el formato con que suben a la nube (las referencias como SyncId).</summary>
    private async Task<Dictionary<Guid, Dictionary<string, object?>>> LoadRowsAsRemoteAsync(AppDbContext db, TableMap map, List<Guid> ids,
        Dictionary<Type, Dictionary<int, Guid>> idMaps, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(LoadEntitiesGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(map.ClrType);
        var entities = await (Task<List<ISyncEntity>>)method.Invoke(null, new object[] { db, ids, ct })!;

        var result = new Dictionary<Guid, Dictionary<string, object?>>();
        foreach (var entity in entities)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["SyncId"] = FormatGuid(entity.SyncId),
                ["UpdatedAtUtc"] = FormatDate(entity.UpdatedAtUtc)
            };
            foreach (var column in map.Columns) row[column.Name] = ToRemoteValue(column.Property.GetValue(entity));
            foreach (var reference in map.References)
            {
                Guid? principal = null;
                if (reference.Property.GetValue(entity) is int localId && localId > 0)
                {
                    if (!idMaps.TryGetValue(reference.PrincipalType, out var lookup))
                    {
                        lookup = await LoadIdToSyncIdAsync(db, reference.PrincipalType, ct);
                        idMaps[reference.PrincipalType] = lookup;
                    }
                    if (lookup.TryGetValue(localId, out var g)) principal = g;
                }
                row[reference.RemoteColumn] = principal is { } p ? FormatGuid(p) : null;
            }
            result[entity.SyncId] = row;
        }
        return result;
    }

    private static async Task<List<ISyncEntity>> LoadEntitiesGenericAsync<T>(AppDbContext db, List<Guid> ids, CancellationToken ct)
        where T : class, ISyncEntity =>
        (await db.Set<T>().AsNoTracking().Where(e => ids.Contains(EF.Property<Guid>(e, nameof(ISyncEntity.SyncId)))).ToListAsync(ct))
        .Cast<ISyncEntity>().ToList();

    /// <summary>
    /// La nube se reemplazó con un respaldo y ya está completa: respalda esta base, la vacía (sin lápidas ni anotaciones;
    /// lo pendiente ya quedó guardado) y deja la generación nueva y la bajada completa para la sincronización que sigue.
    /// </summary>
    private async Task ReloadNewGenerationAsync(CloudHandshake handshake, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        string backup;
        try
        {
            backup = new BackupService(db.Database.GetDbConnection().DataSource).CreateNamedBackup("antes-de-bajar-la-nube-reemplazada");
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo respaldar la base antes de volver a bajar la nube reemplazada", ex);
            throw new TursoException($"La nube se reemplazó con un respaldo, pero no se pudo respaldar esta base antes de volver a bajarla: {ex.Message}");
        }

        await ClearForCloudDownloadAsync(db, handshake.ClinicId, NormalizeGeneration(handshake.Generation), replace: true, ct);
        var settings = _settingsService.CurrentSettings;
        settings.LastPulledServerWatermark = null;
        _settingsService.SaveSettings(settings);
        IsWaitingForCloudReplacement = false;
        NotifyLocalViews();
        AppLogger.Info($"Turso: la nube se reemplazó con un respaldo (generación {handshake.Generation}); esta base se vacía y se vuelve a bajar. Respaldo: {backup}");
    }

    // =========================================================================
    // REEMPLAZAR LA NUBE CON EL RESPALDO RESTAURADO
    // =========================================================================

    /// <summary>
    /// Lo que se perdería al reemplazar la nube con lo que hay aquí (el respaldo restaurado): las filas de la nube que no
    /// están aquí o son más nuevas, y los borrados de la nube de filas que aquí siguen. Lee toda la nube una vez.
    /// </summary>
    public async Task<(bool Ok, string Message, CloudReplacementPreview? Preview)> PreviewCloudReplacementAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        if (string.IsNullOrWhiteSpace(settings.DatabaseUrl)) return (false, "Esta computadora no tiene nube configurada.", null);
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5), ct)) return (false, "Ya hay una sincronización en curso. Intente en un momento.", null);
        try
        {
            using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
            await EnsureRemoteSchemaAsync(client, ct);
            _generationChecked = false;
            var (lost, preview, rowsRead) = await ReadCloudLossAsync(client, ct);
            _settingsService.RecordUsage(rowsRead + HandshakeKeys.Length, 0);
            return (true, preview.Summary, preview);
        }
        catch (TursoException ex)
        {
            return (false, ex.Message, null);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// «Reemplazar la nube con este respaldo» (paso 8.9): escribe una generación nueva (la cerca detiene a los demás
    /// equipos), guarda en un archivo lo que la nube tenía y el respaldo no, vacía la nube, sube todo lo de aquí, revisa
    /// los conteos y al final escribe generationReady. Si se corta, la siguiente sincronización lo termina.
    /// </summary>
    public async Task<CloudReplacementResult> ReplaceCloudWithLocalAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        if (string.IsNullOrWhiteSpace(settings.DatabaseUrl)) return new CloudReplacementResult { Message = "Esta computadora no tiene nube configurada." };
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return new CloudReplacementResult { Message = "Ya hay una sincronización en curso. Intente en un momento." };
        try
        {
            string generation;
            using (var db = _contextFactory.CreateDbContext())
            {
                // Uno que se cortó se termina con su misma generación
                generation = await DbMeta.GetAsync(db, DbMeta.ReplacingCloud, ct) ?? Guid.NewGuid().ToString();
                await DbMeta.SetAsync(db, DbMeta.ReplacingCloud, generation, ct);
            }
            SetStatus(TursoSyncStatus.Syncing, "Reemplazando la nube con este respaldo...");
            var result = await Task.Run(() => RunCloudReplacementAsync(generation, ct), ct);
            SetStatus(TursoSyncStatus.Success, "✓ La nube quedó igual a este respaldo");
            NotifyLocalViews();
            return result;
        }
        catch (TursoException ex)
        {
            AppLogger.Error("No se terminó de reemplazar la nube con el respaldo", ex);
            SetStatus(TursoSyncStatus.Error, "El reemplazo de la nube no terminó: se continúa en la siguiente sincronización");
            return new CloudReplacementResult
            {
                Message = $"No se terminó de reemplazar la nube: {ex.Message} Se continúa en la siguiente sincronización; mientras tanto los " +
                          "demás equipos esperan y no bajan una nube incompleta."
            };
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Termina un reemplazo que se cortó (desde la sincronización, con el candado ya tomado).</summary>
    private async Task ContinueCloudReplacementAsync(string generation, CancellationToken ct)
    {
        AppLogger.Info($"Turso: se continúa el reemplazo de la nube con el respaldo (generación {generation}).");
        await RunCloudReplacementAsync(generation, ct);
    }

    private async Task<CloudReplacementResult> RunCloudReplacementAsync(string generation, CancellationToken ct)
    {
        var settings = _settingsService.CurrentSettings;
        using var client = ClientFactory(settings.DatabaseUrl, settings.AuthToken);
        await EnsureRemoteSchemaAsync(client, ct);
        var (handshake, _, _) = await ReadHandshakeAsync(client, ct);
        int rowsRead = HandshakeKeys.Length, rowsWritten = 0;
        if (handshake.MinClientProtocol is { } minimum && minimum > ClientProtocol)
        {
            throw new TursoException("Actualice TPVC en esta computadora: la nube ya usa una versión más nueva de la sincronización.", ClientOutdatedCode);
        }

        var meta = Q(MetaTable);
        string? lostFile = null;
        CloudReplacementPreview? lost = null;
        if (handshake.GenerationReady != generation)
        {
            // 1. La generación nueva: desde aquí la cerca rechaza lo que suban los demás equipos (y los que no la conocen,
            //    con el protocolo 2, deben actualizar)
            if (handshake.Generation != generation)
            {
                var previous = NormalizeGeneration(handshake.Generation) ?? NoGeneration;
                await client.TransactionAsync(new (string, object?[]?)[]
                {
                    ($"INSERT INTO {meta} (\"Key\", \"Value\") VALUES ('generationPrevious', ?) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";", new object?[] { previous }),
                    ($"INSERT INTO {meta} (\"Key\", \"Value\") VALUES ('generation', ?) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";", new object?[] { generation }),
                    (AtLeastSql(meta, "minClientProtocol"), new object?[] { GenerationProtocol.ToString(CultureInfo.InvariantCulture) }),
                    (AtLeastSql(meta, "protocol"), new object?[] { GenerationProtocol.ToString(CultureInfo.InvariantCulture) })
                }, ct);
                rowsWritten += 4;
                AppLogger.Info($"Turso: la nube pasa a la generación {generation} (antes {previous}); los demás equipos dejan de escribir.");
            }
            _syncGeneration = generation;
            _generationChecked = true;

            // 2. Lo que la nube tiene y el respaldo no: a un archivo y a «Volver a aplicar». Una sola vez por reemplazo
            using (var db = _contextFactory.CreateDbContext())
            {
                var saved = await DbMeta.GetAsync(db, DbMeta.ReplaceLostFile, ct);
                if (saved == null)
                {
                    var (changes, preview, read) = await ReadCloudLossAsync(client, ct, NormalizeGeneration(handshake.GenerationPrevious) ?? NormalizeGeneration(handshake.Generation));
                    rowsRead += read;
                    lost = preview;
                    if (changes.Count > 0)
                    {
                        var dir = new BackupService(db.Database.GetDbConnection().DataSource).BackupDirectory;
                        Directory.CreateDirectory(dir);
                        lostFile = Path.Combine(dir, $"nube-antes-de-reemplazar-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                        SyncSavedChanges.WriteFile(lostFile, changes);
                        await SyncSavedChanges.SaveAsync(db, changes, ct);
                        RefreshSavedChangesCount();
                    }
                    await DbMeta.SetAsync(db, DbMeta.ReplaceLostFile, lostFile ?? "-", ct);
                    AppLogger.Info($"Turso: lo que la nube tenía y el respaldo no: {changes.Count} cambios" + (lostFile != null ? $", guardados en {lostFile}." : "."));
                }
                else if (saved != "-")
                {
                    lostFile = saved;
                }
            }

            // 3. La nube vacía (también las lápidas), solo si sigue en esta generación
            var tables = GetTableMaps();
            var deletes = tables.Reverse().Select(m => (FullScanMarker + SyncSchema.DeleteAllFencedSql(m.Table), (object?[]?)new object?[] { generation }))
                .Append((FullScanMarker + SyncSchema.DeleteAllFencedSql(TombstonesTable), new object?[] { generation }))
                .Append((SyncSchema.GenerationSql, null))
                .ToList();
            var deleted = await client.TransactionAsync(deletes, ct);
            EnsureSameGeneration(deleted[^1].Rows);
            rowsWritten += deleted.Take(deleted.Count - 1).Sum(r => r.AffectedRows);

            // 4. Todo lo de aquí sube (como la primera subida: el registro se llena de nuevo), con la cerca de la generación nueva
            using (var db = _contextFactory.CreateDbContext())
            {
                DbMeta.Remove(db, DbMeta.LastPushedSeq);
            }
            var (pushed, _) = await PushLocalChangesAsync(client, settings.DeviceId, fullResync: true, ct);
            rowsWritten += pushed;

            // 5. Lista: los demás equipos ya pueden volver a bajar
            var ready = await client.TransactionAsync(new (string, object?[]?)[]
            {
                ($"INSERT INTO {meta} (\"Key\", \"Value\") SELECT 'generationReady', ? WHERE {SyncSchema.GenerationFenceSql} " +
                 "ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";", new object?[] { generation, generation }),
                ($"INSERT INTO {meta} (\"Key\", \"Value\") VALUES ('fullUploadAtUtc', {ServerNowSql}) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\";", null),
                (SyncSchema.GenerationSql, null)
            }, ct);
            EnsureSameGeneration(ready[^1].Rows);
            rowsWritten += 2;
        }

        // 6. Esta base queda con la generación nueva, sin volver a bajar lo que acaba de subir
        var (after, _, _) = await ReadHandshakeAsync(client, ct);
        rowsRead += HandshakeKeys.Length;
        using (var db = _contextFactory.CreateDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await DbMeta.SetAsync(db, DbMeta.Generation, generation, ct);
            if (after.ClinicId != null) await DbMeta.SetAsync(db, DbMeta.ClinicId, after.ClinicId, ct);
            foreach (var table in GetTableMaps().Select(m => m.Table).Append(TombstonesTable))
            {
                await DbMeta.SetAsync(db, DbMeta.PullCursorPrefix + table, new PullCursor(after.Watermark, string.Empty).Serialize(), ct);
            }
            foreach (var map in GetTableMaps())
            {
                await DbMeta.SetAsync(db, DbMeta.PullColumnsPrefix + map.Table, string.Join(",", ColumnsOf(map)), ct);
            }
            DbMeta.Remove(db, DbMeta.ReplacingCloud);
            DbMeta.Remove(db, DbMeta.ReplaceLostFile);
            DbMeta.Remove(db, DbMeta.RestoredWithCloud);
            await tx.CommitAsync(ct);
        }
        settings.LastPulledServerWatermark = after.Watermark;
        settings.LastSyncUtc = DateTime.UtcNow;
        _settingsService.RecordUsage(rowsRead, rowsWritten);
        _settingsService.SaveSettings(settings);

        // 7. El reporte de la sincronización completa (paso 8.7): los mismos conteos aquí y en la nube
        var report = await BuildFullSyncReportAsync(client, ct);
        _settingsService.RecordUsage(report.RowsRead, 0);
        AppLogger.Info($"Turso: la nube se reemplazó con el respaldo (generación {generation}).");
        return new CloudReplacementResult
        {
            Ok = true,
            LostFile = lostFile,
            Lost = lost,
            Report = report,
            Message = "✓ La nube quedó igual a este respaldo. Los demás equipos guardan lo que no habían subido, vuelven a bajar todo y " +
                      "ofrecen «Volver a aplicar»." + (lostFile != null ? $" Lo que la nube tenía y el respaldo no quedó en {Path.GetFileName(lostFile)} y aquí, para «Volver a aplicar»." : string.Empty) +
                      (report.IsClean ? string.Empty : " El reporte encontró diferencias: revíselo en Nube y dispositivos.")
        };
    }

    private static string AtLeastSql(string meta, string key) =>
        $"INSERT INTO {meta} (\"Key\", \"Value\") VALUES ('{key}', ?) ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = excluded.\"Value\" " +
        $"WHERE CAST({meta}.\"Value\" AS INTEGER) < CAST(excluded.\"Value\" AS INTEGER);";

    /// <summary>
    /// Lee toda la nube, tabla por tabla y por páginas, y la compara con lo de aquí: lo que se perdería al reemplazarla.
    /// Las filas van completas (para «Volver a aplicar») y los borrados con su DeletedAtUtc.
    /// </summary>
    private async Task<(List<SavedChange> Changes, CloudReplacementPreview Preview, int RowsRead)> ReadCloudLossAsync(TursoClient client, CancellationToken ct,
        string? generation = null)
    {
        var changes = new List<SavedChange>();
        var lines = new List<CloudLossLine>();
        var pageSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        var savedAt = FormatDate(DateTime.UtcNow);
        int rowsRead = 0;

        var remoteTombstones = await ReadWholeTableAsync(client, TombstonesTable, pageSizes, ct);
        rowsRead += remoteTombstones.Count;

        using var db = _contextFactory.CreateDbContext();
        foreach (var map in GetTableMaps())
        {
            var remote = await ReadWholeTableAsync(client, map.Table, pageSizes, ct);
            rowsRead += remote.Count;
            var local = await LoadStampsAsync(db, map.ClrType, ct);

            int rows = 0;
            foreach (var row in remote)
            {
                if (ParseGuid(row.GetValueOrDefault("SyncId")) is not { } id) continue;
                var stamp = ParseDate(row.GetValueOrDefault("UpdatedAtUtc"));
                if (local.TryGetValue(id, out var mine) && stamp <= mine) continue;
                rows++;
                changes.Add(SavedChangeOf(SyncSavedChanges.SourceCloud, generation, map.ClrType.Name, id, SyncSavedChanges.KindRow, SerializeRemoteRow(row), savedAt));
            }

            // Un borrado en la nube de algo que aquí sigue (y no es más nuevo que el borrado): al reemplazar se deshace
            int undone = 0;
            foreach (var t in remoteTombstones.Where(t => t.GetValueOrDefault("EntityType")?.ToString() == map.ClrType.Name))
            {
                if (ParseGuid(t.GetValueOrDefault("SyncId")) is not { } id || !local.TryGetValue(id, out var mine)) continue;
                var deletedAt = ParseDate(t.GetValueOrDefault("DeletedAtUtc"));
                if (!SyncRules.RemoteTombstoneDeletes(mine, deletedAt)) continue;
                undone++;
                changes.Add(SavedChangeOf(SyncSavedChanges.SourceCloud, generation, map.ClrType.Name, id, SyncSavedChanges.KindTombstone, FormatDate(deletedAt), savedAt));
            }
            if (rows > 0 || undone > 0) lines.Add(new CloudLossLine(Capitalize(LocalDataLabel(map.ClrType)), rows, undone));
        }
        return (changes, new CloudReplacementPreview { Lines = lines }, rowsRead);
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    /// <summary>Todas las filas de una tabla de la nube, por páginas (sin omitir las de este equipo).</summary>
    private async Task<List<Dictionary<string, object?>>> ReadWholeTableAsync(TursoClient client, string table, Dictionary<string, int> pageSizes, CancellationToken ct)
    {
        var all = new List<Dictionary<string, object?>>();
        var position = PullCursor.Start;
        while (true)
        {
            var (page, size) = await ReadPageAsync(client, table, position, string.Empty, skipOwnRows: false, pageSizes, ct);
            all.AddRange(page);
            if (page.Count < size) return all;
            var last = page[^1];
            position = new PullCursor(last.GetValueOrDefault("SyncedAtUtc")?.ToString() ?? string.Empty, last.GetValueOrDefault("SyncId")?.ToString() ?? string.Empty);
        }
    }

    private static async Task<Dictionary<Guid, DateTime>> LoadStampsAsync(AppDbContext db, Type clrType, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(LoadStampsGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(clrType);
        return await (Task<Dictionary<Guid, DateTime>>)method.Invoke(null, new object[] { db, ct })!;
    }

    private static async Task<Dictionary<Guid, DateTime>> LoadStampsGenericAsync<T>(AppDbContext db, CancellationToken ct) where T : class, ISyncEntity
    {
        var rows = await db.Set<T>().AsNoTracking().Select(e => new { e.SyncId, e.UpdatedAtUtc }).ToListAsync(ct);
        var map = new Dictionary<Guid, DateTime>();
        foreach (var r in rows) map[r.SyncId] = r.UpdatedAtUtc;
        return map;
    }

    /// <summary>
    /// «Desconectar esta PC de la nube» (paso 8.9): deja de sincronizar y olvida la nube (URL, token y su identidad). La
    /// nube y los demás equipos siguen como estaban. No se permite a medio reemplazo: dejaría la nube incompleta.
    /// </summary>
    public async Task<(bool Ok, string Message)> DisconnectFromCloudAsync(CancellationToken ct = default)
    {
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5), ct)) return (false, "Ya hay una sincronización en curso. Intente en un momento.");
        try
        {
            using (var db = _contextFactory.CreateDbContext())
            {
                if (await DbMeta.GetAsync(db, DbMeta.ReplacingCloud, ct) != null)
                {
                    return (false, "Hay un reemplazo de la nube a medias: primero sincronice para terminarlo (si no, los demás equipos esperarían para siempre).");
                }
                DbMeta.Remove(db, DbMeta.RestoredWithCloud);
                DbMeta.Remove(db, DbMeta.ClinicId);
                DbMeta.Remove(db, DbMeta.Generation);
                DbMeta.RemoveByPrefix(db, DbMeta.PullCursorPrefix);
                DbMeta.Remove(db, DbMeta.LastPushedSeq);
            }

            var settings = _settingsService.CurrentSettings;
            settings.IsSyncEnabled = false;
            settings.DatabaseUrl = string.Empty;
            settings.AuthToken = string.Empty;
            settings.LastPulledServerWatermark = null;
            _settingsService.SaveSettings(settings);
            IdentityConflict = null;
            _verifiedSchemaUrl = null;
            _registeredDeviceUrl = null;
            SetStatus(TursoSyncStatus.NotConfigured, "Sin configurar");
            AppLogger.Info("Turso: esta PC se desconectó de la nube después de restaurar un respaldo.");
            return (true, "✓ Esta PC ya no sincroniza: se quedó con el respaldo restaurado. La nube y los demás equipos siguen como estaban.");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    // =========================================================================
    // VOLVER A APLICAR
    // =========================================================================

    /// <summary>Cuántos cambios hay guardados para «Volver a aplicar» (en pantalla).</summary>
    public int SavedChangesCount { get; private set; }

    public int RefreshSavedChangesCount()
    {
        try
        {
            using var db = _contextFactory.CreateDbContext();
            SyncSavedChanges.EnsureTable(db);
            SavedChangesCount = SyncSavedChanges.CountAsync(db).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudieron contar los cambios guardados: {ex.Message}");
        }
        return SavedChangesCount;
    }

    public async Task<SavedChangesSummary> GetSavedChangesSummaryAsync(CancellationToken ct = default)
    {
        using var db = _contextFactory.CreateDbContext();
        var saved = await SyncSavedChanges.ReadAllAsync(db, ct);
        var labels = GetTableMaps().ToDictionary(m => m.ClrType.Name, m => Capitalize(LocalDataLabel(m.ClrType)));
        var lines = saved.GroupBy(s => s.EntityType)
            .Select(g => $"{labels.GetValueOrDefault(g.Key, g.Key)}: {g.Count(s => s.Kind == SyncSavedChanges.KindRow):N0}" +
                         (g.Any(s => s.Kind == SyncSavedChanges.KindTombstone) ? $" (y {g.Count(s => s.Kind == SyncSavedChanges.KindTombstone):N0} borrados)" : string.Empty))
            .ToList();
        return new SavedChangesSummary(
            saved.Count(s => s.Source == SyncSavedChanges.SourceThisDevice),
            saved.Count(s => s.Source == SyncSavedChanges.SourceCloud),
            saved.Count(s => s.Source != SyncSavedChanges.SourceThisDevice && s.Source != SyncSavedChanges.SourceCloud),
            lines);
    }

    /// <summary>
    /// «Volver a aplicar N cambios» (paso 8.9): cada fila guardada se escribe aquí como un cambio nuevo (con marca nueva,
    /// así gana y sube en la siguiente sincronización) y cada borrado guardado se vuelve a hacer. Las filas van en el
    /// orden de dependencias; los borrados, al revés.
    /// </summary>
    public async Task<(bool Ok, string Message)> ReapplySavedChangesAsync(CancellationToken ct = default)
    {
        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5), ct)) return (false, "Ya hay una sincronización en curso. Intente en un momento.");
        int rows = 0, deletes = 0;
        try
        {
            List<SavedChange> saved;
            using (var db = _contextFactory.CreateDbContext())
            {
                saved = await SyncSavedChanges.ReadAllAsync(db, ct);
            }
            if (saved.Count == 0) return (true, "No hay cambios guardados.");

            var maps = GetTableMaps();
            var syncIdMaps = new Dictionary<Type, Dictionary<Guid, int>>();
            foreach (var map in maps)
            {
                // La versión más reciente de cada registro (puede venir de varias fuentes)
                var group = saved.Where(s => s.EntityType == map.ClrType.Name && s.Kind == SyncSavedChanges.KindRow)
                    .Select(s => DeserializeRemoteRow(s.Payload))
                    .GroupBy(r => ParseGuid(r.GetValueOrDefault("SyncId")))
                    .Where(g => g.Key != null)
                    .Select(g => g.MaxBy(r => ParseDate(r.GetValueOrDefault("UpdatedAtUtc")))!)
                    .ToList();
                if (group.Count == 0) continue;
                rows += await ApplyPageAsync(map, group, syncIdMaps, null, ct, force: true);
                syncIdMaps.Remove(map.ClrType);
                await RestampAsync(map, group.Select(r => ParseGuid(r.GetValueOrDefault("SyncId"))!.Value).ToList(), ct);
            }

            foreach (var map in maps.Reverse())
            {
                var ids = saved.Where(s => s.EntityType == map.ClrType.Name && s.Kind == SyncSavedChanges.KindTombstone)
                    .Select(s => ParseGuid(s.SyncId))
                    .Where(g => g != null)
                    .Select(g => g!.Value)
                    .Distinct()
                    .ToList();
                if (ids.Count > 0) deletes += await DeleteAsLocalChangeAsync(map, ids, ct);
            }

            using (var db = _contextFactory.CreateDbContext())
            {
                await SyncSavedChanges.DeleteAsync(db, saved.Select(s => s.Id), ct);
            }
            RefreshSavedChangesCount();
            NotifyLocalViews();
            AppLogger.Info($"Turso: se volvieron a aplicar {rows} registros y {deletes} borrados guardados.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Error("No se pudieron volver a aplicar los cambios guardados", ex);
            return (false, $"No se pudieron volver a aplicar los cambios: {(ex.InnerException ?? ex).Message}");
        }
        finally
        {
            _syncLock.Release();
        }

        OnLocalDataChanged();
        return (true, $"✓ Se volvieron a aplicar {rows:N0} registros" + (deletes > 0 ? $" y {deletes:N0} borrados" : string.Empty) +
                      ": suben en la siguiente sincronización.");
    }

    /// <summary>Descarta los cambios guardados (el archivo de la PC que reemplazó la nube sigue en la carpeta de respaldos).</summary>
    public async Task DiscardSavedChangesAsync(CancellationToken ct = default)
    {
        using var db = _contextFactory.CreateDbContext();
        await SyncSavedChanges.DeleteAllAsync(db, ct);
        RefreshSavedChangesCount();
        AppLogger.Info("Turso: se descartaron los cambios guardados para volver a aplicar.");
    }

    /// <summary>Agrega los cambios de un archivo de «Reemplazar la nube» (de esta u otra PC) a los guardados.</summary>
    public async Task<(bool Ok, string Message)> ImportSavedChangesFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var changes = SyncSavedChanges.ReadFile(path);
            foreach (var c in changes) c.Source = Path.GetFileName(path);
            using var db = _contextFactory.CreateDbContext();
            await SyncSavedChanges.SaveAsync(db, changes, ct);
            RefreshSavedChangesCount();
            return (true, $"✓ {changes.Count:N0} cambios del archivo quedaron listos para «Volver a aplicar».");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, $"No se pudo leer el archivo: {ex.Message}");
        }
    }

    /// <summary>Una marca nueva a filas recién aplicadas sin anotar (como una edición hecha aquí): se anotan y suben.</summary>
    private async Task RestampAsync(TableMap map, List<Guid> ids, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(RestampGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(map.ClrType);
        await (Task)method.Invoke(null, new object[] { _contextFactory, ids, ct })!;
    }

    private static async Task RestampGenericAsync<T>(IDbContextFactory<AppDbContext> factory, List<Guid> ids, CancellationToken ct) where T : class, ISyncEntity
    {
        using var db = factory.CreateDbContext();
        foreach (var entity in await db.Set<T>().Where(e => ids.Contains(EF.Property<Guid>(e, nameof(ISyncEntity.SyncId)))).ToListAsync(ct))
        {
            db.Entry(entity).State = EntityState.Modified;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Borra aquí, como un borrado hecho en este equipo (con su lápida y su anotación): sube en la siguiente sincronización.</summary>
    private async Task<int> DeleteAsLocalChangeAsync(TableMap map, List<Guid> ids, CancellationToken ct)
    {
        var method = typeof(TursoSyncService).GetMethod(nameof(DeleteAsLocalChangeGenericAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(map.ClrType);
        return await (Task<int>)method.Invoke(null, new object[] { _contextFactory, ids, ct })!;
    }

    private static async Task<int> DeleteAsLocalChangeGenericAsync<T>(IDbContextFactory<AppDbContext> factory, List<Guid> ids, CancellationToken ct) where T : class, ISyncEntity
    {
        int deleted = 0;
        foreach (var id in ids)
        {
            using var db = factory.CreateDbContext();
            var entity = await db.Set<T>().FirstOrDefaultAsync(e => EF.Property<Guid>(e, nameof(ISyncEntity.SyncId)) == id, ct);
            if (entity == null) continue;
            try
            {
                db.Set<T>().Remove(entity);
                await db.SaveChangesAsync(ct);
                deleted++;
            }
            catch (DbUpdateException ex)
            {
                // P. ej. una categoría que todavía tiene productos: se deja como está
                AppLogger.Warn($"Turso: no se volvió a aplicar el borrado de {typeof(T).Name} {id}: {(ex.InnerException ?? ex).Message}");
            }
        }
        return deleted;
    }
}
