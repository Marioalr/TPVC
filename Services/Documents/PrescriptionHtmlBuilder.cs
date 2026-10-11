using System.Text;
using TPVC.Models;
using static TPVC.Services.Documents.LetterheadHtml;
using static TPVC.Services.Documents.LetterheadLayout;

namespace TPVC.Services.Documents;

/// <summary>
/// La receta en HTML (paso 8.11; antes PrintingService.BuildPrescriptionHtml), con el mismo formato que la impresión:
/// media carta horizontal (o carta), al imprimirlo o guardarlo como PDF desde el navegador. Sin WPF: de aquí salen los
/// vectores <c>contracts/golden/prescription-html-fields.json</c> que sigue el PDF de la app de Android.
/// </summary>
public static class PrescriptionHtmlBuilder
{
    /// <summary>
    /// HTML de la receta (también XML bien formado, para revisarlo en las pruebas). Cada valor lleva data-field con su
    /// nombre: paciente, edad, fecha, alergias, ta, temp, peso, talla, spo2, telefono, domicilio. Las imágenes van dentro.
    /// </summary>
    public static string Build(PrescriptionSheet sheet)
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
}
