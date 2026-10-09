using System;
using System.Text;
using System.Text.RegularExpressions;

namespace TPVC.Helpers;

/// <summary>
/// Reglas de la fecha dd/mm/aaaa con las diagonales solas (fase 7, 7.1), sin WPF: qué queda en el
/// cuadro y dónde queda el cursor al escribir, pegar o borrar. Las usa DateMask (la propiedad adjunta
/// del TextBox). El texto siempre queda como día/mes/año: hasta 2, 2 y 4 dígitos con sus diagonales.
/// </summary>
public static class DateMaskText
{
    /// <summary>Lo más largo que puede quedar: dd/mm/aaaa.</summary>
    public const int MaxLength = 10;

    /// <summary>Cómo queda el cuadro tras una tecla: el texto y dónde queda el cursor.</summary>
    public readonly record struct Edit(string Text, int Caret);

    private static readonly Regex IsoDate = new(@"^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Escribir (o pegar) <paramref name="input"/> sobre la selección. Solo entran dígitos y "/" ("-" y
    /// "." cuentan como "/"). null si no entra nada: letras, una "/" de más, un dígito dentro de un día o
    /// mes que ya está completo y cerrado, o que pase de dd/mm/aaaa.
    /// </summary>
    public static Edit? Insert(string? text, int selectionStart, int selectionLength, string? input)
    {
        text ??= string.Empty;
        (selectionStart, selectionLength) = Clamp(text, selectionStart, selectionLength);

        var typed = new StringBuilder();
        foreach (var c in input ?? string.Empty)
        {
            if (char.IsAsciiDigit(c)) typed.Append(c);
            else if (IsSeparator(c)) typed.Append('/');
        }
        if (typed.Length == 0) return null;

        var before = text[..selectionStart];
        var after = text[(selectionStart + selectionLength)..];
        if (typed.ToString() == "/")
        {
            // La diagonal ya está ahí (la puso la máscara): solo se pasa sobre ella
            if (selectionLength == 0 && after.Length > 0 && IsSeparator(after[0])) return new Edit(text, selectionStart + 1);
            // Sin día o mes antes no hay nada que cerrar (p. ej. la "/" que se teclea tras la automática)
            if (before.Length == 0 || IsSeparator(before[^1])) return null;
        }

        var raw = before + typed + after;
        var result = Normalize(raw, before.Length + typed.Length, typing: true);
        if (result is not { } edit) return null;

        // Día o mes completo justo antes de su diagonal: el cursor pasa a la parte siguiente
        if (edit.Caret < edit.Text.Length && edit.Text[edit.Caret] == '/' && DigitsBefore(edit.Text, edit.Caret) == 2)
        {
            edit = edit with { Caret = edit.Caret + 1 };
        }
        return edit;
    }

    /// <summary>
    /// Pegar: como escribir todo de una vez (08082008 → 08/08/2008; 08/08/2008 y 8/8/2008 quedan igual).
    /// Una fecha año-mes-día (2008-08-08) se acomoda a día/mes/año. null si no cabe.
    /// </summary>
    public static Edit? Paste(string? text, int selectionStart, int selectionLength, string? pasted)
    {
        var clean = pasted?.Trim() ?? string.Empty;
        var iso = IsoDate.Match(clean);
        if (iso.Success) clean = $"{iso.Groups[3].Value}/{iso.Groups[2].Value}/{iso.Groups[1].Value}";
        return Insert(text, selectionStart, selectionLength, clean);
    }

    /// <summary>
    /// Retroceso. Justo después de una diagonal: si es la última del texto se borra con el dígito
    /// anterior (08/ → 0); a media fecha se borra el dígito anterior y la diagonal se queda para que lo
    /// de después no se recorra (08/08/|2008 → 08/0|/2008). null si no hay nada que borrar.
    /// </summary>
    public static Edit? Backspace(string? text, int selectionStart, int selectionLength)
    {
        text ??= string.Empty;
        (selectionStart, selectionLength) = Clamp(text, selectionStart, selectionLength);
        if (selectionLength > 0) return Remove(text, selectionStart, selectionLength);
        if (selectionStart == 0) return null;

        int caret = selectionStart;
        if (text[caret - 1] == '/' && caret >= 2 && char.IsAsciiDigit(text[caret - 2]))
        {
            return caret == text.Length
                ? Remove(text, caret - 2, 2)   // la diagonal del final y el dígito anterior
                : Remove(text, caret - 2, 1);  // el dígito anterior; la diagonal se queda
        }
        return Remove(text, caret - 1, 1);
    }

    /// <summary>
    /// Suprimir. Justo antes de una diagonal se borra el dígito que la sigue (la diagonal se queda) y el
    /// cursor queda tras ella; si no la sigue un dígito se borra la diagonal. null si no hay nada que borrar.
    /// </summary>
    public static Edit? Delete(string? text, int selectionStart, int selectionLength)
    {
        text ??= string.Empty;
        (selectionStart, selectionLength) = Clamp(text, selectionStart, selectionLength);
        if (selectionLength > 0) return Remove(text, selectionStart, selectionLength);
        if (selectionStart >= text.Length) return null;

        int caret = selectionStart;
        if (text[caret] == '/' && caret + 1 < text.Length && char.IsAsciiDigit(text[caret + 1]))
        {
            return Remove(text, caret + 1, 1);
        }
        return Remove(text, caret, 1);
    }

    private static Edit Remove(string text, int start, int length) =>
        Normalize(text.Remove(start, length), start, typing: false)!.Value;

    /// <summary>
    /// Acomoda el texto en día/mes/año (máximo 2, 2 y 4 dígitos): un dígito que ya no cabe en el día o
    /// el mes empieza la parte siguiente con su diagonal. Al escribir (<paramref name="typing"/>) no se
    /// acepta que algo se pierda ni que un dígito empuje una parte que ya tiene su diagonal (null), y
    /// al completar el día o el mes al final se agrega su diagonal. Al borrar se acomoda lo que quede.
    /// </summary>
    private static Edit? Normalize(string raw, int caret, bool typing)
    {
        int lastSeparator = -1;
        for (int i = raw.Length - 1; i >= 0; i--)
        {
            if (IsSeparator(raw[i])) { lastSeparator = i; break; }
        }

        var sb = new StringBuilder(MaxLength);
        int part = 0, partDigits = 0, mappedCaret = 0;
        for (int i = 0; i <= raw.Length; i++)
        {
            if (i == caret) mappedCaret = sb.Length;
            if (i == raw.Length) break;

            char c = raw[i];
            if (IsSeparator(c))
            {
                if (part == 2)
                {
                    if (typing) return null; // una tercera diagonal
                    continue;
                }
                sb.Append('/');
                part++;
                partDigits = 0;
            }
            else if (char.IsAsciiDigit(c))
            {
                if (partDigits == (part < 2 ? 2 : 4))
                {
                    if (part == 2 || (typing && i < lastSeparator))
                    {
                        if (typing) return null; // el año ya tiene 4 dígitos, o la parte llena ya está cerrada
                        continue;
                    }
                    sb.Append('/');
                    part++;
                    partDigits = 0;
                }
                sb.Append(c);
                partDigits++;
            }
            // Lo demás (letras, espacios de un texto viejo) no se queda
        }

        // Día o mes recién completado al final: su diagonal aparece sola
        if (typing && part < 2 && partDigits == 2 && mappedCaret == sb.Length) sb.Append('/');

        return new Edit(sb.ToString(), mappedCaret);
    }

    private static bool IsSeparator(char c) => c is '/' or '-' or '.';

    private static int DigitsBefore(string text, int index)
    {
        int count = 0;
        for (int i = index - 1; i >= 0 && char.IsAsciiDigit(text[i]); i--) count++;
        return count;
    }

    private static (int Start, int Length) Clamp(string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        return (start, length);
    }
}
