using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

// Runs previews and sends in the background (one at a time) and keeps the plans that were previewed,
// so "Send" sends exactly what the user looked at.
class Job
{
    public string Id = Guid.NewGuid().ToString("N")[..8];
    public string Op = "monthly";            // monthly | hourly
    public bool Scheduled;                   // started by the automatic hourly scheduler (shown in the Hourly: Automatic tab)
    public string Kind = "";                 // preview | send
    public string State = "Running";         // Running | Done | Failed | Stopped
    public DateTime Started = DateTime.Now;
    public DateTime? Finished;
    public string PushDate = "";
    public string? PlanId;
    public string? Error;
    public int Total, Index, Pushed, Failed;
    public Dictionary<string, int> Replies = new();
    public string? StoppedBecause;
    public readonly List<FailRow> Failures = new();   // meters the API did not accept, with the reply
    public readonly List<string> Lines = new();
    public readonly CancellationTokenSource Cts = new();

    public void Log(string m)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {m}";
        lock (Lines) { if (Lines.Count < 20000) Lines.Add(line); }
        Console.WriteLine($"[{Kind} {Id}] {line}"); // also visible in the window the app runs in
    }
}

// Empty = request fields that had no value (their element was left out), a likely reason for "Un-Successful:null"
record FailRow(string ServiceNo, string Unique, string Reply, List<string> Empty);

class StoredPlan
{
    public string Id = Guid.NewGuid().ToString("N")[..10];
    public required MonthlyPush.PlanResult Plan;
    public required Opts Opts;
    public DateTime Created = DateTime.Now;
    public bool Sent;                         // a plan can be sent only once
}

class StoredHourlyPlan
{
    public string Id = Guid.NewGuid().ToString("N")[..10];
    public required HourlyPush.HPlan Plan;
    public DateTime Created = DateTime.Now;
    public bool Sent;                         // a plan can be sent only once
}

static class Runner
{
    static readonly object Gate = new();
    static Job? active;
    public static readonly ConcurrentDictionary<string, Job> Jobs = new();
    public static readonly ConcurrentDictionary<string, StoredPlan> Plans = new();

    public static Job? Active { get { lock (Gate) return active is { State: "Running" } ? active : null; } }

    // Keeps memory flat: a plan holds every meter of a run (thousands of requests), the scheduler makes one every hour.
    // Previews/plans older than 3 hours and jobs (with their log lines) finished more than 6 hours ago are dropped.
    static void Prune()
    {
        var planCut = DateTime.Now.AddHours(-3); var jobCut = DateTime.Now.AddHours(-6);
        foreach (var p in Plans.Values.Where(p => p.Created < planCut)) Plans.TryRemove(p.Id, out _);
        foreach (var p in HPlans.Values.Where(p => p.Created < planCut)) HPlans.TryRemove(p.Id, out _);
        foreach (var j in Jobs.Values.Where(j => j.Finished != null && j.Finished < jobCut)) Jobs.TryRemove(j.Id, out _);
    }

    static Job? TryStart(string kind, string pushDate, Action<Job> work, out string? error, string op = "monthly")
    {
        lock (Gate)
        {
            if (active is { State: "Running" }) { error = $"A {active.Op} {active.Kind} for {active.PushDate} is still running. Wait for it or stop it first."; return null; }
            Prune();
            var job = new Job { Kind = kind, PushDate = pushDate, Op = op };
            active = job;
            Jobs[job.Id] = job;
            error = null;
            Task.Run(() =>
            {
                try { work(job); if (job.State == "Running") job.State = "Done"; }
                catch (Exception ex) { job.Error = ex.Message; job.State = "Failed"; job.Log("ERROR: " + ex.Message); }
                finally { job.Finished = DateTime.Now; }
            });
            return job;
        }
    }

