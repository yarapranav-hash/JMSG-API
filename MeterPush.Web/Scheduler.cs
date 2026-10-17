using Microsoft.Data.SqlClient;

// Automatic hourly push: at the start of every hour (20:00, 21:00 ...) the web app pushes the readings of the PREVIOUS hour
// (19:00, 20:00 ...; at 00:00 it is 23:00 of the day before), exactly like Automatic mode on the Hourly tab: every meter's reading stamped
// exactly that hour is sent whatever later readings it has, meters already sent for that hour are not sent again.
// Switched on/off from the "Hourly: Automatic" tab; the switch is stored in dbo.PushConfig (GLOBAL / HOURLY_SCHEDULER = ON | OFF, default OFF).
// Every run is summarised in dbo.PushRun_Hourly (sql\06_create_PushRun_Hourly.sql), the meters in dbo.PushLog_Hourly.
class HourlyScheduler : BackgroundService
{
    public const int Minute = 0;      // the run starts at HH:00 (it pushes the readings of the hour before)
    public const int WindowEnd = 60;  // a run that could not start earlier (page busy / program just started) still starts until the end of that hour (HH:59)

    // the reading hour a run started now is for: the previous hour (can be 23:00 of yesterday)
    public static DateTime TargetSlot(DateTime now) => now.Date.AddHours(now.Hour - 1);

    readonly string connStr;
    DateTime lastSlot = DateTime.MinValue;   // the hour already handled (started, or recorded as missed)
    Job? lastJob;                            // the run started for lastSlot
    DateTime busySlot = DateTime.MinValue; int busyTries;
    bool warnedTables;

