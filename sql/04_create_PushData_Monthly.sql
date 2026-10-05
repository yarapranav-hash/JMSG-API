-- dbo.PushData_Monthly: one row per (push date, service) with everything that was sent (or built, for skipped services).
-- Written by MeterPush after you type "yes". A rerun for the same push date and service updates the row.
-- Parameter columns hold the value exactly as sent (quoted values include their single quotes); NULL = element not sent.
-- The password is never stored, and it is masked in RequestXml.
USE JMSGPILOT;
GO

IF OBJECT_ID('dbo.PushData_Monthly') IS NULL
CREATE TABLE dbo.PushData_Monthly (
    DataId           int IDENTITY(1,1) CONSTRAINT PK_PushData_Monthly PRIMARY KEY,
    PushDate         date          NOT NULL,
    ServiceNo        varchar(30)   NOT NULL,
    UniqueServiceNo  varchar(30)   NULL,
    Outcome          varchar(10)   NOT NULL,   -- OK | FAILED | SKIPPED
    Reply            varchar(500)  NULL,       -- API reply, or the skip reason
    PushedOn         datetime      NOT NULL CONSTRAINT DF_PushData_Monthly_PushedOn DEFAULT GETDATE(),
    -- readings used by the checks (not sent)
    OpeningKwh       decimal(18,2) NULL,
    OpeningReadDate  date          NULL,       -- day the opening reading came from
    MdReadDate       date          NULL,       -- day the rmd_kva / rmd_kwh reading came from
    -- the request parameters, in WSDL order
    ukscno varchar(100) NULL, meter_no varchar(100) NULL, meter_phase varchar(100) NULL, tariff_category varchar(100) NULL,
    date_time varchar(100) NULL, op_rdgdt varchar(100) NULL, bill_date varchar(100) NULL, bill_processdt varchar(100) NULL,
    rmd_kva varchar(100) NULL, rmd_kwh varchar(100) NULL, contract_load varchar(100) NULL, billing_type varchar(100) NULL,
    opening_kwh varchar(100) NULL, opening_kvah varchar(100) NULL, closing_kwh varchar(100) NULL, closing_kvah varchar(100) NULL,
    status varchar(100) NULL, noof_months varchar(100) NULL, billing_status varchar(100) NULL, consumed_units varchar(100) NULL,
    energy_consumed_amount varchar(100) NULL, fixed_charges varchar(100) NULL, minimum_charges varchar(100) NULL,
    customer_charges varchar(100) NULL, total_deduction_amount varchar(100) NULL, ed_charges varchar(100) NULL,
    op_balance varchar(100) NULL, adj_charges varchar(100) NULL, closing_balance varchar(100) NULL,
    old_finkwh varchar(100) NULL, old_finkvah varchar(100) NULL, old_rmd varchar(100) NULL, meter_chgdt varchar(100) NULL,
    diff_amt varchar(100) NULL, diff_amt_bldt varchar(100) NULL, diff_amt_sentdt varchar(100) NULL,
    scno varchar(100) NULL, username varchar(100) NULL,
    RequestXml       nvarchar(max) NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PushData_Monthly' AND object_id = OBJECT_ID('dbo.PushData_Monthly'))
CREATE UNIQUE INDEX UX_PushData_Monthly ON dbo.PushData_Monthly (PushDate, ServiceNo);
GO
