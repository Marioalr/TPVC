using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPVC.Data;
using TPVC.Helpers;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC;

public partial class App : Application
{
    public IServiceProvider Services { get; }

    public App()
    {
        Services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddTpvcCoreServices();

        // ViewModels y vistas principales
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Apagado o cierre de sesión de Windows: no perder lo último escrito en la consulta
        FlushConsultationDraft();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FlushConsultationDraft();
        base.OnExit(e);
    }

    private void FlushConsultationDraft()
    {
        try
        {
            (MainWindow?.DataContext as MainViewModel)?.MedicalRecordsVM.FlushConsultationDraft();
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo guardar la consulta en curso al cerrar", ex);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterGlobalErrorHandlers();
        DialogService.ConfirmHandler = (message, title) =>
            MessageBox.Show(MainWindow ?? Current.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        DialogService.ErrorHandler = (message, title) =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

        AppLogger.Info($"Inicio de TPVC v{Services.GetRequiredService<AppUpdateService>().CurrentVersion}");

        var profileService = Services.GetRequiredService<ClinicProfileService>();
        bool isFirstRun = profileService.IsFirstRun;

        // Inicializar, migrar y sembrar base de datos local SQLite si es necesario
        try
        {
            var factory = Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            using var db = factory.CreateDbContext();
            DatabaseInitializer.Initialize(db, seedDemoData: !isFirstRun);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo inicializar la base de datos", ex);
            MessageBox.Show(
                $"No se pudo abrir la base de datos del consultorio.\n\n{ex.Message}\n\nRuta: {AppDbContext.DbPath}\nRespaldos: {Services.GetRequiredService<BackupService>().BackupDirectory}",
                "TPVC - Error de base de datos", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Cargar y aplicar configuración de temas guardada
        var themeService = Services.GetRequiredService<ThemeService>();
        themeService.LoadTheme();

        // Mostrar la ventana principal
        var mainWindow = Services.GetRequiredService<MainWindow>();
        TitleBar.Follow(mainWindow, themeService);
        mainWindow.Show();

        // Respaldo diario y limpieza de bitácoras en segundo plano (no retrasa el arranque)
        var backupService = Services.GetRequiredService<BackupService>();
        _ = Task.Run(() =>
        {
            try
            {
                backupService.EnsureDailyBackup();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Fallo el respaldo automático diario", ex);
            }
            AppLogger.PurgeOldLogs();
        });
    }

    private void RegisterGlobalErrorHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLogger.Error("Excepción no controlada (AppDomain)", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Excepción no observada en tarea", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Mantener la app abierta: una consulta en curso no debe perderse por un error de UI
        AppLogger.Error("Excepción no controlada en la interfaz", e.Exception);
        MessageBox.Show(
            $"Ocurrió un error inesperado, pero la aplicación sigue en funcionamiento.\n\n{e.Exception.Message}\n\nDetalle guardado en: {AppLogger.LogDirectory}",
            "TPVC - Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
