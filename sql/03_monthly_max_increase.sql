-- setMonthly: a service is pushed only if (closing_kwh - opening_kwh) / opening_kwh * 100 <= this value.
-- Opening 0 with closing > 0 counts as above the limit. Held-back services are logged as SKIPPED in PushLog_Monthly.
USE JMSGPILOT;
GO
IF NOT EXISTS (SELECT 1 FROM dbo.PushConfig WHERE Operation='GLOBAL' AND ParamName='MONTHLY_MAX_INCREASE_PCT')
INSERT dbo.PushConfig (Operation, ParamName, SourceType, SourceValue, SortOrder, Remarks)
VALUES ('GLOBAL','MONTHLY_MAX_INCREASE_PCT','SETTING',N'120',20,N'setMonthly: max % increase from opening_kwh to closing_kwh that is still pushed');
GO
SELECT ParamName, SourceValue FROM dbo.PushConfig WHERE ParamName='MONTHLY_MAX_INCREASE_PCT';
