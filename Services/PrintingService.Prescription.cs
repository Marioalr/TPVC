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

    // Las letras salen de la hoja (las de siempre o las que se eligieron en Médico y consultorio, desde v1.23.0); si
    // no están instaladas, WPF usa las siguientes de la lista
    private static FontFamily BodyFontOf(PrescriptionSheet sheet) => new(sheet.BodyFont);
    private static FontFamily DoctorFontOf(PrescriptionSheet sheet) => new(sheet.DoctorFont);

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
        string? generalNotes = null,
        PatientConsultation? consultation = null)
    {
        if (patient == null) return false;

        return ExecuteOnStaThread(() =>
        {
            try
            {
                // Primero la hoja: si no cabe en una media carta se imprime en carta vertical
                var sheet = CreatePrescriptionSheet(patient, items, profile, generalNotes, consultation);
                var dialog = new PrintDialog();
                var presetPrinter = TryUsePaper(dialog, sheet.Paper);
                if (dialog.ShowDialog() != true) return false;

                // Si eligió otra impresora en el diálogo, también se le pide el papel de la receta
                // (en la misma impresora se respeta el papel que haya escogido)
                if (!string.Equals(presetPrinter, PrinterName(dialog), StringComparison.OrdinalIgnoreCase))
                {
                    TryUsePaper(dialog, sheet.Paper);
                }

                var (pageWidth, pageHeight) = PrescriptionPageSize(dialog, sheet.Paper);
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

    // ------------------------------------------------------------------ hoja, letras y papel (desde v1.23.0)

    private readonly LocalPreferencesStore? _preferences;

    /// <param name="preferences">Las letras de la receta que eligió este equipo (Médico y consultorio).</param>
    public PrintingService(LocalPreferencesStore? preferences = null)
    {
        _preferences = preferences;
    }

    /// <summary>Las letras de la receta en este equipo: las elegidas o las de siempre.</summary>
    public PrescriptionFonts PrescriptionFonts => _preferences?.Current.PrescriptionFonts ?? PrescriptionFonts.Default;

    /// <summary>La hoja con las letras de este equipo y el papel medido: carta si no cabe en una media carta.</summary>
    /// <param name="consultation">La consulta de la receta (una anterior se reimprime con su fecha y sus signos); null = hoy.</param>
    public PrescriptionSheet CreatePrescriptionSheet(PatientRecord patient, IEnumerable<PatientPrescriptionItem> items, ClinicProfile profile, string? generalNotes,
        PatientConsultation? consultation = null)
    {
        var sheet = PrescriptionSheet.Create(patient, items, profile, generalNotes, consultation?.Date ?? DateTime.Now, PrescriptionFonts,
            string.IsNullOrWhiteSpace(consultation?.VitalSigns) ? null : consultation.VitalSigns);
        sheet.Paper = MeasurePaper(sheet);
        return sheet;
    }

    /// <summary>El papel que usará la receta (la vista previa lo muestra igual que saldrá impresa).</summary>
    public PrescriptionPaper ChoosePaper(PatientRecord patient, IEnumerable<PatientPrescriptionItem> items, ClinicProfile profile, string? generalNotes = null) =>
        patient == null ? PrescriptionPaper.HalfLetter : CreatePrescriptionSheet(patient, items, profile, generalNotes).Paper;

    /// <summary>
    /// Media carta si la receta cabe en una sola hoja de media carta; si no, carta vertical. Se mide con el mismo
    /// documento que se imprime (el texto real, con sus renglones partidos y la letra elegida), no con una estimación.
    /// Sin WPF (pruebas en Linux), media carta.
    /// </summary>
    public static PrescriptionPaper MeasurePaper(PrescriptionSheet sheet)
    {
        try
        {
            return RunOnSta(() =>
            {
                var doc = BuildPrescriptionDocument(sheet, PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight);
                var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
                paginator.PageSize = new Size(PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight);
                paginator.ComputePageCount();
                return paginator.PageCount > 1 ? PrescriptionPaper.Letter : PrescriptionPaper.HalfLetter;
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"No se pudo medir la receta; sale en media carta: {ex.Message}");
            return PrescriptionPaper.HalfLetter;
        }
    }

    /// <summary>Corre en un hilo STA (WPF lo exige para armar el documento): este, el de la interfaz o uno nuevo.</summary>
    private static T RunOnSta<T>(Func<T> action)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA) return action();
        if (Application.Current?.Dispatcher is { } dispatcher) return dispatcher.Invoke(action);

        T result = default!;
        Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result;
    }

    private static string? PrinterName(PrintDialog dialog)
    {
        try { return dialog.PrintQueue?.FullName; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Pide el papel de la receta en la impresora del diálogo: media carta (Statement, 5.5 x 8.5 in) horizontal o,
    /// si la receta no cabe en ella, carta vertical; solo si la impresora tiene ese papel y esa orientación.
    /// Devuelve el nombre de la impresora revisada.
    /// </summary>
    private static string? TryUsePaper(PrintDialog dialog, PrescriptionPaper paper)
    {
        bool letter = paper == PrescriptionPaper.Letter;
        var orientation = letter ? PageOrientation.Portrait : PageOrientation.Landscape;
        Func<PageMediaSize?, bool> isWanted = letter ? IsLetter : IsStatement;
        var description = letter ? "carta vertical" : "media carta horizontal";
        try
        {
            var queue = dialog.PrintQueue;
            if (queue == null) return null;

            var capabilities = queue.GetPrintCapabilities();
            var size = capabilities.PageMediaSizeCapability?.FirstOrDefault(s => isWanted(s));
            bool hasOrientation = capabilities.PageOrientationCapability?.Contains(orientation) == true;
            if (size == null || !hasOrientation)
            {
                AppLogger.Info($"La impresora {queue.FullName} no tiene {description}: la receta se acomoda a su papel.");
                return queue.FullName;
            }

            var requested = new PrintTicket
            {
                PageMediaSize = size,
                PageOrientation = orientation
            };
            var validated = queue.MergeAndValidatePrintTicket(dialog.PrintTicket ?? queue.UserPrintTicket, requested).ValidatedPrintTicket;
            if (validated != null && isWanted(validated.PageMediaSize) && validated.PageOrientation == orientation)
            {
                dialog.PrintTicket = validated;
            }
            return queue.FullName;
        }
        catch (Exception ex)
        {
            // Controladores que no responden a la consulta: se imprime con lo que elija en el diálogo
            AppLogger.Warn($"No se pudo pedir {description} a la impresora: {ex.Message}");
            return null;
        }
    }

    private static bool IsLetter(PageMediaSize? size)
    {
        if (size == null) return false;
        if (size.PageMediaSizeName == PageMediaSizeName.NorthAmericaLetter) return true;
        return size.Width is { } w && size.Height is { } h
               && Math.Abs(Math.Min(w, h) - PrescriptionSheet.PageWidth) <= 3
               && Math.Abs(Math.Max(w, h) - PrescriptionSheet.LetterPageHeight) <= 3;
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
    public static (double Width, double Height) PrescriptionPageSize(double paperWidth, double paperHeight, PrescriptionPaper paper = PrescriptionPaper.HalfLetter)
    {
        if (paper == PrescriptionPaper.Letter)
        {
            // Carta vertical; en una hoja más chica, lo que haya (el texto se acomoda y pasa a otra hoja si hace falta)
            if (paperWidth <= 0 || paperHeight <= 0) return (PrescriptionSheet.PageWidth, PrescriptionSheet.LetterPageHeight);
            return (Math.Min(paperWidth, PrescriptionSheet.PageWidth), Math.Min(paperHeight, PrescriptionSheet.LetterPageHeight));
        }
        if (paperWidth <= 0 || paperHeight <= 0) return (PrescriptionSheet.PageWidth, PrescriptionSheet.PageHeight);
        double width = Math.Min(paperWidth, PrescriptionSheet.PageWidth);
        double height = Math.Min(paperHeight, Math.Max(PrescriptionSheet.PageHeight, PrescriptionSheet.PageHeight * PrescriptionSheet.PageWidth / width));
        return (width, height);
    }

    private static (double Width, double Height) PrescriptionPageSize(PrintDialog dialog, PrescriptionPaper paper)
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
        return PrescriptionPageSize(width, height, paper);
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
            FontFamily = BodyFontOf(sheet),
            FontSize = BodyFontSize,
            TextAlignment = TextAlignment.Left,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            // El papel siempre es blanco con tinta negra, sin importar el tema ni el contraste alto de Windows
            Foreground = Brushes.Black,
            Background = Brushes.White
        };

        var header = BuildPrescriptionHeader(sheet);
        var font = BodyFontOf(sheet);
        var patientRow = InfoRow(font,
            ("Paciente:", sheet.PatientName, null, 0),
            ("Edad:", sheet.Age, null, AgeMinWidth),
            ("Fecha:", sheet.Date, null, DateMinWidth));
        var vitalsRow = InfoRow(font,
            ("Alergias:", sheet.Allergies, sheet.HasAllergies ? PaperDanger : null, 0),
            ("T/A:", sheet.BloodPressure, null, BloodPressureMinWidth),
            ("Temp:", sheet.Temperature, null, TemperatureMinWidth),
            ("Peso:", sheet.Weight, null, WeightMinWidth),
            ("Talla:", sheet.Height, null, HeightMinWidth),
            ("SpO2%:", sheet.OxygenSaturation, null, OxygenMinWidth));
        var footer = BuildPrescriptionFooter(sheet);

        // Lo que queda para el cuerpo se calcula con la altura real de cada bloque: unas alergias largas
        // ocupan dos renglones y con alturas fijas el pie pasaba a otra hoja
        double contentHeight = pageHeight - 2 * PrescriptionSheet.MarginTopBottom;
        double contentWidth = pageWidth - 2 * PrescriptionSheet.MarginLeftRight;
        double fixedHeight = MeasuredHeight(header, contentWidth) + PatientRowGap + MeasuredHeight(patientRow, contentWidth)
                             + VitalsRowGap + MeasuredHeight(vitalsRow, contentWidth) + BodyGap
                             + FooterGap + MeasuredHeight(footer, contentWidth);

        doc.Blocks.Add(Fixed(header, top: 0));
        doc.Blocks.Add(Fixed(patientRow, PatientRowGap));
        doc.Blocks.Add(Fixed(vitalsRow, VitalsRowGap));
        doc.Blocks.Add(BuildPrescriptionBody(sheet, Math.Max(0, contentHeight - fixedHeight - PageSafety), contentWidth));
        doc.Blocks.Add(Fixed(footer, FooterGap));
        return doc;
    }

    private static BlockUIContainer Fixed(UIElement element, double top) => new(element) { Margin = new Thickness(0, top, 0, 0) };

    /// <summary>Alto que ocupará el bloque con el ancho de la hoja (nunca menos que lo previsto).</summary>
    private static double MeasuredHeight(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        return Math.Max(element.DesiredSize.Height, element.MinHeight);
    }

    private static void ApplyPaperText(FrameworkElement element, FontFamily font)
    {
        TextElement.SetFontFamily(element, font);
        TextElement.SetFontSize(element, BodyFontSize);
        TextElement.SetForeground(element, Brushes.Black);
    }

    /// <summary>Escudo a la izquierda; nombre (grande), universidad y cédula al centro; trazo a la derecha.</summary>
    private static Grid BuildPrescriptionHeader(PrescriptionSheet sheet)
    {
        var grid = new Grid { Height = HeaderHeight };
        ApplyPaperText(grid, BodyFontOf(sheet));
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
            FontFamily = DoctorFontOf(sheet),
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
    private static Grid InfoRow(FontFamily font, params (string Label, string Value, Brush? Ink, double MinWidth)[] fields)
    {
        var grid = new Grid { MinHeight = InfoRowHeight };
        ApplyPaperText(grid, font);
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
            // El nombre en negritas; la presentación y lo que se surte, junto a él en letra normal
            var name = new Paragraph(new Bold(new Run(line.Medicine)))
            {
                Margin = new Thickness(0),
                LineHeight = LineHeight,
                KeepWithNext = true
            };
            if (line.Detail.Length > 0) name.Inlines.Add(new Run(line.Detail));
            medicines.Blocks.Add(name);
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
        ApplyPaperText(grid, BodyFontOf(sheet));
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
        string? targetFilePath = null,
        PatientConsultation? consultation = null)
    {
        if (patient == null) return null;

        try
        {
            var sheet = CreatePrescriptionSheet(patient, items, profile, generalNotes, consultation);
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

    /// <summary>"Neue Haas Grotesk Text Pro, Segoe UI, Arial" → "'Neue Haas Grotesk Text Pro', 'Segoe UI', 'Arial', sans-serif".</summary>
    private static string CssFonts(string families, string generic) =>
        string.Join(", ", families.Split(',')
            .Select(f => f.Trim().Replace("'", string.Empty).Replace("<", string.Empty).Replace(";", string.Empty))
            .Where(f => f.Length > 0)
            .Select(f => $"'{f}'")
            .Append(generic));

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
        AppendLetterheadCss(sb, sheet);
        sb.AppendLine(Css($".body {{ flex: 1 0 auto; display: grid; grid-template-columns: 7fr 3fr; column-gap: 10px; margin-top: {BodyGap}px; }}"));
        sb.AppendLine(".meds, .measures { background: #F0F0F0; padding: 6px 8px; }");
        sb.AppendLine(Css($".med {{ break-inside: avoid; margin-bottom: {LineHeight}px; }}"));
        sb.AppendLine(".med:last-child { margin-bottom: 0; }");
        sb.AppendLine(".med-name { font-weight: 700; }");
        sb.AppendLine(".med-detail { font-weight: 400; }");
        sb.AppendLine(".measures-title { font-weight: 700; text-align: center; font-size: 9pt; margin-bottom: 4px; }");
        sb.AppendLine(".measures-text { white-space: pre-line; }");
        AppendLetterheadPrintCss(sb, sheet);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"sheet\">");

        AppendLetterheadHeaderHtml(sb, sheet);

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
            var detail = line.Detail.Length > 0 ? $"<span class=\"med-detail\">{H(line.Detail)}</span>" : string.Empty;
            sb.AppendLine($"        <div class=\"med-name\">{H(line.Medicine)}{detail}</div>");
            sb.AppendLine($"        <div class=\"med-posology\">{H(line.Posology)}</div>");
            sb.AppendLine("      </div>");
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"measures\">");
        sb.AppendLine("      <div class=\"measures-title\">Medidas generales:</div>");
        sb.AppendLine($"      <div class=\"measures-text\">{H(sheet.GeneralMeasures.Replace("\r\n", "\n"))}</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");

        AppendLetterheadFooterHtml(sb, sheet);

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// Estilos del membrete que comparten la receta y la solicitud de laboratorio: página, hoja, encabezado y los
    /// renglones de etiquetas con campos sombreados.
    /// </summary>
    private static void AppendLetterheadCss(StringBuilder sb, PrescriptionSheet sheet)
    {
        sb.AppendLine(Css($"@page {{ size: {PrescriptionSheet.PageWidthInches}in {sheet.PaperHeightInches}in; margin: {PrescriptionSheet.MarginTopBottomInches}in {PrescriptionSheet.MarginLeftRightInches}in; }}"));
        sb.AppendLine("* { box-sizing: border-box; }");
        sb.AppendLine("html { background: #e5e7eb; }");
        sb.AppendLine("body { margin: 0; padding: 24px 0; font-family: " + CssFonts(sheet.BodyFont, "sans-serif") + "; font-size: 10pt; line-height: 16px; color: #000; -webkit-print-color-adjust: exact; print-color-adjust: exact; }");
        sb.AppendLine(Css($".sheet {{ width: {PrescriptionSheet.PageWidthInches}in; min-height: {sheet.PaperHeightInches}in; margin: 0 auto; padding: {PrescriptionSheet.MarginTopBottomInches}in {PrescriptionSheet.MarginLeftRightInches}in; background: #fff; box-shadow: 0 2px 12px rgba(0, 0, 0, 0.25); display: flex; flex-direction: column; }}"));
        sb.AppendLine(Css($".header {{ position: relative; height: {HeaderHeight}px; }}"));
        sb.AppendLine(Css($".crest {{ position: absolute; left: 0; top: 4px; width: {CrestSize}px; height: {CrestSize}px; object-fit: contain; }}"));
        sb.AppendLine(Css($".titles {{ margin-left: {CrestSize + 16}px; text-align: center; }}"));
        sb.AppendLine(Css($".doctor {{ height: 48px; line-height: 48px; font-family: {CssFonts(sheet.DoctorFont, "cursive")}; font-size: {DoctorFontPx(sheet.DoctorName)}px; color: #0369A1; white-space: nowrap; }}"));
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
    }

    /// <summary>Estilos del pie (teléfono y domicilio) y de la impresión, comunes a la receta y a la solicitud de laboratorio.</summary>
    private static void AppendLetterheadPrintCss(StringBuilder sb, PrescriptionSheet sheet)
    {
        sb.AppendLine(Css($".footer {{ display: grid; grid-template-columns: 4fr 6fr; column-gap: 10px; margin-top: {FooterGap}px; font-size: 9pt; break-inside: avoid; }}"));
        sb.AppendLine(Css($".footer > div {{ background: #F0F0F0; min-height: {FooterHeight}px; padding: 2px 8px; display: flex; align-items: center; justify-content: center; text-align: center; }}"));
        sb.AppendLine(".footer img { width: 16px; height: 16px; margin-right: 6px; flex: none; }");
        sb.AppendLine(Css($"@media print {{ html {{ background: #fff; }} body {{ padding: 0; }} .sheet {{ width: auto; min-height: calc({sheet.PaperHeightInches}in - {2 * PrescriptionSheet.MarginTopBottomInches + 0.03:0.###}in); padding: 0; margin: 0; box-shadow: none; }} }}"));
    }

    /// <summary>Encabezado: escudo, nombre del médico, universidad, cédula y el trazo de la derecha.</summary>
    private static void AppendLetterheadHeaderHtml(StringBuilder sb, PrescriptionSheet sheet)
    {
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
    }

    /// <summary>Pie: teléfono (WhatsApp) y domicilio (ubicación).</summary>
    private static void AppendLetterheadFooterHtml(StringBuilder sb, PrescriptionSheet sheet)
    {
        sb.AppendLine("  <div class=\"footer\">");
        sb.AppendLine(FooterField("phone", "telefono", "icon-whatsapp", PrescriptionSheet.WhatsAppIconPng, sheet.Phone));
        sb.AppendLine(FooterField("address", "domicilio", "icon-location", PrescriptionSheet.LocationIconPng, sheet.Address));
        sb.AppendLine("  </div>");
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
