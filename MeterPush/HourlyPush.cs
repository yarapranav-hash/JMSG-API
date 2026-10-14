using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

// setHourly (shared by the web app). One call of dbo.PrBLSTotalDCWiseMeterDataInstantDay for the reading date gives the latest
// reading of every meter for that day; only the meters whose "Date & Time" is exactly the chosen hour (HH:00) are sent, with reading_time = HH:00:00.
// The field rules (which value comes from which column, which are fixed) are the ones in Fields below.
// Progress / "already sent" is kept in dbo.PushLog_Hourly (sql\05_create_PushLog_Hourly.sql), one row per (reading date, hour, service).
class HourlyPush
{
    const string Op = "setHourly";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly SqlConnection db;
    readonly string connStr;
    readonly HttpClient http;
    readonly Dictionary<string, string> global;
    readonly Action<string> logSink;

    public HourlyPush(SqlConnection db, string connStr, HttpClient http, Dictionary<string, string> global, Action<string>? log = null)
    {
        this.db = db; this.connStr = connStr; this.http = http; this.global = global;
        logSink = log ?? (m => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}"));
    }

    // ------------------------------------------------------------ field rules, in the order of the request
    // "proc:<column>" = value from the procedure row, "svc:*" = from ServiceDetails, "date"/"time" = reading date / hour, anything else = fixed value
    internal static readonly (string Name, string Source)[] Fields =
    {
        ("vendor_code", "0"), ("reading_date", "date"), ("reading_time", "time"), ("unique_scno", "svc:unique"),
        ("reading_kvah", "proc:Meter Reading(kVAh)"), ("reading_kwh", "proc:Meter Reading(kWh)"), ("kwh_status", "01"), ("kvah_status", "01"),
        ("rmd_kw", "proc:MD kW"), ("rmd_kva", "proc:MD kVA"), ("phase", "1"), ("meterno", "proc:MSN"), ("metermake", "ECIL"), ("mf", "1"),
        ("pf", "proc:Power Factor"), ("deducted_amt", "0"), ("balance_amt", "0"), ("old_reading_kvah", "0"), ("old_reading_kwh", "0"),
        ("new_reading_kvah", "0"), ("new_reading_kwh", "0"), ("new_meter_mf", "0"), ("r_vol", "0"), ("y_vol", "0"), ("b_vol", "0"),
        ("r_cur", "0"), ("y_cur", "0"), ("b_cur", "0"), ("vol_cur_miss_flag", "0"), ("consumed_units", "0"), ("relay_status", "0"),
        ("opn_rdg", "0"), ("opn_balance", "0"), ("slab_rate", "0"), ("crje", "0"), ("drje", "0"), ("echgs", "0"), ("fchgs", "0"),
        ("cchgs", "0"), ("edchgs", "0"), ("mphase", "1"), ("scno", "svc:compact"), ("username", "setting:SOAP_USERNAME"), ("password", "setting:SOAP_PASSWORD")
    };

    // values the vendor cannot do without: a meter that has none of them is skipped instead of sent
    static readonly string[] Needed = { "reading_kvah", "reading_kwh", "rmd_kw", "rmd_kva", "pf", "meterno" };

    internal record HItem(string ServiceNo, string Unique, Dictionary<string, string?> Values, string Xml, bool Done, string ReadAt);
    internal record HSkip(string ServiceNo, string? Unique, string Why);

    internal sealed class HPlan
    {
        public DateTime Date { get; init; }
        public int Hour { get; init; }
        public string Endpoint { get; init; } = "";
        public int Limit { get; init; }
        public bool IgnoreLog { get; init; }
        public bool LogReady { get; init; }            // dbo.PushLog_Hourly exists (needed to send)
        public int ProcRows { get; init; }             // meters the procedure returned for the day
        public List<HItem> Items { get; init; } = new();   // everything that passed the checks (including meters already sent this hour)
        public List<HSkip> Skips { get; init; } = new();
        public List<HItem> ToSend { get; init; } = new();
        public string Label => $"{Date:yyyy-MM-dd} {Hour:00}:00";
    }

    // ------------------------------------------------------------ plan (reads only)

