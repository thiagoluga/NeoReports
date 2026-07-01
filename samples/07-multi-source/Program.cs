using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Samples.MultiSource;
using NeoReports.Sources.Join.Pro;
using static NeoReports.Formats.Csv.Format;

// Sample 07 — multi-source report via keyset merge-join (the Pro feature).
//
// Two sources ordered by the same key (customers by Id, orders by CustomerId) are merged into one
// report: each customer with its order count and total. LeftOuter keeps customers that have no
// orders. In-memory sources so it runs with no database.
//
//   dotnet run --project samples/07-multi-source   ->   writes ./out/customer-orders.csv
//
// NOTE: NeoReports.Sources.Join.Pro is commercial (PolyForm Small Business — free under USD 1M
// annual revenue). This sample references it as a project reference for demonstration.

// customer 1 -> 2 orders (150), customer 2 -> none, customer 3 -> 1 order (200). Both ordered by key.
var customers = new InMemorySource<Customer>(new[]
{
    new Customer(1, "Acme"),
    new Customer(2, "Globex"),
    new Customer(3, "Initech"),
});
var orders = new InMemorySource<Order>(new[]
{
    new Order(1, 100m),
    new Order(1, 50m),
    new Order(3, 200m),
});

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddReport<CustomerReport>("customer-orders", b => b
    .From(Join.MergeJoin(
        left: customers, keyLeft: c => c.Id,
        right: orders, keyRight: o => o.CustomerId,
        map: (c, os) => new CustomerReport(c.Id, c.Name, os.Count, os.Sum(o => o.Amount)),
        kind: JoinKind.LeftOuter))
    .Column(v => v.Id, "Customer ID")
    .Column(v => v.Name, "Customer")
    .Column(v => v.OrderCount, "Orders")
    .Column(v => v.Total, "Total", format: "C2", culture: "pt-BR")
    .To(Csv(o => o.Delimiter(';')))
    .UploadTo(Destination.Local("./out/{name}.{ext}")));

await using var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IReportRunner>();

var result = await runner.RunAsync("customer-orders");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.RemotePath} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;
