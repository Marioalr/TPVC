using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

[TestClass]
public class DataImportTests
{
    private string _tempDir = null!;
    private string _dbPath = null!;
    private string _settingsPath = null!;
    private string _profilePath = null!;
    private IDbContextFactory<AppDbContext> _factory = null!;
    private BackupService _backupService = null!;
    private ClinicProfileService _profileService = null!;
    private InventoryService _inventoryService = null!;
    private DataImportService _importService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TPVC_ImportTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "consultorio.db");
        _settingsPath = Path.Combine(_tempDir, "turso_settings.json");
        _profilePath = Path.Combine(_tempDir, "clinic_profile.json");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _factory = new TestDbContextFactory(options);

        using (var db = _factory.CreateDbContext())
        {
            DatabaseInitializer.Initialize(db, seedDemoData: false);
        }

        _backupService = new BackupService(_dbPath);
        _profileService = new ClinicProfileService(_profilePath);
        _inventoryService = new InventoryService(_factory);
        _importService = new DataImportService(_factory, _backupService, _profileService, _inventoryService);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock issues on temporary test folders
        }
    }

    [TestMethod]
    public void ValidateConfirmationWord_OnlyAcceptsConfirmar()
    {
        Assert.IsTrue(DataImportService.ValidateConfirmationWord("CONFIRMAR"));
        Assert.IsTrue(DataImportService.ValidateConfirmationWord("confirmar"));
        Assert.IsTrue(DataImportService.ValidateConfirmationWord(" Confirmar "));
        Assert.IsTrue(DataImportService.ValidateConfirmationWord("CoNfIrMaR"));

        Assert.IsFalse(DataImportService.ValidateConfirmationWord("SI"));
        Assert.IsFalse(DataImportService.ValidateConfirmationWord("CONFIRMA"));
        Assert.IsFalse(DataImportService.ValidateConfirmationWord("OK"));
        Assert.IsFalse(DataImportService.ValidateConfirmationWord(""));
        Assert.IsFalse(DataImportService.ValidateConfirmationWord(null));
        Assert.IsFalse(DataImportService.ValidateConfirmationWord("CANCELAR"));
    }

    [TestMethod]
    public async Task ImportExcel_WithUserSample_ExtractsProductsProfileAndPatient()
    {
        // Ruta al asset de prueba con los datos reales del usuario
        string samplePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestAssets", "catalogo_muestra.xlsx");
        if (!File.Exists(samplePath))
        {
            // Intentar ruta relativa al proyecto
            samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../TestAssets/catalogo_muestra.xlsx"));
        }

        Assert.IsTrue(File.Exists(samplePath), $"El archivo de prueba debe existir en {samplePath}");

        var result = await _importService.ImportExcelOrCsvAsync(samplePath);

        Assert.IsTrue(result.Success, $"La importación debe ser exitosa: {result.Message}");
        Assert.AreEqual(259, result.ProductsImported, "Debe importar 259 medicamentos de la hoja MEDICAMENTOS");
        Assert.IsTrue(result.DoctorProfileUpdated, "Debe actualizar los datos del médico desde la hoja RECETA");
        Assert.IsTrue(File.Exists(result.BackupPath), "Debe haber creado un respaldo preventivo de la base de datos");

        // 1. Verificar perfil médico extraído
        var profile = _profileService.CurrentProfile;
        Assert.AreEqual("Dra. María del Cielo Alberto Galicia", profile.DoctorName);
        Assert.AreEqual("Benemérita Universidad Autónoma de Puebla", profile.University);
        Assert.AreEqual("15632243", profile.ProfessionalLicense);
        Assert.AreEqual("2481592448", profile.Phone);
        StringAssert.Contains(profile.Address, "Yehualtepec");

        // 2. Verificar medicamentos en la base de datos
        using (var db = _factory.CreateDbContext())
        {
            var med1 = await db.Products.FirstOrDefaultAsync(p => p.Code == "3045");
            Assert.IsNotNull(med1, "Debe existir el medicamento con clave 3045");
            StringAssert.Contains(med1.Name, "Acetato de medroxiprogesterona");

            var med2 = await db.Products.FirstOrDefaultAsync(p => p.Code == "4263");
            Assert.IsNotNull(med2, "Debe existir el medicamento con clave 4263");
            StringAssert.Contains(med2.Name, "Aciclovir");

            // 3. Verificar paciente y signos vitales extraídos (Zairi Carrera Macedo)
            var patient = await db.Patients.Include(p => p.Consultations).FirstOrDefaultAsync(p => p.FullName.Contains("Zairi Carrera"));
            Assert.IsNotNull(patient, "Debe existir el expediente de Zairi Carrera Macedo");
            Assert.AreEqual(4, patient.Age);
            Assert.AreEqual("Negadas", patient.Allergies);

            var consultation = patient.Consultations.FirstOrDefault();
            Assert.IsNotNull(consultation, "Debe existir la consulta importada");
            Assert.AreEqual(new DateTime(2026, 10, 3), consultation.Date.Date);

            // Verificar parsing de signos vitales numéricos (incluyendo SpO2)
            consultation.EnsureNumericVitalSigns();
            Assert.AreEqual(36.1m, consultation.Temperature);
            Assert.AreEqual(15m, consultation.Weight);
            Assert.AreEqual(0.95m, consultation.Height);
            Assert.AreEqual(94m, consultation.OxygenSaturation);
            Assert.AreEqual(2, consultation.Prescriptions.Count, "Debe incluir las 2 prescripciones médicas");
        }
    }

    [TestMethod]
    public async Task ImportCsv_ImportsProductsSafely()
    {
        string csvPath = Path.Combine(_tempDir, "catalogo_test.csv");
        await File.WriteAllLinesAsync(csvPath, new[]
        {
            "Medicamento,Clave",
            "Paracetamol 500 mg tabletas,PAR-500",
            "Ibuprofeno 400 mg cápsulas,IBU-400",
            "Amoxicilina 500 mg suspensión,"
        });

        var result = await _importService.ImportExcelOrCsvAsync(csvPath);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.ProductsImported);
        Assert.IsTrue(File.Exists(result.BackupPath), "Debe crear respaldo antes del CSV");

        using (var db = _factory.CreateDbContext())
        {
            var par = await db.Products.FirstOrDefaultAsync(p => p.Code == "PAR-500");
            Assert.IsNotNull(par);
            Assert.AreEqual("Paracetamol 500 mg tabletas", par.Name);

            var amox = await db.Products.FirstOrDefaultAsync(p => p.Name.Contains("Amoxicilina"));
            Assert.IsNotNull(amox);
            Assert.IsTrue(amox.Code.StartsWith("MED-"), "Debe autogenerar código cuando la clave está vacía");
        }
    }

    [TestMethod]
    public async Task ImportDatabaseFile_ReplacesDatabaseSafely()
    {
        // 1. Crear una base de datos externa de origen con un producto especial
        string sourceDbPath = Path.Combine(_tempDir, "base_externa.db");
        var sourceOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sourceDbPath}")
            .Options;

        using (var sourceDb = new AppDbContext(sourceOptions))
        {
            DatabaseInitializer.Initialize(sourceDb, seedDemoData: false);
            var cat = new Category { Name = "Categoría Especial" };
            sourceDb.Categories.Add(cat);
            sourceDb.Products.Add(new Product
            {
                Code = "ESP-999",
                Name = "Producto Importado de Respaldo",
                Category = cat
            });
            sourceDb.SaveChanges();
        }

        SqliteConnection.ClearAllPools();

        // 2. Restaurar la base externa sobre la base actual
        var result = await _importService.ImportDatabaseFileAsync(sourceDbPath);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(File.Exists(result.BackupPath), "Debe haber respaldado la base anterior");

        // 3. Verificar que el producto especial existe en la base restaurada
        using (var db = _factory.CreateDbContext())
        {
            var prod = await db.Products.FirstOrDefaultAsync(p => p.Code == "ESP-999");
            Assert.IsNotNull(prod, "El producto importado de la base externa debe existir en consultorio.db");
            Assert.AreEqual("Producto Importado de Respaldo", prod.Name);
        }
    }

    [TestMethod]
    public async Task TursoSyncViewModel_ImportConfirmationWorkflow()
    {
        var syncSettings = new TursoSettingsService(_settingsPath);
        var syncService = new TursoSyncService(_factory, syncSettings, _profileService, _inventoryService);
        var vm = new TursoSyncViewModel(syncService, syncSettings, _profileService, _importService);

        string samplePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestAssets", "catalogo_muestra.xlsx");
        if (!File.Exists(samplePath))
        {
            samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../TestAssets/catalogo_muestra.xlsx"));
        }

        // Simular selector de archivo
        DialogService.OpenFilePickerHandler = (_, _) => samplePath;

        // Iniciar importación
        vm.StartImportExcelCommand.Execute(null);

        Assert.IsTrue(vm.IsImportConfirmModalOpen, "El modal de confirmación debe abrirse");
        Assert.AreEqual("Excel", vm.SelectedImportFileType);
        Assert.AreEqual(samplePath, vm.SelectedImportFilePath);
        Assert.IsFalse(vm.CanProceedWithImport, "No debe permitir proceder sin escribir CONFIRMAR");

        // Escribir texto erróneo
        vm.ImportConfirmWord = "SI";
        Assert.IsFalse(vm.CanProceedWithImport);

        vm.ImportConfirmWord = "confirm";
        Assert.IsFalse(vm.CanProceedWithImport);

        // Escribir CONFIRMAR
        vm.ImportConfirmWord = "CONFIRMAR";
        Assert.IsTrue(vm.CanProceedWithImport, "Debe habilitar el botón al escribir CONFIRMAR");

        // Ejecutar importación confirmada
        await vm.ExecuteConfirmedImportCommand.ExecuteAsync(null);

        Assert.IsFalse(vm.IsImportConfirmModalOpen, "El modal debe cerrarse tras completarse");
        Assert.AreEqual(string.Empty, vm.ImportConfirmWord, "La palabra de confirmación debe limpiarse");
        StringAssert.Contains(vm.ImportStatusMessage, "Catálogo procesado");
    }

    private class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new(_options);
    }
}
