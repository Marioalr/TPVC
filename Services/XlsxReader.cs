using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TPVC.Services;

/// <summary>
/// Hoja de cálculo leída de un .xlsx: celdas por número real de fila (1..n) y columna (A=1),
/// incluyendo fórmulas. Las filas vacías que Excel omite en el XML no desplazan a las demás.
/// </summary>
public sealed class XlsxSheet
{
    private readonly Dictionary<(int Row, int Col), string> _values = new();
    private readonly Dictionary<(int Row, int Col), string> _formulas = new();

    public string Name { get; init; } = string.Empty;
    public int MaxRow { get; private set; }
    public int MaxColumn { get; private set; }

    internal void Set(int row, int col, string value, string? formula)
    {
        if (!string.IsNullOrEmpty(value)) _values[(row, col)] = value;
        if (!string.IsNullOrEmpty(formula)) _formulas[(row, col)] = formula;
        MaxRow = Math.Max(MaxRow, row);
        MaxColumn = Math.Max(MaxColumn, col);
    }

    public string Get(int row, int col) => _values.TryGetValue((row, col), out var v) ? v : string.Empty;

    public string Get(int row, string column) => Get(row, XlsxReader.ColumnIndex(column));

    public string? GetFormula(int row, int col) => _formulas.TryGetValue((row, col), out var f) ? f : null;

    /// <summary>Valores de la fila desde la columna A hasta la última usada en la hoja.</summary>
    public List<string> GetRow(int row) => Enumerable.Range(1, MaxColumn).Select(c => Get(row, c)).ToList();

    /// <summary>Imágenes colocadas sobre la hoja (dibujo de la hoja), en el orden del archivo.</summary>
    public List<XlsxImage> Images { get; } = new();
}

/// <summary>
/// Imagen colocada sobre una hoja: la celda donde empieza y donde termina (fila 1..n, columna A=1),
/// el archivo de xl/media y el recorte que Excel le aplica al mostrarla.
/// </summary>
public sealed class XlsxImage
{
    public int FromRow { get; init; }
    public int FromColumn { get; init; }
    public int ToRow { get; init; }
    public int ToColumn { get; init; }

    /// <summary>Parte del libro con la imagen (ej. xl/media/image1.png).</summary>
    public string MediaPath { get; init; } = string.Empty;

    public byte[] Content { get; init; } = Array.Empty<byte>();

    /// <summary>Recorte de Excel (a:srcRect) en fracción de la imagen: 0.50441 = se ocultó el 50.441 %.</summary>
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropRight { get; init; }
    public double CropBottom { get; init; }

    public bool IsCropped => CropLeft > 0 || CropTop > 0 || CropRight > 0 || CropBottom > 0;

    /// <summary>
    /// La imagen como se ve en Excel: con el recorte aplicado si es un PNG que se puede recortar;
    /// si no, el archivo original.
    /// </summary>
    public byte[] GetDisplayedContent() =>
        IsCropped ? PngImage.Crop(Content, CropLeft, CropTop, CropRight, CropBottom) ?? Content : Content;
}

