using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services.LabFiles;

namespace TPVC.Services;

/// <summary>Lo que el médico captura de un resultado nuevo (el archivo va aparte).</summary>
public sealed record LabResultDraft(string Title, string Laboratory, DateTime ResultDate, string Notes, int? PatientConsultationId = null);

/// <summary>Cómo terminó traer el archivo de un resultado.</summary>
public enum LabFetchStatus
{
    /// <summary>Está en este equipo (ya estaba o se acaba de bajar).</summary>
    Ok,

    /// <summary>Todavía no llega a la nube (otro equipo no lo ha subido) o no hay nube.</summary>
    NotInCloud,

    /// <summary>Otro equipo lo está subiendo.</summary>
    Uploading,

    /// <summary>Se desalojó de la nube por el tope: solo está en el archivo de la PC principal y en su respaldo.</summary>
    Evicted,

    /// <summary>Llegó con otro tamaño o contenido (o le falta un trozo en la nube).</summary>
    Broken,

    /// <summary>Sin conexión o la nube respondió con error.</summary>
    Failed
}

/// <summary>El resultado de traer un archivo, con el mensaje para el médico.</summary>
public sealed record LabFetchResult(LabFetchStatus Status, string Message, byte[]? Content = null)
{
    public bool Ok => Status == LabFetchStatus.Ok;
}

/// <summary>Quien sabe bajar un archivo de la nube (la sincronización con Turso).</summary>
public interface ILabFileFetcher
{
    Task<LabFetchResult> FetchLabFileAsync(string sha256, CancellationToken ct = default);
}

