# NeoReports.Abstractions

Public contracts and types for [NeoReports](https://github.com/thiagoluga/NeoReports) — the
frozen, typed-only ABI that sources, formats, destinations and jobs are built against.

This package has no dependencies beyond `Microsoft.Extensions.Logging.Abstractions`. You normally
depend on it only when **authoring a plugin** (a custom source/format/destination); applications
reference [`NeoReports.Core`](https://www.nuget.org/packages/NeoReports.Core) instead.

## What's inside

- Schema: `ColumnType`, `ReportColumn`, `ReportSchema`
- Data: `ReportBatch<T>`
- Sources: `IReportSource`, `IBatchSource<T>`, `IStreamingSource<T>`, `BatchContext`, `BatchResult<T>`
- Formats: `IReportWriter`, `WriterContext`
- Destinations: `IReportDestination`, `ReportFile`, `DestinationContext`, `UploadResult`
- Resilience: `IFailureStrategy`, `BatchFailureContext`, `FailureDecision`, `FailureAction`
- Jobs: `IReportJobScheduler`, `IJobStore`, `ReportJob`, `ReportJobStatus`, `JobStats`, `ICheckpointStore`
- Extensibility: `ISourceFactory`, `IWriterFactory`, `IDestinationFactory`
- Exceptions: `NeoReportsException` and friends

## License

MIT © NeoReports Contributors
