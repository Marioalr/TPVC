using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Data;

public static class DatabaseInitializer
{
    public static void Initialize(AppDbContext context)
    {
        // Asegura que las tablas existan
        context.Database.EnsureCreated();

        // Si ya hay categorías, no sembrar
        if (context.Categories.Any())
        {
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
    }
}
