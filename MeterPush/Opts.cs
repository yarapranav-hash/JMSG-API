// Command-line options of the monthly push (also filled by the web app).
#pragma warning disable CS0649 // Csv, FullRead and VerifyQuick are only set by the command line
// Limit > 0 is a quick test: only the meters needed are read. FullRead = read every meter even with a limit.
// Opening: opening readings taken from an uploaded file instead of the meter tables (key = meter number, upper case); OpeningName = the file's name.
class Opts
{
    public bool DryRun; public bool IgnoreLog; public bool FullRead; public string Op = "both"; public int Limit; public int VerifyQuick; public DateTime? Date; public string? Csv;
    public Dictionary<string, OpeningRow>? Opening; public string? OpeningName;
}

// One meter of an opening-readings file: the reading, the date it was taken (null = not in the file) and the service number (null = not in the file)
record OpeningRow(decimal Kwh, DateTime? Date, string? ServiceNo);
