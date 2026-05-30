using System.Text;
using FluentAssertions;
using NeoReports.Abstractions;
using NeoReports.Destinations.Local;
using Xunit;

namespace NeoReports.Destinations.Local.UnitTests;

public class LocalDestinationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nr-local-tests", Guid.NewGuid().ToString("N"));

    private DestinationContext Context() =>
        new(new ReportExecutionContext("job", "vendas", null,
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

        var result = await destination.UploadAsync(FileOf("vendas.csv", "a,b\n1,2\n"), Context(), CancellationToken.None);

        result.Success.Should().BeTrue();
        File.Exists(result.RemotePath).Should().BeTrue();
        (await File.ReadAllTextAsync(result.RemotePath!)).Should().Be("a,b\n1,2\n");
        Path.GetFileName(result.RemotePath!).Should().StartWith("vendas-").And.EndWith(".csv");
    }

    [Fact]
    public async Task Overwrites_existing_file_atomically()
    {
        var template = Path.Combine(_root, "{name}.{ext}");
        var destination = new LocalDestination(template);

        await destination.UploadAsync(FileOf("r.csv", "old"), Context(), CancellationToken.None);
        var result = await destination.UploadAsync(FileOf("r.csv", "new"), Context(), CancellationToken.None);

        (await File.ReadAllTextAsync(result.RemotePath!)).Should().Be("new");
        // No leftover temp files in the directory.
        Directory.GetFiles(_root).Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
