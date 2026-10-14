using System.IO.Compression;
using System.Security;
using System.Text;

// Minimal .xlsx writer (several sheets, every cell stored as text so values such as 030102233 keep their leading zero).
static class XlsxWriter
{
    public static byte[] Build(IEnumerable<(string Name, List<string[]> Rows)> sheets)
    {
        var list = sheets.ToList();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                string.Concat(list.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(list.Select((s, i) => $"<sheet name=\"{Esc(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(list.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                $"<Relationship Id=\"rId{list.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            // style 1 = bold (header row)
            Add(zip, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs></styleSheet>");

            for (int i = 0; i < list.Count; i++)
            {
                var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><sheetData>");
                for (int r = 0; r < list[i].Rows.Count; r++)
                {
                    sb.Append($"<row r=\"{r + 1}\">");
                    var cells = list[i].Rows[r];
                    for (int c = 0; c < cells.Length; c++)
                    {
                        if (string.IsNullOrEmpty(cells[c])) continue;
                        sb.Append($"<c r=\"{Col(c)}{r + 1}\" t=\"inlineStr\"{(r == 0 ? " s=\"1\"" : "")}><is><t xml:space=\"preserve\">{Esc(cells[c])}</t></is></c>");
                    }
                    sb.Append("</row>");
                }
                sb.Append("</sheetData></worksheet>");
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", sb.ToString());
            }
        }
        return ms.ToArray();
    }

    static void Add(ZipArchive zip, string path, string xml)
    {
        using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        w.Write(xml);
    }

    static string Esc(string s) => SecurityElement.Escape(new string(s.Where(ch => ch >= ' ' || ch is '\t' or '\n' or '\r').ToArray())) ?? "";

    static string Col(int i) { var s = ""; for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s; return s; }
}
