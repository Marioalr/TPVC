using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace TPVC.Services;

/// <summary>
/// PNG en .NET puro (sin WPF ni System.Drawing; reutilizable por la app Android): leer sus pixeles y
/// recortarlo. Sirve para guardar las imágenes del Excel como se ven en la hoja: Excel puede mostrar solo
/// una parte de la imagen (a:srcRect) y el archivo guarda la imagen completa.
/// </summary>
public static class PngImage
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Pixeles sin filtros: <see cref="BytesPerPixel"/> bytes por pixel, fila tras fila.</summary>
    public sealed class Bitmap
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int BitDepth { get; init; }
        /// <summary>0 gris, 2 RGB, 3 paleta, 4 gris + alfa, 6 RGBA.</summary>
        public int ColorType { get; init; }
        public int BytesPerPixel { get; init; }
        public byte[] Pixels { get; init; } = Array.Empty<byte>();

        /// <summary>Bloques que no dependen del tamaño (paleta, transparencia, gamma, resolución...).</summary>
        internal List<(string Type, byte[] Data)> Ancillary { get; init; } = new();
        internal byte[] Header { get; init; } = Array.Empty<byte>();

        /// <summary>Bytes del pixel (x, y).</summary>
        public ReadOnlySpan<byte> Pixel(int x, int y) => Pixels.AsSpan((y * Width + x) * BytesPerPixel, BytesPerPixel);
    }

    /// <summary>Ancho y alto de un PNG, o null si no lo es.</summary>
    public static (int Width, int Height)? Size(byte[] png)
    {
        if (png == null || png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    /// <summary>
    /// Pixeles del PNG, o null si no es un PNG que se pueda leer aquí (entrelazado, menos de 8 bits por
    /// canal o dañado).
    /// </summary>
    public static Bitmap? Decode(byte[] png)
    {
        try
        {
            return DecodeCore(png);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Recorta el PNG quitando de cada lado la fracción indicada (0.5 = la mitad). Devuelve null si no
    /// se puede recortar (ver <see cref="Decode"/>); entonces se usa la imagen completa.
    /// </summary>
    public static byte[]? Crop(byte[] png, double left, double top, double right, double bottom)
    {
        if (Size(png) is not { } size) return null;
        int x0 = (int)Math.Round(size.Width * Math.Clamp(left, 0, 1));
        int x1 = size.Width - (int)Math.Round(size.Width * Math.Clamp(right, 0, 1));
        int y0 = (int)Math.Round(size.Height * Math.Clamp(top, 0, 1));
        int y1 = size.Height - (int)Math.Round(size.Height * Math.Clamp(bottom, 0, 1));
        if (x1 <= x0 || y1 <= y0) return null;
        if (x0 == 0 && y0 == 0 && x1 == size.Width && y1 == size.Height) return png;

        if (Decode(png) is not { } bitmap) return null;
        int bpp = bitmap.BytesPerPixel, stride = bitmap.Width * bpp;
        int newWidth = x1 - x0, newHeight = y1 - y0, newStride = newWidth * bpp;

        // Filas recortadas sin filtro (tipo 0)
        var filtered = new byte[newHeight * (newStride + 1)];
        for (int y = 0; y < newHeight; y++)
        {
            Buffer.BlockCopy(bitmap.Pixels, (y0 + y) * stride + x0 * bpp, filtered, y * (newStride + 1) + 1, newStride);
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = (byte[])bitmap.Header.Clone();
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), newWidth);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), newHeight);
        WriteChunk(output, "IHDR", header);
        foreach (var (type, data) in bitmap.Ancillary) WriteChunk(output, type, data);

        using (var compressed = new MemoryStream())
        {
            using (var deflater = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflater.Write(filtered);
            }
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static Bitmap? DecodeCore(byte[] png)
    {
        if (Size(png) is not { } size) return null;

        byte[] header = Array.Empty<byte>();
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        var idat = new MemoryStream();
        var ancillary = new List<(string Type, byte[] Data)>();
        bool seenIdat = false;

        for (int pos = 8; pos + 12 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos, 4));
            if (length < 0 || pos + 12 + length > png.Length) return null;
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    if (length != 13) return null;
                    header = data.ToArray();
                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    bitDepth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                    break;
                case "IDAT":
                    idat.Write(data);
                    seenIdat = true;
                    break;
                case "IEND":
                    break;
                default:
                    // PLTE, tRNS, gAMA, pHYs, sRGB...: no dependen del tamaño y van antes de los datos
                    if (!seenIdat) ancillary.Add((type, data.ToArray()));
                    break;
            }
            pos += 12 + length;
        }

        if (width != size.Width || height != size.Height || width <= 0 || height <= 0) return null;
        if (interlace != 0 || bitDepth < 8) return null;
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0) return null;
        int bpp = channels * bitDepth / 8;
        int stride = width * bpp;

        byte[] raw;
        idat.Position = 0;
        using (var inflater = new ZLibStream(idat, CompressionMode.Decompress))
        using (var buffer = new MemoryStream())
        {
            inflater.CopyTo(buffer);
            raw = buffer.ToArray();
        }
        if (raw.Length < (long)height * (stride + 1)) return null;

        // Cada fila lleva un byte de filtro que depende del pixel de la izquierda y de la fila anterior
        var pixels = new byte[height * stride];
        for (int y = 0; y < height; y++)
        {
            int src = y * (stride + 1);
            byte filter = raw[src];
            int row = y * stride;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? pixels[row + i - bpp] : 0;
                int b = y > 0 ? pixels[row - stride + i] : 0;
                int c = i >= bpp && y > 0 ? pixels[row - stride + i - bpp] : 0;
                int value = raw[src + 1 + i];
                value += filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("Filtro de PNG desconocido")
                };
                pixels[row + i] = (byte)value;
            }
        }

        return new Bitmap
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            ColorType = colorType,
            BytesPerPixel = bpp,
            Pixels = pixels,
            Ancillary = ancillary,
            Header = header
        };
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFFu);
        crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
