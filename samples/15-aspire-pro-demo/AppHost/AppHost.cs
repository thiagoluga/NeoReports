// Sample 15 — sample 14's all-sources demo plus the commercial Pro packages. Provisions the same
// four databases (PostgreSQL, MySQL, SQL Server, MongoDB) and reuses sample 14's seeding, column
// definitions and named-source registration verbatim through NeoReports.Samples.AllSourcesShared;
// the Web host on top adds the three Pro packages (ADR D50/L1).
//
//   # Pro packages require a license at run time (ADR D70) — set it first:
//   $env:NEOREPORTS_LICENSE_KEY = "<your key>"      # bash: export NEOREPORTS_LICENSE_KEY="<your key>"
//   dotnet run --project samples/15-aspire-pro-demo/AppHost
//
// Without a valid key the web host throws NeoReportsLicenseException at startup, naming every way
// to supply one. Sample 14 is the same demo without any Pro package, and needs no license.

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

builder.AddProject<Projects.NeoReports_Samples_AspireProDemo_Web>("web")
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
