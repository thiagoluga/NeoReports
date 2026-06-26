using Hangfire;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Destinations.Local;
using NeoReports.Jobs.Hangfire.DependencyInjection;
using NeoReports.Samples.AsyncJobHangfire;
using NeoReports.Sources.Sql;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

// Sample 03 — trigger reports over HTTP, executed by a single Hangfire server.
//
//   dotnet run --project samples/03-async-job-hangfire
//   curl -X POST http://localhost:5000/api/reports/monthly-sales/run            # async -> { jobId }
//   curl      http://localhost:5000/api/jobs/{jobId}                            # status
//   curl -OJ  http://localhost:5000/api/jobs/{jobId}/download                   # result
//   curl -X POST "http://localhost:5000/api/reports/monthly-sales/run?mode=sync" -o out.csv
//
// Uses Hangfire in-memory storage so the sample runs with no external dependencies; swap
// UseInMemoryStorage() for UseSqlServerStorage(connString) for real single-server persistence.

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Sales")
    ?? "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True";

builder.Services.AddReport<Sale>("monthly-sales", b => b
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

await app.RunAsync();