    internal HPlan Plan(DateTime date, int hour, int limit, bool ignoreLog)
    {
        var endpoint = global["ENDPOINT"];
        var logReady = LogTableExists();
        if (!logReady) Log("setHourly: dbo.PushLog_Hourly not found - the preview works, but sending needs it (run sql\\05_create_PushLog_Hourly.sql)");

        var services = LoadServices();
        var done = logReady ? LoadDone(date, hour) : new HashSet<string>(StringComparer.Ordinal);

        Log($"setHourly: reading all meters for {date:yyyy-MM-dd} through the procedure (1 to 7 minutes)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dictionary<string, MonthlyPush.Reading> rows;
        using (var conn = new SqlConnection(connStr))
        {
            conn.Open();
            rows = MonthlyPush.CallProc(date, conn);
        }
        Log($"setHourly: the procedure returned {rows.Count} meter(s) in {sw.Elapsed.TotalSeconds:0} s");

        var items = new List<HItem>(); var skips = new List<HSkip>();
        void Skip(string no, string? unique, string why) => skips.Add(new HSkip(no, unique, why));
        var hourStart = date.Date.AddHours(hour);

        foreach (var (no, row) in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            if (!services.TryGetValue(no, out var svc)) { Skip(no, null, "service not found in ServiceDetails (or not active)"); continue; }
            if (!Regex.IsMatch(svc.Unique ?? "", @"^\d{9}$")) { Skip(no, svc.Unique, "no valid 9-digit UniqueServiceNo in ServiceDetails"); continue; }

            var stamp = row.GetValueOrDefault("Date & Time");
            if (!DateTime.TryParseExact(stamp?.Trim(), "yyyy-MM-dd HH:mm", Inv, DateTimeStyles.None, out var at)) { Skip(no, svc.Unique, "no Date & Time in the reading"); continue; }
            if (at != hourStart) { Skip(no, svc.Unique, $"latest reading is at {at:yyyy-MM-dd HH:mm}, not exactly {hour:00}:00"); continue; }

            var values = BuildValues(date, hour, row, svc);
            var missing = Needed.Where(n => string.IsNullOrWhiteSpace(values[n])).ToList();
            if (missing.Count > 0) { Skip(no, svc.Unique, "no value in the reading for " + string.Join(", ", missing)); continue; }

            var xml = Soap.Envelope(Op, Fields.Select(f => (f.Name, values[f.Name])));
            items.Add(new HItem(no, svc.Unique!, values, xml, done.Contains(no), stamp!.Trim()));
        }
        foreach (var no in services.Keys.Where(k => !rows.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            Skip(no, services[no].Unique, $"no reading for {date:yyyy-MM-dd}");

        var toSend = items.Where(i => ignoreLog || !i.Done).ToList();
        if (limit > 0 && toSend.Count > limit) toSend = toSend.Take(limit).ToList();
        Log($"setHourly: {items.Count} meter(s) have their latest reading exactly at {hour:00}:00 - to send {toSend.Count}, already sent {items.Count(i => i.Done)}, skipped {skips.Count}");
        return new HPlan { Date = date, Hour = hour, Endpoint = endpoint, Limit = limit, IgnoreLog = ignoreLog, LogReady = logReady, ProcRows = rows.Count, Items = items, Skips = skips, ToSend = toSend };
    }

    Dictionary<string, string?> BuildValues(DateTime date, int hour, MonthlyPush.Reading row, MonthlyPush.Svc svc)
    {
        var v = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, source) in Fields)
        {
            v[name] = source switch
            {
                "date" => date.ToString("dd/MM/yy", Inv),
                "time" => $"{hour:00}:00:00",
                "svc:unique" => svc.Unique,
                "svc:compact" => Regex.Replace(svc.ServiceNo, @"\s+", ""),
                _ when source.StartsWith("proc:") => Number(name, row.GetValueOrDefault(source[5..])),
                _ when source.StartsWith("setting:") => global.GetValueOrDefault(source[8..]),
                _ => source
            };
        }
        return v;
    }

    // numbers are sent without trailing zeros (8932.130 -> 8932.13); meter number is text and goes as it is
    static string? Number(string field, string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (field == "meterno") return s;
        return decimal.TryParse(s, NumberStyles.Any, Inv, out var d) ? Math.Round(d, 3).ToString("0.###", Inv) : s;
    }

    // ------------------------------------------------------------ send

    internal MonthlyPush.ExecResult Execute(HPlan plan, Action<MonthlyPush.ExecProgress>? onProgress = null, CancellationToken ct = default)
    {
        if (!LogTableExists()) throw new InvalidOperationException("dbo.PushLog_Hourly does not exist - run sql\\05_create_PushLog_Hourly.sql first.");
        Log($"setHourly: pushing {plan.ToSend.Count} record(s) for {plan.Label} to {plan.Endpoint}");

        var responses = new Dictionary<string, int>();
        int pushed = 0, failed = 0, consecutiveFails = 0, index = 0;
        string? stoppedBecause = null;
        foreach (var it in plan.ToSend)
        {
            if (ct.IsCancellationRequested) { stoppedBecause = "stopped by the user"; Log("setHourly: stopped by the user"); break; }
            var (ok, detail) = Soap.Post(http, plan.Endpoint, Op, it.Xml);
            WriteLog(plan.Date, plan.Hour, it, ok ? "OK" : "FAILED", detail);
            index++;
            string line;
            if (ok)
            {
                pushed++; consecutiveFails = 0;
                responses[detail] = responses.GetValueOrDefault(detail) + 1;
                line = $"{it.ServiceNo} ({it.Unique}) -> {detail}";
            }
            else
            {
                failed++; consecutiveFails++;
                line = $"{it.ServiceNo} ({it.Unique}) FAILED - {detail}";
            }
            Log($"setHourly: {line}");
            onProgress?.Invoke(new MonthlyPush.ExecProgress(index, plan.ToSend.Count, pushed, failed, it.ServiceNo, ok, detail));
            if (consecutiveFails >= 5) { stoppedBecause = "5 failures in a row"; Log("setHourly: 5 failures in a row - stopping, rerun to retry"); break; }
        }
        foreach (var (msg, n) in responses.OrderByDescending(r => r.Value))
            Log($"setHourly: server said '{msg}' for {n} meter(s)");
        Log($"setHourly: done - accepted {pushed}, failed {failed}");
        return new MonthlyPush.ExecResult(pushed, failed, plan.Skips.Count, responses, stoppedBecause);
    }

    // ------------------------------------------------------------ database

    bool LogTableExists()
    {
        using var cmd = new SqlCommand("SELECT OBJECT_ID('dbo.PushLog_Hourly')", db);
        return cmd.ExecuteScalar() is not (null or DBNull);
    }

    Dictionary<string, MonthlyPush.Svc> LoadServices()
    {
        var d = new Dictionary<string, MonthlyPush.Svc>(StringComparer.Ordinal);
        using var cmd = new SqlCommand("SELECT ServiceNo, UniqueServiceNo, ContractedLoad FROM dbo.ServiceDetails WHERE IsActive=1", db);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var no = r.GetString(0).Trim();
            var s = new MonthlyPush.Svc(no, r.IsDBNull(1) ? null : r.GetString(1).Trim(), r.IsDBNull(2) ? null : r.GetDecimal(2));
            // same rule as setMonthly: if a ServiceNo appears twice keep the row that has a usable unique number
            if (!d.TryGetValue(no, out var existing) || (!Regex.IsMatch(existing.Unique ?? "", @"^\d{9}$") && Regex.IsMatch(s.Unique ?? "", @"^\d{9}$")))
                d[no] = s;
        }
        return d;
    }

