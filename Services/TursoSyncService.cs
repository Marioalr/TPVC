using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.ViewModels;

namespace TPVC.Services;

/// <summary>
/// Motor de sincronización bidireccional incremental entre SQLite local y Turso Cloud (libSQL).
/// Diseñado para operar 100% GRATIS en el Free Tier de Turso con hasta 3 dispositivos
/// (1 PC Win11, 1 Tablet Android, 1 Celular Android administrador).
/// Utiliza resolución de conflictos Last-Write-Wins (LWW) y referencias desacopladas por SyncId.
/// </summary>
public class TursoSyncService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly TursoSettingsService _settingsService;
    private readonly ClinicProfileService _clinicProfileService;
    private readonly InventoryService? _inventoryService;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Debouncer _autoSyncDebouncer = new();

    public TursoSyncStatus Status { get; private set; } = TursoSyncStatus.NotConfigured;
    public string StatusMessage { get; private set; } = "Sin configurar";
    public TursoSyncResult? LastSyncResult { get; private set; }

    public event Action<TursoSyncStatus, string>? StatusChanged;
    public event Action<TursoSyncResult>? SyncCompleted;

    public TursoSyncService(
        IDbContextFactory<AppDbContext> contextFactory,
        TursoSettingsService settingsService,
        ClinicProfileService clinicProfileService,
        InventoryService? inventoryService = null)
    {
        _contextFactory = contextFactory;
        _settingsService = settingsService;
        _clinicProfileService = clinicProfileService;
        _inventoryService = inventoryService;

        UpdateInitialStatus();

        if (_inventoryService != null)
        {
            _inventoryService.OnInventoryChanged += OnLocalDataChanged;
            _inventoryService.OnPatientsChanged += OnLocalDataChanged;
            _inventoryService.OnSalesChanged += OnLocalDataChanged;
            _inventoryService.OnAppointmentsChanged += OnLocalDataChanged;
        }

        _settingsService.SettingsChanged += UpdateInitialStatus;
    }

    private void UpdateInitialStatus()
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl))
        {
            Status = TursoSyncStatus.NotConfigured;
            StatusMessage = "Sincronización en la nube desactivada (Modo Local SQLite)";
        }
        else
        {
            Status = TursoSyncStatus.Idle;
            StatusMessage = settings.LastSyncUtc.HasValue
                ? $"Conectado a Turso • Último sync: {settings.LastSyncUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}"
                : "Conectado a Turso • Pendiente primera sincronización";
        }
        StatusChanged?.Invoke(Status, StatusMessage);
    }

    private void OnLocalDataChanged()
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || !settings.AutoSyncOnChanges || string.IsNullOrWhiteSpace(settings.DatabaseUrl))
            return;

        // Debounce de 4 segundos: si el médico guarda varios registros seguidos,
        // se agrupan en una única llamada eficiente a Turso (máximo ahorro de cuota gratuita)
        _ = _autoSyncDebouncer.RunAsync(async () =>
        {
            try
            {
                await SyncAsync(isBackgroundAutoSync: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Auto-sincronización con Turso no pudo completarse: {ex.Message}");
            }
        }, delayMs: 4000);
    }

    public async Task<TursoSyncResult> SyncAsync(bool isBackgroundAutoSync = false, CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        if (!settings.IsSyncEnabled || string.IsNullOrWhiteSpace(settings.DatabaseUrl))
        {
            return new TursoSyncResult
            {
                Success = false,
                ErrorMessage = "La sincronización no está habilitada o falta configurar la URL de Turso."
            };
        }

        if (!await _syncLock.WaitAsync(isBackgroundAutoSync ? 0 : 5000, ct))
        {
            return new TursoSyncResult
            {
                Success = false,
                ErrorMessage = "Ya hay una sincronización en curso. Por favor espere un momento."
            };
        }

        var sw = Stopwatch.StartNew();
        Status = TursoSyncStatus.Syncing;
        StatusMessage = "Sincronizando con Turso Cloud...";
        StatusChanged?.Invoke(Status, StatusMessage);

        int rowsPushed = 0;
        int rowsPulled = 0;
        var syncCutoffUtc = DateTime.UtcNow;
        var lastSyncUtc = settings.LastSyncUtc ?? DateTime.MinValue;

        try
        {
            using var client = new TursoClient(settings.DatabaseUrl, settings.AuthToken);

            // 1. Asegurar esquema en Turso si es la primera vez
            await EnsureRemoteSchemaAsync(client, ct);

            // 2. Registrar este dispositivo en la nube
            await RegisterDeviceAsync(client, settings, ct);

            // 3. PUSH: Subir cambios locales a Turso
            rowsPushed = await PushLocalChangesAsync(client, lastSyncUtc, syncCutoffUtc, ct);

            // 4. PULL: Descargar cambios de Turso a la base local
            rowsPulled = await PullRemoteChangesAsync(client, lastSyncUtc, ct);

            // 5. Sincronizar perfil de la clínica si aplica
            await SyncClinicProfileAsync(client, ct);

            sw.Stop();
            settings.LastSyncUtc = syncCutoffUtc;
            _settingsService.RecordUsage(rowsPulled + 15, rowsPushed + 10);
            _settingsService.SaveSettings(settings);

            Status = TursoSyncStatus.Success;
            StatusMessage = $"✓ Sincronizado ({rowsPushed} subidos, {rowsPulled} recibidos • {sw.ElapsedMilliseconds} ms)";
            StatusChanged?.Invoke(Status, StatusMessage);

            var result = new TursoSyncResult
            {
                Success = true,
                RowsPushed = rowsPushed,
                RowsPulled = rowsPulled,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                TimestampUtc = syncCutoffUtc
            };
            LastSyncResult = result;
            SyncCompleted?.Invoke(result);

            // Notificar a las vistas si hubo cambios descargados
            if (rowsPulled > 0 && _inventoryService != null)
            {
                NotifyLocalViews();
            }

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Error("Fallo la sincronización con Turso", ex);

            Status = TursoSyncStatus.Error;
            StatusMessage = $"Error de sincronización: {ex.Message}";
            StatusChanged?.Invoke(Status, StatusMessage);

            var result = new TursoSyncResult
            {
                Success = false,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message,
                TimestampUtc = DateTime.UtcNow
            };
            LastSyncResult = result;
            SyncCompleted?.Invoke(result);
            return result;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Sube todos los datos locales existentes a Turso (migración inicial completa de la base).
    /// </summary>
    public async Task<TursoSyncResult> InitialUploadToTursoAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.CurrentSettings;
        if (string.IsNullOrWhiteSpace(settings.DatabaseUrl))
        {
            return new TursoSyncResult { Success = false, ErrorMessage = "URL de Turso no especificada." };
        }

        await _syncLock.WaitAsync(ct);
        var sw = Stopwatch.StartNew();

        try
        {
            Status = TursoSyncStatus.Syncing;
            StatusMessage = "Preparando esquema e inicio en Turso...";
            StatusChanged?.Invoke(Status, StatusMessage);

            using var client = new TursoClient(settings.DatabaseUrl, settings.AuthToken);
            await EnsureRemoteSchemaAsync(client, ct);
            await RegisterDeviceAsync(client, settings, ct);

            int totalPushed = await PushLocalChangesAsync(client, DateTime.MinValue, DateTime.UtcNow, ct);
            await SyncClinicProfileAsync(client, ct);

            sw.Stop();
            settings.LastSyncUtc = DateTime.UtcNow;
            _settingsService.RecordUsage(20, totalPushed);
            _settingsService.SaveSettings(settings);

            Status = TursoSyncStatus.Success;
            StatusMessage = $"✓ Base local subida a Turso con éxito ({totalPushed} registros subidos)";
            StatusChanged?.Invoke(Status, StatusMessage);

            var res = new TursoSyncResult
            {
                Success = true,
                RowsPushed = totalPushed,
                RowsPulled = 0,
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
            LastSyncResult = res;
            SyncCompleted?.Invoke(res);
            return res;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Error("Error en migración inicial a Turso", ex);
            Status = TursoSyncStatus.Error;
            StatusMessage = $"Error en carga inicial: {ex.Message}";
            StatusChanged?.Invoke(Status, StatusMessage);
            return new TursoSyncResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task EnsureRemoteSchemaAsync(TursoClient client, CancellationToken ct = default)
    {
        const string schemaSql = @"
CREATE TABLE IF NOT EXISTS Categories (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    Name TEXT NOT NULL,
    Description TEXT,
    Icon TEXT,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Products (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL,
    GenericName TEXT,
    Presentation TEXT,
    CategorySyncId TEXT,
    Price REAL NOT NULL,
    CurrentStock INTEGER NOT NULL,
    MinStock INTEGER NOT NULL,
    OptimalStock INTEGER NOT NULL,
    Location TEXT,
    RequiresRefrigeration INTEGER NOT NULL,
    Notes TEXT,
    Unit TEXT,
    IsActive INTEGER NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ProductBatches (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    ProductSyncId TEXT NOT NULL,
    BatchNumber TEXT NOT NULL,
    ExpirationDate TEXT NOT NULL,
    Quantity INTEGER NOT NULL,
    ReceivedDate TEXT NOT NULL,
    Notes TEXT,
    IsDepleted INTEGER NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS InventoryMovements (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    ProductSyncId TEXT NOT NULL,
    BatchSyncId TEXT,
    MovementType INTEGER NOT NULL,
    Quantity INTEGER NOT NULL,
    StockBefore INTEGER NOT NULL,
    StockAfter INTEGER NOT NULL,
    Date TEXT NOT NULL,
    Reason TEXT,
    Responsible TEXT,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ClinicalServices (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL,
    Description TEXT,
    Price REAL NOT NULL,
    Category TEXT,
    IsActive INTEGER NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ServiceDefaultProducts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    ServiceSyncId TEXT NOT NULL,
    ProductSyncId TEXT NOT NULL,
    DefaultQuantity INTEGER NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Patients (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    RecordNumber TEXT NOT NULL,
    FullName TEXT NOT NULL,
    Age INTEGER NOT NULL,
    Gender TEXT,
    Phone TEXT,
    Email TEXT,
    Address TEXT,
    BloodType TEXT,
    Allergies TEXT,
    ChronicDiseases TEXT,
    LastVisit TEXT,
    LastDiagnosis TEXT,
    VitalSigns TEXT,
    ClinicalNotes TEXT,
    ActivePrescriptionJson TEXT,
    ActiveSuppliesJson TEXT,
    ActiveServicesJson TEXT,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS PatientConsultations (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    PatientSyncId TEXT NOT NULL,
    Folio TEXT NOT NULL,
    Date TEXT NOT NULL,
    StartedAt TEXT,
    EndedAt TEXT,
    ChiefComplaint TEXT,
    VitalSigns TEXT,
    BloodPressureSystolic REAL,
    BloodPressureDiastolic REAL,
    HeartRate REAL,
    RespiratoryRate REAL,
    Temperature REAL,
    Weight REAL,
    Height REAL,
    BMI REAL,
    Glucose REAL,
    PhysicalExamination TEXT,
    SystemsReview TEXT,
    Diagnosis TEXT,
    TreatmentPlan TEXT,
    Notes TEXT,
    PrescriptionsJson TEXT,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Appointments (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    PatientSyncId TEXT,
    PatientName TEXT NOT NULL,
    PatientRecordNumber TEXT,
    PatientPhone TEXT,
    Date TEXT NOT NULL,
    Time TEXT,
    DurationMinutes INTEGER NOT NULL,
    Reason TEXT,
    Notes TEXT,
    Status TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Sales (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    Folio TEXT NOT NULL,
    Date TEXT NOT NULL,
    CustomerName TEXT,
    PaymentMethod TEXT,
    Cashier TEXT,
    Total REAL NOT NULL,
    IsCancelled INTEGER NOT NULL,
    CancellationReason TEXT,
    CancelledAt TEXT,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS SaleItems (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SyncId TEXT NOT NULL UNIQUE,
    SaleSyncId TEXT NOT NULL,
    ProductSyncId TEXT,
    ClinicalServiceId INTEGER,
    IsService INTEGER NOT NULL,
    Code TEXT,
    Description TEXT NOT NULL,
    Quantity INTEGER NOT NULL,
    UnitPrice REAL NOT NULL,
    Subtotal REAL NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS SyncTombstones (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    EntityType TEXT NOT NULL,
    SyncId TEXT NOT NULL,
    DeletedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ClinicProfiles (
    Id INTEGER PRIMARY KEY,
    DoctorName TEXT,
    ProfessionalLicense TEXT,
    Specialty TEXT,
    University TEXT,
    ClinicName TEXT,
    Address TEXT,
    Phone TEXT,
    Email TEXT,
    TaxId TEXT,
    AdditionalNotes TEXT,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS RegisteredDevices (
    DeviceId TEXT PRIMARY KEY,
    DeviceName TEXT NOT NULL,
    DeviceRole TEXT NOT NULL,
    AppVersion TEXT NOT NULL,
    LastSeenUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_products_sync ON Products(SyncId);
CREATE INDEX IF NOT EXISTS idx_patients_sync ON Patients(SyncId);
CREATE INDEX IF NOT EXISTS idx_consultations_sync ON PatientConsultations(SyncId);
CREATE INDEX IF NOT EXISTS idx_sales_sync ON Sales(SyncId);
CREATE INDEX IF NOT EXISTS idx_appointments_sync ON Appointments(SyncId);
";

        await client.ExecuteScriptAsync(schemaSql, ct);
    }

    private static async Task RegisterDeviceAsync(TursoClient client, TursoSettings settings, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc)
VALUES (?, ?, ?, ?, ?)
ON CONFLICT(DeviceId) DO UPDATE SET
    DeviceName = excluded.DeviceName,
    DeviceRole = excluded.DeviceRole,
    AppVersion = excluded.AppVersion,
    LastSeenUtc = excluded.LastSeenUtc;
";
        await client.ExecuteAsync(sql, new object?[]
        {
            settings.DeviceId,
            settings.DeviceName,
            settings.DeviceRole.ToString(),
            "1.8.1",
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
        }, ct);
    }

    private async Task<int> PushLocalChangesAsync(TursoClient client, DateTime lastSyncUtc, DateTime syncCutoffUtc, CancellationToken ct)
    {
        using var db = _contextFactory.CreateDbContext();
        var statements = new List<(string Sql, object?[]? Args)>();

        // 1. Categorías
        var cats = await db.Categories.AsNoTracking().Where(c => c.UpdatedAtUtc > lastSyncUtc && c.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var c in cats)
        {
            statements.Add((
                @"INSERT INTO Categories (SyncId, Name, Description, Icon, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    Name = excluded.Name, Description = excluded.Description, Icon = excluded.Icon, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= Categories.UpdatedAtUtc;",
                new object?[] { c.SyncId.ToString(), c.Name, c.Description, c.Icon, FormatDate(c.UpdatedAtUtc) }
            ));
        }

        // 2. Productos
        var prods = await db.Products.Include(p => p.Category).AsNoTracking().Where(p => p.UpdatedAtUtc > lastSyncUtc && p.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var p in prods)
        {
            statements.Add((
                @"INSERT INTO Products (SyncId, Code, Name, GenericName, Presentation, CategorySyncId, Price, CurrentStock, MinStock, OptimalStock, Location, RequiresRefrigeration, Notes, Unit, IsActive, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    Code = excluded.Code, Name = excluded.Name, GenericName = excluded.GenericName, Presentation = excluded.Presentation,
                    CategorySyncId = excluded.CategorySyncId, Price = excluded.Price, CurrentStock = excluded.CurrentStock,
                    MinStock = excluded.MinStock, OptimalStock = excluded.OptimalStock, Location = excluded.Location,
                    RequiresRefrigeration = excluded.RequiresRefrigeration, Notes = excluded.Notes, Unit = excluded.Unit,
                    IsActive = excluded.IsActive, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= Products.UpdatedAtUtc;",
                new object?[] {
                    p.SyncId.ToString(), p.Code, p.Name, p.GenericName, p.Presentation,
                    p.Category?.SyncId.ToString(), (double)p.Price, p.CurrentStock, p.MinStock, p.OptimalStock,
                    p.Location, p.RequiresRefrigeration ? 1 : 0, p.Notes, p.Unit, p.IsActive ? 1 : 0, FormatDate(p.UpdatedAtUtc)
                }
            ));
        }

        // 3. Lotes
        var batches = await db.ProductBatches.Include(b => b.Product).AsNoTracking().Where(b => b.UpdatedAtUtc > lastSyncUtc && b.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var b in batches)
        {
            if (b.Product == null) continue;
            statements.Add((
                @"INSERT INTO ProductBatches (SyncId, ProductSyncId, BatchNumber, ExpirationDate, Quantity, ReceivedDate, Notes, IsDepleted, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    ProductSyncId = excluded.ProductSyncId, BatchNumber = excluded.BatchNumber, ExpirationDate = excluded.ExpirationDate,
                    Quantity = excluded.Quantity, ReceivedDate = excluded.ReceivedDate, Notes = excluded.Notes, IsDepleted = excluded.IsDepleted,
                    UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= ProductBatches.UpdatedAtUtc;",
                new object?[] { b.SyncId.ToString(), b.Product.SyncId.ToString(), b.BatchNumber, FormatDate(b.ExpirationDate), b.Quantity, FormatDate(b.ReceivedDate), b.Notes, b.IsDepleted ? 1 : 0, FormatDate(b.UpdatedAtUtc) }
            ));
        }

        // 4. Movimientos de inventario
        var moves = await db.InventoryMovements.Include(m => m.Product).Include(m => m.Batch).AsNoTracking().Where(m => m.UpdatedAtUtc > lastSyncUtc && m.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var m in moves)
        {
            if (m.Product == null) continue;
            statements.Add((
                @"INSERT INTO InventoryMovements (SyncId, ProductSyncId, BatchSyncId, MovementType, Quantity, StockBefore, StockAfter, Date, Reason, Responsible, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    ProductSyncId = excluded.ProductSyncId, BatchSyncId = excluded.BatchSyncId, MovementType = excluded.MovementType,
                    Quantity = excluded.Quantity, StockBefore = excluded.StockBefore, StockAfter = excluded.StockAfter, Date = excluded.Date,
                    Reason = excluded.Reason, Responsible = excluded.Responsible, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= InventoryMovements.UpdatedAtUtc;",
                new object?[] { m.SyncId.ToString(), m.Product.SyncId.ToString(), m.Batch?.SyncId.ToString(), (int)m.MovementType, m.Quantity, m.StockBefore, m.StockAfter, FormatDate(m.Date), m.Reason, m.Responsible, FormatDate(m.UpdatedAtUtc) }
            ));
        }

        // 5. Servicios Clínicos
        var services = await db.ClinicalServices.AsNoTracking().Where(s => s.UpdatedAtUtc > lastSyncUtc && s.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var s in services)
        {
            statements.Add((
                @"INSERT INTO ClinicalServices (SyncId, Code, Name, Description, Price, Category, IsActive, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    Code = excluded.Code, Name = excluded.Name, Description = excluded.Description, Price = excluded.Price,
                    Category = excluded.Category, IsActive = excluded.IsActive, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= ClinicalServices.UpdatedAtUtc;",
                new object?[] { s.SyncId.ToString(), s.Code, s.Name, s.Description, (double)s.Price, s.Category, s.IsActive ? 1 : 0, FormatDate(s.UpdatedAtUtc) }
            ));
        }

        // 6. Pacientes (Expedientes)
        var patients = await db.Patients.AsNoTracking().Where(p => p.UpdatedAtUtc > lastSyncUtc && p.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var p in patients)
        {
            statements.Add((
                @"INSERT INTO Patients (SyncId, RecordNumber, FullName, Age, Gender, Phone, Email, Address, BloodType, Allergies, ChronicDiseases, LastVisit, LastDiagnosis, VitalSigns, ClinicalNotes, ActivePrescriptionJson, ActiveSuppliesJson, ActiveServicesJson, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    RecordNumber = excluded.RecordNumber, FullName = excluded.FullName, Age = excluded.Age, Gender = excluded.Gender, Phone = excluded.Phone,
                    Email = excluded.Email, Address = excluded.Address, BloodType = excluded.BloodType, Allergies = excluded.Allergies,
                    ChronicDiseases = excluded.ChronicDiseases, LastVisit = excluded.LastVisit, LastDiagnosis = excluded.LastDiagnosis,
                    VitalSigns = excluded.VitalSigns, ClinicalNotes = excluded.ClinicalNotes, ActivePrescriptionJson = excluded.ActivePrescriptionJson,
                    ActiveSuppliesJson = excluded.ActiveSuppliesJson, ActiveServicesJson = excluded.ActiveServicesJson, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= Patients.UpdatedAtUtc;",
                new object?[] { p.SyncId.ToString(), p.RecordNumber, p.FullName, p.Age, p.Gender, p.Phone, p.Email, p.Address, p.BloodType, p.Allergies, p.ChronicDiseases, FormatDate(p.LastVisit), p.LastDiagnosis, p.VitalSigns, p.ClinicalNotes, p.ActivePrescriptionJson, p.ActiveSuppliesJson, p.ActiveServicesJson, FormatDate(p.UpdatedAtUtc) }
            ));
        }

        // 7. Consultas Médicas
        var consults = await db.PatientConsultations.Include(c => c.PatientRecord).AsNoTracking().Where(c => c.UpdatedAtUtc > lastSyncUtc && c.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var c in consults)
        {
            if (c.PatientRecord == null) continue;
            statements.Add((
                @"INSERT INTO PatientConsultations (SyncId, PatientSyncId, Folio, Date, StartedAt, EndedAt, ChiefComplaint, VitalSigns, BloodPressureSystolic, BloodPressureDiastolic, HeartRate, RespiratoryRate, Temperature, Weight, Height, BMI, Glucose, PhysicalExamination, SystemsReview, Diagnosis, TreatmentPlan, Notes, PrescriptionsJson, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    PatientSyncId = excluded.PatientSyncId, Folio = excluded.Folio, Date = excluded.Date, StartedAt = excluded.StartedAt, EndedAt = excluded.EndedAt,
                    ChiefComplaint = excluded.ChiefComplaint, VitalSigns = excluded.VitalSigns, BloodPressureSystolic = excluded.BloodPressureSystolic,
                    BloodPressureDiastolic = excluded.BloodPressureDiastolic, HeartRate = excluded.HeartRate, RespiratoryRate = excluded.RespiratoryRate,
                    Temperature = excluded.Temperature, Weight = excluded.Weight, Height = excluded.Height, BMI = excluded.BMI, Glucose = excluded.Glucose,
                    PhysicalExamination = excluded.PhysicalExamination, SystemsReview = excluded.SystemsReview, Diagnosis = excluded.Diagnosis,
                    TreatmentPlan = excluded.TreatmentPlan, Notes = excluded.Notes, PrescriptionsJson = excluded.PrescriptionsJson, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= PatientConsultations.UpdatedAtUtc;",
                new object?[] {
                    c.SyncId.ToString(), c.PatientRecord.SyncId.ToString(), c.Folio, FormatDate(c.Date),
                    c.StartedAt.HasValue ? FormatDate(c.StartedAt.Value) : null,
                    c.EndedAt.HasValue ? FormatDate(c.EndedAt.Value) : null,
                    c.ChiefComplaint, c.VitalSigns,
                    (double?)c.BloodPressureSystolic, (double?)c.BloodPressureDiastolic,
                    (double?)c.HeartRate, (double?)c.RespiratoryRate,
                    (double?)c.Temperature, (double?)c.Weight,
                    (double?)c.Height, (double?)c.BMI, (double?)c.Glucose,
                    c.PhysicalExamination, c.SystemsReview, c.Diagnosis, c.TreatmentPlan, c.Notes,
                    c.PrescriptionsJson, FormatDate(c.UpdatedAtUtc)
                }
            ));
        }

        // 8. Citas Médicas
        var appts = await db.Appointments.Include(a => a.PatientRecord).AsNoTracking().Where(a => a.UpdatedAtUtc > lastSyncUtc && a.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var a in appts)
        {
            statements.Add((
                @"INSERT INTO Appointments (SyncId, PatientSyncId, PatientName, PatientRecordNumber, PatientPhone, Date, Time, DurationMinutes, Reason, Notes, Status, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    PatientSyncId = excluded.PatientSyncId, PatientName = excluded.PatientName, PatientRecordNumber = excluded.PatientRecordNumber,
                    PatientPhone = excluded.PatientPhone, Date = excluded.Date, Time = excluded.Time, DurationMinutes = excluded.DurationMinutes,
                    Reason = excluded.Reason, Notes = excluded.Notes, Status = excluded.Status, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= Appointments.UpdatedAtUtc;",
                new object?[] { a.SyncId.ToString(), a.PatientRecord?.SyncId.ToString(), a.PatientName, a.PatientRecordNumber, a.PatientPhone, FormatDate(a.Date), a.Time, a.DurationMinutes, a.Reason, a.Notes, a.Status, FormatDate(a.UpdatedAtUtc) }
            ));
        }

        // 9. Ventas (Tickets)
        var sales = await db.Sales.AsNoTracking().Where(s => s.UpdatedAtUtc > lastSyncUtc && s.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var s in sales)
        {
            statements.Add((
                @"INSERT INTO Sales (SyncId, Folio, Date, CustomerName, PaymentMethod, Cashier, Total, IsCancelled, CancellationReason, CancelledAt, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    Folio = excluded.Folio, Date = excluded.Date, CustomerName = excluded.CustomerName, PaymentMethod = excluded.PaymentMethod,
                    Cashier = excluded.Cashier, Total = excluded.Total, IsCancelled = excluded.IsCancelled, CancellationReason = excluded.CancellationReason,
                    CancelledAt = excluded.CancelledAt, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= Sales.UpdatedAtUtc;",
                new object?[] { s.SyncId.ToString(), s.Folio, FormatDate(s.Date), s.CustomerName, s.PaymentMethod, s.Cashier, (double)s.Total, s.IsCancelled ? 1 : 0, s.CancellationReason, s.CancelledAt.HasValue ? FormatDate(s.CancelledAt.Value) : null, FormatDate(s.UpdatedAtUtc) }
            ));
        }

        // 10. Items de Venta
        var saleItems = await db.SaleItems.Include(i => i.Sale).AsNoTracking().Where(i => i.UpdatedAtUtc > lastSyncUtc && i.UpdatedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var i in saleItems)
        {
            if (i.Sale == null) continue;
            statements.Add((
                @"INSERT INTO SaleItems (SyncId, SaleSyncId, ClinicalServiceId, IsService, Code, Description, Quantity, UnitPrice, Subtotal, UpdatedAtUtc)
                  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                  ON CONFLICT(SyncId) DO UPDATE SET
                    SaleSyncId = excluded.SaleSyncId, ClinicalServiceId = excluded.ClinicalServiceId, IsService = excluded.IsService,
                    Code = excluded.Code, Description = excluded.Description, Quantity = excluded.Quantity, UnitPrice = excluded.UnitPrice,
                    Subtotal = excluded.Subtotal, UpdatedAtUtc = excluded.UpdatedAtUtc
                  WHERE excluded.UpdatedAtUtc >= SaleItems.UpdatedAtUtc;",
                new object?[] { i.SyncId.ToString(), i.Sale.SyncId.ToString(), i.ClinicalServiceId, i.IsService ? 1 : 0, i.Code, i.Description, i.Quantity, (double)i.UnitPrice, (double)i.Subtotal, FormatDate(i.UpdatedAtUtc) }
            ));
        }

        // 11. Borrados (Tombstones)
        var tombstones = await db.SyncTombstones.AsNoTracking().Where(t => t.DeletedAtUtc > lastSyncUtc && t.DeletedAtUtc <= syncCutoffUtc).ToListAsync(ct);
        foreach (var t in tombstones)
        {
            statements.Add((
                @"INSERT INTO SyncTombstones (EntityType, SyncId, DeletedAtUtc) VALUES (?, ?, ?);",
                new object?[] { t.EntityType, t.SyncId.ToString(), FormatDate(t.DeletedAtUtc) }
            ));

            var tableName = MapEntityTypeToTable(t.EntityType);
            if (!string.IsNullOrEmpty(tableName))
            {
                statements.Add(($"DELETE FROM {tableName} WHERE SyncId = ?;", new object?[] { t.SyncId.ToString() }));
            }
        }

        if (statements.Count == 0) return 0;

        const int chunkSize = 100;
        int pushed = 0;
        for (int i = 0; i < statements.Count; i += chunkSize)
        {
            var chunk = statements.Skip(i).Take(chunkSize);
            await client.ExecuteBatchAsync(chunk, ct);
            pushed += chunk.Count();
        }

        return pushed;
    }

    private async Task<int> PullRemoteChangesAsync(TursoClient client, DateTime lastSyncUtc, CancellationToken ct)
    {
        string lastSyncStr = FormatDate(lastSyncUtc);
        int totalPulled = 0;

        // 1. Descargar Pacientes
        var remotePatients = await client.QueryAsync("SELECT * FROM Patients WHERE UpdatedAtUtc > ?;", new object?[] { lastSyncStr }, ct);
        if (remotePatients.Count > 0)
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var r in remotePatients)
            {
                if (!Guid.TryParse(r["SyncId"]?.ToString(), out var syncId)) continue;
                var updated = ParseDate(r["UpdatedAtUtc"]);

                var local = await db.Patients.FirstOrDefaultAsync(p => p.SyncId == syncId, ct);
                if (local == null)
                {
                    local = new PatientRecord { SyncId = syncId };
                    db.Patients.Add(local);
                }
                else if (local.UpdatedAtUtc >= updated)
                {
                    continue; // LWW: El registro local es más reciente
                }

                local.RecordNumber = r["RecordNumber"]?.ToString() ?? string.Empty;
                local.FullName = r["FullName"]?.ToString() ?? string.Empty;
                local.Age = Convert.ToInt32(r["Age"] ?? 0);
                local.Gender = r["Gender"]?.ToString() ?? string.Empty;
                local.Phone = r["Phone"]?.ToString() ?? string.Empty;
                local.Email = r["Email"]?.ToString() ?? string.Empty;
                local.Address = r["Address"]?.ToString() ?? string.Empty;
                local.BloodType = r["BloodType"]?.ToString() ?? string.Empty;
                local.Allergies = r["Allergies"]?.ToString() ?? string.Empty;
                local.ChronicDiseases = r["ChronicDiseases"]?.ToString() ?? string.Empty;
                local.LastVisit = ParseDate(r["LastVisit"]);
                local.LastDiagnosis = r["LastDiagnosis"]?.ToString() ?? string.Empty;
                local.VitalSigns = r["VitalSigns"]?.ToString() ?? string.Empty;
                local.ClinicalNotes = r["ClinicalNotes"]?.ToString() ?? string.Empty;
                local.ActivePrescriptionJson = r["ActivePrescriptionJson"]?.ToString() ?? string.Empty;
                local.ActiveSuppliesJson = r["ActiveSuppliesJson"]?.ToString() ?? string.Empty;
                local.ActiveServicesJson = r["ActiveServicesJson"]?.ToString() ?? string.Empty;
                local.UpdatedAtUtc = updated;

                totalPulled++;
            }
            await db.SaveChangesAsync(ct);
        }

        // 2. Descargar Productos
        var remoteProducts = await client.QueryAsync("SELECT * FROM Products WHERE UpdatedAtUtc > ?;", new object?[] { lastSyncStr }, ct);
        if (remoteProducts.Count > 0)
        {
            using var db = _contextFactory.CreateDbContext();
            var categories = await db.Categories.ToListAsync(ct);
            var defaultCategory = categories.FirstOrDefault();

            foreach (var r in remoteProducts)
            {
                if (!Guid.TryParse(r["SyncId"]?.ToString(), out var syncId)) continue;
                var updated = ParseDate(r["UpdatedAtUtc"]);

                var local = await db.Products.FirstOrDefaultAsync(p => p.SyncId == syncId, ct);
                if (local == null)
                {
                    local = new Product { SyncId = syncId };
                    db.Products.Add(local);
                }
                else if (local.UpdatedAtUtc >= updated)
                {
                    continue;
                }

                local.Code = r["Code"]?.ToString() ?? string.Empty;
                local.Name = r["Name"]?.ToString() ?? string.Empty;
                local.GenericName = r["GenericName"]?.ToString();
                local.Presentation = r["Presentation"]?.ToString();
                local.Price = Convert.ToDecimal(r["Price"] ?? 0);
                local.CurrentStock = Convert.ToInt32(r["CurrentStock"] ?? 0);
                local.MinStock = Convert.ToInt32(r["MinStock"] ?? 5);
                local.OptimalStock = Convert.ToInt32(r["OptimalStock"] ?? 20);
                local.Location = r["Location"]?.ToString();
                local.RequiresRefrigeration = Convert.ToInt32(r["RequiresRefrigeration"] ?? 0) == 1;
                local.Notes = r["Notes"]?.ToString();
                local.Unit = r["Unit"]?.ToString() ?? "Piezas";
                local.IsActive = Convert.ToInt32(r["IsActive"] ?? 1) == 1;
                local.UpdatedAtUtc = updated;

                if (Guid.TryParse(r["CategorySyncId"]?.ToString(), out var catSyncId))
                {
                    var matchedCategory = categories.FirstOrDefault(c => c.SyncId == catSyncId);
                    if (matchedCategory != null) local.CategoryId = matchedCategory.Id;
                }
                else if (local.CategoryId == 0 && defaultCategory != null)
                {
                    local.CategoryId = defaultCategory.Id;
                }

                totalPulled++;
            }
            await db.SaveChangesAsync(ct);
        }

        // 3. Descargar Citas
        var remoteAppts = await client.QueryAsync("SELECT * FROM Appointments WHERE UpdatedAtUtc > ?;", new object?[] { lastSyncStr }, ct);
        if (remoteAppts.Count > 0)
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var r in remoteAppts)
            {
                if (!Guid.TryParse(r["SyncId"]?.ToString(), out var syncId)) continue;
                var updated = ParseDate(r["UpdatedAtUtc"]);

                var local = await db.Appointments.FirstOrDefaultAsync(a => a.SyncId == syncId, ct);
                if (local == null)
                {
                    local = new PatientAppointment { SyncId = syncId };
                    db.Appointments.Add(local);
                }
                else if (local.UpdatedAtUtc >= updated)
                {
                    continue;
                }

                local.PatientName = r["PatientName"]?.ToString() ?? string.Empty;
                local.PatientRecordNumber = r["PatientRecordNumber"]?.ToString() ?? string.Empty;
                local.PatientPhone = r["PatientPhone"]?.ToString() ?? string.Empty;
                local.Date = ParseDate(r["Date"]);
                local.Time = r["Time"]?.ToString() ?? "10:00";
                local.DurationMinutes = Convert.ToInt32(r["DurationMinutes"] ?? 30);
                local.Reason = r["Reason"]?.ToString() ?? string.Empty;
                local.Notes = r["Notes"]?.ToString() ?? string.Empty;
                local.Status = r["Status"]?.ToString() ?? "Programada";
                local.UpdatedAtUtc = updated;

                if (Guid.TryParse(r["PatientSyncId"]?.ToString(), out var patSyncId))
                {
                    var pat = await db.Patients.FirstOrDefaultAsync(p => p.SyncId == patSyncId, ct);
                    if (pat != null) local.PatientRecordId = pat.Id;
                }

                totalPulled++;
            }
            await db.SaveChangesAsync(ct);
        }

        // 4. Descargar Borrados (Tombstones)
        var remoteTombstones = await client.QueryAsync("SELECT * FROM SyncTombstones WHERE DeletedAtUtc > ?;", new object?[] { lastSyncStr }, ct);
        if (remoteTombstones.Count > 0)
        {
            using var db = _contextFactory.CreateDbContext();
            foreach (var t in remoteTombstones)
            {
                if (!Guid.TryParse(t["SyncId"]?.ToString(), out var syncId)) continue;
                var entityType = t["EntityType"]?.ToString() ?? string.Empty;

                switch (entityType)
                {
                    case nameof(PatientRecord):
                        var pat = await db.Patients.FirstOrDefaultAsync(p => p.SyncId == syncId, ct);
                        if (pat != null) { db.Patients.Remove(pat); totalPulled++; }
                        break;
                    case nameof(Product):
                        var prod = await db.Products.FirstOrDefaultAsync(p => p.SyncId == syncId, ct);
                        if (prod != null) { db.Products.Remove(prod); totalPulled++; }
                        break;
                    case nameof(PatientAppointment):
                        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.SyncId == syncId, ct);
                        if (appt != null) { db.Appointments.Remove(appt); totalPulled++; }
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        return totalPulled;
    }

    private async Task SyncClinicProfileAsync(TursoClient client, CancellationToken ct)
    {
        var localProfile = _clinicProfileService.CurrentProfile;
        if (localProfile.IsConfigured)
        {
            const string sql = @"
INSERT INTO ClinicProfiles (Id, DoctorName, ProfessionalLicense, Specialty, University, ClinicName, Address, Phone, Email, TaxId, AdditionalNotes, UpdatedAtUtc)
VALUES (1, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
ON CONFLICT(Id) DO UPDATE SET
    DoctorName = excluded.DoctorName,
    ProfessionalLicense = excluded.ProfessionalLicense,
    Specialty = excluded.Specialty,
    University = excluded.University,
    ClinicName = excluded.ClinicName,
    Address = excluded.Address,
    Phone = excluded.Phone,
    Email = excluded.Email,
    TaxId = excluded.TaxId,
    AdditionalNotes = excluded.AdditionalNotes,
    UpdatedAtUtc = excluded.UpdatedAtUtc;";

            await client.ExecuteAsync(sql, new object?[]
            {
                localProfile.DoctorName,
                localProfile.ProfessionalLicense,
                localProfile.Specialty,
                localProfile.University,
                localProfile.ClinicName,
                localProfile.Address,
                localProfile.Phone,
                localProfile.Email,
                localProfile.TaxId,
                localProfile.AdditionalNotes,
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            }, ct);
        }
        else
        {
            // Intentar descargar el perfil si está configurado en Turso (ej. desde otro dispositivo)
            var rows = await client.QueryAsync("SELECT * FROM ClinicProfiles WHERE Id = 1;", Array.Empty<object?>(), ct);
            if (rows.Count > 0)
            {
                var r = rows[0];
                var docName = r["DoctorName"]?.ToString();
                var clinicName = r["ClinicName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(docName) || !string.IsNullOrWhiteSpace(clinicName))
                {
                    localProfile.DoctorName = docName ?? string.Empty;
                    localProfile.ProfessionalLicense = r["ProfessionalLicense"]?.ToString() ?? string.Empty;
                    localProfile.Specialty = r["Specialty"]?.ToString() ?? string.Empty;
                    localProfile.University = r["University"]?.ToString() ?? string.Empty;
                    localProfile.ClinicName = clinicName ?? string.Empty;
                    localProfile.Address = r["Address"]?.ToString() ?? string.Empty;
                    localProfile.Phone = r["Phone"]?.ToString() ?? string.Empty;
                    localProfile.Email = r["Email"]?.ToString() ?? string.Empty;
                    localProfile.TaxId = r["TaxId"]?.ToString() ?? string.Empty;
                    localProfile.AdditionalNotes = r["AdditionalNotes"]?.ToString() ?? string.Empty;
                    localProfile.IsConfigured = true;
                    localProfile.IsInitialSetupCompleted = true;
                    _clinicProfileService.SaveProfile(localProfile);
                }
            }
        }
    }

    private void NotifyLocalViews()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null)
            {
                app.Dispatcher.InvokeAsync(() =>
                {
                    _inventoryService?.NotifyAllChanged();
                });
            }
        }
        catch
        {
            // Silencioso
        }
    }

    private static string MapEntityTypeToTable(string entityType) => entityType switch
    {
        nameof(Category) => "Categories",
        nameof(Product) => "Products",
        nameof(ProductBatch) => "ProductBatches",
        nameof(InventoryMovement) => "InventoryMovements",
        nameof(ClinicalService) => "ClinicalServices",
        nameof(PatientRecord) => "Patients",
        nameof(PatientConsultation) => "PatientConsultations",
        nameof(PatientAppointment) => "Appointments",
        nameof(Sale) => "Sales",
        nameof(SaleItem) => "SaleItems",
        _ => string.Empty
    };

    private static string FormatDate(DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(object? raw)
    {
        if (raw == null) return DateTime.MinValue;
        var s = raw.ToString();
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        if (DateTime.TryParse(s, out dt)) return dt;
        return DateTime.MinValue;
    }
}
