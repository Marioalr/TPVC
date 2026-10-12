using System.Globalization;
using System.Text;
using TPVC.Models;
using static TPVC.Services.Documents.LetterheadHtml;

namespace TPVC.Services.Documents;

/// <summary>
/// El ticket de venta en HTML para papel térmico de 80 mm (paso 8.11): los mismos renglones que
/// <see cref="TicketText"/> y que la impresión de la PC, con las partidas en tabla. Es lo que imprime la app de Android
/// (WebView solo para imprimir; el PDF para compartir lo dibuja con los vectores de ticket-text). Sin WPF.
/// </summary>
public static class TicketHtml
{
    /// <summary>HTML del ticket (también XML bien formado). Los datos llevan data-field: folio, folio-sin-conexion (paso 8.17, solo si lo hay), fecha, paciente, pago, atendio, articulos, total.</summary>
    public static string Build(SaleReceipt receipt, ClinicProfile? profile)
    {
        profile ??= new ClinicProfile();
        var header = TicketText.HeaderLines(profile);
        var meta = TicketText.MetaLines(receipt);
        var totals = TicketText.TotalLines(receipt);
        string[] metaFields = string.IsNullOrWhiteSpace(receipt.OriginalFolio)
            ? new[] { "folio", "fecha", "paciente", "pago", "atendio" }
            : new[] { "folio", "folio-sin-conexion", "fecha", "paciente", "pago", "atendio" };

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"es\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>Ticket {H(receipt.Folio)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("@page { size: 80mm auto; margin: 4mm; }");
        sb.AppendLine("body { margin: 0; font-family: 'Consolas', 'Courier New', monospace; font-size: 10.5px; line-height: 1.35; color: #000; background: #fff; }");
        sb.AppendLine(".ticket { width: 72mm; }");
        sb.AppendLine(".header, .footer { text-align: center; }");
        sb.AppendLine(".header { margin-bottom: 8px; } .meta, .items, .totals { margin-bottom: 8px; } .footer { margin-top: 4px; }");
        sb.AppendLine(".clinic, .folio, .paid, th { font-weight: 700; }");
        sb.AppendLine(".line, .rule { white-space: pre-wrap; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; } th, td { text-align: left; vertical-align: top; padding: 0; } .num { text-align: right; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"ticket\">");

        sb.AppendLine("  <div class=\"header\">");
        sb.AppendLine($"    <div class=\"clinic\">{H(header[0])}</div>");
        for (int i = 1; i < header.Count; i++) sb.AppendLine($"    <div>{H(header[i])}</div>");
        sb.AppendLine($"    <div class=\"rule\">{TicketText.ThinRule}</div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <div class=\"meta\">");
        for (int i = 0; i < meta.Count; i++)
        {
            sb.AppendLine($"    <div class=\"line{(i == 0 ? " folio" : string.Empty)}\" data-field=\"{metaFields[i]}\">{H(meta[i])}</div>");
        }
        sb.AppendLine($"    <div class=\"rule\">{TicketText.ThickRule}</div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <table class=\"items\">");
        sb.AppendLine("    <tr>" +
                      $"<th>{H(TicketText.ItemHeaders[0])}</th><th>{H(TicketText.ItemHeaders[1])}</th>" +
                      $"<th class=\"num\">{H(TicketText.ItemHeaders[2])}</th><th class=\"num\">{H(TicketText.ItemHeaders[3])}</th></tr>");
        foreach (var item in receipt.Items)
        {
            sb.AppendLine("    <tr>" +
                          $"<td>{item.Quantity.ToString(CultureInfo.InvariantCulture)}</td><td>{H(item.Name)}</td>" +
                          $"<td class=\"num\">{H(TicketText.Money(item.UnitPrice))}</td><td class=\"num\">{H(TicketText.Money(item.Subtotal))}</td></tr>");
        }
        sb.AppendLine("  </table>");

        sb.AppendLine("  <div class=\"totals\">");
        sb.AppendLine($"    <div class=\"rule\">{TicketText.ThinRule}</div>");
        sb.AppendLine($"    <div class=\"line\" data-field=\"articulos\">{H(totals[0])}</div>");
        sb.AppendLine($"    <div class=\"line paid\" data-field=\"total\">{H(totals[1])}</div>");
        sb.AppendLine($"    <div class=\"rule\">{TicketText.ThickRule}</div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <div class=\"footer\">");
        foreach (var line in TicketText.FooterLines) sb.AppendLine($"    <div>{H(line)}</div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }
}
