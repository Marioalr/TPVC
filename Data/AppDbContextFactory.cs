using Microsoft.EntityFrameworkCore;

namespace TPVC.Data;

/// <summary>
/// Fábrica simple a partir de opciones ya construidas (usada por pruebas y por el
/// constructor de compatibilidad de InventoryService).
/// </summary>
public sealed class AppDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;

    public AppDbContextFactory()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder);
        _options = builder.Options;
    }

    public AppDbContextFactory(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    public AppDbContext CreateDbContext() => new(_options);
}
