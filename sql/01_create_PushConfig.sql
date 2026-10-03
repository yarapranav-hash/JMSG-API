-- Push configuration for MeterReadingService (setHourly / setMonthly)
-- Target DB: JMSGPILOT (SQL Server 2016)
-- One row per setting / per SOAP field.
--   Operation   : GLOBAL | setHourly | setMonthly
--   SourceType  : SETTING = plain setting value (GLOBAL rows, watermark rows)
--                 QUERY   = SELECT used to fetch rows to push (params: @LastId, @BatchSize)
--                 COLUMN  = SOAP element value taken from this column of the QUERY result
--                 CONST   = SOAP element value is this fixed text
--   SortOrder   : order of the elements in the SOAP body (must follow the WSDL sequence)
USE JMSGPILOT;
GO

IF OBJECT_ID('dbo.PushConfig') IS NOT NULL
BEGIN
    PRINT 'PushConfig already exists - nothing created.';
    SET NOEXEC ON;
END
GO

CREATE TABLE dbo.PushConfig
(
    ConfigId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PushConfig PRIMARY KEY,
    Operation   varchar(30)   NOT NULL,
    ParamName   varchar(60)   NOT NULL,
    SourceType  varchar(10)   NOT NULL CONSTRAINT CK_PushConfig_SourceType CHECK (SourceType IN ('SETTING','QUERY','COLUMN','CONST')),
    SourceValue nvarchar(max) NULL,
    SortOrder   int           NOT NULL CONSTRAINT DF_PushConfig_Sort DEFAULT (0),
    IsActive    bit           NOT NULL CONSTRAINT DF_PushConfig_Active DEFAULT (1),
    Remarks     nvarchar(400) NULL,
    UpdatedOn   datetime      NOT NULL CONSTRAINT DF_PushConfig_Upd DEFAULT (GETDATE()),
    CONSTRAINT UQ_PushConfig UNIQUE (Operation, ParamName)
);
GO

-- ---------------------------------------------------------------- GLOBAL
INSERT dbo.PushConfig (Operation, ParamName, SourceType, SourceValue, SortOrder, Remarks) VALUES
('GLOBAL','ENDPOINT',     'SETTING', N'http://117.239.151.73:9999/tsspd/services/MeterReadingService.MeterReadingServiceHttpSoap11Endpoint/', 1, N'SOAP 1.1 endpoint from the WSDL'),
('GLOBAL','SOAP_USERNAME','SETTING', N'WINAMR',      2, N'sent as <username> in both operations'),
('GLOBAL','SOAP_PASSWORD','SETTING', N'winamr$22',   3, N'sent as <password> in both operations'),
('GLOBAL','VENDOR_CODE',  'SETTING', N'0',           4, N'sent as <vendor_code> in setHourly'),
('GLOBAL','BATCH_SIZE',   'SETTING', N'500',         5, N'max rows pushed per operation per run'),
('GLOBAL','TIMEOUT_SEC',  'SETTING', N'60',          6, N'HTTP timeout per request');
GO

-- ------------------------------------------------------- setHourly source
-- Watermark = PeriodicalDataLive.DataLiveID (only index on that table is the PK, so we page by ID, never by time).
-- Starts ~20000 rows (a few hours) behind the newest row; lower it to back-fill, raise it to skip history.
DECLARE @startHourly bigint = (SELECT MAX(DataLiveID) FROM dbo.PeriodicalDataLive) - 20000;
DECLARE @startMonthly int   = (SELECT ISNULL(MAX(MonthlyBillingId),0) FROM dbo.MonthlyBilling);

INSERT dbo.PushConfig (Operation, ParamName, SourceType, SourceValue, SortOrder, Remarks) VALUES
('setHourly','LAST_ID','SETTING', CONVERT(nvarchar(20),@startHourly), 0, N'Highest DataLiveID already pushed. Updated by the app after each successful row.'),
('setMonthly','LAST_ID','SETTING', CONVERT(nvarchar(20),@startMonthly), 0, N'Highest MonthlyBillingId already pushed. Starts at current max = only new bills are pushed.');
GO

