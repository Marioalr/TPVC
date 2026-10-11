using System;
using System.IO;
using System.Linq;
using System.Text;
using TPVC.Models;
using static TPVC.Services.Documents.LetterheadLayout;

namespace TPVC.Services.Documents;

/// <summary>
/// Piezas del HTML que comparten la receta y la solicitud de laboratorio (paso 8.11; antes en PrintingService): la
/// página, el encabezado con escudo, nombre, universidad y cédula, los renglones de etiquetas con campos sombreados y el
/// pie con teléfono y domicilio. Sin WPF: es la referencia del PDF que arma la app de Android.
/// </summary>
public static class LetterheadHtml
{
    /// <summary>Texto seguro para HTML (un "&lt;" en una dosis o un nombre ya no rompe el documento).</summary>
    internal static string H(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>"Neue Haas Grotesk Text Pro, Segoe UI, Arial" → "'Neue Haas Grotesk Text Pro', 'Segoe UI', 'Arial', sans-serif".</summary>
    internal static string CssFonts(string families, string generic) =>
        string.Join(", ", families.Split(',')
            .Select(f => f.Trim().Replace("'", string.Empty).Replace("<", string.Empty).Replace(";", string.Empty))
            .Where(f => f.Length > 0)
            .Select(f => $"'{f}'")
            .Append(generic));

    /// <summary>Regla de CSS con números en formato invariable (0.197in, nunca 0,197in).</summary>
    internal static string Css(FormattableString rule) => FormattableString.Invariant(rule);

    /// <summary>
    /// Estilos del membrete que comparten la receta y la solicitud de laboratorio: página, hoja, encabezado y los
    /// renglones de etiquetas con campos sombreados.
    /// </summary>
    internal static void AppendLetterheadCss(StringBuilder sb, PrescriptionSheet sheet)
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
    internal static void AppendLetterheadPrintCss(StringBuilder sb, PrescriptionSheet sheet)
    {
        sb.AppendLine(Css($".footer {{ display: grid; grid-template-columns: 4fr 6fr; column-gap: 10px; margin-top: {FooterGap}px; font-size: 9pt; break-inside: avoid; }}"));
        sb.AppendLine(Css($".footer > div {{ background: #F0F0F0; min-height: {FooterHeight}px; padding: 2px 8px; display: flex; align-items: center; justify-content: center; text-align: center; }}"));
        sb.AppendLine(".footer img { width: 16px; height: 16px; margin-right: 6px; flex: none; }");
        sb.AppendLine(Css($"@media print {{ html {{ background: #fff; }} body {{ padding: 0; }} .sheet {{ width: auto; min-height: calc({sheet.PaperHeightInches}in - {2 * PrescriptionSheet.MarginTopBottomInches + 0.03:0.###}in); padding: 0; margin: 0; box-shadow: none; }} }}"));
    }

    /// <summary>Encabezado: escudo, nombre del médico, universidad, cédula y el trazo de la derecha.</summary>
    internal static void AppendLetterheadHeaderHtml(StringBuilder sb, PrescriptionSheet sheet)
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
    internal static void AppendLetterheadFooterHtml(StringBuilder sb, PrescriptionSheet sheet)
    {
        sb.AppendLine("  <div class=\"footer\">");
        sb.AppendLine(FooterField("phone", "telefono", "icon-whatsapp", PrescriptionSheet.WhatsAppIconPng, sheet.Phone));
        sb.AppendLine(FooterField("address", "domicilio", "icon-location", PrescriptionSheet.LocationIconPng, sheet.Address));
        sb.AppendLine("  </div>");
    }

    /// <summary>Etiqueta y valor sombreado; el valor lleva data-field con su nombre.</summary>
    internal static string Field(string label, string field, string widthClass, string value, bool first = false, bool alert = false)
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
            var mime = ImageExtension(bytes) switch
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

    /// <summary>".png" o ".jpg" según los primeros bytes; null si no es ninguna de las dos.</summary>
    public static string? ImageExtension(byte[]? content)
    {
        if (content == null || content.Length < 8) return null;
        if (content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47) return ".png";
        if (content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF) return ".jpg";
        return null;
    }

    /// <summary>
    /// Tamaño del nombre del médico en el HTML: grande como en el Excel y más chico si es muy largo, para que
    /// quepa en un renglón (en la impresión lo ajusta un Viewbox).
    /// </summary>
    internal static int DoctorFontPx(string name)
    {
        const double available = 600; // ancho del renglón sin el escudo, en px
        int length = Math.Max(1, name.Trim().Length);
        return (int)Math.Clamp(Math.Floor(available / (length * 0.55)), 16, 30);
    }
}
