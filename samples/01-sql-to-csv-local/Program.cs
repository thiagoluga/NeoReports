using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using NeoReports.Sources.Sql;
using static NeoReports.Core.Building.ReportColumns;

// Sample 01 — SQL Server -> CSV -> local filesystem (the first end-to-end report).
//
// Run against any SQL Server with a Vendas table:
//   dotnet run --project samples/01-sql-to-csv-local -- "<connection-string>"
//
// Expected schema: Vendas(Id BIGINT, Cliente NVARCHAR, Valor DECIMAL, Data DATETIME2).

var connectionString = args.Length > 0
    ? args[0]
    : "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True";

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddReport<Venda>("vendas-mensal", b => b
    .From(Source.Sql(
            connectionString,
            "SELECT Id, Cliente, Valor, Data FROM Vendas " +
            "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
        .Keyset<Venda, long>(v => v.Id, pageSize: 1000))
    .Filter(v => v.Valor > 0)
    .Columns(
        Col<Venda, long>(v => v.Id, "ID Venda"),
        Col<Venda, string>(v => v.Cliente, "Cliente"),
        Col<Venda, decimal>(v => v.Valor, "Valor", format: "C2", culture: "pt-BR"),
        Col<Venda, DateTime>(v => v.Data, "Data Venda", format: "yyyy-MM-dd"))
    .To(Format.Csv(o => o.Delimiter(';').Encoding(Encoding.UTF8)))
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}")));

var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IReportRunner>();

var result = await runner.RunAsync("vendas-mensal");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.RemotePath} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;

internal sealed record Venda(long Id, string Cliente, decimal Valor, DateTime Data);
