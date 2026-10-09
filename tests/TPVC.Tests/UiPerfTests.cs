using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Tests;

/// <summary>
/// Medición de rendimiento de la interfaz (solo Windows; la CI la corre aparte, en su propio proceso,
/// para que el arranque se mida con el código sin compilar todavía). No falla por tiempos: deja
/// rendimiento.md en TPVC_PERF_DIR para comparar antes y después de un cambio.
/// Mide: arranque (base, ventana y datos iniciales), cambio de pantalla (primera vez y en caliente),
/// abrir un expediente con muchas consultas y la memoria tras recorrer todo.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UiPerfTests
{
    [TestMethod]
    [TestCategory("Rendimiento")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public void Measure_StartupScreensChartAndMemory()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { new UiPerfSession().Execute(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal sealed class UiPerfSession
{
    private const double Width = 1366;
    private const double Height = 860;
    private const int ManyConsultations = 400;
    private const int WarmRounds = 5;

    private static readonly string[] Screens =
    {
        "MainHub", "Pos", "Records", "Dashboard", "Inventory", "Batches", "Movements", "Services",
        "ClinicProfile", "Appearance", "Data", "TursoSync", "Updates"
    };

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"tpvc_perf_{Guid.NewGuid():N}");
    private readonly string? _outputDir = Environment.GetEnvironmentVariable("TPVC_PERF_DIR");
    private readonly List<(string Section, string Name, string Value)> _rows = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Execute()
    {
        Directory.CreateDirectory(_workDir);
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            Run();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }
        Write();
    }

    private void Run()
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();

        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        foreach (var dictionary in new[] { "Styles/ThemeColors.xaml", "Styles/Styles.xaml", "Styles/DesignSystem.xaml" })
        {
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/TPVC;component/{dictionary}", UriKind.Absolute)
            });
        }
        var themes = new ThemeService();
        themes.ApplyTheme(new ThemeSettings());
        Add("Arranque", "Estilos y tema", step);

        step.Restart();
        var dbPath = Path.Combine(_workDir, "consultorio.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={dbPath}");
        using (var db = new AppDbContext(builder.Options))
        {
            DatabaseInitializer.Initialize(db);
        }
        Add("Arranque", "Base de datos (crear y sembrar la demostración)", step);

        var bigPatientId = SeedManyConsultations(builder.Options);

        step.Restart();
        var factory = new AppDbContextFactory(builder.Options);
        var inventory = new InventoryService(factory);
        var profile = new ClinicProfileService(Path.Combine(_workDir, "clinic_profile.json"));
        var tursoSettings = new TursoSettingsService(Path.Combine(_workDir, "turso.json"));
        var main = new MainViewModel(
            inventory,
            new ConsultationService(seedDemoPatients: false),
            themes,
            new AppUpdateService(),
            new BackupService(dbPath),
            profile,
            printingService: null,
            tursoSyncService: new TursoSyncService(factory, tursoSettings, profile, inventory),
            tursoSettingsService: tursoSettings,
            dataImportService: null,
            consultationDraftStore: new ConsultationDraftStore(Path.Combine(_workDir, "consulta.json")),
            notificationService: new NotificationService());
        main.IsFirstRunWizardOpen = false;
        Add("Arranque", "ViewModels", step);

        step.Restart();
        var window = new MainWindow(main) { Width = Width, Height = Height };
        var host = (System.Windows.FrameworkElement)window.Content;
        Draw(host);
        Add("Arranque", "Ventana principal hasta el primer dibujo", step);

        step.Restart();
        WaitFor(main.MedicalRecordsVM.InitialLoad);
        Add("Arranque", "Datos iniciales de Pacientes", step);
        Add("Arranque", "Total", total);

        // Cambio de pantalla: la primera visita crea la vista; las siguientes la reutilizan
        var cold = new Dictionary<string, double>();
        var warm = Screens.ToDictionary(s => s, _ => new List<double>());
        foreach (var screen in Screens) cold[screen] = Visit(host, main, screen);
        for (int round = 0; round < WarmRounds; round++)
        {
            foreach (var screen in Screens) warm[screen].Add(Visit(host, main, screen));
        }
        foreach (var screen in Screens)
        {
            _rows.Add(("Cambio de pantalla", screen, $"{cold[screen]:0} ms la primera vez · {Median(warm[screen]):0} ms después"));
        }
        _rows.Add(("Cambio de pantalla", "Promedio", $"{cold.Values.Average():0} ms la primera vez · {Screens.Average(s => Median(warm[s])):0} ms después"));

        // Expediente: la primera ficha crea la pantalla; luego una con cientos de consultas (se leen de
        // la base la primera vez) y la misma otra vez (ya en memoria)
        var records = main.MedicalRecordsVM;
        main.NavigateTo("Records");
        Settle(host);
        var regular = records.Patients.First(p => p.Id != bigPatientId);
        var open = Stopwatch.StartNew();
        records.OpenPatientChart(regular);
        PumpUntil(() => records.ConsultationsHistory.Count >= regular.ConsultationCount);
        Draw(host);
        Add("Expediente", "Abrir la primera ficha (crea la pantalla)", open);

        records.Navigation.GoToPatientsHome();
        Settle(host);
        var big = records.Patients.First(p => p.Id == bigPatientId);
        open.Restart();
        records.OpenPatientChart(big);
        var afterOpen = open.Elapsed.TotalMilliseconds;
        PumpUntil(() => records.ConsultationsHistory.Count >= ManyConsultations);
        var afterLoad = open.Elapsed.TotalMilliseconds;
        Pump();
        var afterPump = open.Elapsed.TotalMilliseconds;
        host.Measure(new System.Windows.Size(Width, Height));
        host.Arrange(new System.Windows.Rect(0, 0, Width, Height));
        host.UpdateLayout();
        var afterLayout = open.Elapsed.TotalMilliseconds;
        Draw(host);
        Add("Expediente", $"Abrir uno con {ManyConsultations} consultas", open);
        _rows.Add(("Desglose", "Abrir (código)", $"{afterOpen:0} ms"));
        _rows.Add(("Desglose", "Leer consultas", $"{afterLoad - afterOpen:0} ms"));
        _rows.Add(("Desglose", "Pendientes del despachador", $"{afterPump - afterLoad:0} ms"));
        _rows.Add(("Desglose", "Medir y acomodar", $"{afterLayout - afterPump:0} ms"));
        _rows.Add(("Desglose", "Dibujar", $"{open.Elapsed.TotalMilliseconds - afterLayout:0} ms"));
        _rows.Add(("Desglose", "Elementos del historial", $"{records.Timeline.Count} renglones, {CountVisuals(host)} elementos visuales"));

        records.Navigation.GoToPatientsHome();
        Settle(host);
        open.Restart();
        records.OpenPatientChart(big);
        Draw(host);
        Add("Expediente", "Abrirlo otra vez", open);

        Settle(host);
        open.Restart();
        records.ChartPrimaryAction();
        Draw(host);
        Add("Expediente", "Iniciar la consulta", open);
        WaitFor(records.DiscardActiveConsultationAsync());
        Draw(host);

        // Memoria después de recorrer todo (proceso de prueba: sirve para comparar, no es la cifra absoluta)
        foreach (var screen in Screens) Visit(host, main, screen);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        _rows.Add(("Memoria", "Objetos vivos (GC)", $"{GC.GetTotalMemory(forceFullCollection: true) / 1048576.0:0.0} MB"));
        _rows.Add(("Memoria", "Memoria privada del proceso", $"{process.PrivateMemorySize64 / 1048576.0:0} MB"));
        _rows.Add(("Memoria", "Conjunto de trabajo", $"{process.WorkingSet64 / 1048576.0:0} MB"));
    }

    /// <summary>Un paciente de la demostración con cientos de consultas con signos (como años de seguimiento).</summary>
    private static int SeedManyConsultations(DbContextOptions<AppDbContext> options)
    {
        using var db = new AppDbContext(options);
        var patient = db.Patients.OrderBy(p => p.Id).First();
        var start = DateTime.Today.AddDays(-ManyConsultations * 7);
        for (int i = 0; i < ManyConsultations; i++)
        {
            var date = start.AddDays(i * 7).AddHours(10);
            db.PatientConsultations.Add(new PatientConsultation
            {
                SyncId = Guid.NewGuid(),
                UpdatedAtUtc = DateTime.UtcNow,
                PatientRecordId = patient.Id,
                Folio = $"CNS-PERF-{i:D4}",
                Date = date,
                StartedAt = date,
                EndedAt = date.AddMinutes(20),
                ChiefComplaint = "Control de hipertensión arterial y diabetes",
                Diagnosis = i % 3 == 0 ? "I10 Hipertensión esencial" : "E11 Diabetes mellitus tipo 2",
                VitalSigns = $"TA: {120 + i % 20}/{80 + i % 10} | FC: 76 | Peso: {78 + i % 5}",
                BloodPressureSystolic = 120 + i % 20,
                BloodPressureDiastolic = 80 + i % 10,
                Weight = 78 + i % 5,
                Glucose = 100 + i % 40,
                PrescriptionsJson = "[]"
            });
        }
        db.SaveChanges();
        return patient.Id;
    }

    private static double Visit(System.Windows.FrameworkElement host, MainViewModel main, string screen)
    {
        var watch = Stopwatch.StartNew();
        main.NavigateTo(screen);
        Draw(host);
        var elapsed = watch.Elapsed.TotalMilliseconds;
        // Lo que la pantalla carga en segundo plano termina aquí, fuera de la medición de la siguiente
        Settle(host);
        return elapsed;
    }

    private static void Settle(System.Windows.FrameworkElement host)
    {
        var quiet = Stopwatch.StartNew();
        while (quiet.ElapsedMilliseconds < 250)
        {
            Pump();
            Thread.Sleep(5);
        }
        Draw(host);
    }

    /// <summary>Lo que hace falta para ver la pantalla: atender lo pendiente, medir, acomodar y dibujar.</summary>
    private static void Draw(System.Windows.FrameworkElement host)
    {
        Pump();
        host.Measure(new System.Windows.Size(Width, Height));
        host.Arrange(new System.Windows.Rect(0, 0, Width, Height));
        host.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width, (int)Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
    }

    private static int CountVisuals(System.Windows.DependencyObject root)
    {
        int count = 1;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            count += CountVisuals(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        }
        return count;
    }

    private static void Pump()
    {
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
    }

    private static void PumpUntil(Func<bool> done)
    {
        var limit = Stopwatch.StartNew();
        while (!done() && limit.Elapsed < TimeSpan.FromSeconds(30))
        {
            Pump();
            Thread.Sleep(1);
        }
    }

    private static void WaitFor(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        var timeout = new Timer(_ => frame.Continue = false, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timeout.Dispose();
        if (task.IsFaulted) ExceptionDispatchInfo.Capture(task.Exception!.InnerException ?? task.Exception).Throw();
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private void Add(string section, string name, Stopwatch watch) =>
        _rows.Add((section, name, $"{watch.Elapsed.TotalMilliseconds:0} ms"));

    private void Write()
    {
        var text = new StringBuilder();
        text.AppendLine("## Rendimiento de la interfaz (en proceso)");
        text.AppendLine();
        foreach (var group in _rows.GroupBy(r => r.Section))
        {
            text.AppendLine($"| {group.Key} | |");
            text.AppendLine("|---|---|");
            foreach (var row in group) text.AppendLine($"| {row.Name} | {row.Value} |");
            text.AppendLine();
        }
        Console.WriteLine(text.ToString());
        if (string.IsNullOrEmpty(_outputDir)) return;
        Directory.CreateDirectory(_outputDir);
        File.WriteAllText(Path.Combine(_outputDir, "rendimiento.md"), text.ToString(), Encoding.UTF8);
    }
}
