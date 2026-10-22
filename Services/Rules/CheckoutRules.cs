using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TPVC.Services.Rules;

/// <summary>
/// Las cuentas del cobro (paso 8.26; antes dentro de <c>CheckoutViewModel</c>, sin cambios), las mismas en el Punto de venta y
/// en "Terminar consulta": cambio, lo que falta, si se puede cobrar, el texto del botón, el recibido que se escribe y los
/// billetes sugeridos. Sin WPF ni base: vectores en <c>contracts/golden/checkout.json</c> (que también corren la cuenta real:
/// agregar, sumar y quitar renglones).
/// </summary>
public static class CheckoutRules
{
    public const string Cash = "Efectivo";

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private static readonly decimal[] CashSteps = { 50m, 100m, 200m, 500m, 1000m };

    /// <summary>Se paga en efectivo: la forma de pago es exactamente "Efectivo".</summary>
    public static bool IsCash(string? paymentMethod) => paymentMethod == Cash;

    /// <summary>
    /// Hasta 3 billetes probables por encima del total para escribir el recibido de un clic: el total redondeado hacia arriba a
    /// múltiplos de 50, 100, 200, 500 y 1000, los que pasan del total, sin repetir y de menor a mayor. Nada con total 0.
    /// </summary>
    public static IReadOnlyList<decimal> SuggestCashAmounts(decimal total)
    {
        if (total <= 0) return Array.Empty<decimal>();
        return CashSteps
            .Select(step => Math.Ceiling(total / step) * step)
            .Where(amount => amount > total)
            .Distinct()
            .OrderBy(amount => amount)
            .Take(3)
            .ToList();
    }

    /// <summary>
    /// El recibido como se escribe ("500", "$1,000", "250.5", "150,50"): solo cuentan los dígitos 0-9, el punto y la coma. Sin
    /// punto y con una sola coma seguida de 1 o 2 dígitos al final, la coma es el punto decimal ("1,5" = 1.50, "150,50" =
    /// 150.50; revisión 20: antes la coma siempre se quitaba y "1,5" era 15); si no, las comas separan miles ("1,500" = 1500,
    /// "1,500.50" = 1500.50). Redondeado a 2 decimales (al par); 0 si no es un número mayor que 0 (vacío = paga exacto).
    /// </summary>
    public static decimal ParseAmount(string? text)
    {
        var clean = new string((text ?? string.Empty).Where(c => c is >= '0' and <= '9' or '.' or ',').ToArray());
        int comma = clean.IndexOf(',');
        bool decimalComma = comma >= 0 && !clean.Contains('.') && comma == clean.LastIndexOf(',')
                            && clean.Length - comma - 1 is 1 or 2;
        clean = decimalComma ? clean.Replace(',', '.') : clean.Replace(",", string.Empty);
        return decimal.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && amount > 0
            ? Math.Round(amount, 2)
            : 0m;
    }

    /// <summary>El texto del recibido después de elegirlo con un botón: vacío en 0, si no "0.##" invariante.</summary>
    public static string AmountText(decimal value) => value == 0 ? string.Empty : value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>El cambio que se muestra: lo recibido menos el total, nunca negativo (con cualquier forma de pago).</summary>
    public static decimal ChangeDue(decimal received, decimal total) => Math.Max(0m, received - total);

    /// <summary>Se muestra el cambio: efectivo, lo recibido alcanza y hay algo que cobrar.</summary>
    public static bool HasChange(bool isCash, decimal received, decimal total) => isCash && received >= total && total > 0;

    /// <summary>Lo que falta cuando se escribió un efectivo menor al total (0 = paga exacto o alcanza).</summary>
    public static decimal MissingAmount(bool isCash, decimal received, decimal total) =>
        isCash && received > 0 && received < total ? total - received : 0m;

    /// <summary>Se puede cobrar: hay renglones (o la cuenta vacía se permite, al terminar una consulta) y no falta efectivo.</summary>
    public static bool CanCharge(bool hasItems, bool allowsEmpty, decimal missingAmount) => (hasItems || allowsEmpty) && !(missingAmount > 0);

    /// <summary>"Cobrar $1,234.50" (moneda de es-MX) más el complemento, o el texto de la cuenta vacía si se permite y no hay renglones.</summary>
    public static string ChargeText(bool hasItems, decimal total, string? suffix, string? emptyChargeText) =>
        hasItems || emptyChargeText == null
            ? $"Cobrar {total.ToString("C2", Mexican)}{suffix}"
            : emptyChargeText;

    /// <summary>"Faltan $50.00 en el efectivo recibido." al intentar cobrar con efectivo de menos.</summary>
    public static string MissingText(decimal missingAmount) => $"Faltan {missingAmount.ToString("C2", Mexican)} en el efectivo recibido.";

    /// <summary>Lo que dice el recibo: en efectivo, lo recibido (o el total si no se escribió nada) y el cambio; con otra forma, nada.</summary>
    public static (decimal? Received, decimal? Change) ReceiptCash(bool isCash, decimal received, decimal total)
    {
        decimal? cash = isCash ? (received > 0 ? received : total) : null;
        return (cash, cash.HasValue ? Math.Max(0m, cash.Value - total) : null);
    }

    /// <summary>La cantidad escrita a mano en un renglón: mínimo 1 y, en un producto con existencias conocidas, no más de lo que hay.</summary>
    public static int ClampQuantity(int value, bool isService, int availableStock)
    {
        if (value < 1) return 1;
        if (!isService && availableStock > 0 && value > availableStock) return availableStock;
        return value;
    }
}
