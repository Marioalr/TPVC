using System;
using System.IO;
using System.Linq;

namespace TPVC.Services;

/// <summary>
/// Bitácora mínima en %LocalAppData%\TPVC\logs (un archivo por día, se conservan 30).
/// Sirve para diagnosticar fallos reportados por el consultorio sin depurador.
/// </summary>
public static class AppLogger
{
    private static readonly object Sync = new();
    private const int RetentionDays = 30;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TPVC", "logs");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        System.Diagnostics.Debug.WriteLine($"[{level}] {message} {ex}");
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"tpvc-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex != null) line += Environment.NewLine + ex;
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // La bitácora nunca debe interrumpir la operación clínica
        }
    }

    public static void PurgeOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            var limit = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "tpvc-*.log")
                         .Where(f => File.GetLastWriteTime(f) < limit))
            {
                File.Delete(file);
            }
        }
        catch
        {
        }
    }
}