    public HourlyScheduler(string connStr) { this.connStr = connStr; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        Console.WriteLine("[scheduler] ready - when switched on it runs at the start of every hour for the previous hour (20:00 pushes 19:00)");
        while (!stop.IsCancellationRequested)
        {
            try { Tick(); } catch (Exception ex) { Console.WriteLine($"[scheduler] {DateTime.Now:HH:mm:ss} error: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (TaskCanceledException) { break; }
        }
    }

    void Tick()
    {
        var now = DateTime.Now;
        var slot = TargetSlot(now);   // the reading hour this tick is responsible for
        if (!SchedulerSetting.IsOn(connStr)) return;
        if (!RunStore.Ready(connStr))
        {
            if (!warnedTables) { Console.WriteLine("[scheduler] dbo.PushRun_Hourly / dbo.PushLog_Hourly missing - run sql\\05 and sql\\06 first"); warnedTables = true; }
            return;
        }

        // an earlier hour that kept waiting for a free slot and never got to run: record it as Missed
        if (busySlot != DateTime.MinValue && busySlot < slot)
        {
            if (!RunStore.HasRun(connStr, busySlot.Date, busySlot.Hour))
                RunStore.AddMissed(connStr, busySlot.Date, busySlot.Hour, "Not started: another push was still running until the end of the hour");
            busySlot = DateTime.MinValue;
        }

        if (now.Minute < Minute) return;
        if (lastSlot == slot)
        {
            // the run for this hour has ended without doing anything (it failed while loading): try again a minute later, until HH:59
            if (lastJob is { Finished: { } fin } && now - fin > TimeSpan.FromSeconds(60) && !RunStore.HasRun(connStr, slot.Date, slot.Hour))
            {
                Console.WriteLine($"[scheduler] {now:HH:mm:ss} the run for {slot:yyyy-MM-dd HH}:00 did not complete - trying again");
                lastSlot = DateTime.MinValue; lastJob = null;
            }
            else return;
        }
        if (RunStore.HasRun(connStr, slot.Date, slot.Hour)) { lastSlot = slot; return; }
        if (now.Minute >= WindowEnd) { lastSlot = slot; return; }

        var job = Runner.StartScheduledHourly(connStr, slot.Date, slot.Hour, out var error);
        if (job == null)
        {
            if (busySlot != slot) { busySlot = slot; busyTries = 0; }
            if (busyTries++ == 0) Console.WriteLine($"[scheduler] {now:HH:mm:ss} reading hour {slot:HH}:00 waiting: {error}");
            return; // try again at the next tick
        }
        lastSlot = slot; lastJob = job;
        Console.WriteLine($"[scheduler] {now:HH:mm:ss} started the run for the {slot:yyyy-MM-dd HH}:00 readings (job {job.Id})");
    }
}

static class SchedulerSetting
{
    public static bool IsOn(string connStr)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand("SELECT SourceValue FROM dbo.PushConfig WHERE Operation='GLOBAL' AND ParamName='HOURLY_SCHEDULER' AND IsActive=1", db);
        return string.Equals(cmd.ExecuteScalar() as string, "ON", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(string connStr, bool on)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand(@"
UPDATE dbo.PushConfig SET SourceValue=@v, IsActive=1, UpdatedOn=GETDATE() WHERE Operation='GLOBAL' AND ParamName='HOURLY_SCHEDULER';
IF @@ROWCOUNT=0 INSERT dbo.PushConfig (Operation, ParamName, SourceType, SourceValue, SortOrder, Remarks)
VALUES ('GLOBAL','HOURLY_SCHEDULER','SETTING',@v,50,N'ON = the web page sends the hourly data automatically at the start of every hour (for the previous hour)');", db);
        cmd.Parameters.AddWithValue("@v", on ? "ON" : "OFF");
        cmd.ExecuteNonQuery();
    }
}

record RunRow(int RunId, DateTime ReadingDate, int ReadingHour, DateTime StartedOn, DateTime? FinishedOn, string State,
    int Planned, int AlreadySent, int Skipped, int Accepted, int Failed, string? Replies, string? Message);

// summary rows of the automatic runs (dbo.PushRun_Hourly)
static class RunStore
{
    // both tables the automatic run needs
    public static bool Ready(string connStr)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID('dbo.PushRun_Hourly') IS NOT NULL AND OBJECT_ID('dbo.PushLog_Hourly') IS NOT NULL THEN 1 ELSE 0 END", db);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    public const int MaxAttempts = 3;   // a run that failed before it sent anything is tried again, up to this many runs for one hour

    // The hour has been handled: a run exists that did something (sent, nothing to send, stopped, missed ...). A run that FAILED before
    // it had anything to send (the database timed out while loading) does not count - the scheduler tries that hour again, up to MaxAttempts times.
    public static bool HasRun(string connStr, DateTime date, int hour)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand(@"SELECT
  SUM(CASE WHEN State='Failed' AND Planned=0 AND Accepted=0 AND Failed=0 THEN 0 ELSE 1 END),
  COUNT(*)
FROM dbo.PushRun_Hourly WHERE ReadingDate=@d AND ReadingHour=@h", db) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@d", date.Date); cmd.Parameters.AddWithValue("@h", (byte)hour);
        using var r = cmd.ExecuteReader();
        r.Read();
        var counted = r.IsDBNull(0) ? 0 : r.GetInt32(0);
        var all = r.GetInt32(1);
        return counted > 0 || all >= MaxAttempts;
    }

    public static int Start(string connStr, DateTime date, int hour)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand("INSERT dbo.PushRun_Hourly (ReadingDate, ReadingHour, State) VALUES (@d,@h,'Running'); SELECT CAST(SCOPE_IDENTITY() AS int)", db);
        cmd.Parameters.AddWithValue("@d", date.Date); cmd.Parameters.AddWithValue("@h", (byte)hour);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static void AddMissed(string connStr, DateTime date, int hour, string message)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand("INSERT dbo.PushRun_Hourly (ReadingDate, ReadingHour, FinishedOn, State, Message) VALUES (@d,@h,GETDATE(),'Missed',@m)", db);
        cmd.Parameters.AddWithValue("@d", date.Date); cmd.Parameters.AddWithValue("@h", (byte)hour); cmd.Parameters.AddWithValue("@m", message);
        cmd.ExecuteNonQuery();
    }

    public static void Update(string connStr, int runId, string state, int planned, int alreadySent, int skipped, int accepted, int failed,
        string? replies, string? message, bool finished)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand(@"UPDATE dbo.PushRun_Hourly SET State=@s, Planned=@p, AlreadySent=@a, Skipped=@k, Accepted=@ok, Failed=@f,
Replies=@r, Message=@m, FinishedOn=CASE WHEN @fin=1 THEN GETDATE() ELSE FinishedOn END WHERE RunId=@id", db);
        cmd.Parameters.AddWithValue("@id", runId); cmd.Parameters.AddWithValue("@s", state);
        cmd.Parameters.AddWithValue("@p", planned); cmd.Parameters.AddWithValue("@a", alreadySent); cmd.Parameters.AddWithValue("@k", skipped);
        cmd.Parameters.AddWithValue("@ok", accepted); cmd.Parameters.AddWithValue("@f", failed);
        cmd.Parameters.AddWithValue("@r", (object?)Cut(replies) ?? DBNull.Value); cmd.Parameters.AddWithValue("@m", (object?)Cut(message) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fin", finished ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    // After a manual send of an hour: the Automatic tab shows the LATEST numbers for that hour. The counts and the API replies are
    // recalculated from the hour's records in dbo.PushLog_Hourly (a meter sent again replaces its earlier record, so a failed meter that
    // is now accepted counts as accepted). The latest run row of the hour is updated; if the hour has no run row (the scheduler did not run
    // for it) one is added.
    public static void SyncAfterManualSend(string connStr, DateTime date, int hour, int skipped, DateTime jobStarted, string? stoppedBecause)
    {
        using var db = new SqlConnection(connStr); db.Open();
        int ok = 0, failed = 0;
        using (var c = new SqlCommand("SELECT Status, COUNT(*) FROM dbo.PushLog_Hourly WHERE ReadingDate=@d AND ReadingHour=@h GROUP BY Status", db) { CommandTimeout = 120 })
        {
            c.Parameters.AddWithValue("@d", date.Date); c.Parameters.AddWithValue("@h", (byte)hour);
            using var r = c.ExecuteReader();
            while (r.Read()) { if (r.GetString(0) == "OK") ok = r.GetInt32(1); else if (r.GetString(0) == "FAILED") failed = r.GetInt32(1); }
        }
        var replies = new List<string>();
        using (var c = new SqlCommand("SELECT TOP 6 Detail, COUNT(*) FROM dbo.PushLog_Hourly WHERE ReadingDate=@d AND ReadingHour=@h GROUP BY Detail ORDER BY COUNT(*) DESC", db) { CommandTimeout = 120 })
        {
            c.Parameters.AddWithValue("@d", date.Date); c.Parameters.AddWithValue("@h", (byte)hour);
            using var r = c.ExecuteReader();
            while (r.Read()) replies.Add($"{(r.IsDBNull(0) ? "" : r.GetString(0))}: {r.GetInt32(1)}");
        }
        int? runId = null;
        using (var c = new SqlCommand("SELECT TOP 1 RunId FROM dbo.PushRun_Hourly WHERE ReadingDate=@d AND ReadingHour=@h ORDER BY RunId DESC", db) { CommandTimeout = 120 })
        {
            c.Parameters.AddWithValue("@d", date.Date); c.Parameters.AddWithValue("@h", (byte)hour);
            if (c.ExecuteScalar() is int id) runId = id;
        }
        var state = failed == 0 && stoppedBecause == null ? "Passed" : ok == 0 ? "Failed" : "Partial";
        var note = $"updated after a manual send at {DateTime.Now:HH:mm}: {ok} accepted, {failed} failed" + (stoppedBecause != null ? $"; stopped: {stoppedBecause}" : "");
        if (runId == null)
        {
            runId = Start(connStr, date, hour);
            using var u = new SqlCommand("UPDATE dbo.PushRun_Hourly SET StartedOn=@s WHERE RunId=@id", db);
            u.Parameters.AddWithValue("@s", jobStarted); u.Parameters.AddWithValue("@id", runId.Value);
            u.ExecuteNonQuery();
        }
        Update(connStr, runId.Value, state, ok + failed, 0, skipped, ok, failed, string.Join("; ", replies), note, true);
    }

    // different meters with a FAILED record on this reading date (any hour)
    public static int DistinctFailed(string connStr, DateTime date)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand("SELECT COUNT(DISTINCT ServiceNo) FROM dbo.PushLog_Hourly WHERE ReadingDate=@d AND Status='FAILED'", db);
        cmd.Parameters.AddWithValue("@d", date.Date);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static void Fail(string connStr, int runId, string message)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand("UPDATE dbo.PushRun_Hourly SET State='Failed', Message=@m, FinishedOn=GETDATE() WHERE RunId=@id", db);
        cmd.Parameters.AddWithValue("@id", runId); cmd.Parameters.AddWithValue("@m", Cut(message) ?? "");
        cmd.ExecuteNonQuery();
    }

    // date = only the runs of that reading date (null = the latest runs of all dates)
    public static List<RunRow> List(string connStr, int take, DateTime? date = null)
    {
        var list = new List<RunRow>();
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand(@"SELECT TOP (@t) RunId, ReadingDate, ReadingHour, StartedOn, FinishedOn, State, Planned, AlreadySent, Skipped, Accepted, Failed, Replies, Message
FROM dbo.PushRun_Hourly WHERE (@d IS NULL OR ReadingDate = @d) ORDER BY ReadingDate DESC, ReadingHour DESC, RunId DESC", db);
        cmd.Parameters.AddWithValue("@t", Math.Clamp(take, 1, 500));
        cmd.Parameters.AddWithValue("@d", date.HasValue ? date.Value.Date : DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RunRow(r.GetInt32(0), r.GetDateTime(1), r.GetByte(2), r.GetDateTime(3), r.IsDBNull(4) ? null : r.GetDateTime(4), r.GetString(5),
                r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12)));
        return list;
    }

    static string? Cut(string? s) => s != null && s.Length > 500 ? s[..500] : s;
}
