// Sample 09 — the web UI mounted together with a live engine.
//
// samples/08-web-ui hosts NeoReports.UI alone, so every screen runs in demo mode. This sample
// adds the other half in the SAME host: NeoReports.Core + NeoReports.AspNetCore, with dynamic
// report registration enabled (AddDynamicReports, Epic D / ADR D33) and a self-contained
// in-memory source, so the whole product can be clicked through end to end — register a report
// from the Builder, validate it, save it, run it, download a real file, delete it — without any
// external database or cloud credentials.
//
// Run with: dotnet run --project samples/09-web-ui-live
// Then open the printed URL (redirects into the UI) and follow README.md's walkthrough.

using NeoReports.Abstractions;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Destinations.Local;
using NeoReports.Formats.Csv;
using NeoReports.Formats.Xlsx;
using NeoReports.Jobs.DependencyInjection;
using NeoReports.Samples.WebUiLive;
using NeoReports.UI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNeoReportsUI();

// --- The engine, mounted in the same host as the UI ---
// AddDynamicReports() defaults its config store to ./neoreports-configs (relative to the
// working directory the app is run from); reports registered via the Builder survive a restart.
builder.Services.AddDynamicReports();
builder.Services.AddSingleton<IConfigSourceProvider, InMemorySalesSourceProvider>();  // source "inmemory"
builder.Services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions())); // format "csv"
builder.Services.AddSingleton<IWriterFactory>(new XlsxWriterFactory(new XlsxOptions())); // format "xlsx"
builder.Services.AddSingleton<IDestinationFactory>(
    new LocalDestinationFactory("./out/{name}-{date:yyyy-MM-dd}.{ext}"));              // destination "local"
builder.Services.AddNeoReportsInMemoryJobs();
builder.Services.AddNeoReportsArtifacts();
builder.Services.AddInMemoryJobEvents(); // ADR D38 — powers the job Timeline/Retries/rate cards
builder.Services.AddInMemoryScheduling(); // ADR D41 — recurring runs; overrides + schedules die with the process
// --- end engine wiring ---

var app = builder.Build();

app.MapNeoReports("/api");

var uiPath = app.Configuration["NeoReports:UIPath"] ?? NeoReportsUIExtensions.DefaultBasePath;
app.UseNeoReportsUI(uiPath);

// Convenience: the sample root redirects into the UI.
app.MapGet("/", () => Results.Redirect(uiPath));

await app.RunAsync();