INSERT dbo.PushConfig (Operation, ParamName, SourceType, SourceValue, SortOrder, Remarks) VALUES
('setHourly','SOURCE_QUERY','QUERY', N'
SELECT TOP (@BatchSize)
       p.DataLiveID                                                         AS row_id,
       UPPER(FORMAT(CONVERT(datetime, p.RealtimeClockDateandTime), ''dd-MMM-yyyy'', ''en-US'')) AS reading_date,
       FORMAT(CONVERT(datetime, p.RealtimeClockDateandTime), ''hh:mm:ss tt'', ''en-US'')       AS reading_time,
       REPLACE(s.ServiceNo, '' '', '''')                                       AS unique_scno,
       CONVERT(varchar(30), p.CumulativeEnergykVAh)                         AS reading_kvah,
       CONVERT(varchar(30), p.CumulativeEnergykWh)                          AS reading_kwh,
       m.MeterSNo                                                           AS meterno,
       ISNULL(f.MfgName, '''')                                                AS metermake,
       CONVERT(varchar(10), ISNULL(NULLIF(m.MultiplicationFactor,0),1))     AS mf,
       CONVERT(varchar(10), CASE s.PhaseTypeId WHEN 1 THEN 1 ELSE 3 END)    AS phase,
       CONVERT(varchar(30), p.Signedpowerfactor)                            AS pf,
       CONVERT(varchar(30), p.Voltage)                                      AS r_vol,
       CONVERT(varchar(30), p.NormalPhaseCurrent)                           AS r_cur
FROM   dbo.PeriodicalDataLive p
JOIN   dbo.ServiceDetails s ON s.ServiceConnId = p.ServiceConnID
JOIN   dbo.MeterDetails   m ON m.MeterId       = p.MeterID
LEFT JOIN dbo.ManufacturerDetails f ON f.MfgId = m.MfgId
WHERE  p.DataLiveID > @LastId
  AND  RIGHT(p.RealtimeClockDateandTime, 5) = ''00:00''      -- on-the-hour readings only
  AND  ISDATE(p.RealtimeClockDateandTime) = 1
ORDER BY p.DataLiveID', 1, N'Parameters: @LastId (bigint), @BatchSize (int). Must return row_id + the columns mapped below.');
GO

-- setHourly field mapping - SortOrder follows the WSDL element order
INSERT dbo.PushConfig (Operation, ParamName, SourceType, SourceValue, SortOrder, Remarks) VALUES
('setHourly','vendor_code',        'CONST','{VENDOR_CODE}', 10, N'{VENDOR_CODE} = GLOBAL setting'),
('setHourly','reading_date',       'COLUMN','reading_date', 20, NULL),
('setHourly','reading_time',       'COLUMN','reading_time', 30, NULL),
('setHourly','unique_scno',        'COLUMN','unique_scno',  40, N'ServiceNo without the space (0301 02233 -> 030102233)'),
('setHourly','reading_kvah',       'COLUMN','reading_kvah', 50, NULL),
('setHourly','reading_kwh',        'COLUMN','reading_kwh',  60, NULL),
('setHourly','kwh_status',         'CONST','0', 70, NULL),
('setHourly','kvah_status',        'CONST','0', 80, NULL),
('setHourly','rmd_kw',             'CONST','0', 90, N'No source column in DB - placeholder'),
('setHourly','rmd_kva',            'CONST','0',100, N'No source column in DB - placeholder'),
('setHourly','phase',              'COLUMN','phase',       110, NULL),
('setHourly','meterno',            'COLUMN','meterno',     120, NULL),
('setHourly','metermake',          'COLUMN','metermake',   130, NULL),
('setHourly','mf',                 'COLUMN','mf',          140, NULL),
('setHourly','pf',                 'COLUMN','pf',          150, NULL),
('setHourly','deducted_amt',       'CONST','0',160, N'Prepaid field - not in DB'),
('setHourly','balance_amt',        'CONST','0',170, N'Prepaid field - not in DB'),
('setHourly','old_reading_kvah',   'CONST','0',180, N'Not in DB - confirm meaning with vendor'),
('setHourly','old_reading_kwh',    'CONST','0',190, N'Not in DB - confirm meaning with vendor'),
('setHourly','new_reading_kvah',   'COLUMN','reading_kvah',200, NULL),
('setHourly','new_reading_kwh',    'COLUMN','reading_kwh', 210, NULL),
('setHourly','new_meter_mf',       'CONST','0',220, NULL),
('setHourly','r_vol',              'COLUMN','r_vol',       230, NULL),
('setHourly','y_vol',              'CONST','0',240, NULL),
('setHourly','b_vol',              'CONST','0',250, NULL),
('setHourly','r_cur',              'COLUMN','r_cur',       260, NULL),
('setHourly','y_cur',              'CONST','0',270, NULL),
('setHourly','b_cur',              'CONST','0',280, NULL),
('setHourly','vol_cur_miss_flag',  'CONST','N',290, NULL),
('setHourly','consumed_units',     'CONST','0',300, N'Not in DB - placeholder'),
('setHourly','relay_status',       'CONST','0',310, NULL),
('setHourly','opn_rdg',            'CONST','0',320, NULL),
('setHourly','opn_balance',        'CONST','0',330, NULL),
('setHourly','slab_rate',          'CONST','0',340, NULL),
('setHourly','crje',               'CONST','0',350, NULL),
('setHourly','drje',               'CONST','0',360, NULL),
('setHourly','echgs',              'CONST','0',370, NULL),
('setHourly','fchgs',              'CONST','0',380, NULL),
('setHourly','cchgs',              'CONST','0',390, NULL),
('setHourly','edchgs',             'CONST','0',400, NULL),
('setHourly','mphase',             'COLUMN','phase',       410, NULL),
('setHourly','username',           'CONST','{SOAP_USERNAME}',420, NULL),
('setHourly','password',           'CONST','{SOAP_PASSWORD}',430, NULL);
GO

-- ------------------------------------------------------ setMonthly source
INSERT dbo.PushConfig (Operation, ParamName, SourceType, SourceValue, SortOrder, Remarks) VALUES
('setMonthly','SOURCE_QUERY','QUERY', N'
SELECT TOP (@BatchSize)
       b.MonthlyBillingId                                                   AS row_id,
       CONVERT(varchar(12), TRY_CONVERT(int, REPLACE(b.Service_No, '' '', ''''))) AS ukscno,
       b.METER_NO                                                           AS meter_no,
       CONVERT(varchar(10), CASE s.PhaseTypeId WHEN 1 THEN 1 ELSE 3 END)    AS meter_phase,
       c.CategoryNo                                                         AS tariff_category,
       UPPER(FORMAT(GETDATE(), ''dd-MMM-yyyy HH:mm:ss'', ''en-US''))             AS date_time,
       UPPER(FORMAT(TRY_CONVERT(date, b.Billing_date, 104), ''dd-MMM-yyyy'', ''en-US'')) AS bill_date,
       CONVERT(varchar(30), b.MD)                                           AS rmd_kva,
       CONVERT(varchar(30), s.ContractedLoad)                               AS contract_load,
       CONVERT(varchar(30), b.OPENING_READING)                              AS opening_kwh,
       CONVERT(varchar(30), b.CLOSING_READING)                              AS closing_kwh,
       b.status                                                             AS status,
       b.Spell_Status                                                       AS billing_status,
       CONVERT(varchar(30), b.CONSUMPTION)                                  AS consumed_units
FROM   dbo.MonthlyBilling b
LEFT JOIN dbo.ServiceDetails s ON s.ServiceNo = b.Service_No
LEFT JOIN dbo.ServiceCategoryDetails c ON c.CategoryId = s.CategoryId
WHERE  b.MonthlyBillingId > @LastId
  AND  TRY_CONVERT(int, REPLACE(b.Service_No, '' '', '''')) IS NOT NULL
ORDER BY b.MonthlyBillingId', 1, N'Parameters: @LastId (bigint), @BatchSize (int). Must return row_id + the columns mapped below.');
GO

INSERT dbo.PushConfig (Operation, ParamName, SourceType, SourceValue, SortOrder, Remarks) VALUES
('setMonthly','ukscno',                 'COLUMN','ukscno',          10, N'xs:int - ServiceNo digits, leading zero dropped (0301 02233 -> 30102233)'),
('setMonthly','meter_no',               'COLUMN','meter_no',        20, NULL),
('setMonthly','meter_phase',            'COLUMN','meter_phase',     30, NULL),
('setMonthly','tariff_category',        'COLUMN','tariff_category', 40, NULL),
('setMonthly','date_time',              'COLUMN','date_time',       50, NULL),
('setMonthly','op_rdgdt',               'CONST','',                 60, N'Not in DB'),
('setMonthly','bill_date',              'COLUMN','bill_date',       70, NULL),
('setMonthly','bill_processdt',         'COLUMN','bill_date',       80, N'Not in DB - uses bill_date'),
('setMonthly','rmd_kva',                'COLUMN','rmd_kva',         90, N'MonthlyBilling.MD'),
('setMonthly','rmd_kwh',                'CONST','0',               100, N'Not in DB'),
('setMonthly','contract_load',          'COLUMN','contract_load',  110, NULL),
('setMonthly','billing_type',           'CONST','0',               120, N'Not in DB - confirm code with vendor'),
('setMonthly','opening_kwh',            'COLUMN','opening_kwh',    130, NULL),
('setMonthly','opening_kvah',           'CONST','0',               140, N'Not in DB'),
('setMonthly','closing_kwh',            'COLUMN','closing_kwh',    150, NULL),
('setMonthly','closing_kvah',           'CONST','0',               160, N'Not in DB'),
('setMonthly','status',                 'COLUMN','status',         170, NULL),
('setMonthly','noof_months',            'CONST','1',               180, NULL),
('setMonthly','billing_status',         'COLUMN','billing_status', 190, NULL),
('setMonthly','consumed_units',         'COLUMN','consumed_units', 200, NULL),
('setMonthly','energy_consumed_amount', 'CONST','0',               210, N'Billing amounts are not in DB'),
('setMonthly','fixed_charges',          'CONST','0',               220, NULL),
('setMonthly','minimum_charges',        'CONST','0',               230, NULL),
('setMonthly','customer_charges',       'CONST','0',               240, NULL),
('setMonthly','total_deduction_amount', 'CONST','0',               250, NULL),
('setMonthly','ed_charges',             'CONST','0',               260, NULL),
('setMonthly','op_balance',             'CONST','0',               270, NULL),
('setMonthly','adj_charges',            'CONST','0',               280, NULL),
('setMonthly','closing_balance',        'CONST','0',               290, NULL),
('setMonthly','old_finkwh',             'CONST','0',               300, NULL),
('setMonthly','old_finkvah',            'CONST','0',               310, NULL),
('setMonthly','old_rmd',                'CONST','0',               320, NULL),
('setMonthly','meter_chgdt',            'CONST','',                330, NULL),
('setMonthly','diff_amt',               'CONST','0',               340, NULL),
('setMonthly','diff_amt_bldt',          'CONST','',                350, NULL),
('setMonthly','diff_amt_sentdt',        'CONST','',                360, NULL),
('setMonthly','username',               'CONST','{SOAP_USERNAME}', 370, NULL),
('setMonthly','password',               'CONST','{SOAP_PASSWORD}', 380, NULL);
GO

SET NOEXEC OFF;
GO
SELECT Operation, COUNT(*) AS rows_cnt FROM dbo.PushConfig GROUP BY Operation;
