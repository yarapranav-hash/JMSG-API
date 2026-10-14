using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

// Reads an uploaded .xlsx or .csv file: the first row is the header (column names), every other row is one record.
// Returns one dictionary per record (column name in lower case -> cell text). Only the first sheet of a workbook is read.
static class UploadFile
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<Dictionary<string, string>> Read(string fileName, Stream input)
    {
        var ms = new MemoryStream();
        input.CopyTo(ms);
        ms.Position = 0;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        List<string[]> grid = ext switch
        {
            ".xlsx" => ReadXlsx(ms),
            ".csv" or ".txt" => ReadCsv(ms),
            _ => throw new InvalidOperationException("Please upload an Excel (.xlsx) or CSV (.csv) file. (An old .xls file must be saved as .xlsx first.)")
        };

        var headerAt = grid.FindIndex(r => r.Any(c => c.Trim() != ""));
        if (headerAt < 0) throw new InvalidOperationException("The file is empty.");
        var headers = grid[headerAt].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        if (headers.Distinct().Count(h => h != "") != headers.Count(h => h != ""))
            throw new InvalidOperationException("Two columns have the same name in the first row.");

        var rows = new List<Dictionary<string, string>>();
        for (int at = headerAt + 1; at < grid.Count; at++)
        {
            var line = grid[at];
            if (line.All(c => c.Trim() == "")) continue;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["_row"] = (at + 1).ToString() }; // the row number in the file, for messages
            for (int i = 0; i < headers.Length && i < line.Length; i++)
                if (headers[i] != "") d[headers[i]] = line[i];
            rows.Add(d);
        }
        if (rows.Count == 0) throw new InvalidOperationException("The file has a header row but no records.");
        if (rows.Count > 20000) throw new InvalidOperationException("The file has more than 20,000 rows - please split it.");
        return rows;
    }

    // ---- csv (comma or semicolon, quotes, UTF-8 with or without BOM)
    static List<string[]> ReadCsv(Stream s)
    {
        string text;
        using (var r = new StreamReader(s, new UTF8Encoding(false), true)) text = r.ReadToEnd();
        var firstLine = text.Split('\n', 2)[0];
        var sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';

        var rows = new List<string[]>();
        var row = new List<string>(); var cur = new StringBuilder(); bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; } else q = false; }
                else cur.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == sep) { row.Add(cur.ToString()); cur.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cur.ToString()); cur.Clear(); rows.Add(row.ToArray()); row.Clear();
            }
            else cur.Append(c);
        }
        if (cur.Length > 0 || row.Count > 0) { row.Add(cur.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    // ---- xlsx (a zip of xml files)
    static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    static List<string[]> ReadXlsx(Stream s)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(s, ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new InvalidOperationException("This does not look like a valid .xlsx file."); }
        using (zip)
        {
            XDocument Load(string path) => XDocument.Load((zip.GetEntry(path) ?? throw new InvalidOperationException("This does not look like a valid .xlsx file (missing " + path + ").")).Open());

            // first sheet of the workbook
            var wb = Load("xl/workbook.xml");
            var sheet = wb.Descendants(Ns + "sheet").FirstOrDefault() ?? throw new InvalidOperationException("The workbook has no sheet.");
            var rid = (string?)sheet.Attribute(RelNs + "id");
            var target = Load("xl/_rels/workbook.xml.rels").Descendants(PkgRel + "Relationship")
                .FirstOrDefault(e => (string?)e.Attribute("Id") == rid)?.Attribute("Target")?.Value
                ?? throw new InvalidOperationException("Cannot find the first sheet in the workbook.");
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

            var shared = new List<string>();
            if (zip.GetEntry("xl/sharedStrings.xml") != null)
                foreach (var si in Load("xl/sharedStrings.xml").Descendants(Ns + "si"))
                    shared.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));

            var grid = new List<string[]>();
            foreach (var rowEl in Load(path).Descendants(Ns + "row"))
            {
                var cells = new Dictionary<int, string>();
                int next = 0;
                foreach (var c in rowEl.Elements(Ns + "c"))
                {
                    var r = (string?)c.Attribute("r");
                    var col = r != null ? ColIndex(r) : next;
                    next = col + 1;
                    var t = (string?)c.Attribute("t");
                    var v = c.Element(Ns + "v")?.Value ?? "";
                    cells[col] = t switch
                    {
                        "s" => int.TryParse(v, out var si) && si >= 0 && si < shared.Count ? shared[si] : "",
                        "inlineStr" => string.Concat(c.Descendants(Ns + "t").Select(x => x.Value)),
                        "b" => v == "1" ? "TRUE" : "FALSE",
                        "e" => "", // an Excel error such as #N/A is not a value
                        "str" => v,
                        _ => Number(v) // numbers (and dates, which Excel keeps as serial numbers)
                    };
                }
                var width = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
                var line = new string[width];
                for (int i = 0; i < width; i++) line[i] = cells.GetValueOrDefault(i, "");
                // keep blank rows too so the row numbers in the messages match the sheet
                var rowIndex = (int?)rowEl.Attribute("r") ?? grid.Count + 1;
                while (grid.Count < rowIndex - 1) grid.Add(Array.Empty<string>());
                grid.Add(line);
            }
            return grid;
        }
    }

    static int ColIndex(string cellRef)
    {
        int n = 0;
        foreach (var ch in cellRef) { if (ch < 'A' || ch > 'Z') break; n = n * 26 + (ch - 'A' + 1); }
        return n - 1;
    }

    // Excel stores 2573.9 as 2573.9000000000001 sometimes: show numbers the way the cell shows them (at most 10 decimals)
    static string Number(string v)
    {
        if (v == "" || !double.TryParse(v, NumberStyles.Float, Inv, out var d)) return v;
        if (Math.Abs(d) >= 1e15) return v;
        return Math.Round((decimal)d, 10).ToString("0.##########", Inv);
    }
}
