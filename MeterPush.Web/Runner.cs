using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

// Runs previews and sends in the background (one at a time) and keeps the plans that were previewed,
// so "Send" sends exactly what the user looked at.
class Job
{
    public string Id = Guid.NewGuid().ToString("N")[..8];
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
    public readonly List<string> Lines = new();
    public readonly CancellationTokenSource Cts = new();

    public void Log(string m)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {m}";
        lock (Lines) { if (Lines.Count < 20000) Lines.Add(line); }
        Console.WriteLine($"[{Kind} {Id}] {line}"); // also visible in the window the app runs in
    }
}

class StoredPlan
{
    public string Id = Guid.NewGuid().ToString("N")[..10];
    public required MonthlyPush.PlanResult Plan;
    public required Opts Opts;
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

    static Job? TryStart(string kind, string pushDate, Action<Job> work, out string? error)
    {
        lock (Gate)
        {
            if (active is { State: "Running" }) { error = $"A {active.Kind} for {active.PushDate} is still running. Wait for it or stop it first."; return null; }
            var job = new Job { Kind = kind, PushDate = pushDate };
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

    public static Job? StartPreview(string connStr, DateTime date, int? limit, bool ignoreLog, out string? error) =>
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
        }, out error);

    public static Job? StartSend(string connStr, StoredPlan sp, out string? error) =>
        TryStart("send", sp.Plan.PushDate.ToString("yyyy-MM-dd"), job =>
        {
            job.PlanId = sp.Id;
            job.Total = sp.Plan.ToSend.Count;
            var (db, global) = Open(connStr);
            using var dbScope = db;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(int.Parse(global.GetValueOrDefault("TIMEOUT_SEC", "60"))) };
            var opts = new Opts { Op = "monthly", Date = sp.Plan.PushDate, IgnoreLog = sp.Opts.IgnoreLog, Limit = sp.Opts.Limit };
            var res = new MonthlyPush(db, connStr, http, global, opts, job.Log).Execute(sp.Plan, p =>
            {
                job.Index = p.Index; job.Pushed = p.Pushed; job.Failed = p.Failed;
            }, job.Cts.Token);
            job.Pushed = res.Pushed; job.Failed = res.Failed;
            job.Replies = res.Replies;
            job.StoppedBecause = res.StoppedBecause;
            job.State = res.StoppedBecause != null ? "Stopped" : "Done";
        }, out error);
}
