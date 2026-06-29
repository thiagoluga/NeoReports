# NeoReports.Core

The core engine of [NeoReports](https://github.com/thiagoluga/NeoReports): the fluent
`ReportBuilder<TRow>`, report registry and DI (`AddReport<TRow>`), the batch pipeline with a
compiled `T → object?[]` projection at the writer edge, Polly v8 resilience, and the
`IFailureStrategy` (abort / skip-and-log) with escalation thresholds.

Add a source, a format and a destination package alongside this one to build a working report.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Sources.Sql;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

public sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);

var services = new ServiceCollection();
services.AddLogging();

services.AddReport<Sale>("monthly-sales", b => b
    .From(Source.Sql(connectionString,
            "SELECT Id, Customer, Amount, Date FROM Sales " +
            "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
        .Keyset<Sale, long>(v => v.Id, pageSize: 1000))
    .Filter(v => v.Amount > 0)
    .Columns(
        Col<Sale, long>(v => v.Id, "Sale ID"),
        Col<Sale, string>(v => v.Customer, "Customer"),
        Col<Sale, decimal>(v => v.Amount, "Amount", format: "C2", culture: "pt-BR"),
        Col<Sale, DateTime>(v => v.Date, "Sale Date", format: "yyyy-MM-dd"))
    .To(Csv(o => o.Delimiter(';')))
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}")));

var provider = services.BuildServiceProvider();
var result = await provider.GetRequiredService<IReportRunner>().RunAsync("monthly-sales");
```

## License

MIT © NeoReports Contributors
