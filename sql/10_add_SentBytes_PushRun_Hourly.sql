-- Adds the column that holds the size (bytes) of the requests an hourly run sent; the Hourly: Automatic tab shows it as "Size (KB)" in its last column.
-- Safe to run more than once. Runs made before the column existed show "-".
USE JMSGPILOT;
GO
IF COL_LENGTH('dbo.PushRun_Hourly', 'SentBytes') IS NULL
    ALTER TABLE dbo.PushRun_Hourly ADD SentBytes bigint NULL;
GO
SELECT COL_LENGTH('dbo.PushRun_Hourly', 'SentBytes') AS SentBytesColumnLength;