    HashSet<string> LoadDone(DateTime date, int hour)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = new SqlCommand("SELECT ServiceNo FROM dbo.PushLog_Hourly WHERE ReadingDate=@d AND ReadingHour=@h AND Status='OK'", db);
        cmd.Parameters.AddWithValue("@d", date.Date);
        cmd.Parameters.AddWithValue("@h", (byte)hour);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0).Trim());
        return set;
    }

    void WriteLog(DateTime date, int hour, HItem it, string status, string detail)
    {
        using var cmd = new SqlCommand(@"
UPDATE dbo.PushLog_Hourly SET UniqueServiceNo=@u, MeterNo=@m, ReadAt=@ra, Status=@s, Detail=@t, RequestXml=@x, PushedOn=GETDATE() WHERE ReadingDate=@d AND ReadingHour=@h AND ServiceNo=@n;
IF @@ROWCOUNT=0 INSERT dbo.PushLog_Hourly (ReadingDate, ReadingHour, ServiceNo, UniqueServiceNo, MeterNo, ReadAt, Status, Detail, RequestXml) VALUES (@d,@h,@n,@u,@m,@ra,@s,@t,@x);", db);
        cmd.Parameters.AddWithValue("@d", date.Date);
        cmd.Parameters.AddWithValue("@h", (byte)hour);
        cmd.Parameters.AddWithValue("@n", it.ServiceNo);
        cmd.Parameters.AddWithValue("@u", it.Unique);
        cmd.Parameters.AddWithValue("@m", (object?)it.Values.GetValueOrDefault("meterno") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ra", it.ReadAt);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@t", detail.Length > 500 ? detail[..500] : detail);
        cmd.Parameters.AddWithValue("@x", MonthlyPush.MaskPassword(it.Xml));
        cmd.ExecuteNonQuery();
    }

    void Log(string m) => logSink(m);
}
