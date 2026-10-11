using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TPVC.Models;
using TPVC.Services.Documents;
using TPVC.Services.Rules;
using TPVC.Tests.Fase6;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.11: los generadores de documentos (Services/Documents) y las reglas (Services/Rules) no dependen de WPF ni de
/// la base, el ticket en texto cabe en 42 columnas y su HTML dice lo mismo, y ClinicClock fija "hoy" solo en el flujo de
/// cada prueba.
/// </summary>
[TestClass]
public class DocumentsAndRulesTests
{
    /// <summary>Lo que no puede aparecer: WPF, ventanas, impresión directa, ViewModels ni la base.</summary>
    private static readonly string[] ForbiddenTokens =
    {
        "System.Windows", "Microsoft.Win32", "Application.Current", "Dispatcher", "DialogService", "PrintingService", "MessageBox",
        "Process.Start", "TPVC.ViewModels", "TPVC.Views", "AppDbContext", "Microsoft.EntityFrameworkCore", "InventoryService", "DateTime.Today",
        "DateTime.Now"
    };

    [TestMethod]
    public void DocumentsAndRules_HaveNoWpfDatabaseOrClockReads()
    {
        var root = Fase6Assets.RepoRoot();
        var problems = new List<string>();
        int files = 0;
        foreach (var folder in new[] { Path.Combine(root, "Services", "Documents"), Path.Combine(root, "Services", "Rules") })
        {
            Assert.IsTrue(Directory.Exists(folder), folder);
            foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                files++;
                var code = CSharpSource.Sanitize(File.ReadAllText(file));
                foreach (var token in ForbiddenTokens)
                {
                    foreach (Match m in Regex.Matches(code, $@"(?<![\w.]){Regex.Escape(token)}(?!\w)"))
                    {
                        problems.Add($"{Path.GetRelativePath(root, file)}:{CSharpSource.LineOf(code, m.Index)} usa {token}");
                    }
                }
            }
        }
        Assert.IsGreaterThanOrEqualTo(9, files, "Los generadores y las reglas del paso 8.11");
        Assert.IsEmpty(problems, "Services/Documents y Services/Rules no dependen de WPF, de la base ni de la hora del sistema:\n" + string.Join("\n", problems));
    }

    private static SaleReceipt Receipt() => new()
    {
        Folio = "TKT-20261011-010",
        Date = new DateTime(2026, 10, 11, 12, 30, 0),
        PatientName = "Paciente <Prueba> & Hijos",
        PaymentMethod = "Efectivo",
        Cashier = "Recepción",
        Items =
        {
            new SaleReceiptItem { Name = "Consulta de especialidad con revisión de estudios de laboratorio", Quantity = 1, UnitPrice = 850m },
            new SaleReceiptItem { Name = "Paracetamol", Quantity = 12, UnitPrice = 1500.75m }
        }
    };

    [TestMethod]
    public void TicketText_FitsIn42Columns_AndKeepsTheLabelsAligned()
    {
        var profile = new ClinicProfile
        {
            ClinicName = "Un consultorio con un nombre muy largo que no cabe en un renglón del ticket",
            DoctorName = "Dr. Prueba", Address = "Calle sin espacios:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        };
        var lines = TicketText.Lines(Receipt(), profile);

        foreach (var line in lines) Assert.IsLessThanOrEqualTo(TicketText.Width, line.Length, $"«{line}»");
        CollectionAssert.Contains(lines.ToList(), "FOLIO:    TKT-20261011-010");
        CollectionAssert.Contains(lines.ToList(), "TOTAL PAGADO:       $18,859.00 MXN");
        Assert.AreEqual(TicketText.Build(Receipt(), profile), string.Join("\n", lines) + "\n");
        Assert.IsTrue(lines.Any(l => l.StartsWith("  12 Paracetamol", StringComparison.Ordinal) && l.EndsWith("$1500.75 $18009.00", StringComparison.Ordinal)), string.Join("\n", lines));
    }

    [TestMethod]
    public void TicketHtml_SaysTheSameAsTheText()
    {
        var profile = new ClinicProfile { ClinicName = "Consultorio", DoctorName = "Dra. Prueba", ProfessionalLicense = "123", Phone = "55 1234 5678" };
        var html = TicketHtml.Build(Receipt(), profile);
        var doc = XDocument.Parse(html.Replace("<!DOCTYPE html>", string.Empty));
        string Field(string name) => doc.Descendants().Single(e => (string?)e.Attribute("data-field") == name).Value;

        var meta = TicketText.MetaLines(Receipt());
        Assert.AreEqual(meta[0], Field("folio"));
        Assert.AreEqual(meta[2], Field("paciente"), "El texto va escapado y se lee igual");
        Assert.AreEqual(TicketText.TotalLines(Receipt())[1], Field("total"));
        CollectionAssert.AreEqual(TicketText.HeaderLines(profile).ToArray(),
            doc.Descendants().Single(e => (string?)e.Attribute("class") == "header").Elements().Where(e => (string?)e.Attribute("class") != "rule").Select(e => e.Value).ToArray());
        Assert.AreEqual(3, doc.Descendants("tr").Count(), "El encabezado y las dos partidas");
        StringAssert.Contains(html, "@page { size: 80mm auto;");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [TestMethod]
    public void ClinicClock_FixesTodayOnlyInThisFlow_AndRestoresIt()
    {
        var patient = new PatientRecord { BirthDate = new DateTime(2000, 6, 15) };
        var batch = new ProductBatch { ExpirationDate = new DateTime(2030, 1, 10) };
        var appointment = new PatientAppointment { Date = new DateTime(2030, 1, 10), Status = "Programada" };

        using (ClinicClock.Use(new FixedClock(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero))))
        {
            Assert.AreEqual(new DateTime(2030, 1, 1), ClinicClock.Today);
            Assert.AreEqual(29, patient.Age);
            Assert.AreEqual("29 años", patient.AgeDisplay);
            Assert.AreEqual(9, batch.DaysRemaining);
            Assert.AreEqual("Vence en 9 días", batch.StatusText);
            Assert.IsTrue(appointment.IsUpcoming);
        }

        using (ClinicClock.Use(new FixedClock(new DateTimeOffset(2030, 1, 11, 0, 0, 0, TimeSpan.Zero))))
        {
            Assert.AreEqual("Vencido", batch.StatusText);
            Assert.IsFalse(appointment.IsUpcoming);
        }

        Assert.AreEqual(DateTime.Today, ClinicClock.Today, "Al desecharlo vuelve el reloj del sistema");
    }

    [TestMethod]
    public void VitalSignsRules_WarnAndClassify()
    {
        Assert.AreEqual(string.Empty, VitalSignsRules.Warning("36.5", "120/80", "0.98", "95", "72", 30));
        Assert.AreEqual("Temp 38.4 °C: fiebre · T/A 150/95: elevada · SpO₂ 91 %: baja · Glucosa 250 mg/dL: elevada · FC 110: taquicardia",
            VitalSignsRules.Warning("38,4", "150/95", "0.91", "250", "110", 40));
        Assert.AreEqual(string.Empty, VitalSignsRules.Warning(null, null, null, null, "130", 5), "En un niño, 130 lpm no es alta");
        Assert.AreEqual("FC 150: alta para la edad", VitalSignsRules.Warning(null, null, null, null, "150", 5));
        Assert.AreEqual(VitalSignsRules.BmiUnavailable, VitalSignsRules.BmiText("70", "0"));
        Assert.AreEqual("Peso normal", VitalSignsRules.BmiCategory(24.99m));
        Assert.AreEqual("Sobrepeso", VitalSignsRules.BmiCategory(25.0m));
    }

    [TestMethod]
    public void PatientFormRules_ValidateInTheSameOrderAsTheForm()
    {
        var today = new DateTime(2026, 10, 11);
        var ok = new PatientFormInput("Ana", "López", null, "EXP-1", "14/03/1992", new DateTime(1992, 3, 14), null, "Femenino", "Negadas", "ana@correo.com");
        Assert.IsNull(PatientFormRules.Validate(ok, today, out var age));
        Assert.AreEqual(34, age);

        StringAssert.Contains(PatientFormRules.Validate(ok with { FirstName = " " }, today, out _), "nombre");
        StringAssert.Contains(PatientFormRules.Validate(ok with { LastName = "" }, today, out _), "apellido paterno");
        StringAssert.Contains(PatientFormRules.Validate(ok with { BirthDate = null }, today, out _), "no es válida");
        StringAssert.Contains(PatientFormRules.Validate(ok with { BirthDateText = "", BirthDate = null, AgeText = "abc" }, today, out _), "edad en años");
        Assert.IsNull(PatientFormRules.Validate(ok with { BirthDateText = "", BirthDate = null, AgeText = "40" }, today, out var typed));
        Assert.AreEqual(40, typed);
        StringAssert.Contains(PatientFormRules.Validate(ok with { Allergies = " " }, today, out _), "Negadas");
        StringAssert.Contains(PatientFormRules.Validate(ok with { Email = "ana@" }, today, out _), "correo");
        Assert.AreEqual("Ana López Ruiz", PatientFormRules.ComposeFullName(" Ana ", "López ", " Ruiz"));
    }
}
