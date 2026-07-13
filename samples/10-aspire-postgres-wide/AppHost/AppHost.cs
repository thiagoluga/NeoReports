// Sample 10 — a wide (51-column), large (500,000-row) report read from a real PostgreSQL
// database, provisioned and seeded automatically by .NET Aspire (Epic H / D46).
//
//   dotnet run --project samples/10-aspire-postgres-wide/AppHost
//   Then open the printed dashboard URL, click into the "web" resource's endpoint — that's the
//   full NeoReports UI, seeded and ready. Aspire's only job here is standing up Postgres and
//   starting that UI; everything else (running "wide-transactions", watching progress,
//   downloading the file) happens by clicking through it.
//
// No manual setup: Aspire pulls the postgres:17 image, starts the container, and injects the
// connection string into the web host via WithReference — the same "docker compose up, but typed
// and orchestrated from C#" experience Aspire is built for.

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("widetransactions");

builder.AddProject<Projects.NeoReports_Samples_AspirePostgresWide_Web>("web")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithExternalHttpEndpoints();

builder.Build().Run();
