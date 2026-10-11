using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TPVC.Services;

/// <summary>Un renglón del pedido: lo que dice el correo del proveedor.</summary>
public sealed record SupplierOrderLine(string Description, string Sku, int Quantity, decimal LineTotal)
{
    /// <summary>Costo por unidad: el total del renglón entre la cantidad, al centavo.</summary>
    public decimal UnitCost => Quantity > 0 ? Math.Round(LineTotal / Quantity, 2, MidpointRounding.AwayFromZero) : 0m;
}

/// <summary>El pedido leído del correo: número, renglones y los totales que trae el correo (si los trae).</summary>
public sealed class SupplierOrder
{
    public string? OrderNumber { get; init; }
    public decimal? Subtotal { get; init; }
    public decimal? Tax { get; init; }
    public decimal? Total { get; init; }
    public IReadOnlyList<SupplierOrderLine> Lines { get; init; } = Array.Empty<SupplierOrderLine>();

    public decimal LinesTotal => Lines.Sum(l => l.LineTotal);

    /// <summary>
    /// La suma de los renglones es el subtotal del correo (o su total, si no trae subtotal): se leyeron todos. Null si
    /// el correo no trae ninguno de los dos.
    /// </summary>
    public bool? MatchesEmailTotal => (Subtotal ?? Total) is { } expected ? LinesTotal == expected : null;
}

/// <summary>
/// Lee el pedido de un correo de proveedor pegado como texto (la primera versión: el formato de "Detalles de la Compra"
/// de LUIN, con Producto, SKU, Cantidad y Total por renglón). Sirve igual si al copiar cada celda queda en su línea, en
/// la misma línea separada por tabuladores o por espacios: el texto se junta en una sola línea y cada renglón se
/// reconoce por su forma (descripción, SKU, cantidad y un importe con centavos).
/// </summary>
public static class SupplierOrderParser
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Un importe con centavos: "$1,393.09", "$ 145.02" o "145.02"
    private const string Money = @"\$?\s?(\d{1,3}(?:,\d{3})+\.\d{2}|\d+\.\d{2})";

    // Descripción, SKU (letras y números pegados, en mayúsculas: WAN014), cantidad y total del renglón
    private static readonly Regex LineRegex = new(
        @"(?<desc>\S.*?)\s+(?<sku>[A-Z]{2,6}\d{2,6})\s+(?<qty>\d{1,4})\s+\$?\s?(?<total>\d{1,3}(?:,\d{3})+\.\d{2}|\d+\.\d{2})(?=\s|$)",
        RegexOptions.CultureInvariant);

    private static readonly Regex HeaderRegex = new(@"Producto\s+SKU\s+Cantidad\s+Total", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Lo que viene después de la tabla en el correo
    private static readonly Regex TableEndRegex = new(
        @"Producto\s+Sujeto\s+a\s+Existencia|Cliente\s*:|Resumen\s+de\s+la\s+Compra|Subtotal\s+\$?\s?\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OrderNumberRegex = new(@"N[uú]mero\s+de\s+Orden\s*:?\s*#?\s*([A-Za-z0-9-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SubtotalRegex = new(@"Subtotal\s*:?\s*" + Money, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // El renglón "IVA $0.00", no el de "Total + IVA $1,393.09"
    private static readonly Regex TaxRegex = new(@"(?<!\+\s*)\bIVA\s*:?\s*" + Money, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TotalWithTaxRegex = new(@"Total\s*\+\s*IVA\s*:?\s*" + Money, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    public static SupplierOrder Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new SupplierOrder();

        // Una sola línea: cada celda pudo quedar en su renglón, separada por tabuladores o con espacios duros
        var flat = Whitespace.Replace(text.Replace(' ', ' '), " ").Trim();

        var header = HeaderRegex.Match(flat);
        int start = header.Success ? header.Index + header.Length : 0;
        var end = TableEndRegex.Match(flat, start);
        var table = flat[start..(end.Success ? end.Index : flat.Length)];

        var lines = new List<SupplierOrderLine>();
        foreach (Match m in LineRegex.Matches(table))
        {
            int quantity = int.Parse(m.Groups["qty"].Value, Invariant);
            if (quantity <= 0) continue;
            lines.Add(new SupplierOrderLine(m.Groups["desc"].Value.Trim(), m.Groups["sku"].Value, quantity, ParseMoney(m.Groups["total"].Value)));
        }

        return new SupplierOrder
        {
            OrderNumber = OrderNumberRegex.Match(flat) is { Success: true } order ? order.Groups[1].Value : null,
            Subtotal = LastMoney(SubtotalRegex, flat),
            Tax = LastMoney(TaxRegex, flat),
            Total = LastMoney(TotalWithTaxRegex, flat),
            Lines = lines
        };
    }

    private static decimal ParseMoney(string value) => decimal.Parse(value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, Invariant);

    private static decimal? LastMoney(Regex regex, string text) =>
        regex.Matches(text).LastOrDefault() is { } m ? ParseMoney(m.Groups[1].Value) : null;

    /// <summary>
    /// La presentación que se puede leer de la descripción: "C/14 TABS" → "Caja con 14 tabletas". Null si no la trae.
    /// </summary>
    public static string? PresentationOf(string description)
    {
        var m = Regex.Match(description, @"\bC/\s?(\d+)\s*([A-Za-z]+)?", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var unit = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "TABS" or "TAB" or "TABLETAS" => "tabletas",
            "CAPS" or "CAP" or "CAPSULAS" => "cápsulas",
            "AMP" or "AMPS" or "AMPOLLETAS" => "ampolletas",
            "GRAG" or "GRAGEAS" => "grageas",
            "SOB" or "SOBRES" => "sobres",
            "" => "piezas",
            var other => other.ToLowerInvariant()
        };
        return $"Caja con {m.Groups[1].Value} {unit}";
    }
}