/// <summary>
/// Lector OpenXML mínimo (sin dependencias externas) para importar catálogos y formatos del consultorio.
/// Es .NET puro: reutilizable por la futura app Android.
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelAttr = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<XlsxSheet> Read(string filePath)
    {
        using var zip = ZipFile.OpenRead(filePath);
        var sharedStrings = ReadSharedStrings(zip);
        var relationships = ReadWorkbookRelationships(zip);

        var workbookEntry = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("El archivo no es un libro de Excel válido (falta xl/workbook.xml).");
        XDocument workbook;
        using (var stream = workbookEntry.Open()) workbook = XDocument.Load(stream);

        var sheets = new List<XlsxSheet>();
        int position = 0;
        foreach (var sheetElement in workbook.Descendants(Main + "sheet"))
        {
            position++;
            var name = sheetElement.Attribute("name")?.Value ?? $"Hoja{position}";
            var relId = sheetElement.Attribute(RelAttr + "id")?.Value;
            var path = relId != null && relationships.TryGetValue(relId, out var target) ? target : $"xl/worksheets/sheet{position}.xml";

            var entry = zip.GetEntry(path);
            if (entry == null) continue;

            var sheet = new XlsxSheet { Name = name.Trim() };
            XDocument doc;
            using (var stream = entry.Open()) doc = XDocument.Load(stream);

            int nextRow = 0;
            foreach (var rowElement in doc.Descendants(Main + "row"))
            {
                int rowNumber = int.TryParse(rowElement.Attribute("r")?.Value, out var r) ? r : nextRow + 1;
                nextRow = rowNumber;

                int nextCol = 0;
                foreach (var cell in rowElement.Elements(Main + "c"))
                {
                    var reference = cell.Attribute("r")?.Value;
                    int col = !string.IsNullOrEmpty(reference) ? ColumnIndex(Regex.Match(reference, "^[A-Za-z]+").Value) : nextCol + 1;
                    nextCol = col;

                    var formula = cell.Element(Main + "f")?.Value;
                    sheet.Set(rowNumber, col, ReadCellValue(cell, sharedStrings), formula);
                }
            }

            ReadSheetImages(zip, path, doc, sheet.Images);
            sheets.Add(sheet);
        }

        return sheets;
    }

    private static readonly XNamespace SheetDrawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace DrawingMain = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>
    /// Imágenes del dibujo de la hoja (&lt;drawing r:id&gt; → xl/drawings/drawingN.xml → xl/media/...):
    /// su celda de anclaje, sus bytes y el recorte. Un dibujo que no se puede leer no impide leer las celdas.
    /// </summary>
    private static void ReadSheetImages(ZipArchive zip, string sheetPath, XDocument sheetDoc, List<XlsxImage> images)
    {
        try
        {
            var drawingId = sheetDoc.Root?.Element(Main + "drawing")?.Attribute(RelAttr + "id")?.Value;
            if (drawingId == null) return;
            if (!ReadPartRelationships(zip, sheetPath).TryGetValue(drawingId, out var drawingPath)) return;

            var drawingEntry = zip.GetEntry(drawingPath);
            if (drawingEntry == null) return;
            XDocument drawing;
            using (var stream = drawingEntry.Open()) drawing = XDocument.Load(stream);
            var media = ReadPartRelationships(zip, drawingPath);

            foreach (var anchor in drawing.Root?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (anchor.Name != SheetDrawing + "twoCellAnchor" && anchor.Name != SheetDrawing + "oneCellAnchor") continue;
                var pic = anchor.Element(SheetDrawing + "pic");
                var blipFill = pic?.Element(SheetDrawing + "blipFill");
                var embed = blipFill?.Element(DrawingMain + "blip")?.Attribute(RelAttr + "embed")?.Value;
                if (embed == null || !media.TryGetValue(embed, out var mediaPath)) continue;

                var mediaEntry = zip.GetEntry(mediaPath);
                if (mediaEntry == null) continue;
                byte[] content;
                using (var stream = mediaEntry.Open())
                using (var buffer = new System.IO.MemoryStream())
                {
                    stream.CopyTo(buffer);
                    content = buffer.ToArray();
                }

                var from = anchor.Element(SheetDrawing + "from");
                var to = anchor.Element(SheetDrawing + "to") ?? from;
                var crop = blipFill!.Element(DrawingMain + "srcRect");
                images.Add(new XlsxImage
                {
                    // En el dibujo las filas y columnas empiezan en 0
                    FromRow = AnchorIndex(from, "row") + 1,
                    FromColumn = AnchorIndex(from, "col") + 1,
                    ToRow = AnchorIndex(to, "row") + 1,
                    ToColumn = AnchorIndex(to, "col") + 1,
                    MediaPath = mediaPath,
                    Content = content,
                    CropLeft = CropFraction(crop, "l"),
                    CropTop = CropFraction(crop, "t"),
                    CropRight = CropFraction(crop, "r"),
                    CropBottom = CropFraction(crop, "b")
                });
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or System.IO.InvalidDataException or System.IO.IOException)
        {
            AppLogger.Warn($"No se pudieron leer las imágenes de la hoja ({sheetPath}): {ex.Message}");
        }
    }

    private static int AnchorIndex(XElement? marker, string name) =>
        int.TryParse(marker?.Element(SheetDrawing + name)?.Value, out var value) ? value : 0;

    /// <summary>Recorte en milésimas de por ciento ("50441" = 50.441 %) como fracción entre 0 y 1.</summary>
    private static double CropFraction(XElement? srcRect, string side) =>
        int.TryParse(srcRect?.Attribute(side)?.Value, out var value) ? Math.Clamp(value / 100000.0, 0, 1) : 0;

    /// <summary>Relaciones de una parte del libro (su archivo _rels/X.rels), con destinos como rutas del zip.</summary>
    private static Dictionary<string, string> ReadPartRelationships(ZipArchive zip, string partPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var slash = partPath.LastIndexOf('/');
        var folder = slash >= 0 ? partPath[..slash] : string.Empty;
        var relsPath = (folder.Length > 0 ? folder + "/" : string.Empty) + "_rels/" + partPath[(slash + 1)..] + ".rels";

        var entry = zip.GetEntry(relsPath);
        if (entry == null) return map;
        XDocument doc;
        using (var stream = entry.Open()) doc = XDocument.Load(stream);
        foreach (var rel in doc.Descendants(PackageRel + "Relationship"))
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target) || rel.Attribute("TargetMode")?.Value == "External") continue;
            map[id] = ResolvePartPath(folder, target);
        }
        return map;
    }

    /// <summary>"xl/worksheets" + "../drawings/drawing1.xml" → "xl/drawings/drawing1.xml".</summary>
    private static string ResolvePartPath(string folder, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (segment != ".") parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    /// <summary>"A" → 1, "Z" → 26, "AA" → 27.</summary>
    public static int ColumnIndex(string letters)
    {
        int index = 0;
        foreach (var c in letters.ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z') index = index * 26 + (c - 'A' + 1);
        }
        return index;
    }

    private static string ReadCellValue(XElement cell, List<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        var raw = cell.Element(Main + "v")?.Value ?? string.Empty;

        return type switch
        {
            "s" => int.TryParse(raw, out var idx) && idx >= 0 && idx < sharedStrings.Count ? sharedStrings[idx] : string.Empty,
            "inlineStr" => string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)),
            "b" => raw == "1" ? "VERDADERO" : "FALSO",
            _ => raw
        };
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return list;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Root?.Elements(Main + "si") ?? Enumerable.Empty<XElement>())
        {
            // Texto simple <t> o enriquecido <r><t>; se omiten las guías fonéticas <rPh>
            var parts = si.Elements(Main + "t").Select(t => t.Value)
                .Concat(si.Elements(Main + "r").SelectMany(r => r.Elements(Main + "t")).Select(t => t.Value));
            list.Add(string.Concat(parts));
        }
        return list;
    }

    private static Dictionary<string, string> ReadWorkbookRelationships(ZipArchive zip)
    {
        var map = new Dictionary<string, string>();
        var entry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry == null) return map;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var rel in doc.Descendants(PackageRel + "Relationship"))
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) continue;
            map[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }
        return map;
    }
}
