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

    /// <summary>A stream that throws on read, standing in for a cancelled or failed transfer.</summary>
    private sealed class ThrowingStream : Stream
    {
        private readonly Func<Exception> _throw;

        public ThrowingStream(Func<Exception> onRead) => _throw = onRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw _throw();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_cancelled_upload_surfaces_as_cancellation_not_as_a_destination_error()
    {
        // catch (Exception) swallowed cancellation into UploadResult.Fail, so a deadline firing
        // mid-write was attributed to the filesystem instead of to the deadline (ADR D78).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var destination = new LocalDestination(Path.Join(_root, "{name}.{ext}"));
        var file = new ReportFile(
            "sales.csv", "text/csv", 10, () => new ThrowingStream(() => new OperationCanceledException(cts.Token)));

        await Should.ThrowAsync<OperationCanceledException>(
            () => destination.UploadAsync(file, Context(), cts.Token));

        // The staging file must still be cleaned up on the way out — the cancellation path shares the
        // failure path's cleanup, and skipping it would leave a temp file behind per cancelled run.
        // The target DIRECTORY is created up front and legitimately survives; what must not survive
        // is any file in it, published or temporary.
        if (Directory.Exists(_root))
            Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancellation_from_something_else_is_still_a_destination_error()
    {
        // The filter is on OUR token: an OperationCanceledException carrying someone else's token is
        // a genuine failure and must keep being reported as one.
        var destination = new LocalDestination(Path.Join(_root, "{name}.{ext}"));
        var file = new ReportFile(
            "sales.csv", "text/csv", 10, () => new ThrowingStream(() => new TaskCanceledException("Timed out.")));

        UploadResult result = await destination.UploadAsync(file, Context(), CancellationToken.None);

        result.Success.ShouldBeFalse();
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
