# 03 — async jobs over HTTP, via Hangfire

Trigger a report over HTTP instead of running it inline, executed by a single Hangfire server
(`NeoReports.Jobs.Hangfire`), with progress/status tracked through `NeoReports.AspNetCore`'s job
endpoints.

```bash
dotnet run --project samples/03-async-job-hangfire

curl -X POST http://localhost:5000/api/reports/monthly-sales/run            # async -> { jobId }
curl      http://localhost:5000/api/jobs/{jobId}                            # status
curl -OJ  http://localhost:5000/api/jobs/{jobId}/download                   # result
curl -X POST "http://localhost:5000/api/reports/monthly-sales/run?mode=sync" -o out.csv
```

Expects a `Sales(Id BIGINT, Customer NVARCHAR, Amount DECIMAL, Date DATETIME2)` table (same schema
as sample 01), reachable via the `ConnectionStrings:Sales` config value or the same
`Server=localhost;Database=Sales;...` default the other SQL samples fall back to.

How it works:

- `AddHangfire(cfg => cfg.UseInMemoryStorage())` + `AddHangfireServer()` run a single in-process
  Hangfire worker — no external dependency to try the sample, but swap `UseInMemoryStorage()` for
  `UseSqlServerStorage(...)` for real single-server persistence across restarts.
- `AddNeoReportsHangfireJobs()` wires the engine's job abstractions onto that Hangfire server;
  `AddNeoReportsArtifacts()` retains completed output so `/download` has a file to serve after the
  pipeline's own per-job temp file is gone.
- `MapNeoReports("/api")` exposes the trigger/status/download endpoints — `?mode=sync` runs inline
  and streams the result directly instead of returning a job id to poll.
- `Sale` is the shared row type from `NeoReports.Samples.Shared`.
