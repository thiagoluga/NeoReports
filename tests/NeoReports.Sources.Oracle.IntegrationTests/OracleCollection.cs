using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

/// <summary>
/// Shares a single Oracle container across every test class in this assembly. Every database suite
/// uses this collection-fixture pattern so each provider starts exactly one container regardless of
/// class count — a per-class <c>IClassFixture</c> would start one per class and overcommit the CI
/// runner's memory. Oracle has the extra motivation of a slow container start.
/// </summary>
[CollectionDefinition(nameof(OracleCollection))]
public sealed class OracleCollection : ICollectionFixture<OracleServerFixture>
{
}
