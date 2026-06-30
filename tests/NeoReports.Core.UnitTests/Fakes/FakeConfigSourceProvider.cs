using NeoReports.Abstractions;

namespace NeoReports.Core.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IConfigSourceProvider"/> for the dynamic-config tests. It turns a fixed set
/// of positional value arrays into <see cref="ReportRecord"/> rows aligned to the supplied schema,
/// served as a single page by a <see cref="FakeBatchSource{T}"/>.
/// </summary>
public sealed class FakeConfigSourceProvider : IConfigSourceProvider
{
    private readonly IReadOnlyList<object?[]> _rows;

    public FakeConfigSourceProvider(IReadOnlyList<object?[]> rows, string type = "inmemory")
    {
        _rows = rows;
        Type = type;
    }

    /// <summary>The most recent source configuration this provider was asked to build.</summary>
    public SourceConfig? LastConfig { get; private set; }

    public string Type { get; }

    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        LastConfig = source;
        var records = _rows.Select(values => new ReportRecord(schema, values)).ToArray();
        return new FakeBatchSource<ReportRecord>(new[] { (IReadOnlyList<ReportRecord>)records });
    }
}
