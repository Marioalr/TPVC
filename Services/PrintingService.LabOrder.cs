using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TPVC.Helpers;
using TPVC.Models;
using TPVC.Services.Documents;

namespace TPVC.Services;

/// <summary>
/// La solicitud de estudios de laboratorio impresa y exportada (HTML/PDF), desde v1.25.0. Lleva el membrete de la
/// receta y su papel (media carta horizontal, o carta vertical si no cabe); los datos salen de
/// <see cref="LabOrderSheet"/>, los mismos de la vista previa.
/// </summary>
public partial class PrintingService
{
    private const double SexMinWidth = LetterheadLayout.SexMinWidth;
    private const double TitleGap = LetterheadLayout.TitleGap;
    private const double AreaGap = LetterheadLayout.AreaGap;

    /// <summary>La hoja con las letras de este equipo y el papel medido: carta si no cabe en una media carta.</summary>
    /// <param name="consultation">La consulta de la orden (una anterior se reimprime con su fecha); null = hoy.</param>
    public LabOrderSheet CreateLabOrderSheet(PatientRecord patient, LabOrder order, ClinicProfile profile, string? diagnosis,
        PatientConsultation? consultation = null)
    {
        var sheet = LabOrderSheet.Create(patient, order, profile, diagnosis, consultation?.Date ?? DateTime.Now, PrescriptionFonts);
        sheet.Paper = MeasureLabOrderPaper(sheet);
        return sheet;
    }

