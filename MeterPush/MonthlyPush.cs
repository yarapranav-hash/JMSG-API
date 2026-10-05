using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

// setMonthly: mapping comes from dbo.PushConfig_Monthly, progress is kept in dbo.PushLog_Monthly.
// Data: dbo.PrBLSTotalDCWiseMeterDataInstantDay for the push date (NEW) and push date - 1 month (OLD),
// joined on Service No to dbo.ServiceDetails.
class MonthlyPush
{
    const string Op = "setMonthly";
    const string Proc = "dbo.PrBLSTotalDCWiseMeterDataInstantDay";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    record Param(string Name, int Sort, string Type, string? Value, int? OffsetMonths, string? Format, bool Quote);
    record Svc(string ServiceNo, string? Unique, decimal? ContractedLoad);
    class Reading : Dictionary<string, string?> { public Reading() : base(StringComparer.OrdinalIgnoreCase) { } }

    readonly SqlConnection db;
    readonly HttpClient http;
    readonly Dictionary<string, string> global;
    readonly Opts opts;

    readonly string connStr; // the parallel procedure calls open their own connections

    public MonthlyPush(SqlConnection db, string connStr, HttpClient http, Dictionary<string, string> global, Opts opts)
    {
        this.db = db; this.connStr = connStr; this.http = http; this.global = global; this.opts = opts;
    }

