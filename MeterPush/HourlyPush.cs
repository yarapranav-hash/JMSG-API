using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

// setHourly (shared by the web app). For the chosen date and hour, every meter's reading stamped exactly HH:00 is read straight from the
// modem tables (full read), or only the meters needed (quick test, when a test limit is set); reading_time = HH:00:00. A later reading of the
// meter (e.g. 12:30) does not matter. Same selection rule as the procedure dbo.PrBLSTotalDCWiseMeterDataInstantDay, but for the hour.
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
        public int ProcRows { get; init; }             // meters that have a reading at the hour (full read)
        public bool Quick { get; init; }               // quick test: only some meters were read, so the counts are not for the whole day
        public int Examined { get; init; }             // quick test: how many meters were looked at
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
        if (limit > 0) return PlanQuick(date, hour, limit, ignoreLog, endpoint, logReady, services, done);

        Log($"setHourly: reading the {hour:00}:00 reading of every meter for {date:yyyy-MM-dd}, straight from the meter tables...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = ReadHour(date, hour);
        Log($"setHourly: {rows.Count} meter(s) have a reading at {hour:00}:00 (read in {sw.Elapsed.TotalSeconds:0} s)");

        var items = new List<HItem>(); var skips = new List<HSkip>();
        foreach (var (no, row) in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
            Evaluate(no, row, services, done, date, hour, items, skips);
        foreach (var no in services.Keys.Where(k => !rows.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            skips.Add(new HSkip(no, services[no].Unique, $"no reading at {hour:00}:00 on {date:yyyy-MM-dd}"));

        var toSend = items.Where(i => ignoreLog || !i.Done).ToList();
        Log($"setHourly: {items.Count} meter(s) passed the checks for {hour:00}:00 - to send {toSend.Count}, already sent {items.Count(i => i.Done)}, skipped {skips.Count}");
        return new HPlan { Date = date, Hour = hour, Endpoint = endpoint, IgnoreLog = ignoreLog, LogReady = logReady, ProcRows = rows.Count, Items = items, Skips = skips, ToSend = toSend };
    }

    // All meters, one pass over the modem tables: each meter's reading stamped exactly HH:00 on the date. Same selection rule as the
    // procedure and MonthlyPush.ReadQuick (active service on feeder 6/11/19, MD kW and phase current present, newest CRTS among the
    // rows, highest kWh when a service has rows in several tables) - but for the hour instead of "the newest reading of the day",
    // so it does not matter whether the meter has reported a later reading (e.g. 12:30) since. Any error stops the read (no silent gaps).
    Dictionary<string, MonthlyPush.Reading> ReadHour(DateTime date, int hour)
    {
        var tables = MonthlyPush.LoadServiceTables(db).Values.SelectMany(t => t).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
        var day = date.ToString("yyyy-MM-dd", Inv);
        var from = $"{day} {hour:00}:00:00"; var to = $"{day} {hour:00}:00:59";
        var parallelism = Math.Max(1, int.Parse(global.GetValueOrDefault("PROC_PARALLELISM", "5")));
        var result = new Dictionary<string, MonthlyPush.Reading>(StringComparer.Ordinal);
        var gate = new object();
        Log($"setHourly: {tables.Count} meter table(s), {parallelism} at a time");

        Parallel.ForEach(tables, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            () => { var c = new SqlConnection(connStr); c.Open(); return c; },
            (table, _, conn) =>
            {
                var rows = ReadHourTable(conn, table, from, to);
                lock (gate)
                    foreach (var r in rows)
                    {
                        var no = r.GetValueOrDefault("Service No")?.Trim();
                        if (string.IsNullOrEmpty(no)) continue;
                        if (!result.TryGetValue(no, out var cur) || Better(r, cur)) result[no] = r;
                    }
                return conn;
            },
            conn => conn.Dispose());
        return result;
    }

    // across tables the procedure keeps the row with the highest CumulativeEnergykWh (compared as text), then the latest time
    static bool Better(MonthlyPush.Reading a, MonthlyPush.Reading b)
    {
        var ka = a.GetValueOrDefault("Meter Reading(kWh)")?.TrimEnd() ?? ""; var kb = b.GetValueOrDefault("Meter Reading(kWh)")?.TrimEnd() ?? "";
        var c = string.CompareOrdinal(ka, kb);
        return c != 0 ? c > 0 : string.CompareOrdinal(a.GetValueOrDefault("Date & Time") ?? "", b.GetValueOrDefault("Date & Time") ?? "") > 0;
    }

    static List<MonthlyPush.Reading> ReadHourTable(SqlConnection conn, string table, string from, string to)
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
      FROM {MonthlyPush.Br(table)} AS DT
      LEFT OUTER JOIN ServiceDetails SD ON SD.ServiceConnId=DT.ServiceconnId
      LEFT OUTER JOIN DCDetails DD ON SD.DCId=DD.DCId
      LEFT OUTER JOIN MeterDetails MD ON MD.MeterId=DT.MeterId
      LEFT OUTER JOIN STATUSDETAILS STD ON STD.STATUSCODE=MD.STATUSCODE
      WHERE DT.REALTIMECLOCKDATEANDTIME BETWEEN @from AND @to
        AND SD.IsActive=1 AND DT.NORMALPHASECURRENT IS NOT NULL AND DT.MDKW IS NOT NULL AND SD.FEEDERID IN (6,11,19)) A
WHERE RK=1", conn) { CommandTimeout = 300 };
        cmd.Parameters.Add("@from", System.Data.SqlDbType.VarChar, 30).Value = from;
        cmd.Parameters.Add("@to", System.Data.SqlDbType.VarChar, 30).Value = to;
        var list = new List<MonthlyPush.Reading>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new MonthlyPush.Reading();
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), Inv);
            list.Add(row);
        }
        return list;
    }

    // The checks for one meter's reading at the hour (same for the full read and the quick test).
    void Evaluate(string no, MonthlyPush.Reading row, Dictionary<string, MonthlyPush.Svc> services, HashSet<string> done,
        DateTime date, int hour, List<HItem> items, List<HSkip> skips)
    {
        void Skip(string? unique, string why) => skips.Add(new HSkip(no, unique, why));
        if (!services.TryGetValue(no, out var svc)) { Skip(null, "service not found in ServiceDetails (or not active)"); return; }
        if (!Regex.IsMatch(svc.Unique ?? "", @"^\d{9}$")) { Skip(svc.Unique, "no valid 9-digit UniqueServiceNo in ServiceDetails"); return; }

        var stamp = row.GetValueOrDefault("Date & Time");
        if (!DateTime.TryParseExact(stamp?.Trim(), "yyyy-MM-dd HH:mm", Inv, DateTimeStyles.None, out var at)) { Skip(svc.Unique, "no Date & Time in the reading"); return; }
        if (at != date.Date.AddHours(hour)) { Skip(svc.Unique, $"latest reading is at {at:yyyy-MM-dd HH:mm}, not exactly {hour:00}:00"); return; }

        var values = BuildValues(date, hour, row, svc);
        var missing = Needed.Where(n => string.IsNullOrWhiteSpace(values[n])).ToList();
        if (missing.Count > 0) { Skip(svc.Unique, "no value in the reading for " + string.Join(", ", missing)); return; }

        var xml = Soap.Envelope(Op, Fields.Select(f => (f.Name, values[f.Name])));
        items.Add(new HItem(no, svc.Unique!, values, xml, done.Contains(no), stamp!.Trim()));
    }

    // ------------------------------------------------------------ quick test (a test limit is set)
    // Only candidate meters are read, straight from their own modem table, with the same selection rule the procedure uses
    // (MonthlyPush.ReadQuick: the day's latest row, MD kW and phase current present, highest kWh across tables), in batches
    // until `limit` meters have a reading exactly at the chosen hour and pass the checks.

    const int QuickBatch = 20, QuickMaxExamined = 1500;

    HPlan PlanQuick(DateTime date, int hour, int limit, bool ignoreLog, string endpoint, bool logReady,
        Dictionary<string, MonthlyPush.Svc> services, HashSet<string> done)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log($"setHourly: quick test for {limit} record(s) - reading only the meters needed, straight from the meter tables (the full procedure is not run)");
        var tables = MonthlyPush.LoadServiceTables(db);
        var candidates = services.Values
            .Where(s => tables.ContainsKey(s.ServiceNo) && Regex.IsMatch(s.Unique ?? "", @"^\d{9}$") && (ignoreLog || !done.Contains(s.ServiceNo)))
            .OrderBy(s => s.ServiceNo, StringComparer.Ordinal).ToList();
        Log($"setHourly: {candidates.Count} candidate meter(s){(ignoreLog ? "" : " not yet sent this hour")}; checking them {QuickBatch} at a time until {limit} are ready");

        var items = new List<HItem>(); var skips = new List<HSkip>();
        int examined = 0, noReading = 0;
        var parallelism = Math.Max(1, int.Parse(global.GetValueOrDefault("PROC_PARALLELISM", "5")));

        for (int pos = 0; pos < candidates.Count && pos < QuickMaxExamined && items.Count < limit; pos += QuickBatch)
        {
            var batch = candidates.Skip(pos).Take(QuickBatch).ToList();
            var fetched = new MonthlyPush.Reading?[batch.Count];
            Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, i =>
            {
                using var conn = new SqlConnection(connStr);
                conn.Open();
                fetched[i] = MonthlyPush.ReadQuick(conn, tables[batch[i].ServiceNo], batch[i].ServiceNo, date, $"{hour:00}:00:00", $"{hour:00}:00:59");
            });
            for (int i = 0; i < batch.Count && items.Count < limit; i++)
            {
                examined++;
                if (fetched[i] == null) { noReading++; continue; }
                Evaluate(batch[i].ServiceNo, fetched[i]!, services, done, date, hour, items, skips);
            }
            Log($"setHourly: checked {examined} meter(s): {items.Count} ready, {skips.Count} skipped, {noReading} with no reading at {hour:00}:00 ({sw.Elapsed.TotalSeconds:0} s)");
        }
        if (items.Count < limit) Log($"setHourly: only {items.Count} meter(s) found with a reading at {hour:00}:00 after checking {examined} candidate(s)");
        Log($"setHourly: quick test done in {sw.Elapsed.TotalSeconds:0} s");

        var toSend = items.Where(i => ignoreLog || !i.Done).Take(limit).ToList();
        return new HPlan { Date = date, Hour = hour, Endpoint = endpoint, Limit = limit, IgnoreLog = ignoreLog, LogReady = logReady, Quick = true, Examined = examined, Items = items, Skips = skips, ToSend = toSend };
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
        int pushed = 0, failed = 0, index = 0;
        var parallelism = ParallelSend.Parallelism(global);
        Log($"setHourly: sending {parallelism} record(s) at a time");
        string? stoppedBecause;
        try
        {
            stoppedBecause = ParallelSend.Run(plan.ToSend, parallelism, ct,
                it => Soap.Post(http, plan.Endpoint, Op, it.Xml),
                (it, ok, detail) => // runs one at a time (see ParallelSend)
                {
                    QueueLog(plan, it, ok ? "OK" : "FAILED", detail);
                    index++;
                    if (ok)
                    {
                        pushed++;
                        responses[detail] = responses.GetValueOrDefault(detail) + 1; // accepted meters are only counted (summary at the end)
                    }
                    else
                    {
                        failed++;
                        Log($"setHourly: {it.ServiceNo} ({it.Unique}) FAILED - {detail}");
                    }
                    onProgress?.Invoke(new MonthlyPush.ExecProgress(index, plan.ToSend.Count, pushed, failed, it.ServiceNo, ok, detail));
                });
        }
        finally { FlushLog(plan.Date, plan.Hour); } // whatever was sent is saved even when the run stops or fails
        if (stoppedBecause != null) Log($"setHourly: {stoppedBecause} - stopping, rerun to retry");
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

    // ---- log writes are batched: one database round trip per ~100 records instead of one per record
