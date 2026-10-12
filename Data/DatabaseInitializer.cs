using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using TPVC.Models;

namespace TPVC.Data;

public static partial class DatabaseInitializer
{
    public static void Initialize(AppDbContext context, bool seedDemoData = true)
    {
        // Asegura que las tablas existan (base nueva)
        context.Database.EnsureCreated();

        // WAL: lecturas concurrentes mientras se escribe y escrituras más rápidas.
        // Es persistente en el archivo, basta con aplicarlo una vez por base.
        try
        {
            context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }
        catch
        {
            // Algunos entornos (bases en memoria / sólo lectura) no admiten WAL
        }

        // Migración aditiva automática para bases preexistentes
        EnsureSchema(context);

        // Una base unida a una nube (paso 8.8) nunca vuelve a sembrar, ni a media descarga: su catálogo es el de la nube
        if (IsCloudJoined(context)) return;

        // El catálogo de una base nueva (categorías y servicios clínicos) lleva SyncId fijos y marca del año 2000: dos
        // equipos que siembran no lo duplican al sincronizar, y cualquier edición o borrado real le gana a una siembra
        // repetida. Los datos demo (productos y pacientes) solo salen del asistente inicial o de las pruebas
        if (!context.Categories.Any()) SeedCategories(context);
        if (seedDemoData) SeedDemoProducts(context);
        SeedClinicalServices(context);
        if (seedDemoData) SeedPatients(context);
    }

    /// <summary>Marca del catálogo sembrado: anterior a cualquier cambio real.</summary>
    public static readonly DateTime SeedStampUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>La base se unió a una nube («Unirse», «Recuperar» o «Reemplazar con la nube», paso 8.8).</summary>
    public static bool IsCloudJoined(AppDbContext context)
    {
        try
        {
            return DbMeta.Get(context, DbMeta.CloudJoined) == "1";
        }
        catch (Exception)
        {
            return false; // Sin DbMeta todavía
        }
    }

    /// <summary>Las categorías del catálogo que siembra una base nueva: nombre, descripción e icono.</summary>
    public static readonly (string Name, string Description, string Icon)[] SeededCategories =
    {
        ("Medicamentos y Fármacos", "Analgésicos, antibióticos, antipiréticos, antihistamínicos", "Pill"),
        ("Material de Curación", "Gasas, vendas, algodón, apósitos, cinta micropore", "Bandage"),
        ("Insumos Clínicos Descartables", "Jeringas, agujas, guantes, abatelenguas, cubrebocas", "Syringe"),
        ("Antisépticos y Soluciones", "Alcohol etílico, solución fisiológica, yodopovidona, jabón quirúrgico", "Flask"),
        ("Diagnóstico y Exploración", "Tiras reactivas de glucosa, lancetas, termómetros, abatelenguas estériles", "Stethoscope")
    };

    /// <summary>Códigos de los servicios clínicos que siembra una base nueva.</summary>
    public static IReadOnlyList<string> SeededServiceCodes => new[] { "SERV-001", "SERV-002", "SERV-003", "SERV-004", "SERV-005" };

    private static void SeedCategories(AppDbContext context)
    {
        foreach (var (name, description, icon) in SeededCategories)
        {
            context.Categories.Add(new Category
            {
                SyncId = Services.SyncIds.Seeded(nameof(Category), name),
                UpdatedAtUtc = SeedStampUtc,
                Name = name,
                Description = description,
                Icon = icon
            });
        }
        SaveSeeded(context);
    }

    /// <summary>Guarda lo sembrado con su marca del año 2000 (sin la marca de ahora). Los triggers lo anotan para subir.</summary>
    private static void SaveSeeded(AppDbContext context)
    {
        context.ApplyingRemoteChanges = true;
        try
        {
            context.SaveChanges();
        }
        finally
        {
            context.ApplyingRemoteChanges = false;
        }
    }

    public static void SeedDemoData(AppDbContext context)
    {
        // Unida a una nube, los datos demo subirían a ella (paso 8.8)
        if (IsCloudJoined(context)) return;

        SeedDemoProducts(context);
        SeedClinicalServices(context);
        SeedPatients(context);
    }

