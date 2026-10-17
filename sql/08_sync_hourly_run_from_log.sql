-- One-time fix for an hour that was sent manually BEFORE the Automatic tab followed manual sends:
-- sets the latest run row of that hour to the real numbers in dbo.PushLog_Hourly (accepted = OK records, failed = FAILED records).
-- New manual sends update the Automatic tab by themselves; this is only for hours sent earlier.
-- Change the two values, run the SELECT first to see what will change, then the UPDATE.
USE JMSGPILOT;
GO
DECLARE @d date = '2026-10-16', @h tinyint = 21;

SELECT r.RunId, r.State, r.Planned, r.Accepted, r.Failed AS FailedNow,
       SUM(CASE WHEN l.Status='OK' THEN 1 ELSE 0 END) AS AcceptedInLog,
       SUM(CASE WHEN l.Status='FAILED' THEN 1 ELSE 0 END) AS FailedInLog
FROM dbo.PushRun_Hourly r
LEFT JOIN dbo.PushLog_Hourly l ON l.ReadingDate = r.ReadingDate AND l.ReadingHour = r.ReadingHour
WHERE r.RunId = (SELECT MAX(RunId) FROM dbo.PushRun_Hourly WHERE ReadingDate=@d AND ReadingHour=@h)
GROUP BY r.RunId, r.State, r.Planned, r.Accepted, r.Failed;

UPDATE r SET
    Accepted = x.ok, Failed = x.bad, Planned = x.ok + x.bad,
    State = CASE WHEN x.bad = 0 THEN 'Passed' WHEN x.ok = 0 THEN 'Failed' ELSE 'Partial' END,
    Message = N'updated from the log after a manual send: ' + CAST(x.ok AS nvarchar(10)) + N' accepted, ' + CAST(x.bad AS nvarchar(10)) + N' failed',
    FinishedOn = ISNULL(r.FinishedOn, GETDATE())
FROM dbo.PushRun_Hourly r
CROSS APPLY (SELECT SUM(CASE WHEN Status='OK' THEN 1 ELSE 0 END) ok, SUM(CASE WHEN Status='FAILED' THEN 1 ELSE 0 END) bad
             FROM dbo.PushLog_Hourly WHERE ReadingDate=@d AND ReadingHour=@h) x
WHERE r.RunId = (SELECT MAX(RunId) FROM dbo.PushRun_Hourly WHERE ReadingDate=@d AND ReadingHour=@h);
