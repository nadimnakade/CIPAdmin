using System.Data;
using System.IO.Compression;
using System.Text;

namespace CIPAdmin.Backend.Services;

/// <summary>
/// Minimal SpreadsheetML (.xlsx) writer — no dependencies, no Excel Interop.
/// Produces a single-sheet workbook with an inline-string table.
/// </summary>
public static class ExcelExporter
{
    public static byte[] BuildXlsx(DataTable table, string sheetName = "Report", bool headerStyle = true)
    {
        var cols = table.Columns.Cast<DataColumn>().ToList();

        string Esc(string? s)
        {
            s = s ?? "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default:
                        if (ch >= 32 && ch != 127) sb.Append(ch); // strip control chars (illegal in XML)
                        break;
                }
            }
            return sb.ToString();
        }

        string CellRef(int row1Based, int col1Based)
        {
            // 1 -> A, 26 -> Z, 27 -> AA ...
            var sb = new StringBuilder();
            var n = col1Based;
            while (n > 0)
            {
                var rem = (n - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }
            return sb.ToString() + row1Based;
        }

        var sheetXml = new StringBuilder();
        sheetXml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheetXml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sheetXml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sheetXml.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
        sheetXml.Append("<cols>");
        for (int c = 1; c <= cols.Count; c++)
            sheetXml.Append($"<col min=\"{c}\" max=\"{c}\" width=\"22\" customWidth=\"1\"/>");
        sheetXml.Append("</cols>");
        sheetXml.Append("<sheetData>");

        // Header row
        sheetXml.Append($"<row r=\"1\">");
        for (int c = 1; c <= cols.Count; c++)
        {
            var styleAttr = headerStyle ? " s=\"1\"" : "";
            sheetXml.Append($"<c r=\"{CellRef(1, c)}\" t=\"inlineStr\"{styleAttr}><is><t xml:space=\"preserve\">{Esc(cols[c - 1].ColumnName)}</t></is></c>");
        }
        sheetXml.Append("</row>");

        // Data rows
        int rowIndex = 2;
        foreach (DataRow row in table.Rows)
        {
            sheetXml.Append($"<row r=\"{rowIndex}\">");
            for (int c = 1; c <= cols.Count; c++)
            {
                var raw = row[c - 1];
                if (raw == DBNull.Value) continue;

                var text = raw.ToString() ?? "";
                if (text.Length == 0) continue;

                // Numbers stay numeric so Excel can aggregate them
                if (raw is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                {
                    sheetXml.Append($"<c r=\"{CellRef(rowIndex, c)}\"><v>{Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                }
                else if (raw is DateTime dt)
                {
                    // Real date serial, not text — this is what lets Excel's own
                    // Data > Sort order rows chronologically.
                    sheetXml.Append($"<c r=\"{CellRef(rowIndex, c)}\" s=\"2\"><v>{dt.ToOADate():0.###############}</v></c>");
                }
                else if (raw is TimeSpan ts)
                {
                    // Time of day as a fraction-of-a-day serial, styled as a time.
                    sheetXml.Append($"<c r=\"{CellRef(rowIndex, c)}\" s=\"3\"><v>{ts.TotalDays:0.###############}</v></c>");
                }
                else
                {
                    sheetXml.Append($"<c r=\"{CellRef(rowIndex, c)}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                }
            }
            sheetXml.Append("</row>");
            rowIndex++;
        }

        sheetXml.Append("</sheetData>");
        // autoFilter must come AFTER sheetData per the CT_Worksheet sequence
        // (sheetPr, dimension, sheetViews, sheetFormatPr, cols, sheetData, ...,
        // autoFilter, ...). Emitting it before sheetData makes Excel refuse the
        // file with "we found a problem with some content" and repair/drop it.
        if (cols.Count > 0)
            sheetXml.Append($"<autoFilter ref=\"A1:{CellRef(1, cols.Count)}\"/>");
        sheetXml.Append("</worksheet>");

        string nsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var workbookXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"" + nsR + "\">" +
            $"<sheets><sheet name=\"{Esc(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

        var workbookRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        var contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>";

        var stylesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\">" +
            "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            // CT_Font is an ordered sequence: b, i, ..., sz, color, name, ...
            // color-before-sz is a schema violation that makes Excel repair the file.
            "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
            "</fonts>" +
            "<fills count=\"3\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2563EB\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "</fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"4\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +                                     // 0 default
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +      // 1 header
            "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +             // 2 date (m/d/yyyy)
            // 3 time (h:mm:ss) — a TimeSpan lands here as a fraction of a day, so it needs a
            // time number format or Excel renders it as a 1900-01-00 date instead.
            "<xf numFmtId=\"21\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "</cellXfs>" +
            "</styleSheet>";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string path, string content)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", contentTypes);
            Add("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            Add("xl/workbook.xml", workbookXml);
            Add("xl/_rels/workbook.xml.rels", workbookRels);
            Add("xl/styles.xml", stylesXml);
            Add("xl/worksheets/sheet1.xml", sheetXml.ToString());
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Build an .xlsx from a raw grid of string cells (first row = header).
    /// A column whose data cells all parse as "clean" numbers is written numeric
    /// so Excel can aggregate it; anything else stays text verbatim (leading
    /// zeros, trailing zeros and comma formatting are never silently altered).
    /// </summary>
    public static byte[] BuildFromArrays(string?[][] grid, string sheetName = "Sheet1")
    {
        static bool Clean(string s, out decimal d)
        {
            d = 0;
            if (s.Length == 0 || s.Length > 18) return false;
            if (!decimal.TryParse(s, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) return false;
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture) == s;
        }

        int rows = grid.Length;
        int cols = 0;
        foreach (var r in grid) cols = Math.Max(cols, r.Length);
        if (cols == 0) cols = 1;

        // Header names (fallback for blank/duplicate headers)
        var names = new string[cols];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < cols; c++)
        {
            var h = (rows > 0 && c < grid[0].Length ? grid[0][c] : null)?.Trim();
            if (string.IsNullOrEmpty(h)) h = $"Column{c + 1}";
            var baseName = h;
            var i = 2;
            while (!used.Add(h)) h = $"{baseName} ({i++})";
            names[c] = h;
        }

        // Column types: decimal only when every data cell (row 2+) is a clean number or empty
        var numeric = new bool[cols];
        for (int c = 0; c < cols; c++)
        {
            numeric[c] = rows > 1;
            for (int r = 1; r < rows && numeric[c]; r++)
            {
                var v = (c < grid[r].Length ? grid[r][c] : null)?.Trim() ?? "";
                if (v.Length == 0) continue;
                if (!Clean(v, out _)) numeric[c] = false;
            }
        }

        var dt = new DataTable();
        for (int c = 0; c < cols; c++)
            dt.Columns.Add(names[c], numeric[c] ? typeof(decimal) : typeof(string));

        for (int r = 1; r < rows; r++)
        {
            var row = dt.NewRow();
            for (int c = 0; c < cols; c++)
            {
                var v = (c < grid[r].Length ? grid[r][c] : null)?.Trim() ?? "";
                if (v.Length == 0) { row[c] = DBNull.Value; continue; }
                row[c] = numeric[c] && Clean(v, out var d) ? d : v;
            }
            dt.Rows.Add(row);
        }
        return BuildXlsx(dt, sheetName);
    }
}
