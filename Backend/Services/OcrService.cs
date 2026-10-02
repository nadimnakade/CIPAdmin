using Tesseract;

namespace CIPAdmin.Backend.Services;

/// <summary>A recognised word with its position on the image (pixels).</summary>
public record OcrWord(double X, double Y, double W, double H, string Text);

/// <summary>Result of OCR → table reconstruction: grid of cells, row 0 = header row.</summary>
public class OcrGrid
{
    public List<string[]> Rows { get; set; } = new();
    public int RowCount { get; set; }
    public int ColCount { get; set; }
    public int WordCount { get; set; }
}

public interface IOcrService
{
    /// <summary>Run OCR over an image stream and rebuild the spreadsheet grid.</summary>
    Task<OcrGrid> ExtractGridAsync(Stream imageStream);
}

/// <summary>
/// Tesseract-based OCR that converts a photo/screenshot of an Excel sheet back into
/// a grid. Reconstruction is geometric: words are clustered into visual rows by their
/// vertical centre, and column boundaries are detected from the horizontal gaps
/// between words (larger than one character height) so whitespace columns survive.
/// </summary>
public class OcrService : IOcrService
{
    public async Task<OcrGrid> ExtractGridAsync(Stream imageStream)
    {
        var tessDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(tessDir, "eng.traineddata")))
            throw new InvalidOperationException(
                "OCR language data is missing (tessdata/eng.traineddata). Redeploy the backend so the tessdata folder ships with the binaries.");

        Pix img;
        try
        {
            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms);
            img = Pix.LoadFromMemory(ms.ToArray());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The file could not be decoded as an image (JPG, PNG, BMP or GIF expected).", ex);
        }

        var cells = new List<OcrWord>();
        try
        {
            // Engine load is heavyweight and Tesseract is not thread-safe, so one
            // engine per request keeps concurrent uploads independent.
            using (var engine = new TesseractEngine(tessDir, "eng", EngineMode.Default))
            {
                // SparseText suits sheet-like layouts: no assumption of a single
                // text block, works with ruled lines and wide column gaps.
                engine.DefaultPageSegMode = PageSegMode.SparseText;
                using var page = engine.Process(img);
                using var iter = page.GetIterator();
                iter.Begin();
                do
                {
                    if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var rect)) continue;
                    var text = iter.GetText(PageIteratorLevel.Word);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    text = text.Trim().Replace("\n", " ").Trim();
                    if (text.Length == 0) continue;
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    cells.Add(new OcrWord(rect.X1, rect.Y1, rect.Width, rect.Height, text));
                } while (iter.Next(PageIteratorLevel.Word));
            }
        }
        finally
        {
            img.Dispose();
        }

        return BuildGrid(cells);
    }

    /// <summary>Turn positioned words into a grid (rows by Y-clustering, columns by gap edges).</summary>
    public static OcrGrid BuildGrid(List<OcrWord> cells)
    {
        var rows = new List<string[]>();
        var result = new OcrGrid { WordCount = cells.Count };
        if (cells.Count == 0) return result;

        // 1) Body text size = median word height; drives all tolerances.
        var heights = cells.Select(c => c.H).OrderBy(h => h).ToList();
        double fontH = heights[heights.Count / 2];
        double rowTol = Math.Max(6, fontH * 0.6);   // same visual line
        double colGap = Math.Max(8, fontH * 1.2);   // whitespace that means "new column"

        // 2) Cluster words into visual rows by vertical centre (top-to-bottom).
        var ordered = cells.OrderBy(c => c.Y + c.H / 2).ToList();
        var lines = new List<List<OcrWord>>();
        foreach (var w in ordered)
        {
            if (lines.Count > 0)
            {
                var cur = lines[^1];
                double centre = cur.Average(c => c.Y + c.H / 2);
                if (Math.Abs((w.Y + w.H / 2) - centre) <= rowTol)
                {
                    cur.Add(w);
                    continue;
                }
            }
            lines.Add(new List<OcrWord> { w });
        }

        // 3) Column boundaries: midpoints of the wide gaps, gathered across all
        //    rows so columns that appear on any row are detected.
        var edges = new List<double>();
        foreach (var line in lines)
        {
            var lw = line.OrderBy(c => c.X).ToList();
            for (int i = 1; i < lw.Count; i++)
            {
                double gap = lw[i].X - (lw[i - 1].X + lw[i - 1].W);
                if (gap > colGap) edges.Add(lw[i].X - gap / 2);
            }
        }
        edges.Sort();
        var bounds = new List<double>();   // left edge of each column after column 1
        foreach (var e in edges)
            if (bounds.Count == 0 || e - bounds[^1] > fontH)   // merge near-duplicates
                bounds.Add(e);

        // 4) No wide gaps anywhere → it is plain text; keep one word per line.
        if (bounds.Count == 0)
        {
            foreach (var line in lines)
            {
                var text = string.Join(" ", line.OrderBy(c => c.X).Select(c => c.Text));
                rows.Add(new[] { text });
            }
            result.Rows = rows;
            result.RowCount = rows.Count;
            result.ColCount = 1;
            return result;
        }

        // 5) Assign each word to a column by its horizontal midpoint, merging
        //    words that land in the same cell (multi-word cells).
        int colCount = bounds.Count + 1;
        foreach (var line in lines)
        {
            var arr = new string[colCount];
            foreach (var w in line.OrderBy(c => c.X))
            {
                double mid = w.X + w.W / 2;
                int ci = 0;
                for (int k = 0; k < bounds.Count; k++)
                    if (mid > bounds[k]) ci = k + 1;
                arr[ci] = arr[ci] == null ? w.Text : arr[ci] + " " + w.Text;
            }
            rows.Add(arr);
        }

        result.Rows = rows;
        result.RowCount = rows.Count;
        result.ColCount = colCount;
        return result;
    }
}
