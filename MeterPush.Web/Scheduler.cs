using Microsoft.Data.SqlClient;

// Automatic hourly push: at 2 minutes past every hour (12:02, 13:02 ...) the web app pushes the readings of that hour
// (12:00, 13:00 ...) for today's date, exactly like Automatic mode on the Hourly tab (full read, meters already sent are not sent again).
// Switched on/off from the "Hourly: Automatic" tab; the switch is stored in dbo.PushConfig (GLOBAL / HOURLY_SCHEDULER = ON | OFF, default OFF).
// Every run is summarised in dbo.PushRun_Hourly (sql\06_create_PushRun_Hourly.sql), the meters in dbo.PushLog_Hourly.
class HourlyScheduler : BackgroundService
{
    public const int Minute = 2;      // the run starts at HH:02
    public const int WindowEnd = 30;  // a run that could not start earlier (page busy / program just started) still starts until HH:29

    readonly string connStr;
    DateTime lastSlot = DateTime.MinValue;   // the hour already handled (started, or recorded as missed)
    DateTime busySlot = DateTime.MinValue; int busyTries;
    bool warnedTables;

    public HourlyScheduler(string connStr) { this.connStr = connStr; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        Console.WriteLine($"[scheduler] ready - when switched on it runs at {Minute:00} minutes past every hour");
        while (!stop.IsCancellationRequested)
        {
            try { Tick(); } catch (Exception ex) { Console.WriteLine($"[scheduler] {DateTime.Now:HH:mm:ss} error: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (TaskCanceledException) { break; }
        }
    }

    void Tick()
    {
        var now = DateTime.Now;
        if (now.Minute < Minute) return;
        var slot = now.Date.AddHours(now.Hour);
        if (lastSlot == slot) return;
        if (!SchedulerSetting.IsOn(connStr)) return;
        if (!RunStore.Ready(connStr))
        {
            if (!warnedTables) { Console.WriteLine("[scheduler] dbo.PushRun_Hourly / dbo.PushLog_Hourly missing - run sql\\05 and sql\\06 first"); warnedTables = true; }
            return;
        }
        if (RunStore.HasRun(connStr, slot.Date, slot.Hour)) { lastSlot = slot; return; }

        if (now.Minute >= WindowEnd)
        {
            // too late for this hour: later readings have replaced the hour's reading as the meters' latest one
            if (busySlot == slot) RunStore.AddMissed(connStr, slot.Date, slot.Hour, "Not started: another push was still running until the window closed");
            lastSlot = slot;
            return;
        }

        var job = Runner.StartScheduledHourly(connStr, slot.Date, slot.Hour, out var error);
        if (job == null)
        {
            if (busySlot != slot) { busySlot = slot; busyTries = 0; }
            if (busyTries++ == 0) Console.WriteLine($"[scheduler] {now:HH:mm:ss} {slot:HH}:00 waiting: {error}");
            return; // try again at the next tick
        }
        lastSlot = slot;
        Console.WriteLine($"[scheduler] {now:HH:mm:ss} started the run for {slot:yyyy-MM-dd HH}:00 (job {job.Id})");
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
VALUES ('GLOBAL','HOURLY_SCHEDULER','SETTING',@v,50,N'ON = the web page sends the hourly data automatically at 2 minutes past every hour');", db);
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

    public static bool HasRun(string connStr, DateTime date, int hour)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PushRun_Hourly WHERE ReadingDate=@d AND ReadingHour=@h", db);
        cmd.Parameters.AddWithValue("@d", date.Date); cmd.Parameters.AddWithValue("@h", (byte)hour);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
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

    public static void Fail(string connStr, int runId, string message)
    {
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand("UPDATE dbo.PushRun_Hourly SET State='Failed', Message=@m, FinishedOn=GETDATE() WHERE RunId=@id", db);
        cmd.Parameters.AddWithValue("@id", runId); cmd.Parameters.AddWithValue("@m", Cut(message) ?? "");
        cmd.ExecuteNonQuery();
    }

    public static List<RunRow> List(string connStr, int take)
    {
        var list = new List<RunRow>();
        using var db = new SqlConnection(connStr); db.Open();
        using var cmd = new SqlCommand(@"SELECT TOP (@t) RunId, ReadingDate, ReadingHour, StartedOn, FinishedOn, State, Planned, AlreadySent, Skipped, Accepted, Failed, Replies, Message
FROM dbo.PushRun_Hourly ORDER BY ReadingDate DESC, ReadingHour DESC, RunId DESC", db);
        cmd.Parameters.AddWithValue("@t", Math.Clamp(take, 1, 500));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RunRow(r.GetInt32(0), r.GetDateTime(1), r.GetByte(2), r.GetDateTime(3), r.IsDBNull(4) ? null : r.GetDateTime(4), r.GetString(5),
                r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12)));
        return list;
    }

    static string? Cut(string? s) => s != null && s.Length > 500 ? s[..500] : s;
}