    /// <summary>Media carta si la solicitud cabe en una sola hoja de media carta; si no, carta vertical (como la receta).</summary>
    public static PrescriptionPaper MeasureLabOrderPaper(LabOrderSheet sheet)
    {
        try
        {
            return RunOnSta(() =>
            {
                var doc = BuildLabOrderDocument(sheet, PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight);
                var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
                paginator.PageSize = new Size(PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight);
                paginator.ComputePageCount();
                return paginator.PageCount > 1 ? PrescriptionPaper.Letter : PrescriptionPaper.HalfLetter;
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo medir la solicitud de laboratorio; sale en media carta: {ex.Message}");
            return PrescriptionPaper.HalfLetter;
        }
    }

    /// <summary>Imprime la solicitud de laboratorio en el papel de la receta (el diálogo ya lo trae elegido si la impresora lo tiene).</summary>
    public bool PrintLabOrder(PatientRecord patient, LabOrder order, ClinicProfile profile, string? diagnosis, PatientConsultation? consultation = null)
    {
        if (patient == null || order == null || order.IsEmpty) return false;

        return ExecuteOnStaThread(() =>
        {
            try
            {
                var sheet = CreateLabOrderSheet(patient, order, profile, diagnosis, consultation);
                var dialog = new PrintDialog();
                var presetPrinter = TryUsePaper(dialog, sheet.Paper);
                if (dialog.ShowDialog() != true) return false;
                if (!string.Equals(presetPrinter, PrinterName(dialog), StringComparison.OrdinalIgnoreCase))
                {
                    TryUsePaper(dialog, sheet.Paper);
                }

                var (pageWidth, pageHeight) = PrescriptionPageSize(dialog, sheet.Paper);
                var doc = BuildLabOrderDocument(sheet, pageWidth, pageHeight);
                var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
                paginator.PageSize = new Size(pageWidth, pageHeight);
                dialog.PrintDocument(paginator, $"Laboratorio - {patient.FullName}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error al imprimir la solicitud de laboratorio", ex);
                DialogService.ShowError($"No se pudo imprimir la solicitud de laboratorio: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>Guarda la solicitud como HTML con el mismo formato que la impresión (para imprimirla o guardarla como PDF desde el navegador).</summary>
    public string? ExportLabOrderDocument(PatientRecord patient, LabOrder order, ClinicProfile profile, string? diagnosis,
        string? targetFilePath = null, PatientConsultation? consultation = null)
    {
        if (patient == null || order == null || order.IsEmpty) return null;

        try
        {
            var sheet = CreateLabOrderSheet(patient, order, profile, diagnosis, consultation);
            string dest = targetFilePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"Laboratorio_{patient.RecordNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.html");

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(dest, LabOrderHtmlBuilder.Build(sheet), Encoding.UTF8);
            return dest;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al exportar la solicitud de laboratorio", ex);
            DialogService.ShowError($"Error al guardar la solicitud de laboratorio: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ documento impreso

    /// <summary>
    /// La solicitud como FlowDocument: el encabezado y los renglones del paciente de la receta, el título, los estudios
    /// por área a la izquierda (en dos columnas si son muchos) y las indicaciones a la derecha; el pie al fondo.
    /// </summary>
    public static FlowDocument BuildLabOrderDocument(LabOrderSheet sheet, double pageWidth, double pageHeight)
    {
        var letterhead = sheet.Letterhead;
        var doc = new FlowDocument
        {
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            PagePadding = new Thickness(PrescriptionSheet.MarginLeftRight, PrescriptionSheet.MarginTopBottom,
                PrescriptionSheet.MarginLeftRight, PrescriptionSheet.MarginTopBottom),
            ColumnWidth = pageWidth,
            ColumnGap = 0,
            FontFamily = BodyFontOf(letterhead),
            FontSize = BodyFontSize,
            TextAlignment = TextAlignment.Left,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = Brushes.Black,
            Background = Brushes.White
        };

        var font = BodyFontOf(letterhead);
        var header = BuildPrescriptionHeader(letterhead);
        var patientRow = InfoRow(font,
            ("Paciente:", letterhead.PatientName, null, 0),
            ("Edad:", letterhead.Age, null, AgeMinWidth),
            ("Fecha:", letterhead.Date, null, DateMinWidth));
        var diagnosisRow = InfoRow(font,
            ("Sexo:", sheet.Sex, null, SexMinWidth),
            ("Diagnóstico:", sheet.Diagnosis, null, 0));
        var title = new TextBlock
        {
            Text = LabOrderSheet.Title.ToUpperInvariant(),
            FontWeight = FontWeights.Bold,
            Foreground = PaperAccent,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        ApplyPaperText(title, font);
        title.Foreground = PaperAccent;
        var footer = BuildPrescriptionFooter(letterhead);

        double contentHeight = pageHeight - 2 * PrescriptionSheet.MarginTopBottom;
        double contentWidth = pageWidth - 2 * PrescriptionSheet.MarginLeftRight;
        double fixedHeight = MeasuredHeight(header, contentWidth) + PatientRowGap + MeasuredHeight(patientRow, contentWidth)
                             + VitalsRowGap + MeasuredHeight(diagnosisRow, contentWidth) + TitleGap + MeasuredHeight(title, contentWidth)
                             + BodyGap + FooterGap + MeasuredHeight(footer, contentWidth);

        doc.Blocks.Add(Fixed(header, top: 0));
        doc.Blocks.Add(Fixed(patientRow, PatientRowGap));
        doc.Blocks.Add(Fixed(diagnosisRow, VitalsRowGap));
        doc.Blocks.Add(Fixed(title, TitleGap));
        doc.Blocks.Add(BuildLabOrderBody(sheet, Math.Max(0, contentHeight - fixedHeight - PageSafety), contentWidth));
        doc.Blocks.Add(Fixed(footer, FooterGap));
        return doc;
    }

    /// <summary>Estudios por área (en una o dos columnas) e indicaciones; la columna del medio sostiene el alto para que el pie quede abajo.</summary>
    private static Table BuildLabOrderBody(LabOrderSheet sheet, double minHeight, double contentWidth)
    {
        const double gap = 10;
        double usable = Math.Max(200, contentWidth - gap);
        double studiesWidth = Math.Floor(usable * 0.7);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, BodyGap, 0, 0) };
        table.Columns.Add(new TableColumn { Width = new GridLength(studiesWidth) });
        table.Columns.Add(new TableColumn { Width = new GridLength(gap) });
        table.Columns.Add(new TableColumn { Width = new GridLength(usable - studiesWidth) });

        var studies = new TableCell { Background = PaperFill, Padding = new Thickness(8, 6, 8, 6) };
        var columns = LabOrderHtmlBuilder.SplitInColumns(sheet.Groups);
        if (columns.Count == 1)
        {
            AddGroups(studies.Blocks, columns[0]);
        }
        else
        {
            // Dos columnas dentro del mismo recuadro: una tabla anidada con anchos fijos (como la de afuera)
            double inner = studiesWidth - 16;
            var nested = new Table { CellSpacing = 0, Margin = new Thickness(0) };
            nested.Columns.Add(new TableColumn { Width = new GridLength(Math.Floor(inner / 2)) });
            nested.Columns.Add(new TableColumn { Width = new GridLength(inner - Math.Floor(inner / 2)) });
            var row = new TableRow();
            foreach (var column in columns)
            {
                var cell = new TableCell { Padding = new Thickness(0, 0, 8, 0) };
                AddGroups(cell.Blocks, column);
                row.Cells.Add(cell);
            }
            var rows = new TableRowGroup();
            rows.Rows.Add(row);
            nested.RowGroups.Add(rows);
            studies.Blocks.Add(nested);
        }

        var spacer = new TableCell(new Paragraph(new Run("​"))
        {
            Margin = new Thickness(0),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Max(1, minHeight)
        })
        {
            Padding = new Thickness(0)
        };

        var indications = new TableCell { Background = PaperFill, Padding = new Thickness(8, 6, 8, 6) };
        indications.Blocks.Add(new Paragraph(new Bold(new Run("Indicaciones:")))
        {
            TextAlignment = TextAlignment.Center,
            FontSize = SmallFontSize,
            Margin = new Thickness(0, 0, 0, 4),
            LineHeight = LineHeight
        });
        foreach (var text in sheet.Indications.Replace("\r\n", "\n").Split('\n'))
        {
            indications.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0), LineHeight = LineHeight });
        }

        var bodyRow = new TableRow();
        bodyRow.Cells.Add(studies);
        bodyRow.Cells.Add(spacer);
        bodyRow.Cells.Add(indications);
        var group = new TableRowGroup();
        group.Rows.Add(bodyRow);
        table.RowGroups.Add(group);
        return table;
    }

    private static void AddGroups(BlockCollection blocks, IReadOnlyList<LabOrderSheetGroup> groups)
    {
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            blocks.Add(new Paragraph(new Bold(new Run(group.Area.ToUpperInvariant())))
            {
                FontSize = SmallFontSize,
                Foreground = PaperSecondary,
                Margin = new Thickness(0, g == 0 ? 0 : AreaGap, 0, 0),
                LineHeight = LineHeight,
                KeepWithNext = true
            });
            foreach (var study in group.Studies)
            {
                blocks.Add(new Paragraph(new Run("•  " + study)) { Margin = new Thickness(0), LineHeight = LineHeight });
            }
        }
        if (groups.Count == 0) blocks.Add(new Paragraph { Margin = new Thickness(0) });
    }
}
