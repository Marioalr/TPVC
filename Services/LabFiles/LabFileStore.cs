using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TPVC.Data;

namespace TPVC.Services.LabFiles;

/// <summary>
/// El archivo de resultados de laboratorio de esta PC (paso 8.12b): %LocalAppData%\TPVC\laboratorio\&lt;ab&gt;\&lt;sha&gt;, con el
/// SHA-256 del contenido como nombre. Va fuera de consultorio.db: los 14 respaldos diarios de la base no lo multiplican y
/// restaurar un respaldo no lo toca. Es inmutable: un archivo nunca cambia, y borrar un resultado no borra su archivo.
/// </summary>
public sealed class LabFileStore
{
    /// <summary>La carpeta de los respaldos externos dentro de la carpeta que elige el médico.</summary>
    public const string BackupFolderName = "TPVC-laboratorio";

    public static string DefaultRoot => Path.Combine(AppDbContext.DataDirectory, "laboratorio");

    /// <param name="root">La carpeta del archivo (las pruebas usan una temporal).</param>
    public LabFileStore(string? root = null)
    {
        Root = root ?? DefaultRoot;
    }

    public string Root { get; }

    /// <summary>SHA-256 en hex con minúsculas (el nombre del archivo y su llave en la nube).</summary>
    public static string HashOf(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static bool IsValidHash(string? sha) =>
        sha is { Length: 64 } && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public string PathFor(string sha) => PathIn(Root, sha);

    private static string PathIn(string root, string sha)
    {
        if (!IsValidHash(sha)) throw new ArgumentException($"No es un SHA-256: {sha}", nameof(sha));
        return Path.Combine(root, sha[..2], sha);
    }

    public bool Contains(string sha) => IsValidHash(sha) && File.Exists(PathFor(sha));

    /// <summary>Guarda el contenido (si ya estaba, no lo vuelve a escribir) y regresa su SHA-256.</summary>
    public string Save(byte[] content)
    {
        var sha = HashOf(content);
        Write(PathFor(sha), content);
        return sha;
    }

    /// <summary>Guarda lo que llegó de la nube solo si su SHA-256 es el esperado.</summary>
    public bool SaveVerified(string sha, byte[] content)
    {
        if (!string.Equals(HashOf(content), sha, StringComparison.Ordinal)) return false;
        Write(PathFor(sha), content);
        return true;
    }

    /// <summary>
    /// Escribe a un temporal y lo mueve: un corte a la mitad no deja un archivo incompleto con el nombre bueno. Si ya existe
    /// con el mismo tamaño, se deja (el nombre es el hash).
    /// </summary>
    private static void Write(string path, byte[] content)
    {
        var existing = new FileInfo(path);
        if (existing.Exists && existing.Length == content.LongLength) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>El contenido, o null si no está aquí.</summary>
    public byte[]? Read(string sha)
    {
        var path = PathFor(sha);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>El contenido si está aquí y su SHA-256 es el de su nombre; null si falta o se dañó.</summary>
    public byte[]? ReadVerified(string sha)
    {
        var content = Read(sha);
        return content != null && string.Equals(HashOf(content), sha, StringComparison.Ordinal) ? content : null;
    }

    /// <summary>Los archivos que hay aquí (los que no tienen nombre de SHA-256 no cuentan).</summary>
    public IEnumerable<(string Sha, long Size)> List() => ListIn(Root);

    private static IEnumerable<(string Sha, long Size)> ListIn(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (!IsValidHash(name) || !string.Equals(Path.GetFileName(dir), name[..2], StringComparison.Ordinal)) continue;
                yield return (name, new FileInfo(file).Length);
            }
        }
    }

    // =========================================================================
    // RESPALDO EXTERNO ("Respaldar laboratorios": una USB o una carpeta de OneDrive)
    // =========================================================================

    /// <summary>Dónde va un archivo dentro de la carpeta de respaldo elegida.</summary>
    public static string BackupPathFor(string folder, string sha) => PathIn(Path.Combine(folder, BackupFolderName), sha);

    /// <summary>¿Está en el respaldo, con el mismo tamaño que aquí? (false si la carpeta no está conectada).</summary>
    public bool IsBackedUp(string folder, string sha)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Contains(sha)) return false;
        try
        {
            var backup = new FileInfo(BackupPathFor(folder, sha));
            return backup.Exists && backup.Length == new FileInfo(PathFor(sha)).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copia al respaldo lo que le falta, revisando el SHA-256 de cada copia. Regresa cuántos copió y cuántos ya estaban;
    /// lanza si la carpeta no existe (la USB no está conectada).
    /// </summary>
    public (int Copied, int AlreadyThere) BackUpTo(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException($"No se encuentra la carpeta del respaldo: {folder}");

        int copied = 0, already = 0;
        foreach (var (sha, size) in List().ToList())
        {
            var target = BackupPathFor(folder, sha);
            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == size)
            {
                already++;
                continue;
            }

            var content = ReadVerified(sha);
            if (content == null)
            {
                AppLogger.Warn($"Laboratorio: el archivo {sha} de esta PC está dañado; no se respalda.");
                continue;
            }
            Write(target, content);
            if (!string.Equals(HashOf(File.ReadAllBytes(target)), sha, StringComparison.Ordinal))
            {
                File.Delete(target);
                throw new IOException($"La copia de {sha} en el respaldo salió dañada.");
            }
            copied++;
        }
        return (copied, already);
    }

    /// <summary>Trae del respaldo lo que falta aquí (una PC nueva), revisando el SHA-256 de cada uno. Regresa cuántos trajo.</summary>
    public int RestoreFrom(string folder)
    {
        var root = Path.Combine(folder, BackupFolderName);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"No hay un respaldo de laboratorio en {folder}");

        int restored = 0;
        foreach (var (sha, _) in ListIn(root).ToList())
        {
            if (Contains(sha)) continue;
            var content = File.ReadAllBytes(PathIn(root, sha));
            if (SaveVerified(sha, content)) restored++;
            else AppLogger.Warn($"Laboratorio: el archivo {sha} del respaldo está dañado; no se trae.");
        }
        return restored;
    }
}
