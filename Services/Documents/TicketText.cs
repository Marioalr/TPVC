using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TPVC.Models;

namespace TPVC.Services.Documents;

/// <summary>
/// El ticket de venta en texto de 42 columnas (paso 8.11), a partir de <see cref="SaleReceipt"/>: lo mismo que imprime
/// la PC en la impresora térmica (cabecera, folio, fecha, paciente, pago, quién atendió, partidas, totales y pie), en el
/// mismo orden y con los mismos textos. La impresión con WPF toma de aquí sus renglones. Sin WPF: de aquí salen los
/// vectores <c>contracts/golden/ticket-text.json</c> del ticket del celular.
/// </summary>
public static class TicketText
{
    /// <summary>Ancho del papel térmico de 80 mm en caracteres.</summary>
    public const int Width = 42;

    public static readonly string ThinRule = new('-', Width);
    public static readonly string ThickRule = new('=', Width);

    public const string DefaultClinicName = "CONSULTORIO MÉDICO TPVC";
    public const string DefaultDoctorName = "Dr. Titular";

    public static readonly IReadOnlyList<string> FooterLines = new[]
    {
        "¡Gracias por su visita y confianza!",
        "Comprobante de atención médica y farmacia"
    };

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    // Columnas de las partidas: Cant (4) · Descripción (17) · P.U. (9) · Total (9), con un espacio entre ellas
    private const int QuantityWidth = 4;
    private const int DescriptionWidth = 17;
    private const int MoneyWidth = 9;

    /// <summary>Cabecera: el consultorio en mayúsculas, el médico con su cédula, domicilio y teléfono (los que haya).</summary>
    public static IReadOnlyList<string> HeaderLines(ClinicProfile profile)
    {
        string clinicName = !string.IsNullOrWhiteSpace(profile.ClinicName) ? profile.ClinicName : DefaultClinicName;
        string doctorName = !string.IsNullOrWhiteSpace(profile.DoctorName) ? profile.DoctorName : DefaultDoctorName;
        string license = !string.IsNullOrWhiteSpace(profile.ProfessionalLicense) ? profile.ProfessionalLicense : string.Empty;

        var lines = new List<string> { clinicName.ToUpperInvariant() };
        lines.Add(string.IsNullOrWhiteSpace(license) ? doctorName : $"{doctorName} • Céd. {license}");
        if (!string.IsNullOrWhiteSpace(profile.Address)) lines.Add(profile.Address);
        if (!string.IsNullOrWhiteSpace(profile.Phone)) lines.Add($"Tel: {profile.Phone}");
        return lines;
    }

    /// <summary>Folio, fecha, paciente, forma de pago y quién atendió.</summary>
    public static IReadOnlyList<string> MetaLines(SaleReceipt receipt) => new[]
    {
        $"FOLIO:    {receipt.Folio}",
        $"FECHA:    {receipt.Date.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}",
        $"PACIENTE: {receipt.PatientName}",
        $"PAGO:     {receipt.PaymentMethod}",
        $"ATENDIÓ:  {receipt.Cashier}"
    };

    public static readonly IReadOnlyList<string> ItemHeaders = new[] { "Cant", "Descripción", "P.U.", "Total" };

    /// <summary>Un importe de la partida ("$12.50", sin separador de miles).</summary>
    public static string Money(decimal amount) => "$" + amount.ToString("F2", Mexican);

    /// <summary>Total de artículos y total pagado ("$1,234.50 MXN").</summary>
    public static IReadOnlyList<string> TotalLines(SaleReceipt receipt) => new[]
    {
        $"TOTAL DE ARTÍCULOS: {receipt.TotalItems}",
        $"TOTAL PAGADO:       ${receipt.TotalAmount.ToString("N2", Mexican)} MXN"
    };

    /// <summary>El ticket completo, renglón por renglón (sin salto al final).</summary>
    public static IReadOnlyList<string> Lines(SaleReceipt receipt, ClinicProfile? profile)
    {
        profile ??= new ClinicProfile();
        var lines = new List<string>();
        foreach (var line in HeaderLines(profile)) lines.AddRange(Wrap(line, Width).Select(Center));
        lines.Add(ThinRule);
        lines.AddRange(MetaLines(receipt).SelectMany(l => Wrap(l, Width)));
        lines.Add(ThickRule);
        lines.Add(ItemRow(ItemHeaders[0], ItemHeaders[1], ItemHeaders[2], ItemHeaders[3]));
        foreach (var item in receipt.Items)
        {
            var description = Wrap(item.Name, DescriptionWidth);
            lines.Add(ItemRow(item.Quantity.ToString(CultureInfo.InvariantCulture), description[0], Money(item.UnitPrice), Money(item.Subtotal)));
            foreach (var rest in description.Skip(1)) lines.Add(ItemRow(string.Empty, rest, string.Empty, string.Empty));
        }
        lines.Add(ThinRule);
        lines.AddRange(TotalLines(receipt));
        lines.Add(ThickRule);
        foreach (var line in FooterLines) lines.AddRange(Wrap(line, Width).Select(Center));
        return lines;
    }

    /// <summary>El ticket en texto, con "\n" entre renglones y al final.</summary>
    public static string Build(SaleReceipt receipt, ClinicProfile? profile) =>
        string.Join("\n", Lines(receipt, profile)) + "\n";

    private static string ItemRow(string quantity, string description, string unitPrice, string total)
    {
        var row = new StringBuilder();
        row.Append(quantity.PadLeft(QuantityWidth)).Append(' ');
        row.Append(description.PadRight(DescriptionWidth)).Append(' ');
        row.Append(unitPrice.PadLeft(MoneyWidth)).Append(' ');
        row.Append(total.PadLeft(MoneyWidth));
        return row.ToString().TrimEnd();
    }

    private static string Center(string line)
    {
        if (line.Length >= Width) return line;
        return new string(' ', (Width - line.Length) / 2) + line;
    }

    /// <summary>
    /// Parte un texto en renglones de <paramref name="width"/> como máximo: en el último espacio que quepa (el espacio
    /// no pasa a ningún renglón) o, si una palabra es más larga, a la mitad de ella. Lo que cabe conserva sus espacios
    /// ("FOLIO:    TKT-…").
    /// </summary>
    public static List<string> Wrap(string? text, int width)
    {
        var lines = new List<string>();
        var rest = (text ?? string.Empty).TrimEnd();
        while (rest.Length > width)
        {
            int cut = rest.LastIndexOf(' ', width);
            if (cut > 0 && rest[..cut].Trim().Length > 0)
            {
                lines.Add(rest[..cut].TrimEnd());
                rest = rest[(cut + 1)..].TrimStart();
            }
            else
            {
                lines.Add(rest[..width]);
                rest = rest[width..].TrimStart();
            }
        }
        lines.Add(rest);
        return lines;
    }
}
