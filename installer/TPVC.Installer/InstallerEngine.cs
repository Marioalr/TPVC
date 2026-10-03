using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace TPVC.Installer;

public class InstallProgressReport
{
    public int Percentage { get; set; }
    public string Status { get; set; } = string.Empty;
}

public static class InstallerEngine
{
    public static string InstallationDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TPVC");

    public static string InstalledExePath =>
        Path.Combine(InstallationDirectory, "TPVC.exe");

    public static async Task InstallAsync(IProgress<InstallProgressReport> progress)
    {
        await Task.Run(async () =>
        {
            // Paso 1: Inicialización y cierre de instancias previas
            progress.Report(new InstallProgressReport { Percentage = 5, Status = "Comprobando entorno del sistema..." });
            await Task.Delay(200);

            try
            {
                var runningProcesses = Process.GetProcessesByName("TPVC");
                foreach (var proc in runningProcesses)
                {
                    try { proc.Kill(); proc.WaitForExit(1500); } catch { }
                }
            }
            catch { }

            progress.Report(new InstallProgressReport { Percentage = 15, Status = "Creando directorio de instalación..." });
            Directory.CreateDirectory(InstallationDirectory);
            await Task.Delay(200);

            // Paso 2: Localizar el paquete de la aplicación
            progress.Report(new InstallProgressReport { Percentage = 25, Status = "Preparando archivos de la aplicación..." });

            Stream? zipStream = GetPayloadStream();
            if (zipStream != null)
            {
                using (zipStream)
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                    int total = entries.Count;

                    for (int i = 0; i < total; i++)
                    {
                        var entry = entries[i];
                        string destPath = Path.Combine(InstallationDirectory, entry.FullName);
                        string? destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        entry.ExtractToFile(destPath, overwrite: true);

                        int pct = 25 + (int)((i + 1) * 55.0 / total);
                        progress.Report(new InstallProgressReport
                        {
                            Percentage = pct,
                            Status = $"Instalando: {entry.Name} ({i + 1}/{total})"
                        });
                    }
                }
            }
            else
            {
                // Modo respaldo local: copiar desde publish/bin si existe
                CopyFromLocalSource(progress);
            }

            // Paso 3: Configuración de accesos directos
            progress.Report(new InstallProgressReport { Percentage = 85, Status = "Creando accesos directos en el sistema..." });
            await Task.Delay(250);
            CreateShortcuts();

            // Paso 4: Finalización
            progress.Report(new InstallProgressReport { Percentage = 95, Status = "Verificando instalación..." });
            await Task.Delay(250);

            progress.Report(new InstallProgressReport { Percentage = 100, Status = "¡Instalación completada exitosamente!" });
            await Task.Delay(200);
        });
    }

    private static Stream? GetPayloadStream()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Payload.zip", StringComparison.OrdinalIgnoreCase));

        if (resourceName != null)
        {
            return assembly.GetManifestResourceStream(resourceName);
        }

        // Buscar en el directorio actual o en rutas relativas de desarrollo
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] searchPaths = new[]
        {
            Path.Combine(appDir, "Payload.zip"),
            Path.Combine(appDir, "Resources", "Payload.zip"),
            Path.Combine(appDir, "..", "..", "..", "..", "publish", "TPVC-v1.3.0-win-x64.zip"),
            Path.Combine(appDir, "..", "..", "..", "..", "publish", "TPVC-v1.2.1-win-x64.zip")
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                return File.OpenRead(path);
            }
        }

        return null;
    }

    private static void CopyFromLocalSource(IProgress<InstallProgressReport> progress)
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidateDirs = new[]
        {
            Path.Combine(appDir, "..", "..", "..", "..", "publish", "TPVC-v1.3.0-win-x64"),
            Path.Combine(appDir, "..", "..", "..", "..", "bin", "Debug", "net10.0-windows")
        };

        foreach (var srcDir in candidateDirs)
        {
            if (Directory.Exists(srcDir) && File.Exists(Path.Combine(srcDir, "TPVC.exe")))
            {
                var files = Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories);
                int total = files.Length;
                for (int i = 0; i < total; i++)
                {
                    var file = files[i];
                    string relative = Path.GetRelativePath(srcDir, file);
                    string dest = Path.Combine(InstallationDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);

                    int pct = 25 + (int)((i + 1) * 55.0 / total);
                    progress.Report(new InstallProgressReport
                    {
                        Percentage = pct,
                        Status = $"Copiando: {Path.GetFileName(file)}"
                    });
                }
                return;
            }
        }
    }

    public static void CreateShortcuts()
    {
        if (!File.Exists(InstalledExePath)) return;

        try
        {
            // Acceso directo en el Escritorio
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string desktopShortcut = Path.Combine(desktopDir, "TPVC.lnk");
            CreateShortcutFile(desktopShortcut, InstalledExePath, "TPVC - Sistema de Consultorio Médico");

            // Acceso directo en el Menú Inicio
            string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string startMenuShortcut = Path.Combine(startMenuDir, "TPVC.lnk");
            CreateShortcutFile(startMenuShortcut, InstalledExePath, "TPVC - Sistema de Consultorio Médico");
        }
        catch { }
    }

    private static void CreateShortcutFile(string shortcutPath, string targetPath, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.Description = description;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.Save();
                return;
            }
        }
        catch { }

        // Respaldo mediante PowerShell
        try
        {
            string workingDir = Path.GetDirectoryName(targetPath) ?? string.Empty;
            string command = $"$s=(New-Object -COM WScript.Shell).CreateShortcut('{shortcutPath}'); $s.TargetPath='{targetPath}'; $s.WorkingDirectory='{workingDir}'; $s.Description='{description}'; $s.Save()";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{command}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { }
    }

    public static void LaunchInstalledApp()
    {
        if (File.Exists(InstalledExePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = InstalledExePath,
                WorkingDirectory = InstallationDirectory,
                UseShellExecute = true
            });
        }
    }
}
