using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;

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

    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public static string DbPath { get; } = GetDatabasePath();

    private static string GetDatabasePath()
    {
        // Guardar en AppData/Local/TPVC para garantizar permisos de escritura seguros
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "TPVC");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return Path.Combine(dir, "consultorio.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={DbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Indices
        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Code);

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Name);

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
    }
}
