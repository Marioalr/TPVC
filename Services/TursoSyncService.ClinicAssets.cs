using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Paso 8.12 del plan de la Fase 8: el membrete en la nube. ClinicProfiles lleva el SHA-256 del escudo y de la imagen del
/// encabezado; los bytes viven en ClinicAssets (una fila por imagen, que nunca cambia) y ClinicAssetChunks (trozos de
/// 512 KiB). La PC sube una imagen una sola vez, antes del perfil que la nombra; los equipos la bajan solo cuando cambia
/// su hash, así que una sincronización normal no lee estas tablas.
/// </summary>
public partial class TursoSyncService
{
    /// <summary>Los hashes de las imágenes en la nube: null si ninguna PC con v1.29.0 lo ha escrito; "" sin imagen.</summary>
    internal readonly record struct LetterheadHashes(string? Crest, string? Header)
    {
        public string? For(LetterheadImageKind kind) => kind == LetterheadImageKind.Crest ? Crest : Header;
    }

    private static readonly LetterheadImageKind[] LetterheadKinds = { LetterheadImageKind.Crest, LetterheadImageKind.Header };

    /// <summary>Bajar una imagen (literal en contracts/hrana/statements.json): su fila y cada trozo por (Hash, Part).</summary>
    internal static readonly string AssetMetaSql = $"SELECT ByteCount, ChunkCount FROM {SyncSchema.ClinicAssetsTable} WHERE Hash = ?;";

    internal static readonly string AssetChunkSql = $"SELECT Content FROM {SyncSchema.ClinicAssetChunksTable} WHERE Hash = ? AND Part = ?;";

    /// <summary>Imágenes que no se pudieron bajar completas en esta sesión: no se reintentan hasta reiniciar (cada intento costaría todos sus trozos).</summary>
    private readonly HashSet<string> _failedAssetHashes = new(StringComparer.Ordinal);

    private static LetterheadHashes RemoteLetterheadHashes(Dictionary<string, object?>? remote) => remote == null
        ? new LetterheadHashes(null, null)
        : new LetterheadHashes(remote.GetValueOrDefault("CrestImageHash")?.ToString(), remote.GetValueOrDefault("HeaderImageHash")?.ToString());

    private static string HashColumn(LetterheadImageKind kind) => kind == LetterheadImageKind.Crest ? "CrestImageHash" : "HeaderImageHash";

    private static string KindName(LetterheadImageKind kind) => kind == LetterheadImageKind.Crest ? "crest" : "header";

    /// <summary>
    /// Lo que esta PC escribe en el perfil de la nube para cada imagen, después de subir las que falten: la suya si tiene;
    /// "" si la quitó aquí; si no la tocó aquí, la de la nube (una PC recién instalada no le quita el escudo a las demás).
    /// </summary>
    private async Task<LetterheadHashes> PrepareLetterheadForPushAsync(TursoClient client, LetterheadHashes remote, CancellationToken ct)
    {
        var profile = _clinicProfileService.CurrentProfile;
        var result = new Dictionary<LetterheadImageKind, string?>();
        foreach (var kind in LetterheadKinds)
        {
            var local = _clinicProfileService.GetLetterheadHash(kind);
            bool editedHere = kind == LetterheadImageKind.Crest ? profile.CrestImageEditedHere : profile.HeaderImageEditedHere;
            if (local.Length > 0 && await EnsureAssetUploadedAsync(client, kind, local, ct))
            {
                result[kind] = local;
            }
            else
            {
                result[kind] = local.Length == 0 && editedHere ? string.Empty : remote.For(kind);
            }
        }
        return new LetterheadHashes(result[LetterheadImageKind.Crest], result[LetterheadImageKind.Header]);
    }

