// Sample 12 — a wide (51-column), large (500,000-row) report read from a real SQL Server database,
// provisioned and seeded automatically by .NET Aspire (Epic H / D46).
//
//   dotnet run --project samples/12-aspire-sqlserver-wide/AppHost
//   Then open the printed dashboard URL to watch "sqlserver" come up and "report-runner" seed the
//   database and write ./out/wide-transactions-<date>.csv + .xlsx.

var builder = DistributedApplication.CreateBuilder(args);

var sqlserver = builder.AddSqlServer("sqlserver")
    .WithDataVolume()
    .AddDatabase("widetransactions");

builder.AddProject<Projects.NeoReports_Samples_AspireSqlServerWide_ReportRunner>("report-runner")
    .WithReference(sqlserver)
    .WaitFor(sqlserver);

builder.Build().Run();
