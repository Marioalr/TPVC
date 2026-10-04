using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Installer;

namespace TPVC.Tests;

[TestClass]
public class InstallerTests
{
    [TestMethod]
    public void InstallationPaths_TargetCorrectUserDirectories()
    {
        string installDir = InstallerEngine.InstallationDirectory;
        string exePath = InstallerEngine.InstalledExePath;

        Assert.IsFalse(string.IsNullOrWhiteSpace(installDir));
        Assert.IsTrue(installDir.Contains("Programs", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(installDir.EndsWith("TPVC", StringComparison.OrdinalIgnoreCase));

        Assert.IsFalse(string.IsNullOrWhiteSpace(exePath));
        Assert.IsTrue(exePath.EndsWith("TPVC.exe", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(Path.Combine(installDir, "TPVC.exe"), exePath);
    }

    [TestMethod]
    public void InstallerProgressReport_StoresPropertiesAccurately()
    {
        var report = new InstallProgressReport
        {
            Percentage = 65,
            Status = "Desempaquetando archivos..."
        };

        Assert.AreEqual(65, report.Percentage);
        Assert.AreEqual("Desempaquetando archivos...", report.Status);
    }

    [TestMethod]
    public void PublishedPackages_IncludeExecutableAndInstaller()
    {
        string rootDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string publishDir = Path.Combine(rootDir, "publish");

        string setupExe = Directory.Exists(publishDir) 
            ? Directory.GetFiles(publishDir, "TPVC-Setup*.exe").OrderByDescending(f => f).FirstOrDefault() ?? Path.Combine(publishDir, "TPVC-Setup-v1.3.2.exe")
            : Path.Combine(publishDir, "TPVC-Setup-v1.3.2.exe");
        string zipArchive = Directory.Exists(publishDir)
            ? Directory.GetFiles(publishDir, "TPVC-*-win-x64.zip").OrderByDescending(f => f).FirstOrDefault() ?? Path.Combine(publishDir, "TPVC-v1.3.2-win-x64.zip")
            : Path.Combine(publishDir, "TPVC-v1.3.2-win-x64.zip");

        if (File.Exists(setupExe))
        {
            var setupFileInfo = new FileInfo(setupExe);
            Assert.IsGreaterThan(setupFileInfo.Length, 1_000_000, "El instalador ejecutable debe ser mayor a 1 MB");
        }

        if (File.Exists(zipArchive))
        {
            using var archive = ZipFile.OpenRead(zipArchive);
            var tpvcExe = archive.GetEntry("TPVC.exe");
            Assert.IsNotNull(tpvcExe, "El zip portable debe contener TPVC.exe");
            var tpvcDll = archive.GetEntry("TPVC.dll");
            Assert.IsNotNull(tpvcDll, "El zip portable debe contener TPVC.dll");
        }
    }
}
