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
        SeedDoctorLetterhead(profile);
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
            records.Home.HomeSearchText = "mar";
            Capture(host, $"04-pacientes-busqueda-{suffix}");
            records.Home.HomeSearchText = string.Empty;

            records.OpenPatientChart(records.Patients.FirstOrDefault(p => p.Consultations.Count > 0) ?? records.Patients.First());
            Capture(host, $"05-ficha-{suffix}");
            records.ToggleAntecedent(records.AntecedentSections.First());
            if (records.Timeline.Count > 0) records.ToggleTimelineEntry(records.Timeline[0]);
            records.IsVitalsChartVisible = true;
            Capture(host, $"06-ficha-detalle-{suffix}");
            records.IsVitalsChartVisible = false;

            WaitFor(records.Agenda.ScheduleForSelectedPatientAsync());
            WaitFor(records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree)));
            Capture(host, $"07-agenda-{suffix}");
            records.Agenda.CloseAgendaPanel();

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
            records.Info.DismissInfoModal();
            Capture(host, $"11c-receta-con-medicamentos-{suffix}");
            records.OpenPrescriptionPdfPreview();
            Capture(host, $"11d-vista-previa-receta-{suffix}");
            records.ClosePrescriptionPdfPreview();
            // La hoja impresa es papel: no depende del tema (se revisa una vez)
            if (suffix == "claro") CheckPrintedPrescription(records.Session.SelectedPatient!, profile.CurrentProfile, records.PrescriptionIndications);
            records.Info.DismissInfoModal();
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
            records.PatientForm.FormGender = "Femenino";
            records.PatientForm.FormBirthDateText = DateTime.Today.AddYears(-32).ToString("dd/MM/yyyy");
            records.PatientForm.OpenFormSection("Gineco");
            Capture(host, $"17-alta-gineco-{suffix}");
            records.PatientForm.CancelPatientForm();

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

            // Fase 7 (7.6): confirmaciones y errores con la ventana de la app (antes, MessageBox blanco en oscuro)
            CaptureDialog(TPVC.Views.ConfirmDialogKind.Confirm, "Descartar consulta",
                "¿Descartar la consulta en curso?\n\nNo se guardará la nota médica ni se cobrará nada de la cuenta.",
                $"26-confirmacion-{suffix}");
            CaptureDialog(TPVC.Views.ConfirmDialogKind.Error, "TPVC",
                "No se pudo imprimir la receta médica: la impresora no respondió.",
                $"26b-error-{suffix}");
        }

        // Después de todas las capturas (cambia datos y muestra la ventana): menús y teclado reales
        new UiKeyboardPass(window, main, _failures, _bindingErrors, Path.Combine(_workDir, "consulta.json")).Run();
    }

    // ------------------------------------------------------------------ receta impresa (fase 7.4)

    /// <summary>El médico y su membrete como quedan al importar su Excel (escudo y trazo de la hoja RECETA).</summary>
    private static void SeedDoctorLetterhead(ClinicProfileService profile)
    {
        profile.SaveProfile(new ClinicProfile
        {
            DoctorName = "Dra. María del Cielo Alberto Galicia",
            University = "Benemérita Universidad Autónoma de Puebla",
            ProfessionalLicense = "15632243",
            Phone = "2481592448",
            Address = "Calle Niños Héroes #503, Sección Segunda, Yehualtepec",
            ClinicName = "Consultorio Médico"
        });
        var receta = XlsxReader.Read(Path.Combine(AppContext.BaseDirectory, "TestAssets", "catalogo_muestra.xlsx")).Single(s => s.Name == "RECETA");
        var (crest, header) = DataImportService.FindLetterheadImages(receta);
        profile.SaveLetterheadImage(LetterheadImageKind.Crest, crest!.GetDisplayedContent());
        profile.SaveLetterheadImage(LetterheadImageKind.Header, header!.GetDisplayedContent());
    }

    /// <summary>
    /// La receta impresa (el FlowDocument que va a la impresora): media carta horizontal; con los
    /// medicamentos de la hoja del Excel cabe en una hoja con el pie abajo; con muchos pasa a otras hojas
    /// sin cortar nada. Guarda la imagen de las hojas junto a las demás capturas.
    /// </summary>
    private void CheckPrintedPrescription(PatientRecord patient, ClinicProfile clinic, string indications)
    {
        _bindingErrors.Screen = "receta-impresa";

        var consultation = Paginate(PrescriptionSheet.Create(patient, patient.ActivePrescription, clinic, indications, DateTime.Now));
        SavePage(consultation.Pages[0], "11e-receta-impresa");
        if (consultation.Pages[0].Size != new System.Windows.Size(816, 528))
        {
            _failures.Add($"[receta impresa] la hoja mide {consultation.Pages[0].Size} (media carta horizontal: 816x528)");
        }

        var excelItems = new[]
        {
            new PatientPrescriptionItem { MedicineName = "Espabion suspensión", Dosage = "Dar 5 ml vía oral cada 8 hrs por 5 días" },
            new PatientPrescriptionItem { MedicineName = "Adinol (Paracetamol) Jarabe 3.2 g/100 ml", Dosage = "Dar 8 ml vía oral cada 8 hrs por 5 días" }
        };
        var excel = Paginate(PrescriptionSheet.Create(patient, excelItems, clinic, indications, DateTime.Now));
        if (excel.Pages.Count != 1)
        {
            _failures.Add($"[receta impresa] dos medicamentos ocupan {excel.Pages.Count} hojas (el pie no debe pasar a otra hoja)");
        }
        if (!excel.Pages[0].ContainsImage(TPVC.Helpers.PrescriptionImages.WhatsApp))
        {
            _failures.Add("[receta impresa] el pie con el teléfono no está en la hoja");
        }

        var many = Enumerable.Range(1, 24)
            .Select(i => new PatientPrescriptionItem { MedicineName = $"Medicamento de prueba {i}", Dosage = "Tomar 1 tableta vía oral cada 8 hrs por 5 días" })
            .ToList();
        var flowing = Paginate(PrescriptionSheet.Create(patient, many, clinic, indications, DateTime.Now));
        SavePage(flowing.Pages[^1], "11f-receta-impresa-ultima-hoja");
        if (flowing.Pages.Count < 2)
        {
            _failures.Add($"[receta impresa] 24 medicamentos caben en {flowing.Pages.Count} hoja: deberían pasar a otra");
        }
        if (!flowing.Pages[^1].ContainsImage(TPVC.Helpers.PrescriptionImages.WhatsApp))
        {
            _failures.Add("[receta impresa] con varias hojas, el pie no quedó en la última");
        }
        // Nada se corta: el texto de las hojas (si WPF lo expone) trae el último medicamento
        var text = string.Concat(flowing.Pages.Select(p => p.Text));
        if (text.Length > 0 && !text.Contains("Medicamentodeprueba24", StringComparison.Ordinal))
        {
            _failures.Add("[receta impresa] el último de 24 medicamentos no aparece en ninguna hoja (se cortó)");
        }
    }

    private sealed record PrintedPage(System.Windows.Documents.DocumentPage Page)
    {
        public System.Windows.Size Size => Page.Size;

        /// <summary>Texto de la hoja sin espacios (de los GlyphRun que dibuja WPF).</summary>
        public string Text
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                Walk(Page.Visual, v =>
                {
                    if (System.Windows.Media.VisualTreeHelper.GetDrawing(v) is { } drawing) Collect(drawing, sb);
                });
                return new string(sb.ToString().Where(c => !char.IsWhiteSpace(c)).ToArray());
            }
        }

        public bool ContainsImage(System.Windows.Media.ImageSource? source)
        {
            bool found = false;
            Walk(Page.Visual, v => found |= v is System.Windows.Controls.Image image && ReferenceEquals(image.Source, source));
            return found;
        }

        private static void Walk(System.Windows.Media.Visual visual, Action<System.Windows.Media.Visual> action)
        {
            action(visual);
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(visual); i++)
            {
                if (System.Windows.Media.VisualTreeHelper.GetChild(visual, i) is System.Windows.Media.Visual child) Walk(child, action);
            }
        }

        private static void Collect(System.Windows.Media.Drawing drawing, System.Text.StringBuilder sb)
        {
            switch (drawing)
            {
                case System.Windows.Media.DrawingGroup group:
                    foreach (var child in group.Children) Collect(child, sb);
                    break;
                case System.Windows.Media.GlyphRunDrawing glyphs when glyphs.GlyphRun?.Characters != null:
                    sb.Append(glyphs.GlyphRun.Characters.ToArray());
                    break;
            }
        }
    }

    private sealed record PrintedDocument(List<PrintedPage> Pages);

    private static PrintedDocument Paginate(PrescriptionSheet sheet)
    {
        var doc = PrintingService.BuildPrescriptionDocument(sheet, PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight);
        var paginator = ((System.Windows.Documents.IDocumentPaginatorSource)doc).DocumentPaginator;
        paginator.PageSize = new System.Windows.Size(PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight);
        paginator.ComputePageCount();
        return new PrintedDocument(Enumerable.Range(0, paginator.PageCount).Select(i => new PrintedPage(paginator.GetPage(i))).ToList());
    }

    /// <summary>La hoja impresa a 192 ppp sobre papel blanco, como las demás capturas.</summary>
    private void SavePage(PrintedPage page, string name)
    {
        if (string.IsNullOrEmpty(_screenshotDir)) return;
        Directory.CreateDirectory(_screenshotDir);
        var size = page.Size;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        var paper = new System.Windows.Media.DrawingVisual();
        using (var dc = paper.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(size));
        }
        bitmap.Render(paper);
        bitmap.Render(page.Page.Visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_screenshotDir, name + ".png"));
        encoder.Save(stream);
    }

    private void ExecuteCloud(string sql)
    {
        using var held = _cloud!.Lock();
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
        using var held = _cloud!.Lock();
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

    /// <summary>
    /// La ventana de confirmación o de error de DialogService, sin mostrarla: su contenido (la tarjeta) se
    /// mide y se dibuja como el de la ventana principal, sigue siendo hijo lógico de su ventana y se revisa
    /// el contraste de cada texto e icono con los colores del tema actual.
    /// </summary>
    private void CaptureDialog(TPVC.Views.ConfirmDialogKind kind, string title, string message, string name)
    {
        var dialog = new TPVC.Views.ConfirmDialog(message, title, kind);
        try
        {
            Capture((System.Windows.FrameworkElement)dialog.Content, name);
        }
        finally
        {
            dialog.Close();
        }
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

/// <summary>
/// Fase 6 (paso 1d): menús contextuales y teclado con eventos reales. La ventana se muestra fuera de la
/// pantalla (sin activarla) para que haya PresentationSource, Loaded e IsVisible como en la aplicación:
/// MedicalRecordsView solo atiende Esc y "escribir para buscar" cuando está cargada y visible. Se
/// levantan PreviewKeyDown/KeyDown y PreviewTextInput de verdad (no se llama al ViewModel) y se revisa
/// el resultado, así un manejador del código detrás que deje de hacer algo se nota aquí.
/// </summary>
internal sealed class UiKeyboardPass
{
    private const string Screen = "menus-teclado";

    private readonly MainWindow _window;
    private readonly MainViewModel _main;
    private readonly List<string> _failures;
    private readonly BindingErrorCollector _bindingErrors;
    private readonly string _draftPath;
    private MedicalRecordsViewModel _records = null!;
    private System.Windows.PresentationSource? _source;

    public UiKeyboardPass(MainWindow window, MainViewModel main, List<string> failures, BindingErrorCollector bindingErrors, string draftPath)
    {
        _window = window;
        _main = main;
        _failures = failures;
        _bindingErrors = bindingErrors;
        _draftPath = draftPath;
    }

    private void Fail(string message) => _failures.Add($"[{Screen}] {message}");

    private bool Check(bool condition, string message)
    {
        if (!condition) Fail(message);
        return condition;
    }

    public void Run()
    {
        _bindingErrors.Screen = Screen;
        _records = _main.MedicalRecordsVM;
        _records.AutosaveDelay = TimeSpan.FromHours(1); // el borrador solo se escribe con Ctrl+S
        _main.NavigateTo("Records");
        _records.Info.DismissInfoModal();
        _records.Agenda.CloseAgendaPanel();
        _records.Navigation.GoToPatientsHome();

        _window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        _window.Left = -32000;
        _window.Top = -32000;
        _window.ShowActivated = false;
        _window.ShowInTaskbar = false;
        _window.Show();
        try
        {
            _source = System.Windows.PresentationSource.FromVisual(_window);
            var shellView = PumpUntilFound<TPVC.Views.MedicalRecordsView>(v => v.IsLoaded && v.IsVisible);
            if (_source == null || shellView == null)
            {
                Fail("La ventana no quedó cargada y visible: sin eso Esc y escribir para buscar no se pueden probar");
                return;
            }

            var patient = _records.Patients.First(p => p.RecordNumber == "EXP-2026-002");
            TypeToSearch(patient);
            ClearSearchButton();
            BirthDateMask();
            ChartMenu(patient);
            AgendaSlotMenu(patient);
            ConsultationKeys(patient);
            ThemedConfirmations(patient);
        }
        catch (Exception ex)
        {
            Fail($"Se interrumpió: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            System.Windows.Input.Keyboard.ClearFocus();
            _window.Close();
            Pump();
        }
    }

    // ------------------------------------------------------------------ escribir para buscar y Esc en inicio y ficha

    private void TypeToSearch(PatientRecord patient)
    {
        _records.Navigation.GoToPatientsHome();
        _records.Home.HomeSearchText = string.Empty;
        Settle();
        Check(TypeText("r") && _records.Home.HomeSearchText == "r", $"Inicio: escribir con el foco fuera de un campo va al buscador (quedó \"{_records.Home.HomeSearchText}\")");
        Check(PressEscape() && _records.Home.HomeSearchText.Length == 0 && _records.Navigation.IsHomePage, "Esc en el inicio borra la búsqueda");

        _records.OpenPatientChart(patient);
        Settle();
        Check(TypeText("m") && _records.Home.HomeSearchText == "m" && _records.Navigation.IsHomePage, "Ficha: escribir abre la búsqueda en el inicio");
        Check(PressEscape() && _records.Home.HomeSearchText.Length == 0, "Esc borra la búsqueda abierta desde la ficha");

        _records.OpenPatientChart(patient);
        Settle();
        Check(PressEscape() && _records.Navigation.IsHomePage, "Esc en la ficha vuelve al inicio");
    }

    // ------------------------------------------------------------------ fase 7: la X del buscador (7.5) y la fecha con diagonales (7.1)

    private void ClearSearchButton()
    {
        _records.Navigation.GoToPatientsHome();
        _records.Home.HomeSearchText = string.Empty;
        Settle();
        var shell = PumpUntilFound<TPVC.Views.MedicalRecordsView>(v => v.IsVisible);
        var searchBox = shell?.FindName("PatientSearchBox") as System.Windows.Controls.TextBox;
        var clear = shell == null ? null : FindDescendant<System.Windows.Controls.Button>(shell,
            b => System.Windows.Automation.AutomationProperties.GetName(b) == "Borrar la búsqueda");
        if (!Check(searchBox != null && clear != null, "Buscador: no se encontró el cuadro o su X (\"Borrar la búsqueda\")")) return;
        Check(!clear!.IsVisible, "Buscador: sin texto la X no debe verse");

        _records.Home.HomeSearchText = "gar";
        Settle();
        if (!Check(clear.IsVisible, "Buscador: con texto la X debe verse")) return;

        // Sin foco en ningún lado (ni lógico): la X debe dejar el cursor en el buscador
        var scope = System.Windows.Input.FocusManager.GetFocusScope(searchBox!);
        System.Windows.Input.Keyboard.ClearFocus();
        System.Windows.Input.FocusManager.SetFocusedElement(scope, null);
        Invoke(clear);
        Check(_records.Home.HomeSearchText.Length == 0 && _records.Navigation.IsHomePage, $"Un clic en la X borra la búsqueda (quedó \"{_records.Home.HomeSearchText}\")");
        Check(PumpUntil(() => searchBox!.IsKeyboardFocused || ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(scope), searchBox), 2000),
            "La X deja el cursor en el buscador");
        Settle();
        Check(!clear.IsVisible, "Buscador: tras borrar la X se oculta");
    }

    private void BirthDateMask()
    {
        WaitFor(_records.PatientForm.NewPatientAsync());
        _records.PatientForm.OpenFormSection("Identificacion");
        Settle();
        try
        {
            var form = PumpUntilFound<TPVC.Views.PatientFormView>(v => v.IsVisible);
            var box = form == null ? null : PumpUntilFound<System.Windows.Controls.TextBox>(form, b => TPVC.Helpers.DateMask.GetIsEnabled(b) && b.IsVisible);
            if (!Check(box != null, "Alta: no se encontró la fecha de nacimiento con las diagonales solas (DateMask)")) return;
            box!.Focus();

            foreach (var digit in "08082008") TypeInto(box, digit.ToString());
            var patientForm = _records.PatientForm;
            Check(box.Text == "08/08/2008" && patientForm.FormBirthDateText == "08/08/2008",
                $"Alta: teclear 08082008 debe verse 08/08/2008 (se ve \"{box.Text}\", el ViewModel tiene \"{patientForm.FormBirthDateText}\")");
            Check(patientForm.FormBirthDate == new DateTime(2008, 8, 8), $"Alta: la fecha tecleada no quedó leída ({patientForm.FormBirthDate:dd/MM/yyyy})");
            Check(TypeInto(box, "a") && box.Text == "08/08/2008", $"Alta: una letra no entra en la fecha (se ve \"{box.Text}\")");

            // Retroceso desde el final: las diagonales se van solas con el dígito anterior
            for (int i = 0; i < 5; i++) PressKey(box, System.Windows.Input.Key.Back);
            Check(box.Text == "08/0" && patientForm.FormBirthDateText == "08/0" && patientForm.FormBirthDate == null,
                $"Alta: cinco retrocesos desde 08/08/2008 dejan \"08/0\" (se ve \"{box.Text}\")");
        }
        finally
        {
            _records.PatientForm.CancelPatientForm();
            Settle();
        }
    }

    // ------------------------------------------------------------------ menús contextuales

    private void ChartMenu(PatientRecord patient)
    {
        _records.OpenPatientChart(patient);
        Settle();
        var chart = PumpUntilFound<TPVC.Views.PatientChartView>(v => v.IsVisible);
        var button = chart == null ? null : FindDescendant<System.Windows.Controls.Button>(chart,
            b => TPVC.Helpers.MenuOnClick.GetIsEnabled(b) && b.DataContext is MedicalRecordsViewModel);
        if (!Check(button != null, "Ficha: no se encontró el botón ⋯ (Más acciones)")) return;
        OpenAndCloseMenu(button!, "Ficha ⋯", menu => menu.DataContext is MedicalRecordsViewModel, expectedItems: 3);
    }

    private void AgendaSlotMenu(PatientRecord patient)
    {
        _records.OpenPatientChart(patient);
        WaitFor(_records.Agenda.ScheduleForSelectedPatientAsync());
        var slot = _records.Agenda.AgendaSlots.FirstOrDefault(s => s.IsFree);
        if (!Check(slot != null, "Agenda: no hay huecos libres para agendar")) return;
        WaitFor(_records.Agenda.OpenAgendaSlotAsync(slot));
        _records.Agenda.SetAgendaReason("Control");
        WaitFor(_records.Agenda.SaveAgendaFormAsync());
        Settle();
        var booked = _records.Agenda.AgendaSlots.FirstOrDefault(s => s.HasAppointment && s.Appointment!.PatientRecordId == patient.Id);
        if (!Check(booked != null, $"Agenda: no se agendó la cita ({_records.Agenda.AgendaFormError})")) return;

        var agenda = PumpUntilFound<TPVC.Views.AgendaPanelView>(v => v.IsVisible);
        var button = agenda == null ? null : PumpUntilFound<System.Windows.Controls.Button>(agenda,
            b => TPVC.Helpers.MenuOnClick.GetIsEnabled(b) && ReferenceEquals(b.DataContext, booked));
        if (!Check(button != null, "Agenda: no se encontró el botón de acciones de la cita")) return;
        OpenAndCloseMenu(button!, "Agenda (cita)", menu => ReferenceEquals(menu.DataContext, booked), expectedItems: 6,
            item => Check(item.CommandParameter is PatientAppointment, $"Agenda: \"{item.Header}\" no recibe la cita"));

        // Esc con la cita en cambio de horario y luego con el panel
        _records.Agenda.StartMovingAppointment(booked!.Appointment);
        Settle();
        Check(PressEscape() && !_records.Agenda.IsMovingAppointment && _records.Agenda.IsAgendaPanelOpen, "Esc cancela el cambio de horario (el panel sigue abierto)");
        Check(PressEscape() && !_records.Agenda.IsAgendaPanelOpen, "Esc cierra el panel de agenda");
    }

    private void OpenAndCloseMenu(System.Windows.Controls.Button button, string what, Func<System.Windows.Controls.ContextMenu, bool> dataContextOk,
        int expectedItems, Action<System.Windows.Controls.MenuItem>? checkItem = null)
    {
        // Clic real: MenuOnClick abre el menú con el DataContext del botón
        button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
        Pump();
        var menu = button.ContextMenu;
        if (!Check(menu is { IsOpen: true }, $"{what}: el menú no se abrió")) return;
        Check(dataContextOk(menu!), $"{what}: el menú no tiene el DataContext esperado ({menu!.DataContext?.GetType().Name ?? "null"})");
        var items = menu!.Items.OfType<System.Windows.Controls.MenuItem>().ToList();
        Check(items.Count == expectedItems, $"{what}: {items.Count} opciones en lugar de {expectedItems}");
        foreach (var item in items)
        {
            Check(item.Command != null, $"{what}: \"{item.Header}\" sin comando (binding sin resolver)");
            checkItem?.Invoke(item);
        }
        menu.IsOpen = false;
        Pump();
        Check(!menu.IsOpen, $"{what}: el menú no se cerró");
    }

    // ------------------------------------------------------------------ consulta: Ctrl, hojas y Esc por capas

    private void ConsultationKeys(PatientRecord patient)
    {
        if (_records.HasActiveConsultation) WaitFor(_records.DiscardActiveConsultationAsync());
        _records.OpenPatientChart(patient);
        _records.ChartPrimaryAction();
        if (!Check(_records.Navigation.IsConsultationPage, "No se abrió la consulta")) return;
        Settle();

        // Ctrl+↓ / Ctrl+↑ / Ctrl+S en la consulta
        var workspace = PumpUntilFound<TPVC.Views.ConsultationWorkspaceView>(v => v.IsVisible);
        var grid = workspace?.FindName("ConsultationWorkspace") as System.Windows.IInputElement;
        if (Check(grid != null, "Consulta: no se encontró el área de la consulta"))
        {
            _records.ConsultationSection = 2;
            Settle();
            WithControl(() =>
            {
                Check(PressKey(grid!, System.Windows.Input.Key.Down) && _records.ConsultationSection == 3, $"Ctrl+↓ pasa a la sección siguiente (quedó {_records.ConsultationSection})");
                Check(PressKey(grid!, System.Windows.Input.Key.Up) && _records.ConsultationSection == 2, $"Ctrl+↑ vuelve a la sección anterior (quedó {_records.ConsultationSection})");
                try { File.Delete(_draftPath); } catch { }
                _records.AutosaveStatus = string.Empty;
                Check(PressKey(grid!, System.Windows.Input.Key.S) && File.Exists(_draftPath), "Ctrl+S guarda la consulta en curso");
            });
            Pump();
            Check(_records.AutosaveStatus.StartsWith("Guardado", StringComparison.Ordinal), $"Ctrl+S muestra la hora del guardado (\"{_records.AutosaveStatus}\")");
        }

        // CIE-10 abierto, "Enviar a…" y "Terminar consulta": Esc los cierra uno por uno
        _records.ConsultationDiagnosis = "farin";
        Settle();
        Check(_records.IsCie10DropdownOpen, "Escribir el diagnóstico abre las sugerencias CIE-10");
        Check(PressEscape() && !_records.IsCie10DropdownOpen, "Esc cierra las sugerencias CIE-10");
        _records.IsSendConsultationSheetOpen = true;
        Settle();
        Check(PressEscape() && !_records.IsSendConsultationSheetOpen, "Esc cierra \"Enviar a…\"");
        WaitFor(_records.FinalizeConsultationAsync());
        Settle();
        Check(PressEscape() && !_records.IsFinishConsultationOpen && _records.HasActiveConsultation, "Esc en \"Terminar consulta\" vuelve a la consulta sin cobrar");

        PrescriptionSheet();
        SuppliesSheet();
        ServicesSheet();

        WaitFor(_records.DiscardActiveConsultationAsync());
        Settle();
    }

    private void PrescriptionSheet()
    {
        WaitFor(_records.OpenPrescriptionModalAsync());
        _records.PrescriptionSearchText = string.Empty;
        Settle();
        var sheet = PumpUntilFound<TPVC.Views.PrescriptionSheetView>(v => v.IsVisible);
        var box = sheet?.FindName("PrescriptionSearchTextBox") as System.Windows.Controls.TextBox;
        if (!Check(box != null, "Receta: no se encontró el buscador")) return;

        var suggestions = _records.PrescriptionProductSuggestions.ToList();
        var before = _records.SelectedPatient!.ActivePrescription.Count;
        if (Check(suggestions.Count > 1, "Receta: se esperaban al menos dos sugerencias"))
        {
            Check(PressKey(box!, System.Windows.Input.Key.Down) && ReferenceEquals(_records.SelectedPrescriptionProduct, suggestions[1]),
                "Receta: ↓ en el buscador elige la siguiente sugerencia");
            var chosen = suggestions[1];
            Check(PressKey(box!, System.Windows.Input.Key.Enter), "Receta: Enter en el buscador se atiende");
            Check(PumpUntil(() => _records.SelectedPatient!.ActivePrescription.Count == before + 1),
                "Receta: Enter agrega la sugerencia elegida");
            Check(_records.SelectedPatient!.ActivePrescription.LastOrDefault()?.MedicineName.StartsWith(chosen.Name, StringComparison.Ordinal) == true,
                $"Receta: se agregó otro medicamento ({_records.SelectedPatient!.ActivePrescription.LastOrDefault()?.MedicineName})");
        }

        // Capas sobre la receta: el aviso, la alerta de alergia, el catálogo y la vista previa
        _records.Info.InfoModalMessage = "Aviso de prueba";
        _records.Info.IsInfoModalVisible = true;
        Settle();
        Check(PressEscape() && !_records.Info.IsInfoModalVisible && _records.IsPrescriptionModalOpen, "Esc cierra primero el aviso (la receta sigue)");
        _records.IsAllergyAlertOpen = true;
        Settle();
        Check(PressEscape() && !_records.IsAllergyAlertOpen && _records.IsPrescriptionModalOpen, "Esc cierra la alerta de alergia");
        WaitFor(_records.OpenPrescriptionCatalogAsync());
        Settle();
        Check(PressEscape() && !_records.IsCatalogBrowserModalOpen && _records.IsPrescriptionModalOpen, "Esc cierra el catálogo (la receta sigue)");
        _records.OpenPrescriptionPdfPreview();
        Settle();
        Check(PressEscape() && !_records.IsPrescriptionPdfModalOpen, "Esc cierra la vista previa de la receta");
        Check(PressEscape() && !_records.IsPrescriptionModalOpen, "Esc cierra la receta");
    }

    private void SuppliesSheet()
    {
        WaitFor(_records.OpenSuppliesModalAsync());
        Settle();
        var sheet = PumpUntilFound<TPVC.Views.SuppliesSheetView>(v => v.IsVisible);
        var box = sheet?.FindName("SupplySearchTextBox") as System.Windows.Controls.TextBox;
        if (!Check(box != null, "Insumos: no se encontró el buscador")) return;

        var suggestions = _records.SupplyProductSuggestions.ToList();
        var before = _records.SelectedPatient!.ActiveSupplies.Count;
        if (Check(suggestions.Count > 1, "Insumos: se esperaban al menos dos sugerencias"))
        {
            Check(PressKey(box!, System.Windows.Input.Key.Down) && ReferenceEquals(_records.SelectedSupplyProduct, suggestions[1]),
                "Insumos: ↓ en el buscador elige la siguiente sugerencia");
            Check(PressKey(box!, System.Windows.Input.Key.Enter), "Insumos: Enter en el buscador se atiende");
            Check(PumpUntil(() => _records.SelectedPatient!.ActiveSupplies.Count == before + 1), "Insumos: Enter agrega la sugerencia elegida");
        }

        // Enter en el buscador del catálogo agrega el primero con existencias
        WaitFor(_records.OpenSupplyCatalogAsync());
        _records.CatalogBrowserSearchText = "gasa";
        Settle();
        var catalog = PumpUntilFound<TPVC.Views.CatalogBrowserSheetView>(v => v.IsVisible);
        var catalogBox = catalog?.FindName("CatalogBrowserSearchTextBox") as System.Windows.Controls.TextBox;
        if (Check(catalogBox != null, "Catálogo: no se encontró el buscador"))
        {
            var count = _records.SelectedPatient!.ActiveSupplies.Count;
            PressKey(catalogBox!, System.Windows.Input.Key.Enter);
            Check(PumpUntil(() => !_records.IsCatalogBrowserModalOpen && _records.SelectedPatient!.ActiveSupplies.Count == count + 1),
                "Catálogo: Enter en la búsqueda agrega el primero y cierra el catálogo");
        }
        Settle();
        Check(PressEscape() && !_records.IsSuppliesModalOpen, "Esc cierra los insumos");
    }

    private void ServicesSheet()
    {
        WaitFor(_records.OpenServicesModalAsync());
        Settle();
        var sheet = PumpUntilFound<TPVC.Views.ServicesSheetView>(v => v.IsVisible);
        var box = sheet?.FindName("ServiceModalSearchTextBox") as System.Windows.Controls.TextBox;
        if (!Check(box != null, "Servicios: no se encontró el buscador")) return;

        var suggestions = _records.ServiceModalSuggestions.ToList();
        var before = _records.SelectedPatient!.ActiveServices.Count;
        if (Check(suggestions.Count > 1, "Servicios: se esperaban al menos dos sugerencias"))
        {
            Check(PressKey(box!, System.Windows.Input.Key.Down) && ReferenceEquals(_records.SelectedServiceModalItem, suggestions[1]),
                "Servicios: ↓ en el buscador elige la siguiente sugerencia");
            Check(PressKey(box!, System.Windows.Input.Key.Enter), "Servicios: Enter en el buscador se atiende");
            Check(PumpUntil(() => _records.SelectedPatient!.ActiveServices.Count == before + 1), "Servicios: Enter agrega la sugerencia elegida");
        }
        Settle();
        Check(PressEscape() && !_records.IsServicesModalOpen, "Esc cierra los servicios");
    }

    // ------------------------------------------------------------------ confirmaciones con la ventana de la app (Fase 7, 7.6)

    /// <summary>
    /// DialogService con la ventana de la app sobre la principal (cargada y visible), contestada sin
    /// mostrarla: con el botón que tocan Esc y Enter (lo seguro, con el foco al abrir) y con "Sí". Lo
    /// seguro sigue siendo "No": DialogService regresa false y "Descartar consulta" conserva la consulta.
    /// </summary>
    private void ThemedConfirmations(PatientRecord patient)
    {
        if (_records.HasActiveConsultation) WaitFor(_records.DiscardActiveConsultationAsync());
        _records.OpenPatientChart(patient);
        _records.ChartPrimaryAction();
        if (!Check(_records.HasActiveConsultation, "Confirmaciones: no se abrió la consulta")) return;
        Settle();

        var previousConfirm = DialogService.ConfirmHandler;
        var previousError = DialogService.ErrorHandler;
        var previousShowModal = TPVC.Helpers.ThemedDialogs.ShowModal;
        var asked = new List<(TPVC.Views.ConfirmDialogKind Kind, string Title)>();
        bool answerYes = false;

        TPVC.Helpers.ThemedDialogs.Install(() => _window);
        TPVC.Helpers.ThemedDialogs.ShowModal = dialog =>
        {
            asked.Add((dialog.Kind, dialog.Title));
            var what = $"Ventana \"{dialog.Title}\"";
            Check(ReferenceEquals(dialog.Owner, _window), $"{what}: no queda sobre la ventana principal");
            Check(!dialog.ShowInTaskbar, $"{what}: aparece en la barra de tareas");

            var buttons = LogicalDescendants<System.Windows.Controls.Button>(dialog)
                .Where(b => b.Visibility == System.Windows.Visibility.Visible)
                .ToList();
            var escape = buttons.Where(b => b.IsCancel).ToList();
            var enter = buttons.Where(b => b.IsDefault).ToList();
            if (!Check(escape.Count == 1 && enter.Count == 1 && ReferenceEquals(escape[0], enter[0]),
                    $"{what}: Esc y Enter deben tocar el mismo botón, el seguro ({escape.Count} con Esc, {enter.Count} con Enter)"))
            {
                return;
            }
            var safe = escape[0];
            Check(ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(dialog), safe),
                $"{what}: el foco al abrir no está en \"{safe.Content}\"");

            System.Windows.Controls.Button? yes = null;
            if (dialog.Kind == TPVC.Views.ConfirmDialogKind.Confirm)
            {
                yes = buttons.SingleOrDefault(b => !ReferenceEquals(b, safe));
                Check(buttons.Count == 2 && Equals(safe.Content, "No") && Equals(yes?.Content, "Sí"),
                    $"{what}: se esperaban \"No\" (lo seguro) y \"Sí\" ({string.Join(", ", buttons.Select(b => b.Content))})");
            }
            else
            {
                Check(buttons.Count == 1 && Equals(safe.Content, "Aceptar"),
                    $"{what}: un error lleva solo \"Aceptar\" ({string.Join(", ", buttons.Select(b => b.Content))})");
            }

            var target = answerYes ? yes : safe;
            if (Check(target != null, $"{what}: no hay botón para contestar \"Sí\"")) Invoke(target!);
        };

        try
        {
            Check(TPVC.Helpers.ThemedDialogs.CanShowOver(_window), "La ventana principal cargada y visible no puede llevar la confirmación de la app");

            // Lo seguro (Esc o Enter al abrir): la consulta sigue
            WaitFor(_records.DiscardActiveConsultationAsync());
            Check(asked.Count == 1 && asked[0] == (TPVC.Views.ConfirmDialogKind.Confirm, "Descartar consulta"),
                $"\"Descartar consulta\" no preguntó con la ventana de la app ({string.Join(", ", asked)})");
            Check(_records.HasActiveConsultation, "Contestar lo seguro (Esc o Enter) en \"Descartar consulta\" descartó la consulta");
            Check(!DialogService.Confirm("¿Continuar?", "Prueba"), "DialogService.Confirm contestado con lo seguro regresó true");

            answerYes = true;
            Check(DialogService.Confirm("¿Continuar?", "Prueba"), "DialogService.Confirm contestado con \"Sí\" regresó false");
            WaitFor(_records.DiscardActiveConsultationAsync());
            Check(!_records.HasActiveConsultation, "\"Sí\" en \"Descartar consulta\" no descartó la consulta");
            answerYes = false;

            int before = asked.Count;
            DialogService.ShowError("No se pudo imprimir la receta médica: prueba.", "TPVC");
            Check(asked.Count == before + 1 && asked[^1].Kind == TPVC.Views.ConfirmDialogKind.Error, "DialogService.ShowError no usó la ventana de la app");

            // Sin ventana principal o con una que nunca se mostró: el MessageBox de Windows (aquí solo se revisa la condición)
            var neverShown = new System.Windows.Window();
            try
            {
                Check(!TPVC.Helpers.ThemedDialogs.CanShowOver(neverShown) && !TPVC.Helpers.ThemedDialogs.CanShowOver(null),
                    "Sin ventana principal cargada no debe usarse la ventana de la app (sin dueño no se puede mostrar)");
            }
            finally
            {
                neverShown.Close();
            }
        }
        finally
        {
            DialogService.ConfirmHandler = previousConfirm;
            DialogService.ErrorHandler = previousError;
            TPVC.Helpers.ThemedDialogs.ShowModal = previousShowModal;
        }
    }

    /// <summary>
    /// Lo que hacen Enter, Esc o un clic en un botón: su OnClick (con IsCancel, además, la orden de cancelar
    /// el diálogo, que cierra la ventana cuando es modal). Por automatización, como un lector de pantalla.
    /// </summary>
    private static void Invoke(System.Windows.Controls.Button button)
    {
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        var invoke = (System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
        invoke.Invoke();
        Pump();
    }

    private static IEnumerable<T> LogicalDescendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in LogicalDescendants<T>(child)) yield return nested;
        }
    }

    // ------------------------------------------------------------------ eventos de teclado reales

    private bool PressEscape() => PressKey(_window, System.Windows.Input.Key.Escape);

    /// <summary>PreviewKeyDown y luego KeyDown (como WPF), sobre el elemento destino; true si alguien lo atendió.</summary>
    private bool PressKey(System.Windows.IInputElement target, System.Windows.Input.Key key)
    {
        var preview = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, _source!, Environment.TickCount, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(preview);
        var down = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, _source!, Environment.TickCount, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent,
            Handled = preview.Handled
        };
        target.RaiseEvent(down);
        Pump();
        return preview.Handled || down.Handled;
    }

    /// <summary>Texto tecleado con el foco fuera de cualquier campo (PreviewTextInput de la ventana).</summary>
    private bool TypeText(string text)
    {
        System.Windows.Input.Keyboard.ClearFocus();
        var composition = new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, _window, text);
        var args = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputEvent
        };
        _window.RaiseEvent(args);
        Pump();
        return args.Handled;
    }

    /// <summary>Texto tecleado con el foco en un campo (PreviewTextInput sobre el campo).</summary>
    private bool TypeInto(System.Windows.Controls.TextBox box, string text)
    {
        var composition = new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, box, text);
        var args = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputEvent
        };
        box.RaiseEvent(args);
        Pump();
        return args.Handled;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    /// <summary>Ctrl presionado para este hilo (Keyboard.Modifiers lo lee del estado del teclado del hilo).</summary>
    private void WithControl(Action action)
    {
        var original = new byte[256];
        GetKeyboardState(original);
        var pressed = (byte[])original.Clone();
        pressed[0x11] |= 0x80; // VK_CONTROL
        pressed[0xA2] |= 0x80; // VK_LCONTROL
        SetKeyboardState(pressed);
        try
        {
            if (Check(System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control,
                    $"No se pudo simular Ctrl (Keyboard.Modifiers = {System.Windows.Input.Keyboard.Modifiers})"))
            {
                action();
            }
        }
        finally
        {
            SetKeyboardState(original);
        }
    }

    // ------------------------------------------------------------------ utilidades WPF

    private void Settle()
    {
        Pump();
        _window.UpdateLayout();
        Pump();
    }

    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
        }
    }

    private bool PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            Pump();
            if (condition()) return true;
            Thread.Sleep(15);
        }
        Pump();
        return condition();
    }

    private T? PumpUntilFound<T>(Func<T, bool> predicate) where T : System.Windows.DependencyObject =>
        PumpUntilFound(_window, predicate);

    private T? PumpUntilFound<T>(System.Windows.DependencyObject root, Func<T, bool> predicate) where T : System.Windows.DependencyObject
    {
        T? found = null;
        PumpUntil(() =>
        {
            _window.UpdateLayout();
            found = FindDescendant(root, predicate);
            return found != null;
        }, 3000);
        return found;
    }

    private static T? FindDescendant<T>(System.Windows.DependencyObject root, Func<T, bool> predicate) where T : System.Windows.DependencyObject
    {
        if (root is T match && predicate(match)) return match;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindDescendant(System.Windows.Media.VisualTreeHelper.GetChild(root, i), predicate);
            if (found != null) return found;
        }
        return null;
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
