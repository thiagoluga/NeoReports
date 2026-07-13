// Sample 11 — a wide (51-column), large (500,000-row) report read from a real MySQL database,
// provisioned and seeded automatically by .NET Aspire (Epic H / D46).
//
//   dotnet run --project samples/11-aspire-mysql-wide/AppHost
//   Then open the printed dashboard URL to watch "mysql" come up and "report-runner" seed the
//   database and write ./out/wide-transactions-<date>.csv + .xlsx.

var builder = DistributedApplication.CreateBuilder(args);

var mysql = builder.AddMySql("mysql")
    .WithDataVolume()
    .AddDatabase("widetransactions");

builder.AddProject<Projects.NeoReports_Samples_AspireMySqlWide_ReportRunner>("report-runner")
    .WithReference(mysql)
    .WaitFor(mysql);

builder.Build().Run();
