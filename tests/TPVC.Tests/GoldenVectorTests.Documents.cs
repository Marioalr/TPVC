using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TPVC.Models;
using TPVC.Services.Documents;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.11: los vectores que necesita el celular (corte de caja, alertas, compra sugerida, la hoja de la receta, los
/// campos de su HTML y el ticket en texto), generados con el mismo código que usa la PC (Services/Documents y
/// Services/Rules). Reglas al escribirlos: cada redondeo lleva un caso de punto medio, los desempates llevan nombres con
/// acentos (orden de es-MX) y lo que depende de "hoy" se genera en UTC y en America/Mexico_City.
/// </summary>
public partial class GoldenVectorTests
{
    /// <summary>Un instante en que el día cambia según la zona: 12/10/2026 en UTC y 11/10/2026 en la Ciudad de México.</summary>
    private static readonly DateTimeOffset VectorNow = new(2026, 10, 12, 5, 30, 0, TimeSpan.Zero);

    private static readonly CultureInfo EsMx = CultureInfo.GetCultureInfo("es-MX");

    private static readonly (string Name, TimeZoneInfo Zone)[] VectorZones =
    {
        ("UTC", TimeZoneInfo.Utc),
        ("America/Mexico_City", TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"))
    };

    /// <summary>Reloj fijo con la zona del consultorio que se pida (para ClinicClock).</summary>
    private sealed class ZoneClock(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private static DateTime TodayIn(TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(VectorNow, zone).Date;

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string DatedVectorFile(string rule, string? today, string? now, JsonArray cases, params string[] notes) =>
        TPVC.Services.SyncContract.ToStableJson(new JsonObject
        {
            ["rule"] = rule,
            ["culture"] = "es-MX",
            ["today"] = today,
            ["now"] = now,
            ["contract"] = TPVC.Services.SyncContract.Revision,
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["cases"] = cases
        });

    /// <summary>Corre <paramref name="action"/> con es-MX como cultura actual (los formatos que dependen de ella).</summary>
    private static T InMexicanCulture<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = EsMx;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ================================================================== contracts/golden/cash-cut.json

    private static Sale VectorSale(decimal total, string payment, bool cancelled, params (bool Service, int Quantity, decimal Subtotal)[] items) => new()
    {
        Total = total,
        PaymentMethod = payment,
        IsCancelled = cancelled,
        Items = items.Select(i => new SaleItem { IsService = i.Service, Quantity = i.Quantity, Subtotal = i.Subtotal }).ToList()
    };

    private static JsonObject SaleInput(Sale s) => new()
    {
        ["total"] = Money(s.Total),
        ["paymentMethod"] = s.PaymentMethod,
        ["cancelled"] = s.IsCancelled,
        ["items"] = new JsonArray(s.Items.Select(i => (JsonNode)new JsonObject
        {
            ["isService"] = i.IsService, ["quantity"] = i.Quantity, ["subtotal"] = Money(i.Subtotal)
        }).ToArray())
    };

    [TestMethod]
    public void CashCut_MatchesTheVectors()
    {
        var day = new DateTime(2026, 10, 11);
        var scenarios = new (string Name, Sale[] Sales)[]
        {
            ("día sin ventas", Array.Empty<Sale>()),
            ("cada forma de pago, sin distinguir mayúsculas ni el acento", new[]
            {
                VectorSale(350m, "EFECTIVO", false, (true, 1, 350m)),
                VectorSale(120.50m, "Tarjeta de Débito", false, (false, 2, 120.50m)),
                VectorSale(80m, "tarjeta de debito", false, (false, 1, 80m)),
                VectorSale(500m, "Tarjeta de crédito", false, (true, 1, 400m), (false, 4, 100m)),
                VectorSale(99.99m, "Tarjeta de credito", false, (false, 1, 99.99m)),
                VectorSale(1200m, "Transferencia SPEI", false, (true, 2, 1200m)),
                VectorSale(45m, "Vale de despensa", false, (false, 3, 45m))
            }),
            ("la primera forma que aparece gana: «Efectivo y tarjeta de débito» es efectivo", new[]
            {
                VectorSale(200m, "Efectivo y tarjeta de débito", false, (true, 1, 200m))
            }),
            ("las canceladas solo cuentan aparte (ni forma de pago ni servicios contra productos)", new[]
            {
                VectorSale(300m, "Efectivo", false, (true, 1, 300m)),
                VectorSale(150m, "Efectivo", true, (false, 5, 150m)),
                VectorSale(75.25m, "Transferencia", true, (true, 1, 75.25m))
            }),
            ("una venta sin forma de pago cuenta en otros", new[] { VectorSale(10m, "", false, (false, 1, 10m)) })
        };

        var cases = new JsonArray();
        foreach (var (name, sales) in scenarios)
        {
            var cut = CashCutRules.Compute(sales, day);
            cases.Add(Case(name,
                new JsonObject { ["day"] = Day(day), ["sales"] = new JsonArray(sales.Select(s => (JsonNode)SaleInput(s)).ToArray()) },
                new JsonObject
                {
                    ["totalTickets"] = cut.TotalTicketsCount,
                    ["activeTickets"] = cut.ActiveTicketsCount,
                    ["cancelledTickets"] = cut.CancelledTicketsCount,
                    ["totalSales"] = Money(cut.TotalSalesAmount),
                    ["totalCancelled"] = Money(cut.TotalCancelledAmount),
                    ["cash"] = Money(cut.TotalCash),
                    ["debitCard"] = Money(cut.TotalDebitCard),
                    ["creditCard"] = Money(cut.TotalCreditCard),
                    ["transfer"] = Money(cut.TotalTransfer),
                    ["other"] = Money(cut.TotalOtherPaymentMethods),
                    ["servicesAmount"] = Money(cut.TotalServicesAmount),
                    ["servicesCount"] = cut.TotalServicesCount,
                    ["productsAmount"] = Money(cut.TotalProductsAmount),
                    ["productUnits"] = cut.TotalProductsUnitsSold
                }));
        }

        CheckOrWrite("contracts/golden/cash-cut.json", DatedVectorFile("cash-cut", Day(day), null, cases,
            "Services/Rules/CashCutRules.cs. Entran las ventas del día (Sales.Date de 00:00 a 23:59 en la hora del consultorio) con sus renglones.",
            "Forma de pago: el texto en minúsculas sin espacios alrededor; gana la primera regla que se cumple: contiene 'efectivo'; 'débito' o 'debito'; 'crédito' o 'credito'; 'transfer'; si no, otros.",
            "Las canceladas solo suman tickets cancelados y su total; ni forma de pago ni servicios contra productos. Servicios: renglones con isService; cuentan su subtotal y su cantidad.",
            "Importes: decimales exactos en texto invariable (sin redondeo: el corte solo suma)."));
    }

    // ================================================================== contracts/golden/alerts.json

    [TestMethod]
    public void Alerts_MatchTheVectors()
    {
        var cases = new JsonArray();

        // Stock bajo: activo, con mínimo y en el mínimo o menos; del de menos existencias al de más
        var products = new[]
        {
            new Product { Code = "P1", Name = "Paracetamol 500 mg", MinStock = 10, CurrentStock = 4, IsActive = true },
            new Product { Code = "P2", Name = "Ácido fólico 5 mg", MinStock = 5, CurrentStock = 5, IsActive = true },
            new Product { Code = "P3", Name = "Agua inyectable", MinStock = 5, CurrentStock = 6, IsActive = true },
            new Product { Code = "P4", Name = "Amoxicilina 500 mg", MinStock = 0, CurrentStock = 0, IsActive = true },
            new Product { Code = "P5", Name = "Ibuprofeno 400 mg", MinStock = 8, CurrentStock = 0, IsActive = false },
            new Product { Code = "P6", Name = "Omeprazol 20 mg", MinStock = 3, CurrentStock = 0, IsActive = true }
        };
        cases.Add(Case("stock bajo",
            new JsonObject
            {
                ["kind"] = "lowStock",
                ["products"] = new JsonArray(products.Select(p => (JsonNode)new JsonObject
                {
                    ["code"] = p.Code, ["name"] = p.Name, ["active"] = p.IsActive, ["minStock"] = p.MinStock, ["currentStock"] = p.CurrentStock
                }).ToArray())
            },
            new JsonArray(StockAlertRules.LowStock(products).Select(p => (JsonNode)JsonValue.Create(p.Code)!).ToArray())));

        // Caducidad: depende de "hoy", que cambia con la zona
        foreach (var (zoneName, zone) in VectorZones)
        {
            var today = TodayIn(zone);
            var active = new Product { Name = "Activo", IsActive = true };
            var inactive = new Product { Name = "Inactivo", IsActive = false };
            var batches = new (string Batch, DateTime Expiration, int Quantity, bool Depleted, Product Product)[]
            {
                ("VENCIDO-AYER", new DateTime(2026, 10, 10), 5, false, active),
                ("VENCE-11-OCT", new DateTime(2026, 10, 11), 5, false, active),
                ("VENCE-12-OCT", new DateTime(2026, 10, 12), 5, false, active),
                ("VENCE-10-NOV", new DateTime(2026, 11, 10), 5, false, active),
                ("VENCE-11-NOV", new DateTime(2026, 11, 11), 5, false, active),
                ("VENCE-12-NOV", new DateTime(2026, 11, 12), 5, false, active),
                ("AGOTADO", new DateTime(2026, 10, 1), 0, false, active),
                ("MARCADO-AGOTADO", new DateTime(2026, 10, 1), 3, true, active),
                ("DE-INACTIVO", new DateTime(2026, 10, 1), 3, false, inactive)
            };
            var models = batches.Select(b => new ProductBatch
            {
                BatchNumber = b.Batch, ExpirationDate = b.Expiration, Quantity = b.Quantity, IsDepleted = b.Depleted, Product = b.Product
            }).ToList();
            var (expired, near) = StockAlertRules.SplitByExpiration(models, today);
            cases.Add(Case($"caducidad en {zoneName}",
                new JsonObject
                {
                    ["kind"] = "expiration",
                    ["timeZone"] = zoneName,
                    ["now"] = VectorNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["batches"] = new JsonArray(batches.Select(b => (JsonNode)new JsonObject
                    {
                        ["batch"] = b.Batch, ["expiration"] = Day(b.Expiration), ["quantity"] = b.Quantity, ["depleted"] = b.Depleted, ["productActive"] = b.Product.IsActive
                    }).ToArray())
                },
                new JsonObject
                {
                    ["today"] = Day(today),
                    ["expired"] = new JsonArray(expired.Select(b => (JsonNode)JsonValue.Create(b.BatchNumber)!).ToArray()),
                    ["nearExpiry"] = new JsonArray(near.Select(b => (JsonNode)JsonValue.Create(b.BatchNumber)!).ToArray()),
                    // El estado de cada lote en Inventario (otra ventana: 60 días)
                    ["status"] = new JsonObject(models.Select(b => KeyValuePair.Create(b.BatchNumber, (JsonNode?)JsonValue.Create(b.StatusTextOn(today)))))
                }));
        }

        CheckOrWrite("contracts/golden/alerts.json", DatedVectorFile("alerts", null, VectorNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), cases,
            "Services/Rules/StockAlertRules.cs y ProductBatch.StatusTextOn.",
            "Stock bajo: activo, MinStock > 0 y CurrentStock <= MinStock; ordenado por CurrentStock (el orden de los empates no está definido).",
            "Caducidad: cuentan los lotes con piezas, sin marcar como agotados y de un producto activo. Vencido: caduca antes de hoy. Por vencer: de hoy a hoy + 30 días, los dos incluidos. Por fecha de caducidad.",
            "Estado del lote (Inventario): 'Vencido' antes de hoy; 'Vence en N días' de hoy a 60 días; si no, 'Vigente'.",
            "Hoy es la fecha local del consultorio: el mismo instante da 12/10/2026 en UTC y 11/10/2026 en America/Mexico_City (UTC-6, sin horario de verano desde 2022)."));
    }

    // ================================================================== contracts/golden/suggested-purchase.json

    [TestMethod]
    public void SuggestedPurchase_MatchesTheVectors()
    {
        var general = new Category { Name = "Medicamentos" };
        var products = new[]
        {
            new Product { Code = "AGU", Name = "Agua inyectable 5 ml", Price = 10.10m, MinStock = 10, CurrentStock = 3, IsActive = true, Category = general },
            new Product { Code = "ABA", Name = "Abatelenguas", Price = 1m, UnitCost = 0.125m, CostExcludesVat = true, MinStock = 10, CurrentStock = 3, IsActive = true },
            new Product { Code = "ACI", Name = "Ácido fólico 5 mg", Price = 0.30m, MinStock = 10, CurrentStock = 3, IsActive = true, Category = general },
            new Product { Code = "PAR", Name = "Paracetamol 500 mg", Price = 25m, UnitCost = 12.50m, MinStock = 20, CurrentStock = 0, IsActive = true, Category = general },
            new Product { Code = "GAS", Name = "Gasas estériles", Price = 0m, MinStock = 4, CurrentStock = 4, IsActive = true },
            new Product { Code = "OME", Name = "Omeprazol 20 mg", Price = 40m, MinStock = 5, CurrentStock = 6, IsActive = true },
            new Product { Code = "IBU", Name = "Ibuprofeno 400 mg", Price = 30m, MinStock = 5, CurrentStock = 1, IsActive = false },
            new Product { Code = "LOR", Name = "Loratadina 10 mg", Price = 35m, MinStock = 0, CurrentStock = 0, IsActive = true }
        };

        var list = SuggestedPurchaseRules.Build(products);
        var cases = new JsonArray
        {
            Case("lista de productos con stock bajo",
                new JsonObject
                {
                    ["products"] = new JsonArray(products.Select(p => (JsonNode)new JsonObject
                    {
                        ["code"] = p.Code, ["name"] = p.Name, ["category"] = p.Category?.Name, ["active"] = p.IsActive,
                        ["price"] = Money(p.Price), ["unitCost"] = Money(p.UnitCost), ["costExcludesVat"] = p.CostExcludesVat,
                        ["minStock"] = p.MinStock, ["currentStock"] = p.CurrentStock
                    }).ToArray())
                },
                new JsonArray(list.Select(i => (JsonNode)new JsonObject
                {
                    ["code"] = i.ProductCode,
                    ["category"] = i.CategoryName,
                    ["suggestedQuantity"] = i.SuggestedQuantity,
                    ["unitCost"] = Money(i.EstimatedUnitCost),
                    ["costEstimated"] = i.IsCostEstimated,
                    ["totalCost"] = Money(i.EstimatedTotalCost),
                    ["unitCostText"] = i.UnitCostText,
                    ["reason"] = i.Reason
                }).ToArray()))
        };

        CheckOrWrite("contracts/golden/suggested-purchase.json", DatedVectorFile("suggested-purchase", null, null, cases,
            "Services/Rules/SuggestedPurchaseRules.cs. Entran los productos con stock bajo (activo, MinStock > 0 y CurrentStock <= MinStock).",
            "Cantidad: max(2 × MinStock − CurrentStock, MinStock).",
            "Costo: el capturado; si no incluye IVA, × 1.16 redondeado a centavos con el medio hacia arriba (AwayFromZero: 0.145 da 0.15). Sin costo: el precio × 0.65 redondeado a centavos al par (ToEven, HALF_EVEN en Kotlin: 6.565 da 6.56 y 0.195 da 0.20); sin costo ni precio, 25.00. costEstimated dice si salió del precio.",
            "Categoría: 'General' si no tiene. Motivo: 'Agotado en consultorio' con 0; si no, 'Stock bajo (N de M mín.)'.",
            "Orden: CurrentStock y luego el nombre con el orden de es-MX (Collator: Abatelenguas, Ácido fólico, Agua inyectable; por código Unicode, Ácido quedaría al final).",
            "unitCostText: el costo con formato de moneda es-MX ('$6.56'), con '≈ ' delante si es estimado."));
    }

    // ================================================================== contracts/golden/prescription-sheet.json y prescription-html-fields.json

    private sealed record SheetScenario(string Name, PatientRecord Patient, PatientPrescriptionItem[] Items, ClinicProfile Profile, string? Notes, string? VitalSigns);

    private static SheetScenario[] SheetScenarios() => new[]
    {
        new SheetScenario("adulta con alergias, signos completos y presentación",
            new PatientRecord
            {
                FullName = "  María José Pérez López ", BirthDate = new DateTime(1990, 3, 14), Gender = "Femenino", Allergies = "Penicilina, sulfas",
                VitalSigns = "TA: 120/80 mmHg | FC: 76 lpm | Temp: 37.8 °C | Peso: 64.5 kg | Talla: 1.62 m | SpO2: 0.97"
            },
            new[]
            {
                new PatientPrescriptionItem { MedicineName = " Paracetamol 500 mg ", Dosage = "1 tableta cada 8 horas por 3 días", Presentation = "Caja con 20 tabletas" },
                new PatientPrescriptionItem { MedicineName = "Amoxicilina 500 mg", Dosage = " 1 cápsula cada 12 h " },
                new PatientPrescriptionItem { MedicineName = "   ", Dosage = "sin nombre: no sale" }
            },
            new ClinicProfile { DoctorName = "Dra. María del Cielo Alberto Galicia", ProfessionalLicense = " 15632243 ", University = "UNAM", Phone = "55 1234 5678", Address = "Av. Siempre Viva 742, CDMX" },
            "Reposo.\r\nLíquidos abundantes.", null),
        new SheetScenario("bebé de 11 o 12 meses según la zona, alergias negadas y signos con punto medio",
            new PatientRecord { FullName = "Bebé Prueba", BirthDate = new DateTime(2025, 10, 12), Gender = "Masculino", Allergies = "Negadas" },
            new[] { new PatientPrescriptionItem { MedicineName = "Paracetamol gotas 100 mg/ml", Dosage = "0.6 ml cada 6 horas" } },
            new ClinicProfile(),
            null,
            "TA: 95/60 | Temp: 36.25 | Peso: 9.125 | Talla: 0.745 | SpO2: 96.5"),
        new SheetScenario("sin fecha de nacimiento, alergias sin interrogar y sin signos",
            new PatientRecord { FullName = "Juan Sin Fecha", Age = 1 },
            Array.Empty<PatientPrescriptionItem>(),
            new ClinicProfile { DoctorName = "Dr. Prueba" },
            "  ", null)
    };

    private static JsonObject SheetInput(SheetScenario s, string zone) => new()
    {
        ["timeZone"] = zone,
        ["now"] = VectorNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["patient"] = new JsonObject
        {
            ["fullName"] = s.Patient.FullName,
            ["birthDate"] = s.Patient.BirthDate is { } b ? Day(b) : null,
            ["age"] = s.Patient.BirthDate == null ? s.Patient.Age : null,
            ["allergies"] = s.Patient.Allergies,
            ["vitalSigns"] = s.Patient.VitalSigns
        },
        ["consultationVitalSigns"] = s.VitalSigns,
        ["items"] = new JsonArray(s.Items.Select(i => (JsonNode)new JsonObject
        {
            ["medicineName"] = i.MedicineName, ["dosage"] = i.Dosage, ["presentation"] = i.Presentation
        }).ToArray()),
        ["profile"] = new JsonObject
        {
            ["doctorName"] = s.Profile.DoctorName, ["professionalLicense"] = s.Profile.ProfessionalLicense, ["university"] = s.Profile.University,
            ["phone"] = s.Profile.Phone, ["address"] = s.Profile.Address
        },
        ["generalNotes"] = s.Notes
    };

    /// <summary>La hoja con "hoy" en la zona (la edad y la fecha de la receta dependen de él).</summary>
    private static PrescriptionSheet SheetIn(SheetScenario s, TimeZoneInfo zone)
    {
        using (ClinicClock.Use(new ZoneClock(VectorNow, zone)))
        {
            return InMexicanCulture(() => PrescriptionSheet.Create(s.Patient, s.Items, s.Profile, s.Notes, ClinicClock.Today, vitalSigns: s.VitalSigns));
        }
    }

    [TestMethod]
    public void PrescriptionSheet_MatchesTheVectors()
    {
        var cases = new JsonArray();
        foreach (var scenario in SheetScenarios())
        {
            foreach (var (zoneName, zone) in VectorZones)
            {
                var sheet = SheetIn(scenario, zone);
                cases.Add(Case($"{scenario.Name} ({zoneName})", SheetInput(scenario, zoneName), new JsonObject
                {
                    ["doctorName"] = sheet.DoctorName,
                    ["university"] = sheet.University,
                    ["licenseLine"] = sheet.LicenseLine,
                    ["patientName"] = sheet.PatientName,
                    ["age"] = sheet.Age,
                    ["date"] = sheet.Date,
                    ["allergies"] = sheet.Allergies,
                    ["hasAllergies"] = sheet.HasAllergies,
                    ["bloodPressure"] = sheet.BloodPressure,
                    ["temperature"] = sheet.Temperature,
                    ["weight"] = sheet.Weight,
                    ["height"] = sheet.Height,
                    ["oxygenSaturation"] = sheet.OxygenSaturation,
                    ["medicines"] = new JsonArray(sheet.Medicines.Select(m => (JsonNode)new JsonObject
                    {
                        ["medicine"] = m.Medicine, ["detail"] = m.Detail, ["posology"] = m.Posology
                    }).ToArray()),
                    ["generalMeasures"] = sheet.GeneralMeasures,
                    ["phone"] = sheet.Phone,
                    ["address"] = sheet.Address
                }));
            }
        }

        CheckOrWrite("contracts/golden/prescription-sheet.json", DatedVectorFile("prescription-sheet", null, VectorNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), cases,
            "Models/PrescriptionSheet.cs: lo que dice la receta (la misma hoja de la impresión, el HTML y la vista previa). La fecha de la receta es hoy en la zona del consultorio (dd/MM/aaaa).",
            "Edad (PatientRecord.FormatAge): con fecha de nacimiento, meses cumplidos; menos de 1 mes en días, menos de 24 en meses, menos de 6 años en años y meses, después solo años. Sin fecha, la edad guardada en años.",
            "Signos: los de la consulta si los hay; si no, los del expediente, leídos con el mismo analizador. T/A 'S/D mmHg'; temperatura con 1 decimal; peso hasta 2; talla en cm hasta 1 (la talla en metros × 100); SpO2 hasta 1 y como porcentaje si viene como fracción (0.97 = 97 %). Formato de double de .NET con el punto como separador: un punto medio exacto en binario se redondea hacia arriba (36.25 da 36.3 y 9.125 da 9.13) y uno inexacto según su valor binario.",
            "Alergias: cuáles; 'Negadas' si las niega (en rojo solo si tiene); 'No interrogadas' si está vacío. Sin medidas generales, el texto de siempre (PrescriptionDefaults.Indications). Un medicamento sin nombre no sale; el detalle es ' · ' + presentación.",
            "Sin nombre del médico: 'Dr. Titular'. Sin cédula, licenseLine vacío. Todo se recorta de espacios."));
    }

    [TestMethod]
    public void PrescriptionHtmlFields_MatchTheVectors()
    {
        var cases = new JsonArray();
        foreach (var scenario in SheetScenarios())
        {
            var sheet = SheetIn(scenario, VectorZones[1].Zone);
            if (scenario.Name.StartsWith("sin fecha", StringComparison.Ordinal)) sheet.Paper = PrescriptionPaper.Letter;
            var html = PrescriptionHtmlBuilder.Build(sheet);
            var doc = XDocument.Parse(html.Replace("<!DOCTYPE html>", string.Empty));
            bool HasClass(XElement e, string name) => ((string?)e.Attribute("class") ?? string.Empty).Split(' ').Contains(name);

            var fields = new JsonObject();
            foreach (var e in doc.Descendants().Where(e => e.Attribute("data-field") != null))
            {
                fields[(string)e.Attribute("data-field")!] = e.Value;
            }
            var page = Regex.Match(html, @"@page \{ size: ([^;]+);").Groups[1].Value;
            cases.Add(Case(scenario.Name,
                new JsonObject
                {
                    ["paper"] = sheet.Paper == PrescriptionPaper.Letter ? "letter" : "halfLetter",
                    ["sheet"] = new JsonObject
                    {
                        ["patientName"] = sheet.PatientName, ["age"] = sheet.Age, ["date"] = sheet.Date, ["allergies"] = sheet.Allergies,
                        ["hasAllergies"] = sheet.HasAllergies, ["bloodPressure"] = sheet.BloodPressure, ["temperature"] = sheet.Temperature,
                        ["weight"] = sheet.Weight, ["height"] = sheet.Height, ["oxygenSaturation"] = sheet.OxygenSaturation,
                        ["doctorName"] = sheet.DoctorName, ["university"] = sheet.University, ["licenseLine"] = sheet.LicenseLine,
                        ["medicines"] = new JsonArray(sheet.Medicines.Select(m => (JsonNode)new JsonObject
                        {
                            ["medicine"] = m.Medicine, ["detail"] = m.Detail, ["posology"] = m.Posology
                        }).ToArray()),
                        ["generalMeasures"] = sheet.GeneralMeasures, ["phone"] = sheet.Phone, ["address"] = sheet.Address
                    }
                },
                new JsonObject
                {
                    ["pageSize"] = page,
                    ["fields"] = fields,
                    ["allergiesAlert"] = doc.Descendants().Any(e => (string?)e.Attribute("data-field") == "alergias" && HasClass(e, "alert")),
                    ["doctor"] = doc.Descendants().Single(e => HasClass(e, "doctor")).Value,
                    ["university"] = doc.Descendants().Single(e => HasClass(e, "university")).Value,
                    ["license"] = doc.Descendants().Single(e => HasClass(e, "license")).Value,
                    ["medicines"] = new JsonArray(doc.Descendants().Where(e => HasClass(e, "med")).Select(m => (JsonNode)new JsonObject
                    {
                        ["name"] = m.Elements().Single(e => HasClass(e, "med-name")).Value,
                        ["posology"] = m.Elements().Single(e => HasClass(e, "med-posology")).Value
                    }).ToArray()),
                    ["generalMeasures"] = doc.Descendants().Single(e => HasClass(e, "measures-text")).Value,
                    ["footerIcons"] = doc.Descendants("img").Count(e => ((string?)e.Attribute("src") ?? string.Empty).StartsWith("data:image/png", StringComparison.Ordinal))
                }));
        }

        CheckOrWrite("contracts/golden/prescription-html-fields.json", DatedVectorFile("prescription-html-fields", null, null, cases,
            "Services/Documents/PrescriptionHtmlBuilder.cs: los valores de la receta en HTML (cada uno con data-field) y lo demás que dibuja la hoja. El PDF de la app de Android pone lo mismo en los mismos lugares.",
            "data-field: paciente, edad, fecha, alergias, ta, temp, peso, talla, spo2, telefono, domicilio. 'alergias' lleva la clase alert (en rojo, #991B1B) solo si tiene alergias.",
            "Medicamentos: el nombre en negritas seguido del detalle (' · presentación') sin negritas, y la posología en el renglón siguiente. Medidas generales con '\\n' entre renglones (un '\\r\\n' se vuelve '\\n').",
            "Papel: media carta horizontal (8.5in 5.5in) o carta (8.5in 11in) si no cabe; márgenes 0.197in arriba y abajo y 0.236in a los lados. Junto al teléfono y al domicilio va su icono solo si hay texto (footerIcons)."));
    }

    // ================================================================== contracts/golden/ticket-text.json

    [TestMethod]
    public void TicketText_MatchesTheVectors()
    {
        var profile = new ClinicProfile
        {
            ClinicName = "Consultorio Médico Santa Lucía", DoctorName = "Dra. María del Cielo Alberto Galicia", ProfessionalLicense = "15632243",
            Address = "Av. Siempre Viva 742, Col. Centro, Ciudad de México", Phone = "55 1234 5678"
        };
        var scenarios = new (string Name, SaleReceipt Receipt, ClinicProfile Profile)[]
        {
            ("venta corta con el perfil completo", new SaleReceipt
            {
                Folio = "TKT-20261011-001", Date = new DateTime(2026, 10, 11, 17, 5, 0), PatientName = "María José Pérez López", PaymentMethod = "Efectivo",
                Cashier = "Dra. María del Cielo Alberto Galicia",
                Items = { new SaleReceiptItem { Name = "Consulta general", Quantity = 1, UnitPrice = 500m }, new SaleReceiptItem { Name = "Paracetamol 500 mg", Quantity = 2, UnitPrice = 12.5m } }
            }, profile),
            ("descripción larga partida en renglones e importes con miles y punto medio", new SaleReceipt
            {
                Folio = "TKT-20261011-002", Date = new DateTime(2026, 10, 11, 9, 0, 0), PatientName = "Público General", PaymentMethod = "Tarjeta de crédito",
                Cashier = "Recepción",
                Items =
                {
                    new SaleReceiptItem { Name = "Amoxicilina con ácido clavulánico 875/125 mg tabletas", Quantity = 3, UnitPrice = 1234.5m },
                    new SaleReceiptItem { Name = "Insumo con precio de tres decimales", Quantity = 1, UnitPrice = 0.125m },
                    new SaleReceiptItem { Name = "Supercalifragilísticoexpialidoso", Quantity = 10, UnitPrice = 9.99m }
                }
            }, profile),
            ("sin perfil: los nombres de siempre", new SaleReceipt
            {
                Folio = "TKT-20261011-003", Date = new DateTime(2026, 1, 5, 23, 59, 0), PatientName = "Juan", PaymentMethod = "Transferencia", Cashier = "PC",
                Items = { new SaleReceiptItem { Name = "Curación", Quantity = 1, UnitPrice = 150m } }
            }, new ClinicProfile()),
            // Paso 8.17: cobrado sin conexión y ya regularizado: debajo del folio, el que se imprimió entonces
            ("reimpresión de un ticket cobrado sin conexión", new SaleReceipt
            {
                Folio = "TKT-20261011-0004", OriginalFolio = "TKT-20261011-T1-0001", Date = new DateTime(2026, 10, 11, 12, 30, 0), PatientName = "Público General",
                PaymentMethod = "Efectivo", Cashier = "Recepción",
                Items = { new SaleReceiptItem { Name = "Consulta general", Quantity = 1, UnitPrice = 500m } }
            }, profile)
        };

        var cases = new JsonArray();
        foreach (var (name, receipt, p) in scenarios)
        {
            cases.Add(Case(name,
                new JsonObject
                {
                    ["receipt"] = new JsonObject
                    {
                        ["folio"] = receipt.Folio,
                        ["date"] = receipt.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                        ["patientName"] = receipt.PatientName,
                        ["paymentMethod"] = receipt.PaymentMethod,
                        ["cashier"] = receipt.Cashier,
                        ["items"] = new JsonArray(receipt.Items.Select(i => (JsonNode)new JsonObject
                        {
                            ["name"] = i.Name, ["quantity"] = i.Quantity, ["unitPrice"] = Money(i.UnitPrice)
                        }).ToArray()),
                        ["originalFolio"] = receipt.OriginalFolio
                    },
                    ["profile"] = new JsonObject
                    {
                        ["clinicName"] = p.ClinicName, ["doctorName"] = p.DoctorName, ["professionalLicense"] = p.ProfessionalLicense,
                        ["address"] = p.Address, ["phone"] = p.Phone
                    }
                },
                new JsonArray(TicketText.Lines(receipt, p).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray())));
        }

        CheckOrWrite("contracts/golden/ticket-text.json", DatedVectorFile("ticket-text", null, null, cases,
            "Services/Documents/TicketText.cs: el ticket en 42 columnas, renglón por renglón; lo mismo que imprime la PC en la impresora térmica (sus textos salen de aquí).",
            "Cabecera centrada (relleno a la izquierda de (42 − largo) / 2 espacios, sin relleno a la derecha): el consultorio en mayúsculas ('CONSULTORIO MÉDICO TPVC' si no hay), el médico ('Dr. Titular' si no hay) con ' • Céd. ' y la cédula, domicilio y 'Tel: ' + teléfono si los hay. Un renglón largo se parte por palabras a 42; una palabra más larga se corta.",
            "Partidas: Cant (4, a la derecha), Descripción (17, partida por palabras; el resto en renglones solo con la descripción), P.U. y Total (9 cada uno, a la derecha), con un espacio entre columnas y sin espacios al final.",
            "Importes de las partidas: '$' + F2 (sin miles; un tercer decimal se redondea con el medio hacia arriba: 0.125 da 0.13). Total pagado: '$' + N2 con miles + ' MXN'. Fecha 'dd/MM/yyyy HH:mm' en la hora del consultorio.",
            "Separadores de 42 '-' y '='. El pie va centrado.",
            "Paso 8.17: con 'originalFolio' (un ticket cobrado sin conexión que la PC principal ya regularizó), debajo del folio va 'FOLIO SIN CONEXIÓN: ' + ese folio; null si no lo hay."));
    }
}
