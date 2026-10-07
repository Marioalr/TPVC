using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Servicio de impresión física y exportación digital real para recetas médicas,
/// tickets térmicos del punto de venta y cortes de caja diarios.
/// </summary>
public class PrintingService
{
    /// <summary>
    /// Imprime el ticket de compra en cualquier impresora instalada (térmica o estándar)
    /// mediante el diálogo de impresión nativo de Windows.
    /// </summary>
    public bool PrintTicket(SaleReceipt receipt, ClinicProfile profile)
    {
        if (receipt == null) return false;

        return ExecuteOnStaThread(() =>
        {
            try
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true) return false;

                var doc = BuildTicketDocument(receipt, profile);
                doc.PageWidth = 280; // Ancho térmico estándar de 80mm a 96 DPI
                doc.ColumnWidth = 280;

                IDocumentPaginatorSource dps = doc;
                dialog.PrintDocument(dps.DocumentPaginator, $"Ticket {receipt.Folio}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error al imprimir el ticket de venta", ex);
                DialogService.ShowError($"No se pudo imprimir el ticket: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// Imprime un ticket a partir de la entidad Sale persistida (útil para reimpresión por folio).
    /// </summary>
    public bool PrintTicket(Sale sale, ClinicProfile profile)
    {
        if (sale == null) return false;

        var receipt = new SaleReceipt
        {
            Folio = sale.Folio,
            Date = sale.Date,
            PatientName = sale.CustomerName,
            PaymentMethod = sale.PaymentMethod,
            Cashier = sale.Cashier,
            Items = sale.Items.Select(i => new SaleReceiptItem
            {
                ProductId = i.ProductId ?? 0,
                Name = i.Description,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        return PrintTicket(receipt, profile);
    }

    /// <summary>
    /// Imprime la receta médica oficial (NOM-004-SSA3) en cualquier impresora o PDF virtual.
    /// </summary>
    public bool PrintPrescription(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes = null)
    {
        if (patient == null) return false;

        return ExecuteOnStaThread(() =>
        {
            try
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true) return false;

                var doc = BuildPrescriptionDocument(patient, items, profile, generalNotes);
                doc.PageWidth = dialog.PrintableAreaWidth > 0 ? dialog.PrintableAreaWidth : 793;
                doc.PageHeight = dialog.PrintableAreaHeight > 0 ? dialog.PrintableAreaHeight : 1122;
                doc.ColumnWidth = doc.PageWidth;

                IDocumentPaginatorSource dps = doc;
                dialog.PrintDocument(dps.DocumentPaginator, $"Receta - {patient.FullName}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error al imprimir la receta médica", ex);
                DialogService.ShowError($"No se pudo imprimir la receta médica: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// Imprime el resumen del corte de caja diario.
    /// </summary>
    public bool PrintCashCut(DailyCashCut cut, ClinicProfile profile)
    {
        if (cut == null) return false;

        return ExecuteOnStaThread(() =>
        {
            try
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true) return false;

                var doc = BuildCashCutDocument(cut, profile);
                doc.PageWidth = dialog.PrintableAreaWidth > 0 ? dialog.PrintableAreaWidth : 793;
                doc.ColumnWidth = doc.PageWidth;

                IDocumentPaginatorSource dps = doc;
                dialog.PrintDocument(dps.DocumentPaginator, $"Corte de Caja - {cut.Date:yyyyMMdd}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error al imprimir el corte de caja", ex);
                DialogService.ShowError($"No se pudo imprimir el corte de caja: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// Genera y exporta la receta médica a un archivo HTML imprimible de alta fidelidad,
    /// abriéndolo automáticamente para su conversión inmediata a PDF o envío digital.
    /// </summary>
    /// <summary>Texto seguro para HTML (un "&lt;" en una dosis o un nombre ya no rompe el documento).</summary>
    private static string H(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>La receta siempre dice algo de alergias: cuáles, "Negadas" o "No interrogadas".</summary>
    public static string PrescriptionAllergyText(PatientRecord patient) => patient.AllergySummary;

    public string? ExportPrescriptionDocument(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes = null,
        string? targetFilePath = null)
    {
        if (patient == null) return null;

        try
        {
            string clinicName = H(!string.IsNullOrWhiteSpace(profile.ClinicName) ? profile.ClinicName : "Consultorio Médico");
            string doctorName = H(!string.IsNullOrWhiteSpace(profile.DoctorName) ? profile.DoctorName : "Dr. Titular");
            string license = H(!string.IsNullOrWhiteSpace(profile.ProfessionalLicense) ? profile.ProfessionalLicense : "Sin Cédula");
            string specialty = H(!string.IsNullOrWhiteSpace(profile.Specialty) ? profile.Specialty : "Medicina General");
            string address = H(!string.IsNullOrWhiteSpace(profile.Address) ? profile.Address : "Domicilio Conocido");
            string phone = H(!string.IsNullOrWhiteSpace(profile.Phone) ? profile.Phone : "N/D");

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"es\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine($"<title>Receta Médica - {H(patient.FullName)}</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 40px; color: #1e293b; line-height: 1.5; }");
            sb.AppendLine(".header { border-bottom: 2px solid #0284c7; padding-bottom: 15px; margin-bottom: 20px; display: flex; justify-content: space-between; align-items: center; }");
            sb.AppendLine(".clinic-title { font-size: 22px; font-weight: bold; color: #0284c7; margin: 0; }");
            sb.AppendLine(".doctor-name { font-size: 15px; font-weight: 600; color: #0f172a; margin: 3px 0; }");
            sb.AppendLine(".doctor-license { font-size: 13px; color: #64748b; margin: 0; }");
            sb.AppendLine(".patient-box { background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px; padding: 15px; margin-bottom: 25px; }");
            sb.AppendLine(".patient-grid { display: grid; grid-template-columns: 2fr 1fr 1fr; gap: 10px; font-size: 13px; }");
            sb.AppendLine(".rx-title { font-size: 18px; font-weight: bold; color: #0284c7; border-bottom: 1px solid #cbd5e1; padding-bottom: 6px; margin-bottom: 15px; }");
            sb.AppendLine(".med-item { margin-bottom: 14px; padding-bottom: 10px; border-bottom: 1px dashed #e2e8f0; }");
            sb.AppendLine(".med-name { font-weight: bold; font-size: 14px; color: #0f172a; }");
            sb.AppendLine(".med-dosage { font-size: 13px; color: #475569; margin-top: 3px; }");
            sb.AppendLine(".instructions { background: #f0fdf4; border-left: 4px solid #16a34a; padding: 12px; border-radius: 4px; font-size: 12.5px; margin-top: 25px; color: #166534; }");
            sb.AppendLine(".footer { margin-top: 60px; text-align: center; }");
            sb.AppendLine(".sign-line { width: 260px; border-top: 1px solid #475569; margin: 0 auto 8px auto; }");
            sb.AppendLine("@media print { body { margin: 15mm; } .no-print { display: none; } }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            // Header
            sb.AppendLine("<div class=\"header\">");
            sb.AppendLine("  <div>");
            sb.AppendLine($"    <h1 class=\"clinic-title\">{clinicName}</h1>");
            sb.AppendLine($"    <div class=\"doctor-name\">{doctorName} &bull; {specialty}</div>");
            sb.AppendLine($"    <div class=\"doctor-license\">Cédula Profesional: <strong>{license}</strong></div>");
            sb.AppendLine($"    <div style=\"font-size:12px; color:#64748b;\">{address} &bull; Tel: {phone}</div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("  <div style=\"text-align: right;\">");
            sb.AppendLine("    <div style=\"background: #e0f2fe; color: #0369a1; padding: 6px 12px; border-radius: 6px; font-weight: bold; font-size: 12px;\">RECETA MÉDICA</div>");
            sb.AppendLine($"    <div style=\"font-size: 12px; color: #64748b; margin-top: 5px;\">Expediente: <strong>{H(patient.RecordNumber)}</strong></div>");
            sb.AppendLine($"    <div style=\"font-size: 12px; color: #64748b;\">Fecha: {DateTime.Now:dd/MM/yyyy HH:mm}</div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</div>");

            // Patient Box
            sb.AppendLine("<div class=\"patient-box\">");
            sb.AppendLine("  <div class=\"patient-grid\">");
            sb.AppendLine($"    <div><strong>Paciente:</strong> {H(patient.FullName)}</div>");
            sb.AppendLine($"    <div><strong>Edad:</strong> {H(patient.AgeDisplay)}</div>");
            sb.AppendLine($"    <div><strong>Sexo:</strong> {H(patient.Gender)}</div>");
            sb.AppendLine($"    <div><strong>Signos:</strong> {H(string.IsNullOrWhiteSpace(patient.VitalSigns) ? "No registrados" : patient.VitalSigns)}</div>");
            sb.AppendLine($"    <div style=\"grid-column: span 2;\"><strong>Alergias:</strong> <span style=\"color:#dc2626; font-weight:600;\">{H(PrescriptionAllergyText(patient))}</span></div>");
            if (!string.IsNullOrWhiteSpace(patient.LastDiagnosis))
            {
                sb.AppendLine($"    <div style=\"grid-column: span 3;\"><strong>Diagnóstico:</strong> {H(patient.LastDiagnosis)}</div>");
            }
            sb.AppendLine("  </div>");
            sb.AppendLine("</div>");

            // Rx List
            sb.AppendLine("<div class=\"rx-title\">&#8478; PRESCRIPCIÓN MÉDICA Y POSOLOGÍA</div>");
            var itemList = items?.ToList() ?? new List<PatientPrescriptionItem>();
            if (itemList.Count == 0)
            {
                sb.AppendLine("<p style=\"color: #64748b; font-style: italic;\">Sin medicamentos activos prescritos.</p>");
            }
            else
            {
                foreach (var item in itemList)
                {
                    sb.AppendLine("<div class=\"med-item\">");
                    sb.AppendLine($"  <div class=\"med-name\">&bull; {H(item.MedicineName)} <span style=\"float:right; font-size:12px; background:#f1f5f9; padding:2px 8px; border-radius:4px;\">Cantidad: {item.RecommendedQuantity}</span></div>");
                    sb.AppendLine($"  <div class=\"med-dosage\"><strong>Indicaciones:</strong> {H(item.Dosage)}</div>");
                    sb.AppendLine("</div>");
                }
            }

            // General Instructions
            string indications = !string.IsNullOrWhiteSpace(generalNotes)
                ? generalNotes
                : "Completar el esquema terapéutico prescrito, mantener hidratación adecuada y reposo relativo. Acudir a revaloración médica o servicio de urgencias en caso de persistencia de fiebre, dificultad respiratoria o reacciones adversas.";
            sb.AppendLine("<div class=\"instructions\">");
            sb.AppendLine($"  <strong>&bull; Indicaciones Generales y Datos de Alarma:</strong><br>{H(indications).Replace("\n", "<br>")}");
            sb.AppendLine("</div>");

            // Signature
            sb.AppendLine("<div class=\"footer\">");
            sb.AppendLine("  <div class=\"sign-line\"></div>");
            sb.AppendLine($"  <div style=\"font-weight: bold; font-size: 13px;\">{doctorName}</div>");
            sb.AppendLine($"  <div style=\"font-size: 11px; color: #475569;\">Cédula Profesional: {license}</div>");
            sb.AppendLine("  <div style=\"font-size: 11px; color: #64748b; font-style: italic;\">Firma y Sello del Médico Tratante</div>");
            sb.AppendLine("</div>");

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            string dest = targetFilePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"Receta_{patient.RecordNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.html"
            );

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(dest, sb.ToString(), Encoding.UTF8);
            return dest;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al exportar la receta médica", ex);
            DialogService.ShowError($"Error al exportar la receta: {ex.Message}");
            return null;
        }
    }

    #region Constructores de FlowDocument para Impresión

    private FlowDocument BuildTicketDocument(SaleReceipt receipt, ClinicProfile profile)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(10),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Left
        };

        string clinicName = !string.IsNullOrWhiteSpace(profile.ClinicName) ? profile.ClinicName : "CONSULTORIO MÉDICO TPVC";
        string doctorName = !string.IsNullOrWhiteSpace(profile.DoctorName) ? profile.DoctorName : "Dr. Titular";
        string license = !string.IsNullOrWhiteSpace(profile.ProfessionalLicense) ? profile.ProfessionalLicense : string.Empty;
        string address = !string.IsNullOrWhiteSpace(profile.Address) ? profile.Address : string.Empty;
        string phone = !string.IsNullOrWhiteSpace(profile.Phone) ? profile.Phone : string.Empty;

        // Cabecera
        var headerPara = new Paragraph { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        headerPara.Inlines.Add(new Bold(new Run(clinicName.ToUpperInvariant() + "\n")));
        if (!string.IsNullOrWhiteSpace(doctorName))
        {
            string docLine = string.IsNullOrWhiteSpace(license) ? doctorName : $"{doctorName} • Céd. {license}";
            headerPara.Inlines.Add(new Run(docLine + "\n"));
        }
        if (!string.IsNullOrWhiteSpace(address)) headerPara.Inlines.Add(new Run(address + "\n"));
        if (!string.IsNullOrWhiteSpace(phone)) headerPara.Inlines.Add(new Run($"Tel: {phone}\n"));
        headerPara.Inlines.Add(new Run("------------------------------------------"));
        doc.Blocks.Add(headerPara);

        // Metadatos
        var metaPara = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        metaPara.Inlines.Add(new Bold(new Run($"FOLIO:    {receipt.Folio}\n")));
        metaPara.Inlines.Add(new Run($"FECHA:    {receipt.Date:dd/MM/yyyy HH:mm}\n"));
        metaPara.Inlines.Add(new Run($"PACIENTE: {receipt.PatientName}\n"));
        metaPara.Inlines.Add(new Run($"PAGO:     {receipt.PaymentMethod}\n"));
        metaPara.Inlines.Add(new Run($"ATENDIÓ:  {receipt.Cashier}\n"));
        metaPara.Inlines.Add(new Run("=========================================="));
        doc.Blocks.Add(metaPara);

        // Ítems
        var itemsTable = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8) };
        itemsTable.Columns.Add(new TableColumn { Width = new GridLength(35) });
        itemsTable.Columns.Add(new TableColumn { Width = new GridLength(140) });
        itemsTable.Columns.Add(new TableColumn { Width = new GridLength(45) });
        itemsTable.Columns.Add(new TableColumn { Width = new GridLength(55) });

        var headerRow = new TableRow();
        headerRow.Cells.Add(new TableCell(new Paragraph(new Bold(new Run("Cant")))));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Bold(new Run("Descripción")))));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Bold(new Run("P.U.")))));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Bold(new Run("Total")))));
        var rowGroup = new TableRowGroup();
        rowGroup.Rows.Add(headerRow);

        foreach (var item in receipt.Items)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(item.Quantity.ToString()))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(item.Name))));
            row.Cells.Add(new TableCell(new Paragraph(new Run($"${item.UnitPrice:F2}"))));
            row.Cells.Add(new TableCell(new Paragraph(new Run($"${item.Subtotal:F2}"))));
            rowGroup.Rows.Add(row);
        }
        itemsTable.RowGroups.Add(rowGroup);
        doc.Blocks.Add(itemsTable);

        // Totales
        var totalsPara = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        totalsPara.Inlines.Add(new Run("------------------------------------------\n"));
        totalsPara.Inlines.Add(new Run($"TOTAL DE ARTÍCULOS: {receipt.TotalItems}\n"));
        totalsPara.Inlines.Add(new Bold(new Run($"TOTAL PAGADO:       ${receipt.TotalAmount:N2} MXN\n")));
        totalsPara.Inlines.Add(new Run("=========================================="));
        doc.Blocks.Add(totalsPara);

        // Pie
        var footerPara = new Paragraph { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        footerPara.Inlines.Add(new Run("¡Gracias por su visita y confianza!\n"));
        footerPara.Inlines.Add(new Run("Comprobante de atención médica y farmacia\n"));
        doc.Blocks.Add(footerPara);

        return doc;
    }

    private FlowDocument BuildPrescriptionDocument(
        PatientRecord patient,
        IEnumerable<PatientPrescriptionItem> items,
        ClinicProfile profile,
        string? generalNotes)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(36),
            FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
            FontSize = 11.5,
            TextAlignment = TextAlignment.Left
        };

        string clinicName = !string.IsNullOrWhiteSpace(profile.ClinicName) ? profile.ClinicName : "CONSULTORIO MÉDICO INTEGRAL";
        string doctorName = !string.IsNullOrWhiteSpace(profile.DoctorName) ? profile.DoctorName : "Dr. Titular";
        string license = !string.IsNullOrWhiteSpace(profile.ProfessionalLicense) ? profile.ProfessionalLicense : "Cédula en trámite";
        string specialty = !string.IsNullOrWhiteSpace(profile.Specialty) ? profile.Specialty : "Medicina General";
        string address = !string.IsNullOrWhiteSpace(profile.Address) ? profile.Address : "Domicilio Conocido";
        string phone = !string.IsNullOrWhiteSpace(profile.Phone) ? profile.Phone : string.Empty;

        // Membrete
        var headerPara = new Paragraph { Margin = new Thickness(0, 0, 0, 14) };
        headerPara.Inlines.Add(new Bold(new Run(clinicName) { FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(2, 132, 199)) }) { });
        headerPara.Inlines.Add(new Run("\n" + doctorName) { FontSize = 13, FontWeight = FontWeights.SemiBold });
        headerPara.Inlines.Add(new Run($" • {specialty}\n"));
        headerPara.Inlines.Add(new Run($"Cédula Profesional: {license} • {address} • Tel: {phone}\n"));
        headerPara.Inlines.Add(new Run(new string('─', 75)) { Foreground = Brushes.LightGray });
        doc.Blocks.Add(headerPara);

        // Ficha Paciente
        var patientPara = new Paragraph { Margin = new Thickness(0, 0, 0, 16) };
        patientPara.Inlines.Add(new Bold(new Run("Paciente: ")) { });
        patientPara.Inlines.Add(new Run(patient.FullName + "   "));
        patientPara.Inlines.Add(new Bold(new Run("Edad: ")));
        patientPara.Inlines.Add(new Run($"{patient.AgeDisplay}   "));
        patientPara.Inlines.Add(new Bold(new Run("Sexo: ")));
        patientPara.Inlines.Add(new Run($"{patient.Gender}   "));
        patientPara.Inlines.Add(new Bold(new Run("Fecha: ")));
        patientPara.Inlines.Add(new Run($"{DateTime.Now:dd/MM/yyyy HH:mm}\n"));
        if (!string.IsNullOrWhiteSpace(patient.VitalSigns))
        {
            patientPara.Inlines.Add(new Bold(new Run("Signos Vitales: ")));
            patientPara.Inlines.Add(new Run(patient.VitalSigns + "\n"));
        }
        var allergyBrush = patient.HasAllergies ? Brushes.Crimson : Brushes.Black;
        patientPara.Inlines.Add(new Bold(new Run("Alergias: ")) { Foreground = allergyBrush });
        patientPara.Inlines.Add(new Run(PrescriptionAllergyText(patient) + "\n") { Foreground = allergyBrush });
        if (!string.IsNullOrWhiteSpace(patient.LastDiagnosis))
        {
            patientPara.Inlines.Add(new Bold(new Run("Diagnóstico: ")));
            patientPara.Inlines.Add(new Run(patient.LastDiagnosis + "\n"));
        }
        doc.Blocks.Add(patientPara);

        // Prescripción
        var rxTitle = new Paragraph(new Bold(new Run("℞ PRESCRIPCIÓN MÉDICA Y POSOLOGÍA") { FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(2, 132, 199)) }))
        {
            Margin = new Thickness(0, 0, 0, 10)
        };
        doc.Blocks.Add(rxTitle);

        var itemList = items?.ToList() ?? new List<PatientPrescriptionItem>();
        if (itemList.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("Sin medicamentos activos prescritos.")) { FontStyle = FontStyles.Italic, Foreground = Brushes.Gray });
        }
        else
        {
            var medList = new List();
            foreach (var item in itemList)
            {
                var medPara = new Paragraph();
                medPara.Inlines.Add(new Bold(new Run($"{item.MedicineName}  (Cantidad: {item.RecommendedQuantity})\n")));
                medPara.Inlines.Add(new Run($"   Indicaciones: {item.Dosage}\n") { Foreground = Brushes.DarkSlateGray });
                medList.ListItems.Add(new ListItem(medPara));
            }
            doc.Blocks.Add(medList);
        }

        // Indicaciones generales
        string indications = !string.IsNullOrWhiteSpace(generalNotes)
            ? generalNotes
            : "Completar el tratamiento prescrito, mantener hidratación adecuada y reposo relativo. Acudir a valoración si presenta fiebre persistente o complicaciones.";
        var indicationsPara = new Paragraph { Margin = new Thickness(0, 16, 0, 30) };
        indicationsPara.Inlines.Add(new Bold(new Run("Indicaciones Generales y Datos de Alarma:\n")));
        indicationsPara.Inlines.Add(new Run(indications) { Foreground = Brushes.DarkSlateGray });
        doc.Blocks.Add(indicationsPara);

        // Firma
        var signPara = new Paragraph { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        signPara.Inlines.Add(new Run("_________________________________________\n"));
        signPara.Inlines.Add(new Bold(new Run(doctorName + "\n")));
        signPara.Inlines.Add(new Run($"Cédula Profesional: {license}\n"));
        signPara.Inlines.Add(new Run("Firma y Sello del Médico Tratante") { FontStyle = FontStyles.Italic, Foreground = Brushes.Gray });
        doc.Blocks.Add(signPara);

        return doc;
    }

    private FlowDocument BuildCashCutDocument(DailyCashCut cut, ClinicProfile profile)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(20),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 10,
            TextAlignment = TextAlignment.Left
        };

        string clinicName = !string.IsNullOrWhiteSpace(profile.ClinicName) ? profile.ClinicName : "CONSULTORIO MÉDICO TPVC";
        string doctorName = !string.IsNullOrWhiteSpace(profile.DoctorName) ? profile.DoctorName : "Dr. Titular";

        var p = new Paragraph();
        p.Inlines.Add(new Bold(new Run($"*** CORTE DE CAJA DIARIO ***\n{clinicName}\n")));
        p.Inlines.Add(new Run($"Médico/Titular: {doctorName}\n"));
        p.Inlines.Add(new Run($"Fecha de Corte: {cut.Date:dd/MM/yyyy}\n"));
        p.Inlines.Add(new Run($"Generado el:    {DateTime.Now:dd/MM/yyyy HH:mm:ss}\n"));
        p.Inlines.Add(new Run("===================================================\n"));

        p.Inlines.Add(new Bold(new Run("1. RESUMEN DE VENTAS\n")));
        p.Inlines.Add(new Run($"   Total Ventas Netas:   ${cut.TotalSalesAmount:N2} MXN\n"));
        p.Inlines.Add(new Run($"   Tickets Cobrados:     {cut.ActiveTicketsCount}\n"));
        p.Inlines.Add(new Run($"   Tickets Cancelados:   {cut.CancelledTicketsCount} (${cut.TotalCancelledAmount:N2})\n\n"));

        p.Inlines.Add(new Bold(new Run("2. INGRESOS POR MÉTODO DE PAGO\n")));
        p.Inlines.Add(new Run($"   Efectivo:             ${cut.TotalCash:N2}\n"));
        p.Inlines.Add(new Run($"   Tarjeta de Débito:    ${cut.TotalDebitCard:N2}\n"));
        p.Inlines.Add(new Run($"   Tarjeta de Crédito:   ${cut.TotalCreditCard:N2}\n"));
        p.Inlines.Add(new Run($"   Transferencia:        ${cut.TotalTransfer:N2}\n"));
        if (cut.TotalOtherPaymentMethods > 0)
        {
            p.Inlines.Add(new Run($"   Otros Métodos:        ${cut.TotalOtherPaymentMethods:N2}\n"));
        }
        p.Inlines.Add(new Run("\n"));

        p.Inlines.Add(new Bold(new Run("3. DESGLOSE PRODUCTOS VS SERVICIOS\n")));
        p.Inlines.Add(new Run($"   Servicios Médicos:    ${cut.TotalServicesAmount:N2} ({cut.TotalServicesCount} servicios)\n"));
        p.Inlines.Add(new Run($"   Productos Farmacia:   ${cut.TotalProductsAmount:N2} ({cut.TotalProductsUnitsSold} pzas)\n"));
        p.Inlines.Add(new Run("===================================================\n"));

        doc.Blocks.Add(p);
        return doc;
    }

    private static bool ExecuteOnStaThread(Func<bool> action)
    {
        if (Application.Current == null)
        {
            // Entorno sin interfaz gráfica (ej. pruebas unitarias en Linux)
            return false;
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            return action();
        }

        return Application.Current.Dispatcher.Invoke(action);
    }

    #endregion
}
