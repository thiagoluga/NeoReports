using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.MongoDb.IntegrationTests;

/// <summary>
/// ADR D47: <see cref="ISourceRowCounter"/> against a real MongoDB container — an exact
/// <c>CountDocumentsAsync</c>, not the estimated/approximate count (see the ADR for why).
/// </summary>
public class MongoDbRowCounterTests : IClassFixture<MongoDbServerFixture>
{
    private readonly MongoDbServerFixture _fixture;

    public MongoDbRowCounterTests(MongoDbServerFixture fixture) => _fixture = fixture;

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, NullLogger.Instance, CancellationToken.None);

    [SkippableFact]
    public async Task CountAsync_matches_the_seeded_document_count()
    {
        Skip.IfNot(_fixture.Available, "Docker/MongoDB container not available.");

        var source = Source.MongoDb(_fixture.ConnectionString, MongoDbServerFixture.Database, MongoDbServerFixture.Collection)
            .Keyset<Sale, long>(v => v.Id, pageSize: 1000);
        var counter = (ISourceRowCounter)source;

        var count = await counter.CountAsync(Exec(), CancellationToken.None);

        count.ShouldBe(_fixture.SeededRows);
    }
}
