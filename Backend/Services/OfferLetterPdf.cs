using System.Globalization;
using System.Text;

namespace CIPAdmin.Backend.Services;

public static class OfferLetterPdf
{
    public class OfferData
    {
        public string CandidateName { get; set; } = "";
        public string Designation { get; set; } = "";
        public string Location { get; set; } = "";
        public DateTime Doj { get; set; }
        public decimal AnnualCtc { get; set; }
        public string CompanyName { get; set; } = "";
    }

    public static byte[] Build(OfferData d)
    {
        var suffix = Suffix(d.Doj.Day);
        var dojLong = $"{d.Doj:dd}{suffix} {d.Doj:MMM yyyy}";
        var ctc = d.AnnualCtc.ToString("N0", new CultureInfo("en-IN"));

        var paragraphs = new[]
        {
            $"Dear {d.CandidateName},",
            "It gives us great pleasure to offer you employment with {CO} as a {DESIG}. Your employment will be based out of our {LOC} office.",
            "Your annual remuneration will be Rupees {CTC} (Rupees {CTCWORDS} Only) per annum, inclusive of all components.",
            "You will be required to join us on or before {DOJ}. This offer is contingent upon successful verification of your documents and references.",
            "We look forward to a long and mutually rewarding association. Please sign a copy of this offer letter as a token of your acceptance.",
            "With best wishes,",
        };

        var body = paragraphs
            .Select(p => p
                .Replace("{CO}", d.CompanyName)
                .Replace("{DESIG}", string.IsNullOrWhiteSpace(d.Designation) ? "Designated Role" : d.Designation)
                .Replace("{LOC}", string.IsNullOrWhiteSpace(d.Location) ? "Head Office" : d.Location)
                .Replace("{CTCWORDS}", NumberToWords((long)d.AnnualCtc))
                .Replace("{CTC}", ctc)
                .Replace("{DOJ}", dojLong))
            .ToList();

        var items = new List<object>
        {
            new PdfDoc.Line("OFFER LETTER", 14, Bold: true, CenterX: 300f),
            new PdfDoc.Line(""),
            new PdfDoc.Line(d.CompanyName, 10, Bold: true),
            new PdfDoc.Line($"Date: {DateTime.Now:dd MMMM yyyy}"),
            new PdfDoc.Line(""),
            new PdfDoc.Line(d.CandidateName, 10, Bold: true),
            new PdfDoc.Line(""),
            new PdfDoc.Line($"Subject: Offer of Employment - {d.Designation}", 10, Bold: true),
            new PdfDoc.Line(""),
            new PdfDoc.Paragraph(body, 10, 15),
            new PdfDoc.Line(""),
            new PdfDoc.Line("(Authorized Signatory)"),
            new PdfDoc.Line(d.CompanyName, 10, Bold: true),
        };

        return PdfDoc.Build(new[] { new PdfDoc.Page(items, 56f, 72f) });
    }

    private static string Suffix(int day) => day switch
    {
        11 or 12 or 13 => "th",
        _ when day % 10 == 1 => "st",
        _ when day % 10 == 2 => "nd",
        _ when day % 10 == 3 => "rd",
        _ => "th",
    };

    private static string NumberToWords(long n)
    {
        if (n <= 0) return "Zero";
        var map = new[] { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
            "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
        var tens = new[] { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };
        string Three(int x)
        {
            var s = "";
            if (x >= 100) { s += map[x / 100] + " Hundred"; x %= 100; if (x > 0) s += " "; }
            if (x >= 20) { s += tens[x / 10]; x %= 10; if (x > 0) s += " " + map[x]; }
            else if (x > 0) s += map[x];
            return s;
        }
        var parts = new List<string>();
        long crore = n / 10000000; n %= 10000000;
        long lakh = n / 100000; n %= 100000;
        long thousand = n / 1000; n %= 1000;
        if (crore > 0) parts.Add(Three((int)crore) + " Crore");
        if (lakh > 0) parts.Add(Three((int)lakh) + " Lakh");
        if (thousand > 0) parts.Add(Three((int)thousand) + " Thousand");
        if (n > 0) parts.Add(Three((int)n));
        return string.Join(" ", parts);
    }
}

/// <summary>Minimal PDF writer — Helvetica Base-14 fonts only, no external packages.
/// Sufficient for letters: text lines, wrapped paragraphs, simple pages.</summary>
public static class PdfDoc
{
    public record Line(string Text, float Size = 10, bool Bold = false, float? CenterX = null);
    public record Paragraph(IReadOnlyList<string> Items, float Size, float Leading);
    public record Page(IReadOnlyList<object> Items, float MarginX, float MarginTop);

