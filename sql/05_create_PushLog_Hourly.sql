-- dbo.PushLog_Hourly: one row per (reading date, hour, service) for setHourly. Used to remember what was already sent
-- in that hour (so it is not sent twice) and to show what the vendor replied. The password is masked in RequestXml.
-- A rerun for the same date, hour and service updates the row (e.g. a FAILED one that is retried and becomes OK).
USE JMSGPILOT;
GO

IF OBJECT_ID('dbo.PushLog_Hourly') IS NULL
CREATE TABLE dbo.PushLog_Hourly (
    LogId            int IDENTITY(1,1) CONSTRAINT PK_PushLog_Hourly PRIMARY KEY,
    ReadingDate      date          NOT NULL,
    ReadingHour      tinyint       NOT NULL,   -- 0-23; the request carries reading_time = HH:00:00
    ServiceNo        varchar(30)   NOT NULL,
    UniqueServiceNo  varchar(30)   NULL,
    MeterNo          varchar(100)  NULL,
    ReadAt           varchar(20)   NULL,       -- "Date & Time" of the meter reading that was sent
    Status           varchar(10)   NOT NULL,   -- OK | FAILED
    Detail           varchar(500)  NULL,       -- API reply
    RequestXml       nvarchar(max) NULL,
    PushedOn         datetime      NOT NULL CONSTRAINT DF_PushLog_Hourly_PushedOn DEFAULT GETDATE(),
    CONSTRAINT UQ_PushLog_Hourly UNIQUE (ReadingDate, ReadingHour, ServiceNo)
);
GO
