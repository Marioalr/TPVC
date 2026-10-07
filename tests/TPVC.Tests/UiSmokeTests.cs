using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
/// Prueba de humo de la interfaz (solo en Windows, donde corre la CI). Carga la ventana principal
/// con datos de demostración, recorre las pantallas y sus estados principales, y falla si WPF
/// reporta bindings rotos o un recurso inexistente. También comprueba colores reales y, si existe
/// la variable TPVC_SCREENSHOTS_DIR, guarda capturas en claro y oscuro (artefacto de la CI).
/// </summary>
[TestClass]
[DoNotParallelize]
public class UiSmokeTests
{
    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public void Screens_LoadWithoutBindingErrors_AndKeepThemeColors()
    {
        if (!OperatingSystem.IsWindows()) return;
        UiSmokeRunner.Run();
    }
}

internal static class UiSmokeRunner
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { new UiSmokeSession().Execute(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal sealed class BindingErrorCollector : TraceListener
{
    private readonly object _gate = new();
    private System.Text.StringBuilder _line = new();

    public string Screen { get; set; } = "inicio";
    public List<string> Errors { get; } = new();

    public override void Write(string? message)
    {
        lock (_gate) _line.Append(message);
    }

    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            _line.Append(message);
            var text = _line.ToString();
            _line = new System.Text.StringBuilder();
            if (text.Contains("Error", StringComparison.Ordinal)) Errors.Add($"[{Screen}] {text}");
        }
    }
}