    public static void SeedDemoProducts(AppDbContext context)
    {
        if (context.Products.Any()) return;

        var catMedicamentos = context.Categories.FirstOrDefault(c => c.Name.Contains("Medicamentos"))
            ?? context.Categories.First();
        var catCuracion = context.Categories.FirstOrDefault(c => c.Name.Contains("Curación"))
            ?? catMedicamentos;
        var catInsumos = context.Categories.FirstOrDefault(c => c.Name.Contains("Insumos"))
            ?? catMedicamentos;
        var catAntisepticos = context.Categories.FirstOrDefault(c => c.Name.Contains("Antisépticos"))
            ?? catMedicamentos;
        var catDiagnostico = context.Categories.FirstOrDefault(c => c.Name.Contains("Diagnóstico"))
            ?? catMedicamentos;

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
                UnitCost = 27.00m,
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
                UnitCost = 98.00m,
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
                UnitCost = 18.50m,
                CostExcludesVat = true,
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
                UnitCost = 15.00m,
                CostExcludesVat = true,
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
                UnitCost = 82.00m,
                CostExcludesVat = true,
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
                UnitCost = 70.00m,
                CostExcludesVat = true,
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
                UnitCost = 30.00m,
                CostExcludesVat = true,
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
                UnitCost = 22.00m,
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
                UnitCost = 185.00m,
                CostExcludesVat = true,
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

        // Sembrar movimientos iniciales de apertura: uno por lote y otro sin lote para el resto (paso 8.19), así el libro de
        // existencias cuadra desde el principio
        var openedAt = DateTime.Now.AddDays(-10);
        foreach (var p in productos)
        {
            int stock = 0;
            var parts = p.Batches.Select(b => (Batch: (ProductBatch?)b, b.Quantity))
                .Append((Batch: null, Quantity: p.CurrentStock - p.Batches.Sum(b => b.Quantity)))
                .Where(part => part.Quantity > 0);
            foreach (var (batch, quantity) in parts)
            {
                context.InventoryMovements.Add(new InventoryMovement
                {
                    ProductId = p.Id,
                    BatchId = batch?.Id,
                    MovementType = MovementType.EntradaAjuste,
                    Quantity = quantity,
                    Delta = quantity,
                    StockBefore = stock,
                    StockAfter = stock + quantity,
                    Date = openedAt,
                    Reason = "Inventario inicial de apertura de consultorio",
                    Responsible = "Dr. Titular"
                });
                stock += quantity;
            }
        }
        context.SaveChanges();
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
        void AddDefault(ClinicalService service, Product product, int quantity, string instructions) =>
            service.DefaultProducts.Add(new ServiceDefaultProduct
            {
                SyncId = Services.SyncIds.Seeded(nameof(ServiceDefaultProduct), $"{service.Code}|{product.Code}"),
                UpdatedAtUtc = SeedStampUtc,
                ProductId = product.Id,
                Quantity = quantity,
                Instructions = instructions
            });
        if (gasa != null)
        {
            AddDefault(services[1], gasa, 2, "Limpieza y cobertura");
            AddDefault(services[2], gasa, 2, "Campo y secado");
        }
        if (solucion != null)
        {
            AddDefault(services[1], solucion, 1, "Lavado de arrastre");
            AddDefault(services[2], solucion, 1, "Lavado pre-sutura");
        }
        if (venda != null)
        {
            AddDefault(services[1], venda, 1, "Vendaje protector");
        }
        if (alcohol != null)
        {
            AddDefault(services[0], alcohol, 1, "Asepsia");
            AddDefault(services[3], alcohol, 1, "Asepsia digital");
        }
        if (tiras != null)
        {
            AddDefault(services[3], tiras, 1, "Medición");
        }

        // SyncId fijos y marca del año 2000 (paso 8.8)
        foreach (var service in services)
        {
            service.SyncId = Services.SyncIds.Seeded(nameof(ClinicalService), service.Code);
            service.UpdatedAtUtc = SeedStampUtc;
        }
        context.ClinicalServices.AddRange(services);
        SaveSeeded(context);
    }