/// <summary>
/// Los resultados de laboratorio del expediente (paso 8.12b): las filas de LabResults (sincronizadas) y su archivo en
/// esta PC (<see cref="LabFileStore"/>). Subir y bajar los archivos le toca a la sincronización
/// (TursoSyncService.LabFiles.cs); aquí solo se pide un archivo que falta al abrirlo.
/// </summary>
public sealed class LabResultService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly LocalPreferencesStore? _preferences;

    public LabResultService(IDbContextFactory<AppDbContext> contextFactory, LabFileStore? store = null, LocalPreferencesStore? preferences = null)
    {
        _contextFactory = contextFactory;
        _preferences = preferences;
        Store = store ?? new LabFileStore();
    }

    public LabFileStore Store { get; }

    /// <summary>Baja los archivos que faltan (la sincronización con Turso se pone aquí al crearse).</summary>
    public ILabFileFetcher? Fetcher { get; set; }

    /// <summary>Se agregó o quitó un resultado aquí (la sincronización automática lo sube).</summary>
    public event Action? LocalChanged;

    /// <summary>Cambió lo que hay (aquí, o llegó de la nube un resultado o un archivo): las pantallas se recargan.</summary>
    public event Action? Changed;

    /// <summary>Lo llama la sincronización cuando bajó resultados o archivos.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>La carpeta del respaldo externo (USB u OneDrive); null si no se ha elegido.</summary>
    public string? BackupFolder
    {
        get => _preferences?.Current.LabBackupFolder;
        set
        {
            if (_preferences == null) return;
            _preferences.Current.LabBackupFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _preferences.Save();
        }
    }

    public async Task<List<LabResult>> GetForPatientAsync(int patientId, CancellationToken ct = default)
    {
        using var db = _contextFactory.CreateDbContext();
        return await db.LabResults.AsNoTracking()
            .Where(r => r.PatientRecordId == patientId)
            .OrderByDescending(r => r.ResultDate)
            .ThenByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Agrega un resultado: primero guarda el archivo aquí (si la PC se apaga a la mitad, no queda una fila sin archivo) y
    /// después su fila. Lo sube la sincronización.
    /// </summary>
    public async Task<LabResult> AddAsync(int patientId, LabResultDraft draft, PreparedLabFile file, CancellationToken ct = default)
    {
        var title = draft.Title.Trim();
        if (title.Length == 0) throw new LabFileException("Escriba qué estudio es (por ejemplo, «Biometría hemática»).");

        var sha = Store.Save(file.Content);
        var result = new LabResult
        {
            PatientRecordId = patientId,
            PatientConsultationId = draft.PatientConsultationId,
            Title = title,
            Laboratory = draft.Laboratory.Trim(),
            ResultDate = draft.ResultDate.Date,
            Notes = draft.Notes.Trim(),
            MimeType = file.MimeType,
            FileName = Path.GetFileName(file.FileName),
            PageCount = file.PageCount,
            SizeBytes = file.Content.LongLength,
            ContentSha256 = sha,
            CreatedAt = ClinicClock.Now
        };

        using (var db = _contextFactory.CreateDbContext())
        {
            db.LabResults.Add(result);
            if (await db.LabFileStates.FindAsync(new object[] { sha }, ct) == null)
            {
                db.LabFileStates.Add(new LabFileState { Sha256 = sha, UpdatedAtUtc = DateTime.UtcNow });
            }
            await db.SaveChangesAsync(ct);
        }

        AppLogger.Info($"Laboratorio: resultado agregado ({file.MimeType}, {file.Content.Length:N0} bytes, {sha}).");
        LocalChanged?.Invoke();
        Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// Quita el resultado del expediente (en todos los equipos). Su archivo se queda en el archivo de esta PC; la nube lo
    /// borra a los 30 días si ningún resultado lo nombra.
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        using (var db = _contextFactory.CreateDbContext())
        {
            var result = await db.LabResults.FindAsync(new object[] { id }, ct);
            if (result == null) return;
            db.LabResults.Remove(result);
            await db.SaveChangesAsync(ct);
        }
        LocalChanged?.Invoke();
        Changed?.Invoke();
    }

    public bool HasLocalFile(LabResult result) => Store.Contains(result.ContentSha256);

    /// <summary>El archivo del resultado: el de aquí o, si falta, el de la nube (no lo frena el tope de bytes del día).</summary>
    public async Task<LabFetchResult> GetContentAsync(LabResult result, CancellationToken ct = default)
    {
        var local = Store.ReadVerified(result.ContentSha256);
        if (local != null) return new LabFetchResult(LabFetchStatus.Ok, string.Empty, local);
        if (Fetcher == null)
            return new LabFetchResult(LabFetchStatus.NotInCloud, "El archivo no está en este equipo y la sincronización con la nube no está activa.");
        return await Fetcher.FetchLabFileAsync(result.ContentSha256, ct);
    }

    /// <summary>
    /// Una copia para abrirla con el programa de Windows, con un nombre legible ("Biometría hemática 2026-10-11.pdf"), en
    /// la carpeta temporal: el archivo de la PC no se toca.
    /// </summary>
    public static string WriteViewCopy(LabResult result, byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TPVC", "resultados");
        Directory.CreateDirectory(dir);
        var invalid = Path.GetInvalidFileNameChars();
        var title = new string(result.Title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (title.Length > 80) title = title[..80].Trim();
        var path = Path.Combine(dir, $"{(title.Length == 0 ? "Resultado" : title)} {result.ResultDate:yyyy-MM-dd} {result.ContentSha256[..6]}{result.Extension}");
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Cuántos archivos hay en esta PC y cuántos bytes suman.</summary>
    public (int Files, long Bytes) LocalTotals()
    {
        var all = Store.List().ToList();
        return (all.Count, all.Sum(f => f.Size));
    }

    /// <summary>Resultados cuyo archivo todavía no está en esta PC.</summary>
    public async Task<int> CountMissingLocallyAsync(CancellationToken ct = default)
    {
        using var db = _contextFactory.CreateDbContext();
        var hashes = await db.LabResults.AsNoTracking().Select(r => r.ContentSha256).Distinct().ToListAsync(ct);
        return hashes.Count(h => !Store.Contains(h));
    }

    /// <summary>Copia al respaldo externo lo que le falta (lanza si la carpeta no está conectada).</summary>
    public Task<(int Copied, int AlreadyThere)> BackUpAsync(string folder) => Task.Run(() => Store.BackUpTo(folder));

    /// <summary>Trae del respaldo externo lo que falta en esta PC (una PC nueva).</summary>
    public async Task<int> RestoreFromBackupAsync(string folder)
    {
        var restored = await Task.Run(() => Store.RestoreFrom(folder));
        if (restored > 0) Changed?.Invoke();
        return restored;
    }
}
