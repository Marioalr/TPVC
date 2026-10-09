using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Lectura mínima de código C# para las pruebas de arquitectura de la fase 6 (sin Roslyn): quita
/// comentarios y el contenido de cadenas y caracteres (conserva saltos de línea y posiciones) y
/// ubica las clases para saber a cuál pertenece cada línea.
/// </summary>
internal static class CSharpSource
{
    /// <summary>Mismo largo que el original: comentarios y contenido de literales como espacios.</summary>
    public static string Sanitize(string code)
    {
        var o = code.ToCharArray();
        // Agujeros de cadenas interpoladas abiertos: (verbatim, profundidad de llaves dentro del agujero)
        var holes = new Stack<(bool Verbatim, int Depth)>();
        int i = 0;

        void Blank(int from, int to)
        {
            for (int k = from; k < to && k < o.Length; k++)
            {
                if (o[k] != '\n' && o[k] != '\r') o[k] = ' ';
            }
        }

        // Recorre el contenido de una cadena desde i (después de la comilla de apertura o de un agujero).
        // Devuelve true si terminó en un agujero { de una interpolada (i queda después de la llave).
        bool ScanString(bool interpolated, bool verbatim)
        {
            int start = i;
            while (i < code.Length)
            {
                var c = code[i];
                if (!verbatim && c == '\\') { i += 2; continue; }
                if (c == '"')
                {
                    if (verbatim && i + 1 < code.Length && code[i + 1] == '"') { i += 2; continue; }
                    Blank(start, i);
                    i++;
                    return false;
                }
                if (interpolated && c == '{')
                {
                    if (i + 1 < code.Length && code[i + 1] == '{') { i += 2; continue; }
                    Blank(start, i);
                    i++;
                    return true;
                }
                if (interpolated && c == '}' && i + 1 < code.Length && code[i + 1] == '}') { i += 2; continue; }
                if (!verbatim && c == '\n') { Blank(start, i); return false; } // cadena sin cerrar
                i++;
            }
            Blank(start, i);
            return false;
        }

        void OpenString(bool interpolated, bool verbatim)
        {
            if (ScanString(interpolated, verbatim)) holes.Push((verbatim, 0));
        }

        while (i < code.Length)
        {
            var c = code[i];
            var next = i + 1 < code.Length ? code[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                int end = code.IndexOf('\n', i);
                if (end < 0) end = code.Length;
                Blank(i, end);
                i = end;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 2;
                Blank(i, end);
                i = end;
                continue;
            }
            if (c == '\'')
            {
                // Carácter: 'a', '\n', '\''
                int j = i + 1;
                if (j < code.Length && code[j] == '\\') j += 2; else j += 1;
                while (j < code.Length && code[j] != '\'' && code[j] != '\n') j++;
                Blank(i + 1, j);
                i = j + 1;
                continue;
            }
            if (c == '"') { i++; OpenString(false, false); continue; }
            if ((c == '$' || c == '@') && (next == '"' || ((next == '$' || next == '@') && i + 2 < code.Length && code[i + 2] == '"')))
            {
                bool interpolated = c == '$' || next == '$';
                bool verbatim = c == '@' || next == '@';
                i += next == '"' ? 2 : 3;
                OpenString(interpolated, verbatim);
                continue;
            }
            if (holes.Count > 0)
            {
                if (c == '{')
                {
                    var top = holes.Pop();
                    holes.Push((top.Verbatim, top.Depth + 1));
                }
                else if (c == '}')
                {
                    var top = holes.Pop();
                    if (top.Depth == 0)
                    {
                        i++;
                        OpenString(true, top.Verbatim);
                        continue;
                    }
                    holes.Push((top.Verbatim, top.Depth - 1));
                }
            }
            i++;
        }
        return new string(o);
    }

    public sealed record TypeSpan(string Name, int Start, int End, int Depth);

    /// <summary>Clases, records, structs e interfaces con el tramo de su cuerpo (texto ya saneado).</summary>
    public static List<TypeSpan> TypeSpans(string sanitized)
    {
        var spans = new List<(string Name, int Start, int End)>();
        foreach (Match m in Regex.Matches(sanitized, @"\b(?:class|record|struct|interface)\s+(\w+)"))
        {
            int brace = -1;
            for (int k = m.Index + m.Length; k < sanitized.Length; k++)
            {
                if (sanitized[k] == '{') { brace = k; break; }
                if (sanitized[k] == ';') break;
            }
            if (brace < 0) continue;
            int depth = 0, end = sanitized.Length;
            for (int k = brace; k < sanitized.Length; k++)
            {
                if (sanitized[k] == '{') depth++;
                else if (sanitized[k] == '}' && --depth == 0) { end = k; break; }
            }
            spans.Add((m.Groups[1].Value, brace, end));
        }
        return spans
            .Select(s => new TypeSpan(s.Name, s.Start, s.End, spans.Count(o => o.Start < s.Start && o.End > s.End)))
            .ToList();
    }

    /// <summary>Nombre del tipo (con + para anidados) que contiene la posición, o null.</summary>
    public static string? TypeAt(List<TypeSpan> spans, int position)
    {
        var containing = spans.Where(s => s.Start < position && s.End > position).OrderBy(s => s.Depth).Select(s => s.Name).ToList();
        return containing.Count == 0 ? null : string.Join("+", containing);
    }

    public static string? Namespace(string sanitized) =>
        Regex.Match(sanitized, @"\bnamespace\s+([\w.]+)") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Línea (desde 1) de una posición.</summary>
    public static int LineOf(string text, int position) => text.AsSpan(0, Math.Min(position, text.Length)).Count('\n') + 1;
}
