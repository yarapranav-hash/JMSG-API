-- dbo.PushRun_Hourly: one row per automatic (scheduler) hourly run, shown in the "Hourly: Automatic" tab.
-- The per-meter results stay in dbo.PushLog_Hourly (05_create_PushLog_Hourly.sql); this table only holds the summary of each run.
USE JMSGPILOT;
GO

IF OBJECT_ID('dbo.PushRun_Hourly') IS NULL
BEGIN
    CREATE TABLE dbo.PushRun_Hourly (
        RunId        int IDENTITY(1,1) CONSTRAINT PK_PushRun_Hourly PRIMARY KEY,
        ReadingDate  date          NOT NULL,
        ReadingHour  tinyint       NOT NULL,   -- the run for 12:02 pushes the readings of 12:00
        StartedOn    datetime      NOT NULL CONSTRAINT DF_PushRun_Hourly_Started DEFAULT GETDATE(),
        FinishedOn   datetime      NULL,
        State        varchar(12)   NOT NULL,   -- Running | Passed | Partial | Failed | Nothing | Missed
        Planned      int           NOT NULL CONSTRAINT DF_PushRun_Hourly_Planned DEFAULT 0,      -- meters to send
        AlreadySent  int           NOT NULL CONSTRAINT DF_PushRun_Hourly_Already DEFAULT 0,      -- meters at this hour that were already sent OK
        Skipped      int           NOT NULL CONSTRAINT DF_PushRun_Hourly_Skipped DEFAULT 0,      -- meters held back (not at this hour, missing values, ...)
        Accepted     int           NOT NULL CONSTRAINT DF_PushRun_Hourly_Accepted DEFAULT 0,
        Failed       int           NOT NULL CONSTRAINT DF_PushRun_Hourly_Failed DEFAULT 0,
        Replies      varchar(500)  NULL,       -- API replies with counts
        Message      varchar(500)  NULL
    );
    CREATE INDEX IX_PushRun_Hourly_Slot ON dbo.PushRun_Hourly (ReadingDate, ReadingHour);
END
GO
