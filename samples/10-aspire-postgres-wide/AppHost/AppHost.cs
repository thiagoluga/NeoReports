// Sample 10 — a wide (51-column), large (500,000-row) report read from a real PostgreSQL
// database, provisioned and seeded automatically by .NET Aspire (Epic H / D46).
//
//   dotnet run --project samples/10-aspire-postgres-wide/AppHost
//   Then open the printed dashboard URL to watch "postgres" come up and "report-runner" seed
//   the database and write ./out/wide-transactions-<date>.csv + .xlsx.
//
// No manual setup: Aspire pulls the postgres:17 image, starts the container, and injects the
// connection string into ReportRunner via WithReference — the same "docker compose up, but typed
// and orchestrated from C#" experience Aspire is built for.

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("widetransactions");

builder.AddProject<Projects.NeoReports_Samples_AspirePostgresWide_ReportRunner>("report-runner")
    .WithReference(postgres)
    .WaitFor(postgres);

builder.Build().Run();
