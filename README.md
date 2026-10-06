# MeterPush: how the monthly push works

MeterPush is a .NET console app. It reads meter data from the `JMSGPILOT` SQL Server database and sends it, one service at a time, to the `MeterReadingService` SOAP API (`setMonthly`).

This document describes the **monthly push (`setMonthly`)**, which is finished and tested. The hourly push (`setHourly`) is still a first draft and is described at the end.

---

## 1. Folder layout

```
winamr/
  README.md                       this file
  MeterPush/                      the app
    Program.cs                    command-line options, hourly push (draft), starts the monthly push
    MonthlyPush.cs                the whole monthly push (fetch, filter, build, send, log)
    Soap.cs                       builds the SOAP envelope, posts it, classifies the reply
    appsettings.json              SQL Server connection string
  sql/
    01_create_PushConfig.sql          global settings + hourly draft mapping
    02_create_PushConfig_Monthly.sql  monthly mapping + push log tables
    03_monthly_quote_and_ukscno.sql   Quote column (its ukscno part is superseded: ukscno is now UniqueServiceNo)
    03_monthly_max_increase.sql       120% rule setting
    04_create_PushData_Monthly.sql    table with the values sent / skipped per service
  preview/                        CSV previews and a sample request for Postman
```

## 2. How to run

Run everything from the `MeterPush` folder:

```powershell
cd C:\Users\ecil\Desktop\winamr\MeterPush

dotnet build -c Release                                              # once, after any code change
```

**1. Preview only (no API call, nothing written to the log):**
```powershell
dotnet run -c Release --no-build -- --op monthly --dry-run --date 2026-10-01 --csv ..\preview\monthly_preview_2026-10-01.csv
```
It writes the preview file `monthly_preview_2026-10-01.csv` (one row per record to be pushed, with exactly the request parameters as columns, password hidden; services already sent OK are left out) and `monthly_preview_2026-10-01_skipped.csv` (skipped services with the reason), prints the counts and one sample request. Excel shows `01` as `1`; open the CSV in Notepad to see the real value. It then stops. Open the CSV and check it.

**2. Push to the API (after you checked the preview):**
```powershell
dotnet run -c Release --no-build -- --op monthly --date 2026-10-01 --limit 1   # first test: ONE real service
dotnet run -c Release --no-build -- --op monthly --date 2026-10-01             # everything
```
Without `--dry-run`/`--csv` the app writes the preview file first (default `MeterPush\preview\monthly_<date>.csv`) and then **sends straight away**, with no confirmation. To look at the data without sending, use the preview-only command above, or start with `--limit 1`.

| Option | Meaning |
|---|---|
| `--op monthly` | Run only the monthly push. Always include it for now, because without it the draft hourly push runs too. |
| `--dry-run` | Build the preview and print one sample request. Nothing is sent and nothing is written to the database. |
| `--csv file.csv` | Where to write the preview (and `file_skipped.csv`). Implies `--dry-run`. |
| `--limit N` | Preview and send at most N records. Use `1` for a first test. |
| `--date yyyy-MM-dd` | The push date. The default is today. |
| `--ignore-log` | Also send the services already logged OK for the push date (resend everything). Their log and data rows are updated with the new reply and values. |

The exit code is `0` if no service failed and `1` if any failed.

## 3. Step by step: what a monthly run does

### Step 1: Load the configuration
- `dbo.PushConfig` rows with `Operation = 'GLOBAL'` give the API endpoint, the SOAP username and password, the HTTP timeout and `MONTHLY_MAX_INCREASE_PCT` (default 120).
- `dbo.PushConfig_Monthly` gives the mapping: one row per `setMonthly` parameter, sorted by `SortOrder` (the WSDL order). See section 5.

### Step 2: Work out the two dates
- **Push date** is today, or the `--date` value.
- **Opening date** is the push date minus 1 month. For example, push date 2026-10-01 gives opening date 2026-09-01.

### Step 3: Read the meter readings (stored procedure)
All the dates the run may need (push date, opening date plus `OPENING_FORWARD_DAYS` days after it, last day of the previous month plus `MD_LOOKBACK_DAYS` days before it) are requested **in parallel**, each call on its own connection, `PROC_PARALLELISM` (default 5) at a time. The console prints a line when each call starts and when it returns, with the number of services and the seconds it took. The older description of two calls below is the core of it.

The app calls `dbo.PrBLSTotalDCWiseMeterDataInstantDay @Date, @DC = NULL, @stmt = 'ALL'`:
- Once for the push date. This is the **NEW** reading, used for the closing values, `meter_no`, `rmd_kva`, `rmd_kwh` and `date_time`.
- Once for the opening date. This is the **OLD** reading, used for the opening values.

