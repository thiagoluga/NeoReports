using Xunit;

namespace NeoReports.Sources.Oracle.IntegrationTests;

/// <summary>
/// Shares a single Oracle container across every test class in this assembly. Oracle containers
/// are considerably slower to start than SQL Server/PostgreSQL/MySQL, so unlike those providers'
/// per-class <c>IClassFixture</c> fixtures, Oracle tests opt into a collection fixture instead.
/// </summary>
[CollectionDefinition(nameof(OracleCollection))]
public sealed class OracleCollection : ICollectionFixture<OracleServerFixture>
{
}
