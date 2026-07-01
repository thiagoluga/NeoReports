using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Samples.MultiSheetXlsx;
using static NeoReports.Xlsx.Pro.Format;

// Sample 06 — multi-sheet XLSX workbook (the Pro feature).
//
// One .xlsx with an "Approved" and a "Rejected" worksheet, each with its own filter and columns,
// produced from a single source read. The "Approved" sheet inherits the report's four columns; the
// "Rejected" sheet declares its own two. Uses an in-memory source so it runs with no database.
//
//   dotnet run --project samples/06-multi-sheet-xlsx   ->   writes ./out/monthly-sales-<date>.xlsx
//
// NOTE: NeoReports.Xlsx.Pro is a commercial package (PolyForm Small Business — free under USD 1M
// annual revenue). This sample references it as a project reference for demonstration.

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddReport<Sale>("monthly-sales", b => b
    .From(new InMemorySalesSource(10))
    .Column(v => v.Id, "Sale ID")
    .Column(v => v.Customer, "Customer")
    .Column(v => v.Amount, "Amount", format: "C2", culture: "pt-BR")
    .Column(v => v.Date, "Sale Date", format: "yyyy-MM-dd")
    .ToSections(XlsxWorkbook(o => o.AutoFilter()), s => s
        .Section("Approved", v => v.Where(x => x.Amount > 0))
        .Section("Rejected", v => v
            .Where(x => x.Amount <= 0)
            .Column(x => x.Id, "Sale ID")
            .Column(x => x.Customer, "Customer")))
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}")));

await using var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IReportRunner>();

var result = await runner.RunAsync("monthly-sales");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.RemotePath} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;
