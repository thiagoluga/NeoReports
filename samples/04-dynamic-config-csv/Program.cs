using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using NeoReports.Samples.DynamicConfigCsv;

// Sample 04 — config-driven report (the dynamic path), wired through DI.
//
// The whole report is defined in report.json, with no typed POCO. Rows flow through the same
// pipeline as the typed path, as positional ReportRecords. The JSON fully drives the report name,
// the source selection by id, the columns and schema (name, type, header, format and culture), a
// JsonLogic filter, and the selection of outputs and destinations by id.
//
// AddReportFromConfigFile registers the report; it is then runnable by name like any code-first
// report. The SQL config source (sample 05) swaps only the source section; here an in-memory source
// provider supplies the rows. Binding format/destination options from config is still a later step,
// so the CSV and Local factories are pre-wired in DI.
//
// Run with: dotnet run --project samples/04-dynamic-config-csv

var configPath = Path.Join(AppContext.BaseDirectory, "report.json");

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddReportFromConfigFile(configPath);                                   // register the config report
services.AddSingleton<IConfigSourceProvider, InMemorySalesSourceProvider>();    // source  "inmemory"
services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));  // format  "csv"
services.AddSingleton<IDestinationFactory>(
    new LocalDestinationFactory("./out/{name}-{date:yyyy-MM-dd}.{ext}"));       // dest    "local"

await using var provider = services.BuildServiceProvider();

var runner = provider.GetRequiredService<IReportRunner>();
var result = await runner.RunAsync("monthly-sales");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.RemotePath} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;
