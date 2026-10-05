using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

// MeterPush - pushes meter data to MeterReadingService over SOAP 1.1.
//   setHourly  : mapping in dbo.PushConfig (SOURCE_QUERY + field rows), watermark LAST_ID
//   setMonthly : mapping in dbo.PushConfig_Monthly, progress in dbo.PushLog_Monthly (see MonthlyPush.cs)
//
//   MeterPush                         push hourly + monthly
//   MeterPush --op hourly|monthly     only one of them
//   MeterPush --dry-run               print the SOAP requests, send nothing, write nothing
//   MeterPush --limit 5               push at most 5 rows/services per operation (first test)
//   MeterPush --op monthly --csv f.csv   write everything setMonthly would send to f.csv (+ f_skipped.csv); sends nothing
//   MeterPush --date 2026-10-01      monthly push date (default: today); opening = this date - 1 month

var opts = ParseArgs(args);
var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var connStr = JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement.GetProperty("ConnectionString").GetString()!;

using var db = new SqlConnection(connStr);
db.Open();

var config = LoadConfig(db);
var global = config.Where(c => c.Operation == "GLOBAL" && c.IsActive)
                   .ToDictionary(c => c.ParamName, c => c.SourceValue ?? "", StringComparer.OrdinalIgnoreCase);

var endpoint = global["ENDPOINT"];
var batchSize = int.Parse(global.GetValueOrDefault("BATCH_SIZE", "500"));
if (opts.Limit > 0) batchSize = Math.Min(batchSize, opts.Limit);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(int.Parse(global.GetValueOrDefault("TIMEOUT_SEC", "60"))) };

var failed = false;
if (opts.Op is "both" or "hourly" && !RunHourly()) failed = true;
if (opts.Op is "both" or "monthly" && !new MonthlyPush(db, connStr, http, global, opts).Run()) failed = true;
return failed ? 1 : 0;

// ---------------------------------------------------------------------------

bool RunHourly()
{
    const string op = "setHourly";
    var rows = config.Where(c => c.Operation == op && c.IsActive).ToList();
    var query = rows.FirstOrDefault(r => r.SourceType == "QUERY")?.SourceValue;
    var lastIdRow = rows.FirstOrDefault(r => r.ParamName == "LAST_ID");
    var fields = rows.Where(r => r.SourceType is "COLUMN" or "CONST").OrderBy(r => r.SortOrder).ToList();

    if (query == null || lastIdRow == null || fields.Count == 0)
    {
        Log($"{op}: not configured (needs SOURCE_QUERY, LAST_ID and field rows) - skipped");
        return true;
    }

    var lastId = long.Parse(lastIdRow.SourceValue ?? "0");
    var table = new DataTable();
    using (var cmd = new SqlCommand(query, db) { CommandTimeout = 300 })
    {
        cmd.Parameters.AddWithValue("@LastId", lastId);
        cmd.Parameters.AddWithValue("@BatchSize", batchSize);
        new SqlDataAdapter(cmd).Fill(table);
    }

    Log($"{op}: {table.Rows.Count} row(s) after LAST_ID={lastId}");
    int ok = 0;
    foreach (DataRow row in table.Rows)
    {
        var rowId = Convert.ToInt64(row["row_id"]);
        var xml = Soap.Envelope(op, fields.Select(f => (f.ParamName, ResolveHourly(op, f, row))));

        if (opts.DryRun)
        {
            Console.WriteLine($"--- {op} row_id={rowId} ---");
            Console.WriteLine(xml);
            continue;
        }

        var (success, detail) = Soap.Post(http, endpoint, op, xml);
        if (!success)
        {
            Log($"{op}: row_id={rowId} FAILED - {detail}. Stopping; it will be retried next run.");
            return false;
        }

        using var upd = new SqlCommand(
            "UPDATE dbo.PushConfig SET SourceValue=@v, UpdatedOn=GETDATE() WHERE ConfigId=@id", db);
        upd.Parameters.AddWithValue("@v", rowId.ToString());
        upd.Parameters.AddWithValue("@id", lastIdRow.ConfigId);
        upd.ExecuteNonQuery();
        ok++;
        Log($"{op}: row_id={rowId} pushed - {detail}");
    }
    if (!opts.DryRun) Log($"{op}: done, {ok} pushed");
    return true;
}

string? ResolveHourly(string op, Cfg f, DataRow row)
{
    if (f.SourceType == "COLUMN")
    {
        if (!row.Table.Columns.Contains(f.SourceValue ?? ""))
            throw new InvalidOperationException($"{op}.{f.ParamName}: column '{f.SourceValue}' is not in the SOURCE_QUERY result");
        return row[f.SourceValue!] is DBNull ? null : Convert.ToString(row[f.SourceValue!]);
    }
    var v = f.SourceValue;
    if (v == null) return null;
    foreach (var (k, val) in global) v = v.Replace("{" + k + "}", val);
    return v;
}

static List<Cfg> LoadConfig(SqlConnection db)
{
    var list = new List<Cfg>();
    using var cmd = new SqlCommand(
        "SELECT ConfigId, Operation, ParamName, SourceType, SourceValue, SortOrder, IsActive FROM dbo.PushConfig", db);
    using var r = cmd.ExecuteReader();
    while (r.Read())
        list.Add(new Cfg(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                         r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetBoolean(6)));
    return list;
}

static Opts ParseArgs(string[] a)
{
    var o = new Opts();
    for (int i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--dry-run": o.DryRun = true; break;
            case "--op": o.Op = a[++i].ToLowerInvariant(); break;
            case "--limit": o.Limit = int.Parse(a[++i]); break;
            case "--csv": o.Csv = Path.GetFullPath(a[++i]); o.DryRun = true; break;
            case "--date": o.Date = DateTime.ParseExact(a[++i], "yyyy-MM-dd", null); break;
            default: throw new ArgumentException($"Unknown argument {a[i]}");
        }
    }
    if (o.Op is not ("both" or "hourly" or "monthly")) throw new ArgumentException("--op must be hourly, monthly or both");
    return o;
}

static void Log(string m) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}");

record Cfg(int ConfigId, string Operation, string ParamName, string SourceType, string? SourceValue, int SortOrder, bool IsActive);
class Opts { public bool DryRun; public string Op = "both"; public int Limit; public DateTime? Date; public string? Csv; }
