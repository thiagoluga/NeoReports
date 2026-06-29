using System.Text;
using NeoReports.Abstractions;
using NeoReports.Destinations.Local;
using Shouldly;
using Xunit;

namespace NeoReports.Destinations.Local.UnitTests;

public class LocalDestinationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr-local-tests", Guid.NewGuid().ToString("N"));

    private DestinationContext Context() =>
        new(new ReportExecutionContext("job", "sales", null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None), null);

    private static ReportFile FileOf(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new ReportFile(name, "text/csv", bytes.Length, () => new MemoryStream(bytes));
    }

    [Fact]
    public async Task Writes_file_to_resolved_path()
    {
        var template = Path.Combine(_root, "{name}-{date:yyyy-MM-dd}.{ext}");
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
        var template = Path.Combine(_root, "{name}.{ext}");
        var destination = new LocalDestination(template);

        await destination.UploadAsync(FileOf("r.csv", "old"), Context(), CancellationToken.None);
        var result = await destination.UploadAsync(FileOf("r.csv", "new"), Context(), CancellationToken.None);

        (await File.ReadAllTextAsync(result.RemotePath!)).ShouldBe("new");
        // No leftover temp files in the directory.
        Directory.GetFiles(_root).ShouldHaveSingleItem();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
