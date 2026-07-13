// Sample 13 — a wide (51-column), large (500,000-row) report read from a real MongoDB database,
// provisioned and seeded automatically by .NET Aspire (Epic H / D46).
//
//   dotnet run --project samples/13-aspire-mongodb-wide/AppHost
//   Then open the printed dashboard URL to watch "mongodb" come up and "report-runner" seed the
//   database and write ./out/wide-transactions-<date>.csv + .xlsx.

var builder = DistributedApplication.CreateBuilder(args);

var mongodb = builder.AddMongoDB("mongodb")
    .WithDataVolume()
    .AddDatabase("widetransactions");

builder.AddProject<Projects.NeoReports_Samples_AspireMongoDbWide_ReportRunner>("report-runner")
    .WithReference(mongodb)
    .WaitFor(mongodb);

builder.Build().Run();
