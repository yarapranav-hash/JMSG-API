using System.Globalization;

// Reads the opening-readings file (.xlsx or .csv) of the monthly push: one row per meter, first row = column names.
// Used columns (not case sensitive): METER NO, CLOSING READING (= the opening reading of this month's push), Service No (cross-check, optional),
// Present month reading date (the date of that reading, optional: becomes op_rdgdt). The other columns are ignored.
static class OpeningFile
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly string[] MeterNames = { "meter no", "meter no.", "meter number", "meter_no", "meterno", "meter" };
    static readonly string[] ReadingNames = { "closing reading", "closing_reading", "closing kwh", "closing" };
    static readonly string[] DateNames = { "present month reading date", "reading date", "present month reading dt" };
    static readonly string[] ServiceNames = { "service no", "service no.", "service number", "service_no", "serviceno" };
    static readonly string[] DateFormats = { "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd" };

    public sealed record Result(Dictionary<string, OpeningRow> Rows, List<string> Notes, int Total);

    static string? Find(Dictionary<string, string> first, string[] names) => names.FirstOrDefault(first.ContainsKey);

    public static Result Read(string fileName, Stream input)
    {
        var rows = UploadFile.Read(fileName, input);
        var first = rows[0];
        var meterCol = Find(first, MeterNames) ?? throw new InvalidOperationException("The file has no 'METER NO' column in the first row.");
        var readingCol = Find(first, ReadingNames) ?? throw new InvalidOperationException("The file has no 'CLOSING READING' column in the first row.");
        var dateCol = Find(first, DateNames);
        var serviceCol = Find(first, ServiceNames);

        var notes = new List<string>();
        var parsed = new List<(string Meter, string RowNo, OpeningRow Row)>();
        int blank = 0, bad = 0, noMeter = 0, undated = 0;
        var badRows = new List<string>();

        foreach (var r in rows)
        {
            var meter = r.GetValueOrDefault(meterCol, "").Trim().ToUpperInvariant();
            if (meter == "") { noMeter++; continue; }
            var reading = r.GetValueOrDefault(readingCol, "").Trim();
            if (reading == "") { blank++; continue; }
            if (!decimal.TryParse(reading, NumberStyles.Number, Inv, out var kwh) || kwh < 0)
            {
                bad++;
                if (badRows.Count < 5) badRows.Add(r.GetValueOrDefault("_row", "?"));
                continue;
            }
            DateTime? date = dateCol == null ? null : ParseDate(r.GetValueOrDefault(dateCol, ""));
            if (date == null) undated++;
            var svc = serviceCol == null ? null : r.GetValueOrDefault(serviceCol, "").Trim();
            parsed.Add((meter, r.GetValueOrDefault("_row", "?"), new OpeningRow(kwh, date, string.IsNullOrEmpty(svc) ? null : svc)));
        }

        // a meter number that appears twice can't be matched safely: both rows are left out
        var dupMeters = parsed.GroupBy(p => p.Meter).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var dupSet = new HashSet<string>(dupMeters);
        var result = new Dictionary<string, OpeningRow>(StringComparer.Ordinal);
        foreach (var p in parsed.Where(p => !dupSet.Contains(p.Meter))) result[p.Meter] = p.Row;

        notes.Add($"{rows.Count:N0} row(s) read, {result.Count:N0} usable.");
        if (blank > 0) notes.Add($"{blank:N0} row(s) have no closing reading - those meters use the normal opening reading.");
        if (bad > 0) notes.Add($"{bad:N0} row(s) have a closing reading that is not a number (rows {string.Join(", ", badRows)}{(bad > badRows.Count ? ", ..." : "")}) - ignored.");
        if (noMeter > 0) notes.Add($"{noMeter:N0} row(s) have no meter number - ignored.");
        if (dupMeters.Count > 0) notes.Add($"{dupMeters.Count:N0} meter number(s) appear more than once ({string.Join(", ", dupMeters.Take(5))}{(dupMeters.Count > 5 ? ", ..." : "")}) - left out, those meters use the normal opening reading.");
        if (dateCol == null) notes.Add("No 'Present month reading date' column - op_rdgdt stays push date minus one month.");
        else if (undated > 0) notes.Add($"{undated:N0} usable row(s) have no readable reading date (dd.MM.yyyy) - op_rdgdt stays push date minus one month for them.");
        if (serviceCol == null) notes.Add("No 'Service No' column - meters are matched on the meter number only.");
        return new Result(result, notes, rows.Count);
    }

    static DateTime? ParseDate(string s)
    {
        s = s.Trim();
        if (s == "") return null;
        if (DateTime.TryParseExact(s, DateFormats, Inv, DateTimeStyles.None, out var d)) return d.Date;
        // a real Excel date arrives as a serial number
        if (double.TryParse(s, NumberStyles.Float, Inv, out var serial) && serial > 20000 && serial < 80000) return DateTime.FromOADate(Math.Floor(serial)).Date;
        return null;
    }
}
