using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TPVC.Models;

namespace TPVC.Services.LabFiles;

/// <summary>Un archivo que eligió el médico: su nombre y su contenido.</summary>
public sealed record LabFileInput(string FileName, byte[] Content);

/// <summary>El archivo listo para guardar: un PDF (el original o el de varias fotos) o un JPEG.</summary>
public sealed record PreparedLabFile(byte[] Content, string MimeType, int PageCount, string FileName, string? Warning = null);

/// <summary>Un archivo que no se puede agregar, con el motivo para el médico.</summary>
public sealed class LabFileException(string message) : Exception(message);

/// <summary>
/// Prepara lo que el médico elige para un resultado (paso 8.12b; EVALUACION_RESULTADOS_LABORATORIO.md §2):
/// <list type="bullet">
/// <item>Un PDF va tal cual (un PDF digital pesa 0.1–0.5 MB); arriba de 3 MB se avisa y arriba de 10 MB no se acepta.</item>
/// <item>Una foto se vuelve a guardar como JPEG de calidad 80 con 2000 px en el lado largo (~180 dpi en hoja carta, 0.2–0.6
/// MB), derecha según la orientación que anotó la cámara y sin sus metadatos (también la ubicación del GPS).</item>
/// <item>Varias fotos se juntan en un PDF con una página por foto.</item>
/// </list>
/// JPEG y no WebP: en Windows, WebP depende de que esté instalado su códec.
/// </summary>
public static class LabFileImporter
{
    public const int MaxLongSidePixels = 2000;
    public const int JpegQuality = 80;
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const long LargePdfBytes = 3 * 1024 * 1024;
    public const int MaxPhotos = 20;

    public static readonly string[] PhotoExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif", ".heic", ".webp" };

    /// <summary>El filtro del diálogo para elegir archivos.</summary>
    public const string PickerFilter = "Resultados (PDF o fotos)|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif|PDF|*.pdf|Fotos|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif";

    /// <summary>
    /// <see cref="Prepare"/> en un hilo STA aparte: las fotos de 12 MP tardan en decodificarse y la ventana no se congela.
    /// </summary>
    public static Task<PreparedLabFile> PrepareAsync(IReadOnlyList<LabFileInput> files)
    {
        var done = new TaskCompletionSource<PreparedLabFile>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Prepare(files));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "TPVC laboratorio"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    public static PreparedLabFile Prepare(IReadOnlyList<LabFileInput> files)
    {
        if (files.Count == 0) throw new LabFileException("Elija un PDF o una o varias fotos del resultado.");

        var pdfs = files.Where(f => JpegPdfWriter.IsPdf(f.Content)).ToList();
        if (pdfs.Count > 0)
        {
            if (files.Count > 1) throw new LabFileException("Elija un solo PDF, o una o varias fotos (sin mezclarlas con un PDF).");
            var pdf = pdfs[0];
            if (pdf.Content.LongLength > MaxFileBytes)
                throw new LabFileException($"El PDF pesa {Megabytes(pdf.Content.LongLength)}; el máximo es {Megabytes(MaxFileBytes)}. " +
                                           "Pida al laboratorio el PDF digital o escanéelo con menos resolución.");
            var warning = pdf.Content.LongLength > LargePdfBytes
                ? $"El PDF pesa {Megabytes(pdf.Content.LongLength)}. Si es un escaneo, uno más ligero ocupa menos en la nube."
                : null;
            return new PreparedLabFile(pdf.Content, LabResult.PdfMimeType, JpegPdfWriter.CountPages(pdf.Content), pdf.FileName, warning);
        }

        if (files.Count > MaxPhotos) throw new LabFileException($"Elija hasta {MaxPhotos} fotos para un mismo resultado.");
        var pages = files.Select(f => Reencode(f)).ToList();
        var content = pages.Count == 1 ? pages[0] : JpegPdfWriter.Build(pages);
        if (content.LongLength > MaxFileBytes)
            throw new LabFileException($"Las fotos juntas pesan {Megabytes(content.LongLength)}; el máximo es {Megabytes(MaxFileBytes)}. Divídalas en dos resultados.");
        return pages.Count == 1
            ? new PreparedLabFile(content, LabResult.JpegMimeType, 1, files[0].FileName)
            : new PreparedLabFile(content, LabResult.PdfMimeType, pages.Count, files[0].FileName);
    }

    public static string Megabytes(long bytes) => $"{bytes / 1_000_000.0:0.0} MB";

    /// <summary>El tamaño para la lista: en KB debajo de 1 MB ("350 KB"), si no en MB.</summary>
    public static string SizeText(long bytes) => bytes < 1_000_000 ? $"{Math.Max(1, bytes / 1000)} KB" : Megabytes(bytes);

    /// <summary>La foto como JPEG de calidad 80, con 2000 px como máximo en el lado largo, derecha y sin metadatos.</summary>
    internal static byte[] Reencode(LabFileInput file)
    {
        BitmapSource source;
        int orientation;
        try
        {
            using var stream = new MemoryStream(file.Content, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            orientation = ReadOrientation(frame);
            source = frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
                                       or COMException or IOException or OverflowException)
        {
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            throw new LabFileException(extension is ".heic" or ".webp"
                ? $"Windows no puede abrir «{file.FileName}» ({extension}). Pase la foto a JPG (en el celular: Ajustes de cámara › Formato › Más compatible)."
                : $"«{file.FileName}» no es un PDF ni una foto que se pueda abrir.");
        }

        source = Orient(source, orientation);
        double scale = Math.Min(1.0, (double)MaxLongSidePixels / Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale < 1.0) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        source = OnWhite(source);

        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>La orientación EXIF (1–8) que anotó la cámara; 1 si no hay.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value && value is >= 1 and <= 8)
                return value;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException)
        {
            // Formatos sin EXIF (PNG, BMP…)
        }
        return 1;
    }

    /// <summary>Gira o voltea según la orientación EXIF, para que la foto quede como se ve en el celular.</summary>
    private static BitmapSource Orient(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null
        };
        return transform == null ? source : new TransformedBitmap(source, transform);
    }

    /// <summary>En 24 bits sobre fondo blanco: la transparencia de un PNG no se vuelve negra en el JPEG.</summary>
    private static BitmapSource OnWhite(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        var rgb = new byte[width * height * 3];
        for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
        {
            int alpha = pixels[i + 3];
            rgb[j] = (byte)((pixels[i] * alpha + 255 * (255 - alpha)) / 255);
            rgb[j + 1] = (byte)((pixels[i + 1] * alpha + 255 * (255 - alpha)) / 255);
            rgb[j + 2] = (byte)((pixels[i + 2] * alpha + 255 * (255 - alpha)) / 255);
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, rgb, width * 3);
        result.Freeze();
        return result;
    }
}
