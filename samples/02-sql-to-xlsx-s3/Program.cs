using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.S3;
using NeoReports.Sources.Sql;
using static NeoReports.Core.Building.ReportColumns;
// Import the format entry methods directly so Csv(...) and Xlsx(...) read cleanly and avoid the
// Format class-name collision between the two format packages (ADR D16).
using static NeoReports.Formats.Csv.Format;
using static NeoReports.Formats.Xlsx.Format;

// Sample 02 — SQL Server -> CSV + XLSX (single pass) -> Amazon S3.
//
// Reads the source once and writes BOTH formats, then uploads each to S3.
// Requires AWS credentials/region in the environment and an existing bucket:
//   dotnet run --project samples/02-sql-to-xlsx-s3 -- "<connection-string>" "<bucket>"

var connectionString = args.Length > 0
    ? args[0]
    : "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True";
var bucket = args.Length > 1 ? args[1] : "my-reports-bucket";

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
    .To(Csv(o => o.Delimiter(';').Encoding(Encoding.UTF8)))
    .To(Xlsx(o => o.SheetName("Sales").AutoFilter()))
    .UploadTo(Destination.S3(bucket, "reports/{name}/{date:yyyy-MM-dd}.{ext}")));

var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IReportRunner>();

var result = await runner.RunAsync("monthly-sales");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.Url} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;

internal sealed record Sale(long Id, string Customer, decimal Amount, DateTime Date);
