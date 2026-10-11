using System;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.LabFiles;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.12b: contracts/golden/lab-files.json, lo que la tablet necesita para subir y bajar resultados de laboratorio
/// igual que la PC: cómo se parte un archivo en trozos (con el SHA-256 de cada uno), el SQL literal de cada paso y los
/// topes. Sale de las mismas constantes que usa TursoSyncService.LabFiles.cs.
/// </summary>
public partial class GoldenVectorTests
{
    /// <summary>Un contenido que Kotlin puede generar igual: el byte i vale i % 251.</summary>
    private static byte[] PatternContent(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

    private static JsonArray Strings(params string[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    [TestMethod]
    public void LabFiles_MatchTheVectors()
    {
        const int chunk = SyncSchema.AssetChunkBytes;
        var cases = new JsonArray();
        foreach (var (name, size) in new[] { ("un byte", 1), ("justo un trozo", chunk), ("un trozo y un byte", chunk + 1), ("1.2 MB", 1_200_000) })
        {
            var content = PatternContent(size);
            int count = TursoSyncService.ChunkCountFor(size);
            var chunks = new JsonArray();
            for (int i = 0; i < count; i++)
            {
                var piece = content.AsSpan(i * chunk, Math.Min(chunk, size - i * chunk));
                chunks.Add(new JsonObject { ["chunkIndex"] = i, ["size"] = piece.Length, ["sha256"] = LabFileStore.HashOf(piece) });
            }
            Assert.AreEqual(size, chunks.Sum(c => c!["size"]!.GetValue<int>()));
            cases.Add(Case($"trozos · {name}", new JsonObject { ["sizeBytes"] = size, ["content"] = "byte i = i % 251" }, new JsonObject
            {
                ["sha256"] = LabFileStore.HashOf(content),
                ["chunkSize"] = chunk,
                ["chunkCount"] = count,
                ["chunks"] = chunks
            }));
        }

        cases.Add(Case("sql", new JsonObject(), new JsonObject
        {
            ["register"] = TursoSyncService.LabRegisterSql,
            ["registerArgs"] = Strings("Sha256", "SizeBytes", "ChunkSize", "ChunkCount", "MimeType", "OriginDeviceId"),
            ["state"] = TursoSyncService.LabStateSql,
            ["existingChunks"] = TursoSyncService.LabExistingChunksSql,
            ["insertChunk"] = TursoSyncService.LabInsertChunkSql,
            ["insertChunkArgs"] = Strings("Sha256", "ChunkIndex", "Data (blob)"),
            ["close"] = TursoSyncService.LabCloseSql,
            ["closeArgs"] = Strings("Sha256", "Sha256"),
            ["readChunks"] = TursoSyncService.LabReadChunksSql,
            ["readChunksArgs"] = Strings("Sha256", "el primer ChunkIndex que falta"),
            ["usage"] = TursoSyncService.LabUsageSql,
            ["cloudSpace"] = Strings(TursoSyncService.CloudSpaceSql),
            ["archived"] = TursoSyncService.LabArchivedSql,
            ["evictionCandidates"] = TursoSyncService.LabEvictionCandidatesSql,
            ["evictChunks"] = TursoSyncService.LabEvictChunksSql,
            ["evict"] = TursoSyncService.LabEvictSql,
            ["orphans"] = Strings(TursoSyncService.LabOrphanSql)
        }));

        cases.Add(Case("topes", new JsonObject(), new JsonObject
        {
            ["cloudCapBytes"] = TursoSyncService.LabCloudCapBytes,
            ["warnBytes"] = TursoSyncService.WarnBytesFor(TursoSyncService.LabCloudCapBytes),
            ["evictTargetBytes"] = TursoSyncService.EvictTargetFor(TursoSyncService.LabCloudCapBytes),
            ["cloudSpaceStopBytes"] = TursoSyncService.CloudSpaceStopBytes,
            ["orphanDays"] = TursoSyncService.LabOrphanDays,
            ["chunksPerRead"] = 4,
            ["maxFileBytes"] = LabFileImporter.MaxFileBytes,
            ["largePdfBytes"] = LabFileImporter.LargePdfBytes,
            ["maxLongSidePixels"] = LabFileImporter.MaxLongSidePixels,
            ["jpegQuality"] = LabFileImporter.JpegQuality,
            ["maxPhotos"] = LabFileImporter.MaxPhotos,
            ["mimeTypes"] = Strings(LabResult.PdfMimeType, LabResult.JpegMimeType),
            ["androidBytesPerDay"] = new JsonObject
            {
                ["down"] = TursoConsumptionBrake.AndroidMaxLabBytesDownPerDay,
                ["up"] = TursoConsumptionBrake.AndroidMaxLabBytesUpPerDay
            },
            ["pcBytesPerDay"] = new JsonObject
            {
                ["down"] = TursoConsumptionBrake.MaxLabBytesDownPerDay,
                ["up"] = TursoConsumptionBrake.MaxLabBytesUpPerDay
            }
        }));

        CheckOrWrite("contracts/golden/lab-files.json", VectorFile("lab-files", cases,
            "Un archivo se parte en trozos de 'chunkSize' bytes (el último puede ser más chico); 'sha256' es el del archivo completo en hex con minúsculas y es su llave (LabResults.ContentSha256, LabBlobs.Sha256).",
            "Subir: 'state' y 'existingChunks' en una petición; si no hay fila, revisar el tope con 'usage' y escribir 'register'; subir cada trozo que falta con 'insertChunk', uno por petición, y el último en un batch con BEGIN/COMMIT junto con 'close'. Un archivo con EvictedAtUtc no se vuelve a subir.",
            "Bajar: 'state' (sin CompletedAtUtc se sigue subiendo; con EvictedAtUtc ya no está en la nube) y 'readChunks' desde 0 de 'chunksPerRead' en 'chunksPerRead'; revisar el tamaño y el SHA-256 antes de usarlo.",
            "Solo la PC principal corre 'archived', 'evictionCandidates', 'evictChunks', 'evict' (los dos en una transacción) y 'orphans' (los cuatro en una transacción, una vez al día).",
            "Lo que viaja es el base64 del trozo: 4 × ceil(bytes / 3). El freno de bytes por día cuenta eso; abrir un resultado a pedido no se frena.",
            "Espacio de toda la base: (page_count − freelist_count) × page_size con 'cloudSpace'; lo mismo que cuenta Turso con dbstat (V20). Arriba de 'cloudSpaceStopBytes' no se sube ningún archivo."));
    }
}
