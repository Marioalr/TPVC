using System.Collections.Generic;
using System.Linq;
using System.Text;
using TPVC.Models;
using static TPVC.Services.Documents.LetterheadHtml;
using static TPVC.Services.Documents.LetterheadLayout;

namespace TPVC.Services.Documents;

/// <summary>
/// La solicitud de estudios de laboratorio en HTML (paso 8.11; antes PrintingService.BuildLabOrderHtml), con el membrete
/// de la receta. Sin WPF.
/// </summary>
public static class LabOrderHtmlBuilder
{
    /// <summary>
    /// Las áreas en una columna o, si son muchos renglones, en dos parejas: se llena la primera hasta la mitad de los
    /// renglones y un área nunca se parte entre columnas. La usan el HTML y la impresión.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<LabOrderSheetGroup>> SplitInColumns(IReadOnlyList<LabOrderSheetGroup> groups)
    {
        int Lines(LabOrderSheetGroup g) => 1 + g.Studies.Count;
        int total = groups.Sum(Lines);
        if (total <= SingleColumnLines || groups.Count < 2) return new[] { groups };

        var first = new List<LabOrderSheetGroup>();
        int used = 0;
        foreach (var group in groups)
        {
            // Pasa a la segunda columna cuando la primera ya llegó a la mitad (siempre queda algo para la segunda)
            if (first.Count > 0 && used + Lines(group) / 2.0 > total / 2.0) break;
            first.Add(group);
            used += Lines(group);
        }
        if (first.Count == groups.Count) first.RemoveAt(first.Count - 1);
        return new IReadOnlyList<LabOrderSheetGroup>[] { first, groups.Skip(first.Count).ToList() };
    }

    /// <summary>HTML de la solicitud (también XML bien formado). Los campos llevan data-field: paciente, edad, fecha, sexo, diagnostico, telefono, domicilio.</summary>
    public static string Build(LabOrderSheet sheet)
    {
        var letterhead = sheet.Letterhead;
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"es\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>{H(LabOrderSheet.Title)} - {H(letterhead.PatientName)}</title>");
        sb.AppendLine("<style>");
        AppendLetterheadCss(sb, letterhead);
        sb.AppendLine(".f-dx { flex: 1 1 0; }");
        sb.AppendLine(Css($".f-sex {{ min-width: {SexMinWidth}px; }}"));
        sb.AppendLine(Css($".title {{ margin-top: {TitleGap}px; text-align: center; font-weight: 700; color: #0369A1; text-transform: uppercase; }}"));
        sb.AppendLine(Css($".body {{ flex: 1 0 auto; display: grid; grid-template-columns: 7fr 3fr; column-gap: 10px; margin-top: {BodyGap}px; }}"));
        sb.AppendLine(".studies, .indications { background: #F0F0F0; padding: 6px 8px; }");
        sb.AppendLine(".studies.two { column-count: 2; column-gap: 16px; }");
        sb.AppendLine(".area { break-inside: avoid; }");
        sb.AppendLine(Css($".area + .area {{ margin-top: {AreaGap}px; }}"));
        sb.AppendLine(".area-title { font-weight: 700; font-size: 9pt; color: #5A687D; text-transform: uppercase; }");
        sb.AppendLine(".study::before { content: \"•\\00a0\\00a0\"; }");
        sb.AppendLine(".indications-title { font-weight: 700; text-align: center; font-size: 9pt; margin-bottom: 4px; }");
        sb.AppendLine(".indications-text { white-space: pre-line; }");
        AppendLetterheadPrintCss(sb, letterhead);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"sheet\">");

        AppendLetterheadHeaderHtml(sb, letterhead);

        sb.AppendLine("  <div class=\"row patient\">");
        sb.AppendLine(Field("Paciente:", "paciente", "f-patient", letterhead.PatientName, first: true));
        sb.AppendLine(Field("Edad:", "edad", "f-age fit", letterhead.Age));
        sb.AppendLine(Field("Fecha:", "fecha", "f-date fit", letterhead.Date));
        sb.AppendLine("  </div>");

        sb.AppendLine("  <div class=\"row vitals\">");
        sb.AppendLine(Field("Sexo:", "sexo", "f-sex fit", sheet.Sex, first: true));
        sb.AppendLine(Field("Diagnóstico:", "diagnostico", "f-dx", sheet.Diagnosis));
        sb.AppendLine("  </div>");

        sb.AppendLine($"  <div class=\"title\">{H(LabOrderSheet.Title)}</div>");

        bool twoColumns = SplitInColumns(sheet.Groups).Count > 1;
        sb.AppendLine("  <div class=\"body\">");
        sb.AppendLine($"    <div class=\"studies{(twoColumns ? " two" : string.Empty)}\">");
        foreach (var group in sheet.Groups)
        {
            sb.AppendLine("      <div class=\"area\">");
            sb.AppendLine($"        <div class=\"area-title\">{H(group.Area)}</div>");
            foreach (var study in group.Studies)
            {
                sb.AppendLine($"        <div class=\"study\">{H(study)}</div>");
            }
            sb.AppendLine("      </div>");
        }
        sb.AppendLine("    </div>");
        sb.AppendLine("    <div class=\"indications\">");
        sb.AppendLine("      <div class=\"indications-title\">Indicaciones:</div>");
        sb.AppendLine($"      <div class=\"indications-text\">{H(sheet.Indications.Replace("\r\n", "\n"))}</div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </div>");

        AppendLetterheadFooterHtml(sb, letterhead);

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }
}
