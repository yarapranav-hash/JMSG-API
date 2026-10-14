// Command-line options of the monthly push (also filled by the web app).
#pragma warning disable CS0649 // Csv, FullRead and VerifyQuick are only set by the command line
// Limit > 0 is a quick test: only the meters needed are read. FullRead = read every meter even with a limit.
class Opts { public bool DryRun; public bool IgnoreLog; public bool FullRead; public string Op = "both"; public int Limit; public int VerifyQuick; public DateTime? Date; public string? Csv; }
