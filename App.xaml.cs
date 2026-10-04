using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TPVC.Data;
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

        // Base de Datos SQLite
        services.AddDbContext<AppDbContext>();

        // Servicios Core
        services.AddSingleton<ThemeService>();
        services.AddSingleton<InventoryService>();
        services.AddSingleton<ConsultationService>();
        services.AddSingleton<AppUpdateService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<InventoryViewModel>();
        services.AddTransient<BatchesViewModel>();
        services.AddTransient<MovementsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ProductDialogViewModel>();

        // Vistas principales
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Inicializar y sembrar base de datos local SQLite si es necesario
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DatabaseInitializer.Initialize(db);
        }

        // Cargar y aplicar configuración de temas guardada
        var themeService = Services.GetRequiredService<ThemeService>();
        themeService.LoadTheme();

        // Mostrar la ventana principal
        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
}
