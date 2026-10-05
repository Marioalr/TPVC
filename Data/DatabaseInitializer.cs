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
            context.Database.ExecuteSqlRaw("ALTER TABLE Patients ADD COLUMN ActiveSuppliesJson TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
        }

        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE Patients ADD COLUMN ActiveServicesJson TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
        }

        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE Patients ADD COLUMN NextAppointmentDate TEXT NULL;");
        }
        catch
        {
        }

        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE Patients ADD COLUMN NextAppointmentTime TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
        }

        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE Patients ADD COLUMN NextAppointmentReason TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
        }

        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE Patients ADD COLUMN NextAppointmentNotes TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
        }

        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE PatientConsultations ADD COLUMN StartedAt TEXT NULL;");
        }
        catch
        {
        }

        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE PatientConsultations ADD COLUMN EndedAt TEXT NULL;");
        }
        catch
        {
        }

        try
        {
            context.Database.ExecuteSqlRaw("ALTER TABLE PatientConsultations ADD COLUMN SystemsReview TEXT NOT NULL DEFAULT '';");
        }
        catch
        {
        }

        // Migraciones de historia clínica completa (NOM-004-SSA3)
        string[] newPatientColumns = new[]
        {
            "BirthDate TEXT NULL",
            "Occupation TEXT NOT NULL DEFAULT ''",
            "Religion TEXT NOT NULL DEFAULT ''",
            "FamilyHistory TEXT NOT NULL DEFAULT ''",
            "Surgeries TEXT NOT NULL DEFAULT ''",
            "Transfusions TEXT NOT NULL DEFAULT ''",
            "Hospitalizations TEXT NOT NULL DEFAULT ''",
            "ImmunizationSchedule TEXT NOT NULL DEFAULT ''",
            "MenarcheAge TEXT NOT NULL DEFAULT ''",
            "SexualOnsetAge TEXT NOT NULL DEFAULT ''",
            "SexualPartnersCount TEXT NOT NULL DEFAULT ''",
            "PeriodDuration TEXT NOT NULL DEFAULT ''",
            "IsPeriodHeavy TEXT NOT NULL DEFAULT ''",
            "PapSmear TEXT NOT NULL DEFAULT ''",
            "PregnanciesCount INTEGER NOT NULL DEFAULT 0",
            "CesareansCount INTEGER NOT NULL DEFAULT 0",
            "AbortionsCount INTEGER NOT NULL DEFAULT 0",
            "IsCurrentlyPregnant INTEGER NOT NULL DEFAULT 0",
            "GestationalWeeks TEXT NOT NULL DEFAULT ''",
            "DevelopmentalMilestones TEXT NOT NULL DEFAULT ''",
            "Diet TEXT NOT NULL DEFAULT ''",
            "HousingConditions TEXT NOT NULL DEFAULT ''",
            "Zoonosis TEXT NOT NULL DEFAULT ''"
        };

        foreach (var colDef in newPatientColumns)
        {
            try
            {
                context.Database.ExecuteSqlRaw($"ALTER TABLE Patients ADD COLUMN {colDef};");
            }
            catch
            {
            }
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

                CREATE TABLE IF NOT EXISTS Patients (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RecordNumber TEXT NOT NULL,
                    FullName TEXT NOT NULL,
                    Age INTEGER NOT NULL,
                    Gender TEXT NOT NULL,
                    Phone TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    Address TEXT NOT NULL,
                    BloodType TEXT NOT NULL,
                    Allergies TEXT NOT NULL,
                    ChronicDiseases TEXT NOT NULL,
                    EmergencyContactName TEXT NOT NULL,
                    EmergencyContactPhone TEXT NOT NULL,
                    BirthDate TEXT NULL,
                    Occupation TEXT NOT NULL DEFAULT '',
                    Religion TEXT NOT NULL DEFAULT '',
                    FamilyHistory TEXT NOT NULL DEFAULT '',
                    Surgeries TEXT NOT NULL DEFAULT '',
                    Transfusions TEXT NOT NULL DEFAULT '',
                    Hospitalizations TEXT NOT NULL DEFAULT '',
                    ImmunizationSchedule TEXT NOT NULL DEFAULT '',
                    MenarcheAge TEXT NOT NULL DEFAULT '',
                    SexualOnsetAge TEXT NOT NULL DEFAULT '',
                    SexualPartnersCount TEXT NOT NULL DEFAULT '',
                    PeriodDuration TEXT NOT NULL DEFAULT '',
                    IsPeriodHeavy TEXT NOT NULL DEFAULT '',
                    PapSmear TEXT NOT NULL DEFAULT '',
                    PregnanciesCount INTEGER NOT NULL DEFAULT 0,
                    CesareansCount INTEGER NOT NULL DEFAULT 0,
                    AbortionsCount INTEGER NOT NULL DEFAULT 0,
                    IsCurrentlyPregnant INTEGER NOT NULL DEFAULT 0,
                    GestationalWeeks TEXT NOT NULL DEFAULT '',
                    DevelopmentalMilestones TEXT NOT NULL DEFAULT '',
                    Diet TEXT NOT NULL DEFAULT '',
                    HousingConditions TEXT NOT NULL DEFAULT '',
                    Zoonosis TEXT NOT NULL DEFAULT '',
                    LastVisit TEXT NOT NULL,
                    LastDiagnosis TEXT NOT NULL,
                    VitalSigns TEXT NOT NULL,
                    ClinicalNotes TEXT NOT NULL,
                    ActivePrescriptionJson TEXT NOT NULL,
                    ActiveSuppliesJson TEXT NOT NULL DEFAULT '',
                    ActiveServicesJson TEXT NOT NULL DEFAULT '',
                    NextAppointmentDate TEXT NULL,
                    NextAppointmentTime TEXT NOT NULL DEFAULT '',
                    NextAppointmentReason TEXT NOT NULL DEFAULT '',
                    NextAppointmentNotes TEXT NOT NULL DEFAULT '',
                    IsActive INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Patients_RecordNumber ON Patients (RecordNumber);
                CREATE INDEX IF NOT EXISTS IX_Patients_FullName ON Patients (FullName);

                CREATE TABLE IF NOT EXISTS PatientConsultations (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PatientRecordId INTEGER NOT NULL,
                    Folio TEXT NOT NULL,
                    Date TEXT NOT NULL,
                    StartedAt TEXT NULL,
                    EndedAt TEXT NULL,
                    ChiefComplaint TEXT NOT NULL,
                    VitalSigns TEXT NOT NULL,
                    PhysicalExamination TEXT NOT NULL,
                    SystemsReview TEXT NOT NULL DEFAULT '',
                    Diagnosis TEXT NOT NULL,
                    TreatmentPlan TEXT NOT NULL,
                    Notes TEXT NOT NULL,
                    PrescriptionsJson TEXT NOT NULL,
                    FOREIGN KEY (PatientRecordId) REFERENCES Patients (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_PatientConsultations_Folio ON PatientConsultations (Folio);
                CREATE INDEX IF NOT EXISTS IX_PatientConsultations_Date ON PatientConsultations (Date);
                CREATE INDEX IF NOT EXISTS IX_PatientConsultations_PatientRecordId ON PatientConsultations (PatientRecordId);
            ");
        }
        catch
        {
        }

        // Si ya hay categorías, solo verificar si faltan los servicios clínicos o pacientes
        if (context.Categories.Any())
        {
            SeedClinicalServices(context);
            SeedPatients(context);
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
        SeedPatients(context);
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

