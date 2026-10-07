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

    public bool IsRowEmpty(int row) => Enumerable.Range(1, MaxColumn).All(c => string.IsNullOrWhiteSpace(Get(row, c)));
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

            sheets.Add(sheet);
        }

        return sheets;
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