    static (SqlConnection Db, Dictionary<string, string> Global) Open(string connStr)
    {
        var db = new SqlConnection(connStr);
        db.Open();
        var g = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = new SqlCommand("SELECT ParamName, SourceValue FROM dbo.PushConfig WHERE Operation='GLOBAL' AND IsActive=1", db);
        using var r = cmd.ExecuteReader();
        while (r.Read()) g[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        return (db, g);
    }

    // auto = Automatic mode: after the preview is built the records are sent straight away, in the same job (no review step)
    public static Job? StartPreview(string connStr, DateTime date, int? limit, bool ignoreLog, bool auto, out string? error) =>
        TryStart("preview", date.ToString("yyyy-MM-dd"), job =>
        {
            var (db, global) = Open(connStr);
            using var dbScope = db;
            using var http = new HttpClient();
            var opts = new Opts { Op = "monthly", Date = date, Limit = limit ?? 0, IgnoreLog = ignoreLog, DryRun = true };
            var plan = new MonthlyPush(db, connStr, http, global, opts, job.Log).Plan();
            var sp = new StoredPlan { Plan = plan, Opts = opts };
            Plans[sp.Id] = sp;
            job.PlanId = sp.Id;
            job.Total = plan.ToSend.Count;
            // drop plans older than 3 hours
            foreach (var old in Plans.Values.Where(p => p.Created < DateTime.Now.AddHours(-3))) Plans.TryRemove(old.Id, out _);

            if (!auto) return;
            if (plan.ToSend.Count == 0) { job.Log("Automatic mode: nothing to send."); return; }
            lock (sp) sp.Sent = true;
            job.Kind = "send"; // from here on the page shows the send progress
            job.Log($"Automatic mode: sending {plan.ToSend.Count} record(s) without a review step.");
            DoSend(job, connStr, sp);
        }, out error);

    // records from an uploaded file: checked on the spot (no meter tables are read), kept as a plan that is previewed and sent like any other
    public static StoredPlan PlanUpload(string connStr, DateTime date, bool ignoreLog, List<Dictionary<string, string>> rows, string fileName, List<string> notes)
    {
        var (db, global) = Open(connStr);
        using var dbScope = db;
        using var http = new HttpClient();
        var opts = new Opts { Op = "monthly", Date = date, IgnoreLog = ignoreLog, DryRun = true };
        var plan = new MonthlyPush(db, connStr, http, global, opts, notes.Add).PlanFromRows(date, rows, fileName);
        var sp = new StoredPlan { Plan = plan, Opts = opts };
        Plans[sp.Id] = sp;
        foreach (var old in Plans.Values.Where(p => p.Created < DateTime.Now.AddHours(-3))) Plans.TryRemove(old.Id, out _);
        return sp;
    }

    public static (List<string> All, List<string> Required, List<(string Name, string Default)> Optional) UploadInfo(string connStr)
    {
        var (db, global) = Open(connStr);
        using var dbScope = db;
        using var http = new HttpClient();
        return new MonthlyPush(db, connStr, http, global, new Opts { Op = "monthly" }).UploadInfo();
    }

    public static Job? StartSend(string connStr, StoredPlan sp, out string? error) =>
        TryStart("send", sp.Plan.PushDate.ToString("yyyy-MM-dd"), job =>
        {
            job.PlanId = sp.Id;
            job.Total = sp.Plan.ToSend.Count;
            DoSend(job, connStr, sp);
        }, out error);

    // ------------------------------------------------------------ hourly (setHourly): same flow, own plans

    public static readonly ConcurrentDictionary<string, StoredHourlyPlan> HPlans = new();

    public static Job? StartHourlyPreview(string connStr, DateTime date, int hour, int? limit, bool ignoreLog, bool auto, out string? error) =>
        TryStart("preview", $"{date:yyyy-MM-dd} {hour:00}:00", job =>
        {
            var (db, global) = Open(connStr);
            using var dbScope = db;
            using var http = new HttpClient();
            var plan = new HourlyPush(db, connStr, http, global, job.Log).Plan(date, hour, limit ?? 0, ignoreLog);
            var sp = new StoredHourlyPlan { Plan = plan };
            HPlans[sp.Id] = sp;
            job.PlanId = sp.Id;
            job.Total = plan.ToSend.Count;
            foreach (var old in HPlans.Values.Where(p => p.Created < DateTime.Now.AddHours(-3))) HPlans.TryRemove(old.Id, out _);

            if (!auto) return;
            if (plan.ToSend.Count == 0) { job.Log("Automatic mode: nothing to send."); return; }
            if (!plan.LogReady) throw new InvalidOperationException("Cannot send: dbo.PushLog_Hourly does not exist. Run sql\\05_create_PushLog_Hourly.sql first.");
            lock (sp) sp.Sent = true;
            job.Kind = "send";
            job.Log($"Automatic mode: sending {plan.ToSend.Count} record(s) without a review step.");
            DoHourlySend(job, connStr, sp);
        }, out error, "hourly");

    public static Job? StartHourlySend(string connStr, StoredHourlyPlan sp, out string? error) =>
        TryStart("send", sp.Plan.Label, job =>
        {
            job.PlanId = sp.Id;
            job.Total = sp.Plan.ToSend.Count;
            DoHourlySend(job, connStr, sp);
        }, out error, "hourly");

    // The automatic run (HourlyScheduler): full read for the date, send everything at the hour that was not sent before, summarise in dbo.PushRun_Hourly.
    public static Job? StartScheduledHourly(string connStr, DateTime date, int hour, out string? error) =>
        TryStart("preview", $"{date:yyyy-MM-dd} {hour:00}:00", job =>
        {
            int runId = 0;
            job.Scheduled = true;
            try
            {
                job.Log($"Scheduler: automatic hourly run for {date:yyyy-MM-dd} {hour:00}:00");
                runId = RunStore.Start(connStr, date, hour);
                var (db, global) = Open(connStr);
                using var dbScope = db;
                using var http = new HttpClient();
                var plan = new HourlyPush(db, connStr, http, global, job.Log).Plan(date, hour, 0, false);
                var sp = new StoredHourlyPlan { Plan = plan, Sent = true }; // not kept in HPlans: the Automatic tab reads the results from the database
                job.Total = plan.ToSend.Count;
                var already = plan.Items.Count(i => i.Done);
                RunStore.Update(connStr, runId, "Running", plan.ToSend.Count, already, plan.Skips.Count, 0, 0, null, null, false);

                if (plan.ToSend.Count == 0)
                {
                    job.Log("Scheduler: nothing to send.");
                    RunStore.Update(connStr, runId, "Nothing", 0, already, plan.Skips.Count, 0, 0, null,
                        $"nothing to send: {plan.Items.Count} meter(s) have a reading at {hour:00}:00, {already} of them already sent", true);
                    return;
                }
                if (!plan.LogReady) throw new InvalidOperationException("dbo.PushLog_Hourly does not exist (run sql\\05_create_PushLog_Hourly.sql).");

                job.Kind = "send";
                DoHourlySend(job, connStr, sp);

                var state = job.Failed == 0 && job.StoppedBecause == null ? "Passed" : job.Pushed == 0 ? "Failed" : "Partial";
                var message = job.StoppedBecause != null ? $"stopped: {job.StoppedBecause}; {plan.ToSend.Count - job.Pushed - job.Failed} meter(s) not sent" : null;
                RunStore.Update(connStr, runId, state, plan.ToSend.Count, already, plan.Skips.Count, job.Pushed, job.Failed,
                    string.Join("; ", job.Replies.OrderByDescending(r => r.Value).Select(r => $"{r.Key}: {r.Value}")), message, true);
            }
            catch (Exception ex)
            {
                if (runId > 0) { try { RunStore.Fail(connStr, runId, ex.Message); } catch { } }
                throw; // TryStart marks the job as failed and logs it
            }
        }, out error, "hourly");

    static void DoHourlySend(Job job, string connStr, StoredHourlyPlan sp)
    {
        var (db, global) = Open(connStr);
        using var dbScope = db;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(int.Parse(global.GetValueOrDefault("TIMEOUT_SEC", "60"))) };
        var res = new HourlyPush(db, connStr, http, global, job.Log).Execute(sp.Plan, p =>
        {
            job.Index = p.Index; job.Pushed = p.Pushed; job.Failed = p.Failed;
            if (p.Ok) return;
            var it = sp.Plan.ToSend.FirstOrDefault(i => i.ServiceNo == p.ServiceNo);
            lock (job.Failures) job.Failures.Add(new FailRow(p.ServiceNo, it?.Unique ?? "", p.Reply, new List<string>()));
        }, job.Cts.Token);
        job.Pushed = res.Pushed; job.Failed = res.Failed;
        job.Replies = res.Replies;
        job.StoppedBecause = res.StoppedBecause;
        job.State = res.StoppedBecause != null ? "Stopped" : "Done";
    }

