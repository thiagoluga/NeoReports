// Sample 08 — hosting the NeoReports web UI.
//
// The UI ships as a Razor Class Library (NeoReports.UI); any ASP.NET Core host
// mounts it with two calls: AddNeoReportsUI on the service collection and
// UseNeoReportsUI with the base path on the pipeline. The base path is
// configurable — this sample reads it from the NeoReports:UIPath configuration
// key (appsettings.json, environment variable or command line) and falls back
// to the default, /neoreports. See README.md for the run commands.

using NeoReports.UI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNeoReportsUI();

var app = builder.Build();

var uiPath = app.Configuration["NeoReports:UIPath"] ?? NeoReportsUIExtensions.DefaultBasePath;
app.UseNeoReportsUI(uiPath);

// Convenience: the sample root redirects into the UI.
app.MapGet("/", () => Results.Redirect(uiPath));

await app.RunAsync();
