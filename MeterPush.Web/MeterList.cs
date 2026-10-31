using System.Globalization;

// The list of all meters the monthly push should cover: an Excel/CSV file on disk (path in appsettings.json "AllMetersFile";
// columns: service, optionally Meter No and DC). It is read when first needed and again whenever the file changes.
// The Monthly tab compares it with dbo.PushLog_Monthly to show the meters not sent yet.
static class MeterList
{
    public sealed record Meter(string Dc, string Service, string MeterNo);
    public sealed record Data(string Name, List<Meter> Meters, List<string> Notes);

    static readonly string[] ServiceNames = { "service", "service no", "service no.", "service number", "service_no", "serviceno" };
    static readonly string[] MeterNames = { "meter no", "meter no.", "meter number", "meter_no", "meterno" };
    static readonly string[] DcNames = { "dc", "dc no", "dc name" };

    static readonly object Gate = new();
    static Data? cached;
    static string? cachedPath;
    static DateTime cachedStamp;
    static long cachedLength;

    // service numbers are compared without spaces and in upper case ("0301 02087" = "030102087")
    public static string Norm(string s) => s.Replace(" ", "").Trim().ToUpperInvariant();

    // throws FileNotFoundException when the file is missing, InvalidOperationException when it can't be understood
    public static Data Load(string path)
    {
        lock (Gate)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException("file not found", path);
            if (cached != null && cachedPath == path && cachedStamp == fi.LastWriteTimeUtc && cachedLength == fi.Length) return cached;
            // ReadWrite sharing: the file may be open in Excel
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var d = Parse(path, fs);
            if (d.Meters.Count == 0) throw new InvalidOperationException("no meters found in the file");
            cached = d; cachedPath = path; cachedStamp = fi.LastWriteTimeUtc; cachedLength = fi.Length;
            return d;
        }
    }

    static Data Parse(string fileName, Stream input)
    {
        var rows = UploadFile.Read(fileName, input);
        var first = rows[0];
        var svcCol = ServiceNames.FirstOrDefault(first.ContainsKey) ?? throw new InvalidOperationException("the file has no 'service' column in the first row");
        var meterCol = MeterNames.FirstOrDefault(first.ContainsKey);
        var dcCol = DcNames.FirstOrDefault(first.ContainsKey);

        var meters = new List<Meter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int blank = 0, dup = 0;
        foreach (var r in rows)
        {
            var svc = r.GetValueOrDefault(svcCol, "").Trim();
            if (svc == "") { blank++; continue; }
            if (!seen.Add(Norm(svc))) { dup++; continue; }
            meters.Add(new Meter(dcCol == null ? "" : r.GetValueOrDefault(dcCol, "").Trim(), svc, meterCol == null ? "" : r.GetValueOrDefault(meterCol, "").Trim()));
        }
        var notes = new List<string> { $"{rows.Count:N0} row(s) read, {meters.Count:N0} meter(s) in the list." };
        if (blank > 0) notes.Add($"{blank:N0} row(s) without a service number were ignored.");
        if (dup > 0) notes.Add($"{dup:N0} repeated service number(s) were ignored.");
        return new Data(Path.GetFileName(fileName), meters, notes);
    }
}