#pragma warning disable CS0649 // Tx is only set by tests
    internal SqlTransaction? Tx;   // set by tests only (so a test can roll everything back)
#pragma warning restore CS0649
    const int LogBatch = 100;       // 100 records x 8 parameters, far below SQL Server's 2,100-parameter limit
    readonly List<(HItem Item, string Status, string Detail)> pendingLog = new();
    DateTime lastLogFlush = DateTime.UtcNow;

    // called one at a time from ParallelSend; flushes when 100 are waiting or 3 seconds passed
    void QueueLog(HPlan plan, HItem it, string status, string detail)
    {
        pendingLog.Add((it, status, detail));
        if (pendingLog.Count >= LogBatch || (DateTime.UtcNow - lastLogFlush).TotalSeconds >= 3) FlushLog(plan.Date, plan.Hour);
    }

    static void Param(SqlCommand cmd, string name, SqlDbType type, int size, string? value)
    {
        var p = cmd.Parameters.Add(name, type, size);
        p.Value = (object?)value ?? DBNull.Value;
    }

    // Same statements as one-at-a-time (UPDATE the (date, hour, service) row, INSERT it when it is new), many per command.
    // The request XML is kept only for FAILED records (it is what the failed-meters sheet and the failure check need);
    // accepted records store NULL, which keeps the table small.
    internal void FlushLog(DateTime date, int hour)
    {
        if (pendingLog.Count == 0) return;
        var batch = pendingLog.ToList();
        pendingLog.Clear();
        lastLogFlush = DateTime.UtcNow;
        try
        {
            using var cmd = new SqlCommand { Connection = db, Transaction = Tx, CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@d", date.Date);
            cmd.Parameters.AddWithValue("@h", (byte)hour);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < batch.Count; i++)
            {
                var (it, status, detail) = batch[i];
                sb.Append($@"UPDATE dbo.PushLog_Hourly SET UniqueServiceNo=@u{i}, MeterNo=@m{i}, ReadAt=@ra{i}, Status=@s{i}, Detail=@t{i}, RequestXml=@x{i}, PushedOn=GETDATE() WHERE ReadingDate=@d AND ReadingHour=@h AND ServiceNo=@n{i};
IF @@ROWCOUNT=0 INSERT dbo.PushLog_Hourly (ReadingDate, ReadingHour, ServiceNo, UniqueServiceNo, MeterNo, ReadAt, Status, Detail, RequestXml) VALUES (@d,@h,@n{i},@u{i},@m{i},@ra{i},@s{i},@t{i},@x{i});
");
                // fixed types and sizes (= the column definitions) so every batch has the same shape and SQL Server reuses its plan
                Param(cmd, $"@n{i}", SqlDbType.VarChar, 30, it.ServiceNo);
                Param(cmd, $"@u{i}", SqlDbType.VarChar, 30, it.Unique);
                Param(cmd, $"@m{i}", SqlDbType.VarChar, 100, it.Values.GetValueOrDefault("meterno"));
                Param(cmd, $"@ra{i}", SqlDbType.VarChar, 20, it.ReadAt);
                Param(cmd, $"@s{i}", SqlDbType.VarChar, 10, status);
                Param(cmd, $"@t{i}", SqlDbType.VarChar, 500, detail.Length > 500 ? detail[..500] : detail);
                Param(cmd, $"@x{i}", SqlDbType.NVarChar, -1, status == "FAILED" ? MonthlyPush.MaskPassword(it.Xml) : null);
            }
            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }
        catch
        {
            pendingLog.InsertRange(0, batch); // not lost: the final flush tries again
            throw;
        }
    }

    void Log(string m) => logSink(m);
}
