using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;

namespace TPVC.Data;

public static class DatabaseInitializer
{
    public static void Initialize(AppDbContext context)
    {
        // Asegura que las tablas existan
        context.Database.EnsureCreated();

        // Migración preventiva segura para SQLite en caso de bases de datos preexistentes
        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE Products ADD COLUMN Price TEXT NOT NULL DEFAULT '0';");
        }
        catch
        {
            // Columna ya existe o base nueva
        }

        try
        {
            context.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ClinicalServices (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Code TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Price TEXT NOT NULL,
                    EstimatedDurationMinutes INTEGER NOT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ClinicalServices_Code ON ClinicalServices (Code);
                CREATE INDEX IF NOT EXISTS IX_ClinicalServices_Name ON ClinicalServices (Name);

                CREATE TABLE IF NOT EXISTS ServiceDefaultProducts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ClinicalServiceId INTEGER NOT NULL,
                    ProductId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL,
                    Instructions TEXT NULL,
                    FOREIGN KEY (ClinicalServiceId) REFERENCES ClinicalServices (Id) ON DELETE CASCADE,
                    FOREIGN KEY (ProductId) REFERENCES Products (Id) ON DELETE RESTRICT
                );
                CREATE INDEX IF NOT EXISTS IX_ServiceDefaultProducts_ClinicalServiceId ON ServiceDefaultProducts (ClinicalServiceId);
                CREATE INDEX IF NOT EXISTS IX_ServiceDefaultProducts_ProductId ON ServiceDefaultProducts (ProductId);
            ");
        }
        catch
        {
        }

        // Si ya hay categorías, solo verificar si faltan los servicios clínicos
        if (context.Categories.Any())
        {
            SeedClinicalServices(context);
            return;
        }

        // Categorías iniciales para Médico General
        var catMedicamentos = new Category
        {
            Name = "Medicamentos y Fármacos",
            Description = "Analgésicos, antibióticos, antipiréticos, antihistamínicos",
            Icon = "Pill"
        };
        var catCuracion = new Category
        {
            Name = "Material de Curación",
            Description = "Gasas, vendas, algodón, apósitos, cinta micropore",
            Icon = "Bandage"
        };
        var catInsumos = new Category
        {
            Name = "Insumos Clínicos Descartables",
            Description = "Jeringas, agujas, guantes, abatelenguas, cubrebocas",
            Icon = "Syringe"
        };
        var catAntisepticos = new Category
        {
            Name = "Antisépticos y Soluciones",
            Description = "Alcohol etílico, solución fisiológica, yodopovidona, jabón quirúrgico",
            Icon = "Flask"
        };
        var catDiagnostico = new Category
        {
            Name = "Diagnóstico y Exploración",
            Description = "Tiras reactivas de glucosa, lancetas, termómetros, abatelenguas estériles",
            Icon = "Stethoscope"
        };

        context.Categories.AddRange(catMedicamentos, catCuracion, catInsumos, catAntisepticos, catDiagnostico);
        context.SaveChanges();

