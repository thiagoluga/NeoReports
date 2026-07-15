// Sample 14 — a single combined demo that provisions all four supported relational/document
// databases (PostgreSQL, MySQL, SQL Server, MongoDB) at once and mounts one NeoReports UI in
// front of all of them, so every source type, the Source Registry, the Builder wizard, and
// scheduling can all be exercised end-to-end from one running app — no "Demo mode" fallback,
// because the Web host below registers a config-source provider for every one of these types.
//
//   dotnet run --project samples/14-aspire-all-sources-demo/AppHost
//   Then open the printed dashboard URL, click into the "web" resource's endpoint — that's the
//   full NeoReports UI, with four pre-registered named sources (postgres-demo, mysql-demo,
//   sqlserver-demo, mongodb-demo) and four working example reports, one per database.
//
// No manual setup: Aspire pulls all four images, starts the containers, and injects the
// connection strings into the web host via WithReference.

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("postgres-db");

var mysql = builder.AddMySql("mysql")
    .WithDataVolume()
    .AddDatabase("mysql-db");

var sqlServer = builder.AddSqlServer("sqlserver")
    .WithDataVolume()
    .AddDatabase("sqlserver-db");

var mongo = builder.AddMongoDB("mongodb")
    .WithDataVolume()
    .AddDatabase("mongodb-db");

builder.AddProject<Projects.NeoReports_Samples_AspireAllSourcesDemo_Web>("web")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithReference(mysql)
    .WaitFor(mysql)
    .WithReference(sqlServer)
    .WaitFor(sqlServer)
    .WithReference(mongo)
    .WaitFor(mongo)
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();