    /// <summary>
    /// Deja las imágenes de esta PC como dice la nube: baja la que cambió de hash o quita la que la nube ya no tiene. Si la
    /// nube todavía no sabe nada de una imagen (NULL, de antes de v1.29.0) y esta PC la tiene, la sube y la anota.
    /// </summary>
    private async Task ReconcileLetterheadAsync(TursoClient client, LetterheadHashes remote, CancellationToken ct)
    {
        foreach (var kind in LetterheadKinds)
        {
            var remoteHash = remote.For(kind);
            var local = _clinicProfileService.GetLetterheadHash(kind);
            if (remoteHash == null)
            {
                if (local.Length == 0 || !await EnsureAssetUploadedAsync(client, kind, local, ct)) continue;
                // Solo si sigue sin hash: otra PC pudo anotar el suyo mientras tanto
                await client.ExecuteAsync($"UPDATE ClinicProfiles SET {HashColumn(kind)} = ? WHERE Id = 1 AND {HashColumn(kind)} IS NULL;",
                    new object?[] { local }, ct);
                AddExternalUsage(1, 1);
                AppLogger.Info($"Turso: la imagen del membrete ({KindName(kind)}) de esta PC quedó en la nube.");
                continue;
            }
            if (string.Equals(remoteHash, local, StringComparison.OrdinalIgnoreCase)) continue;

            if (remoteHash.Length == 0)
            {
                RunOnUiThread(() => _clinicProfileService.ApplyLetterheadImageFromCloud(kind, null));
                AppLogger.Info($"Turso: se quitó la imagen del membrete ({KindName(kind)}), como en la nube.");
                continue;
            }

            var content = await DownloadAssetAsync(client, remoteHash, ct);
            if (content == null) continue;
            if (ClinicProfileService.ImageExtension(content) == null || content.Length > ClinicProfileService.MaxLetterheadImageBytes)
            {
                _failedAssetHashes.Add(remoteHash);
                AppLogger.Warn($"Turso: la imagen del membrete {remoteHash} no es PNG ni JPG; no se usa.");
                continue;
            }
            RunOnUiThread(() => _clinicProfileService.ApplyLetterheadImageFromCloud(kind, content));
            AppLogger.Info($"Turso: se bajó la imagen del membrete ({KindName(kind)}, {content.Length:N0} bytes).");
        }
    }

    /// <summary>
    /// La imagen de esta PC queda completa en la nube: si ya está (por su hash), no se vuelve a subir; si no, sube sus trozos
    /// (uno por petición) y al final su fila de ClinicAssets. false si no se pudo leer el archivo.
    /// </summary>
    private async Task<bool> EnsureAssetUploadedAsync(TursoClient client, LetterheadImageKind kind, string hash, CancellationToken ct)
    {
        var existing = await client.QueryAsync($"SELECT Hash FROM {SyncSchema.ClinicAssetsTable} WHERE Hash = ?;", new object?[] { hash }, ct);
        AddExternalUsage(1, 0);
        if (existing.Count > 0) return true;

        var content = _clinicProfileService.ReadLetterheadImage(kind);
        if (content == null || !string.Equals(ClinicProfileService.HashImage(content), hash, StringComparison.Ordinal))
        {
            AppLogger.Warn($"Turso: no se pudo leer la imagen del membrete ({KindName(kind)}) para subirla.");
            return false;
        }

        int chunks = Math.Max(1, (content.Length + SyncSchema.AssetChunkBytes - 1) / SyncSchema.AssetChunkBytes);
        for (int part = 0; part < chunks; part++)
        {
            var piece = content.AsSpan(part * SyncSchema.AssetChunkBytes, Math.Min(SyncSchema.AssetChunkBytes, content.Length - part * SyncSchema.AssetChunkBytes)).ToArray();
            await client.ExecuteAsync(
                $"INSERT INTO {SyncSchema.ClinicAssetChunksTable} (Hash, Part, Content) VALUES (?, ?, ?) ON CONFLICT(Hash, Part) DO NOTHING;",
                new object?[] { hash, part, piece }, ct);
        }
        var mime = ClinicProfileService.ImageExtension(content) == ".png" ? "image/png" : "image/jpeg";
        await client.ExecuteAsync(
            $"INSERT INTO {SyncSchema.ClinicAssetsTable} (Hash, Kind, MimeType, ByteCount, ChunkCount, UpdatedAtUtc) VALUES (?, ?, ?, ?, ?, {ServerNowSql}) " +
            "ON CONFLICT(Hash) DO NOTHING;",
            new object?[] { hash, KindName(kind), mime, content.Length, chunks }, ct);
        AddExternalUsage(0, chunks + 1);
        AppLogger.Info($"Turso: se subió la imagen del membrete ({KindName(kind)}, {content.Length:N0} bytes en {chunks} trozos).");
        return true;
    }

