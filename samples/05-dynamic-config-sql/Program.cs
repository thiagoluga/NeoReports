using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using NeoReports.Sources.Sql;

// Sample 05 — config-driven report reading from SQL Server (dynamic path + A3), wired through DI.
//
// The whole report is defined in report.json, with a "sql" source and no typed POCO. Point it at a
// SQL Server that has a Sales(Id BIGINT, Customer NVARCHAR, Amount DECIMAL, Date DATETIME2) table:
//
//   dotnet run --project samples/05-dynamic-config-sql -- "<connection-string>"
//
// This is sample 04 with one change: the source section is "sql" instead of "inmemory", registered
// with AddSqlConfigSource(). The report is then runnable by name like any code-first report.

var connectionString = args.Length > 0
    ? args[0]
    : "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True";

// Inject the connection string (JSON-escaped) into the placeholder, so no secret is committed to
// report.json. Path.Join never drops earlier segments.
var configPath = Path.Join(AppContext.BaseDirectory, "report.json");
var configJson = (await File.ReadAllTextAsync(configPath))
    .Replace("\"__CONNECTION_STRING__\"", JsonSerializer.Serialize(connectionString), StringComparison.Ordinal);

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddReportFromConfig(configJson);                                       // register the config report
services.AddSqlConfigSource();                                                  // source  "sql"
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
