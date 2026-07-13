// Sample 13 — a wide (51-column), large (500,000-row) report read from a real MongoDB database,
// provisioned and seeded automatically by .NET Aspire (Epic H / D46).
//
//   dotnet run --project samples/13-aspire-mongodb-wide/AppHost
//   Then open the printed dashboard URL, click into the "web" resource's endpoint — that's the
//   full NeoReports UI, seeded and ready. Aspire's only job here is standing up MongoDB and
//   starting that UI; everything else (running "wide-transactions", watching progress,
//   downloading the file) happens by clicking through it.

var builder = DistributedApplication.CreateBuilder(args);

var mongodb = builder.AddMongoDB("mongodb")
    .WithDataVolume()
    .AddDatabase("widetransactions");

builder.AddProject<Projects.NeoReports_Samples_AspireMongoDbWide_Web>("web")
    .WithReference(mongodb)
    .WaitFor(mongodb)
    .WithExternalHttpEndpoints();

builder.Build().Run();