The procedure is the same one the existing UI uses. Each call takes roughly 20 to 40 seconds. What the procedure does:
- It finds the active meters on **feeders 6, 11 and 19** (`ServiceDetails.FeederId IN (6,11,19)` and `ServiceDetails.IsActive = 1`), through `ModemDetails` and `LogDCDetails`.
- It reads each meter's own table, which is named after the modem IMEI, for readings on that calendar day. It joins `ServiceDetails`, `DCDetails`, `ConsumerDetails`, `MeterDetails`, `MeterModel` and `StatusDetails`.
- It keeps readings where the phase current and `MDkW` are not null.
- It returns **one row per service** for that day.
- It returns these columns: `DC SNo`, `Service No`, `MSN`, `Meter Reading(kWh)`, `Meter Reading(kVAh)`, `MD kW`, `MD kVA`, `Date & Time` and others.

The app keeps the results in memory, keyed by `Service No`.

### Step 4: Read the service master data
From `dbo.ServiceDetails` (`IsActive = 1`) the app loads `ServiceNo`, `UniqueServiceNo` and `ContractedLoad`. It joins them to the procedure rows on `ServiceNo`. If a `ServiceNo` appears twice, it keeps the row with a usable 9-digit unique number.

### Step 5: Load what is already sent
From `dbo.PushLog_Monthly` it loads the services with `Status = 'OK'` for this push date, so they are not sent twice.

### Step 6: Go through each service, in `ServiceNo` order
Each service in the push-date result is checked in this order. The first check that fails **skips** the service. The reason is listed in the `_skipped.csv` preview, and in a real run it is written to `PushLog_Monthly` with `Status = 'SKIPPED'`.

The opening and closing kWh are read from the procedure only for checks 6 and 7. The real opening reading is **not sent** (`opening_kwh` and `opening_kvah` are fixed `0`), but `consumed_units` is sent: closing kWh minus the opening kWh reading.

| # | Check | Skip reason in the log |
|---|---|---|
| 1 | Already logged OK for this push date | not sent again, and left out of the preview |
| 2 | The service is not in `ServiceDetails` with `IsActive = 1` | `service not found (active) in ServiceDetails` |
| 3 | `ServiceDetails.UniqueServiceNo` is not exactly 9 digits (or empty) | `no valid 9-digit UniqueServiceNo in ServiceDetails` |
| 4 | The service has no reading on the opening date or (and, if `OPENING_FORWARD_DAYS` is above 0, on that many following days; it is 0 now, so only the opening date is checked) | `no reading for <opening date> (opening)` |
| 5 | A mapped value can't be built (missing column), or the opening/closing kWh is missing | the error message / `opening or closing kWh reading missing` |
| 6 | Closing is lower than opening, for example a replaced meter | `closing < opening (consumed -x) - meter replaced/rolled over?` |
| 7 | **The 120% rule:** (closing_kwh - opening_kwh) / opening_kwh x 100 is greater than `MONTHLY_MAX_INCREASE_PCT` | `increase x% > 120% (opening ..., closing ...)` |

| 8 | `rmd_kva` or `rmd_kwh` is greater than `MONTHLY_MAX_MD` (default 6) | `rmd_kva 28592807.76 > 6` |

A service with no MD value (no reading on the allowed days) is skipped before these checks, with the reason `no rmd_kva/rmd_kwh reading on <date> or the N day(s) before`.

About the 120% rule (check 7):
- The services sent are those with an increase of **120% or less**.
- If `opening_kwh` is 0 and closing is above 0, the increase is unbounded, so the service is **not sent**.
- If both readings are 0, the increase is 0% and the service is sent.

### Step 7: Build the values
Each mapped parameter is filled according to its `SourceType`. See section 5.

### Step 8: Build the request, write the preview, send
- The preview file is written first and the records are then sent without a confirmation (see section 2).
- The values are put in a SOAP 1.1 `setMonthly` envelope (namespace `http://service.tg.spd`), in `SortOrder`.
- Elements with an empty value are left out.
- Parameters with `Quote = 1` are wrapped in single quotes (`'A08798'`). The API puts the values straight into its own SQL, so text values need the quotes.
- It is one HTTP POST per service. **The app waits for each reply before sending the next one**, at about 5 services per second.
- The request goes to the `ENDPOINT` setting with the header `SOAPAction: "urn:setMonthly"`.

