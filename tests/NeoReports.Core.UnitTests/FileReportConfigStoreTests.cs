using NeoReports.Core.Configuration;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>Epic D / D1: <see cref="FileReportConfigStore"/> save/list/exists/delete roundtrip.</summary>
public class FileReportConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "nr-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_then_exists_then_list_then_delete_roundtrips()
    {
        var store = new FileReportConfigStore(_directory);

        await store.SaveAsync("alpha", """{"name":"alpha"}""", CancellationToken.None);

        (await store.ExistsAsync("alpha", CancellationToken.None)).ShouldBeTrue();

        var listed = await store.ListAsync(CancellationToken.None);
        listed.ShouldHaveSingleItem();
        listed[0].Name.ShouldBe("alpha");
        listed[0].Document.ShouldBe("""{"name":"alpha"}""");

        (await store.DeleteAsync("alpha", CancellationToken.None)).ShouldBeTrue();
        (await store.ExistsAsync("alpha", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Save_overwrites_existing_content()
    {
        var store = new FileReportConfigStore(_directory);

        await store.SaveAsync("alpha", "first", CancellationToken.None);
        await store.SaveAsync("alpha", "second", CancellationToken.None);

        var listed = await store.ListAsync(CancellationToken.None);
        listed.ShouldHaveSingleItem();
        listed[0].Document.ShouldBe("second");
    }

    [Fact]
    public async Task Delete_unknown_name_returns_false()
    {
        var store = new FileReportConfigStore(_directory);

        (await store.DeleteAsync("missing", CancellationToken.None)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("1abc")]
    public async Task Invalid_name_throws_on_every_operation(string invalidName)
    {
        var store = new FileReportConfigStore(_directory);

        await Should.ThrowAsync<ArgumentException>(() => store.SaveAsync(invalidName, "{}", CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => store.ExistsAsync(invalidName, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => store.DeleteAsync(invalidName, CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_on_missing_directory_returns_empty()
    {
        var store = new FileReportConfigStore(_directory); // never written to, directory never created

        var listed = await store.ListAsync(CancellationToken.None);

        listed.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_ignores_leftover_tmp_files()
    {
        var store = new FileReportConfigStore(_directory);
        await store.SaveAsync("alpha", "{}", CancellationToken.None);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Join(_directory, "beta.json.tmp"), "{}");

        var listed = await store.ListAsync(CancellationToken.None);

        listed.ShouldHaveSingleItem();
        listed[0].Name.ShouldBe("alpha");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
