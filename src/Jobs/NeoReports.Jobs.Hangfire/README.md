# NeoReports.Jobs.Hangfire

Hangfire single-server job backend for [NeoReports](https://github.com/thiagoluga/NeoReports).

Runs report jobs on a single Hangfire server, persisting job state across restarts. Cancellation is
cooperative (deleting the Hangfire job trips the injected `CancellationToken`); a crashed job
restarts from zero (idempotent), which the pipeline guarantees by staging output to a temp file and
publishing only at the end.

## Usage

```csharp
using Hangfire;
using NeoReports.Jobs.Hangfire.DependencyInjection;

// Configure Hangfire (single server). Swap UseInMemoryStorage for UseSqlServerStorage in production.
builder.Services.AddHangfire(cfg => cfg.UseInMemoryStorage());
builder.Services.AddHangfireServer();

builder.Services.AddNeoReportsHangfireJobs();
```

## License

MIT © NeoReports Contributors
