using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.SourceRegistry;

/// <summary>ADR D42: <see cref="InMemorySourceRegistryStore"/> — same contract as the file-backed twin.</summary>
public class InMemorySourceRegistryStoreTests
{
    [Fact]
    public async Task Save_then_get_roundtrips()
    {
        var store = new InMemorySourceRegistryStore();
        await store.SaveAsync(new SourceDefinition("sales-db", "sql"), CancellationToken.None);

        (await store.GetAsync("sales-db", CancellationToken.None))!.Type.ShouldBe("sql");
    }

    [Fact]
    public async Task Delete_removes_the_definition()
    {
        var store = new InMemorySourceRegistryStore();
        await store.SaveAsync(new SourceDefinition("sales-db", "sql"), CancellationToken.None);

        (await store.DeleteAsync("sales-db", CancellationToken.None)).ShouldBeTrue();
        (await store.GetAsync("sales-db", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task List_returns_every_definition_sorted_by_name()
    {
        var store = new InMemorySourceRegistryStore();
        await store.SaveAsync(new SourceDefinition("beta-db", "sql"), CancellationToken.None);
        await store.SaveAsync(new SourceDefinition("alpha-db", "sql"), CancellationToken.None);

        (await store.ListAsync(CancellationToken.None)).Select(d => d.Name).ShouldBe(new[] { "alpha-db", "beta-db" });
    }
}