    public static void SeedPatients(AppDbContext context)
    {
        if (context.Patients.Any())
        {
            return;
        }

        var patients = new List<PatientRecord>
        {
            new()
            {
                RecordNumber = "EXP-2026-001",
                FullName = "García Morales, Juan Carlos",
                Age = 42,
                Gender = "Masculino",
                Phone = "55 4123 8890",
                Email = "juan.garcia@email.com",
                Address = "Av. Insurgentes Sur 450, Col. Roma Sur, CDMX",
                BloodType = "O+",
                Allergies = "Penicilina (Reacción alérgica severa)",
                ChronicDiseases = "Ninguna relevante",
                EmergencyContactName = "Laura Morales (Esposa)",
                EmergencyContactPhone = "55 4123 8899",
                LastVisit = DateTime.Today.AddDays(-2),
                LastDiagnosis = "Faringoamigdalitis aguda bacteriana + Cefalea tensional",
                VitalSigns = "TA: 120/80 mmHg | FC: 76 lpm | Temp: 37.8 °C | Peso: 78 kg",
                ClinicalNotes = "Paciente masculino acude por odinofagia de 3 días de evolución, hipertermia cuantificada y adenomegalias cervicales dolorosas. Faringe congestiva con placas blanquecinas.",
                ActivePrescription = new List<PatientPrescriptionItem>
                {
                    new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 tableta cada 8 horas por 5 días", RecommendedQuantity = 1 },
                    new() { MedicineName = "Gasa Estéril 10x10 cm", Dosage = "Curación y aseo bucofaríngeo", RecommendedQuantity = 1 }
                },
                IsActive = true,
                CreatedAt = DateTime.Now.AddDays(-60),
                Consultations = new List<PatientConsultation>
                {
                    new()
                    {
                        Folio = "CNS-20260402-001",
                        Date = DateTime.Today.AddDays(-2),
                        StartedAt = DateTime.Today.AddDays(-2).AddHours(10).AddMinutes(15),
                        EndedAt = DateTime.Today.AddDays(-2).AddHours(10).AddMinutes(40),
                        ChiefComplaint = "Dolor de garganta intenso y fiebre",
                        VitalSigns = "TA: 120/80 mmHg | FC: 76 lpm | FR: 18 rpm | Temp: 37.8 °C | Peso: 78 kg | Talla: 1.74 m | IMC: 25.8 (Sobrepeso leve)",
                        PhysicalExamination = "Orofaringe hiperémica, hipertrofia amigdalina grado III con exudado blanquecino en criptas. Adenopatías submandibulares palpables dolorosas.",
                        Diagnosis = "Faringoamigdalitis aguda bacteriana exudativa (J03.9)",
                        TreatmentPlan = "Reposo relativo, hidratación abundante, antibioticoterapia oral, analgésico antipirético.",
                        Notes = "Se explican signos de alarma y acudir a revaloración en 72 hrs.",
                        Prescriptions = new List<PatientPrescriptionItem>
                        {
                            new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 tableta cada 8 horas por 5 días", RecommendedQuantity = 1 },
                            new() { MedicineName = "Gasa Estéril 10x10 cm", Dosage = "Curación y aseo bucofaríngeo", RecommendedQuantity = 1 }
                        }
                    }
                }
            },
            new()
            {
                RecordNumber = "EXP-2026-002",
                FullName = "Rodríguez Silva, María Elena",
                Age = 63,
                Gender = "Femenino",
                Phone = "55 9876 1234",
                Email = "maria.elena@email.com",
                Address = "Calle Durango 128, Col. Condesa, CDMX",
                BloodType = "A+",
                Allergies = "Ninguna conocida",
                ChronicDiseases = "Diabetes Mellitus Tipo 2, Hipertensión Arterial",
                EmergencyContactName = "Carlos Rodríguez (Hijo)",
                EmergencyContactPhone = "55 9876 1235",
                LastVisit = DateTime.Today.AddDays(-6),
                LastDiagnosis = "Control periódico de Hipertensión Arterial y Glucemia",
                VitalSigns = "TA: 135/85 mmHg | FC: 68 lpm | Temp: 36.4 °C | Glucosa: 134 mg/dL",
                ClinicalNotes = "Acude a control mensual. Refiere buena tolerancia al tratamiento actual. Sin sintomatología cardiovascular ni edema en miembros inferiores.",
                ActivePrescription = new List<PatientPrescriptionItem>
                {
                    new() { MedicineName = "Tiras Reactivas para Glucosa Capilar", Dosage = "Monitoreo matutino en ayunas 3 veces por semana", RecommendedQuantity = 1 },
                    new() { MedicineName = "Alcohol Etílico Desnaturalizado 70%", Dosage = "Asepsia para toma de glucosa", RecommendedQuantity = 1 }
                },
                IsActive = true,
                CreatedAt = DateTime.Now.AddDays(-120),
                Consultations = new List<PatientConsultation>
                {
                    new()
                    {
                        Folio = "CNS-20260329-002",
                        Date = DateTime.Today.AddDays(-6),
                        StartedAt = DateTime.Today.AddDays(-6).AddHours(11).AddMinutes(0),
                        EndedAt = DateTime.Today.AddDays(-6).AddHours(11).AddMinutes(28),
                        ChiefComplaint = "Control mensual de enfermedades crónicas",
                        VitalSigns = "TA: 135/85 mmHg | FC: 68 lpm | FR: 16 rpm | Temp: 36.4 °C | Peso: 65 kg | Talla: 1.58 m | IMC: 26.0 (Sobrepeso)",
                        PhysicalExamination = "Paciente orientada, ruidos cardíacos rítmicos sin soplos, campos pulmonares limpios, pulsos periféricos presentes y simétricos.",
                        Diagnosis = "Diabetes mellitus tipo 2 sin complicaciones (E11.9) + Hipertensión esencial (I10)",
                        TreatmentPlan = "Mantener dieta baja en sodio y azúcares refinados, caminata diaria 30 min, monitoreo de glucosa.",
                        Notes = "Cita de seguimiento con estudios de laboratorio en 30 días.",
                        Prescriptions = new List<PatientPrescriptionItem>
                        {
                            new() { MedicineName = "Tiras Reactivas para Glucosa Capilar", Dosage = "Monitoreo matutino en ayunas 3 veces por semana", RecommendedQuantity = 1 },
                            new() { MedicineName = "Alcohol Etílico Desnaturalizado 70%", Dosage = "Asepsia para toma de glucosa", RecommendedQuantity = 1 }
                        }
                    }
                }
            },
            new()
            {
                RecordNumber = "EXP-2026-003",
                FullName = "Hernández López, Sofía",
                Age = 27,
                Gender = "Femenino",
                Phone = "55 3344 5566",
                Email = "sofia.hernandez@email.com",
                Address = "Calzada de Tlalpan 1890, CDMX",
                BloodType = "B+",
                Allergies = "Sulfas",
                ChronicDiseases = "Rinitis alérgica estacional",
                EmergencyContactName = "Patricia López (Madre)",
                EmergencyContactPhone = "55 3344 5560",
                LastVisit = DateTime.Today.AddDays(-14),
                LastDiagnosis = "Traumatismo menor en tobillo derecho (Esguince Grado I)",
                VitalSigns = "TA: 110/70 mmHg | FC: 72 lpm | Temp: 36.6 °C | Peso: 56 kg",
                ClinicalNotes = "Presenta dolor e inflamación leve tras inversión forzada al bajar escalón. Sin deformidad ni pérdida de continuidad ósea a la palpación.",
                ActivePrescription = new List<PatientPrescriptionItem>
                {
                    new() { MedicineName = "Venda Elástica 10 cm", Dosage = "Vendaje compresivo en ocho por 7 días", RecommendedQuantity = 1 },
                    new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 tableta cada 8 horas si hay dolor", RecommendedQuantity = 1 }
                },
                IsActive = true,
                CreatedAt = DateTime.Now.AddDays(-90),
                Consultations = new List<PatientConsultation>
                {
                    new()
                    {
                        Folio = "CNS-20260321-003",
                        Date = DateTime.Today.AddDays(-14),
                        StartedAt = DateTime.Today.AddDays(-14).AddHours(16).AddMinutes(20),
                        EndedAt = DateTime.Today.AddDays(-14).AddHours(16).AddMinutes(45),
                        ChiefComplaint = "Torcedura y dolor en tobillo derecho al bajar escalones",
                        VitalSigns = "TA: 110/70 mmHg | FC: 72 lpm | FR: 16 rpm | Temp: 36.6 °C | Peso: 56 kg | Talla: 1.62 m | IMC: 21.3 (Peso normal)",
                        PhysicalExamination = "Edema leve en maléolo externo derecho, dolor a la inversión forzada, sin crepitación ósea, llenado capilar distal < 2 segundos.",
                        Diagnosis = "Esguince de ligamento lateral de tobillo derecho Grado I (S93.4)",
                        TreatmentPlan = "Reposo, hielo local por 15 min 3 veces al día, vendaje compresivo, elevación de extremidad.",
                        Notes = "Evolución favorable esperada en 7 a 10 días.",
                        Prescriptions = new List<PatientPrescriptionItem>
                        {
                            new() { MedicineName = "Venda Elástica 10 cm", Dosage = "Vendaje compresivo en ocho por 7 días", RecommendedQuantity = 1 },
                            new() { MedicineName = "Paracetamol 500 mg", Dosage = "1 tableta cada 8 horas si hay dolor", RecommendedQuantity = 1 }
                        }
                    }
                }
            },
            new()
            {
                RecordNumber = "EXP-2026-004",
                FullName = "Martínez Castro, Roberto",
                Age = 35,
                Gender = "Masculino",
                Phone = "55 7788 9900",
                Email = "roberto.martinez@email.com",
                Address = "Eje Central Lázaro Cárdenas 55, CDMX",
                BloodType = "O+",
                Allergies = "Ninguna",
                ChronicDiseases = "Ninguna",
                EmergencyContactName = "Mariana Castro (Hermana)",
                EmergencyContactPhone = "55 7788 9911",
                LastVisit = DateTime.Today.AddDays(-21),
                LastDiagnosis = "Curación de herida contuso-cortante en antebrazo izquierdo",
                VitalSigns = "TA: 125/80 mmHg | FC: 78 lpm | Temp: 36.5 °C | Peso: 82 kg",
                ClinicalNotes = "Herida limpia de 3 cm afrontada previamente. Retiro de puntos satisfactorio. Cicatrización de primera intención sin signos de infección.",
                ActivePrescription = new List<PatientPrescriptionItem>
                {
                    new() { MedicineName = "Solución Fisiológica 0.9%", Dosage = "Lavado de herida diario", RecommendedQuantity = 1 },
                    new() { MedicineName = "Gasa Estéril 10x10 cm", Dosage = "Protección de herida", RecommendedQuantity = 1 }
                },
                IsActive = true,
                CreatedAt = DateTime.Now.AddDays(-45),
                Consultations = new List<PatientConsultation>
                {
                    new()
                    {
                        Folio = "CNS-20260314-004",
                        Date = DateTime.Today.AddDays(-21),
                        StartedAt = DateTime.Today.AddDays(-21).AddHours(9).AddMinutes(10),
                        EndedAt = DateTime.Today.AddDays(-21).AddHours(9).AddMinutes(35),
                        ChiefComplaint = "Revisión y retiro de puntos de herida en brazo",
                        VitalSigns = "TA: 125/80 mmHg | FC: 78 lpm | FR: 17 rpm | Temp: 36.5 °C | Peso: 82 kg | Talla: 1.78 m | IMC: 25.9 (Sobrepeso leve)",
                        PhysicalExamination = "Herida de 3 cm en cara anterior de tercio medio de antebrazo izquierdo, bordes afrontados, sin secreción ni eritema circundante.",
                        Diagnosis = "Herida de antebrazo en fase resolutiva (S51.9)",
                        TreatmentPlan = "Retiro de suturas con técnica aséptica, curación tópica y vendaje oclusivo ligero por 48 horas.",
                        Notes = "Alta del procedimiento de sutura con adecuada evolución clínica.",
                        Prescriptions = new List<PatientPrescriptionItem>
                        {
                            new() { MedicineName = "Solución Fisiológica 0.9%", Dosage = "Lavado de herida diario", RecommendedQuantity = 1 },
                            new() { MedicineName = "Gasa Estéril 10x10 cm", Dosage = "Protección de herida", RecommendedQuantity = 1 }
                        }
                    }
                }
            }
        };

        foreach (var p in patients)
        {
            p.SyncPrescriptionJson();
            foreach (var c in p.Consultations)
            {
                c.PrescriptionsJson = System.Text.Json.JsonSerializer.Serialize(c.Prescriptions);
            }
        }

        context.Patients.AddRange(patients);
        context.SaveChanges();
    }
}

