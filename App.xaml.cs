using System;
using System.Diagnostics;
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
        // Confirmaciones y errores con la ventana de la app, que sigue el tema ("No" por defecto, como
        // antes). Sin la ventana principal cargada o desde otro hilo, con el MessageBox de Windows
        // (MainWindow solo se puede leer desde el hilo de la interfaz)
        ThemedDialogs.Install(() => Dispatcher.CheckAccess() ? MainWindow : null);

        AppLogger.Info($"Inicio de TPVC v{Services.GetRequiredService<AppUpdateService>().CurrentVersion}");

        // Inicializar, migrar y sembrar el catálogo de una base nueva. Los datos demo solo salen del asistente inicial
        // (antes, cualquier arranque con la tabla de pacientes vacía sembraba los pacientes demo; paso 8.8)
        try
        {
            var factory = Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            using var db = factory.CreateDbContext();
            DatabaseInitializer.Initialize(db, seedDemoData: false);
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
        mainWindow.ContentRendered += LogStartupTime;
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

    /// <summary>
    /// Deja en la bitácora cuánto tardó en verse la ventana desde que Windows inició el proceso
    /// (y la memoria en ese momento): sirve para comparar versiones y diagnosticar equipos lentos.
    /// </summary>
    private static void LogStartupTime(object? sender, EventArgs e)
    {
        if (sender is Window window) window.ContentRendered -= LogStartupTime;
        try
        {
            using var process = Process.GetCurrentProcess();
            var elapsed = DateTime.Now - process.StartTime;
            AppLogger.Info($"Arranque: ventana lista en {elapsed.TotalMilliseconds:0} ms · memoria {process.WorkingSet64 / 1048576.0:0} MB");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo medir el arranque: {ex.Message}");
        }
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
