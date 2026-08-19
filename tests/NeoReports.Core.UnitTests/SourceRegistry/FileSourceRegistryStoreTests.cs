using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.SourceRegistry;

/// <summary>ADR D42: <see cref="FileSourceRegistryStore"/> save/get/delete/list roundtrip, plus corrupt-file resilience.</summary>
public class FileSourceRegistryStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "nr-d42-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_then_get_roundtrips_a_definition()
    {
        var store = new FileSourceRegistryStore(_directory);
        var definition = new SourceDefinition("sales-db", "sql",
            new Dictionary<string, object?> { ["connectionString"] = "${SALES_DB}" }, "Sales database");

        await store.SaveAsync(definition, CancellationToken.None);

        SourceDefinition? read = await store.GetAsync("sales-db", CancellationToken.None);
        read.ShouldNotBeNull();
        read!.Name.ShouldBe("sales-db");
        read.Type.ShouldBe("sql");
        read.Description.ShouldBe("Sales database");
        read.Properties!["connectionString"].ShouldBe("${SALES_DB}");
    }

    [Fact]
    public async Task Save_then_get_roundtrips_every_primitive_property_kind()
    {
        var store = new FileSourceRegistryStore(_directory);
        var definition = new SourceDefinition("sales-db", "sql", new Dictionary<string, object?>
        {
            ["port"] = 1433L,
            ["timeoutSeconds"] = 30.5,
            ["encrypt"] = true,
            ["trustServerCertificate"] = false,
            ["label"] = "primary",
            ["nothing"] = null,
        });

        await store.SaveAsync(definition, CancellationToken.None);

        SourceDefinition? read = await store.GetAsync("sales-db", CancellationToken.None);
        read!.Properties!["port"].ShouldBe(1433L);
        read.Properties["timeoutSeconds"].ShouldBe(30.5);
        read.Properties["encrypt"].ShouldBe(true);
        read.Properties["trustServerCertificate"].ShouldBe(false);
        read.Properties["label"].ShouldBe("primary");
        read.Properties["nothing"].ShouldBeNull();
    }

    [Fact]
    public async Task Get_unknown_name_returns_null()
    {
        var store = new FileSourceRegistryStore(_directory);
        (await store.GetAsync("missing", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Save_replaces_an_existing_definition()
    {
        var store = new FileSourceRegistryStore(_directory);
        await store.SaveAsync(new SourceDefinition("sales-db", "sql", Description: "first"), CancellationToken.None);
        await store.SaveAsync(new SourceDefinition("sales-db", "sql", Description: "second"), CancellationToken.None);

        SourceDefinition? read = await store.GetAsync("sales-db", CancellationToken.None);
        read!.Description.ShouldBe("second");
    }

    [Fact]
    public async Task Delete_removes_the_definition()
    {
        var store = new FileSourceRegistryStore(_directory);
        await store.SaveAsync(new SourceDefinition("sales-db", "sql"), CancellationToken.None);

        (await store.DeleteAsync("sales-db", CancellationToken.None)).ShouldBeTrue();
        (await store.GetAsync("sales-db", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_unknown_name_returns_false()
    {
        var store = new FileSourceRegistryStore(_directory);
        (await store.DeleteAsync("missing", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task List_returns_every_stored_definition()
    {
        var store = new FileSourceRegistryStore(_directory);
        await store.SaveAsync(new SourceDefinition("beta-db", "sql"), CancellationToken.None);
        await store.SaveAsync(new SourceDefinition("alpha-db", "sql"), CancellationToken.None);

        IReadOnlyList<SourceDefinition> listed = await store.ListAsync(CancellationToken.None);
        listed.Select(d => d.Name).ShouldBe(new[] { "alpha-db", "beta-db" });
    }

    [Fact]
    public async Task List_on_a_missing_directory_returns_empty()
    {
        var store = new FileSourceRegistryStore(_directory);
        (await store.ListAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_skips_a_corrupt_file_rather_than_throwing()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Join(_directory, "broken.json"), "{ not valid json");

        var store = new FileSourceRegistryStore(_directory);
        await store.SaveAsync(new SourceDefinition("good-db", "sql"), CancellationToken.None);

        IReadOnlyList<SourceDefinition> listed = await store.ListAsync(CancellationToken.None);
        listed.ShouldHaveSingleItem();
        listed[0].Name.ShouldBe("good-db");
    }

    [Fact]
    public async Task Get_a_corrupt_file_returns_null_rather_than_throwing()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Join(_directory, "broken.json"), "{ not valid json");

        var store = new FileSourceRegistryStore(_directory);
        (await store.GetAsync("broken", CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>
    /// A write under a name the store cannot key is the caller's mistake and still throws — rejecting
    /// it is what keeps the name from ever becoming a path. A read or delete is a miss instead: a
    /// lookup for something that could never have been written is simply not found, and throwing there
    /// made <c>GET /api/sources/{name}</c> answer 500 (with the validation regex in the body) for a
    /// name a plain 404 already describes.
    /// </summary>
    [Theory]
    [InlineData("../evil")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task Invalid_name_is_refused_on_write_and_a_miss_on_read(string invalidName)
    {
        var store = new FileSourceRegistryStore(_directory);

        await Should.ThrowAsync<ArgumentException>(
            () => store.SaveAsync(new SourceDefinition(invalidName, "sql"), CancellationToken.None));

        (await store.GetAsync(invalidName, CancellationToken.None)).ShouldBeNull();
        (await store.DeleteAsync(invalidName, CancellationToken.None)).ShouldBeFalse();

        // None of the three touched the filesystem: the store only creates its directory on a
        // successful write, so its absence is the strongest available proof that the name never
        // reached GetPath.
        Directory.Exists(_directory).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