internal sealed class UiSmokeSession
{
    private const double Width = 1366;
    private const double Height = 860;

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"tpvc_ui_{Guid.NewGuid():N}");
    private readonly string? _screenshotDir = Environment.GetEnvironmentVariable("TPVC_SCREENSHOTS_DIR");
    private readonly BindingErrorCollector _bindingErrors = new();
    private readonly List<string> _failures = new();
    private System.Windows.Application _app = null!;
    private ThemeService _themes = null!;

    public void Execute()
    {
        Directory.CreateDirectory(_workDir);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try
        {
            SetUpApplication();
            CheckComponentColors();
            RunScreens();
        }
        finally
        {
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(_bindingErrors);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }

        var problems = _failures.Concat(_bindingErrors.Errors.Distinct()).ToList();
        if (problems.Count > 0)
        {
            Assert.Fail($"La interfaz reportó {problems.Count} problema(s):\n" + string.Join("\n", problems.Take(80)));
        }
    }

    private void SetUpApplication()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        _app.Resources.MergedDictionaries.Clear();
        foreach (var dictionary in new[] { "Styles/ThemeColors.xaml", "Styles/Styles.xaml", "Styles/DesignSystem.xaml" })
        {
            _app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/TPVC;component/{dictionary}", UriKind.Absolute)
            });
        }

        _themes = new ThemeService();
        _themes.ApplyTheme(new ThemeSettings());

        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingErrors);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }

    // ------------------------------------------------------------------ colores reales

    private System.Windows.Media.Color ThemeColorOf(string key) =>
        ((System.Windows.Media.SolidColorBrush)_app.Resources[key]).Color;

    private void CheckComponentColors()
    {
        _bindingErrors.Screen = "componentes";
        var primary = new System.Windows.Controls.Button { Content = "Iniciar consulta" };
        primary.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "ButtonPrimary");

        var badgeText = new System.Windows.Controls.TextBlock { Text = "En consulta" };
        var badge = new System.Windows.Controls.Border { Child = badgeText };
        badge.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "BadgeInfo");

        var plain = new System.Windows.Controls.TextBlock { Text = "Texto normal" };

        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(primary);
        panel.Children.Add(badge);
        panel.Children.Add(plain);
        var host = Host(panel);
        Layout(host, 400, 200);

        ExpectForeground("Texto de un botón principal", FindText(primary), ThemeColorOf("ThemeAccentText"));
        ExpectForeground("Texto dentro de un distintivo", badgeText, ThemeColorOf("ThemeInfoText"));
        ExpectForeground("Texto normal", plain, ThemeColorOf("ThemeText"));
    }

    private void ExpectForeground(string what, System.Windows.Controls.TextBlock? text, System.Windows.Media.Color expected)
    {
        if (text == null)
        {
            _failures.Add($"[colores] {what}: no se encontró el texto");
            return;
        }
        var actual = (text.Foreground as System.Windows.Media.SolidColorBrush)?.Color;
        if (actual != expected) _failures.Add($"[colores] {what}: {actual} en lugar de {expected}");
    }

    private static System.Windows.Controls.TextBlock? FindText(System.Windows.DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock tb) return tb;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindText(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    // ------------------------------------------------------------------ pantallas

    private void RunScreens()
    {
        var dbPath = Path.Combine(_workDir, "consultorio.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, $"Data Source={dbPath}");
        using (var db = new AppDbContext(builder.Options))
        {
            DatabaseInitializer.Initialize(db);
        }
        var factory = new AppDbContextFactory(builder.Options);
        var inventory = new InventoryService(factory);
        var consultations = new ConsultationService(seedDemoPatients: false);
        var profile = new ClinicProfileService(Path.Combine(_workDir, "clinic_profile.json"));
        var tursoSettings = new TursoSettingsService(Path.Combine(_workDir, "turso.json"));
        var tursoSync = new TursoSyncService(factory, tursoSettings, profile, inventory);
        var notifications = new NotificationService();

        var main = new MainViewModel(
            inventory,
            consultations,
            _themes,
            new AppUpdateService(),
            new BackupService(dbPath),
            profile,
            printingService: null,
            tursoSyncService: tursoSync,
            tursoSettingsService: tursoSettings,
            dataImportService: null,
            consultationDraftStore: new ConsultationDraftStore(Path.Combine(_workDir, "consulta.json")),
            notificationService: notifications);
        main.IsFirstRunWizardOpen = false;

        // La ventana real sin mostrarla: los cambios de tema llegan a todo su contenido
        var window = new MainWindow(main) { Width = Width, Height = Height };
        var host = window;

        WaitFor(main.MedicalRecordsVM.InitialLoad);
        notifications.Success("Expediente guardado");

        foreach (var dark in new[] { false, true })
        {
            _themes.ApplyTheme(dark
                ? new ThemeSettings { ThemeMode = "Oscuro", BackgroundColor = "#0F172A", SurfaceColor = "#1E293B", TextColor = "#F8FAFC", SecondaryTextColor = "#94A3B8", BorderColor = "#334155" }
                : new ThemeSettings());
            var suffix = dark ? "oscuro" : "claro";

            Show(host, main, "MainHub", $"01-inicio-{suffix}");
            Show(host, main, "Pos", $"02-pos-{suffix}");

            var records = main.MedicalRecordsVM;
            Show(host, main, "Records", $"03-pacientes-seleccionar-{suffix}");
            records.CloseSelectPatientModal();
            records.SelectPatient(records.Patients.First());
            Capture(host, $"04-pacientes-ficha-{suffix}");

            records.StartActiveConsultation();
            records.ConsultationChiefComplaint = "Fiebre y dolor de garganta de 2 días";
            records.ConsultationTA = "130/85";
            records.ConsultationDiagnosis = "J00 Rinofaringitis aguda";
            Capture(host, $"05-consulta-{suffix}");

            records.ConsultationSection = 5;
            Capture(host, $"06-consulta-tratamiento-{suffix}");

            WaitFor(records.OpenPrescriptionModalAsync());
            Capture(host, $"07-receta-{suffix}");
            records.ClosePrescriptionModal();

            WaitFor(records.FinalizeConsultationAsync());
            Capture(host, $"08-terminar-consulta-{suffix}");
            records.ClosePosModal();
            WaitFor(records.DiscardActiveConsultationAsync());

            WaitFor(records.NewPatientAsync());
            Capture(host, $"09-alta-paciente-{suffix}");
            records.ClosePatientModal();

            foreach (var sub in new[] { "Dashboard", "Inventory", "Batches", "Movements", "ClinicProfile", "Settings", "TursoSync", "DesignSystem" })
            {
                Show(host, main, sub, $"10-config-{sub.ToLowerInvariant()}-{suffix}");
            }
        }
    }

    private void Show(System.Windows.Window host, MainViewModel main, string screen, string name)
    {
        main.NavigateTo(screen);
        Capture(host, name);
    }

    private void Capture(System.Windows.Window host, string name)
    {
        _bindingErrors.Screen = name;
        Pump();
        Layout(host, Width, Height);
        Pump();
        Layout(host, Width, Height);

        if (string.IsNullOrEmpty(_screenshotDir)) return;
        Directory.CreateDirectory(_screenshotDir);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width, (int)Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_screenshotDir, name + ".png"));
        encoder.Save(stream);
    }

    // ------------------------------------------------------------------ utilidades WPF

    /// <summary>Contenedor que imita la ventana: fondo y color de texto del tema.</summary>
    private static System.Windows.Controls.Border Host(System.Windows.UIElement content)
    {
        var host = new System.Windows.Controls.Border { Child = content };
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ThemeBackground");
        host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "ThemeText");
        return host;
    }

    private static void Layout(System.Windows.FrameworkElement element, double width, double height)
    {
        element.Measure(new System.Windows.Size(width, height));
        element.Arrange(new System.Windows.Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
        }
    }

    private static void WaitFor(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        var timeout = new System.Threading.Timer(_ => frame.Continue = false, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timeout.Dispose();
        if (task.IsFaulted) ExceptionDispatchInfo.Capture(task.Exception!.InnerException ?? task.Exception).Throw();
    }
}
