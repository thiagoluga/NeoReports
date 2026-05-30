# NeoReports

[![CI](https://github.com/thiagoluga/NeoReports/actions/workflows/ci.yml/badge.svg)](https://github.com/thiagoluga/NeoReports/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-512BD4)](https://dotnet.microsoft.com/)

**NeoReports** is an open-source .NET library for generating reports from data
sources, with a typed fluent API, constant-memory streaming, resilience, and
upload to destinations.

> **Status:** v1 is an MVP under active development by a single maintainer. The
> public contract (`NeoReports.Abstractions`) is treated as a frozen ABI; see the
> architecture notes before depending on internals.

## Why NeoReports

- **Typed, code-first.** A report is defined in C# over your own POCO — no
  dictionaries, no string-keyed rows, full compile-time safety.
- **Constant memory.** Sources are read in batches and streamed straight to the
  output; nothing materializes the whole report in memory (CSV is fully
  streaming).
- **Resilient.** Batch reads are wrapped in [Polly](https://github.com/App-vNext/Polly)
  retries, with a pluggable failure strategy (abort or skip-and-log) and
  escalation thresholds.
- **Pluggable.** Sources, formats, and destinations are small, independent
  packages.

## Packages (v1)

| Area | Package | Description |
|------|---------|-------------|
| Contracts | `NeoReports.Abstractions` | Public, frozen contract (typed-only) |
| Engine | `NeoReports.Core` | Fluent builder, pipeline, projection, resilience, DI |
| Source | `NeoReports.Sources.Sql` | SQL Server with keyset pagination |
| Format | `NeoReports.Formats.Csv` | Streaming CSV writer (RFC 4180) |
| Format | `NeoReports.Formats.Xlsx` | XLSX writer (ClosedXML) |
| Destination | `NeoReports.Destinations.Local` | Local filesystem (atomic publish) |
| Destination | `NeoReports.Destinations.S3` | Amazon S3 (all-or-nothing upload) |

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Sources.Sql;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

public sealed record Venda(long Id, string Cliente, decimal Valor, DateTime Data);

var services = new ServiceCollection();
services.AddLogging();

services.AddReport<Venda>("vendas-mensal", b => b
    .From(Source.Sql(connectionString,
            "SELECT Id, Cliente, Valor, Data FROM Vendas " +
            "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
        .Keyset<Venda, long>(v => v.Id, pageSize: 1000))
    .Filter(v => v.Valor > 0)
    .Columns(
        Col<Venda, long>(v => v.Id, "ID Venda"),
        Col<Venda, string>(v => v.Cliente, "Cliente"),
        Col<Venda, decimal>(v => v.Valor, "Valor", format: "C2", culture: "pt-BR"),
        Col<Venda, DateTime>(v => v.Data, "Data Venda", format: "yyyy-MM-dd"))
    .To(Csv(o => o.Delimiter(';')))
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}")));

var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IReportRunner>();
var result = await runner.RunAsync("vendas-mensal");
```

Runnable end-to-end samples live in [`samples/`](samples/).

## Building from source

Requires the .NET 8 and .NET 9 SDKs.

```bash
dotnet build          # build the solution
dotnet test           # run all tests (SQL integration tests need Docker)
dotnet format         # apply the .editorconfig style
```

The SQL integration tests use [Testcontainers](https://dotnet.testcontainers.org/)
and require a running Docker daemon; they skip automatically when Docker is
unavailable.

## Contributing

Contributions are welcome — please read [CONTRIBUTING.md](CONTRIBUTING.md) and
the [Code of Conduct](CODE_OF_CONDUCT.md). Security issues should follow
[SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © NeoReports Contributors
