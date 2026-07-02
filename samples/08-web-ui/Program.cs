// Sample 08 — hosting the NeoReports web UI.
//
// The UI ships as a Razor Class Library (NeoReports.Web); any ASP.NET Core host
// mounts it with two calls: AddNeoReportsUi on the service collection and
// UseNeoReportsUi with the base path on the pipeline. The base path is
// configurable — this sample reads it from the NeoReports:UiPath configuration
// key (appsettings.json, environment variable or command line) and falls back
// to the default, /neoreports. See README.md for the run commands.

using NeoReports.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNeoReportsUi();

var app = builder.Build();

var uiPath = app.Configuration["NeoReports:UiPath"] ?? NeoReportsUiExtensions.DefaultBasePath;
app.UseNeoReportsUi(uiPath);

// Convenience: the sample root redirects into the UI.
app.MapGet("/", () => Results.Redirect(uiPath));

await app.RunAsync();