### Step 9: Read the reply and log it
The API replies with HTTP 200 even when its own insert fails, so the app reads the text in `<return>`:

| Reply | Treated as | Logged as |
|---|---|---|
| `Successful` | accepted | `OK` |
| `Record already updated` | accepted (the record already exists) | `OK` |
| Starts with `Un-Successful`, `fail` or `error`, or contains `ORA-nnnnn` | failed | `FAILED` |
| SOAP Fault, non-200 HTTP code, timeout or network error | failed | `FAILED` |
| Anything else | accepted | `OK`, with the text in `Detail` |

- Every reply is written to `PushLog_Monthly.Detail`.
- The console prints one line per service, for example `0216 00011 (021600011) -> Successful`.
- After 5 failures in a row the app stops. Rerunning the same command retries the failed services and skips the OK ones.
- At the end it prints a count per reply and `done - accepted X, skipped Y, failed Z`.

## 4. Tables

| Table / object | Used for |
|---|---|
| `dbo.PrBLSTotalDCWiseMeterDataInstantDay` (stored procedure) | Meter readings for a given date (NEW and OLD). Reads the per-IMEI meter tables and several master tables. |
| `dbo.ServiceDetails` | `ServiceNo` (to match the procedure result), `UniqueServiceNo` (sent as `ukscno`), `ContractedLoad`. Only rows with `IsActive = 1`. |
| `dbo.PushConfig` | Global settings: endpoint, SOAP username and password, timeout, `MONTHLY_MAX_INCREASE_PCT`. Also holds the draft hourly mapping. |
| `dbo.PushConfig_Monthly` | The parameter mapping for `setMonthly`. |
| `dbo.PushLog_Monthly` | One row per service and push date: `OK`, `FAILED` or `SKIPPED`, with the reply or reason. |
| `dbo.PushData_Monthly` | One row per service and push date with every parameter value as sent (or as built, for skipped services), the reply or skip reason, the opening kWh and the dates the opening and MD readings came from, and the request XML (password masked). Written during a real run (not by `--dry-run`/`--csv`). Created by `sql\04_create_PushData_Monthly.sql`. |

`PushLog_Monthly` has a unique key on `(PushDate, ServiceNo)`. A rerun updates the row instead of adding a new one.

## 5. Where each `setMonthly` value comes from

`SourceType` values in `PushConfig_Monthly`:
`PROC_NEW` = column of the procedure result for the push date, `PROC_OLD` = column for the opening date, `PROC_PREVEND` = column for the last day of the previous month (a third procedure call), `SERVICE` = column of `ServiceDetails`, `PUSHDATE` = push date shifted by `DateOffsetMonths`, `CALC` = a subtraction of two other parameters, `CONST` = fixed text (empty means the element is not sent).

| Parameter | Source | Notes |
|---|---|---|
| `ukscno` | `ServiceDetails.UniqueServiceNo` | The 9-digit unique number, for example `101463320` |
| `meter_no` | `PROC_NEW` `MSN` | Sent in single quotes: `'A08798'` |
| `meter_phase` | `CONST` `1` | |
| `tariff_category` | `CONST` `1` | |
| `date_time` | `PROC_NEW` `Date & Time` | Format `dd/MM/yyyy hh:mm:ss tt`, for example `01/10/2026 08:30:00 PM` |
| `op_rdgdt` | push date minus 1 month | `dd/MM/yyyy` |
| `bill_date` | push date | `dd/MM/yyyy` |
| `bill_processdt` | push date | `dd/MM/yyyy` |
| `rmd_kva` | `PROC_PREVEND` `MD kVA` | From the last day of the month before the push date (push 2026-10-01 -> 2026-09-30), latest reading that day. `MD_LOOKBACK_DAYS` (now 1) says how many earlier days are searched when a meter has no reading that day (09-30, then 09-29). **A meter with no MD value after that is skipped** (`no rmd_kva/rmd_kwh reading on 2026-09-30 or the 1 day(s) before`). Left out if none is found. |
| `rmd_kwh` | `PROC_PREVEND` `MD kW` | Same date logic as `rmd_kva`. The procedure has no MD kWh, so MD kW is used |
| `contract_load` | `ServiceDetails.ContractedLoad` | Without trailing zeros: `1.00` is sent as `1` |
| `billing_type` | `CONST` `kWh` | Not quoted |
| `opening_kwh` | `CONST` `0` | Fixed `0`; the real opening reading is only used for the checks and `consumed_units` |
| `opening_kvah` | `CONST` `0` | |
| `closing_kwh` | `PROC_NEW` `Meter Reading(kWh)` | Reading on the push date |
| `closing_kvah` | `PROC_NEW` `Meter Reading(kVAh)` | Reading on the push date |
| `status` | `CONST` `01` | |
| `noof_months` | `CONST` `1` | |
| `billing_status` | `CONST` `01` | |
| `consumed_units` | `CALC` `closing_kwh - opening_kwh_reading` | Closing kWh minus the opening kWh reading (not sent itself), rounded to 2 decimals, e.g. `53.25` |
| `energy_consumed_amount`, `fixed_charges`, `minimum_charges`, `customer_charges`, `total_deduction_amount`, `ed_charges`, `op_balance`, `adj_charges`, `closing_balance` | `CONST` `0` | No source in the DB |
| `old_finkwh`, `old_finkvah`, `old_rmd`, `diff_amt` | `CONST` `0` | |
| `meter_chgdt`, `diff_amt_bldt`, `diff_amt_sentdt` | `CONST` empty | The elements are not sent |
| `scno` | `ServiceDetails.ServiceNo` | Spaces removed: `0216 00003` becomes `021600003`; not quoted |
| `username`, `password` | `PushConfig` global settings | `SOAP_USERNAME`, `SOAP_PASSWORD` |

