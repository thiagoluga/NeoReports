// Sample 08 — hosting the NeoReports web UI.
//
// The UI ships as a Razor Class Library (NeoReports.Web); any ASP.NET Core host
// mounts it with two calls:
//
//   builder.Services.AddNeoReportsUi();
//   app.UseNeoReportsUi("<base path>");
//
// The base path is configurable. This sample reads it from configuration
// (NeoReports:UiPath — appsettings.json, environment variable or command line,
// e.g. `dotnet run -- --NeoReports:UiPath=/reports-admin`) and falls back to
// the default /neoreports.

using NeoReports.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNeoReportsUi();

var app = builder.Build();

var uiPath = app.Configuration["NeoReports:UiPath"] ?? NeoReportsUiExtensions.DefaultBasePath;
app.UseNeoReportsUi(uiPath);

// Convenience: the sample root redirects into the UI.
app.MapGet("/", () => Results.Redirect(uiPath));

await app.RunAsync();
