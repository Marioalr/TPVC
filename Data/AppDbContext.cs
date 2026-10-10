using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.Data;

public class AppDbContext : DbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ProductBatch> ProductBatches => Set<ProductBatch>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<ClinicalService> ClinicalServices => Set<ClinicalService>();
    public DbSet<ServiceDefaultProduct> ServiceDefaultProducts => Set<ServiceDefaultProduct>();
    public DbSet<PatientRecord> Patients => Set<PatientRecord>();
    public DbSet<PatientConsultation> PatientConsultations => Set<PatientConsultation>();
    public DbSet<PatientAppointment> Appointments => Set<PatientAppointment>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<SyncTombstone> SyncTombstones => Set<SyncTombstone>();
    public DbSet<PrescriptionTemplate> PrescriptionTemplates => Set<PrescriptionTemplate>();
    public DbSet<PediatricDoseRule> PediatricDoseRules => Set<PediatricDoseRule>();

    private static readonly SqlitePoolOpenGuard PoolOpenGuard = new();
    private static readonly SqlitePragmaInterceptor PragmaInterceptor = new();

    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public static string DataDirectory { get; } = GetDataDirectory();

    public static string DbPath { get; } = Path.Combine(DataDirectory, "consultorio.db");

    public static string ConnectionString => $"Data Source={DbPath}";

    private static string GetDataDirectory()
    {
        // Guardar en AppData/Local/TPVC para garantizar permisos de escritura seguros
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "TPVC");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Configuración común de SQLite (usada por la app y por el DbContextFactory).
    /// </summary>
    public static void ConfigureSqlite(DbContextOptionsBuilder optionsBuilder, string? connectionString = null)
    {
        optionsBuilder
            .UseSqlite(connectionString ?? ConnectionString)
            .AddInterceptors(PoolOpenGuard, PragmaInterceptor);
    }

    /// <summary>
    /// Libera las conexiones del pool (p.ej. antes de reemplazar el archivo de la base). Usar en lugar de
    /// SqliteConnection.ClearAllPools: se serializa con las aperturas (ver <see cref="SqlitePoolOpenGuard"/>).
    /// </summary>
    public static void ClearConnectionPools() => SqlitePoolOpenGuard.ClearAllPools();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            ConfigureSqlite(optionsBuilder);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampSyncMetadata();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampSyncMetadata();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Asigna SyncId / UpdatedAtUtc y registra tombstones de borrados físicos,
    /// dejando la base lista para una sincronización incremental con servidor.
    /// </summary>
    /// <summary>
    /// true mientras se aplican cambios descargados de la nube: se conservan SyncId y
    /// UpdatedAtUtc remotos y no se generan tombstones. Sin esto, cada registro recibido
    /// parecería un cambio local nuevo y se volvería a subir (ciclo infinito entre dispositivos).
    /// </summary>
    public bool ApplyingRemoteChanges { get; set; }

    private void StampSyncMetadata()
    {
        if (ApplyingRemoteChanges) return;

        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ISyncEntity>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.SyncId == Guid.Empty) entry.Entity.SyncId = Guid.NewGuid();
                    entry.Entity.UpdatedAtUtc = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = AfterStoredStamp(entry, now);
                    break;
                case EntityState.Deleted:
                    if (entry.Entity.SyncId != Guid.Empty)
                    {
                        SyncTombstones.Add(new SyncTombstone
                        {
                            EntityType = entry.Metadata.ClrType.Name,
                            SyncId = entry.Entity.SyncId,
                            DeletedAtUtc = AfterStoredStamp(entry, now)
                        });
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// La marca de un cambio siempre queda después de la que ya tenía el registro. Si la última
    /// versión vino de un equipo con el reloj adelantado (p. ej. la tablet que mandó la consulta),
    /// una edición hecha aquí "después" tendría una marca anterior y la nube la descartaría.
    /// </summary>
    private static DateTime AfterStoredStamp(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<ISyncEntity> entry, DateTime now)
    {
        var stored = (DateTime)entry.Property(nameof(ISyncEntity.UpdatedAtUtc)).OriginalValue!;
        return SyncRules.NextLocalStamp(now, stored, TimeSpan.Zero);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Índices de sincronización (SyncId único + UpdatedAtUtc para deltas)
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ISyncEntity).IsAssignableFrom(t.ClrType)))
        {
            modelBuilder.Entity(entityType.ClrType).HasIndex(nameof(ISyncEntity.SyncId)).IsUnique();
            modelBuilder.Entity(entityType.ClrType).HasIndex(nameof(ISyncEntity.UpdatedAtUtc));
        }

        // Indices
        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Code);

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Name);

        modelBuilder.Entity<ProductBatch>()
            .HasIndex(b => b.ExpirationDate);

        modelBuilder.Entity<InventoryMovement>()
            .HasIndex(m => m.Date);

        // Relaciones
        modelBuilder.Entity<Product>()
            .HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ProductBatch>()
            .HasOne(b => b.Product)
            .WithMany(p => p.Batches)
            .HasForeignKey(b => b.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<InventoryMovement>()
            .HasOne(m => m.Product)
            .WithMany(p => p.Movements)
            .HasForeignKey(m => m.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<InventoryMovement>()
            .HasOne(m => m.Batch)
            .WithMany()
            .HasForeignKey(m => m.BatchId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ClinicalService>()
            .HasIndex(s => s.Code);

        modelBuilder.Entity<ClinicalService>()
            .HasIndex(s => s.Name);

        modelBuilder.Entity<ServiceDefaultProduct>()
            .HasOne(sp => sp.ClinicalService)
            .WithMany(s => s.DefaultProducts)
            .HasForeignKey(sp => sp.ClinicalServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ServiceDefaultProduct>()
            .HasOne(sp => sp.Product)
            .WithMany()
            .HasForeignKey(sp => sp.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Expedientes Clínicos y Consultas (NOM-004-SSA3)
        modelBuilder.Entity<PatientRecord>()
            .HasIndex(p => p.RecordNumber)
            .IsUnique();

        modelBuilder.Entity<PatientRecord>()
            .HasIndex(p => p.FullName);

        modelBuilder.Entity<PatientRecord>()
            .Ignore(p => p.ActivePrescription)
            .Ignore(p => p.ActiveSupplies)
            .Ignore(p => p.ActiveServices)
            .Ignore(p => p.HasAllergies);

        modelBuilder.Entity<PatientConsultation>()
            .HasIndex(c => c.Folio);

        modelBuilder.Entity<PatientConsultation>()
            .HasIndex(c => c.Date);

        modelBuilder.Entity<PatientConsultation>()
            .HasOne(c => c.PatientRecord)
            .WithMany(p => p.Consultations)
            .HasForeignKey(c => c.PatientRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PatientConsultation>()
            .Ignore(c => c.Prescriptions);

        modelBuilder.Entity<PatientAppointment>()
            .HasIndex(a => a.Date);

        modelBuilder.Entity<PatientAppointment>()
            .HasOne(a => a.PatientRecord)
            .WithMany(p => p.Appointments)
            .HasForeignKey(a => a.PatientRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ventas persistidas (tickets)
        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.Folio)
            .IsUnique();

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.Date);

        modelBuilder.Entity<SaleItem>()
            .HasOne(i => i.Sale)
            .WithMany(s => s.Items)
            .HasForeignKey(i => i.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SyncTombstone>()
            .HasIndex(t => t.DeletedAtUtc);

        // Plantillas de receta importadas del Excel del consultorio
        modelBuilder.Entity<PrescriptionTemplate>()
            .HasIndex(t => new { t.Kind, t.SortOrder });
    }
}

/// <summary>
/// Evita que el pool de Microsoft.Data.Sqlite (10.0.12 y anteriores) preste la misma conexión nativa a
/// dos DbContext a la vez. Al sacar una conexión del pool, SqliteConnectionInternal.Activate la marca
/// como activa antes de registrar a su dueño, fuera del candado del pool; si en ese instante otro hilo
/// abre, encuentra el pool vacío y ejecuta ReclaimLeakedConnections, la toma por "fugada", la devuelve
/// al pool y otra apertura la recibe mientras la primera la sigue usando. Síntoma típico al cargar
/// varias consultas en paralelo: SQLite Error 5 al abrir o cerrar ("unable to delete/modify collation
/// sequence|user-function due to active statements" o "database is locked"), y con escrituras,
/// transacciones mezcladas entre contextos.
/// Corregido en dotnet/efcore#39012, aún sin versión 10.0.x publicada. Serializar la apertura física
/// (sacar del pool y registrar las funciones de EF: microsegundos) cierra la ventana sin renunciar al
/// pool; las consultas siguen en paralelo. Se puede quitar al actualizar a una versión con la corrección.
/// </summary>
internal sealed class SqlitePoolOpenGuard : DbConnectionInterceptor
{
    private static readonly object OpenGate = new();

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (result.IsSuppressed)
        {
            return result;
        }

        lock (OpenGate)
        {
            connection.Open();
        }

        // EF ya no la vuelve a abrir, pero sí llama a ConnectionOpened (pragmas)
        return InterceptionResult.Suppress();
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        // SqliteConnection no tiene apertura asíncrona: su OpenAsync ya ejecuta Open() en el mismo hilo
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ConnectionOpening(connection, eventData, result));
    }

    /// <summary>
    /// ClearAllPools también ejecuta ReclaimLeakedConnections (y desecha lo que reclama), así que comparte el candado.
    /// </summary>
    public static void ClearAllPools()
    {
        lock (OpenGate)
        {
            SqliteConnection.ClearAllPools();
        }
    }
}

/// <summary>
/// Ajustes por conexión: synchronous=NORMAL es seguro con WAL y reduce fsyncs en cada guardado;
/// busy_timeout evita errores "database is locked" si otro proceso (p.ej. un futuro agente de
/// sincronización) escribe al mismo tiempo.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
