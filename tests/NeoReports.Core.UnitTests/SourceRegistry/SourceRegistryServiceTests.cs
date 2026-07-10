using NeoReports.Abstractions;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.SourceRegistry;

/// <summary>
/// ADR D42, locked decision 2: <see cref="SourceRegistryService"/> resolves <c>${VAR}</c>
/// placeholders fresh on every call (never cached), while the raw definition itself is
/// read-through cached and invalidated on save/delete.
/// </summary>
public class SourceRegistryServiceTests : IDisposable
{
    private const string VarName = "NR_D42_TEST_VAR";

    [Fact]
    public async Task ResolveAsync_substitutes_a_placeholder_property()
    {
        Environment.SetEnvironmentVariable(VarName, "first-value");
        var store = new InMemorySourceRegistryStore();
        var registry = new SourceRegistryService(store);
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql",
            new Dictionary<string, object?> { ["connectionString"] = $"${{{VarName}}}" }), CancellationToken.None);

        SourceDefinition? resolved = await registry.ResolveAsync("sales-db", CancellationToken.None);

        resolved.ShouldNotBeNull();
        resolved!.Properties!["connectionString"].ShouldBe("first-value");
    }

    [Fact]
    public async Task ResolveAsync_reflects_a_changed_environment_variable_on_the_next_call()
    {
        Environment.SetEnvironmentVariable(VarName, "first-value");
        var store = new InMemorySourceRegistryStore();
        var registry = new SourceRegistryService(store);
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql",
            new Dictionary<string, object?> { ["connectionString"] = $"${{{VarName}}}" }), CancellationToken.None);

        SourceDefinition? first = await registry.ResolveAsync("sales-db", CancellationToken.None);
        first!.Properties!["connectionString"].ShouldBe("first-value");

        // The definition itself was never re-saved — only the environment changed — proving
        // substitution runs fresh per call rather than being cached alongside the raw definition.
        Environment.SetEnvironmentVariable(VarName, "second-value");
        SourceDefinition? second = await registry.ResolveAsync("sales-db", CancellationToken.None);
        second!.Properties!["connectionString"].ShouldBe("second-value");
    }

    [Fact]
    public async Task ResolveAsync_throws_when_the_referenced_variable_is_unset()
    {
        Environment.SetEnvironmentVariable(VarName, null);
        var store = new InMemorySourceRegistryStore();
        var registry = new SourceRegistryService(store);
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql",
            new Dictionary<string, object?> { ["connectionString"] = $"${{{VarName}}}" }), CancellationToken.None);

        await Should.ThrowAsync<ConfigurationException>(() => registry.ResolveAsync("sales-db", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_returns_null_for_an_unknown_source()
    {
        var registry = new SourceRegistryService(new InMemorySourceRegistryStore());
        (await registry.ResolveAsync("missing", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_invalidates_the_cached_entry()
    {
        var store = new InMemorySourceRegistryStore();
        var registry = new SourceRegistryService(store);
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql", Description: "first"), CancellationToken.None);
        await registry.ResolveAsync("sales-db", CancellationToken.None); // populates the cache

        await registry.SaveAsync(new SourceDefinition("sales-db", "sql", Description: "second"), CancellationToken.None);

        SourceDefinition? resolved = await registry.ResolveAsync("sales-db", CancellationToken.None);
        resolved!.Description.ShouldBe("second");
    }

    [Fact]
    public async Task DeleteAsync_invalidates_the_cached_entry()
    {
        var store = new InMemorySourceRegistryStore();
        var registry = new SourceRegistryService(store);
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql"), CancellationToken.None);
        await registry.ResolveAsync("sales-db", CancellationToken.None); // populates the cache

        await registry.DeleteAsync("sales-db", CancellationToken.None);

        (await registry.ResolveAsync("sales-db", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_delegates_to_the_store_unsubstituted()
    {
        Environment.SetEnvironmentVariable(VarName, "secret-value");
        var store = new InMemorySourceRegistryStore();
        var registry = new SourceRegistryService(store);
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql",
            new Dictionary<string, object?> { ["connectionString"] = $"${{{VarName}}}" }), CancellationToken.None);

        IReadOnlyList<SourceDefinition> listed = await registry.ListAsync(CancellationToken.None);
        listed.ShouldHaveSingleItem();
        listed[0].Properties!["connectionString"].ShouldBe($"${{{VarName}}}");
    }

    public void Dispose() => Environment.SetEnvironmentVariable(VarName, null);
}
