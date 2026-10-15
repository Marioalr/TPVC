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
    [TestCategory("Windows")]
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
    public static void Run() => WpfTestThread.Run(() => new UiSmokeSession().Execute());
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
    private bool _labsSeeded;
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


    /// <summary>El primer elemento con ese nombre de accesibilidad (AutomationProperties.Name).</summary>
    private static System.Windows.FrameworkElement? FindNamed(System.Windows.DependencyObject root, string name)
    {
        if (root is System.Windows.FrameworkElement element && System.Windows.Automation.AutomationProperties.GetName(element) == name) return element;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindNamed(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        }
        return null;
    }
    private static T? FindDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        if (root is T match) return match;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindDescendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
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
            // Indicaciones frecuentes (la base de demostración no trae las del Excel del consultorio): desde el 2026-10-14
            // la receta las ofrece dentro del campo "Indicación"
            db.PrescriptionTemplates.AddRange(SamplePosologies());
            db.SaveChanges();
        }
        var factory = new AppDbContextFactory(builder.Options);
        var inventory = new InventoryService(factory);
        var consultations = new ConsultationService(seedDemoPatients: false);
        var profile = new ClinicProfileService(Path.Combine(_workDir, "clinic_profile.json"));
        SeedDoctorLetterhead(profile);
        var tursoSettings = new TursoSettingsService(Path.Combine(_workDir, "turso.json"));
        var tursoSync = new TursoSyncService(factory, tursoSettings, profile, inventory);
        // Paso 8.12b: los resultados de laboratorio de la ficha, con su archivo en la carpeta de la prueba
        var labs = new LabResultService(factory, new TPVC.Services.LabFiles.LabFileStore(Path.Combine(_workDir, "laboratorio")),
            new LocalPreferencesStore(Path.Combine(_workDir, "preferencias.json")));

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
            (seeded[2], DateTime.Today.AddDays(1), "10:00", "Consulta de seguimiento"),
            // Paso 8.23: se empalma con la de las 10:00 (la marca "Empalme" de la agenda)
            (seeded[3], DateTime.Today.AddDays(1), "10:15", "Primera vez")
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
            consultationHandoffService: handoff,
            labResultService: labs);
        main.IsFirstRunWizardOpen = false;
        // Equipos vinculados (paso 8.10) se lee a mano antes de su captura, no al abrir la sección
        main.TursoSyncVM.RefreshDevicesOnOpen = false;

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

            // Paso 8.23: lo que llegó de otro equipo (un empalme de mañana y un posible duplicado): los avisos que se quedan
            // hasta «Entendido». Después se descartan y se olvida lo descartado, así el siguiente tema los vuelve a mostrar
            var twin = WaitFor(SmokeTwinAsync(inventory, records.Patients.ToList()));
            WaitFor(inventory.AddSyncNoticesAsync(WaitFor(SmokeArrivalsAsync(inventory, twin))));
            WaitFor(records.Home.LoadSyncNoticesAsync());
            Show(host, main, "Records", $"04b-pacientes-avisos-otro-equipo-{suffix}");
            if (!records.Home.HasDuplicatePatientsNotice || !records.Home.HasOverlapNotice)
                _failures.Add($"[04b-{suffix}] No aparecen los avisos de empalme y de posible duplicado");
            WaitFor(records.Home.DismissOverlapNoticeAsync());
            WaitFor(records.Home.DismissDuplicatePatientsNoticeAsync());
            using (var noticesDb = factory.CreateDbContext()) DbMeta.Remove(noticesDb, SyncNotices.MetaKey);
            WaitFor(inventory.DeletePatientAsync(twin.Id));

            records.OpenPatientChart(records.Patients.FirstOrDefault(p => p.Consultations.Count > 0) ?? records.Patients.First());
            Capture(host, $"05-ficha-{suffix}");

            // Resultados de laboratorio (paso 8.12b): uno con su archivo aquí, otro que se baja al abrirlo, y el formulario
            var chartPatient = records.Session.SelectedPatient!;
            if (!_labsSeeded)
            {
                _labsSeeded = true;
                var pdf = System.Text.Encoding.Latin1.GetBytes("%PDF-1.7\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Type /Page >> endobj\n%%EOF");
                WaitFor(labs.AddAsync(chartPatient.Id, new LabResultDraft("Química sanguínea de 6 elementos", "Laboratorio Central", DateTime.Today.AddDays(-3),
                    "Glucosa 112 mg/dL; resto normal"), new TPVC.Services.LabFiles.PreparedLabFile(pdf, LabResult.PdfMimeType, 2, "qs6.pdf")));
                var remote = WaitFor(labs.AddAsync(chartPatient.Id, new LabResultDraft("Biometría hemática", "Laboratorio Central", DateTime.Today.AddDays(-40), string.Empty),
                    new TPVC.Services.LabFiles.PreparedLabFile(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, LabResult.JpegMimeType, 1, "bh.jpg")));
                File.Delete(labs.Store.PathFor(remote.ContentSha256));
            }
            WaitFor(records.LabResults.LoadForPatientAsync(chartPatient.Id));
            var picker = DialogService.OpenFilesPickerHandler;
            var pickedPath = Path.Combine(_workDir, "resultado-ego.pdf");
            File.WriteAllBytes(pickedPath, System.Text.Encoding.Latin1.GetBytes("%PDF-1.4\n%%EOF"));
            DialogService.OpenFilesPickerHandler = (_, _) => new[] { pickedPath };
            try
            {
                records.LabResults.StartAdd();
            }
            finally
            {
                DialogService.OpenFilesPickerHandler = picker;
            }
            records.LabResults.NewTitle = "Examen general de orina";
            Capture(host, $"05b-ficha-laboratorio-{suffix}");
            if (records.LabResults.Items.Count != 2 || !records.LabResults.IsAddFormOpen)
                _failures.Add($"[05b-{suffix}] La tarjeta de laboratorio no muestra sus 2 resultados y el formulario");
            records.LabResults.CancelAdd();
            Pump();
            if (FindNamed(host, "Resultados de laboratorio") is { } labCard)
            {
                Layout(host, Width, Height);
                labCard.BringIntoView();
            }
            Capture(host, $"05c-ficha-laboratorio-lista-{suffix}");
            records.Chart.ToggleAntecedent(records.Chart.AntecedentSections.First());
            records.Chart.IsVitalsChartVisible = true;
            Capture(host, $"06-ficha-detalle-{suffix}");
            records.Chart.IsVitalsChartVisible = false;

            // La hoja de una consulta del historial (v1.24.0): detalle por datos y su receta, también corrigiéndola
            if (records.Chart.Timeline.FirstOrDefault(t => t.Consultation != null)?.Consultation is { } pastConsultation)
            {
                records.ConsultationDetail.Open(pastConsultation);
                Capture(host, $"06b-ficha-consulta-{suffix}");
                records.ConsultationDetail.StartEditPrescription();
                records.ConsultationDetail.NewMedicineText = "para";
                Capture(host, $"06c-ficha-consulta-corregir-receta-{suffix}");
                records.ConsultationDetail.CancelEditPrescription();
                records.ConsultationDetail.Close();
            }
            else
            {
                _failures.Add($"[06b-{suffix}] La ficha de prueba no tiene consultas en el historial");
            }

            WaitFor(records.Agenda.ScheduleForSelectedPatientAsync());
            WaitFor(records.Agenda.OpenAgendaSlotAsync(records.Agenda.AgendaSlots.First(s => s.IsFree)));
            Capture(host, $"07-agenda-{suffix}");
            // Paso 8.23: las dos citas de mañana a las 10:00 y 10:15 llevan la marca "Empalme"
            records.Agenda.CloseAgendaForm();
            Capture(host, $"07b-agenda-empalme-{suffix}");
            if (records.Agenda.AgendaSlots.Count(s => s.IsOverlapping) != 2) _failures.Add($"[07b-{suffix}] La agenda no marca las dos citas empalmadas");
            records.Agenda.CloseAgendaPanel();

            records.ChartPrimaryAction();
            records.Consultation.ConsultationChiefComplaint = "Fiebre y dolor de garganta de 2 días";
            records.Consultation.ConsultationTA = "130/85";
            records.Consultation.ConsultationDiagnosis = "J00 Rinofaringitis aguda";
            Capture(host, $"08-consulta-{suffix}");

            // Enviar a otro equipo: la hoja con los equipos enlazados y la consulta en solo lectura
            WaitFor(records.Handoff.OpenSendConsultationSheetAsync());
            Capture(host, $"08c-consulta-enviar-a-{suffix}");
            WaitFor(records.Handoff.SendConsultationToSelectedDeviceAsync());
            Capture(host, $"08d-consulta-enviada-{suffix}");
            Show(host, main, "Pos", $"08e-pos-consulta-enviada-{suffix}");
            main.NavigateTo("Records");
            WaitFor(records.Handoff.RecoverHandedOffConsultationAsync());

            // El POS con una consulta en curso (se puede abrir o pasarle la venta)
            Show(host, main, "Pos", $"08b-pos-con-consulta-{suffix}");
            main.NavigateTo("Records");

            records.Consultation.ConsultationSection = 5;
            Capture(host, $"09-consulta-tratamiento-{suffix}");

            // Laboratorio (v1.25.0): la sección 6 con estudios y una búsqueda abierta, y la vista previa de la solicitud.
            // Desde el 2026-10-14 se llega con "Estudios de laboratorio" de la sección 5
            if (FindNamed(host, "Estudios de laboratorio") is System.Windows.Controls.Button labButton)
            {
                // Como un clic: su Click (traer la sección a la vista) y su comando
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(labButton)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                Pump();
                if (records.Consultation.ConsultationSection != 6) _failures.Add($"[09b-{suffix}] «Estudios de laboratorio» no abrió la sección 6");
            }
            else
            {
                _failures.Add($"[09-{suffix}] La sección 5 no tiene el botón «Estudios de laboratorio»");
            }
            records.Consultation.ConsultationSection = 6;
            records.Labs.AddPanel(LabCatalog.Panels[0]);
            records.Labs.AppendIndication(LabCatalog.IndicationPresets[0]);
            records.Labs.StudySearchText = "orina";
            Capture(host, $"09b-consulta-laboratorio-{suffix}");
            records.Labs.StudySearchText = string.Empty;
            records.Labs.OpenPreview();
            if (!records.Labs.IsPreviewOpen || records.Labs.PreviewSheet?.StudyCount != 4)
            {
                _failures.Add($"[09c-{suffix}] La vista previa de laboratorio no abrió con los 4 estudios del chequeo general");
            }
            Capture(host, $"09c-vista-previa-laboratorio-{suffix}");
            records.Labs.ClosePreview();

            // El pie de la nota, después de Laboratorio: "Agendar próxima cita" y "Terminar consulta"
            if (FindNamed(host, "Agendar próxima cita") is { } footerSchedule)
            {
                Layout(host, Width, Height);
                footerSchedule.BringIntoView();
                Capture(host, $"09d-consulta-pie-{suffix}");
            }
            else
            {
                _failures.Add($"[09d-{suffix}] No se encontró el pie de la nota con «Agendar próxima cita»");
            }
            records.Consultation.ConsultationSection = 5;

            records.ShowChartFromConsultation();
            Capture(host, $"10-ficha-con-consulta-{suffix}");
            records.ShowConsultation();

            WaitFor(records.Prescription.OpenPrescriptionModalAsync());
            Capture(host, $"11-receta-{suffix}");

            // Pedido del 2026-10-14: el medicamento elegido (aún sin agregar) y las indicaciones frecuentes dentro del campo
            records.Prescription.PrescriptionSearchText = "para";
            Capture(host, $"11e-receta-sugerencias-{suffix}");
            records.Prescription.ConfirmPrescriptionSearch();
            if (!records.Prescription.IsMedicineChosen || !records.Prescription.CaptureIndications.Open())
            {
                _failures.Add($"[11f-{suffix}] No se eligió el medicamento o no se abrieron las indicaciones frecuentes");
            }
            Capture(host, $"11f-receta-elegido-frecuentes-{suffix}");
            records.Prescription.CaptureIndications.Close();
            records.Prescription.ClearChosenMedicine();

            // El paciente de prueba es alérgico a la penicilina: la amoxicilina debe avisar
            records.Prescription.PrescriptionMedicineName = "Amoxicilina / Ácido Clavulánico 500/125 mg";
            records.Prescription.PrescriptionDosage = "1 tableta cada 12 horas por 7 días";
            WaitFor(records.Prescription.SelectAndAddPrescriptionProductAsync(records.Prescription.SelectedPrescriptionProduct));
            if (records.Prescription.IsAllergyAlertOpen)
            {
                Capture(host, $"11b-alerta-alergia-{suffix}");
                records.Prescription.CancelAllergyPrescription();
            }
            WaitFor(records.Prescription.RepeatPreviousPrescriptionAsync());
            records.Info.DismissInfoModal();
            Capture(host, $"11c-receta-con-medicamentos-{suffix}");

            // "Cambiar indicación" en un renglón de la hoja, con sus frecuentes abiertas
            var firstRow = records.Session.SelectedPatient!.ActivePrescription.FirstOrDefault();
            if (firstRow != null)
            {
                records.Prescription.SheetIndication.Start(firstRow);
                records.Prescription.SheetIndication.Picker.Open();
                Capture(host, $"11g-receta-cambiar-indicacion-{suffix}");
                if (!records.Prescription.SheetIndication.IsEditing) _failures.Add($"[11g-{suffix}] «Cambiar indicación» no abrió el campo del renglón");
                records.Prescription.SheetIndication.Cancel();
            }
            else
            {
                _failures.Add($"[11c-{suffix}] La receta repetida quedó vacía");
            }
            records.Preview.OpenPrescriptionPdfPreview();
            Capture(host, $"11d-vista-previa-receta-{suffix}");
            records.Preview.ClosePrescriptionPdfPreview();
            // La hoja impresa es papel: no depende del tema (se revisa una vez)
            if (suffix == "claro") CheckPrintedPrescription(records.Session.SelectedPatient!, profile.CurrentProfile, records.Preview.PrescriptionIndications);
            records.Info.DismissInfoModal();
            records.Prescription.ClosePrescriptionModal();

            // "Cambiar indicación" también en la lista de la sección 5 de la consulta
            if (firstRow != null)
            {
                records.Prescription.PlanIndication.Start(firstRow);
                records.Prescription.PlanIndication.Picker.Open();
                Capture(host, $"11h-consulta-cambiar-indicacion-{suffix}");
                records.Prescription.PlanIndication.Cancel();
            }

            WaitFor(records.Supplies.OpenSuppliesModalAsync());
            Capture(host, $"12-insumos-{suffix}");
            records.Supplies.CloseSuppliesModal();

            WaitFor(records.ClinicalServices.OpenServicesModalAsync());
            Capture(host, $"13-servicios-{suffix}");
            records.ClinicalServices.CloseServicesModal();

            WaitFor(records.Prescription.OpenPrescriptionModalAsync());
            WaitFor(records.Prescription.OpenPrescriptionCatalogAsync());
            Capture(host, $"14-catalogo-{suffix}");
            records.CatalogBrowser.CloseCatalogBrowser();
            records.Prescription.ClosePrescriptionModal();

            WaitFor(records.Consultation.FinalizeConsultationAsync());
            Capture(host, $"15-terminar-consulta-{suffix}");
            records.Consultation.CloseFinishConsultation();
            WaitFor(records.DiscardActiveConsultationAsync());

            // Una consulta que la tablet mandó a esta PC: el aviso en Pacientes
            InsertIncomingHandoff(records.Patients.First(), suffix);
            WaitFor(records.Handoff.PollHandoffsOnceAsync());
            Show(host, main, "Records", $"15b-pacientes-consulta-recibida-{suffix}");
            ExecuteCloud("UPDATE ConsultationHandoffs SET State = 'cancelada', Payload = '' WHERE FromDeviceId = 'device-tablet'");
            WaitFor(records.Handoff.PollHandoffsOnceAsync());

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

            // Pedido del 2026-10-14: la receta de ejemplo en "Médico y consultorio", en media carta y en carta (papel blanco
            // con cualquier tema: las páginas son las del documento que se imprime)
            main.NavigateTo("ClinicProfile");
            var clinicProfile = shell.ClinicProfileVM;
            clinicProfile.OpenPrescriptionPreview();
            Capture(host, $"20l-config-receta-media-carta-{suffix}");
            CheckPreviewPages(host, PrescriptionSheet.PageHeight, $"20l-{suffix}");
            clinicProfile.ShowPreviewPaper(PrescriptionPaper.Letter);
            Capture(host, $"20m-config-receta-carta-{suffix}");
            CheckPreviewPages(host, PrescriptionSheet.LetterPageHeight, $"20m-{suffix}");
            clinicProfile.ClosePrescriptionPreview();

            // Paso 8.2: el freno de consumo detiene la sincronización automática y ofrece "Reanudar"
            tursoSettings.RecordUsage(TursoConsumptionBrake.MaxRowsReadPerDay + 1, 0);
            WaitFor(tursoSync.SyncAsync());
            Show(host, main, "TursoSync", $"20b-config-tursosync-freno-{suffix}");
            tursoSync.ResumeAfterConsumptionBrake();
            WaitFor(tursoSync.SyncAsync());

            // Paso 8.4: filas apartadas ("N cambios sin sincronizar")
            using (var db = factory.CreateDbContext())
            {
                db.SyncParkedRows.Add(new SyncParkedRow { Direction = SyncParkedRow.Pull, EntityType = nameof(PatientConsultation), SyncId = Guid.NewGuid(), Reason = "Captura" });
                db.SyncParkedRows.Add(new SyncParkedRow { Direction = SyncParkedRow.Pull, EntityType = nameof(InventoryMovement), SyncId = Guid.NewGuid(), Reason = "Captura" });
                db.SaveChanges();
            }
            shell.TursoSyncVM.LoadFromSettings();
            Show(host, main, "TursoSync", $"20c-config-tursosync-apartados-{suffix}");
            using (var db = factory.CreateDbContext())
            {
                db.SyncParkedRows.RemoveRange(db.SyncParkedRows);
                db.SaveChanges();
            }
            shell.TursoSyncVM.LoadFromSettings();

            // Paso 8.7: nube de otra base con el reloj 12 min adelantado, y reporte de la sincronización completa con diferencias
            var cloudVm = shell.TursoSyncVM;
            ExecuteCloud("INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('clinicId', 'otra-nube');");
            _cloud!.Clock = () => DateTime.UtcNow.AddMinutes(12);
            WaitFor(tursoSync.SyncAsync());
            cloudVm.FullSyncReportText = new TursoFullSyncReport
            {
                ParkedRows = 1,
                Tables = new[] { new TursoTableCount("Patients", 120, 121), new TursoTableCount("Sales", 300, 300) },
                LegacyTables = new[] { "legacy_v1_Products" }
            }.Summary;
            cloudVm.IsFullSyncReportClean = false;
            Show(host, main, "TursoSync", $"20d-config-tursosync-identidad-{suffix}");
            if (string.IsNullOrEmpty(cloudVm.IdentityNoticeText) || string.IsNullOrEmpty(cloudVm.ClockNoticeText) || !cloudVm.CanAdoptCloudIdentity
                || !cloudVm.CanReplaceWithCloud)
            {
                _failures.Add($"[20d-{suffix}] Faltan los avisos de nube ajena o de hora");
            }

            // Paso 8.8: «Reemplazar con la nube» pide teclear CONFIRMAR
            cloudVm.OpenReplaceWithCloud();
            bool lockedWithoutWord = !cloudVm.CanConfirmReplaceWithCloud;
            cloudVm.ReplaceConfirmWord = "CONFIRMAR";
            Show(host, main, "TursoSync", $"20e-config-tursosync-reemplazar-{suffix}");
            if (!lockedWithoutWord || !cloudVm.CanConfirmReplaceWithCloud)
            {
                _failures.Add($"[20e-{suffix}] «Reemplazar con la nube» no depende de teclear CONFIRMAR");
            }
            cloudVm.CancelReplaceWithCloud();
            ExecuteCloud("DELETE FROM _tpvc_meta WHERE Key = 'clinicId';");
            _cloud.Clock = null;
            WaitFor(tursoSync.SyncAsync());
            cloudVm.LoadFromSettings();
            cloudVm.FullSyncReportText = string.Empty;

            // Paso 8.9: un respaldo restaurado con la nube activa (Respaldos y datos, con lo que se perdería) y cambios
            // guardados para «Volver a aplicar» (Nube y dispositivos)
            using (var db = factory.CreateDbContext())
            {
                DbMeta.Set(db, DbMeta.RestoredWithCloud, "1");
                WaitFor(SyncSavedChanges.SaveAsync(db, new[]
                {
                    new SavedChange
                    {
                        Source = SyncSavedChanges.SourceThisDevice, EntityType = nameof(PatientRecord), SyncId = Guid.NewGuid().ToString(),
                        Kind = SyncSavedChanges.KindRow, Payload = "{}", Protocol = TursoSyncService.ClientProtocol, Contract = SyncContract.Revision,
                        SavedAtUtc = TursoSyncService.FormatDate(DateTime.UtcNow)
                    }
                }));
            }
            tursoSync.RefreshSavedChangesCount();
            main.DataVM.RefreshCloudDecision();
            main.DataVM.CloudPreviewText = new CloudReplacementPreview
            {
                Lines = new[] { new CloudLossLine("Pacientes", 3, 0), new CloudLossLine("Ventas", 12, 1) }
            }.Summary;
            Show(host, main, "Data", $"20f-config-data-nube-restaurada-{suffix}");
            if (!main.DataVM.IsCloudDecisionPending) _failures.Add($"[20f-{suffix}] No aparece la decisión sobre la nube después de restaurar");
            cloudVm.LoadFromSettings();
            Show(host, main, "TursoSync", $"20g-config-tursosync-volver-a-aplicar-{suffix}");
            if (string.IsNullOrEmpty(cloudVm.GenerationNoticeText) || string.IsNullOrEmpty(cloudVm.SavedChangesNoticeText))
            {
                _failures.Add($"[20g-{suffix}] Faltan los avisos del respaldo restaurado o de «Volver a aplicar»");
            }
            using (var db = factory.CreateDbContext())
            {
                DbMeta.Remove(db, DbMeta.RestoredWithCloud);
                WaitFor(SyncSavedChanges.DeleteAllAsync(db));
            }
            tursoSync.RefreshSavedChangesCount();
            main.DataVM.RefreshCloudDecision();
            cloudVm.LoadFromSettings();

            // Paso 8.10: Equipos vinculados, con esta PC como la principal, la tablet con cambios sin subir y la PC de
            // recepción (v1.15, sin código)
            ExecuteCloud("INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('primaryDeviceId', 'device-pc'); " +
                         "INSERT OR IGNORE INTO DeviceRegistry (DeviceId, DeviceCode, Role, DisplayName, Model, PairedAtUtc) VALUES " +
                         "('device-pc', 'P1', 'WindowsDesktop', 'PC del consultorio', 'Windows', strftime('%Y-%m-%d %H:%M:%f','now')), " +
                         "('device-tablet', 'T1', 'AndroidTablet', 'Tablet del consultorio', 'SM-X710', strftime('%Y-%m-%d %H:%M:%f','now')); " +
                         "UPDATE RegisteredDevices SET Platform = 'Android', HandoffProtocol = 1, PendingCount = 12, " +
                         "OldestPendingUtc = strftime('%Y-%m-%d %H:%M:%f','now','-2 hours'), LastSeenUtc = strftime('%Y-%m-%d %H:%M:%f','now','-20 seconds') " +
                         "WHERE DeviceId = 'device-tablet';");
            WaitFor(cloudVm.RefreshDevicesAsync());
            main.NavigateTo("TursoSync");
            Pump();
            Layout(host, Width, Height);
            // La tarjeta queda debajo de «Este equipo»: se desplaza hasta ella para la captura
            var cloudScroller = FindDescendant<System.Windows.Controls.ScrollViewer>(FindDescendant<TPVC.Views.TursoSyncView>(host)!);
            cloudScroller?.ScrollToVerticalOffset(600);
            Capture(host, $"20h-config-tursosync-equipos-{suffix}");
            cloudScroller?.ScrollToVerticalOffset(0);
            if (cloudVm.Devices.Count != 3 || !cloudVm.Devices[0].IsPrimary || !cloudVm.Devices.Any(d => d.PendingCount == 12)
                || string.IsNullOrEmpty(cloudVm.PrimaryText) || cloudVm.CanMakeThisThePrimary)
            {
                _failures.Add($"[20h-{suffix}] Equipos vinculados no muestra los 3 equipos, la PC principal o los cambios sin subir");
            }

            // Paso 8.14: una tablet que leyó el QR espera que la vinculen, y el QR con la URL y el token (con la nube verificada)
            ExecuteCloud("INSERT OR REPLACE INTO RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc, Platform, HandoffProtocol, PendingCount) " +
                         "VALUES ('android-tab-3f9a2c', 'Galaxy Tab S9', 'AndroidTablet', '1.0.0', strftime('%Y-%m-%d %H:%M:%f','now','-5 seconds'), 'Android', 1, 0);");
            using (var db = factory.CreateDbContext()) DbMeta.Set(db, DbMeta.ClinicId, "clinica-de-prueba");
            WaitFor(cloudVm.RefreshDevicesAsync());
            WaitFor(cloudVm.ShowLinkQrAsync());
            Pump();
            Layout(host, Width, Height);
            cloudScroller?.ScrollToVerticalOffset(600);
            Capture(host, $"20i-config-tursosync-equipo-nuevo-{suffix}");
            if (FindNamed(host, "Vincular tablet y celular") is { } linkCard) linkCard.BringIntoView();
            Capture(host, $"20j-config-tursosync-qr-{suffix}");
            cloudScroller?.ScrollToVerticalOffset(0);
            if (!cloudVm.Devices.Any(d => d.IsPendingApproval && d.DeviceIdSuffix == "3F9A2C") || !cloudVm.IsLinkQrVisible || cloudVm.LinkQrImage == null)
            {
                _failures.Add($"[20i-{suffix}] Falta el equipo que espera la aprobación o el QR para vincular");
            }
            cloudVm.CloseLinkQr();
            ExecuteCloud("DELETE FROM RegisteredDevices WHERE DeviceId = 'android-tab-3f9a2c';");
            using (var db = factory.CreateDbContext()) DbMeta.Remove(db, DbMeta.ClinicId);

            // Paso 8.15: se marcó un equipo como perdido (los pasos para anular los tokens) y la tablet no se conecta desde
            // que se anularon
            tursoSettings.ExpectTokenRotation();
            ExecuteCloud($"INSERT OR REPLACE INTO _tpvc_meta (Key, Value) VALUES ('{TursoSyncService.TokensRotatedKey}', strftime('%Y-%m-%d %H:%M:%f','now','-10 minutes')); " +
                         "UPDATE RegisteredDevices SET LastSeenUtc = strftime('%Y-%m-%d %H:%M:%f','now','-1 hours') WHERE DeviceId = 'device-tablet';");
            WaitFor(cloudVm.RefreshDevicesAsync());
            cloudVm.LoadFromSettings();
            Pump();
            Layout(host, Width, Height);
            if (FindNamed(host, "Cortar el acceso") is { } cutCard) cutCard.BringIntoView();
            Pump();
            cloudScroller?.ScrollToVerticalOffset(cloudScroller.VerticalOffset + 330); // también la tablet, debajo
            Pump();
            Capture(host, $"20k-config-tursosync-equipo-perdido-{suffix}");
            cloudScroller?.ScrollToVerticalOffset(0);
            if (string.IsNullOrEmpty(cloudVm.CutAccessGuideText) || !cloudVm.Devices.Any(d => d.NeedsNewToken && d.DeviceId == "device-tablet"))
            {
                _failures.Add($"[20k-{suffix}] Faltan los pasos para cortar el acceso o la tablet que necesita el token nuevo");
            }
            tursoSettings.ClearTokenRotation();
            ExecuteCloud($"DELETE FROM _tpvc_meta WHERE Key = '{TursoSyncService.TokensRotatedKey}'; " +
                         "UPDATE RegisteredDevices SET LastSeenUtc = strftime('%Y-%m-%d %H:%M:%f','now','-20 seconds') WHERE DeviceId = 'device-tablet';");
            cloudVm.LoadFromSettings();
            WaitFor(cloudVm.RefreshDevicesAsync());

            shell.SettingsSearchText = "respaldo";
            Capture(host, $"21-config-buscar-{suffix}");
            shell.SettingsSearchText = string.Empty;

            main.NavigateTo("Inventory");
            WaitFor(shell.InventoryVM.LoadDataAsync());
            WaitFor(shell.InventoryVM.NewProductAsync());
            Capture(host, $"22-config-producto-nuevo-{suffix}");
            shell.InventoryVM.CloseEditor();

            // «Ingresar pedido» (v1.22.0): el correo pegado y la revisión de sus renglones
            shell.InventoryVM.OpenOrderEntry();
            var order = shell.InventoryVM.OrderEntry!;
            order.PastedText = "Detalles de la Compra\nNúmero de Orden: #5175\nProducto\tSKU\tCantidad\tTotal\n" +
                               "PARACETAMOL 500MG C/20 TABS\tPAR500\t4\t$108.00\nVALCLAN 875/125MG C/14 TABS\tWAN014\t2\t$145.02\n" +
                               "VOYDOL S 30MG C/4 TABS\tRAM013\t3\t$30.78\nProducto Sujeto a Existencia\nSubtotal\t$283.80\nIVA\t$0.00\nTotal + IVA\t$283.80";
            Capture(host, $"22b-config-pedido-pegar-{suffix}");
            WaitFor(order.ReadOrderAsync());
            order.Lines[1].BatchNumber = "L2401";
            Capture(host, $"22c-config-pedido-revisar-{suffix}");
            if (order.Lines.Count != 3 || !order.IsTotalCheckOk || order.Lines[1].Error.Length == 0)
            {
                _failures.Add($"[22c-{suffix}] La hoja del pedido no leyó los 3 renglones o no avisa del lote sin caducidad");
            }
            order.Close();

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
            // Paso 8.23: las preguntas antes de agendar una cita empalmada y al crear un posible duplicado
            CaptureDialog(TPVC.Views.ConfirmDialogKind.Confirm, "Cita empalmada",
                TPVC.ViewModels.Patients.AgendaPanelViewModel.OverlapQuestion(new[] { new PatientAppointment { PatientName = "Ana María López", Time = "10:30" } }),
                $"26c-confirmacion-empalme-{suffix}");
            CaptureDialog(TPVC.Views.ConfirmDialogKind.Confirm, "Posible paciente duplicado",
                TPVC.ViewModels.Patients.PatientFormViewModel.DuplicateQuestion(new[]
                {
                    (new PatientRecord { FullName = "Juan Pérez Gómez", RecordNumber = "EXP-2026-004" }, TPVC.Services.Rules.DuplicatePatientRules.Match.BirthDate)
                }),
                $"26d-confirmacion-duplicado-{suffix}");
        }

        // Después de todas las capturas (cambia datos y muestra la ventana): menús y teclado reales
        new UiKeyboardPass(window, main, _failures, _bindingErrors, Path.Combine(_workDir, "consulta.json")).Run();
    }

    /// <summary>Paso 8.23: el gemelo de un paciente (mismo nombre en mayúsculas y mismo teléfono), como si llegara de la tablet.</summary>
    private static Task<PatientRecord> SmokeTwinAsync(InventoryService inventory, List<PatientRecord> patients)
    {
        var original = patients.First(p => TPVC.Services.Rules.DuplicatePatientRules.PhoneKey(p.Phone) != null);
        return inventory.SavePatientAsync(new PatientRecord
        {
            FullName = original.FullName.ToUpperInvariant(), Phone = original.Phone, Gender = original.Gender, Age = original.Age
        });
    }

    /// <summary>Paso 8.23: el empalme de las citas de mañana a las 10:00 y 10:15 y el posible duplicado, como los deja una bajada.</summary>
    private static async Task<SyncArrivals> SmokeArrivalsAsync(InventoryService inventory, PatientRecord twin)
    {
        var tomorrow = (await inventory.GetAppointmentsByDayAsync(DateTime.Today.AddDays(1))).OrderBy(a => a.Time).ToList();
        var rule = TPVC.Services.Rules.DuplicatePatientRules.Of(twin);
        var pairs = (await inventory.GetPatientsAsync())
            .Select(p => (Patient: p, Match: TPVC.Services.Rules.DuplicatePatientRules.Compare(rule, TPVC.Services.Rules.DuplicatePatientRules.Of(p))))
            .Where(x => x.Match != TPVC.Services.Rules.DuplicatePatientRules.Match.None)
            .Select(x => new DuplicatePatientPair(x.Patient, twin, x.Match))
            .ToList();
        return new SyncArrivals(new[] { new AppointmentOverlap(tomorrow[0], tomorrow[1]) }, pairs);
    }

    // ------------------------------------------------------------------ receta impresa (fase 7.4)

    /// <summary>El médico y su membrete como quedan al importar su Excel (escudo y trazo de la hoja RECETA).</summary>
    /// <summary>La vista previa de la receta de "Médico y consultorio" dibujó sus páginas en blanco y con el alto del papel elegido.</summary>
    private void CheckPreviewPages(System.Windows.FrameworkElement host, double paperHeight, string screen)
    {
        var view = FindDescendant<TPVC.Views.ClinicProfileView>(host);
        var pages = view?.FindName("PreviewPages") as System.Windows.Controls.StackPanel;
        var first = pages?.Children.OfType<System.Windows.Controls.Border>().FirstOrDefault();
        if (first == null)
        {
            _failures.Add($"[{screen}] La vista previa de la receta no dibujó ninguna página");
            return;
        }
        if (Math.Abs(first.Height - paperHeight) > 0.5) _failures.Add($"[{screen}] La página mide {first.Height} de alto en lugar de {paperHeight}");
        if ((first.Background as System.Windows.Media.SolidColorBrush)?.Color != System.Windows.Media.Colors.White)
            _failures.Add($"[{screen}] La página de la vista previa no es blanca");
    }

    /// <summary>Indicaciones frecuentes de muestra (la base de demostración no trae las del Excel del consultorio).</summary>
    private static List<PrescriptionTemplate> SamplePosologies() => new()
    {
        new() { Kind = PrescriptionTemplateKinds.Posology, Title = "Cada 8", Text = "Tomar 1 tableta vía oral cada 8 horas por 5 días", SortOrder = 1 },
        new() { Kind = PrescriptionTemplateKinds.Posology, Title = "Cada 12", Text = "Tomar 1 tableta vía oral cada 12 horas por 7 días", SortOrder = 2 },
        new() { Kind = PrescriptionTemplateKinds.Posology, Title = "Si hay dolor", Text = "Tomar 1 tableta vía oral cada 8 horas solo si hay dolor o fiebre", SortOrder = 3 }
    };

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
            _records.Consultation.ConsultationSection = 2;
            Settle();
            WithControl(() =>
            {
                Check(PressKey(grid!, System.Windows.Input.Key.Down) && _records.Consultation.ConsultationSection == 3, $"Ctrl+↓ pasa a la sección siguiente (quedó {_records.Consultation.ConsultationSection})");
                Check(PressKey(grid!, System.Windows.Input.Key.Up) && _records.Consultation.ConsultationSection == 2, $"Ctrl+↑ vuelve a la sección anterior (quedó {_records.Consultation.ConsultationSection})");
                try { File.Delete(_draftPath); } catch { }
                _records.Autosave.AutosaveStatus = string.Empty;
                Check(PressKey(grid!, System.Windows.Input.Key.S) && File.Exists(_draftPath), "Ctrl+S guarda la consulta en curso");
            });
            Pump();
            Check(_records.Autosave.AutosaveStatus.StartsWith("Guardado", StringComparison.Ordinal), $"Ctrl+S muestra la hora del guardado (\"{_records.Autosave.AutosaveStatus}\")");
        }

        // Clic en el cronómetro lo oculta (queda el reloj) y otro clic lo vuelve a mostrar
        var timer = workspace == null ? null : FindDescendant<System.Windows.Controls.Button>(workspace,
            b => System.Windows.Automation.AutomationProperties.GetName(b) == "Duración de la consulta (mostrar u ocultar)");
        if (Check(timer?.Command != null, "Consulta: no se encontró el cronómetro"))
        {
            var time = FindDescendant<System.Windows.Controls.TextBlock>(timer!, t => t.FontFamily.Source == "Consolas");
            var shown = _records.Consultation.IsConsultationTimerVisible;
            timer!.Command.Execute(null);
            Settle();
            Check(_records.Consultation.IsConsultationTimerVisible != shown && time?.IsVisible == !shown, "Clic en el cronómetro lo oculta o lo muestra");
            timer.Command.Execute(null);
            Settle();
            Check(_records.Consultation.IsConsultationTimerVisible == shown && time?.IsVisible == shown, "Otro clic lo deja como estaba");
        }

        // CIE-10 abierto, "Enviar a…" y "Terminar consulta": Esc los cierra uno por uno
        _records.Consultation.ConsultationDiagnosis = "farin";
        Settle();
        Check(_records.Consultation.IsCie10DropdownOpen, "Escribir el diagnóstico abre las sugerencias CIE-10");
        Check(PressEscape() && !_records.Consultation.IsCie10DropdownOpen, "Esc cierra las sugerencias CIE-10");
        _records.Handoff.IsSendConsultationSheetOpen = true;
        Settle();
        Check(PressEscape() && !_records.Handoff.IsSendConsultationSheetOpen, "Esc cierra \"Enviar a…\"");
        WaitFor(_records.Consultation.FinalizeConsultationAsync());
        Settle();
        Check(PressEscape() && !_records.Consultation.IsFinishConsultationOpen && _records.HasActiveConsultation, "Esc en \"Terminar consulta\" vuelve a la consulta sin cobrar");

        PrescriptionSheet();
        SuppliesSheet();
        ServicesSheet();

        WaitFor(_records.DiscardActiveConsultationAsync());
        Settle();
    }

    private void PrescriptionSheet()
    {
        WaitFor(_records.Prescription.OpenPrescriptionModalAsync());
        _records.Prescription.PrescriptionSearchText = string.Empty;
        Settle();
        var sheet = PumpUntilFound<TPVC.Views.PrescriptionSheetView>(v => v.IsVisible);
        var box = sheet?.FindName("PrescriptionSearchTextBox") as System.Windows.Controls.TextBox;
        var dosageBox = sheet?.FindName("PrescriptionDosageTextBox") as System.Windows.Controls.TextBox;
        if (!Check(box != null && dosageBox != null, "Receta: no se encontró el buscador o la indicación")) return;

        var rx = _records.Prescription;
        var suggestions = rx.PrescriptionProductSuggestions.ToList();
        var before = _records.SelectedPatient!.ActivePrescription.Count;
        if (Check(suggestions.Count > 1, "Receta: se esperaban al menos dos sugerencias"))
        {
            // Pedido del 2026-10-14: ↓ abre las sugerencias; Enter elige (no agrega) y el cursor pasa a la indicación
            var downHandled = PressKey(box!, System.Windows.Input.Key.Down);
            Check(downHandled && rx.IsPrescriptionDropdownOpen && rx.SelectedPrescriptionProduct?.Id == suggestions[0].Id,
                $"Receta: ↓ con el buscador vacío abre las sugerencias (atendida={downHandled}, abierta={rx.IsPrescriptionDropdownOpen}, " +
                $"elegido={rx.IsMedicineChosen}, marcada={rx.SelectedPrescriptionProduct?.Name ?? "(ninguna)"}, primera={suggestions[0].Name}, " +
                $"foco={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "(nada)"})");
            Check(PressKey(box!, System.Windows.Input.Key.Down) && rx.SelectedPrescriptionProduct?.Id == suggestions[1].Id,
                "Receta: ↓ en el buscador elige la siguiente sugerencia");
            var chosen = suggestions[1];
            ClearFocus(box!);
            Check(PressKey(box!, System.Windows.Input.Key.Enter), "Receta: Enter en el buscador se atiende");
            Check(rx.IsMedicineChosen && ReferenceEquals(rx.ChosenPrescriptionProduct, chosen) && _records.SelectedPatient!.ActivePrescription.Count == before,
                "Receta: Enter en el buscador elige el medicamento sin agregarlo");
            Check(box!.Text == PrescriptionNaming.MedicineLine(chosen), $"Receta: el buscador muestra el elegido (se ve \"{box.Text}\")");
            Check(PumpUntil(() => HasFocus(dosageBox!)), "Receta: después de elegir, el cursor pasa a la indicación");
            Check(_records.Templates.PosologyTemplates.Count >= 2, "Receta: la base de prueba trae indicaciones frecuentes");

            // Las frecuentes dentro del campo: ↓ las abre, Esc solo cierra la lista, Enter escribe la marcada
            Check(PressKey(dosageBox!, System.Windows.Input.Key.Down) && rx.CaptureIndications.IsOpen, "Receta: ↓ en la indicación abre las frecuentes");
            Check(PressEscape() && !rx.CaptureIndications.IsOpen && rx.IsPrescriptionModalOpen, "Receta: Esc cierra las frecuentes y la receta sigue abierta");
            Check(PressKey(dosageBox!, System.Windows.Input.Key.Down) && PressKey(dosageBox!, System.Windows.Input.Key.Down)
                  && PressKey(dosageBox!, System.Windows.Input.Key.Enter) && !rx.CaptureIndications.IsOpen
                  && rx.PrescriptionDosage == _records.Templates.PosologyTemplates[1].Text && dosageBox!.Text == rx.PrescriptionDosage,
                $"Receta: ↓ y Enter escriben la frecuente marcada en la indicación (quedó \"{rx.PrescriptionDosage}\")");

            // La indicación se edita y Enter en el campo agrega el medicamento con ella
            dosageBox!.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "1 tableta cada 8 horas");
            ClearFocus(dosageBox);
            Check(PressKey(dosageBox, System.Windows.Input.Key.Enter), "Receta: Enter en la indicación se atiende");
            Check(PumpUntil(() => _records.SelectedPatient!.ActivePrescription.Count == before + 1), "Receta: Enter en la indicación agrega el medicamento");
            var added = _records.SelectedPatient!.ActivePrescription.LastOrDefault();
            Check(added?.MedicineName == PrescriptionNaming.MedicineLine(chosen) && added?.Dosage == "1 tableta cada 8 horas",
                $"Receta: se agregó otro medicamento o sin su indicación ({added?.MedicineName}: {added?.Dosage})");
            Check(PumpUntil(() => HasFocus(box)) && box.Text.Length == 0, "Receta: después de agregar, el cursor vuelve al buscador vacío");

            // "Cambiar indicación": el lápiz del renglón abre su campo con el cursor; Esc cierra primero sus frecuentes y Enter guarda
            var pencil = added == null ? null : PumpUntilFound<System.Windows.Controls.Button>(sheet!,
                b => System.Windows.Automation.AutomationProperties.GetName(b) == "Cambiar indicación" && ReferenceEquals(b.DataContext, added));
            if (Check(pencil?.Command != null, "Receta: el renglón no tiene «Cambiar indicación»"))
            {
                var rowId = added!.RowId;
                ClearFocus(pencil!);
                Invoke(pencil!);
                var editBox = PumpUntilFound<System.Windows.Controls.TextBox>(sheet!,
                    t => t.IsVisible && System.Windows.Automation.AutomationProperties.GetName(t) == "Nueva indicación");
                if (Check(editBox != null && PumpUntil(() => HasFocus(editBox)), "Receta: «Cambiar indicación» abre el campo con el cursor"))
                {
                    Check(PressKey(editBox!, System.Windows.Input.Key.Down) && rx.SheetIndication.Picker.IsOpen, "Receta: ↓ en el renglón abre las frecuentes");
                    Check(PressEscape() && !rx.SheetIndication.Picker.IsOpen && rx.SheetIndication.IsEditing && rx.IsPrescriptionModalOpen,
                        "Receta: Esc cierra primero las frecuentes del renglón");
                    editBox!.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "1 tableta cada 6 horas");
                    Check(PressKey(editBox, System.Windows.Input.Key.Enter) && PumpUntil(() => !rx.SheetIndication.IsEditing), "Receta: Enter guarda la indicación del renglón");
                    Check(added.Dosage == "1 tableta cada 6 horas" && added.RowId == rowId && ReferenceEquals(_records.SelectedPatient!.ActivePrescription.LastOrDefault(), added),
                        $"Receta: la indicación nueva queda en el mismo renglón ({added.Dosage})");
                }
                Invoke(pencil!);
                Check(rx.SheetIndication.IsEditing && PressEscape() && !rx.SheetIndication.IsEditing && rx.IsPrescriptionModalOpen && added.Dosage == "1 tableta cada 6 horas",
                    "Receta: Esc cancela «Cambiar indicación» sin cerrar la receta");
            }
        }

        // Capas sobre la receta: el aviso, la alerta de alergia, el catálogo y la vista previa
        _records.Info.InfoModalMessage = "Aviso de prueba";
        _records.Info.IsInfoModalVisible = true;
        Settle();
        Check(PressEscape() && !_records.Info.IsInfoModalVisible && _records.Prescription.IsPrescriptionModalOpen, "Esc cierra primero el aviso (la receta sigue)");
        _records.Prescription.IsAllergyAlertOpen = true;
        Settle();
        Check(PressEscape() && !_records.Prescription.IsAllergyAlertOpen && _records.Prescription.IsPrescriptionModalOpen, "Esc cierra la alerta de alergia");
        WaitFor(_records.Prescription.OpenPrescriptionCatalogAsync());
        Settle();
        Check(PressEscape() && !_records.CatalogBrowser.IsCatalogBrowserModalOpen && _records.Prescription.IsPrescriptionModalOpen, "Esc cierra el catálogo (la receta sigue)");
        _records.Preview.OpenPrescriptionPdfPreview();
        Settle();
        Check(PressEscape() && !_records.Preview.IsPrescriptionPdfModalOpen, "Esc cierra la vista previa de la receta");
        Check(PressEscape() && !_records.Prescription.IsPrescriptionModalOpen, "Esc cierra la receta");
    }

    /// <summary>
    /// Sin foco en ningún lado (ni lógico), como en la prueba de la X del buscador: con la ventana sin activar, Focus() solo
    /// deja el foco lógico si nadie lo tiene, y así se ve a dónde lo manda la pantalla.
    /// </summary>
    private static void ClearFocus(System.Windows.DependencyObject element)
    {
        System.Windows.Input.Keyboard.ClearFocus();
        System.Windows.Input.FocusManager.SetFocusedElement(System.Windows.Input.FocusManager.GetFocusScope(element), null);
    }

    /// <summary>Con el cursor: el foco del teclado o, con la ventana sin activar (la prueba no la activa), el foco lógico.</summary>
    private static bool HasFocus(System.Windows.Controls.TextBox box) =>
        box.IsKeyboardFocused || ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(System.Windows.Input.FocusManager.GetFocusScope(box)), box);

    private void SuppliesSheet()
    {
        WaitFor(_records.Supplies.OpenSuppliesModalAsync());
        Settle();
        var sheet = PumpUntilFound<TPVC.Views.SuppliesSheetView>(v => v.IsVisible);
        var box = sheet?.FindName("SupplySearchTextBox") as System.Windows.Controls.TextBox;
        if (!Check(box != null, "Insumos: no se encontró el buscador")) return;

        var suggestions = _records.Supplies.SupplyProductSuggestions.ToList();
        var before = _records.SelectedPatient!.ActiveSupplies.Count;
        if (Check(suggestions.Count > 1, "Insumos: se esperaban al menos dos sugerencias"))
        {
            Check(PressKey(box!, System.Windows.Input.Key.Down) && ReferenceEquals(_records.Supplies.SelectedSupplyProduct, suggestions[1]),
                "Insumos: ↓ en el buscador elige la siguiente sugerencia");
            Check(PressKey(box!, System.Windows.Input.Key.Enter), "Insumos: Enter en el buscador se atiende");
            Check(PumpUntil(() => _records.SelectedPatient!.ActiveSupplies.Count == before + 1), "Insumos: Enter agrega la sugerencia elegida");
        }

        // Enter en el buscador del catálogo agrega el primero con existencias
        WaitFor(_records.Supplies.OpenSupplyCatalogAsync());
        _records.CatalogBrowser.CatalogBrowserSearchText = "gasa";
        Settle();
        var catalog = PumpUntilFound<TPVC.Views.CatalogBrowserSheetView>(v => v.IsVisible);
        var catalogBox = catalog?.FindName("CatalogBrowserSearchTextBox") as System.Windows.Controls.TextBox;
        if (Check(catalogBox != null, "Catálogo: no se encontró el buscador"))
        {
            var count = _records.SelectedPatient!.ActiveSupplies.Count;
            PressKey(catalogBox!, System.Windows.Input.Key.Enter);
            Check(PumpUntil(() => !_records.CatalogBrowser.IsCatalogBrowserModalOpen && _records.SelectedPatient!.ActiveSupplies.Count == count + 1),
                "Catálogo: Enter en la búsqueda agrega el primero y cierra el catálogo");
        }
        Settle();

        // Lo ya usado: piezas a mano con Enter, + y − en su renglón (como la cuenta del Punto de venta)
        var used = _records.SelectedPatient!.ActiveSupplies.LastOrDefault();
        System.Windows.Controls.Control? RowControl(string name) => used == null ? null : FindDescendant<System.Windows.Controls.Control>(sheet!,
            c => System.Windows.Automation.AutomationProperties.GetName(c) == name && ReferenceEquals(c.DataContext, used));
        if (Check(RowControl("Piezas usadas") is System.Windows.Controls.TextBox, "Insumos: no se encontró la cantidad del renglón"))
        {
            var quantityBox = (System.Windows.Controls.TextBox)RowControl("Piezas usadas")!;
            // Como al teclear: SetCurrentValue conserva el binding (asignar Text lo quitaría)
            quantityBox.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "2");
            Check(PressKey(quantityBox, System.Windows.Input.Key.Enter), "Insumos: Enter en la cantidad se atiende");
            Check(PumpUntil(() => used!.Quantity == 2), "Insumos: Enter aplica las piezas escritas");
            var plus = RowControl("Una pieza más") as System.Windows.Controls.Button;
            var minus = RowControl("Una pieza menos") as System.Windows.Controls.Button;
            if (Check(plus?.Command != null && minus?.Command != null, "Insumos: + y − del renglón sin comando"))
            {
                plus!.Command.Execute(plus.CommandParameter);
                Check(PumpUntil(() => used!.Quantity == 3) && quantityBox.Text == "3", "Insumos: + suma una pieza y la cantidad se ve");
                minus!.Command.Execute(minus.CommandParameter);
                Check(PumpUntil(() => used!.Quantity == 2) && quantityBox.Text == "2", "Insumos: − resta una pieza y la cantidad se ve");
            }
        }

        Check(PressEscape() && !_records.Supplies.IsSuppliesModalOpen, "Esc cierra los insumos");
    }

    private void ServicesSheet()
    {
        WaitFor(_records.ClinicalServices.OpenServicesModalAsync());
        Settle();
        var sheet = PumpUntilFound<TPVC.Views.ServicesSheetView>(v => v.IsVisible);
        var box = sheet?.FindName("ServiceModalSearchTextBox") as System.Windows.Controls.TextBox;
        if (!Check(box != null, "Servicios: no se encontró el buscador")) return;

        var suggestions = _records.ClinicalServices.ServiceModalSuggestions.ToList();
        var before = _records.SelectedPatient!.ActiveServices.Count;
        if (Check(suggestions.Count > 1, "Servicios: se esperaban al menos dos sugerencias"))
        {
            Check(PressKey(box!, System.Windows.Input.Key.Down) && ReferenceEquals(_records.ClinicalServices.SelectedServiceModalItem, suggestions[1]),
                "Servicios: ↓ en el buscador elige la siguiente sugerencia");
            Check(PressKey(box!, System.Windows.Input.Key.Enter), "Servicios: Enter en el buscador se atiende");
            Check(PumpUntil(() => _records.SelectedPatient!.ActiveServices.Count == before + 1), "Servicios: Enter agrega la sugerencia elegida");
        }
        Settle();
        Check(PressEscape() && !_records.ClinicalServices.IsServicesModalOpen, "Esc cierra los servicios");
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
