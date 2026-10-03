-- Mapping for setMonthly. One row per SOAP parameter, in WSDL order (SortOrder).
-- Data comes from stored procedure dbo.PrBLSTotalDCWiseMeterDataInstantDay, called twice:
--   NEW = push date            (closing readings, MSN, MD, Date & Time)
--   OLD = push date - 1 month  (opening readings)
-- and is joined on Service No to dbo.ServiceDetails (UniqueServiceNo, ContractedLoad).
--
--   SourceType  PROC_NEW : column of the procedure result for the push date
--               PROC_OLD : column of the procedure result for push date - 1 month
--               SERVICE  : column of dbo.ServiceDetails
--               PUSHDATE : the push date, shifted by DateOffsetMonths
--               CALC     : "a-b" of two other ParamNames (numeric)
--               CONST    : fixed text ('' = element is not sent). {SOAP_USERNAME}/{SOAP_PASSWORD} come from PushConfig GLOBAL rows
--   Format      .NET format for dates (dd/MM/yyyy ...); applied to PROC_* "Date & Time" and PUSHDATE
USE JMSGPILOT;
GO

IF OBJECT_ID('dbo.PushConfig_Monthly') IS NOT NULL
BEGIN
    PRINT 'PushConfig_Monthly already exists - nothing created.';
    SET NOEXEC ON;
END
GO

CREATE TABLE dbo.PushConfig_Monthly
(
    ConfigId         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PushConfig_Monthly PRIMARY KEY,
    ParamName        varchar(60)  NOT NULL CONSTRAINT UQ_PushConfig_Monthly UNIQUE,
    SortOrder        int          NOT NULL,
    SourceType       varchar(20)  NOT NULL CONSTRAINT CK_PushConfig_Monthly_Type CHECK (SourceType IN ('PROC_NEW','PROC_OLD','PROC_PREVEND','SERVICE','PUSHDATE','CALC','CONST')),
    SourceValue      nvarchar(200) NULL,
    DateOffsetMonths int          NULL,
    Format           varchar(40)  NULL,
    IsActive         bit          NOT NULL CONSTRAINT DF_PushConfig_Monthly_Active DEFAULT (1),
    Remarks          nvarchar(300) NULL,
    UpdatedOn        datetime     NOT NULL CONSTRAINT DF_PushConfig_Monthly_Upd DEFAULT (GETDATE())
);
GO

CREATE TABLE dbo.PushLog_Monthly
(
    LogId           bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PushLog_Monthly PRIMARY KEY,
    PushDate        date          NOT NULL,
    ServiceNo       varchar(50)   NOT NULL,
    UniqueServiceNo varchar(20)   NULL,
    Status          varchar(10)   NOT NULL,   -- OK | FAILED | SKIPPED
    Detail          nvarchar(500) NULL,
    PushedOn        datetime      NOT NULL CONSTRAINT DF_PushLog_Monthly_On DEFAULT (GETDATE()),
    CONSTRAINT UQ_PushLog_Monthly UNIQUE (PushDate, ServiceNo)
);
GO

INSERT dbo.PushConfig_Monthly (ParamName, SortOrder, SourceType, SourceValue, DateOffsetMonths, Format, Remarks) VALUES
('ukscno',                 10, 'SERVICE',  'UniqueServiceNo',            NULL, NULL, N'ServiceDetails.UniqueServiceNo (joined on ServiceNo). Services without a 9-digit value are skipped.'),
('meter_no',               20, 'PROC_NEW', 'MSN',                        NULL, NULL, NULL),
('meter_phase',            30, 'CONST',    '1',                          NULL, NULL, NULL),
('tariff_category',        40, 'CONST',    '1',                          NULL, NULL, NULL),
('date_time',              50, 'PROC_NEW', 'Date & Time',                NULL, 'dd/MM/yyyy hh:mm:ss tt', N'Meter reading timestamp from the procedure'),
('op_rdgdt',               60, 'PUSHDATE', NULL,                         -1,   'dd/MM/yyyy', N'push date - 1 month'),
('bill_date',              70, 'PUSHDATE', NULL,                         0,    'dd/MM/yyyy', N'push date'),
('bill_processdt',         80, 'PUSHDATE', NULL,                         0,    'dd/MM/yyyy', N'push date'),
('rmd_kva',                90, 'PROC_PREVEND', 'MD kVA',                 NULL, NULL, NULL),
('rmd_kwh',               100, 'PROC_PREVEND','MD kW',                      NULL, NULL, N'Procedure has no MD kWh - MD kW used'),
('contract_load',         110, 'SERVICE',  'ContractedLoad',             NULL, NULL, NULL),
('billing_type',          120, 'CONST',    'kWh',                        NULL, NULL, NULL),
('opening_kwh',           130, 'PROC_OLD', 'Meter Reading(kWh)',         NULL, NULL, N'one month before push date'),
('opening_kvah',          140, 'PROC_OLD', 'Meter Reading(kVAh)',        NULL, NULL, N'one month before push date'),
('closing_kwh',           150, 'PROC_NEW', 'Meter Reading(kWh)',         NULL, NULL, N'push date'),
('closing_kvah',          160, 'PROC_NEW', 'Meter Reading(kVAh)',        NULL, NULL, N'push date'),
('status',                170, 'CONST',    '01',                         NULL, NULL, NULL),
('noof_months',           180, 'CONST',    '1',                          NULL, NULL, NULL),
('billing_status',        190, 'CONST',    '01',                         NULL, NULL, NULL),
('consumed_units',        200, 'CALC',     'closing_kwh-opening_kwh',    NULL, NULL, N'closing - opening, 2 decimals'),
('energy_consumed_amount',210, 'CONST',    '0', NULL, NULL, NULL),
('fixed_charges',         220, 'CONST',    '0', NULL, NULL, NULL),
('minimum_charges',       230, 'CONST',    '0', NULL, NULL, NULL),
('customer_charges',      240, 'CONST',    '0', NULL, NULL, NULL),
('total_deduction_amount',250, 'CONST',    '0', NULL, NULL, NULL),
('ed_charges',            260, 'CONST',    '0', NULL, NULL, NULL),
('op_balance',            270, 'CONST',    '0', NULL, NULL, NULL),
('adj_charges',           280, 'CONST',    '0', NULL, NULL, NULL),
('closing_balance',       290, 'CONST',    '0', NULL, NULL, NULL),
('old_finkwh',            300, 'CONST',    '0', NULL, NULL, NULL),
('old_finkvah',           310, 'CONST',    '0', NULL, NULL, NULL),
('old_rmd',               320, 'CONST',    '0', NULL, NULL, NULL),
('meter_chgdt',           330, 'CONST',    '',  NULL, NULL, N'empty = element not sent'),
('diff_amt',              340, 'CONST',    '0', NULL, NULL, NULL),
('diff_amt_bldt',         350, 'CONST',    '',  NULL, NULL, N'empty = element not sent'),
('diff_amt_sentdt',       360, 'CONST',    '',  NULL, NULL, N'empty = element not sent'),
('username',              370, 'CONST',    '{SOAP_USERNAME}', NULL, NULL, NULL),
('password',              380, 'CONST',    '{SOAP_PASSWORD}', NULL, NULL, NULL);
GO

-- the monthly mapping now lives in PushConfig_Monthly; remove the first-draft rows from PushConfig
DELETE FROM dbo.PushConfig WHERE Operation = 'setMonthly';
GO

SET NOEXEC OFF;
GO
SELECT COUNT(*) AS monthly_params FROM dbo.PushConfig_Monthly;
