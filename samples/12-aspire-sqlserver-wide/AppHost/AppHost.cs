// Sample 12 — a wide (51-column), large (500,000-row) report read from a real SQL Server database,
// provisioned and seeded automatically by .NET Aspire (Epic H / D46).
//
//   dotnet run --project samples/12-aspire-sqlserver-wide/AppHost
//   Then open the printed dashboard URL, click into the "web" resource's endpoint — that's the
//   full NeoReports UI, seeded and ready. Aspire's only job here is standing up SQL Server and
//   starting that UI; everything else (running "wide-transactions", watching progress,
//   downloading the file) happens by clicking through it.

var builder = DistributedApplication.CreateBuilder(args);

var sqlserver = builder.AddSqlServer("sqlserver")
    .WithDataVolume()
    .AddDatabase("widetransactions");

builder.AddProject<Projects.NeoReports_Samples_AspireSqlServerWide_Web>("web")
    .WithReference(sqlserver)
    .WaitFor(sqlserver)
    .WithExternalHttpEndpoints();

builder.Build().Run();
