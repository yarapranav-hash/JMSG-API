-- Optional clean-up, run once in SSMS if you want to shrink dbo.PushLog_Hourly.
-- From now on the app stores the request XML only for FAILED hourly records. Records written before that still carry the XML
-- (about 4 KB per meter, 14 MB per hour). The XML of accepted records is not needed by the page or by the failed-meters sheet.
-- This keeps the XML of FAILED records and clears it for OK ones. Nothing else in the table is changed.
USE JMSGPILOT;
GO

-- how much would be cleared (look first)
SELECT COUNT(*) AS ok_rows_with_xml, CAST(SUM(DATALENGTH(RequestXml)) / 1048576.0 AS decimal(10,1)) AS mb
FROM dbo.PushLog_Hourly WHERE Status = 'OK' AND RequestXml IS NOT NULL;
GO

-- clear it in small steps so the log does not grow and other work is not blocked (repeat until it reports 0 rows)
WHILE 1 = 1
BEGIN
    UPDATE TOP (20000) dbo.PushLog_Hourly SET RequestXml = NULL WHERE Status = 'OK' AND RequestXml IS NOT NULL;
    IF @@ROWCOUNT = 0 BREAK;
END
GO

-- optional: give the freed space back to the file (can take a while on a big table)
-- ALTER INDEX ALL ON dbo.PushLog_Hourly REORGANIZE WITH (LOB_COMPACTION = ON);
