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
    // A lookup for a name the store could never have written is a miss, not an error. It used to
    // throw ArgumentException, which nothing above catches — GET /api/sources/{name} with such a name
    // answered 500 (echoing the validation regex) where an unknown-but-legal name answers 404.
    [Theory]
    [InlineData("a b")]
    [InlineData("../evil")]
    [InlineData("1abc")]
    [InlineData("")]
    public async Task A_lookup_for_an_unwritable_name_is_a_miss(string name)
    {
        var store = new InMemorySourceRegistryStore();
        await store.SaveAsync(new SourceDefinition("real-db", "sql"), CancellationToken.None);

        (await store.GetAsync(name, CancellationToken.None)).ShouldBeNull();
        (await store.DeleteAsync(name, CancellationToken.None)).ShouldBeFalse();

        // And the store is untouched by the attempt.
        (await store.ListAsync(CancellationToken.None)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_write_under_an_unwritable_name_still_throws()
    {
        var store = new InMemorySourceRegistryStore();

        // Writes keep validating: there a bad name is the caller's mistake, and rejecting it is what
        // keeps the name from ever becoming a key or a path.
        await Should.ThrowAsync<ArgumentException>(
            () => store.SaveAsync(new SourceDefinition("a b", "sql"), CancellationToken.None));
    }
}
