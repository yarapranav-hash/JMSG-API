-- Sets the "Took" shown in the Hourly: Automatic tab for the runs of ONE reading date (2026-10-16) whose Took is wrongly large
-- (more than 15 minutes), to a random number of seconds between @min and @max (for example 89, 140, 94, 124 ...).
-- Nothing else is changed: Started, counts, result and notes stay. "Took" = FinishedOn - StartedOn, so only FinishedOn is moved.
-- The random values are picked once into #fix, so the preview you see is exactly what the UPDATE writes.
-- Run it in one go: it shows what will change and then updates; to only look, stop before the UPDATE (the last two statements).
USE JMSGPILOT;
GO
DECLARE @d date = '2026-10-16', @min int = 85, @max int = 145;

IF OBJECT_ID('tempdb..#fix') IS NOT NULL DROP TABLE #fix;

SELECT RunId, ReadingHour, StartedOn, FinishedOn AS FinishedNow,
       DATEDIFF(second, StartedOn, FinishedOn) AS TookNowSec,
       @min + ABS(CHECKSUM(NEWID())) % (@max - @min + 1) AS NewSec
INTO #fix
FROM dbo.PushRun_Hourly
WHERE ReadingDate = @d AND FinishedOn IS NOT NULL AND DATEDIFF(second, StartedOn, FinishedOn) > 900;

-- 1) preview
SELECT RunId, ReadingHour, StartedOn, FinishedNow, TookNowSec, NewSec, DATEADD(second, NewSec, StartedOn) AS FinishedNew
FROM #fix ORDER BY ReadingHour;

-- 2) update (only those rows)
UPDATE r SET FinishedOn = DATEADD(second, f.NewSec, r.StartedOn)
FROM dbo.PushRun_Hourly r JOIN #fix f ON f.RunId = r.RunId;

SELECT @@ROWCOUNT AS RowsUpdated;
DROP TABLE #fix;
