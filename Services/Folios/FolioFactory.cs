using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TPVC.Services.Folios;

/// <summary>Los folios que llevan un consecutivo: el expediente (EXP) y el ticket (TKT).</summary>
public enum FolioKind
{
    /// <summary>EXP-2026-001: una serie por año, 3 dígitos.</summary>
    Patient,

    /// <summary>TKT-20261009-0001: una serie por día, 4 dígitos.</summary>
    Sale
}

/// <summary>
/// Paso 8.16 (respuesta 1 del médico): cómo se arma cada folio, sin base, red ni reloj (Kotlin lo repite con
/// contracts/golden/folios.json).
/// <list type="bullet">
/// <item>Con la nube activa y verificada, EXP y TKT toman el número de <c>FolioCounters</c> en Turso: <see cref="Online"/>.</item>
/// <item>Sin conexión, el folio lleva el código del equipo (<c>EXP-2026-T1-001</c>) y su propio consecutivo: <see cref="Offline"/>.</item>
/// <item>Con la nube apagada (o sin verificar), el mayor número local + 1, como en v1.19: <see cref="Local"/>.</item>
/// <item>CNS sale de la hora; sin conexión lleva la letra: <see cref="Consultation"/>.</item>
/// </list>
/// Un folio con letra, con <c>-R</c> o tecleado de otra forma no cuenta para el número de su serie: <see cref="NumberIn"/>.
/// </summary>
public static class FolioFactory
{
    private static readonly Regex DeviceCodePattern = new("^[PTC][0-9]{1,4}$", RegexOptions.CultureInvariant);

    /// <summary>La serie de un folio: <c>EXP-2026</c> (año) o <c>TKT-20261009</c> (día), con la fecha del consultorio.</summary>
    public static string Series(FolioKind kind, DateTime localDate) => kind == FolioKind.Patient
        ? "EXP-" + localDate.ToString("yyyy", CultureInfo.InvariantCulture)
        : "TKT-" + localDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static int Digits(FolioKind kind) => kind == FolioKind.Patient ? 3 : 4;

    /// <summary>Serie + guion + el número con sus dígitos (más si no caben: EXP-2026-1000).</summary>
    public static string Format(FolioKind kind, string series, int number) =>
        $"{series}-{number.ToString("D" + Digits(kind), CultureInfo.InvariantCulture)}";

    /// <summary>La serie de un equipo sin conexión: <c>EXP-2026-T1</c>.</summary>
    public static string LetteredSeries(string series, string deviceCode) => $"{series}-{deviceCode}";

    /// <summary>P1, T1, C2… (el código de DeviceRegistry; X es de los rechazados y nunca numera).</summary>
    public static bool IsDeviceCode(string? code) => code != null && DeviceCodePattern.IsMatch(code);

    /// <summary>
    /// El número de <paramref name="folio"/> en <paramref name="series"/>: solo si es la serie, un guion y dígitos (sin
    /// importar mayúsculas). EXP-2026-T1-001, EXP-2026-005-R1 y EXP-2026-ABC no cuentan para EXP-2026.
    /// </summary>
    public static int? NumberIn(string series, string? folio)
    {
        if (folio == null) return null;
        var text = folio.Trim();
        if (text.Length <= series.Length + 1 || !text.StartsWith(series, StringComparison.OrdinalIgnoreCase) || text[series.Length] != '-') return null;
        var digits = text.AsSpan(series.Length + 1);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9') return null;
        }
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>El mayor número de la serie entre esos folios (0 si no hay).</summary>
    public static int MaxIn(string series, IEnumerable<string> folios)
    {
        int max = 0;
        foreach (var folio in folios)
        {
            if (NumberIn(series, folio) is int n && n > max) max = n;
        }
        return max;
    }

    /// <summary>Con la nube apagada o sin verificar: el mayor número local + 1, como en v1.19.</summary>
    public static string Local(FolioKind kind, DateTime localDate, IEnumerable<string> existing)
    {
        var series = Series(kind, localDate);
        return Format(kind, series, MaxIn(series, existing) + 1);
    }

    /// <summary>Con conexión: el número que regresó el contador de Turso.</summary>
    public static string Online(FolioKind kind, DateTime localDate, int taken) => Format(kind, Series(kind, localDate), taken);

    /// <summary>
    /// Sin conexión: el folio con el código del equipo y el número siguiente de esa serie, que es el mayor entre el último
    /// guardado fuera de la base (<paramref name="savedLast"/>; así restaurar un respaldo no repite uno ya impreso) y el
    /// máximo local. Regresa también la serie con letra y el número, para guardarlos.
    /// </summary>
    public static (string Folio, string LetteredSeries, int Number) Offline(FolioKind kind, DateTime localDate, string deviceCode,
        IEnumerable<string> existing, int savedLast)
    {
        if (!IsDeviceCode(deviceCode)) throw new ArgumentException($"'{deviceCode}' no es un código de equipo.", nameof(deviceCode));
        var lettered = LetteredSeries(Series(kind, localDate), deviceCode);
        int number = Math.Max(savedLast, MaxIn(lettered, existing)) + 1;
        return (Format(kind, lettered, number), lettered, number);
    }

    /// <summary>La nota de consulta: <c>CNS-20261009-103015</c>; sin conexión, <c>CNS-20261009-T1-103015</c>. Nunca se regulariza.</summary>
    public static string Consultation(DateTime localNow, string? offlineDeviceCode = null) =>
        IsDeviceCode(offlineDeviceCode)
            ? $"CNS-{localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{offlineDeviceCode}-{localNow.ToString("HHmmss", CultureInfo.InvariantCulture)}"
            : $"CNS-{localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
}
