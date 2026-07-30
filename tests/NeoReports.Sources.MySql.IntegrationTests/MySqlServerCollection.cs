using Xunit;

namespace NeoReports.Sources.MySql.IntegrationTests;

/// <summary>
/// Shares a single MySQL container across every test class in this assembly. A per-class
/// <c>IClassFixture</c> starts one container per class; sharing one keeps the suite within the CI
/// runner's memory budget and avoids the resource-contention crashes that hit heavier database
/// images. Mirrors the collection-fixture pattern already used by the Oracle and SQL Server suites.
/// </summary>
[CollectionDefinition(nameof(MySqlServerCollection))]
public sealed class MySqlServerCollection : ICollectionFixture<MySqlServerFixture>
{
}
