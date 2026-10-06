// Command-line options of the monthly push (also filled by the web app).
#pragma warning disable CS0649 // Csv is only set by the command line
class Opts { public bool DryRun; public bool IgnoreLog; public string Op = "both"; public int Limit; public DateTime? Date; public string? Csv; }