        // Productos de ejemplo para un consultorio de médico general
        var productos = new List<Product>
        {
            new()
            {
                Code = "MED-001",
                Name = "Paracetamol 500 mg",
                GenericName = "Paracetamol",
                Presentation = "Caja con 20 tabletas",
                Category = catMedicamentos,
                Unit = "Cajas",
                Price = 45.00m,
                CurrentStock = 12,
                MinStock = 5,
                OptimalStock = 25,
                Location = "Botiquín Principal",
                RequiresRefrigeration = false,
                Batches = new List<ProductBatch>
                {
                    new()
                    {
                        BatchNumber = "LOT-PC-2025-A",
                        Quantity = 12,
                        ExpirationDate = DateTime.Today.AddMonths(14),
                        ReceivedDate = DateTime.Today.AddDays(-10)
                    }
                }
            },
            new()
            {
                Code = "MED-002",
                Name = "Amoxicilina / Ácido Clavulánico 500/125 mg",
                GenericName = "Amoxicilina / Clavulanato",
                Presentation = "Caja con 14 tabletas",
                Category = catMedicamentos,
                Unit = "Cajas",
                Price = 160.00m,
                CurrentStock = 3,
                MinStock = 5,
                OptimalStock = 15,
                Location = "Botiquín Principal",
                RequiresRefrigeration = false,
                Batches = new List<ProductBatch>
                {
                    new()
                    {
                        BatchNumber = "LOT-AMX-2026",
                        Quantity = 3,
                        ExpirationDate = DateTime.Today.AddDays(25), // Próximo a vencer para probar alertas
                        ReceivedDate = DateTime.Today.AddMonths(-3)
                    }
                }
            },
            new()
            {
                Code = "CUR-001",
                Name = "Gasa Estéril 10x10 cm",
                GenericName = "Gasa de algodón hidrófilo estéril",
                Presentation = "Paquete con 10 piezas",
                Category = catCuracion,
                Unit = "Paquetes",
                Price = 35.00m,
                CurrentStock = 24,
                MinStock = 10,
                OptimalStock = 40,
                Location = "Cajón de Curaciones",
                RequiresRefrigeration = false
            },
            new()
            {
                Code = "CUR-002",
                Name = "Venda Elástica 10 cm",
                GenericName = "Venda elástica de compresión",
                Presentation = "Pieza individual sellada",
                Category = catCuracion,
                Unit = "Piezas",
                Price = 28.00m,
                CurrentStock = 4,
                MinStock = 6, // Stock bajo para probar alerta
                OptimalStock = 15,
                Location = "Estante B1",
                RequiresRefrigeration = false
            },
            new()
            {
                Code = "INS-001",
                Name = "Jeringa Desechable 5 ml con Aguja 21G",
                GenericName = "Jeringa hipodérmica 3 cuerpos",
                Presentation = "Caja con 100 piezas",
                Category = catInsumos,
                Unit = "Cajas",
                Price = 140.00m,
                CurrentStock = 5,
                MinStock = 2,
                OptimalStock = 10,
                Location = "Estante A2",
                RequiresRefrigeration = false
            },
            new()
            {
                Code = "INS-002",
                Name = "Guantes de Nitrilo (Talla M)",
                GenericName = "Guantes de examen no estériles",
                Presentation = "Caja con 100 piezas",
                Category = catInsumos,
                Unit = "Cajas",
                Price = 120.00m,
                CurrentStock = 2,
                MinStock = 3, // Stock bajo
                OptimalStock = 8,
                Location = "Mesa de Exploración",
                RequiresRefrigeration = false
            },
            new()
            {
                Code = "ANT-001",
                Name = "Alcohol Etílico Desnaturalizado 70%",
                GenericName = "Etanol 70%",
                Presentation = "Botella de 1000 ml",
                Category = catAntisepticos,
                Unit = "Frascos",
                Price = 50.00m,
                CurrentStock = 6,
                MinStock = 2,
                OptimalStock = 8,
                Location = "Repisa Antisépticos",
                RequiresRefrigeration = false
            },
            new()
            {
                Code = "ANT-002",
                Name = "Solución Fisiológica 0.9%",
                GenericName = "Cloruro de sodio 0.9%",
                Presentation = "Matraz / Bolsa 250 ml",
                Category = catAntisepticos,
                Unit = "Frascos",
                Price = 38.00m,
                CurrentStock = 8,
                MinStock = 3,
                OptimalStock = 12,
                Location = "Estante A1",
                RequiresRefrigeration = false
            },
            new()
            {
                Code = "DIA-001",
                Name = "Tiras Reactivas para Glucosa Capilar",
                GenericName = "Tiras para glucómetro Accu-Chek",
                Presentation = "Frasco con 50 tiras",
                Category = catDiagnostico,
                Unit = "Frascos",
                Price = 295.00m,
                CurrentStock = 3,
                MinStock = 2,
                OptimalStock = 6,
                Location = "Maletín de Diagnóstico",
                RequiresRefrigeration = false,
                Batches = new List<ProductBatch>
                {
                    new()
                    {
                        BatchNumber = "LOT-GLU-99",
                        Quantity = 3,
                        ExpirationDate = DateTime.Today.AddMonths(8),
                        ReceivedDate = DateTime.Today.AddDays(-20)
                    }
                }
            }
        };

        context.Products.AddRange(productos);
        context.SaveChanges();

        // Sembrar movimientos iniciales de apertura
        foreach (var p in productos)
        {
            context.InventoryMovements.Add(new InventoryMovement
            {
                ProductId = p.Id,
                MovementType = MovementType.EntradaAjuste,
                Quantity = p.CurrentStock,
                StockBefore = 0,
                StockAfter = p.CurrentStock,
                Date = DateTime.Now.AddDays(-10),
                Reason = "Inventario inicial de apertura de consultorio",
                Responsible = "Dr. Titular"
            });
        }
        context.SaveChanges();

