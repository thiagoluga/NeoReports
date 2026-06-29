using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using NeoReports.Samples.SqlToCsvLocal;
using NeoReports.Sources.Sql;
using static NeoReports.Core.Building.ReportColumns;

// Sample 01 — SQL Server -> CSV -> local filesystem (the first end-to-end report).
//
// Run against any SQL Server with a Sales table:
//   dotnet run --project samples/01-sql-to-csv-local -- "<connection-string>"
//
// Expected schema: Sales(Id BIGINT, Customer NVARCHAR, Amount DECIMAL, Date DATETIME2).

var connectionString = args.Length > 0
    ? args[0]
    : "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True";

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddReport<Sale>("monthly-sales", b => b
    .From(Source.Sql(
            connectionString,
            "SELECT Id, Customer, Amount, Date FROM Sales " +
            "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
        .Keyset<Sale, long>(v => v.Id, pageSize: 1000))
    .Filter(v => v.Amount > 0)
    .Columns(
        Col<Sale, long>(v => v.Id, "Sale ID"),
        Col<Sale, string>(v => v.Customer, "Customer"),
        Col<Sale, decimal>(v => v.Amount, "Amount", format: "C2", culture: "pt-BR"),
        Col<Sale, DateTime>(v => v.Date, "Sale Date", format: "yyyy-MM-dd"))
    .To(Format.Csv(o => o.Delimiter(';').Encoding(Encoding.UTF8)))
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}")));

var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IReportRunner>();

var result = await runner.RunAsync("monthly-sales");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.RemotePath} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;