    public bool Run()
    {
        var pushDate = opts.Date ?? DateTime.Today;
        var oldDate = pushDate.AddMonths(-1);
        var endpoint = global["ENDPOINT"];
        var limit = opts.Limit > 0 ? opts.Limit : int.MaxValue;

        var prms = LoadParams();
        if (prms.Count == 0) { Log("setMonthly: PushConfig_Monthly is empty - skipped"); return true; }

        Log($"setMonthly: push date {pushDate:yyyy-MM-dd}, opening readings from {oldDate:yyyy-MM-dd}");

        // every procedure call we may need, run in parallel (each on its own connection):
        //  - push date (closing) and opening date, plus the following OPENING_FORWARD_DAYS days for services with no opening reading
        //  - PROC_PREVEND params (rmd_kva, rmd_kwh): last day of the month before the push date, plus up to MD_LOOKBACK_DAYS earlier days
        var forward = int.Parse(global.GetValueOrDefault("OPENING_FORWARD_DAYS", "1"));
        var lookback = int.Parse(global.GetValueOrDefault("MD_LOOKBACK_DAYS", "7"));
        var monthStart = pushDate.AddDays(-pushDate.Day).AddDays(1).AddMonths(-1); // first day of the previous month
        var usesMd = prms.Any(p => p.Type == "PROC_PREVEND");
        var forwardDates = Enumerable.Range(1, forward).Select(i => oldDate.AddDays(i)).ToList();
        var mdDates = usesMd
            ? Enumerable.Range(0, lookback + 1).Select(i => pushDate.AddDays(-pushDate.Day - i)).Where(d => d >= monthStart).ToList()
            : new List<DateTime>();
        var results = CallMany(new[] { pushDate, oldDate }.Concat(forwardDates).Concat(mdDates).Distinct().ToList());

        var newR = results[pushDate];
        var oldR = new Dictionary<string, Reading>(results[oldDate], StringComparer.Ordinal);
        Log($"setMonthly: procedure returned {newR.Count} service(s) for {pushDate:yyyy-MM-dd}, {oldR.Count} for {oldDate:yyyy-MM-dd}");

        // opening reading: a service with no reading on the opening date takes the latest reading of the next day that has one
        foreach (var d in forwardDates)
        {
            var added = 0;
            foreach (var (no, r) in results[d])
                if (newR.ContainsKey(no) && oldR.TryAdd(no, r)) added++;
            Log($"setMonthly: {d:yyyy-MM-dd} (opening, forward) gave an opening reading for {added} more service(s)");
        }
        Log($"setMonthly: {newR.Keys.Count(k => !oldR.ContainsKey(k))} service(s) without an opening reading");

        // rmd_kva / rmd_kwh: a service with no reading on the last day of the previous month takes the latest reading of the previous day that has one
        var prevEndR = new Dictionary<string, Reading>(StringComparer.Ordinal);
        foreach (var d in mdDates)
        {
            var added = 0;
            foreach (var (no, r) in results[d])
                if (newR.ContainsKey(no) && prevEndR.TryAdd(no, r)) added++;
            Log($"setMonthly: {d:yyyy-MM-dd} (MD source) gave a reading for {added} service(s)");
        }
        if (usesMd) Log($"setMonthly: {newR.Count - prevEndR.Count} service(s) without an MD reading");

        var services = LoadServices();
        var done = LoadDone(pushDate);

        var maxIncreasePct = decimal.Parse(global.GetValueOrDefault("MONTHLY_MAX_INCREASE_PCT", "120"), Inv);
        var maxMd = decimal.Parse(global.GetValueOrDefault("MONTHLY_MAX_MD", "6"), Inv);

        // ---- phase 1: build everything that would be sent (nothing is sent or written to the log yet)
        prmList = prms;
        dataCols = LoadDataColumns();
        if (dataCols == null) Log("setMonthly: dbo.PushData_Monthly not found - sent values will not be stored (run sql\\04_create_PushData_Monthly.sql)");
        var items = new List<Item>();
        var skips = new List<Skipped>();
        foreach (var (serviceNo, cur) in newR.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var isDone = done.Contains(serviceNo);
            Dictionary<string, string?>? vals = null;
            var info = new Info(null, null, null);
            void Skip(string? u, string why) => skips.Add(new Skipped(serviceNo, u, why, isDone, vals, info));

            // ukscno is ServiceDetails.UniqueServiceNo (9 digits); the service must exist in ServiceDetails
            if (!services.TryGetValue(serviceNo, out var svc)) { Skip(null, "service not found (active) in ServiceDetails"); continue; }
            if (!Regex.IsMatch(svc.Unique ?? "", @"^\d{9}$")) { Skip(svc.Unique, "no valid 9-digit UniqueServiceNo in ServiceDetails"); continue; }
            var unique = svc.Unique!;

            // opening reading: only used for the sanity checks below, it is NOT sent
            if (!oldR.TryGetValue(serviceNo, out var old)) { Skip(unique, forward > 0 ? $"no reading for {oldDate:yyyy-MM-dd} or the next {forward} day(s) (opening)" : $"no reading for {oldDate:yyyy-MM-dd} (opening)"); continue; }

            var prevEnd = prevEndR.GetValueOrDefault(serviceNo);
            info = new Info(null, old.GetValueOrDefault(ReadDate), prevEnd?.GetValueOrDefault(ReadDate));

            Dictionary<string, string?> values;
            try { values = BuildValues(prms, pushDate, cur, old, prevEnd, svc); }
            catch (Exception ex) { Skip(unique, ex.Message); continue; }
            vals = values;

            // no rmd_kva / rmd_kwh on the last day of the previous month or the allowed days before it: do not send the service
            var noMd = prms.Where(p => p.Type == "PROC_PREVEND" && string.IsNullOrEmpty(values[p.Name])).Select(p => p.Name).ToList();
            if (noMd.Count > 0)
            {
                Skip(unique, $"no {string.Join("/", noMd)} reading on {mdDates.FirstOrDefault():yyyy-MM-dd}" + (lookback > 0 ? $" or the {lookback} day(s) before" : ""));
                continue;
            }

            if (!decimal.TryParse(old.GetValueOrDefault(KwhColumn), NumberStyles.Any, Inv, out var openKwh)
                || !decimal.TryParse(cur.GetValueOrDefault(KwhColumn), NumberStyles.Any, Inv, out var closeKwh))
            { Skip(unique, "opening or closing kWh reading missing"); continue; }
            info = info with { OpeningKwh = openKwh };

            var diff = closeKwh - openKwh;
            if (diff < 0) { Skip(unique, $"closing < opening (consumed {diff:0.##}) - meter replaced/rolled over?"); continue; }

            // hold back services whose kWh jumped by more than maxIncreasePct between opening and closing
            // opening 0: any positive closing is an unbounded increase
            if (openKwh > 0 ? diff / openKwh * 100m > maxIncreasePct : diff > 0)
            {
                var pctText = openKwh > 0 ? $"{diff / openKwh * 100m:0.##}%" : "n/a (opening 0)";
                Skip(unique, $"increase {pctText} > {maxIncreasePct}% (opening {openKwh:0.##}, closing {closeKwh:0.##})");
                continue;
            }

            // maximum demand above the limit (also catches glitch readings with absurd MD values)
            var tooHigh = new[] { "rmd_kva", "rmd_kwh" }
                .Where(n => values.TryGetValue(n, out var s) && decimal.TryParse(s, NumberStyles.Any, Inv, out var md) && md > maxMd)
                .Select(n => $"{n} {values[n]}").ToList();
            if (tooHigh.Count > 0) { Skip(unique, $"{string.Join(", ", tooHigh)} > {maxMd}"); continue; }

            items.Add(new Item(serviceNo, unique, values,
                Soap.Envelope(Op, prms.Select(p => (p.Name, Quoted(p, values[p.Name])))), isDone, info));
        }

        var toSend = items.Where(i => !i.Done).Take(limit).ToList();

        // ---- preview document: exactly what would go to the API (password hidden)
        var previewFile = opts.Csv ?? Path.Combine(Environment.CurrentDirectory, "preview", $"monthly_{pushDate:yyyy-MM-dd}.csv");
        WritePreview(previewFile, prms, toSend, skips);
        var skippedFile = Path.ChangeExtension(previewFile, null) + "_skipped.csv";
        var newSkips = skips.Count(s => !s.Done);
        Log($"setMonthly: preview written to {previewFile} ({toSend.Count} rows) and {skippedFile} ({newSkips} rows)");
        Log($"setMonthly: to push {toSend.Count}, already sent OK {items.Count(i => i.Done)}, skipped {newSkips}");

        if (opts.DryRun)
        {
            if (toSend.Count > 0) { Console.WriteLine($"--- sample request: {Op} {toSend[0].ServiceNo} ({toSend[0].Unique}) ---"); Console.WriteLine(MaskPassword(toSend[0].Xml)); }
            Log("setMonthly: preview only - nothing sent, nothing written to the log");
            return true;
        }
        if (toSend.Count == 0) { Log("setMonthly: nothing to push"); return true; }

        // ---- phase 2: push (the preview files above are written first and kept; use --dry-run / --csv to look without sending)
        Log($"setMonthly: pushing {toSend.Count} record(s) to {endpoint}");

        foreach (var s in skips.Where(s => !s.Done))
        {
            WriteLog(pushDate, s.ServiceNo, s.Unique, "SKIPPED", s.Why);
            WriteData(pushDate, s.ServiceNo, s.Unique, "SKIPPED", s.Why, s.Values, null, s.Info);
        }

        var responses = new Dictionary<string, int>(); // server message -> count
        int pushed = 0, failed = 0, consecutiveFails = 0;
        foreach (var it in toSend)
        {
            var (ok, detail) = Soap.Post(http, endpoint, Op, it.Xml);
            WriteLog(pushDate, it.ServiceNo, it.Unique, ok ? "OK" : "FAILED", detail);
            WriteData(pushDate, it.ServiceNo, it.Unique, ok ? "OK" : "FAILED", detail, it.Values, MaskPassword(it.Xml), it.Info);
            if (ok)
            {
                pushed++; consecutiveFails = 0;
                responses[detail] = responses.GetValueOrDefault(detail) + 1;
                Log($"setMonthly: {it.ServiceNo} ({it.Unique}) -> {detail}");
            }
            else
            {
                failed++; consecutiveFails++;
                Log($"setMonthly: {it.ServiceNo} ({it.Unique}) FAILED - {detail}");
                if (consecutiveFails >= 5) { Log("setMonthly: 5 failures in a row - stopping, rerun to retry"); break; }
            }
        }

        foreach (var (msg, n) in responses.OrderByDescending(r => r.Value))
            Log($"setMonthly: server said '{msg}' for {n} service(s)");
        Log($"setMonthly: done - accepted {pushed}, skipped {newSkips}, failed {failed}");
        return failed == 0;
    }

