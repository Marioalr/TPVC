using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TPVC.Helpers;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// Receta médica impresa y exportada (HTML/PDF) con el formato de la hoja RECETA del Excel del médico:
/// media carta horizontal; escudo, nombre, universidad y cédula arriba con el trazo de signos vitales a la
/// derecha; renglones Paciente · Edad · Fecha y Alergias · T/A · Temp · Peso · Talla · SpO2; medicamentos
/// con su posología a la izquierda y "Medidas generales" a la derecha; teléfono y domicilio al pie.
/// Los datos salen de <see cref="PrescriptionSheet"/>, los mismos de la vista previa.
/// </summary>
public partial class PrintingService
{
    /// <summary>La receta siempre dice algo de alergias: cuáles, "Negadas" o "No interrogadas".</summary>
    public static string PrescriptionAllergyText(PatientRecord patient) => patient.AllergySummary;

    /// <summary>Indicaciones de la receta cuando el médico no escribió otras (vista previa, impresión y PDF). El texto vive en PrescriptionDefaults.</summary>
    public const string DefaultPrescriptionIndications = PrescriptionDefaults.Indications;

    // ------------------------------------------------------------------ medidas del formato (px de 1/96")

    // Filas del Excel: 1-3 encabezado (36 + 15 + 15 pt), 4, 6 y 8 separaciones, 5 y 7 los renglones de
    // paciente y signos (15 pt), 9-26 medicamentos y medidas generales (12 pt cada una), 27-28 el pie
    private const double HeaderHeight = 92;
    private const double CrestSize = 76;
    private const double HeaderImageWidth = 131;
    private const double HeaderImageHeight = 44;
    private const double InfoRowHeight = 20;
    private const double PatientRowGap = 13;
    private const double VitalsRowGap = 13;
    private const double BodyGap = 14;
    private const double LineHeight = 16;
    private const double FooterGap = 6;
    private const double FooterHeight = 27;
    private const double LabelColumnWidth = 70;
    // Ancho mínimo de los campos que miden lo que su valor (vacíos se ven como las celdas del Excel)
    private const double AgeMinWidth = 90;
    private const double DateMinWidth = 90;
    private const double BloodPressureMinWidth = 76;
    private const double TemperatureMinWidth = 60;
    private const double WeightMinWidth = 52;
    private const double HeightMinWidth = 56;
    private const double OxygenMinWidth = 48;
    /// <summary>Holgura para que el pie nunca pase a otra hoja por redondeos del acomodo del texto.</summary>
    private const double PageSafety = 12;

    private const double BodyFontSize = 10 * 96.0 / 72;  // 10 pt, como la hoja
    private const double SmallFontSize = 9 * 96.0 / 72;  // 9 pt: "Medidas generales:" y el pie
    private const double DoctorFontSize = 32;            // grande; se reduce solo si el nombre no cabe

    // Tinta sobre papel blanco (≥ 4.5:1 también sobre el sombreado de los campos, iguales a la vista previa)
    private static readonly Brush PaperAccent = Frozen(Color.FromRgb(0x03, 0x69, 0xA1));
    private static readonly Brush PaperSecondary = Frozen(Color.FromRgb(0x5A, 0x68, 0x7D));
    private static readonly Brush PaperDanger = Frozen(Color.FromRgb(0x99, 0x1B, 0x1B));
    /// <summary>Sombreado gris muy claro de los campos del formato del Excel.</summary>
    private static readonly Brush PaperFill = Frozen(Color.FromRgb(0xF0, 0xF0, 0xF0));

    // Letras de la hoja del Excel; si no están instaladas, las de Windows (el nombre, con letra manuscrita)
    private static readonly Lazy<FontFamily> PaperFontFamily = new(() => new FontFamily("Neue Haas Grotesk Text Pro, Segoe UI, Arial"));
    private static readonly Lazy<FontFamily> DoctorFontFamily = new(() => new FontFamily("Frosted Breath, Segoe Script, Segoe UI"));
    private static FontFamily PaperFont => PaperFontFamily.Value;
    private static FontFamily DoctorFont => DoctorFontFamily.Value;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ impresión