    /// <summary>
    /// Baja una imagen completa (su fila de ClinicAssets y sus trozos, cuatro por petición) y revisa su tamaño y su
    /// SHA-256. null si todavía no está completa en la nube (se reintenta en la siguiente sincronización) o si llegó mal.
    /// </summary>
    private async Task<byte[]?> DownloadAssetAsync(TursoClient client, string hash, CancellationToken ct)
    {
        if (_failedAssetHashes.Contains(hash)) return null;
        var meta = (await client.QueryAsync(AssetMetaSql, new object?[] { hash }, ct))
            .FirstOrDefault();
        AddExternalUsage(1, 0);
        if (meta == null)
        {
            AppLogger.Info($"Turso: la imagen del membrete {hash} todavía no está completa en la nube; se reintenta después.");
            return null;
        }

        long byteCount = Convert.ToInt64(meta["ByteCount"], System.Globalization.CultureInfo.InvariantCulture);
        int chunkCount = Convert.ToInt32(meta["ChunkCount"], System.Globalization.CultureInfo.InvariantCulture);
        var parts = new List<byte[]>();
        for (int first = 0; first < chunkCount; first += 4)
        {
            var statements = Enumerable.Range(first, Math.Min(4, chunkCount - first))
                .Select(part => (AssetChunkSql, (object?[]?)new object?[] { hash, part }))
                .ToList();
            var results = await client.QueryBatchAsync(statements, ct);
            AddExternalUsage(statements.Count, 0);
            foreach (var rows in results)
            {
                if (rows.FirstOrDefault()?.GetValueOrDefault("Content") is not byte[] piece)
                {
                    _failedAssetHashes.Add(hash);
                    AppLogger.Warn($"Turso: a la imagen del membrete {hash} le falta un trozo en la nube.");
                    return null;
                }
                parts.Add(piece);
            }
        }

        var content = parts.SelectMany(p => p).ToArray();
        if (content.Length != byteCount || !string.Equals(ClinicProfileService.HashImage(content), hash, StringComparison.Ordinal))
        {
            _failedAssetHashes.Add(hash);
            AppLogger.Warn($"Turso: la imagen del membrete {hash} llegó con otro tamaño o contenido; no se usa.");
            return null;
        }
        return content;
    }

    /// <summary>
    /// Después de cambiar una imagen: borra de la nube las completas que el perfil ya no nombra. Los trozos de una subida a
    /// medias (sin su fila de ClinicAssets) no se tocan: la PC que sube los termina.
    /// </summary>
    private async Task DeleteUnusedClinicAssetsAsync(TursoClient client, CancellationToken ct)
    {
        const string inUse = "(SELECT ifnull(CrestImageHash, '') FROM ClinicProfiles WHERE Id = 1 UNION SELECT ifnull(HeaderImageHash, '') FROM ClinicProfiles WHERE Id = 1)";
        var results = await client.TransactionAsync(new (string, object?[]?)[]
        {
            ($"DELETE FROM {SyncSchema.ClinicAssetChunksTable} WHERE Hash IN (SELECT Hash FROM {SyncSchema.ClinicAssetsTable} WHERE Hash NOT IN {inUse});", null),
            ($"DELETE FROM {SyncSchema.ClinicAssetsTable} WHERE Hash NOT IN {inUse};", null)
        }, ct);
        int deleted = results.Sum(r => r.AffectedRows);
        AddExternalUsage(4, deleted);
        if (deleted > 0) AppLogger.Info($"Turso: se borraron de la nube {results[^1].AffectedRows} imágenes del membrete que ya no se usan.");
    }
}
