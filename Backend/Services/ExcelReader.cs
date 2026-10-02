using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace CIPAdmin.Backend.Services;

/// <summary>
/// Minimal .xlsx reader — reads the FIRST worksheet into a DataTable of strings.
/// No external packages (no Interop). Handles shared strings, inline strings,
/// numbers and booleans. Dates arrive as text (bulk-copy target is varchar).
/// </summary>
public static class ExcelReader
{
    public static DataTable ReadFirstSheet(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        // Map the first sheet in workbook.xml to its part path via the rels file.
        var sheetPath = ResolveFirstSheetPath(zip)
            ?? throw new InvalidOperationException("Workbook contains no worksheets.");

        var shared = ReadSharedStrings(zip);
        var entry = zip.GetEntry(sheetPath) ?? throw new InvalidOperationException($"Missing worksheet part '{sheetPath}'.");

        var table = new DataTable();
        int colCount = 0;

        using (var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true }))
        {
            StringBuilder cellText = new();
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element) continue;
                if (xml.LocalName == "row")
                {
                    var row = table.NewRow();
                    bool anyValue = false;
                    int lastCol = -1;

                    while (xml.Read())
                    {
                        if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "row") break;
                        if (xml.NodeType != XmlNodeType.Element) continue;

                        if (xml.LocalName == "c")
                        {
                            var refAttr = xml.GetAttribute("r");
                            var typeAttr = xml.GetAttribute("t");
                            int colIndex = CellRefToIndex(refAttr, lastCol + 1);

                            cellText.Clear();
                            string? rawValue = null;
                            bool isShared = typeAttr == "s";
                            bool isInline = typeAttr == "inlineStr";

                            if (xml.IsEmptyElement)
                            {
                                // self-closing cell — no value
                            }
                            else
                            {
                                var inner = xml.ReadSubtree();
                                while (inner.Read())
                                {
                                    if (inner.NodeType == XmlNodeType.Element)
                                    {
                                        if (isInline && inner.LocalName == "t")
                                        {
                                            cellText.Append(inner.ReadString());
                                        }
                                        else if (inner.LocalName == "v")
                                        {
                                            rawValue = inner.ReadString();
                                        }
                                    }
                                }
                            }

                            string value = isShared
                                ? (rawValue != null && int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var si) && si < shared.Count ? shared[si] : "")
                                : isInline ? cellText.ToString() : (rawValue ?? "");

                            if (colIndex >= table.Columns.Count)
                            {
                                // fill any gap columns so indexes line up
                                while (table.Columns.Count < colIndex) table.Columns.Add($"C{table.Columns.Count}", typeof(string));
                                table.Columns.Add($"C{colIndex}", typeof(string));
                            }

                            row[colIndex] = value;
                            anyValue |= value.Length > 0;
                            if (colIndex > lastCol) lastCol = colIndex;
                        }
                    }

                    if (anyValue)
                    {
                        while (table.Columns.Count < colCount) table.Columns.Add($"C{table.Columns.Count}", typeof(string));
                        table.Rows.Add(row);
                    }
                }
                else if (xml.LocalName == "dimension")
                {
                    // pre-size columns from the dimension ref (e.g. "A1:D123") for stable column order
                    var dim = xml.GetAttribute("ref");
                    var last = dim?.Split(':')[^1];
                    var idx = last == null ? 0 : CellRefToIndex(last, 0);
                    for (int i = 0; i <= idx && i < 256; i++)
                        if (i >= table.Columns.Count) table.Columns.Add($"C{i}", typeof(string));
                    colCount = table.Columns.Count;
                }
            }
        }

        // drop trailing empty columns
        for (int i = table.Columns.Count - 1; i >= 0; i--)
        {
            bool used = table.AsEnumerable().Any(r => r[i] != DBNull.Value && !string.IsNullOrEmpty((string)r[i]));
            if (used) { colCount = i + 1; break; }
        }
        while (table.Columns.Count > Math.Max(colCount, 1)) table.Columns.RemoveAt(table.Columns.Count - 1);

        return table;
    }

    public static DataTable ReadFirstSheet(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return ReadFirstSheet(ms);
    }

    private static string? ResolveFirstSheetPath(ZipArchive zip)
    {
        var wb = zip.GetEntry("xl/workbook.xml");
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (wb == null) return null;

        string? rid = null;
        using (var xml = XmlReader.Create(wb.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
        {
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sheet")
                {
                    rid = xml.GetAttribute("r:id") ?? xml.GetAttribute("id");
                    break;
                }
            }
        }
        if (rid == null) return null;

        if (rels != null)
        {
            using var xml = XmlReader.Create(rels.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship" && xml.GetAttribute("Id") == rid)
                {
                    var target = xml.GetAttribute("Target") ?? "";
                    target = target.TrimStart('/');
                    return target.StartsWith("xl/") ? target : "xl/" + target;
                }
            }
        }
        return "xl/worksheets/sheet1.xml";
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return list;

        using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        StringBuilder si = new();
        bool inSi = false;
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "si")
            {
                inSi = true; si.Clear();
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "si" && inSi)
            {
                list.Add(si.ToString());
                inSi = false;
            }
            else if (inSi && xml.NodeType == XmlNodeType.Element && xml.LocalName == "t")
            {
                si.Append(xml.ReadString());
            }
        }
        return list;
    }

    private static int CellRefToIndex(string? cellRef, int fallback)
    {
        if (string.IsNullOrEmpty(cellRef)) return fallback;
        int idx = 0; bool sawLetter = false;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') { idx = idx * 26 + (ch - 'A' + 1); sawLetter = true; }
            else if (ch is >= 'a' and <= 'z') { idx = idx * 26 + (ch - 'a' + 1); sawLetter = true; }
            else break;
        }
        return sawLetter ? idx - 1 : fallback;
    }
}
