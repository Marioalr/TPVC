using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Consultorio de prueba para las suites de la fase 6: base sembrada (la de demostración) en una
/// carpeta temporal, con su borrador de consulta y, si se pide, una nube simulada (FakeHranaServer).
/// </summary>
internal sealed class Fase6Clinic : IDisposable
{
    public const string CloudUrl = "https://tpvc-fase6.turso.io";

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"tpvc_fase6_{Guid.NewGuid():N}");
    public string DbPath { get; }
    public DbContextOptions<AppDbContext> Options { get; }
    public AppDbContextFactory Factory { get; }
    public InventoryService Inventory { get; }
    public string DraftPath => Path.Combine(Folder, "borradores", "consulta-en-curso.json");

    public FakeHranaServer? Cloud { get; private set; }
    private bool _ownsCloud;
    public TursoSyncService? Sync { get; private set; }
    public ConsultationHandoffService? Handoff { get; private set; }

    /// <param name="seed">false: base vacía (un segundo equipo que recibe todo de la nube).</param>
    public Fase6Clinic(bool seed = true)
    {
        Directory.CreateDirectory(Folder);
        DbPath = Path.Combine(Folder, "consultorio.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={DbPath}");
        Options = builder.Options;
        using (var db = new AppDbContext(Options))
        {
            if (seed)
            {
                DatabaseInitializer.Initialize(db);
            }
            else
            {
                db.Database.EnsureCreated();
                DatabaseInitializer.EnsureSchema(db);
            }
        }
        Factory = new AppDbContextFactory(Options);
        Inventory = new InventoryService(Factory);
    }

    public ConsultationDraftStore Store() => new(DraftPath);

    /// <summary>Abre "la aplicación" de Pacientes sobre esta base y espera la carga inicial.</summary>
    public async Task<MedicalRecordsViewModel> OpenAsync(ConsultationService consultations, bool drafts = true, NotificationService? notifications = null)
    {
        var records = new MedicalRecordsViewModel(Inventory, consultations,
            consultationDraftStore: drafts ? Store() : null,
            notificationService: notifications,
            consultationHandoffService: Handoff)
        {
            // Sin escrituras del temporizador durante la prueba: el borrador se escribe al pedirlo
            AutosaveDelay = TimeSpan.FromHours(1),
            HandoffSyncLockWait = TimeSpan.FromSeconds(5)
        };
        await records.InitialLoad;
        return records;
    }

    /// <summary>
    /// Nube simulada con este equipo sincronizado. Sin <paramref name="sharedCloud"/> crea la suya y
    /// registra una tablet enlazada que puede recibir consultas; con ella, este equipo es otro más de
    /// esa nube (p. ej. la tablet que recibe).
    /// </summary>
    public async Task ConnectCloudAsync(FakeHranaServer? sharedCloud = null, string deviceId = "device-pc", string deviceName = "PC del consultorio")
    {
        _ownsCloud = sharedCloud == null;
        Cloud = sharedCloud ?? new FakeHranaServer(Path.Combine(Folder, "nube.db"));
        var settings = new TursoSettingsService(Path.Combine(Folder, "turso.json"));
        var s = settings.CurrentSettings;
        s.IsSyncEnabled = true;
        s.AutoSyncOnChanges = false;
        s.AutoSyncOnStartup = false;
        s.DatabaseUrl = CloudUrl;
        s.AuthToken = "fase6";
        s.DeviceId = deviceId;
        s.DeviceName = deviceName;
        settings.SaveSettings(s);

        var profile = new ClinicProfileService(Path.Combine(Folder, "clinic_profile.json"));
        Sync = new TursoSyncService(Factory, settings, profile, Inventory)
        {
            ClientFactory = (url, token) => new TursoClient(url, token, new HttpClient(Cloud, disposeHandler: false))
        };
        Handoff = new ConsultationHandoffService(Sync, settings) { AutoPoll = false };
        Assert.IsTrue((await Sync.SyncAsync()).Success, "Primera sincronización con la nube simulada");
        if (!_ownsCloud) return;

        using var held = Cloud.Lock();
        using var cmd = Cloud.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc) VALUES " +
                          "('device-tablet', 'Tablet del consultorio', 'AndroidTablet', '1.18.0', strftime('%Y-%m-%d %H:%M:%f','now','-4 seconds'))";
        cmd.ExecuteNonQuery();
    }

    public string CloudScalar(string sql)
    {
        using var held = Cloud!.Lock();
        using var cmd = Cloud!.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    public void Dispose()
    {
        if (_ownsCloud) Cloud?.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, recursive: true); } catch { }
    }
}

/// <summary>Guiones de uso compartidos por las suites de la fase 6.</summary>
internal static class Fase6Scenarios
{
    /// <summary>Paciente de la demostración con receta anterior y sin alergias que choquen con el guion.</summary>
    public const string ScriptedRecord = "EXP-2026-002";

    public static PatientRecord ScriptedPatient(MedicalRecordsViewModel records) =>
        records.Patients.Single(p => p.RecordNumber == ScriptedRecord);

    /// <summary>
    /// Una consulta completa: nota y signos en todas las secciones, un medicamento que se surte aquí
    /// (se carga a la cuenta), un insumo, un servicio con sus insumos por defecto y un cargo libre.
    /// </summary>
    public static async Task FillConsultationAsync(MedicalRecordsViewModel records, ConsultationService consultations)
    {
        records.Consultation.ConsultationChiefComplaint = "Control mensual de glucosa y presión";
        records.Consultation.ConsultationSystemsReview = "Niega poliuria y polidipsia";
        records.Consultation.ConsultationPhysicalExam = "Buen estado general, hidratada";
        records.Consultation.ConsultationTA = "135/85";
        records.Consultation.ConsultationFC = "76";
        records.Consultation.ConsultationFR = "18";
        records.Consultation.ConsultationTemp = "36.5";
        records.Consultation.ConsultationWeight = "68";
        records.Consultation.ConsultationHeight = "1.58";
        records.Consultation.ConsultationSpO2 = "97";
        records.Consultation.ConsultationGlucose = "142";
        records.Consultation.ConsultationDiagnosis = "E11.9 Diabetes mellitus tipo 2 sin complicaciones";
        records.Consultation.ConsultationTreatmentPlan = "Continuar metformina 850 mg cada 12 horas";
        records.Consultation.ConsultationNotes = "Revisión en un mes con glucosa en ayunas";
        records.Consultation.ConsultationGeneralIndications = "Dieta baja en azúcares y caminata diaria";
        records.Consultation.ConsultationSection = 4;

        var paracetamol = records.Catalog.AvailableProducts.Single(p => p.Code == "MED-001");
        records.Prescription.PrescriptionDosage = "1 tableta cada 8 horas por 3 días";
        await records.Prescription.SelectAndAddPrescriptionProductAsync(paracetamol);

        var gauze = records.Catalog.AvailableProducts.First(p => p.Name == "Gasa Estéril 10x10 cm");
        records.Supplies.SupplyInstructions = "Curación de herida";
        await records.Supplies.SelectAndAddSupplyProductAsync(gauze);

        var glucose = records.Catalog.Services.Single(s => s.Code == "SERV-004");
        await records.ClinicalServices.SelectAndAddServiceModalSuggestionAsync(glucose);

        Assert.IsTrue(consultations.AddCartItemToActiveConsultation(-1_000_777, "LIBRE", "Curación especial", null, null, "Servicio", 150m, 1));
    }
}