**Changing a mapping needs no code change.** Update the row in `PushConfig_Monthly`, for example:
```sql
UPDATE dbo.PushConfig_Monthly SET SourceValue = '2' WHERE ParamName = 'tariff_category';
UPDATE dbo.PushConfig_Monthly SET Quote = 0       WHERE ParamName = 'meter_no';
UPDATE dbo.PushConfig       SET SourceValue = '150' WHERE ParamName = 'MONTHLY_MAX_INCREASE_PCT';
```
A new `SERVICE` column also needs a small addition in `MonthlyPush.FromService`.

## 6. Checking the results (SSMS, database `JMSGPILOT`)

```sql
-- totals by status
SELECT Status, COUNT(*) AS cnt FROM dbo.PushLog_Monthly
WHERE PushDate = '2026-10-01' GROUP BY Status;

-- the API's replies and how many of each
SELECT Detail, COUNT(*) AS cnt FROM dbo.PushLog_Monthly
WHERE PushDate = '2026-10-01' AND Status = 'OK' GROUP BY Detail ORDER BY cnt DESC;

-- failures and skips with the reason
SELECT ServiceNo, Status, Detail FROM dbo.PushLog_Monthly
WHERE PushDate = '2026-10-01' AND Status <> 'OK' ORDER BY Status, Detail, ServiceNo;
```

To send a skipped or failed service again after fixing the cause, delete its log row (or set it to `FAILED`) and rerun the same command:
```sql
DELETE FROM dbo.PushLog_Monthly WHERE PushDate = '2026-10-01' AND ServiceNo = '0216 00003';
```

## 7. Assumptions and known limits

- **`ukscno` is `ServiceDetails.UniqueServiceNo`.** Services without a valid 9-digit value are skipped. Some values are duplicated or junk (for example `123456789`), so check the preview.
- **The 5,427 records pushed on 2026-10-01 used the old `ukscno` (service number without spaces) and the old field set.** They are marked OK in `PushLog_Monthly`, so a rerun skips them until their log rows are deleted.
- **`rmd_kwh` is the procedure's `MD kW`**, because there is no MD kWh column.
- **Opening is the reading one month before the push date, closing is the push-date reading**, but only the closing values are sent. The opening values are used for the skip checks.
- **Only feeders 6, 11 and 19 are covered**, because the stored procedure filters on them.
- **A service with no reading on the opening date can't be sent** (about 290 on 2026-10-01).
- **"Record already updated" is logged as OK.** The app does not tell you whether that run changed anything on the server.
- **Not implemented:** a rule to change `status` to 9 when `consumed_units` is between -1 and +1. It was discussed but not added.
- **The reading time varies.** `date_time` is the time of each meter's latest reading that day, so it is different for each meter.
- **Credentials:** the SQL Server password is stored as plain text in `appsettings.json`, and the SOAP password is in `dbo.PushConfig`. Restrict access to both.

## 8. The hourly push (`setHourly`), draft only

The hourly mapping is still the first draft. It lives in `dbo.PushConfig` (`Operation = 'setHourly'`): a stored `SOURCE_QUERY` on `dbo.PeriodicalDataLive`, plus one row per field, with a `LAST_ID` watermark. It will be replaced by a `PushConfig_Hourly` table once the final field mapping is decided. The API's quoting rules for text fields (as for `meter_no` above) have not been tested for it. Do not run it against the live API yet.
