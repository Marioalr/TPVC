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
        Task.Run(() => CreateBackup(Path.Combine(BackupDirectory, $"consultorio-{DateTime.Now:yyyyMMdd-HHmmss}.db")));

    private string CreateBackup(string targetPath)
    {
        Directory.CreateDirectory(BackupDirectory);
        var tempPath = targetPath + ".tmp";

        using (var source = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False"))
        using (var destination = new SqliteConnection($"Data Source={tempPath};Pooling=False"))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        File.Move(tempPath, targetPath, overwrite: true);
        PruneOldBackups();
        AppLogger.Info($"Respaldo creado: {targetPath}");
        return targetPath;
    }

    private void PruneOldBackups()
    {
        foreach (var old in GetBackups().Skip(RetainedBackups))
        {
            try { old.Delete(); } catch { }
        }
    }
}