    // readings used by the checks (not sent): opening kWh, the date the opening reading and the MD reading came from
    record Info(decimal? OpeningKwh, string? OpeningReadDate, string? MdReadDate);
    record Item(string ServiceNo, string Unique, Dictionary<string, string?> Values, string Xml, bool Done, Info Info);
    record Skipped(string ServiceNo, string? Unique, string Why, bool Done, Dictionary<string, string?>? Values, Info Info);

    const string ReadDate = "_ReadDate"; // added to every procedure row: the date it was requested for
    List<Param> prmList = new();
    HashSet<string>? dataCols; // columns of dbo.PushData_Monthly, null if the table does not exist

    HashSet<string>? LoadDataColumns()
    {
        using var cmd = new SqlCommand("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PushData_Monthly')", db);
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) set.Add(r.GetString(0));
        return set.Count == 0 ? null : set;
    }

    // One row per (push date, service): every value as sent (or as it was built, for skipped services), the reply and the request XML.
    void WriteData(DateTime pushDate, string serviceNo, string? unique, string outcome, string reply,
        Dictionary<string, string?>? values, string? xml, Info info)
    {
        if (dataCols == null) return;
        var cols = prmList.Where(p => !p.Name.Equals("password", StringComparison.OrdinalIgnoreCase) && dataCols.Contains(p.Name)).ToList();

        using var cmd = new SqlCommand { Connection = db };
        cmd.Parameters.AddWithValue("@d", pushDate.Date);
        cmd.Parameters.AddWithValue("@n", serviceNo);
        cmd.Parameters.AddWithValue("@u", (object?)unique ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@o", outcome);
        cmd.Parameters.AddWithValue("@r", reply.Length > 500 ? reply[..500] : reply);
        cmd.Parameters.AddWithValue("@ok", (object?)info.OpeningKwh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@od", info.OpeningReadDate != null ? DateTime.Parse(info.OpeningReadDate, Inv) : DBNull.Value);
        cmd.Parameters.AddWithValue("@md", info.MdReadDate != null ? DateTime.Parse(info.MdReadDate, Inv) : DBNull.Value);
        cmd.Parameters.AddWithValue("@x", (object?)xml ?? DBNull.Value);

        var sets = new List<string>(); var names = new List<string>(); var vars = new List<string>();
        for (int i = 0; i < cols.Count; i++)
        {
            var p = cols[i];
            var v = values != null && values.TryGetValue(p.Name, out var s) ? Quoted(p, s) : null;
            cmd.Parameters.AddWithValue("@c" + i, (object?)v ?? DBNull.Value);
            sets.Add($"[{p.Name}]=@c{i}"); names.Add($"[{p.Name}]"); vars.Add("@c" + i);
        }
        var fixedCols = "UniqueServiceNo, Outcome, Reply, OpeningKwh, OpeningReadDate, MdReadDate, RequestXml";
        cmd.CommandText = $@"
UPDATE dbo.PushData_Monthly SET UniqueServiceNo=@u, Outcome=@o, Reply=@r, OpeningKwh=@ok, OpeningReadDate=@od, MdReadDate=@md, RequestXml=@x, PushedOn=GETDATE(){(sets.Count > 0 ? ", " + string.Join(", ", sets) : "")}
WHERE PushDate=@d AND ServiceNo=@n;
IF @@ROWCOUNT=0 INSERT dbo.PushData_Monthly (PushDate, ServiceNo, {fixedCols}{(names.Count > 0 ? ", " + string.Join(", ", names) : "")})
VALUES (@d, @n, @u, @o, @r, @ok, @od, @md, @x{(vars.Count > 0 ? ", " + string.Join(", ", vars) : "")});";
        cmd.ExecuteNonQuery();
    }

    // procedure column used for the opening/closing sanity checks (these readings are not part of the request)
    const string KwhColumn = "Meter Reading(kWh)";

    static string MaskPassword(string xml) => Regex.Replace(xml, @"(<ser:password>).*?(</ser:password>)", "$1********$2");

    // The preview holds only the records that will be pushed, with exactly the request parameters as columns.
    // Services already sent OK are left out of both files.
    void WritePreview(string file, List<Param> prms, List<Item> toSend,
        List<Skipped> skips)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var rows = new List<string> { string.Join(",", prms.Select(p => Csv(p.Name))) };
        foreach (var it in toSend)
            // values exactly as sent: Quote=1 parameters include their single quotes; empty = element left out
            rows.Add(string.Join(",", prms.Select(p =>
                Csv(p.Name.Equals("password", StringComparison.OrdinalIgnoreCase) ? "********" : Quoted(p, it.Values[p.Name]) ?? ""))));
        File.WriteAllLines(file, rows);
        File.WriteAllLines(Path.ChangeExtension(file, null) + "_skipped.csv",
            new[] { "service_no,unique_service_no,reason" }.Concat(
                skips.Where(s => !s.Done).Select(s => string.Join(",", new[] { s.ServiceNo, s.Unique ?? "", s.Why }.Select(Csv)))));
    }

    // ------------------------------------------------------------ values

    Dictionary<string, string?> BuildValues(List<Param> prms, DateTime pushDate, Reading cur, Reading old, Reading? prevEnd, Svc svc)
    {
        var v = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in prms.Where(p => p.Type != "CALC"))
        {
            v[p.Name] = p.Type switch
            {
                "PROC_NEW" => FromProc(p, cur),
                "PROC_OLD" => FromProc(p, old),
                "PROC_PREVEND" => prevEnd == null ? null : FromProc(p, prevEnd), // no reading that day -> element left out
                "SERVICE" => FromService(p, svc),
                "PUSHDATE" => pushDate.AddMonths(p.OffsetMonths ?? 0).ToString(p.Format ?? "dd/MM/yyyy", Inv),
                "CONST" => Expand(p.Value),
                _ => throw new InvalidOperationException($"{p.Name}: unknown SourceType {p.Type}")
            };
        }

        // the opening kWh reading is not a request parameter, but CALC rows can use it (e.g. consumed_units = closing_kwh - opening_kwh_reading)
        v["opening_kwh_reading"] = old.GetValueOrDefault(KwhColumn);

        foreach (var p in prms.Where(p => p.Type == "CALC"))
        {
            var m = Regex.Match(p.Value ?? "", @"^\s*(\w+)\s*-\s*(\w+)\s*$");
            if (!m.Success) throw new InvalidOperationException($"{p.Name}: CALC must look like 'a-b'");
            decimal a = Num(v, m.Groups[1].Value, p.Name), b = Num(v, m.Groups[2].Value, p.Name);
            v[p.Name] = Math.Round(a - b, 2).ToString("0.##", Inv);
        }
        return v;
    }

    // Quote=1 params are sent in single quotes ('A08767'); applied last so calculations and checks see the plain value.
    static string? Quoted(Param p, string? value) =>
        p.Quote && !string.IsNullOrEmpty(value) ? "'" + value + "'" : value;

    static decimal Num(Dictionary<string, string?> v, string key, string forParam) =>
        v.TryGetValue(key, out var s) && decimal.TryParse(s, NumberStyles.Any, Inv, out var d)
            ? d : throw new InvalidOperationException($"{forParam}: '{key}' has no numeric value");

    static string? FromProc(Param p, Reading r)
    {
        if (!r.TryGetValue(p.Value ?? "", out var s))
            throw new InvalidOperationException($"{p.Name}: column '{p.Value}' is not in the procedure result");
        if (s == null) return null;
        if (p.Format != null && DateTime.TryParse(s, Inv, DateTimeStyles.None, out var dt))
            return dt.ToString(p.Format, Inv);
        return s.Trim();
    }

    static string? FromService(Param p, Svc svc) => p.Value?.ToLowerInvariant() switch
    {
        "uniqueserviceno" => svc.Unique,
        "servicenocompact" => Regex.Replace(svc.ServiceNo, @"\s+", ""),
        "contractedload" => svc.ContractedLoad?.ToString("0.##", Inv),
        _ => throw new InvalidOperationException($"{p.Name}: unsupported ServiceDetails column '{p.Value}' (add it to MonthlyPush.FromService)")
    };

    string? Expand(string? v)
    {
        if (v == null) return null;
        foreach (var (k, val) in global) v = v.Replace("{" + k + "}", val);
        return v;
    }

    // ------------------------------------------------------------ database

    List<Param> LoadParams()
    {
        var list = new List<Param>();
        using var cmd = new SqlCommand(
            "SELECT ParamName, SortOrder, SourceType, SourceValue, DateOffsetMonths, Format, Quote FROM dbo.PushConfig_Monthly WHERE IsActive=1 ORDER BY SortOrder", db);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Param(r.GetString(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6)));
        return list;
    }

    // Calls the procedure for several dates at the same time, each call on its own connection (PROC_PARALLELISM at a time).
    Dictionary<DateTime, Dictionary<string, Reading>> CallMany(List<DateTime> dates)
    {
        var results = new ConcurrentDictionary<DateTime, Dictionary<string, Reading>>();
        var parallelism = Math.Max(1, int.Parse(global.GetValueOrDefault("PROC_PARALLELISM", "5")));
        Log($"setMonthly: calling the procedure for {dates.Count} date(s), {parallelism} at a time: {string.Join(", ", dates.Select(d => d.ToString("MM-dd")))}");
        var total = Stopwatch.StartNew();
        Parallel.ForEach(dates, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, d =>
        {
            var sw = Stopwatch.StartNew();
            Log($"setMonthly: {d:yyyy-MM-dd} - procedure call started");
            using var conn = new SqlConnection(connStr);
            conn.Open();
            var r = CallProc(d, conn);
            results[d] = r;
            Log($"setMonthly: {d:yyyy-MM-dd} - procedure returned {r.Count} service(s) in {sw.Elapsed.TotalSeconds:0} s");
        });
        Log($"setMonthly: all procedure calls done in {total.Elapsed.TotalSeconds:0} s");
        return results.ToDictionary(k => k.Key, k => k.Value);
    }

    // One row per Service No (the procedure already keeps only the latest reading per service).
    static Dictionary<string, Reading> CallProc(DateTime date, SqlConnection conn)
    {
        var result = new Dictionary<string, Reading>(StringComparer.Ordinal);
        using var cmd = new SqlCommand(Proc, conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 1200 };
        cmd.Parameters.AddWithValue("@Date", date.ToString("yyyy-MM-dd", Inv));
        cmd.Parameters.AddWithValue("@DC", DBNull.Value);
        cmd.Parameters.AddWithValue("@stmt", "ALL");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new Reading { [ReadDate] = date.ToString("yyyy-MM-dd", Inv) };
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), Inv);
            var no = row.GetValueOrDefault("Service No")?.Trim();
            if (!string.IsNullOrEmpty(no)) result.TryAdd(no, row);
        }
        return result;
    }

    Dictionary<string, Svc> LoadServices()
    {
        var d = new Dictionary<string, Svc>(StringComparer.Ordinal);
        using var cmd = new SqlCommand(
            "SELECT ServiceNo, UniqueServiceNo, ContractedLoad FROM dbo.ServiceDetails WHERE IsActive=1", db);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var no = r.GetString(0).Trim();
            var s = new Svc(no, r.IsDBNull(1) ? null : r.GetString(1).Trim(), r.IsDBNull(2) ? null : r.GetDecimal(2));
            // if a ServiceNo appears twice keep the row that has a usable unique number
            if (!d.TryGetValue(no, out var existing) || (!Regex.IsMatch(existing.Unique ?? "", @"^\d{9}$") && Regex.IsMatch(s.Unique ?? "", @"^\d{9}$")))
                d[no] = s;
        }
        return d;
    }

    HashSet<string> LoadDone(DateTime pushDate)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = new SqlCommand("SELECT ServiceNo FROM dbo.PushLog_Monthly WHERE PushDate=@d AND Status='OK'", db);
        cmd.Parameters.AddWithValue("@d", pushDate.Date);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0).Trim());
        return set;
    }

    void WriteLog(DateTime pushDate, string serviceNo, string? unique, string status, string detail)
    {
        using var cmd = new SqlCommand(@"
UPDATE dbo.PushLog_Monthly SET UniqueServiceNo=@u, Status=@s, Detail=@t, PushedOn=GETDATE() WHERE PushDate=@d AND ServiceNo=@n;
IF @@ROWCOUNT=0 INSERT dbo.PushLog_Monthly (PushDate, ServiceNo, UniqueServiceNo, Status, Detail) VALUES (@d,@n,@u,@s,@t);", db);
        cmd.Parameters.AddWithValue("@d", pushDate.Date);
        cmd.Parameters.AddWithValue("@n", serviceNo);
        cmd.Parameters.AddWithValue("@u", (object?)unique ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@t", detail.Length > 500 ? detail[..500] : detail);
        cmd.ExecuteNonQuery();
    }

    static string Csv(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    static void Log(string m) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}");
}
