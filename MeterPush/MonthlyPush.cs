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

    internal record Param(string Name, int Sort, string Type, string? Value, int? OffsetMonths, string? Format, bool Quote);
    internal record Svc(string ServiceNo, string? Unique, decimal? ContractedLoad);
    internal class Reading : Dictionary<string, string?> { public Reading() : base(StringComparer.OrdinalIgnoreCase) { } }

    readonly SqlConnection db;
    readonly HttpClient http;
    readonly Dictionary<string, string> global;
    readonly Opts opts;

    readonly string connStr; // the parallel procedure calls open their own connections
    readonly Action<string> logSink; // console by default; the web app collects the lines for the page

    public MonthlyPush(SqlConnection db, string connStr, HttpClient http, Dictionary<string, string> global, Opts opts, Action<string>? log = null)
    {
        this.db = db; this.connStr = connStr; this.http = http; this.global = global; this.opts = opts;
        logSink = log ?? (m => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}"));
    }

    // Command line: plan, write the preview files, then send (or stop after the preview with --dry-run / --csv).
    public bool Run()
    {
        if (opts.VerifyQuick > 0) return VerifyQuick(opts.VerifyQuick);
        var plan = Plan();
        if (plan.Prms.Count == 0) return true;

        var previewFile = opts.Csv ?? Path.Combine(Environment.CurrentDirectory, "preview", $"monthly_{plan.PushDate:yyyy-MM-dd}.csv");
        WritePreview(previewFile, plan.Prms, plan.ToSend, plan.Skips);
        var skippedFile = Path.ChangeExtension(previewFile, null) + "_skipped.csv";
        Log($"setMonthly: preview written to {previewFile} ({plan.ToSend.Count} rows) and {skippedFile} ({plan.NewSkips} rows)");
        Log($"setMonthly: to push {plan.ToSend.Count}, already sent OK {plan.Items.Count(i => i.Done)}{(opts.IgnoreLog ? " (sent again: --ignore-log)" : "")}, skipped {plan.NewSkips}");

        if (opts.DryRun)
        {
            if (plan.ToSend.Count > 0) { Console.WriteLine($"--- sample request: {Op} {plan.ToSend[0].ServiceNo} ({plan.ToSend[0].Unique}) ---"); Console.WriteLine(MaskPassword(plan.ToSend[0].Xml)); }
            Log("setMonthly: preview only - nothing sent, nothing written to the log");
            return true;
        }
        if (plan.ToSend.Count == 0) { Log("setMonthly: nothing to push"); return true; }

        // the preview files above are written first and kept; use --dry-run / --csv to look without sending
        var result = Execute(plan);
        return result.Failed == 0;
    }

    // ------------------------------------------------------------ plan: build everything that would be sent (nothing is sent or written)

    internal PlanResult Plan()
    {
        var pushDate = opts.Date ?? DateTime.Today;
        var oldDate = pushDate.AddMonths(-1);
        var limit = opts.Limit > 0 ? opts.Limit : int.MaxValue;

        var prms = LoadParams();
        if (prms.Count == 0)
        {
            Log("setMonthly: PushConfig_Monthly is empty - skipped");
            return new PlanResult(pushDate, global["ENDPOINT"], prms, new List<Item>(), new List<Skipped>(), new List<Item>());
        }

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
        // a test limit: read only the meters needed instead of every meter (see PlanQuick)
        if (opts.Limit > 0 && !opts.FullRead) return PlanQuick(prms, pushDate, oldDate, forward, lookback, forwardDates, mdDates);

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
        var ctx = MakeCtx(prms, pushDate, oldDate, forward, lookback, mdDates, services, done, oldR, prevEndR);

        // ---- phase 1: build everything that would be sent (nothing is sent or written to the log yet)
        prmList = prms;
        dataCols = LoadDataColumns();
        if (dataCols == null) Log("setMonthly: dbo.PushData_Monthly not found - sent values will not be stored (run sql\\04_create_PushData_Monthly.sql)");
        var items = new List<Item>();
        var skips = new List<Skipped>();
        Evaluate(newR.OrderBy(k => k.Key, StringComparer.Ordinal), ctx, items, skips);

        // --ignore-log: services already logged OK are sent again too
        var toSend = items.Where(i => opts.IgnoreLog || !i.Done).Take(limit).ToList();
        return new PlanResult(pushDate, global["ENDPOINT"], prms, items, skips, toSend);
    }

    // ------------------------------------------------------------ checks (shared by the full plan and the quick test)

    sealed record EvalCtx(List<Param> Prms, DateTime PushDate, DateTime OldDate, int Forward, int Lookback, List<DateTime> MdDates,
        Dictionary<string, Svc> Services, HashSet<string> Done, Dictionary<string, Reading> OldR, Dictionary<string, Reading> PrevEndR,
        decimal MaxIncreasePct, decimal MaxMd);

    EvalCtx MakeCtx(List<Param> prms, DateTime pushDate, DateTime oldDate, int forward, int lookback, List<DateTime> mdDates,
        Dictionary<string, Svc> services, HashSet<string> done, Dictionary<string, Reading> oldR, Dictionary<string, Reading> prevEndR) =>
        new(prms, pushDate, oldDate, forward, lookback, mdDates, services, done, oldR, prevEndR,
            decimal.Parse(global.GetValueOrDefault("MONTHLY_MAX_INCREASE_PCT", "120"), Inv),
            decimal.Parse(global.GetValueOrDefault("MONTHLY_MAX_MD", "6"), Inv));

    // For each service that has a reading on the push date: apply every rule, then add it to items (will be sent) or skips (with the reason).
    void Evaluate(IEnumerable<KeyValuePair<string, Reading>> rows, EvalCtx c, List<Item> items, List<Skipped> skips)
    {
        var (prms, pushDate, oldDate, forward, lookback, mdDates) = (c.Prms, c.PushDate, c.OldDate, c.Forward, c.Lookback, c.MdDates);
        foreach (var (serviceNo, cur) in rows)
        {
            var isDone = c.Done.Contains(serviceNo);
            Dictionary<string, string?>? vals = null;
            var info = new Info(null, null, null);
            void Skip(string? u, string why) => skips.Add(new Skipped(serviceNo, u, why, isDone, vals, info));

            // ukscno is ServiceDetails.UniqueServiceNo (9 digits); the service must exist in ServiceDetails
            if (!c.Services.TryGetValue(serviceNo, out var svc)) { Skip(null, "service not found (active) in ServiceDetails"); continue; }
            if (!Regex.IsMatch(svc.Unique ?? "", @"^\d{9}$")) { Skip(svc.Unique, "no valid 9-digit UniqueServiceNo in ServiceDetails"); continue; }
            var unique = svc.Unique!;

            // opening reading: only used for the sanity checks below, it is NOT sent
            if (!c.OldR.TryGetValue(serviceNo, out var old)) { Skip(unique, forward > 0 ? $"no reading for {oldDate:yyyy-MM-dd} or the next {forward} day(s) (opening)" : $"no reading for {oldDate:yyyy-MM-dd} (opening)"); continue; }

            var prevEnd = c.PrevEndR.GetValueOrDefault(serviceNo);
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
            if (openKwh > 0 ? diff / openKwh * 100m > c.MaxIncreasePct : diff > 0)
            {
                var pctText = openKwh > 0 ? $"{diff / openKwh * 100m:0.##}%" : "n/a (opening 0)";
                Skip(unique, $"increase {pctText} > {c.MaxIncreasePct}% (opening {openKwh:0.##}, closing {closeKwh:0.##})");
                continue;
            }

            // maximum demand above the limit (also catches glitch readings with absurd MD values)
            var tooHigh = new[] { "rmd_kva", "rmd_kwh" }
                .Where(n => values.TryGetValue(n, out var s) && decimal.TryParse(s, NumberStyles.Any, Inv, out var md) && md > c.MaxMd)
                .Select(n => $"{n} {values[n]}").ToList();
            if (tooHigh.Count > 0) { Skip(unique, $"{string.Join(", ", tooHigh)} > {c.MaxMd}"); continue; }

            items.Add(new Item(serviceNo, unique, values,
                Soap.Envelope(Op, prms.Select(p => (p.Name, Quoted(p, values[p.Name])))), isDone, info));
        }
    }

    // ------------------------------------------------------------ records supplied in a file (web upload)
    // One row per meter, the column headers are the setMonthly parameter names. The values in the file are sent as they are
    // (nothing is read from the meter tables) after format checks only. A blank cell, or a column that is not in the file, takes the
    // standard value from PushConfig_Monthly (CONST / PUSHDATE rows); the columns that carry data (readings, MD, ukscno, meter_no, ...) are required.

    static readonly string[] CredentialParams = { "username", "password" };
    static readonly string[] UploadMayBeEmpty = { "rmd_kva", "closing_kvah" }; // data columns that an uploaded row may leave empty

    // the columns a file must have (data columns) and the ones that fall back to the standard value
    internal static (List<string> Required, List<(string Name, string Default)> Optional) UploadColumns(List<Param> prms, Func<string?, string?> expand)
    {
        var required = new List<string>(); var optional = new List<(string, string)>();
        foreach (var p in prms)
        {
            if (CredentialParams.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (UploadMayBeEmpty.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) optional.Add((p.Name, "")); // may be left empty: the element is then not sent
            else if (p.Type is "CONST" or "PUSHDATE") optional.Add((p.Name, p.Type == "PUSHDATE" ? (p.OffsetMonths is null or 0 ? "push date" : $"push date {p.OffsetMonths:+0;-0} month") : expand(p.Value) ?? ""));
            else required.Add(p.Name);
        }
        return (required, optional);
    }

    // for the upload screen: every column name (template), the required ones, and the standard values used for blank cells
    internal (List<string> All, List<string> Required, List<(string Name, string Default)> Optional) UploadInfo()
    {
        var prms = LoadParams();
        var (required, optional) = UploadColumns(prms, Expand);
        return (prms.Where(p => !CredentialParams.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Select(p => p.Name).ToList(), required, optional);
    }

    internal PlanResult PlanFromRows(DateTime pushDate, List<Dictionary<string, string>> rows, string fileName)
    {
        var prms = LoadParams();
        if (prms.Count == 0) throw new InvalidOperationException("PushConfig_Monthly is empty.");
        var (required, _) = UploadColumns(prms, Expand);
        var columns = rows.SelectMany(r => r.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingCols = required.Where(n => !columns.Contains(n)).ToList();
        if (missingCols.Count > 0) throw new InvalidOperationException("The file has no column for: " + string.Join(", ", missingCols) + ". The first row of the file must hold the column names.");
        var known = prms.Select(p => p.Name).Append("service_no").Append("_row").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = columns.Where(c => !known.Contains(c)).ToList();
        if (unknown.Count > 0) Log("upload: columns not used (not setMonthly parameters): " + string.Join(", ", unknown));

        var services = LoadServices();
        var byCompact = new Dictionary<string, string>(StringComparer.Ordinal); // scno (no spaces) -> Service No as stored, so "already sent" matches the normal runs
        foreach (var s in services.Values) byCompact.TryAdd(Regex.Replace(s.ServiceNo, @"\s+", ""), s.ServiceNo);
        var done = LoadDone(pushDate);
        prmList = prms;
        dataCols = LoadDataColumns();

        var items = new List<Item>(); var skips = new List<Skipped>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var info = new Info(null, null, null);
        int rowNo = 1;
        foreach (var row in rows)
        {
            rowNo++; // the header is row 1 (the upload reader puts the real row number in "_row")
            if (row.TryGetValue("_row", out var rn) && int.TryParse(rn, out var realRow)) rowNo = realRow;
            string Cell(string name) => row.TryGetValue(name, out var s) ? s.Trim() : "";

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in prms)
            {
                var cell = CredentialParams.Contains(p.Name, StringComparer.OrdinalIgnoreCase) ? "" : Cell(p.Name);
                if (cell == "")
                {
                    values[p.Name] = p.Type switch
                    {
                        "CONST" => Expand(p.Value),
                        "PUSHDATE" => pushDate.AddMonths(p.OffsetMonths ?? 0).ToString(p.Format ?? "dd/MM/yyyy", Inv),
                        _ => null
                    };
                    continue;
                }
                if (p.Quote) cell = cell.Trim('\'');                                   // the quotes are added when the request is built
                if (p.Name.Equals("scno", StringComparison.OrdinalIgnoreCase)) cell = Regex.Replace(cell, @"\s+", "");
                if (p.Format != null) cell = NormalizeDate(cell, p.Format);
                values[p.Name] = cell;
            }

            var scno = values.GetValueOrDefault("scno") ?? "";
            var key = Cell("service_no");
            if (key == "") key = byCompact.GetValueOrDefault(scno) ?? (scno != "" ? scno : $"row {rowNo}");
            var unique = values.GetValueOrDefault("ukscno") ?? "";
            var isDone = done.Contains(key);
            void Skip(string why) => skips.Add(new Skipped(key, unique == "" ? null : unique, why, isDone, values, info));

            // already sent for this date (and not resending): counted as "already sent earlier", not checked and not shown as skipped
            if (isDone && !opts.IgnoreLog)
            {
                items.Add(new Item(key, unique, values, Soap.Envelope(Op, prms.Select(p => (p.Name, Quoted(p, values[p.Name])))), true, info));
                continue;
            }

            var empty = required.Where(n => string.IsNullOrEmpty(values.GetValueOrDefault(n))).ToList();
            if (empty.Count > 0) { Skip($"file: row {rowNo} has no value for {string.Join(", ", empty)}"); continue; }
            if (!Regex.IsMatch(unique, @"^\d{9}$")) { Skip($"file: row {rowNo} ukscno '{unique}' is not a 9-digit number"); continue; }
            if (seen.TryGetValue(unique, out var first)) { Skip($"file: row {rowNo} repeats ukscno {unique} from row {first}"); continue; }
            seen[unique] = rowNo;

            items.Add(new Item(key, unique, values,
                Soap.Envelope(Op, prms.Select(p => (p.Name, Quoted(p, values[p.Name])))), isDone, info));
        }
        Log($"upload: {rows.Count} row(s) read from {fileName}: {items.Count} ready, {skips.Count} skipped, {items.Count(i => i.Done)} already sent for {pushDate:yyyy-MM-dd}");

        var toSend = items.Where(i => opts.IgnoreLog || !i.Done).ToList();
        return new PlanResult(pushDate, global["ENDPOINT"], prms, items, skips, toSend) { FileName = fileName };
    }

    // a date cell given as an Excel serial number or in ISO form is written in the format of the parameter; anything else is sent as typed
    static string NormalizeDate(string v, string fmt)
    {
        if (DateTime.TryParseExact(v, fmt, Inv, DateTimeStyles.None, out _)) return v;
        if (double.TryParse(v, NumberStyles.Float, Inv, out var serial) && serial is > 20000 and < 80000)
            return DateTime.FromOADate(serial).ToString(fmt, Inv);
        if (DateTime.TryParseExact(v, DateFormats, Inv, DateTimeStyles.None, out var parsed))
            return parsed.ToString(fmt, Inv);
        return v;
    }

    // day-first dates with - or /, 12 or 24 hour time (with or without seconds), and ISO dates
    static readonly string[] DateFormats = BuildDateFormats();
    static string[] BuildDateFormats()
    {
        var list = new List<string> { "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" };
        var times = new[] { "", " h:mm tt", " h:mm:ss tt", " H:mm", " H:mm:ss" };
        foreach (var sep in new[] { "-", "/" })
            foreach (var year in new[] { "yyyy", "yy" })
                foreach (var t in times)
                    list.Add($"d{sep}M{sep}{year}{t}");
        return list.ToArray();
    }

    // ------------------------------------------------------------ quick test (a test limit is set)
    // The full procedure reads every meter (1-8 minutes). For a test of a few meters it is much faster to read only the
    // candidate meters, straight from their own modem table, with the same selection rule the procedure uses:
    // the day's latest row (CRTS) with MDkW and phase current present, and the highest kWh when a service has several.
    // The same checks (Evaluate) are applied, and candidates are examined until enough pass.

    const int QuickBatch = 20, QuickMaxExamined = 600;

    PlanResult PlanQuick(List<Param> prms, DateTime pushDate, DateTime oldDate, int forward, int lookback, List<DateTime> forwardDates, List<DateTime> mdDates)
    {
        var sw = Stopwatch.StartNew();
        var limit = opts.Limit;
        Log($"setMonthly: quick test for {limit} record(s) - reading only the meters needed, straight from the meter tables (the full procedure is not run)");

        var services = LoadServices();
        var done = LoadDone(pushDate);
        var tables = LoadServiceTables(db);
        var oldR = new Dictionary<string, Reading>(StringComparer.Ordinal);
        var prevEndR = new Dictionary<string, Reading>(StringComparer.Ordinal);
        var ctx = MakeCtx(prms, pushDate, oldDate, forward, lookback, mdDates, services, done, oldR, prevEndR);

        prmList = prms;
        dataCols = LoadDataColumns();
        if (dataCols == null) Log("setMonthly: dbo.PushData_Monthly not found - sent values will not be stored (run sql\\04_create_PushData_Monthly.sql)");

        var candidates = services.Values
            .Where(s => tables.ContainsKey(s.ServiceNo) && Regex.IsMatch(s.Unique ?? "", @"^\d{9}$") && (opts.IgnoreLog || !done.Contains(s.ServiceNo)))
            .OrderBy(s => s.ServiceNo, StringComparer.Ordinal).ToList();
        Log($"setMonthly: {candidates.Count} candidate meter(s) not yet sent; checking them {QuickBatch} at a time until {limit} pass");

        var items = new List<Item>();
        var skips = new List<Skipped>();
        int examined = 0, noReading = 0;
        var parallelism = Math.Max(1, int.Parse(global.GetValueOrDefault("PROC_PARALLELISM", "5")));
        var openingDates = new[] { oldDate }.Concat(forwardDates).ToList();

        for (int pos = 0; pos < candidates.Count && pos < QuickMaxExamined && items.Count < limit; pos += QuickBatch)
        {
            var batch = candidates.Skip(pos).Take(QuickBatch).ToList();
            var fetched = new (Reading? Cur, Reading? Old, Reading? Prev)[batch.Count];
            Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, i =>
            {
                using var conn = new SqlConnection(connStr);
                conn.Open();
                var no = batch[i].ServiceNo;
                var cur = ReadQuick(conn, tables[no], no, pushDate);
                if (cur == null) return; // no reading on the push date: not a meter of this run
                Reading? old = null, prev = null;
                foreach (var d in openingDates) { old = ReadQuick(conn, tables[no], no, d); if (old != null) break; }
                foreach (var d in mdDates) { prev = ReadQuick(conn, tables[no], no, d); if (prev != null) break; }
                fetched[i] = (cur, old, prev);
            });

            for (int i = 0; i < batch.Count && items.Count < limit; i++)
            {
                examined++;
                var no = batch[i].ServiceNo;
                var (cur, old, prev) = fetched[i];
                if (cur == null) { noReading++; continue; }
                if (old != null) oldR[no] = old;
                if (prev != null) prevEndR[no] = prev;
                Evaluate(new[] { KeyValuePair.Create(no, cur) }, ctx, items, skips);
            }
            Log($"setMonthly: checked {examined} meter(s): {items.Count} ready, {skips.Count} skipped, {noReading} with no reading on {pushDate:yyyy-MM-dd} ({sw.Elapsed.TotalSeconds:0} s)");
        }
        if (items.Count < limit) Log($"setMonthly: only {items.Count} meter(s) found after checking {examined} candidate(s)");
        Log($"setMonthly: quick test done in {sw.Elapsed.TotalSeconds:0} s");

        var toSend = items.Where(i => opts.IgnoreLog || !i.Done).Take(limit).ToList();
        return new PlanResult(pushDate, global["ENDPOINT"], prms, items, skips, toSend) { Quick = true, Examined = examined };
    }

    // service -> the modem tables that hold its readings (the same tables the procedure walks: active DC -> modem IMEI table)
    internal static Dictionary<string, List<string>> LoadServiceTables(SqlConnection db)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var cmd = new SqlCommand(@"
SELECT SD.ServiceNo, MDM.ModemIMEIno
FROM ModemDetails MDM
LEFT OUTER JOIN LogDCDetails DD ON DD.ModemId=MDM.ModemId
LEFT OUTER JOIN ServiceDetails SD ON SD.DCId=DD.DCId
INNER JOIN INFORMATION_SCHEMA.TABLES IST ON IST.TABLE_NAME = MDM.ModemIMEIno
WHERE DD.IsActive=1 AND SD.FeederId IN (6,11,19) AND SD.IsActive=1
GROUP BY SD.ServiceNo, MDM.ModemIMEIno", db) { CommandTimeout = 300 };
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
            var no = r.GetString(0).Trim();
            var t = r.GetString(1).Trim();
            if (!Regex.IsMatch(t, @"^\w+$")) continue; // table names come from the database; only plain names are used
            if (!d.TryGetValue(no, out var list)) d[no] = list = new List<string>();
            if (!list.Contains(t)) list.Add(t);
        }
        return d;
    }

    // One service, one day: the same row the procedure returns for it (same column names), or null if it has none.
    internal static Reading? ReadQuick(SqlConnection conn, List<string> tables, string serviceNo, DateTime date)
    {
        var day = date.ToString("yyyy-MM-dd", Inv);
        var rows = new List<Reading>();
        foreach (var t in tables)
        {
            using var cmd = new SqlCommand($@"
SELECT [DC SNo], [Service No], [MSN], [Phase Current], [Neutral Current], [Voltage], [Power Factor], [Frequency],
       [Meter Reading(kWh)], [Meter Reading(kVAh)], [MD kW], [MD kW TS], [MD kVA], [MD kVA TS], [Event Status], [Date & Time]
FROM (SELECT DD.DCSNo AS [DC SNo], SD.ServiceNo AS [Service No], MD.MeterSNo AS [MSN],
             DT.NormalPhaseCurrent AS [Phase Current], DT.NeutralCurrent AS [Neutral Current], DT.Voltage AS [Voltage],
             DT.Signedpowerfactor AS [Power Factor], DT.Frequency AS [Frequency],
             DT.CumulativeEnergykWh AS [Meter Reading(kWh)], DT.CumulativeEnergykVAh AS [Meter Reading(kVAh)],
             DT.MDkW AS [MD kW], DT.MDkWTS AS [MD kW TS], DT.MDkVA AS [MD kVA], DT.MDkVATS AS [MD kVA TS], STD.STATUSDESCRIPTION AS [Event Status],
             CONVERT(VARCHAR(16), DT.REALTIMECLOCKDATEANDTIME, 121) AS [Date & Time],
             RANK() OVER (PARTITION BY SD.ServiceNo ORDER BY DT.CRTS DESC) AS RK
      FROM {Br(t)} AS DT
      LEFT OUTER JOIN ServiceDetails SD ON SD.ServiceConnId=DT.ServiceconnId
      LEFT OUTER JOIN DCDetails DD ON SD.DCId=DD.DCId
      LEFT OUTER JOIN MeterDetails MD ON MD.MeterId=DT.MeterId
      LEFT OUTER JOIN STATUSDETAILS STD ON STD.STATUSCODE=MD.STATUSCODE
      WHERE DT.REALTIMECLOCKDATEANDTIME BETWEEN @from AND @to
        AND SD.IsActive=1 AND DT.NORMALPHASECURRENT IS NOT NULL AND DT.MDKW IS NOT NULL AND SD.FEEDERID IN (6,11,19)
        AND SD.ServiceNo=@sn) A
WHERE RK=1", conn) { CommandTimeout = 300 };
            cmd.Parameters.Add("@from", SqlDbType.VarChar, 30).Value = day + " 00:00:00";
            cmd.Parameters.Add("@to", SqlDbType.VarChar, 30).Value = day + " 23:59:59";
            cmd.Parameters.Add("@sn", SqlDbType.VarChar, 50).Value = serviceNo;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Reading { [ReadDate] = day };
                for (int i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), Inv);
                rows.Add(row);
            }
        }
        // across tables the procedure keeps the row with the highest CumulativeEnergykWh (compared as text), then the latest time
        return rows
            .OrderByDescending(r => r.GetValueOrDefault(KwhColumn) == null ? "" : r[KwhColumn]!.TrimEnd(), StringComparer.Ordinal)
            .ThenByDescending(r => r.GetValueOrDefault("Date & Time") ?? "", StringComparer.Ordinal)
            .FirstOrDefault();
    }

    static string Br(string name) => "[" + name.Replace("]", "]]") + "]";

    // --verify-quick N: compares the quick reader with the procedure on N random services (push date and the MD date), prints the differences
    internal bool VerifyQuick(int n)
    {
        var pushDate = opts.Date ?? DateTime.Today;
        var dates = new[] { pushDate, pushDate.AddDays(-pushDate.Day) }; // push date and the last day of the previous month
        var tables = LoadServiceTables(db);
        var results = CallMany(dates.ToList());
        var cols = new[] { "MSN", KwhColumn, "Meter Reading(kVAh)", "MD kW", "MD kW TS", "MD kVA", "MD kVA TS", "Date & Time" };
        int checkedN = 0, bad = 0; var rnd = new Random(12345);
        foreach (var d in dates)
        {
            var sample = results[d].Keys.Where(tables.ContainsKey).OrderBy(_ => rnd.Next()).Take(n).ToList();
            var noTable = results[d].Keys.Count(k => !tables.ContainsKey(k));
            Log($"verify {d:yyyy-MM-dd}: procedure returned {results[d].Count} service(s), {noTable} without a mapped table; comparing {sample.Count}");
            using var conn = new SqlConnection(connStr); conn.Open();
            var sw = Stopwatch.StartNew();
            foreach (var no in sample)
            {
                checkedN++;
                var q = ReadQuick(conn, tables[no], no, d);
                var p = results[d][no];
                if (q == null) { bad++; Log($"  DIFF {no}: quick reader found no row"); continue; }
                var diffs = cols.Where(c => (p.GetValueOrDefault(c)?.Trim() ?? "") != (q.GetValueOrDefault(c)?.Trim() ?? ""))
                    .Select(c => $"{c}: proc='{p.GetValueOrDefault(c)}' quick='{q.GetValueOrDefault(c)}'").ToList();
                if (diffs.Count > 0) { bad++; Log($"  DIFF {no}: {string.Join("; ", diffs)}"); }
            }
            Log($"verify {d:yyyy-MM-dd}: quick reader took {sw.Elapsed.TotalSeconds:0.0} s for {sample.Count} service(s) ({sw.Elapsed.TotalSeconds / Math.Max(1, sample.Count):0.00} s each)");
        }
        Log($"verify: {checkedN} compared, {bad} different");
        return bad == 0;
    }

    // ------------------------------------------------------------ execute: send the planned records one by one

    internal ExecResult Execute(PlanResult plan, Action<ExecProgress>? onProgress = null, CancellationToken ct = default)
    {
        prmList = plan.Prms;
        dataCols = LoadDataColumns();
        if (dataCols == null) Log("setMonthly: dbo.PushData_Monthly not found - sent values will not be stored (run sql\\04_create_PushData_Monthly.sql)");
        Log($"setMonthly: pushing {plan.ToSend.Count} record(s) to {plan.Endpoint}");

        foreach (var s in plan.Skips.Where(s => !s.Done))
        {
            WriteLog(plan.PushDate, s.ServiceNo, s.Unique, "SKIPPED", s.Why);
            WriteData(plan.PushDate, s.ServiceNo, s.Unique, "SKIPPED", s.Why, s.Values, null, s.Info);
        }

        var responses = new Dictionary<string, int>(); // server message -> count
        int pushed = 0, failed = 0, index = 0;
        var parallelism = ParallelSend.Parallelism(global);
        Log($"setMonthly: sending {parallelism} record(s) at a time");
        var stoppedBecause = ParallelSend.Run(plan.ToSend, parallelism, ct,
            it => Soap.Post(http, plan.Endpoint, Op, it.Xml),
            (it, ok, detail) => // runs one at a time (see ParallelSend)
            {
                WriteLog(plan.PushDate, it.ServiceNo, it.Unique, ok ? "OK" : "FAILED", detail);
                WriteData(plan.PushDate, it.ServiceNo, it.Unique, ok ? "OK" : "FAILED", detail, it.Values, MaskPassword(it.Xml), it.Info);
                index++;
                string line;
                if (ok)
                {
                    pushed++;
                    responses[detail] = responses.GetValueOrDefault(detail) + 1;
                    line = $"{it.ServiceNo} ({it.Unique}) -> {detail}";
                }
                else
                {
                    failed++;
                    line = $"{it.ServiceNo} ({it.Unique}) FAILED - {detail}";
                }
                Log($"setMonthly: {line}");
                onProgress?.Invoke(new ExecProgress(index, plan.ToSend.Count, pushed, failed, it.ServiceNo, ok, detail));
            });
        if (stoppedBecause != null) Log($"setMonthly: {stoppedBecause} - stopping, rerun to retry");

        foreach (var (msg, n) in responses.OrderByDescending(r => r.Value))
            Log($"setMonthly: server said '{msg}' for {n} service(s)");
        Log($"setMonthly: done - accepted {pushed}, skipped {plan.NewSkips}, failed {failed}");
        return new ExecResult(pushed, failed, plan.NewSkips, responses, stoppedBecause);
    }

    internal sealed record ExecProgress(int Index, int Total, int Pushed, int Failed, string ServiceNo, bool Ok, string Reply);
    internal sealed record ExecResult(int Pushed, int Failed, int Skipped, Dictionary<string, int> Replies, string? StoppedBecause);

    internal sealed class PlanResult
    {
        public DateTime PushDate { get; }
        public string Endpoint { get; }
        public List<Param> Prms { get; }
        public List<Item> Items { get; }       // everything that passed the checks (including services already sent OK)
        public List<Skipped> Skips { get; }    // services held back, with the reason
        public List<Item> ToSend { get; }      // what Execute will send
        public int NewSkips => Skips.Count(s => !s.Done);
        public bool Quick { get; init; }       // quick test: only some meters were read, so the counts are not for the whole day
        public int Examined { get; init; }     // quick test: how many meters were looked at
        public string? FileName { get; init; } // set when the records came from an uploaded file
        public PlanResult(DateTime pushDate, string endpoint, List<Param> prms, List<Item> items, List<Skipped> skips, List<Item> toSend)
        { PushDate = pushDate; Endpoint = endpoint; Prms = prms; Items = items; Skips = skips; ToSend = toSend; }
    }

    // readings used by the checks (not sent): opening kWh, the date the opening reading and the MD reading came from
    internal record Info(decimal? OpeningKwh, string? OpeningReadDate, string? MdReadDate);
    internal record Item(string ServiceNo, string Unique, Dictionary<string, string?> Values, string Xml, bool Done, Info Info);
    internal record Skipped(string ServiceNo, string? Unique, string Why, bool Done, Dictionary<string, string?>? Values, Info Info);

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

    internal static string MaskPassword(string xml) => Regex.Replace(xml, @"(<ser:password>).*?(</ser:password>)", "$1********$2");

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
    internal static string? Quoted(Param p, string? value) =>
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
    internal static Dictionary<string, Reading> CallProc(DateTime date, SqlConnection conn)
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

    // "Already sent" = accepted (OK) for ANY push date in the same calendar month as pushDate. A meter sent on 01-10 is not sent again
    // on 02-10 ... 31-10; in a new month the list starts empty again. (Failed and skipped rows do not count.)
    HashSet<string> LoadDone(DateTime pushDate)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var first = new DateTime(pushDate.Year, pushDate.Month, 1);
        using var cmd = new SqlCommand("SELECT DISTINCT ServiceNo FROM dbo.PushLog_Monthly WHERE PushDate>=@from AND PushDate<@to AND Status='OK'", db);
        cmd.Parameters.AddWithValue("@from", first);
        cmd.Parameters.AddWithValue("@to", first.AddMonths(1));
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

    void Log(string m) => logSink(m);
}
