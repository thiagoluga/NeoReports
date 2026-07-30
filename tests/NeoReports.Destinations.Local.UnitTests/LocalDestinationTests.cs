using System.Text;
using NeoReports.Abstractions;
using NeoReports.Destinations.Local;
using Shouldly;
using Xunit;

namespace NeoReports.Destinations.Local.UnitTests;

public class LocalDestinationTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "nr-local-tests", Guid.NewGuid().ToString("N"));

    private DestinationContext Context(IReadOnlyDictionary<string, object?>? parameters = null) =>
        new(new ReportExecutionContext("job", "sales", parameters,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None), null);

    private static ReportFile FileOf(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new ReportFile(name, "text/csv", bytes.Length, () => new MemoryStream(bytes));
    }

    [Fact]
    public async Task Writes_file_to_resolved_path()
    {
        var template = Path.Join(_root, "{name}-{date:yyyy-MM-dd}.{ext}");
        var destination = new LocalDestination(template);

        var result = await destination.UploadAsync(FileOf("sales.csv", "a,b\n1,2\n"), Context(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        File.Exists(result.RemotePath).ShouldBeTrue();
        (await File.ReadAllTextAsync(result.RemotePath!)).ShouldBe("a,b\n1,2\n");
        Path.GetFileName(result.RemotePath!).ShouldStartWith("sales-");
        Path.GetFileName(result.RemotePath!).ShouldEndWith(".csv");
    }

    [Fact]
    public async Task Overwrites_existing_file_atomically()
    {
        var template = Path.Join(_root, "{name}.{ext}");
        var destination = new LocalDestination(template);

        await destination.UploadAsync(FileOf("r.csv", "old"), Context(), CancellationToken.None);
        var result = await destination.UploadAsync(FileOf("r.csv", "new"), Context(), CancellationToken.None);

        (await File.ReadAllTextAsync(result.RemotePath!)).ShouldBe("new");
        // No leftover temp files in the directory.
        Directory.GetFiles(_root).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Substitutes_parameter_value_into_a_single_segment()
    {
        var template = Path.Join(_root, "{customer}", "{name}.{ext}");
        var destination = new LocalDestination(template);
        var parameters = new Dictionary<string, object?> { ["customer"] = "acme" };

        var result = await destination.UploadAsync(
            FileOf("sales.csv", "x"), Context(parameters), CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.RemotePath!.ShouldBe(Path.Join(_root, "acme", "sales.csv"));
        File.Exists(result.RemotePath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("../../../../evil")]
    [InlineData("..\\..\\evil")]
    [InlineData("sub/evil")]
    [InlineData("..")]
    [InlineData(".")]
    public async Task Rejects_path_traversal_in_a_runtime_parameter(string malicious)
    {
        // The vector: a run-time parameter is caller-controlled (report-run request body), so a
        // traversal value must not be able to escape the template's target directory.
        var template = Path.Join(_root, "out", "{customer}.{ext}");
        var destination = new LocalDestination(template);
        var parameters = new Dictionary<string, object?> { ["customer"] = malicious };

        var result = await destination.UploadAsync(
            FileOf("sales.csv", "pwned"), Context(parameters), CancellationToken.None);

        // Rejected before any file I/O: the write never happens and no path is returned.
        result.Success.ShouldBeFalse();
        result.RemotePath.ShouldBeNull();
        // The guard fires before the target directory is even created.
        Directory.Exists(_root).ShouldBeFalse();
    }

    [SkippableFact]
    public async Task Rejects_windows_drive_or_ads_colon_in_a_runtime_parameter()
    {
        // A ':' introduces a Windows drive prefix ("C:..") or NTFS alternate-data-stream
        // ("name:stream"); off Windows it is an ordinary file-name character, so this guard is
        // Windows-specific.
        Skip.IfNot(OperatingSystem.IsWindows(), "':' is only a path-redirection risk on Windows.");

        var template = Path.Join(_root, "out", "{customer}.{ext}");
        var destination = new LocalDestination(template);
        var parameters = new Dictionary<string, object?> { ["customer"] = "C:evil" };

        var result = await destination.UploadAsync(
            FileOf("sales.csv", "pwned"), Context(parameters), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.RemotePath.ShouldBeNull();
    }

    [Fact]
    public async Task Allows_date_token_with_directory_separators_for_partitioning()
    {
        // {date:...} is author-controlled and its output may legitimately contain separators — it is
        // NOT subject to the single-segment guard that runtime parameters are.
        var template = Path.Join(_root, "{date:yyyy/MM/dd}", "{name}.{ext}");
        var destination = new LocalDestination(template);

        var result = await destination.UploadAsync(FileOf("sales.csv", "x"), Context(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        File.Exists(result.RemotePath).ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
