# NeoReports.Jobs

Background job execution for [NeoReports](https://github.com/thiagoluga/NeoReports): the shared
`ReportJobWorker`, an in-memory job store and scheduler, and a no-op checkpoint store.

Use this for dev/tests, or as the base for the Hangfire backend
([`NeoReports.Jobs.Hangfire`](https://www.nuget.org/packages/NeoReports.Jobs.Hangfire)).

## Usage

```csharp
using NeoReports.Jobs.DependencyInjection;

services.AddReport<Sale>("monthly-sales", b => ...);
services.AddNeoReportsInMemoryJobs();

// then
var scheduler = provider.GetRequiredService<IReportJobScheduler>();
var jobId = await scheduler.EnqueueAsync(new ReportJobRequest("monthly-sales"), ct);
```

## License

MIT © NeoReports Contributors