    public static byte[] Build(PdfDoc.Page[] pages)
    {
        var objects = new List<string>();
        var streams = new List<string>();

        // ---- page content streams
        for (var p = 0; p < pages.Length; p++)
        {
            var sb = new StringBuilder();
            float y = 792f - pages[p].MarginTop;
            var x = pages[p].MarginX;

            foreach (var item in pages[p].Items)
            {
                switch (item)
                {
                    case Line l:
                        if (l.CenterX is float cx)
                        {
                            var w = TextWidth(l.Text, l.Size, l.Bold);
                            Draw(sb, Escape(l.Text), cx - w / 2f, y, l.Size, l.Bold);
                        }
                        else
                        {
                            Draw(sb, Escape(l.Text), x, y, l.Size, l.Bold);
                        }
                        y -= l.Size * 1.6f;
                        break;

                    case Paragraph para:
                        foreach (var raw in para.Items)
                        {
                            foreach (var chunk in Wrap(raw, para.Size, 496f))
                            {
                                Draw(sb, Escape(chunk), x, y, para.Size, false);
                                y -= para.Leading;
                            }
                            y -= 6f; // gap between paragraphs
                        }
                        break;
                }

                if (y < 48f) break; // crude overflow guard (letters fit one page)
            }

            streams.Add(sb.ToString());
        }

        // ---- PDF object assembly (object numbers = index + 1)
        // 1: catalog, 2: pages, 3..: page objects, then content streams, then fonts.
        var npages = pages.Length;
        var pageObjStart = 3;
        var contentObjStart = pageObjStart + npages;
        var f1Obj = contentObjStart + npages;
        var f2Obj = f1Obj + 1;

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");                                   // obj 1
        objects.Add($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, npages).Select(i => $"{pageObjStart + i} 0 R"))}] /Count {npages} >>"); // obj 2

        for (var i = 0; i < npages; i++)
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Resources << /Font << /F1 {f1Obj} 0 R /F2 {f2Obj} 0 R >> >> " +
                $"/Contents {contentObjStart + i} 0 R >>");

        for (var i = 0; i < npages; i++)
            objects.Add($"<< /Length {streams[i].Length} >>\nstream\n{streams[i]}\nendstream");

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");

        var buf = new StringBuilder("%PDF-1.4\n");
        for (var i = 0; i < objects.Count; i++)
            buf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");

        var pdf = buf.ToString();
        var xrefPos = Encoding.Latin1.GetByteCount(pdf);
        var count = objects.Count + 1;
        var xref = new StringBuilder($"xref\n0 {count}\n0000000000 65535 f \n");
        var pos = Encoding.Latin1.GetByteCount("%PDF-1.4\n");
        for (var i = 0; i < objects.Count; i++)
        {
            xref.Append($"{pos:D10} 00000 n \n");
            pos += Encoding.Latin1.GetByteCount($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        xref.Append($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF");

        return Encoding.Latin1.GetBytes(pdf + xref);
    }

    private static void Draw(StringBuilder sb, string textEscaped, float x, float y, float size, bool bold)
        => sb.Append("BT /").Append(bold ? "F2" : "F1").Append(' ')
              .Append(size.ToString(CultureInfo.InvariantCulture))
              .Append(" Tf ").Append(x.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(y.ToString(CultureInfo.InvariantCulture)).Append(" Td (")
              .Append(textEscaped).Append(") Tj ET\n");

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static float TextWidth(string s, float size, bool bold) => s.Length * size * (bold ? 0.55f : 0.5f);

    private static IEnumerable<string> Wrap(string text, float size, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) { yield return ""; yield break; }
        var maxChars = (int)(maxWidth / (size * 0.5f));
        var words = text.Split(' ');
        var line = "";
        foreach (var w in words)
        {
            var candidate = line.Length == 0 ? w : line + " " + w;
            if (candidate.Length > maxChars && line.Length > 0)
            {
                yield return line;
                line = w;
            }
            else line = candidate;
        }
        if (line.Length > 0) yield return line;
    }
}
