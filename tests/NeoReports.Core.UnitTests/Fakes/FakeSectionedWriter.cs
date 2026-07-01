using System.Text;
using NeoReports.Core.Sections;

namespace NeoReports.Core.UnitTests.Fakes;

/// <summary>Sectioned writer that records the rows written to each section and streams them out.</summary>
public sealed class FakeSectionedWriter : IReportSectionedWriter
{
    private Stream _output = Stream.Null;

    public List<List<object?[]>> Sections { get; } = [];

    public IReadOnlyList<ReportSection> InitSections { get; private set; } = [];

    public bool Finalized { get; private set; }

    public string Format => "sectioned-fake";
    public string MimeType => "application/x-fake";
    public string FileExtension => "fake";

    public Task InitializeAsync(SectionedWriterContext context, CancellationToken cancellationToken)
    {
        _output = context.Output;
        InitSections = context.Sections;
        for (var i = 0; i < context.Sections.Count; i++)
            Sections.Add([]);
        return Task.CompletedTask;
    }

    public async Task WriteSectionRowsAsync(int sectionIndex, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        Sections[sectionIndex].AddRange(rows);
        foreach (var row in rows)
        {
            var line = string.Join(',', row.Select(v => v?.ToString() ?? string.Empty)) + "\n";
            await _output.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken);
        }
    }

    public Task FinalizeAsync(CancellationToken cancellationToken)
    {
        Finalized = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Factory that produces a single <see cref="FakeSectionedWriter"/> and exposes it.</summary>
public sealed class FakeSectionedWriterFactory : ISectionedWriterFactory
{
    public FakeSectionedWriter? Last { get; private set; }

    public string Format => "sectioned-fake";

    public IReportSectionedWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services)
    {
        var writer = new FakeSectionedWriter();
        Last = writer;
        return writer;
    }
}
