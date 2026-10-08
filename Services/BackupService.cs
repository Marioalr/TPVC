using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TPVC.Data;

namespace TPVC.Services;

/// <summary>
/// Respaldos de la base SQLite usando la API de backup en línea de SQLite
/// (copia consistente aunque la app esté abierta y en modo WAL).
/// Al iniciar se crea un respaldo diario y se conservan los últimos <see cref="RetainedBackups"/>.
/// </summary>
public class BackupService
{
    public const int RetainedBackups = 14;

    private readonly string _databasePath;

    public BackupService() : this(AppDbContext.DbPath)
    {
    }

    public BackupService(string databasePath)
    {
        _databasePath = databasePath;
        BackupDirectory = Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "backups");
    }

    public string BackupDirectory { get; }

    public string DatabasePath => _databasePath;

    public IReadOnlyList<FileInfo> GetBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return Array.Empty<FileInfo>();
        return new DirectoryInfo(BackupDirectory)
            .GetFiles("consultorio-*.db")
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();
    }

    public FileInfo? GetLatestBackup() => GetBackups().FirstOrDefault();

    /// <summary>
    /// Crea el respaldo del día si todavía no existe. Devuelve la ruta creada o null.
    /// </summary>
    public string? EnsureDailyBackup()
    {
        if (!File.Exists(_databasePath)) return null;
        var target = Path.Combine(BackupDirectory, $"consultorio-{DateTime.Now:yyyyMMdd}.db");
        if (File.Exists(target)) return null;
        return CreateBackup(target);
    }

    /// <summary>Respaldo manual con fecha y hora en el nombre.</summary>
    public Task<string> CreateBackupNowAsync() =>
        Task.Run(() => CreateBackup(UniquePath($"consultorio-{DateTime.Now:yyyyMMdd-HHmmss}")));

    /// <summary>Dos respaldos en el mismo segundo no se pisan: el segundo lleva "-2", "-3"…</summary>
    private string UniquePath(string baseName)
    {
        var path = Path.Combine(BackupDirectory, baseName + ".db");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(BackupDirectory, $"{baseName}-{i}.db");
        return path;
    }

    private string CreateBackup(string targetPath)
    {
        Directory.CreateDirectory(BackupDirectory);
        var tempPath = targetPath + ".tmp";

        CopyDatabase(_databasePath, tempPath, sourceReadOnly: true);

        File.Move(tempPath, targetPath, overwrite: true);
        PruneOldBackups();
        AppLogger.Info($"Respaldo creado: {targetPath}");
        return targetPath;
    }

    /// <summary>
    /// Copia consistente de una base SQLite a otra con la API de backup. Si otra conexión está
    /// escribiendo o cerrando la base en ese instante (SQLITE_BUSY/LOCKED), espera y reintenta en
    /// lugar de fallar: antes un respaldo podía fallar solo por coincidir con un guardado.
    /// </summary>
    public static void CopyDatabase(string sourcePath, string destinationPath, bool sourceReadOnly)
    {
        var sourceMode = sourceReadOnly ? "Mode=ReadOnly;" : string.Empty;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var source = new SqliteConnection($"Data Source={sourcePath};{sourceMode}Pooling=False");
                using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
                source.Open();
                destination.Open();
                SetBusyTimeout(source);
                SetBusyTimeout(destination);
                source.BackupDatabase(destination);
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 10)
            {
                System.Threading.Thread.Sleep(100 * attempt);
            }
        }
    }

    private static void SetBusyTimeout(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 3000;";
        command.ExecuteNonQuery();
    }

    private void PruneOldBackups()
    {
        foreach (var old in GetBackups().Skip(RetainedBackups))
        {
            try { old.Delete(); } catch { }
        }
    }
}
