using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Snowflake.UnitTests;

public sealed record Sale(long Id, string Customer);

/// <summary>
/// ADR D57: constructing an <c>AdoKeysetSource&lt;T&gt;</c> never opens a connection — only
/// <c>ReadBatchAsync</c> does — so the fluent builder's key-column/schema derivation can be verified
/// without a live warehouse.
/// </summary>
public class SnowflakeSourceBuilderTests
{
    private const string Sql = "SELECT Id, Customer FROM Sales WHERE (:cursor IS NULL OR Id > :cursor) ORDER BY Id";

    [Fact]
    public void Keyset_derives_the_schema_from_the_key_selector()
    {
        var source = Source.Snowflake("account=x", Sql).Keyset<Sale, long>(v => v.Id);

        source.Schema.Count.ShouldBe(1);
        source.Schema.Columns[0].Name.ShouldBe("Id");
    }

    [Fact]
    public void Named_keyset_derives_the_schema_from_the_key_selector()
    {
        var source = Source.SnowflakeNamed("sales-db", Sql).Keyset<Sale, long>(v => v.Id);

        source.Schema.Count.ShouldBe(1);
        source.Schema.Columns[0].Name.ShouldBe("Id");
    }
}
