using Hangfire;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Destinations.Local;
using NeoReports.Jobs.Hangfire.DependencyInjection;
using NeoReports.Sources.Sql;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

// Sample 03 — trigger reports over HTTP, executed by a single Hangfire server.
//
//   dotnet run --project samples/03-async-job-hangfire
//   curl -X POST http://localhost:5000/api/reports/vendas-mensal/run            # async -> { jobId }
//   curl      http://localhost:5000/api/jobs/{jobId}                            # status
//   curl -OJ  http://localhost:5000/api/jobs/{jobId}/download                   # result
//   curl -X POST "http://localhost:5000/api/reports/vendas-mensal/run?mode=sync" -o out.csv
//
// Uses Hangfire in-memory storage so the sample runs with no external dependencies; swap
// UseInMemoryStorage() for UseSqlServerStorage(connString) for real single-server persistence.

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Sales")
    ?? "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True";

builder.Services.AddReport<Venda>("vendas-mensal", b => b
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
    .To(Csv(o => o.Delimiter(';')))
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}")));

// Hangfire single-server (in-memory storage for the sample).
builder.Services.AddHangfire(cfg => cfg.UseInMemoryStorage());
builder.Services.AddHangfireServer();

// NeoReports job backend (Hangfire) + artifact store for download/sync endpoints.
builder.Services.AddNeoReportsHangfireJobs();
builder.Services.AddNeoReportsArtifacts();

var app = builder.Build();

app.MapNeoReports("/api");

app.Run();

internal sealed record Venda(long Id, string Cliente, decimal Valor, DateTime Data);
