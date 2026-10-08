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
    private FakeHranaServer? _cloud;

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
            ReportContrastIssues();
        }
        finally
        {
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(_bindingErrors);
            _cloud?.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }

        var problems = _failures.Concat(_bindingErrors.Errors.Distinct()).ToList();
        if (problems.Count > 0)
        {
            Assert.Fail($"La interfaz reportó {problems.Count} problema(s):\n" + string.Join("\n", problems.Take(200)));
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

        // Nube simulada: "Enviar a…" con equipos enlazados y el aviso de una consulta recibida
        _cloud = new FakeHranaServer(Path.Combine(_workDir, "nube.db"));
        var ts = tursoSettings.CurrentSettings;
        ts.IsSyncEnabled = true;
        ts.AutoSyncOnChanges = false;
        ts.AutoSyncOnStartup = false;
        ts.DatabaseUrl = "https://tpvc-smoke.turso.io";
        ts.AuthToken = "smoke";
        ts.DeviceId = "device-pc";
        ts.DeviceName = "PC del consultorio";
        tursoSettings.SaveSettings(ts);
        tursoSync.ClientFactory = (url, token) => new TursoClient(url, token, new System.Net.Http.HttpClient(_cloud, disposeHandler: false));
        var handoff = new ConsultationHandoffService(tursoSync, tursoSettings) { AutoPoll = false };
        WaitFor(tursoSync.SyncAsync());
        ExecuteCloud("INSERT INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc) VALUES " +
                     "('device-tablet', 'Tablet del consultorio', 'AndroidTablet', '1.17.0', strftime('%Y-%m-%d %H:%M:%f','now','-4 seconds')), " +
                     "('device-recepcion', 'PC de recepción', 'WindowsDesktop', '1.15.0', strftime('%Y-%m-%d %H:%M:%f','now','-1 day'))");
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
            notificationService: notifications,
            consultationHandoffService: handoff);
        main.IsFirstRunWizardOpen = false;

        // El contenido de la ventana real, sin mostrarla: sigue siendo hijo lógico de la ventana
        // (le llegan los cambios de tema y hereda su color de texto) y se mide y dibuja aparte,
        // porque una ventana que no se muestra no se dibuja
        var window = new MainWindow(main) { Width = Width, Height = Height };
        var host = (System.Windows.FrameworkElement)window.Content;

        WaitFor(main.MedicalRecordsVM.InitialLoad);
        notifications.Success("Expediente guardado");

        // Fase 5: cada pantalla en claro, oscuro, un acento distinto al azul y colores personalizados
        var themes = new (string Suffix, ThemeSettings Settings)[]
        {
            ("claro", new ThemeSettings()),
            ("oscuro", new ThemeSettings { ThemeMode = "Oscuro", BackgroundColor = "#0F172A", SurfaceColor = "#1E293B", TextColor = "#F8FAFC", SecondaryTextColor = "#94A3B8", BorderColor = "#334155" }),
            ("ambar", new ThemeSettings { AccentColor = "#D97706" }),
            ("personalizado", new ThemeSettings { ThemeMode = "Personalizado", BackgroundColor = "#FFF7ED", SurfaceColor = "#FFFFFF", AccentColor = "#0D9488" })
        };
        foreach (var (suffix, settings) in themes)
        {
            _themes.ApplyTheme(settings);

            Show(host, main, "MainHub", $"01-inicio-{suffix}");

            // Ventanas sobre el inicio: lista de compras sugeridas, paleta de comandos y asistente inicial
            WaitFor(main.MainHubVM.OpenSuggestedPurchasesAsync());
            Capture(host, $"01b-compras-sugeridas-{suffix}");
            main.MainHubVM.IsSuggestedPurchasesOpen = false;
            main.OpenCommandPalette();
            main.PaletteSearchText = "con";
            Capture(host, $"01c-paleta-comandos-{suffix}");
            main.CloseCommandPalette();
            main.IsFirstRunWizardOpen = true;
            main.WizardErrorMessage = "Capture al menos el nombre del médico o del consultorio.";
            Capture(host, $"01d-asistente-inicial-{suffix}");
            main.WizardErrorMessage = null;
            main.IsFirstRunWizardOpen = false;
            var pos = main.PosVM;
            Show(host, main, "Pos", $"02-pos-vacio-{suffix}");
            var stocked = pos.Products.Where(p => p.CurrentStock > 3 && p.Price > 0).ToList();
            pos.AddToCartWithQuantity(stocked[0], 2);
            pos.AddToCart(stocked[1]);
            pos.AddServiceOnlyToCart(pos.Services.First());
            pos.Checkout.CashReceived = 1000m;
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

            // Enviar a otro equipo: la hoja con los equipos enlazados y la consulta en solo lectura
            WaitFor(records.OpenSendConsultationSheetAsync());
            Capture(host, $"08c-consulta-enviar-a-{suffix}");
            WaitFor(records.SendConsultationToSelectedDeviceAsync());
            Capture(host, $"08d-consulta-enviada-{suffix}");
            Show(host, main, "Pos", $"08e-pos-consulta-enviada-{suffix}");
            main.NavigateTo("Records");
            WaitFor(records.RecoverHandedOffConsultationAsync());

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
            WaitFor(records.SelectAndAddPrescriptionProductAsync(records.SelectedPrescriptionProduct));
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
            records.CloseFinishConsultation();
            WaitFor(records.DiscardActiveConsultationAsync());

            // Una consulta que la tablet mandó a esta PC: el aviso en Pacientes
            InsertIncomingHandoff(records.Patients.First(), suffix);
            WaitFor(records.PollHandoffsOnceAsync());
            Show(host, main, "Records", $"15b-pacientes-consulta-recibida-{suffix}");
            ExecuteCloud("UPDATE ConsultationHandoffs SET State = 'cancelada', Payload = '' WHERE FromDeviceId = 'device-tablet'");
            WaitFor(records.PollHandoffsOnceAsync());

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

    private void ExecuteCloud(string sql)
    {
        using var cmd = _cloud!.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void InsertIncomingHandoff(PatientRecord patient, string suffix)
    {
        var payload = new ConsultationDraft
        {
            PatientSyncId = patient.SyncId,
            PatientName = patient.FullName,
            Folio = $"CNS-SMOKE-{suffix}",
            StartedAt = DateTime.Today.AddHours(10).AddMinutes(32),
            ChiefComplaint = "Tos y fiebre",
            Diagnosis = "J00 Rinofaringitis aguda",
            Items = { new ConsultationProductItem { Code = "MED-001", Name = "Paracetamol 500 mg", UnitPrice = 45m, Quantity = 2, LineKind = "libre" } }
        };
        using var cmd = _cloud!.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO ConsultationHandoffs (Id, FromDeviceId, FromDeviceName, ToDeviceId, ToDeviceName, PatientSyncId, PatientName, Folio, " +
                          "Payload, PayloadVersion, State, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, 'device-tablet', 'Tablet del consultorio', 'device-pc', " +
                          "'PC del consultorio', $patient, $name, $folio, $payload, 1, 'enviada', strftime('%Y-%m-%d %H:%M:%f','now'), strftime('%Y-%m-%d %H:%M:%f','now'))";
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$patient", TursoSyncService.FormatGuid(patient.SyncId));
        cmd.Parameters.AddWithValue("$name", patient.FullName);
        cmd.Parameters.AddWithValue("$folio", payload.Folio);
        cmd.Parameters.AddWithValue("$payload", ConsultationDraftStore.Serialize(payload));
        cmd.ExecuteNonQuery();
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

        CheckContrast(host, name);

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

    // ------------------------------------------------------------------ contraste real de cada texto

    private readonly Dictionary<string, (string Description, List<string> Screens)> _contrastIssues = new();

    /// <summary>
    /// Cada texto visible contra el fondo que realmente tiene detrás (el primer fondo opaco de sus
    /// contenedores, mezclando los semitransparentes): 4.5:1, o 3:1 en títulos grandes (WCAG 2.1).
    /// Los iconos, como gráficos que dan información: 3:1 (WCAG 1.4.11).
    /// Lo deshabilitado o atenuado a propósito no cuenta.
    /// </summary>
    private void CheckContrast(System.Windows.DependencyObject root, string screen)
    {
        if (root is System.Windows.UIElement element)
        {
            if (element.Visibility != System.Windows.Visibility.Visible || !element.IsEnabled || element.Opacity < 0.99) return;
        }

        switch (root)
        {
            case System.Windows.Controls.TextBlock text:
                CheckTextBlock(text, screen);
                break;
            case System.Windows.Controls.TextBox box when !string.IsNullOrWhiteSpace(box.Text) && box.Foreground is System.Windows.Media.SolidColorBrush boxBrush:
                Report(screen, box, box.Text, boxBrush.Color, box.FontSize, box.FontWeight);
                break;
            case TPVC.Helpers.Icon icon when icon.ActualWidth >= 1 && icon.Data != null && icon.Foreground is System.Windows.Media.SolidColorBrush iconBrush:
                Report(screen, icon, IconLabel(icon), iconBrush.Color, 0, System.Windows.FontWeights.Normal, required: 3.0);
                break;
        }

        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            CheckContrast(System.Windows.Media.VisualTreeHelper.GetChild(root, i), screen);
        }
    }

    private void CheckTextBlock(System.Windows.Controls.TextBlock text, string screen)
    {
        if (text.ActualWidth < 1 || text.ActualHeight < 1) return;
        var runs = text.Inlines.OfType<System.Windows.Documents.Run>().Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToList();
        if (runs.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(text.Text) && text.Foreground is System.Windows.Media.SolidColorBrush brush)
            {
                Report(screen, text, text.Text, brush.Color, text.FontSize, text.FontWeight);
            }
            return;
        }
        foreach (var run in runs)
        {
            if (run.Foreground is System.Windows.Media.SolidColorBrush runBrush)
            {
                Report(screen, text, run.Text, runBrush.Color, run.FontSize, run.FontWeight);
            }
        }
    }

    private void Report(string screen, System.Windows.DependencyObject element, string content, System.Windows.Media.Color foreground,
        double fontSize, System.Windows.FontWeight weight, double? required = null)
    {
        var background = EffectiveBackground(element);
        if (background == null) return;
        var fg = Composite(foreground, background.Value);
        double ratio = ThemeColor.Contrast(ToTheme(fg), ToTheme(background.Value));
        bool large = fontSize >= 24 || (fontSize >= 18.66 && weight.ToOpenTypeWeight() >= 700);
        required ??= large ? 3.0 : 4.5;
        if (ratio >= required - 0.005) return;

        var snippet = content.Replace("\n", " ").Trim();
        if (snippet.Length > 40) snippet = snippet[..40] + "…";
        var key = $"{snippet}|{fg}|{background}";
        if (!_contrastIssues.TryGetValue(key, out var issue))
        {
            var size = fontSize > 0 ? $", {fontSize:0.#}px" : string.Empty;
            issue = ($"\"{snippet}\" {ToHex(fg)} sobre {ToHex(background.Value)} = {ratio:0.00}:1 (mínimo {required:0.#}:1{size})", new List<string>());
            _contrastIssues[key] = issue;
        }
        if (!issue.Screens.Contains(screen)) issue.Screens.Add(screen);
    }

    /// <summary>Nombre legible del icono para el informe: el del control que lo contiene, si lo tiene.</summary>
    private static string IconLabel(TPVC.Helpers.Icon icon)
    {
        for (System.Windows.DependencyObject? current = icon; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            var name = System.Windows.Automation.AutomationProperties.GetName(current);
            if (!string.IsNullOrWhiteSpace(name)) return $"icono de {name}";
            if (current is System.Windows.FrameworkElement { ToolTip: string tip } && !string.IsNullOrWhiteSpace(tip)) return $"icono de {tip}";
            if (current is System.Windows.Controls.Panel panel)
            {
                var label = panel.Children.OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                if (label != null) return $"icono junto a {label}";
            }
        }
        return "icono";
    }

    /// <summary>Fondo pintado detrás del elemento: mezcla los semitransparentes hasta uno opaco.</summary>
    private System.Windows.Media.Color? EffectiveBackground(System.Windows.DependencyObject element)
    {
        var layers = new List<System.Windows.Media.Color>();
        // Un campo de texto pinta su propio fondo; un TextBlock, el de sus contenedores
        var start = element is System.Windows.Controls.Control ? element : System.Windows.Media.VisualTreeHelper.GetParent(element);
        for (var current = start; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            var brush = current switch
            {
                System.Windows.Controls.Border border => border.Background,
                System.Windows.Controls.Panel panel => panel.Background,
                System.Windows.Controls.Control control when ReferenceEquals(control, element) => control.Background,
                _ => null
            };
            if (brush == null) continue;
            if (brush is not System.Windows.Media.SolidColorBrush solid) return null; // degradado o imagen: no se mide
            if (solid.Color.A == 0 || solid.Opacity <= 0) continue;
            var color = solid.Color;
            if (solid.Opacity < 1) color.A = (byte)(color.A * solid.Opacity);
            layers.Add(color);
            if (color.A == 255) break;
        }

        var result = ThemeColorOf("ThemeBackground");
        for (int i = layers.Count - 1; i >= 0; i--) result = Composite(layers[i], result);
        return result;
    }

    private static System.Windows.Media.Color Composite(System.Windows.Media.Color top, System.Windows.Media.Color bottom)
    {
        if (top.A == 255) return top;
        double a = top.A / 255.0;
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(top.R * a + bottom.R * (1 - a)),
            (byte)Math.Round(top.G * a + bottom.G * (1 - a)),
            (byte)Math.Round(top.B * a + bottom.B * (1 - a)));
    }

    private static ThemeColor ToTheme(System.Windows.Media.Color c) => ThemeColor.Rgb(c.R, c.G, c.B);
    private static string ToHex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void ReportContrastIssues()
    {
        if (_contrastIssues.Count == 0) return;
        var lines = _contrastIssues.Values
            .OrderBy(i => i.Screens[0], StringComparer.Ordinal)
            .Select(i => $"[contraste] {i.Description} en {string.Join(", ", i.Screens.Take(4))}{(i.Screens.Count > 4 ? $" y {i.Screens.Count - 4} más" : "")}")
            .ToList();
        if (!string.IsNullOrEmpty(_screenshotDir))
        {
            Directory.CreateDirectory(_screenshotDir);
            File.WriteAllLines(Path.Combine(_screenshotDir, "contraste.txt"), lines);
        }
        _failures.AddRange(lines);
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
