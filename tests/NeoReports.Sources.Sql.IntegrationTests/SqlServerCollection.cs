using Xunit;

namespace NeoReports.Sources.Sql.IntegrationTests;

/// <summary>
/// Shares a single SQL Server container across every test class in this assembly. With a per-class
/// <c>IClassFixture</c> each of the ~9 classes span up its own SQL Server container (~2 GB each),
/// which on a 7 GB CI runner overcommits memory and makes containers crash mid-run
/// (<c>ContainerNotRunningException</c>, exit 255). One shared container serialises the classes and
/// keeps the suite within the runner's memory budget.
/// </summary>
[CollectionDefinition(nameof(SqlServerCollection))]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
