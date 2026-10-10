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

            // Cada medicamento con SU posología (antes la 2a. posología se importaba como medicamento)
            Assert.AreEqual("Espabion suspensión", consultation.Prescriptions[0].MedicineName);
            StringAssert.StartsWith(consultation.Prescriptions[0].Dosage, "Dar 5 ml vía oral cada 8 hrs");
            StringAssert.StartsWith(consultation.Prescriptions[1].MedicineName, "Adinol (Paracetamol) Jarabe");
            StringAssert.StartsWith(consultation.Prescriptions[1].Dosage, "Dar 8 ml vía oral cada 8 hrs");
            Assert.HasCount(2, patient.ActivePrescription, "La receta queda activa en el expediente");
            Assert.AreEqual(new DateTime(2026, 10, 3), patient.LastVisit.Date);

            // Medicamentos de catálogo: sin mínimo de existencias → no inundan las alertas
            Assert.IsFalse(await db.Products.AnyAsync(p => p.Code == "3045" && p.MinStock > 0));
        }

        // Plantillas de las hojas POSOLOGÍA y M. GRALES y reglas de DOSIS PEDIA
        var posologies = await _inventoryService.GetPrescriptionTemplatesAsync(PrescriptionTemplateKinds.Posology);
        Assert.HasCount(21, posologies);
        Assert.IsTrue(posologies.Any(t => t.Text.StartsWith("Tomar 1 tableta vía oral cada 8 hrs por 5 días")));

        var measures = await _inventoryService.GetPrescriptionTemplatesAsync(PrescriptionTemplateKinds.GeneralMeasures);
        Assert.HasCount(3, measures);
        StringAssert.StartsWith(measures[1].Text, "• Mantener hidratación con Vida Suero Oral");
        Assert.IsFalse(measures[1].Text.Contains('\t'), "Se normalizan tabuladores de las viñetas");

        var rules = await _inventoryService.GetPediatricDoseRulesAsync();
        Assert.HasCount(7, rules);
        var paracetamol = rules.First(r => r.Medication.StartsWith("Paracetamol 3.2"));
        Assert.AreEqual(15.0 / 32.0, paracetamol.MlPerKg!.Value, 1e-9, "ml/kg = (15 mg/kg) / (32 mg/ml), tomado de la fórmula");
        Assert.AreEqual(7.0, paracetamol.CalculateMl(15));
        Assert.AreEqual(140.0, paracetamol.CalculateDrops(15));
        var ibuprofen = rules.First(r => r.Medication.StartsWith("Ibuprofeno 2 g"));
        Assert.AreEqual(0.5, ibuprofen.MlPerKg);
        Assert.IsNull(ibuprofen.DropsPerMl, "Sin fórmula de gotas en el Excel");
    }

    [TestMethod]
    public async Task ImportExcel_TakesTheLetterheadImagesOfTheRecetaSheet_WithoutReplacingTheDoctorsOwn()
    {
        string samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../TestAssets/catalogo_muestra.xlsx"));
        if (!File.Exists(samplePath)) samplePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestAssets", "catalogo_muestra.xlsx");
        var receta = XlsxReader.Read(samplePath).Single(s => s.Name == "RECETA");
        var excelCrest = receta.Images.Single(i => i.FromRow == 1 && i.FromColumn == 1);

        var first = await _importService.ImportExcelOrCsvAsync(samplePath);

        Assert.IsTrue(first.Success, first.Message);
        Assert.AreEqual(2, first.LetterheadImagesImported, "Escudo (A1) y trazo de signos vitales (J2)");
        StringAssert.Contains(first.Message, "membrete");
        var profile = _profileService.CurrentProfile;
        Assert.IsNotNull(profile.CrestImagePath);
        Assert.IsNotNull(profile.HeaderImagePath);
        Assert.AreEqual(Path.Combine(_tempDir, "membrete"), Path.GetDirectoryName(profile.CrestImagePath), "Junto al perfil del consultorio");
        CollectionAssert.AreEqual(excelCrest.Content, File.ReadAllBytes(profile.CrestImagePath));
        Assert.AreEqual((740, 245), PngImage.Size(File.ReadAllBytes(profile.HeaderImagePath)), "El trazo como se ve en el Excel (recortado)");
        Assert.IsTrue(profile.IsConfigured);

        // El médico eligió otro escudo y quitó el encabezado: reimportar no reemplaza su escudo
        // (el encabezado vacío sí se vuelve a tomar del Excel)
        var ownCrest = PrescriptionSheet.LocationIconPng;
        _profileService.SaveLetterheadImage(LetterheadImageKind.Crest, ownCrest);
        _profileService.ClearLetterheadImage(LetterheadImageKind.Header);

        var second = await _importService.ImportExcelOrCsvAsync(samplePath);

        Assert.IsTrue(second.Success, second.Message);
        Assert.AreEqual(1, second.LetterheadImagesImported);
        CollectionAssert.AreEqual(ownCrest, File.ReadAllBytes(_profileService.CurrentProfile.CrestImagePath!), "Se conserva el escudo del médico");
        Assert.IsNotNull(_profileService.CurrentProfile.HeaderImagePath);

        var third = await _importService.ImportExcelOrCsvAsync(samplePath);
        Assert.AreEqual(0, third.LetterheadImagesImported, "Ya tiene las dos: no se toca nada");
        Assert.IsFalse(third.Message.Contains("membrete", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ImportExcel_Twice_IsIdempotent()
    {
        string samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../TestAssets/catalogo_muestra.xlsx"));
        if (!File.Exists(samplePath)) samplePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestAssets", "catalogo_muestra.xlsx");

        Assert.IsTrue((await _importService.ImportExcelOrCsvAsync(samplePath)).Success);
        var second = await _importService.ImportExcelOrCsvAsync(samplePath);

        Assert.IsTrue(second.Success, second.Message);
        Assert.AreEqual(0, second.ProductsImported);
        Assert.AreEqual(259, second.ProductsUpdated);
        Assert.AreEqual(0, second.TemplatesImported);
        Assert.AreEqual(0, second.PediatricRulesImported);

        using var db = _factory.CreateDbContext();
        Assert.AreEqual(1, await db.Patients.CountAsync(p => p.FullName == "Zairi Carrera Macedo"));
        Assert.AreEqual(1, await db.PatientConsultations.CountAsync(c => c.Folio.StartsWith("CNS-20261003-IMP")));
    }

    [TestMethod]
    public void ParseMlPerKg_ReadsExcelFormulas()
    {
        Assert.AreEqual(15.0 / 32.0, DataImportService.ParseMlPerKg("(C2*15)/32", "", "")!.Value, 1e-9);
        Assert.AreEqual(0.05, DataImportService.ParseMlPerKg("(C4*0.2)/4", "", "")!.Value, 1e-9);
        Assert.AreEqual(0.25, DataImportService.ParseMlPerKg("C6*0.25", "", "")!.Value, 1e-9);
        Assert.AreEqual(0.5, DataImportService.ParseMlPerKg(null, "10", "5")!.Value, 1e-9);
        Assert.IsNull(DataImportService.ParseMlPerKg(null, "", "0"));
    }

    [TestMethod]
    public async Task ImportCsv_HandlesQuotedFieldsWithDelimiters()
    {
        string csvPath = Path.Combine(_tempDir, "catalogo_comillas.csv");
        await File.WriteAllLinesAsync(csvPath, new[]
        {
            "Medicamento,Clave",
            "\"Paracetamol 100 mg/ml, gotas pediátricas\",PAR-G",
        });

        var result = await _importService.ImportExcelOrCsvAsync(csvPath);
        Assert.IsTrue(result.Success);

        using var db = _factory.CreateDbContext();
        var product = await db.Products.SingleAsync(p => p.Code == "PAR-G");
        Assert.AreEqual("Paracetamol 100 mg/ml, gotas pediátricas", product.Name);
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
    public async Task RestoringABackupFromBeforeTheChangeLog_GetsItsTriggers()
    {
        // Un respaldo de v1.19: sin triggers, sin registro de cambios y sin DbMeta
        string sourceDbPath = Path.Combine(_tempDir, "respaldo_v1_19.db");
        var sourceOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={sourceDbPath}").Options;
        using (var sourceDb = new AppDbContext(sourceOptions))
        {
            DatabaseInitializer.Initialize(sourceDb, seedDemoData: false);
            ChangeLogTestSupport.RemoveChangeLog(sourceDb);
        }
        SqliteConnection.ClearAllPools();

        var result = await _importService.ImportDatabaseFileAsync(sourceDbPath);
        Assert.IsTrue(result.Success, result.Message);

        using var db = _factory.CreateDbContext();
        Assert.HasCount(SyncSchema.FromModel(db.Model).Tables.Count * 3, ChangeLogTestSupport.TriggerNames(db));
        Assert.IsNull(DbMeta.Get(db, DbMeta.LastPushedSeq), "La siguiente subida manda todo");

        var category = db.Categories.First();
        category.Description = "Editada después de restaurar";
        db.SaveChanges();
        Assert.IsNotNull(ChangeLogTestSupport.SeqOf(db, nameof(Category), category.SyncId), "Lo que se guarda después se anota");
    }

    [TestMethod]
    public async Task RestoringABackup_ResetsThePushCursor_AndTheDatabaseIdentity()
    {
        string sourceDbPath = Path.Combine(_tempDir, "respaldo_con_cursor.db");
        var sourceOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={sourceDbPath}").Options;
        string sourceInstance;
        using (var sourceDb = new AppDbContext(sourceOptions))
        {
            DatabaseInitializer.Initialize(sourceDb, seedDemoData: false);
            DbMeta.Set(sourceDb, DbMeta.LastPushedSeq, "42");
            sourceInstance = DbMeta.Get(sourceDb, DbMeta.DbInstanceId)!;
        }
        SqliteConnection.ClearAllPools();

        var result = await _importService.ImportDatabaseFileAsync(sourceDbPath);
        Assert.IsTrue(result.Success, result.Message);

        using var db = _factory.CreateDbContext();
        Assert.IsNull(DbMeta.Get(db, DbMeta.LastPushedSeq), "Como antes con el cursor de reloj: la base cambió por completo");
        var instance = DbMeta.Get(db, DbMeta.DbInstanceId);
        Assert.IsNotNull(instance);
        Assert.AreNotEqual(sourceInstance, instance, "Es otro archivo");
    }

    private static string SampleCatalogPath()
    {
        string samplePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestAssets", "catalogo_muestra.xlsx");
        return File.Exists(samplePath)
            ? samplePath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../TestAssets/catalogo_muestra.xlsx"));
    }

    [TestMethod]
    public async Task DataManagement_ExcelImport_ShowsStepsAndNeedsNoTypedWord()
    {
        var vm = new DataManagementViewModel(_backupService, _importService);
        var samplePath = SampleCatalogPath();
        DialogService.OpenFilePickerHandler = (_, _) => samplePath;

        vm.StartImportExcelCommand.Execute(null);

        Assert.IsTrue(vm.IsImportSheetOpen, "La hoja de importar debe abrirse");
        Assert.IsTrue(vm.IsConfirmStep);
        Assert.AreEqual(DataManagementViewModel.ImportExcel, vm.ImportKind);
        Assert.AreEqual(samplePath, vm.ImportFilePath);
        Assert.AreEqual("catalogo_muestra.xlsx", vm.ImportFileName);
        Assert.IsFalse(vm.RequiresTypedConfirmation, "El catálogo se respalda antes y no borra nada: basta un clic");
        Assert.IsTrue(vm.CanProceedWithImport);
        Assert.AreEqual(3, vm.ImportSteps.Count);
        Assert.AreEqual(1, vm.ImportSteps[0].Number);
        StringAssert.Contains(vm.ImportSteps[0].Text, "respaldo");

        await vm.ExecuteImportCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.HasImportResult);
        Assert.IsFalse(vm.IsConfirmStep, "Tras importar se muestra el resultado, no la confirmación");
        Assert.IsTrue(vm.ImportResult!.Success, vm.ImportResult.Message);
        StringAssert.Contains(vm.ImportResult.Message, "Catálogo procesado");
        Assert.IsTrue(vm.ImportResultLines.Any(l => l.StartsWith("Respaldo previo")), "Debe decir qué respaldo se creó");
        Assert.IsTrue(vm.HasBackups, "El respaldo previo aparece en la lista");

        vm.CloseImportSheetCommand.Execute(null);
        Assert.IsFalse(vm.IsImportSheetOpen);
        Assert.IsFalse(vm.HasImportResult);
        Assert.AreEqual(string.Empty, vm.ImportFilePath);
    }

    [TestMethod]
    public async Task DataManagement_RestoreBackup_RequiresConfirmarAndRestoresThatState()
    {
        var vm = new DataManagementViewModel(_backupService, _importService);
        Assert.IsFalse(vm.HasBackups);
        Assert.AreEqual("Sin respaldos todavía", vm.LastBackupText);

        await vm.BackupNowCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.HasBackups);
        StringAssert.StartsWith(vm.LastBackupText, "Último respaldo hoy");
        var backup = vm.Backups[0];

        // Algo que se registra después del respaldo y que se pierde al volver a él
        using (var db = _factory.CreateDbContext())
        {
            var cat = new Category { Name = "Posterior" };
            db.Categories.Add(cat);
            db.Products.Add(new Product { Code = "POST-1", Name = "Registrado después del respaldo", Category = cat });
            db.SaveChanges();
        }

        vm.RestoreBackupCommand.Execute(backup);
        Assert.IsTrue(vm.IsDatabaseRestore);
        Assert.IsTrue(vm.RequiresTypedConfirmation, "Reemplazar toda la base es irreversible: pide CONFIRMAR");
        Assert.IsFalse(vm.CanProceedWithImport);
        vm.ImportConfirmWord = "SI";
        Assert.IsFalse(vm.CanProceedWithImport);
        vm.ImportConfirmWord = "CONFIRM";
        Assert.IsFalse(vm.CanProceedWithImport);
        vm.ImportConfirmWord = "CONFIRMAR";
        Assert.IsTrue(vm.CanProceedWithImport);

        SqliteConnection.ClearAllPools();
        await vm.ExecuteImportCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.ImportResult!.Success, vm.ImportResult.Message);
        Assert.AreEqual(string.Empty, vm.ImportConfirmWord, "La palabra se limpia al terminar");
        Assert.IsTrue(File.Exists(backup.Path), "El respaldo elegido sigue existiendo");
        using (var db = _factory.CreateDbContext())
        {
            Assert.IsFalse(db.Products.Any(p => p.Code == "POST-1"), "Se volvió al estado del respaldo");
        }
        Assert.AreEqual(2, vm.Backups.Count, "El respaldo previo a restaurar no pisa al elegido aunque sea del mismo segundo");
    }

    [TestMethod]
    public async Task RestoreOldestBackup_WhenRetentionIsFull_StillWorks()
    {
        // Con los 14 respaldos llenos, el respaldo previo a restaurar borra el más antiguo; si ese es el
        // que se restaura, antes fallaba porque el archivo ya no existía.
        Directory.CreateDirectory(_backupService.BackupDirectory);
        for (int i = 0; i < BackupService.RetainedBackups; i++)
        {
            await _backupService.CreateBackupNowAsync();
        }
        var backups = _backupService.GetBackups();
        for (int i = 0; i < backups.Count; i++)
        {
            File.SetLastWriteTime(backups[i].FullName, DateTime.Now.AddDays(-i - 1));
        }
        var oldest = _backupService.GetBackups().Last();

        SqliteConnection.ClearAllPools();
        var result = await _importService.ImportDatabaseFileAsync(oldest.FullName);

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(BackupService.RetainedBackups, _backupService.GetBackups().Count);
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
