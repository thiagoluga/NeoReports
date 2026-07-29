using NeoReports.AspNetCore;
using NeoReports.Samples.AllSourcesShared;
using NeoReports.UI;

// Sample 14 — the Web half of the combined "all sources" Aspire demo. AppHost.cs provisions all
// four databases (Postgres, MySQL, SQL Server, MongoDB) and injects their connection strings via
// WithReference; this host seeds all four once, registers one working typed report per database,
// registers every one of them as a named source in the Source Registry (D42) so the Builder
// wizard can build brand-new reports against them, and — critically — registers every capability
// provider so GET /api/capabilities is never empty and the UI's "Demo mode" banner never appears.
// All of that lives in AllSourcesDemo, shared verbatim with sample 15 (the same demo plus the Pro
// packages) so the diff between the two hosts is only the Pro surface.
// Run standalone with:
//   dotnet run --project samples/14-aspire-all-sources-demo/Web -- "<pg>" "<mysql>" "<sqlserver>" "<mongo>"

AllSourcesDemo.RegisterMongoGuidSerializer();

var builder = WebApplication.CreateBuilder(args);

var connections = AllSourcesDemo.ResolveConnections(
    args, builder.Configuration, "samples/14-aspire-all-sources-demo/AppHost");

AllSourcesDemo.AddSharedServices(builder.Services);
AllSourcesDemo.AddWideTransactionReports(builder.Services, connections);

var app = builder.Build();

app.MapNeoReports("/api");

var uiPath = app.Configuration["NeoReports:UIPath"] ?? NeoReportsUIExtensions.DefaultBasePath;
app.UseNeoReportsUI(uiPath);

// Convenience: the sample root redirects into the UI.
app.MapGet("/", () => Results.Redirect(uiPath));

// Start Kestrel BEFORE seeding: Aspire marks this resource's endpoint reachable as soon as the
// process starts, and seeding four databases can take a while — starting the listener first means
// the UI shell loads immediately instead of the endpoint refusing connections until seeding finishes.
await app.StartAsync();

await AllSourcesDemo.SeedAndRegisterAsync(app.Services, connections);

await app.WaitForShutdownAsync();
