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
using TPVC.ViewModels.Patients;

namespace TPVC.Services;

/// <summary>
/// Servicio de impresión física y exportación digital real para recetas médicas,
/// tickets térmicos del punto de venta y cortes de caja diarios.
/// </summary>
public partial class PrintingService : IPrescriptionOutput
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

    #region Constructores de FlowDocument para Impresión

    private FlowDocument BuildTicketDocument(SaleReceipt receipt, ClinicProfile profile)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(10),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Left,
            // El papel siempre es blanco con tinta negra, sin importar el tema ni el contraste alto de Windows
            Foreground = Brushes.Black,
            Background = Brushes.White
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

    private FlowDocument BuildCashCutDocument(DailyCashCut cut, ClinicProfile profile)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(20),
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 10,
            TextAlignment = TextAlignment.Left,
            // El papel siempre es blanco con tinta negra, sin importar el tema ni el contraste alto de Windows
            Foreground = Brushes.Black,
            Background = Brushes.White
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
