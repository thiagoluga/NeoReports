using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using NeoReports.Samples.DynamicConfigCsv;

// Sample 04 — config-driven report (the dynamic path).
//
// The whole report is defined in report.json: there is NO typed POCO. Rows flow through the same
// pipeline as the typed path as positional ReportRecords. What the JSON fully drives today: the
// report name, the source selection (by id), the columns/schema (name, type, header, format,
// culture), and the selection of outputs/destinations (by id).
//
// Standing in for things not built yet:
//   - the SQL config source arrives in A3, so an in-memory IConfigSourceProvider provides the rows;
//   - binding format/destination *options* from config arrives later (A5), so the CSV and Local
//     factories are pre-wired in DI (the JSON's output/destination "properties" are illustrative).
//
//   dotnet run --project samples/04-dynamic-config-csv

var configPath = Path.Combine(AppContext.BaseDirectory, "report.json");
var json = await File.ReadAllTextAsync(configPath);

// 1) Parse the JSON document into a ReportConfig.
var config = new JsonReportConfigParser().Parse(json);

// 2) Register the providers/factories the compiler resolves by stable id.
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddSingleton<IConfigSourceProvider, InMemorySalesSourceProvider>();          // source  "inmemory"
services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));          // format  "csv"
services.AddSingleton<IDestinationFactory>(
    new LocalDestinationFactory("./out/{name}-{date:yyyy-MM-dd}.{ext}"));               // dest    "local"

await using var provider = services.BuildServiceProvider();

// 3) Compile the config into the same runnable report the fluent builder produces.
var report = ReportConfigCompiler.Compile(config, provider);

// 4) Run it.
var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("dynamic");
var exec = new ReportExecutionContext(
    Guid.NewGuid().ToString("N"), config.Name, parameters: null, logger, CancellationToken.None);

var result = await ReportRunner.ExecuteAsync(report, exec, provider, CancellationToken.None);

Console.WriteLine($"Report: {config.Name}");
Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.RemotePath} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;
