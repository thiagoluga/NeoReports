# NeoReports — MVP Spec (v1)

Defines **what v1 delivers, in a testable way**. The architecture is in `DECISIONS.md`; the build order in `PLAN.md` (root).

## Goal

A .NET developer registers a report in code, strongly typed, that reads from a SQL database via keyset pagination, writes to CSV and XLSX with constant memory, and uploads to a destination (Local or S3) — synchronously (direct stream) or asynchronously (background job with a single worker). Batch failures go through retry (Polly) and a failure strategy (Abort or SkipBatchAndLog).

## What v1 does (functional summary)

- Register typed reports via DI: `services.AddReport<TRow>("name", b => ...)`.
- Read from SQL with keyset pagination (`string?` cursor), opening/closing the connection per page.
- `Map` to an output type, `Filter` with a typed C# delegate.
- Declare the column schema (name, type, format, culture) for the output projection.
- Write CSV (delimiter, encoding, header) and XLSX (sheet name, auto-filter) in the same pass.
- Upload to Local (path template) and S3 (bucket/key template).
- Trigger via API: asynchronous (returns `jobId`) or synchronous (stream in the response).
- Query job status and download the result.
- Retry with Polly + `IFailureStrategy` (Abort / SkipBatchAndLog) + thresholds (consecutive / total / ratio).

## Acceptance criteria (each becomes a test)

1. **Typed registration.** `AddReport<Sale>("sales", b => b.From(sql).Map(...).To(Csv).UploadTo(Local))` registers and the report appears in `IReportRegistry`.
2. **Keyset read.** The SQL source reads all pages in order by a key column, without skipping or repeating records; the connection is opened/closed per page.
3. **Constant memory.** Generating a 1,000,000-row report keeps allocation ~constant (BenchmarkDotNet `MemoryDiagnoser`); there is no growth proportional to the total number of rows.
4. **Correct CSV.** CSV output matches a golden file byte-by-byte: configured delimiter and encoding, header with `DisplayName`, values formatted per the schema's culture/format.
5. **Correct XLSX.** The file opens in Excel/ClosedXML, named sheet, active auto-filter, native types (number/date) preserved.
6. **Multi-output in one pass.** CSV + XLSX generated reading the source **only once**.
7. **Local upload.** File appears at the resolved path (`{date}`, `{name}` tokens expanded).
8. **S3 upload.** Object created at the resolved bucket/key; all-or-nothing upload (no partial object on failure).
9. **Async trigger.** `POST /api/reports/{name}/run` creates a `queued` job, returns `jobId`; the worker processes it; status walks `queued → running → completed`.
10. **Sync trigger.** `POST /api/reports/{name}/run?mode=sync` streams the single format in the response with a correct `Content-Disposition`; multi-output in sync returns `400`.
11. **Transient retry.** A source that fails 2x with a transient error and recovers on the 3rd completes the report without data loss.
12. **FailureStrategy Abort.** With `AbortReport()`, a definitive batch failure aborts the report (status `failed`, reason recorded).
13. **FailureStrategy Skip.** With `SkipBatchAndLog()`, a definitively failed batch is skipped, a structured warning is logged, the report completes marked as **partial**.
14. **Threshold.** With `SkipBatchAndLog().AbortIf(t => t.ConsecutiveFailures(3))`, 3 consecutive failures abort even in skip mode.
15. **Cancellation.** `POST /api/jobs/{id}/cancel` makes the job stop cooperatively (status `cancelled`) within a reasonable time.
16. **Idempotent restart.** An interrupted job (killed process) restarts from zero without corrupting the destination (no partial file published).

## v1 REST API

```
POST   /api/reports/{name}/run            # async  → 202 { jobId }
POST   /api/reports/{name}/run?mode=sync  # sync   → 200 stream (single-output)
GET    /api/reports                        # list registered reports
GET    /api/jobs/{id}                       # status + stats
POST   /api/jobs/{id}/cancel                # cancel
GET    /api/jobs/{id}/download              # download the result when complete
```

Report parameters go in the `run` body (`{ "parameters": { "start": "2026-01-01" } }`). Auth inherits from the host (no auth chain in v1).

## Reference report (target of the first end-to-end)

```csharp
public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);

services.AddReport<Sale>("monthly-sales", b => b
    .From(Source.Sql("sales-db",
        "SELECT Id, Customer, Amount, Date FROM Sales WHERE Date >= @start AND Id > @cursor ORDER BY Id")
        .Keyset(v => v.Id, pageSize: 1000))
    .Filter(v => v.Amount > 0)
    .Columns(
        Col(v => v.Id,       "Sale ID"),
        Col(v => v.Customer, "Customer"),
        Col(v => v.Amount,   "Amount",    format: "C2", culture: "pt-BR"),
        Col(v => v.Date,     "Sale Date", format: "yyyy-MM-dd"))
    .To(Format.Csv(o => o.Delimiter(';').Encoding(Encoding.UTF8)))
    .To(Format.Xlsx(o => o.SheetName("Sales").AutoFilter()))
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}"))
    .Retry(r => r.MaxAttempts(5).Exponential(baseDelay: TimeSpan.FromSeconds(2)).WithJitter())
    .OnFailure(f => f.SkipBatchAndLog().AbortIf(t => t.ConsecutiveFailures(3))));
```

This example must compile and run end-to-end — it is the practical definition of "MVP done".

## v1 non-goals

Dynamic path/JSON config, UI, variants, multi-worker, mid-job resume, PDF, SharePoint, auth chain, dynamic expressions. See the full list in `CLAUDE.md` and the ADR.