        SeedClinicalServices(context);
    }

    public static void SeedClinicalServices(AppDbContext context)
    {
        if (context.ClinicalServices.Any())
        {
            return;
        }

        var gasa = context.Products.FirstOrDefault(p => p.Name.Contains("Gasa"));
        var solucion = context.Products.FirstOrDefault(p => p.Name.Contains("Solución") || p.Name.Contains("Fisiológica"));
        var venda = context.Products.FirstOrDefault(p => p.Name.Contains("Venda"));
        var alcohol = context.Products.FirstOrDefault(p => p.Name.Contains("Alcohol"));
        var tiras = context.Products.FirstOrDefault(p => p.Name.Contains("Tiras") || p.Name.Contains("Glucosa"));

        var services = new List<ClinicalService>
        {
            new()
            {
                Code = "SERV-001",
                Name = "Consulta Médica General",
                Description = "Evaluación clínica general, exploración física, diagnóstico y prescripción médica.",
                Category = "Consulta",
                Price = 300.00m,
                EstimatedDurationMinutes = 25,
                IsActive = true,
                DefaultProducts = new List<ServiceDefaultProduct>()
            },
            new()
            {
                Code = "SERV-002",
                Name = "Curación Menor y Asepsia",
                Description = "Aseo quirúrgico superficial, desinfección, curación de herida o quemadura y vendaje de protección.",
                Category = "Curaciones",
                Price = 250.00m,
                EstimatedDurationMinutes = 20,
                IsActive = true,
                DefaultProducts = new List<ServiceDefaultProduct>()
            },
            new()
            {
                Code = "SERV-003",
                Name = "Sutura de Herida Simple",
                Description = "Afrontamiento por planos de bordes de herida dérmica limpia con material estéril.",
                Category = "Procedimientos",
                Price = 450.00m,
                EstimatedDurationMinutes = 35,
                IsActive = true,
                DefaultProducts = new List<ServiceDefaultProduct>()
            },
            new()
            {
                Code = "SERV-004",
                Name = "Monitoreo de Glucemia Capilar",
                Description = "Toma de muestra capilar y determinación cuantitativa inmediata de glucosa en sangre.",
                Category = "Diagnóstico",
                Price = 80.00m,
                EstimatedDurationMinutes = 10,
                IsActive = true,
                DefaultProducts = new List<ServiceDefaultProduct>()
            },
            new()
            {
                Code = "SERV-005",
                Name = "Certificado Médico Oficial",
                Description = "Examen médico integral, somatometría y expedición de certificado de salud.",
                Category = "Certificados",
                Price = 200.00m,
                EstimatedDurationMinutes = 20,
                IsActive = true,
                DefaultProducts = new List<ServiceDefaultProduct>()
            }
        };

        // Asignar insumos por defecto a los servicios correspondientes si existen los productos
        if (gasa != null)
        {
            services[1].DefaultProducts.Add(new ServiceDefaultProduct { ProductId = gasa.Id, Quantity = 2, Instructions = "Limpieza y cobertura" });
            services[2].DefaultProducts.Add(new ServiceDefaultProduct { ProductId = gasa.Id, Quantity = 2, Instructions = "Campo y secado" });
        }
        if (solucion != null)
        {
            services[1].DefaultProducts.Add(new ServiceDefaultProduct { ProductId = solucion.Id, Quantity = 1, Instructions = "Lavado de arrastre" });
            services[2].DefaultProducts.Add(new ServiceDefaultProduct { ProductId = solucion.Id, Quantity = 1, Instructions = "Lavado pre-sutura" });
        }
        if (venda != null)
        {
            services[1].DefaultProducts.Add(new ServiceDefaultProduct { ProductId = venda.Id, Quantity = 1, Instructions = "Vendaje protector" });
        }
        if (alcohol != null)
        {
            services[0].DefaultProducts.Add(new ServiceDefaultProduct { ProductId = alcohol.Id, Quantity = 1, Instructions = "Asepsia" });
            services[3].DefaultProducts.Add(new ServiceDefaultProduct { ProductId = alcohol.Id, Quantity = 1, Instructions = "Asepsia digital" });
        }
        if (tiras != null)
        {
            services[3].DefaultProducts.Add(new ServiceDefaultProduct { ProductId = tiras.Id, Quantity = 1, Instructions = "Medición" });
        }

        context.ClinicalServices.AddRange(services);
        context.SaveChanges();
    }
}