    static void DoSend(Job job, string connStr, StoredPlan sp)
    {
        var (db, global) = Open(connStr);
        using var dbScope = db;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(int.Parse(global.GetValueOrDefault("TIMEOUT_SEC", "60"))) };
        var opts = new Opts { Op = "monthly", Date = sp.Plan.PushDate, IgnoreLog = sp.Opts.IgnoreLog, Limit = sp.Opts.Limit };
        var res = new MonthlyPush(db, connStr, http, global, opts, job.Log).Execute(sp.Plan, p =>
        {
            job.Index = p.Index; job.Pushed = p.Pushed; job.Failed = p.Failed;
            if (p.Ok) return;
            var it = sp.Plan.ToSend.FirstOrDefault(i => i.ServiceNo == p.ServiceNo);
            var empty = it == null ? new List<string>() : sp.Plan.Prms
                .Where(x => !(x.Type == "CONST" && string.IsNullOrEmpty(x.Value)) && string.IsNullOrEmpty(it.Values.GetValueOrDefault(x.Name)))
                .Select(x => x.Name).ToList();
            lock (job.Failures) job.Failures.Add(new FailRow(p.ServiceNo, it?.Unique ?? "", p.Reply, empty));
        }, job.Cts.Token);
        job.Pushed = res.Pushed; job.Failed = res.Failed;
        job.Replies = res.Replies;
        job.StoppedBecause = res.StoppedBecause;
        job.State = res.StoppedBecause != null ? "Stopped" : "Done";
    }
}
