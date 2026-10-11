using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TPVC.Services.LabFiles;

/// <summary>
/// Un PDF con una foto por página (paso 8.12b), en .NET puro: cada JPEG va tal cual (/DCTDecode), sin volver a
/// comprimirlo, centrado en una hoja carta con margen de 1/4 de pulgada, vertical u horizontal según la foto. Así varias
/// fotos de un mismo resultado quedan en un solo archivo que cualquier equipo abre (Android con PdfRenderer).
/// </summary>
public static class JpegPdfWriter
{
    /// <summary>Hoja carta en puntos (1/72 de pulgada).</summary>
    public const double LetterWidth = 612, LetterHeight = 792;
    public const double Margin = 18;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>Ancho, alto y canales de un JPEG (de su marcador SOF), o null si no lo es.</summary>
    public static (int Width, int Height, int Components)? ReadJpegInfo(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        int i = 2;
        while (i + 3 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return null;
            byte marker = jpeg[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }
            int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            bool isFrame = marker >= 0xC0 && marker <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isFrame)
            {
                if (i + 9 >= jpeg.Length) return null;
                int height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                int width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                int components = jpeg[i + 9];
                return width > 0 && height > 0 ? (width, height, components) : null;
            }
            if (marker == 0xDA) return null; // empezó la imagen sin un SOF
            i += 2 + length;
        }
        return null;
    }

    /// <summary>El PDF con una página por JPEG, en el orden dado.</summary>
    public static byte[] Build(IReadOnlyList<byte[]> jpegs)
    {
        if (jpegs.Count == 0) throw new ArgumentException("Hace falta al menos una foto.", nameof(jpegs));

        using var output = new MemoryStream();
        var offsets = new List<long> { 0 }; // el objeto 0 es la cabeza de la lista libre
        void Text(string s) => output.Write(Latin1.GetBytes(s));
        void BeginObject(int number)
        {
            while (offsets.Count <= number) offsets.Add(0);
            offsets[number] = output.Position;
            Text($"{number} 0 obj\n");
        }

        Text("%PDF-1.4\n%âãÏÓ\n");

        int pageCount = jpegs.Count;
        var kids = new StringBuilder();
        for (int p = 0; p < pageCount; p++) kids.Append(CultureInfo.InvariantCulture, $"{3 + p * 3} 0 R ");

        BeginObject(1);
        Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2);
        Text($"<< /Type /Pages /Kids [{kids.ToString().TrimEnd()}] /Count {pageCount} >>\nendobj\n");

        for (int p = 0; p < pageCount; p++)
        {
            var jpeg = jpegs[p];
            var info = ReadJpegInfo(jpeg) ?? throw new ArgumentException($"La foto {p + 1} no es un JPEG.", nameof(jpegs));
            int pageObject = 3 + p * 3, imageObject = pageObject + 1, contentObject = pageObject + 2;

            bool landscape = info.Width > info.Height;
            double pageWidth = landscape ? LetterHeight : LetterWidth;
            double pageHeight = landscape ? LetterWidth : LetterHeight;
            double scale = Math.Min((pageWidth - 2 * Margin) / info.Width, (pageHeight - 2 * Margin) / info.Height);
            double drawWidth = info.Width * scale, drawHeight = info.Height * scale;
            double x = (pageWidth - drawWidth) / 2, y = (pageHeight - drawHeight) / 2;

            BeginObject(pageObject);
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(pageWidth)} {N(pageHeight)}] " +
                 $"/Resources << /XObject << /Im0 {imageObject} 0 R >> >> /Contents {contentObject} 0 R >>\nendobj\n");

            var colorSpace = info.Components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" };
            BeginObject(imageObject);
            Text($"<< /Type /XObject /Subtype /Image /Width {info.Width} /Height {info.Height} /ColorSpace {colorSpace} " +
                 $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            output.Write(jpeg);
            Text("\nendstream\nendobj\n");

            var content = $"q {N(drawWidth)} 0 0 {N(drawHeight)} {N(x)} {N(y)} cm /Im0 Do Q";
            BeginObject(contentObject);
            Text($"<< /Length {Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        long xref = output.Position;
        Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (int n = 1; n < offsets.Count; n++) Text($"{offsets[n]:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static readonly Regex PageObject = new(@"/Type\s*/Page(?![A-Za-z])", RegexOptions.Compiled);

    /// <summary>
    /// Páginas de un PDF contando sus objetos /Type /Page; 0 si no se encuentran (un PDF con sus objetos comprimidos en
    /// flujos no los muestra).
    /// </summary>
    public static int CountPages(byte[] pdf) => PageObject.Matches(Latin1.GetString(pdf)).Count;

    /// <summary>Un PDF empieza con "%PDF-" en su primer kilobyte (algunos traen bytes antes).</summary>
    public static bool IsPdf(byte[] content) =>
        content.AsSpan(0, Math.Min(content.Length, 1024)).IndexOf("%PDF-"u8) >= 0;
}