    /// <summary>
    /// Imprime la receta en media carta horizontal. Si la impresora tiene ese papel, el diálogo ya lo trae
    /// elegido; si no, la receta se acomoda a la hoja que haya (arriba de una carta, o al ancho de la hoja).
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
                var presetPrinter = TryUseStatementLandscape(dialog);
                if (dialog.ShowDialog() != true) return false;

                // Si eligió otra impresora en el diálogo, también se le pide media carta horizontal
                // (en la misma impresora se respeta el papel que haya escogido)
                if (!string.Equals(presetPrinter, PrinterName(dialog), StringComparison.OrdinalIgnoreCase))
                {
                    TryUseStatementLandscape(dialog);
                }

                var (pageWidth, pageHeight) = PrescriptionPageSize(dialog);
                var sheet = PrescriptionSheet.Create(patient, items, profile, generalNotes, DateTime.Now);
                var doc = BuildPrescriptionDocument(sheet, pageWidth, pageHeight);

                var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
                paginator.PageSize = new Size(pageWidth, pageHeight);
                dialog.PrintDocument(paginator, $"Receta - {patient.FullName}");
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

    private static string? PrinterName(PrintDialog dialog)
    {
        try { return dialog.PrintQueue?.FullName; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Pide media carta (Statement, 5.5 x 8.5 in) horizontal en la impresora del diálogo, si la impresora
    /// tiene ese papel y la orientación horizontal. Devuelve el nombre de la impresora revisada.
    /// </summary>
    private static string? TryUseStatementLandscape(PrintDialog dialog)
    {
        try
        {
            var queue = dialog.PrintQueue;
            if (queue == null) return null;

            var capabilities = queue.GetPrintCapabilities();
            var statement = capabilities.PageMediaSizeCapability?.FirstOrDefault(IsStatement);
            bool hasLandscape = capabilities.PageOrientationCapability?.Contains(PageOrientation.Landscape) == true;
            if (statement == null || !hasLandscape)
            {
                AppLogger.Info($"La impresora {queue.FullName} no tiene media carta horizontal: la receta se acomoda a su papel.");
                return queue.FullName;
            }

            var requested = new PrintTicket
            {
                PageMediaSize = statement,
                PageOrientation = PageOrientation.Landscape
            };
            var validated = queue.MergeAndValidatePrintTicket(dialog.PrintTicket ?? queue.UserPrintTicket, requested).ValidatedPrintTicket;
            if (validated != null && IsStatement(validated.PageMediaSize) && validated.PageOrientation == PageOrientation.Landscape)
            {
                dialog.PrintTicket = validated;
            }
            return queue.FullName;
        }
        catch (Exception ex)
        {
            // Controladores que no responden a la consulta: se imprime con lo que elija en el diálogo
            AppLogger.Warn($"No se pudo pedir media carta horizontal a la impresora: {ex.Message}");
            return null;
        }
    }

    private static bool IsStatement(PageMediaSize? size)
    {
        if (size == null) return false;
        if (size.PageMediaSizeName == PageMediaSizeName.NorthAmericaStatement) return true;
        return size.Width is { } w && size.Height is { } h
               && Math.Abs(Math.Min(w, h) - PrescriptionSheet.PageHeight) <= 3
               && Math.Abs(Math.Max(w, h) - PrescriptionSheet.PageWidth) <= 3;
    }

    /// <summary>
    /// Tamaño de la página de la receta en la hoja elegida. Media carta horizontal (o una hoja más grande,
    /// como carta vertical: la receta sale en la mitad de arriba) usa el tamaño del Excel; una hoja más angosta
    /// usa su ancho y un poco más de alto, y el texto se acomoda sin cortarse.
    /// </summary>
    public static (double Width, double Height) PrescriptionPageSize(double paperWidth, double paperHeight)
    {
        if (paperWidth <= 0 || paperHeight <= 0) return (PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight);
        double width = Math.Min(paperWidth, PrescriptionSheet.PageWidth);
        double height = Math.Min(paperHeight, Math.Max(PrescriptionSheet.PageHeight, PrescriptionSheet.PageHeight * PrescriptionSheet.PageWidth / width));
        return (width, height);
    }

    private static (double Width, double Height) PrescriptionPageSize(PrintDialog dialog)
    {
        double width = 0, height = 0;
        try
        {
            var capabilities = dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket);
            width = capabilities.OrientedPageMediaWidth ?? 0;
            height = capabilities.OrientedPageMediaHeight ?? 0;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo leer el tamaño del papel de la impresora: {ex.Message}");
        }
        if (width <= 0 || height <= 0)
        {
            width = dialog.PrintableAreaWidth;
            height = dialog.PrintableAreaHeight;
        }
        return PrescriptionPageSize(width, height);
    }

    // ------------------------------------------------------------------ documento impreso

    /// <summary>
    /// La receta como FlowDocument con el acomodo del Excel. El encabezado, los renglones del paciente y el
    /// pie son bloques fijos; medicamentos y medidas generales van en una tabla que pasa a otra hoja si no
    /// caben (no se corta nada). Con pocos medicamentos el bloque ocupa lo que falta para que el pie quede
    /// abajo, como en la hoja del Excel.
    /// </summary>
    public static FlowDocument BuildPrescriptionDocument(PrescriptionSheet sheet, double pageWidth, double pageHeight)
    {
        var doc = new FlowDocument
        {
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            PagePadding = new Thickness(PrescriptionSheet.MarginLeftRight, PrescriptionSheet.MarginTopBottom,
                PrescriptionSheet.MarginLeftRight, PrescriptionSheet.MarginTopBottom),
            ColumnWidth = pageWidth, // una sola columna
            ColumnGap = 0,
            FontFamily = PaperFont,
            FontSize = BodyFontSize,
            TextAlignment = TextAlignment.Left,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            // El papel siempre es blanco con tinta negra, sin importar el tema ni el contraste alto de Windows
            Foreground = Brushes.Black,
            Background = Brushes.White
        };

        doc.Blocks.Add(Fixed(BuildPrescriptionHeader(sheet), top: 0));
        doc.Blocks.Add(Fixed(InfoRow(
            ("Paciente:", sheet.PatientName, null, 0),
            ("Edad:", sheet.Age, null, AgeMinWidth),
            ("Fecha:", sheet.Date, null, DateMinWidth)), PatientRowGap));
        doc.Blocks.Add(Fixed(InfoRow(
            ("Alergias:", sheet.Allergies, sheet.HasAllergies ? PaperDanger : null, 0),
            ("T/A:", sheet.BloodPressure, null, BloodPressureMinWidth),
            ("Temp:", sheet.Temperature, null, TemperatureMinWidth),
            ("Peso:", sheet.Weight, null, WeightMinWidth),
            ("Talla:", sheet.Height, null, HeightMinWidth),
            ("SpO2%:", sheet.OxygenSaturation, null, OxygenMinWidth)), VitalsRowGap));

        double contentHeight = pageHeight - 2 * PrescriptionSheet.MarginTopBottom;
        double fixedHeight = HeaderHeight + PatientRowGap + InfoRowHeight + VitalsRowGap + InfoRowHeight + BodyGap + FooterGap + FooterHeight;
        double contentWidth = pageWidth - 2 * PrescriptionSheet.MarginLeftRight;
        doc.Blocks.Add(BuildPrescriptionBody(sheet, Math.Max(0, contentHeight - fixedHeight - PageSafety), contentWidth));
        doc.Blocks.Add(Fixed(BuildPrescriptionFooter(sheet), FooterGap));
        return doc;
    }

    private static BlockUIContainer Fixed(UIElement element, double top) => new(element) { Margin = new Thickness(0, top, 0, 0) };

    private static void ApplyPaperText(FrameworkElement element)
    {
        TextElement.SetFontFamily(element, PaperFont);
        TextElement.SetFontSize(element, BodyFontSize);
        TextElement.SetForeground(element, Brushes.Black);
    }

    /// <summary>Escudo a la izquierda; nombre (grande), universidad y cédula al centro; trazo a la derecha.</summary>
    private static Grid BuildPrescriptionHeader(PrescriptionSheet sheet)
    {
        var grid = new Grid { Height = HeaderHeight };
        ApplyPaperText(grid);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CrestSize + 16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (PrescriptionImages.FromFile(sheet.CrestImagePath) is { } crest)
        {
            grid.Children.Add(new Image
            {
                Source = crest,
                Width = CrestSize,
                Height = CrestSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        var headerImage = PrescriptionImages.FromFile(sheet.HeaderImagePath);
        // Universidad y cédula centradas igual que el nombre, sin encimarse con el trazo de la derecha
        const double sideMargin = HeaderImageWidth + 8;

        var titles = new StackPanel();
        Grid.SetColumn(titles, 1);
        titles.Children.Add(FitLine(new TextBlock
        {
            Text = sheet.DoctorName,
            FontFamily = DoctorFont,
            FontSize = DoctorFontSize,
            Foreground = PaperAccent
        }, 48, 0));
        titles.Children.Add(FitLine(new TextBlock { Text = sheet.University }, InfoRowHeight, sideMargin));
        titles.Children.Add(FitLine(new TextBlock { Text = sheet.LicenseLine }, InfoRowHeight, sideMargin));
        grid.Children.Add(titles);

        if (headerImage != null)
        {
            var image = new Image
            {
                Source = headerImage,
                Width = HeaderImageWidth,
                Height = HeaderImageHeight,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumn(image, 1);
            grid.Children.Add(image);
        }
        return grid;
    }

    /// <summary>Un renglón centrado que se reduce si no cabe (nunca se parte en dos ni empuja el pie).</summary>
    private static Viewbox FitLine(TextBlock text, double height, double sideMargin)
    {
        text.VerticalAlignment = VerticalAlignment.Center;
        return new Viewbox
        {
            Child = text,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(sideMargin, 0, sideMargin, 0)
        };
    }

    /// <summary>
    /// Renglón de etiquetas y campos sombreados (Paciente/Edad/Fecha o Alergias/T/A/...). Un campo con
    /// ancho mínimo 0 toma el espacio que sobra (nombre, alergias; puede ocupar dos renglones); los demás
    /// miden lo que su valor (sin partirse), con un mínimo para que el campo vacío se vea como en el Excel.
    /// </summary>
    private static Grid InfoRow(params (string Label, string Value, Brush? Ink, double MinWidth)[] fields)
    {
        var grid = new Grid { MinHeight = InfoRowHeight };
        ApplyPaperText(grid);
        int column = 0;
        foreach (var (label, value, ink, minWidth) in fields)
        {
            // La primera etiqueta de cada renglón con el mismo ancho: los valores empiezan alineados
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = column == 0 ? new GridLength(LabelColumnWidth) : GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = minWidth > 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star),
                MinWidth = minWidth
            });

            var labelText = new TextBlock
            {
                Text = label,
                Foreground = ink ?? PaperSecondary,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(column == 0 ? 0 : 10, 0, 6, 0)
            };
            Grid.SetColumn(labelText, column);
            grid.Children.Add(labelText);

            var valueBox = new Border
            {
                Background = PaperFill,
                Padding = new Thickness(6, 1, 6, 1),
                MinHeight = InfoRowHeight,
                Child = new TextBlock
                {
                    Text = value,
                    FontWeight = FontWeights.Bold,
                    Foreground = ink ?? Brushes.Black,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(valueBox, column + 1);
            grid.Children.Add(valueBox);
            column += 2;
        }
        return grid;
    }

    /// <summary>Medicamentos (nombre y posología debajo, un renglón en blanco entre ellos) y medidas generales.</summary>
    private static Table BuildPrescriptionBody(PrescriptionSheet sheet, double minHeight, double contentWidth)
    {
        // Anchos fijos calculados con el ancho de la hoja: con columnas de estrella el FlowDocument impreso
        // daba casi todo el ancho a la columna separadora y el texto se partía letra por letra
        const double gap = 10;
        double usable = Math.Max(200, contentWidth - gap);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, BodyGap, 0, 0) };
        table.Columns.Add(new TableColumn { Width = new GridLength(Math.Floor(usable * 0.7)) });
        table.Columns.Add(new TableColumn { Width = new GridLength(gap) });
        table.Columns.Add(new TableColumn { Width = new GridLength(usable - Math.Floor(usable * 0.7)) });

        var medicines = new TableCell { Background = PaperFill, Padding = new Thickness(8, 6, 8, 6) };
        for (int i = 0; i < sheet.Medicines.Count; i++)
        {
            var line = sheet.Medicines[i];
            medicines.Blocks.Add(new Paragraph(new Run(line.Medicine))
            {
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0),
                LineHeight = LineHeight,
                KeepWithNext = true
            });
            medicines.Blocks.Add(new Paragraph(new Run(line.Posology))
            {
                Margin = new Thickness(0, 0, 0, i < sheet.Medicines.Count - 1 ? LineHeight : 0),
                LineHeight = LineHeight
            });
        }
        if (sheet.Medicines.Count == 0) medicines.Blocks.Add(new Paragraph { Margin = new Thickness(0) });

        // La columna entre los dos bloques sostiene la altura mínima (el pie queda al fondo de la hoja) con
        // un renglón vacío de esa altura. No un BlockUIContainer: la tabla toma su ancho como el de la
        // página y deja sin espacio a los medicamentos y a las medidas generales.
        var spacer = new TableCell(new Paragraph(new Run("​"))
        {
            Margin = new Thickness(0),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Max(1, minHeight)
        })
        {
            Padding = new Thickness(0)
        };

        var measures = new TableCell { Background = PaperFill, Padding = new Thickness(8, 6, 8, 6) };
        measures.Blocks.Add(new Paragraph(new Bold(new Run("Medidas generales:")))
        {
            TextAlignment = TextAlignment.Center,
            FontSize = SmallFontSize,
            Margin = new Thickness(0, 0, 0, 4),
            LineHeight = LineHeight
        });
        foreach (var text in sheet.GeneralMeasures.Replace("\r\n", "\n").Split('\n'))
        {
            measures.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0), LineHeight = LineHeight });
        }

        var row = new TableRow();
        row.Cells.Add(medicines);
        row.Cells.Add(spacer);
        row.Cells.Add(measures);
        var group = new TableRowGroup();
        group.Rows.Add(row);
        table.RowGroups.Add(group);
        return table;
    }

    /// <summary>Pie: WhatsApp + teléfono a la izquierda, ubicación + domicilio a la derecha.</summary>
    private static Grid BuildPrescriptionFooter(PrescriptionSheet sheet)
    {
        var grid = new Grid { MinHeight = FooterHeight };
        ApplyPaperText(grid);
        TextElement.SetFontSize(grid, SmallFontSize);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) });

        grid.Children.Add(FooterBox(PrescriptionImages.WhatsApp, sheet.Phone, 0));
        grid.Children.Add(FooterBox(PrescriptionImages.Location, sheet.Address, 2));
        return grid;
    }

    private static Border FooterBox(ImageSource? icon, string text, int column)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (icon != null)
            {
                content.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            }
            content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        }
        var box = new Border { Background = PaperFill, Padding = new Thickness(8, 2, 8, 2), MinHeight = FooterHeight, Child = content };
        Grid.SetColumn(box, column);
        return box;
    }

    // ------------------------------------------------------------------ HTML / PDF

    /// <summary>
    /// Guarda la receta como HTML con el mismo formato que la impresión (media carta horizontal al
    /// imprimirlo o guardarlo como PDF desde el navegador). Las imágenes van dentro del archivo.
    /// </summary>
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
            var sheet = PrescriptionSheet.Create(patient, items, profile, generalNotes, DateTime.Now);
            string dest = targetFilePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"Receta_{patient.RecordNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.html");

            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(dest, BuildPrescriptionHtml(sheet), Encoding.UTF8);
            return dest;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al exportar la receta médica", ex);
            DialogService.ShowError($"Error al exportar la receta: {ex.Message}");
            return null;
        }
    }

    /// <summary>Texto seguro para HTML (un "&lt;" en una dosis o un nombre ya no rompe el documento).</summary>
    private static string H(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>Regla de CSS con números en formato invariable (0.197in, nunca 0,197in).</summary>
    private static string Css(FormattableString rule) => FormattableString.Invariant(rule);

    /// <summary>
    /// HTML de la receta (también XML bien formado, para revisarlo en las pruebas). Cada valor lleva
    /// data-field con su nombre: paciente, edad, fecha, alergias, ta, temp, peso, talla, spo2, telefono, domicilio.
    /// </summary>
    public static string BuildPrescriptionHtml(PrescriptionSheet sheet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"es\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>Receta médica - {H(sheet.PatientName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css($"@page {{ size: {PrescriptionSheet.PageWidthInches}in {PrescriptionSheet.PageHeightInches}in; margin: {PrescriptionSheet.MarginTopBottomInches}in {PrescriptionSheet.MarginLeftRightInches}in; }}"));
        sb.AppendLine("* { box-sizing: border-box; }");
        sb.AppendLine("html { background: #e5e7eb; }");
        sb.AppendLine("body { margin: 0; padding: 24px 0; font-family: 'Neue Haas Grotesk Text Pro', 'Segoe UI', Arial, sans-serif; font-size: 10pt; line-height: 16px; color: #000; -webkit-print-color-adjust: exact; print-color-adjust: exact; }");
        sb.AppendLine(Css($".sheet {{ width: {PrescriptionSheet.PageWidthInches}in; min-height: {PrescriptionSheet.PageHeightInches}in; margin: 0 auto; padding: {PrescriptionSheet.MarginTopBottomInches}in {PrescriptionSheet.MarginLeftRightInches}in; background: #fff; box-shadow: 0 2px 12px rgba(0, 0, 0, 0.25); display: flex; flex-direction: column; }}"));
        sb.AppendLine(Css($".header {{ position: relative; height: {HeaderHeight}px; }}"));
        sb.AppendLine(Css($".crest {{ position: absolute; left: 0; top: 4px; width: {CrestSize}px; height: {CrestSize}px; object-fit: contain; }}"));
        sb.AppendLine(Css($".titles {{ margin-left: {CrestSize + 16}px; text-align: center; }}"));
        sb.AppendLine(Css($".doctor {{ height: 48px; line-height: 48px; font-family: 'Frosted Breath', 'Segoe Script', 'Brush Script MT', cursive; font-size: {DoctorFontPx(sheet.DoctorName)}px; color: #0369A1; white-space: nowrap; }}"));
        sb.AppendLine(Css($".university, .license {{ height: {InfoRowHeight}px; line-height: {InfoRowHeight}px; margin: 0 {HeaderImageWidth + 8}px; white-space: nowrap; }}"));
        sb.AppendLine(Css($".trace {{ position: absolute; right: 0; bottom: 0; width: {HeaderImageWidth}px; height: {HeaderImageHeight}px; object-fit: contain; }}"));
        sb.AppendLine(Css($".row {{ display: flex; align-items: stretch; min-height: {InfoRowHeight}px; }}"));
        sb.AppendLine(Css($".patient {{ margin-top: {PatientRowGap}px; }}"));
        sb.AppendLine(Css($".vitals {{ margin-top: {VitalsRowGap}px; }}"));
        sb.AppendLine(".label { color: #5A687D; padding: 0 6px 0 10px; white-space: nowrap; align-self: center; text-align: right; }");
        sb.AppendLine(Css($".label.first {{ width: {LabelColumnWidth}px; padding-left: 0; flex: none; }}"));
        sb.AppendLine(".value { background: #F0F0F0; font-weight: 700; padding: 1px 6px; display: flex; align-items: center; min-width: 0; }");
        sb.AppendLine(".alert, .label.alert { color: #991B1B; }");
        // Nombre y alergias toman lo que sobra; los demás miden lo que su valor, con el mínimo de la celda del Excel
        sb.AppendLine(".f-patient, .f-allergies { flex: 1 1 0; }");
        sb.AppendLine(".fit { flex: 0 0 auto; white-space: nowrap; }");
        sb.AppendLine(Css($".f-age {{ min-width: {AgeMinWidth}px; }} .f-date {{ min-width: {DateMinWidth}px; }} .f-ta {{ min-width: {BloodPressureMinWidth}px; }} .f-temp {{ min-width: {TemperatureMinWidth}px; }}"));
        sb.AppendLine(Css($".f-weight {{ min-width: {WeightMinWidth}px; }} .f-height {{ min-width: {HeightMinWidth}px; }} .f-spo2 {{ min-width: {OxygenMinWidth}px; }}"));
        sb.AppendLine(Css($".body {{ flex: 1 0 auto; display: grid; grid-template-columns: 7fr 3fr; column-gap: 10px; margin-top: {BodyGap}px; }}"));
        sb.AppendLine(".meds, .measures { background: #F0F0F0; padding: 6px 8px; }");
        sb.AppendLine(Css($".med {{ break-inside: avoid; margin-bottom: {LineHeight}px; }}"));
        sb.AppendLine(".med:last-child { margin-bottom: 0; }");
        sb.AppendLine(".med-name { font-weight: 700; }");
        sb.AppendLine(".measures-title { font-weight: 700; text-align: center; font-size: 9pt; margin-bottom: 4px; }");
        sb.AppendLine(".measures-text { white-space: pre-line; }");
        sb.AppendLine(Css($".footer {{ display: grid; grid-template-columns: 4fr 6fr; column-gap: 10px; margin-top: {FooterGap}px; font-size: 9pt; break-inside: avoid; }}"));
        sb.AppendLine(Css($".footer > div {{ background: #F0F0F0; min-height: {FooterHeight}px; padding: 2px 8px; display: flex; align-items: center; justify-content: center; text-align: center; }}"));
        sb.AppendLine(".footer img { width: 16px; height: 16px; margin-right: 6px; flex: none; }");
        sb.AppendLine(Css($"@media print {{ html {{ background: #fff; }} body {{ padding: 0; }} .sheet {{ width: auto; min-height: calc({PrescriptionSheet.PageHeightInches}in - {2 * PrescriptionSheet.MarginTopBottomInches + 0.03:0.###}in); padding: 0; margin: 0; box-shadow: none; }} }}"));
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"sheet\">");

        // Encabezado
        sb.AppendLine("  <div class=\"header\">");
        if (ImageDataUri(sheet.CrestImagePath) is { } crest)
        {
            sb.AppendLine($"    <img class=\"crest\" src=\"{crest}\" alt=\"Escudo\" />");
        }
        sb.AppendLine("    <div class=\"titles\">");
        sb.AppendLine($"      <div class=\"doctor\">{H(sheet.DoctorName)}</div>");
        sb.AppendLine($"      <div class=\"university\">{H(sheet.University)}</div>");
        sb.AppendLine($"      <div class=\"license\">{H(sheet.LicenseLine)}</div>");
        sb.AppendLine("    </div>");
        if (ImageDataUri(sheet.HeaderImagePath) is { } trace)
        {
            sb.AppendLine($"    <img class=\"trace\" src=\"{trace}\" alt=\"\" />");
        }
        sb.AppendLine("  </div>");

        // Paciente · Edad · Fecha
        sb.AppendLine("  <div class=\"row patient\">");
        sb.AppendLine(Field("Paciente:", "paciente", "f-patient", sheet.PatientName, first: true));
        sb.AppendLine(Field("Edad:", "edad", "f-age fit", sheet.Age));
        sb.AppendLine(Field("Fecha:", "fecha", "f-date fit", sheet.Date));
        sb.AppendLine("  </div>");

        // Alergias · T/A · Temp · Peso · Talla · SpO2
        sb.AppendLine("  <div class=\"row vitals\">");
        sb.AppendLine(Field("Alergias:", "alergias", "f-allergies", sheet.Allergies, first: true, alert: sheet.HasAllergies));
        sb.AppendLine(Field("T/A:", "ta", "f-ta fit", sheet.BloodPressure));
        sb.AppendLine(Field("Temp:", "temp", "f-temp fit", sheet.Temperature));
        sb.AppendLine(Field("Peso:", "peso", "f-weight fit", sheet.Weight));
        sb.AppendLine(Field("Talla:", "talla", "f-height fit", sheet.Height));
        sb.AppendLine(Field("SpO2%:", "spo2", "f-spo2 fit", sheet.OxygenSaturation));
        sb.AppendLine("  </div>");

        // Medicamentos (izquierda) y medidas generales (derecha)
        sb.AppendLine("  <div class=\"body\">");
        sb.AppendLine("    <div class=\"meds\">");
        foreach (var line in sheet.Medicines)
        {
            sb.AppendLine("      <div class=\"med\">");
            sb.AppendLine($"        <div class=\"med-name\">{H(line.Medicine)}</div>");
            sb.AppendLine($"        <div class=\"med-posology\">{H(line.Posology)}</div>");
            sb.AppendLine("      </div>");
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"measures\">");
        sb.AppendLine("      <div class=\"measures-title\">Medidas generales:</div>");
        sb.AppendLine($"      <div class=\"measures-text\">{H(sheet.GeneralMeasures.Replace("\r\n", "\n"))}</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");

        // Pie: teléfono (WhatsApp) y domicilio (ubicación)
        sb.AppendLine("  <div class=\"footer\">");
        sb.AppendLine(FooterField("phone", "telefono", "icon-whatsapp", PrescriptionSheet.WhatsAppIconPng, sheet.Phone));
        sb.AppendLine(FooterField("address", "domicilio", "icon-location", PrescriptionSheet.LocationIconPng, sheet.Address));
        sb.AppendLine("  </div>");

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string Field(string label, string field, string widthClass, string value, bool first = false, bool alert = false)
    {
        var labelClass = "label" + (first ? " first" : string.Empty) + (alert ? " alert" : string.Empty);
        var valueClass = $"value {widthClass}" + (alert ? " alert" : string.Empty);
        return $"    <span class=\"{labelClass}\">{H(label)}</span><span class=\"{valueClass}\" data-field=\"{field}\">{H(value)}</span>";
    }

    private static string FooterField(string boxClass, string field, string iconClass, byte[] icon, string value)
    {
        var iconTag = !string.IsNullOrWhiteSpace(value) && icon.Length > 0
            ? $"<img class=\"{iconClass}\" src=\"data:image/png;base64,{Convert.ToBase64String(icon)}\" alt=\"\" />"
            : string.Empty;
        return $"    <div class=\"{boxClass}\">{iconTag}<span data-field=\"{field}\">{H(value)}</span></div>";
    }

    /// <summary>La imagen dentro del HTML (data:), para que el archivo se pueda enviar o mover sin perderla.</summary>
    private static string? ImageDataUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var mime = ClinicProfileService.ImageExtension(bytes) switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                _ => null
            };
            return mime == null ? null : $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tamaño del nombre del médico en el HTML: grande como en el Excel y más chico si es muy largo, para que
    /// quepa en un renglón (en la impresión lo ajusta un Viewbox).
    /// </summary>
    private static int DoctorFontPx(string name)
    {
        const double available = 600; // ancho del renglón sin el escudo, en px
        int length = Math.Max(1, name.Trim().Length);
        return (int)Math.Clamp(Math.Floor(available / (length * 0.55)), 16, 30);
    }
}
