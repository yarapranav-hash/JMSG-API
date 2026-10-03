USE JMSGPILOT;
GO
-- Quote = 1 wraps the value in single quotes when sent (the API puts values straight into its SQL: text needs quotes, e.g. '<meter_no>'A08767'</meter_no>')
IF COL_LENGTH('dbo.PushConfig_Monthly','Quote') IS NULL
    ALTER TABLE dbo.PushConfig_Monthly ADD Quote bit NOT NULL CONSTRAINT DF_PushConfig_Monthly_Quote DEFAULT (0);
GO
UPDATE dbo.PushConfig_Monthly SET Quote = 1 WHERE ParamName IN ('meter_no','billing_type');
-- ukscno = ServiceNo with the spaces removed (0301 02049 -> 030102049), as in the API sample
UPDATE dbo.PushConfig_Monthly SET SourceValue='ServiceNoCompact',
       Remarks=N'ServiceDetails.ServiceNo with spaces removed (0301 02049 -> 030102049), the format the API expects'
 WHERE ParamName='ukscno';
-- the earlier test row was logged OK although the server answered Un-Successful
UPDATE dbo.PushLog_Monthly SET Status='FAILED' WHERE Status='OK' AND Detail LIKE '%Un-Successful%';
GO
SELECT ParamName, SourceValue, Quote FROM dbo.PushConfig_Monthly WHERE Quote=1 OR ParamName='ukscno';
SELECT ServiceNo, Status, Detail FROM dbo.PushLog_Monthly;
