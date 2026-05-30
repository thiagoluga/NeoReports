using System.Runtime.CompilerServices;
using NeoReports.Abstractions;

namespace NeoReports.Core.UnitTests.Fakes;

/// <summary>In-memory streaming source; the pipeline slices it into batches.</summary>
public sealed class FakeStreamingSource<T> : IStreamingSource<T>
{
    private readonly IReadOnlyList<T> _items;

    public FakeStreamingSource(IReadOnlyList<T> items) => _items = items;

    public ReportSchema Schema { get; } = new(new[] { new ReportColumn("_", ColumnType.String) });

    public async IAsyncEnumerable<T> ReadAsync(
        ReportExecutionContext execution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return item;
        }
    }
}
