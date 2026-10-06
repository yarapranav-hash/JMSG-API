using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

// MeterPush.Web - browser front end for the monthly push (setMonthly).
//   Run screen    : preview what would be sent, then send exactly that
//   History screen: what was sent / skipped / failed per push date (dbo.PushLog_Monthly + dbo.PushData_Monthly)
// The push logic itself is the same code as the console app (MonthlyPush.cs).

var builder = WebApplication.CreateBuilder(args);
var connStr = builder.Configuration["ConnectionString"] ?? throw new InvalidOperationException("ConnectionString is missing in appsettings.json");
var accessCode = builder.Configuration["AccessCode"] ?? "";
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// every /api call needs the access code (X-Access-Code header)
app.Use(async (ctx, next) =>
{
    if (accessCode != "" && ctx.Request.Path.StartsWithSegments("/api"))
    {
        var given = ctx.Request.Headers["X-Access-Code"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(accessCode)))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Wrong or missing access code." });
            return;
        }
    }
    await next();
});

var api = app.MapGroup("/api");

// ------------------------------------------------------------ run screen

api.MapPost("/preview", (PreviewReq req) =>
{
    if (!DateTime.TryParseExact(req.Date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
        return Results.BadRequest(new { error = "Push date must be yyyy-MM-dd." });
    var job = Runner.StartPreview(connStr, date, req.Limit is > 0 ? req.Limit : null, req.IgnoreLog, out var error);
    return job == null ? Results.Conflict(new { error }) : Results.Ok(new { jobId = job.Id });
});

api.MapGet("/jobs/active", () => Runner.Active is { } j ? Results.Ok(new { jobId = j.Id, kind = j.Kind }) : Results.Ok(new { jobId = (string?)null }));

api.MapGet("/jobs/{id}", (string id, int? from) =>
{
    if (!Runner.Jobs.TryGetValue(id, out var j)) return Results.NotFound(new { error = "Unknown job." });
    string[] lines;
    var start = Math.Max(0, from ?? 0);
    lock (j.Lines) lines = j.Lines.Skip(start).ToArray();
    return Results.Ok(new
    {
        id = j.Id, kind = j.Kind, state = j.State, pushDate = j.PushDate, started = j.Started, finished = j.Finished,
        planId = j.PlanId, error = j.Error, total = j.Total, index = j.Index, pushed = j.Pushed, failed = j.Failed,
        replies = j.Replies.OrderByDescending(r => r.Value).Select(r => new { reply = r.Key, count = r.Value, meaning = ReplyMeaning(r.Key) }),
        stoppedBecause = j.StoppedBecause, lines, nextLine = start + lines.Length
    });
});

api.MapPost("/jobs/{id}/stop", (string id) =>
{
    if (!Runner.Jobs.TryGetValue(id, out var j)) return Results.NotFound(new { error = "Unknown job." });
    if (j.State != "Running") return Results.Ok(new { stopped = false });
    j.Cts.Cancel();
    j.Log("Stop requested - finishing the record being sent...");
    return Results.Ok(new { stopped = true });
});

api.MapGet("/plans/{id}", (string id) =>
{
    if (!Runner.Plans.TryGetValue(id, out var sp)) return Results.NotFound(new { error = "This preview has expired. Run Preview again." });
    var p = sp.Plan;
    return Results.Ok(new
    {
        planId = sp.Id, pushDate = p.PushDate.ToString("yyyy-MM-dd"), endpoint = p.Endpoint, created = sp.Created, sent = sp.Sent,
        toSend = p.ToSend.Count, alreadySent = p.Items.Count(i => i.Done), skipped = p.NewSkips,
        limit = sp.Opts.Limit, ignoreLog = sp.Opts.IgnoreLog,
        skippedBreakdown = p.Skips.Where(s => !s.Done).GroupBy(s => SkipLabel(s.Why))
            .OrderByDescending(g => g.Count()).Select(g => new { reason = g.Key, count = g.Count() })
    });
});

api.MapGet("/plans/{id}/records", (string id, int? skip, int? take, string? q) =>
{
    if (!Runner.Plans.TryGetValue(id, out var sp)) return Results.NotFound(new { error = "This preview has expired. Run Preview again." });
    var rows = sp.Plan.ToSend.Where(i => Matches(q, i.ServiceNo, i.Unique, V(i, "meter_no"), V(i, "scno"))).ToList();
    return Results.Ok(new
    {
        total = rows.Count,
        rows = rows.Skip(skip ?? 0).Take(Math.Clamp(take ?? 50, 1, 500)).Select(i => new
        {
            serviceNo = i.ServiceNo, ukscno = V(i, "ukscno"), meterNo = V(i, "meter_no"), scno = V(i, "scno"), dateTime = V(i, "date_time"),
            rmdKva = V(i, "rmd_kva"), rmdKwh = V(i, "rmd_kwh"), closingKwh = V(i, "closing_kwh"), closingKvah = V(i, "closing_kvah"),
            consumedUnits = V(i, "consumed_units"), alreadySent = i.Done
        })
    });
});

api.MapGet("/plans/{id}/records/{serviceNo}", (string id, string serviceNo) =>
{
    if (!Runner.Plans.TryGetValue(id, out var sp)) return Results.NotFound(new { error = "This preview has expired. Run Preview again." });
    var it = sp.Plan.Items.FirstOrDefault(i => i.ServiceNo == serviceNo);
    if (it == null) return Results.NotFound(new { error = "Not in this preview." });
    return Results.Ok(new
    {
        serviceNo = it.ServiceNo, openingKwh = it.Info.OpeningKwh, openingReadDate = it.Info.OpeningReadDate, mdReadDate = it.Info.MdReadDate,
        values = sp.Plan.Prms.Select(p => new { name = p.Name, sent = SentValue(p, it) }),
        xml = MonthlyPush.MaskPassword(it.Xml)
    });
});

api.MapGet("/plans/{id}/skipped", (string id, int? skip, int? take, string? q) =>
{
    if (!Runner.Plans.TryGetValue(id, out var sp)) return Results.NotFound(new { error = "This preview has expired. Run Preview again." });
    var rows = sp.Plan.Skips.Where(s => !s.Done && Matches(q, s.ServiceNo, s.Unique, s.Why)).ToList();
    return Results.Ok(new
    {
        total = rows.Count,
        rows = rows.Skip(skip ?? 0).Take(Math.Clamp(take ?? 50, 1, 500)).Select(s => new
        { serviceNo = s.ServiceNo, ukscno = s.Unique, category = SkipLabel(s.Why), reason = s.Why })
    });
});

api.MapGet("/plans/{id}/download/{what}", (string id, string what) =>
{
    if (!Runner.Plans.TryGetValue(id, out var sp)) return Results.NotFound(new { error = "This preview has expired. Run Preview again." });
    var p = sp.Plan;
    var sb = new StringBuilder();
    if (what == "records")
    {
        sb.AppendLine(string.Join(",", p.Prms.Select(x => Csv(x.Name))));
        foreach (var it in p.ToSend) sb.AppendLine(string.Join(",", p.Prms.Select(x => Csv(SentValue(x, it)))));
    }
    else if (what == "skipped")
    {
        sb.AppendLine("service_no,unique_service_no,reason");
        foreach (var s in p.Skips.Where(s => !s.Done)) sb.AppendLine(string.Join(",", new[] { s.ServiceNo, s.Unique ?? "", s.Why }.Select(Csv)));
    }
    else return Results.NotFound();
    var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    return Results.File(bytes, "text/csv", $"monthly_{what}_{p.PushDate:yyyy-MM-dd}.csv");
});

api.MapPost("/plans/{id}/send", (string id, SendReq req) =>
{
    if (!Runner.Plans.TryGetValue(id, out var sp)) return Results.NotFound(new { error = "This preview has expired. Run Preview again." });
    if (sp.Plan.ToSend.Count == 0) return Results.BadRequest(new { error = "Nothing to send." });
    if (req.ConfirmCount != sp.Plan.ToSend.Count) return Results.BadRequest(new { error = "The confirmed count does not match the preview." });
    lock (sp)
    {
        if (sp.Sent) return Results.Conflict(new { error = "This preview was already sent. Run Preview again to see what is left." });
        var job = Runner.StartSend(connStr, sp, out var error);
        if (job == null) return Results.Conflict(new { error });
        sp.Sent = true;
        return Results.Ok(new { jobId = job.Id });
    }
});

// ------------------------------------------------------------ history screen

api.MapGet("/history/dates", () =>
{
    using var db = new SqlConnection(connStr); db.Open();
    using var cmd = new SqlCommand("SELECT DISTINCT TOP 90 PushDate FROM dbo.PushLog_Monthly ORDER BY PushDate DESC", db);
    using var r = cmd.ExecuteReader();
    var list = new List<string>();
    while (r.Read()) list.Add(r.GetDateTime(0).ToString("yyyy-MM-dd"));
    return Results.Ok(list);
});

api.MapGet("/history", (string date, string? status, string? q, int? take) =>
{
    if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
        return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
    using var db = new SqlConnection(connStr); db.Open();

    var counts = new Dictionary<string, int>();
    using (var c = new SqlCommand("SELECT Status, COUNT(*) FROM dbo.PushLog_Monthly WHERE PushDate=@d GROUP BY Status", db))
    {
        c.Parameters.AddWithValue("@d", d);
        using var r = c.ExecuteReader();
        while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
    }

    var rows = new List<object>();
    using (var c = new SqlCommand(@"
SELECT TOP (@take) l.ServiceNo, l.UniqueServiceNo, l.Status, l.Detail, l.PushedOn,
       d.meter_no, d.closing_kwh, d.consumed_units, d.rmd_kva, d.scno,
       CASE WHEN d.DataId IS NULL THEN 0 ELSE 1 END AS hasData
FROM dbo.PushLog_Monthly l
LEFT JOIN dbo.PushData_Monthly d ON d.PushDate = l.PushDate AND d.ServiceNo = l.ServiceNo
WHERE l.PushDate = @d
  AND (@status IS NULL OR l.Status = @status)
  AND (@q IS NULL OR l.ServiceNo LIKE @like OR l.UniqueServiceNo LIKE @like OR d.meter_no LIKE @like OR l.Detail LIKE @like)
ORDER BY l.PushedOn DESC, l.ServiceNo", db))
    {
        c.Parameters.AddWithValue("@take", Math.Clamp(take ?? 300, 1, 2000));
        c.Parameters.AddWithValue("@d", d);
        c.Parameters.AddWithValue("@status", string.IsNullOrEmpty(status) ? DBNull.Value : status);
        c.Parameters.AddWithValue("@q", string.IsNullOrWhiteSpace(q) ? DBNull.Value : q.Trim());
        c.Parameters.AddWithValue("@like", "%" + (q ?? "").Trim() + "%");
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var detail = r.IsDBNull(3) ? "" : r.GetString(3);
            rows.Add(new
            {
                serviceNo = r.GetString(0), ukscno = r.IsDBNull(1) ? null : r.GetString(1), status = r.GetString(2), reply = detail,
                meaning = r.GetString(2) == "SKIPPED" ? SkipLabel(detail) : ReplyMeaning(detail), pushedOn = r.GetDateTime(4),
                meterNo = r.IsDBNull(5) ? null : r.GetString(5), closingKwh = r.IsDBNull(6) ? null : r.GetString(6),
                consumedUnits = r.IsDBNull(7) ? null : r.GetString(7), rmdKva = r.IsDBNull(8) ? null : r.GetString(8),
                scno = r.IsDBNull(9) ? null : r.GetString(9), hasData = r.GetInt32(10) == 1
            });
        }
    }
    return Results.Ok(new { counts, rows });
});

api.MapGet("/history/{date}/{serviceNo}", (string date, string serviceNo) =>
{
    if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
        return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
    using var db = new SqlConnection(connStr); db.Open();
    using var c = new SqlCommand("SELECT * FROM dbo.PushData_Monthly WHERE PushDate=@d AND ServiceNo=@n", db);
    c.Parameters.AddWithValue("@d", d);
    c.Parameters.AddWithValue("@n", serviceNo);
    using var r = c.ExecuteReader();
    if (!r.Read()) return Results.Ok(new { found = false });
    var fixedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DataId", "PushDate", "ServiceNo", "UniqueServiceNo", "Outcome", "Reply", "PushedOn", "OpeningKwh", "OpeningReadDate", "MdReadDate", "RequestXml" };
    var values = new List<object>();
    object? Cell(int i) => r.IsDBNull(i) ? null : r.GetValue(i);
    string? xml = null; object? openingKwh = null, openingDate = null, mdDate = null, outcome = null, reply = null, pushedOn = null;
    for (int i = 0; i < r.FieldCount; i++)
    {
        var name = r.GetName(i);
        switch (name.ToLowerInvariant())
        {
            case "requestxml": xml = r.IsDBNull(i) ? null : r.GetString(i); break;
            case "openingkwh": openingKwh = Cell(i); break;
            case "openingreaddate": openingDate = Cell(i); break;
            case "mdreaddate": mdDate = Cell(i); break;
            case "outcome": outcome = Cell(i); break;
            case "reply": reply = Cell(i); break;
            case "pushedon": pushedOn = Cell(i); break;
            default: if (!fixedCols.Contains(name)) values.Add(new { name, sent = Cell(i) }); break;
        }
    }
    return Results.Ok(new { found = true, outcome, reply, pushedOn, openingKwh, openingReadDate = openingDate, mdReadDate = mdDate, values, xml });
});

app.Run();

// ------------------------------------------------------------ helpers

static string? V(MonthlyPush.Item i, string name) => i.Values.TryGetValue(name, out var v) ? v : null;

static string SentValue(MonthlyPush.Param p, MonthlyPush.Item it) =>
    p.Name.Equals("password", StringComparison.OrdinalIgnoreCase) ? "********"
        : MonthlyPush.Quoted(p, it.Values.GetValueOrDefault(p.Name)) ?? "";

static bool Matches(string? q, params string?[] fields) =>
    string.IsNullOrWhiteSpace(q) || fields.Any(f => f != null && f.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));

static string Csv(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

// plain-language labels for the staff
static string SkipLabel(string why)
{
    var w = why.ToLowerInvariant();
    if (w.StartsWith("no reading for")) return "No opening reading";
    if (w.StartsWith("no rmd")) return "No MD reading";
    if (w.StartsWith("increase")) return "Consumption increase above the limit";
    if (w.StartsWith("closing <")) return "Closing reading below opening";
    if (w.StartsWith("rmd_")) return "MD value above the limit";
    if (w.StartsWith("no valid 9-digit")) return "No valid unique service number";
    if (w.StartsWith("service not found")) return "Service not in ServiceDetails";
    if (w.StartsWith("opening or closing")) return "kWh reading missing";
    return "Other";
}

static string ReplyMeaning(string reply)
{
    if (reply.Equals("Successful", StringComparison.OrdinalIgnoreCase)) return "Accepted - new record";
    if (reply.Contains("already updated", StringComparison.OrdinalIgnoreCase)) return "Accepted - record already existed";
    if (Regex.IsMatch(reply, @"^(un-?successful|fail|error)|ORA-\d+", RegexOptions.IgnoreCase)) return "Rejected by the API";
    if (reply.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase)) return "Server error";
    return "";
}

record PreviewReq(string Date, int? Limit, bool IgnoreLog);
record SendReq(int ConfirmCount);
