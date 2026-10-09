using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPVC.Data;

namespace TPVC.Services;

public static class ServiceRegistration
{
    /// <summary>
    /// Servicios de dominio y datos (sin tipos WPF, para poder validarlos en pruebas).
    /// </summary>
    public static IServiceCollection AddTpvcCoreServices(this IServiceCollection services, string? connectionString = null)
    {
        // Base de Datos SQLite: un DbContext de vida corta por operación
        services.AddDbContextFactory<AppDbContext>(options => AppDbContext.ConfigureSqlite(options, connectionString));

        // Constructor explícito: AddDbContextFactory también registra AppDbContext y
        // InventoryService tiene un constructor de compatibilidad que lo recibe.
        services.AddSingleton(sp => new InventoryService(sp.GetRequiredService<IDbContextFactory<AppDbContext>>()));
        services.AddSingleton(_ => new ConsultationService(seedDemoPatients: false));
        services.AddSingleton(_ => new ConsultationDraftStore());
        services.AddSingleton(_ => new LocalPreferencesStore());
        services.AddSingleton(_ => new NotificationService());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<AppUpdateService>();
        services.AddSingleton(_ => new BackupService());
        services.AddSingleton<ClinicProfileService>();
        services.AddSingleton<PrintingService>();
        services.AddSingleton<TursoSettingsService>();
        services.AddSingleton(sp => new TursoSyncService(
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
            sp.GetRequiredService<TursoSettingsService>(),
            sp.GetRequiredService<ClinicProfileService>(),
            sp.GetRequiredService<InventoryService>()));
        services.AddSingleton(sp => new ConsultationHandoffService(
            sp.GetRequiredService<TursoSyncService>(),
            sp.GetRequiredService<TursoSettingsService>()));
        services.AddSingleton(sp => new DataImportService(
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
            sp.GetRequiredService<BackupService>(),
            sp.GetRequiredService<ClinicProfileService>(),
            sp.GetRequiredService<InventoryService>(),
            sp.GetRequiredService<TursoSettingsService>()));

        return services;
    }
}
