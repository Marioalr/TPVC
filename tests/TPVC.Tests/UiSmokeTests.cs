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

        // Citas para que el inicio de Pacientes y la agenda tengan contenido
        var seeded = WaitFor(inventory.GetPatientsAsync());
        foreach (var (patient, day, time, reason) in new[]
        {
            (seeded[0], DateTime.Today, "23:00", "Control de presión"),
            (seeded[1], DateTime.Today, "23:30", "Resultados de laboratorio"),
            (seeded[2], DateTime.Today.AddDays(1), "10:00", "Consulta de seguimiento")
        })
        {
            WaitFor(inventory.SaveAppointmentAsync(new PatientAppointment
            {
                PatientRecordId = patient.Id,
                PatientName = patient.FullName,
                PatientPhone = patient.Phone ?? string.Empty,
                Date = day,
                Time = time,
                Reason = reason
            }));
        }

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

        // El contenido de la ventana real, sin mostrarla: sigue siendo hijo lógico de la ventana
        // (le llegan los cambios de tema y hereda su color de texto) y se mide y dibuja aparte,
        // porque una ventana que no se muestra no se dibuja
        var window = new MainWindow(main) { Width = Width, Height = Height };
        var host = (System.Windows.FrameworkElement)window.Content;

        WaitFor(main.MedicalRecordsVM.InitialLoad);
        notifications.Success("Expediente guardado");

        foreach (var dark in new[] { false, true })
        {
            _themes.ApplyTheme(dark
                ? new ThemeSettings { ThemeMode = "Oscuro", BackgroundColor = "#0F172A", SurfaceColor = "#1E293B", TextColor = "#F8FAFC", SecondaryTextColor = "#94A3B8", BorderColor = "#334155" }
                : new ThemeSettings());
            var suffix = dark ? "oscuro" : "claro";

            Show(host, main, "MainHub", $"01-inicio-{suffix}");
            var pos = main.PosVM;
            Show(host, main, "Pos", $"02-pos-vacio-{suffix}");
            var stocked = pos.Products.Where(p => p.CurrentStock > 3 && p.Price > 0).ToList();
            pos.AddToCartWithQuantity(stocked[0], 2);
            pos.AddToCart(stocked[1]);
            pos.AddServiceOnlyToCart(pos.Services.First());
            pos.CashReceived = 1000m;
            Capture(host, $"02b-pos-cuenta-{suffix}");
            pos.SearchText = "3*para";
            Capture(host, $"02c-pos-busqueda-{suffix}");
            pos.SearchText = string.Empty;

            pos.OpenCatalogModal();
            Capture(host, $"02d-pos-catalogo-{suffix}");
            pos.CloseCatalogModal();

            WaitFor(pos.OpenServicesModalAsync());
            Capture(host, $"02e-pos-servicios-{suffix}");
            pos.OpenCreateServiceModal();
            Capture(host, $"02f-pos-nuevo-servicio-{suffix}");
            pos.CloseCreateServiceModal();
            pos.CloseServicesModal();

            WaitFor(pos.ProcessSaleAsync());
            Capture(host, $"02g-pos-ticket-{suffix}");
            pos.CloseTicketModal();

            WaitFor(pos.OpenCashCutModalAsync());
            Capture(host, $"02h-pos-corte-{suffix}");
            pos.CloseCashCutModal();

            WaitFor(pos.OpenTicketHistoryModalAsync());
            Capture(host, $"02i-pos-tickets-{suffix}");
            pos.RequestCancelSale();
            Capture(host, $"02j-pos-cancelar-ticket-{suffix}");
            pos.CloseCancelConfirmModal();
            pos.CloseTicketHistoryModal();

            var records = main.MedicalRecordsVM;
            Show(host, main, "Records", $"03-pacientes-inicio-{suffix}");
            records.HomeSearchText = "mar";
            Capture(host, $"04-pacientes-busqueda-{suffix}");
            records.HomeSearchText = string.Empty;

            records.OpenPatientChart(records.Patients.FirstOrDefault(p => p.Consultations.Count > 0) ?? records.Patients.First());
            Capture(host, $"05-ficha-{suffix}");
            records.ToggleAntecedent(records.AntecedentSections.First());
            if (records.Timeline.Count > 0) records.ToggleTimelineEntry(records.Timeline[0]);
            records.IsVitalsChartVisible = true;
            Capture(host, $"06-ficha-detalle-{suffix}");
            records.IsVitalsChartVisible = false;

            WaitFor(records.ScheduleForSelectedPatientAsync());
            WaitFor(records.OpenAgendaSlotAsync(records.AgendaSlots.First(s => s.IsFree)));
            Capture(host, $"07-agenda-{suffix}");
            records.CloseAgendaPanel();

            records.ChartPrimaryAction();
            records.ConsultationChiefComplaint = "Fiebre y dolor de garganta de 2 días";
            records.ConsultationTA = "130/85";
            records.ConsultationDiagnosis = "J00 Rinofaringitis aguda";
            Capture(host, $"08-consulta-{suffix}");

            // El POS con una consulta en curso (se puede abrir o pasarle la venta)
            Show(host, main, "Pos", $"08b-pos-con-consulta-{suffix}");
            main.NavigateTo("Records");

            records.ConsultationSection = 5;
            Capture(host, $"09-consulta-tratamiento-{suffix}");

            records.ShowChartFromConsultation();
            Capture(host, $"10-ficha-con-consulta-{suffix}");
            records.ShowConsultation();

            WaitFor(records.OpenPrescriptionModalAsync());
            Capture(host, $"11-receta-{suffix}");

            // El paciente de prueba es alérgico a la penicilina: la amoxicilina debe avisar
            records.PrescriptionMedicineName = "Amoxicilina / Ácido Clavulánico 500/125 mg";
            records.PrescriptionDosage = "1 tableta cada 12 horas por 7 días";
            WaitFor(records.AddPrescriptionItemToPatientAsync());
            if (records.IsAllergyAlertOpen)
            {
                Capture(host, $"11b-alerta-alergia-{suffix}");
                records.CancelAllergyPrescription();
            }
            WaitFor(records.RepeatPreviousPrescriptionAsync());
            records.DismissInfoModal();
            Capture(host, $"11c-receta-con-medicamentos-{suffix}");
            records.OpenPrescriptionPdfPreview();
            Capture(host, $"11d-vista-previa-receta-{suffix}");
            records.ClosePrescriptionPdfPreview();
            records.DismissInfoModal();
            records.ClosePrescriptionModal();

            WaitFor(records.OpenSuppliesModalAsync());
            Capture(host, $"12-insumos-{suffix}");
            records.CloseSuppliesModal();

            WaitFor(records.OpenServicesModalAsync());
            Capture(host, $"13-servicios-{suffix}");
            records.CloseServicesModal();

            WaitFor(records.OpenPrescriptionModalAsync());
            WaitFor(records.OpenPrescriptionCatalogAsync());
            Capture(host, $"14-catalogo-{suffix}");
            records.CloseCatalogBrowser();
            records.ClosePrescriptionModal();

            WaitFor(records.FinalizeConsultationAsync());
            Capture(host, $"15-terminar-consulta-{suffix}");
            records.ClosePosModal();
            WaitFor(records.DiscardActiveConsultationAsync());

            WaitFor(records.NewPatientAsync());
            Capture(host, $"16-alta-paciente-{suffix}");
            records.FormGender = "Femenino";
            records.FormBirthDateText = DateTime.Today.AddYears(-32).ToString("dd/MM/yyyy");
            records.OpenFormSection("Gineco");
            Capture(host, $"17-alta-gineco-{suffix}");
            records.CancelPatientForm();

            var shell = main.ConfigurationShellVM;
            foreach (var sub in new[] { "Dashboard", "Inventory", "Batches", "Movements", "Services", "ClinicProfile", "Appearance", "Data", "TursoSync", "Updates", "DesignSystem" })
            {
                Show(host, main, sub, $"20-config-{sub.ToLowerInvariant()}-{suffix}");
            }

            shell.SettingsSearchText = "respaldo";
            Capture(host, $"21-config-buscar-{suffix}");
            shell.SettingsSearchText = string.Empty;

            main.NavigateTo("Inventory");
            WaitFor(shell.InventoryVM.LoadDataAsync());
            WaitFor(shell.InventoryVM.NewProductAsync());
            Capture(host, $"22-config-producto-nuevo-{suffix}");
            shell.InventoryVM.CloseEditor();

            main.NavigateTo("Batches");
            shell.BatchesVM.OpenNewBatchModal();
            shell.BatchesVM.NewBatchExpirationText = "12/2027";
            Capture(host, $"23-config-lote-nuevo-{suffix}");
            shell.BatchesVM.CloseNewBatchModal();

            main.NavigateTo("Services");
            WaitFor(shell.ServicesVM.LoadAsync());
            WaitFor(shell.ServicesVM.EditServiceAsync(shell.ServicesVM.FilteredServices.First()));
            Capture(host, $"24-config-servicio-{suffix}");
            shell.ServicesVM.CloseEditor();

            main.NavigateTo("Data");
            WaitFor(shell.DataVM.BackupNowAsync());
            shell.DataVM.RestoreBackup(shell.DataVM.Backups[0]);
            Capture(host, $"25-config-restaurar-{suffix}");
            shell.DataVM.CloseImportSheet();
        }
    }

    private void Show(System.Windows.FrameworkElement host, MainViewModel main, string screen, string name)
    {
        main.NavigateTo(screen);
        Capture(host, name);
    }

    private void Capture(System.Windows.FrameworkElement host, string name)
    {
        _bindingErrors.Screen = name;
        Pump();
        Layout(host, Width, Height);
        Pump();
        Layout(host, Width, Height);

        if (string.IsNullOrEmpty(_screenshotDir)) return;
        Directory.CreateDirectory(_screenshotDir);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width, (int)Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle((System.Windows.Media.Brush)_app.Resources["ThemeBackground"], null, new System.Windows.Rect(0, 0, Width, Height));
        }
        bitmap.Render(background);
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

    private static T WaitFor<T>(Task<T> task)
    {
        WaitFor((Task)task);
        return task.Result;
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
