using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Sources;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// ADR D58: <see cref="StreamingToBatchSource{T}"/> adapts an <see cref="IStreamingSource{T}"/> to
/// the <see cref="IBatchSource{T}"/> contract the dynamic-path config compiler requires, so a
/// naturally-streaming source (a CSV file, …) doesn't need to invent cursor bookkeeping it doesn't
/// otherwise need. Promoted from <c>NeoReports.Sources.Join.Pro</c>'s own internal, already-proven
/// implementation — these tests cover the same behavior against the new public copy.
/// </summary>
public class StreamingToBatchSourceTests
{
    private static ReportExecutionContext Exec() =>
        new("job", "report", null, NullLogger.Instance, CancellationToken.None);

    private sealed class FiniteStreamingSource : IStreamingSource<int>
    {
        private readonly int _count;
        public int OpenCount { get; private set; }

        public FiniteStreamingSource(int count) => _count = count;

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Value", ColumnType.Integer) });

        public async IAsyncEnumerable<int> ReadAsync(
            ReportExecutionContext execution, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            OpenCount++;
            for (var i = 1; i <= _count; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }
    }

    [Fact]
    public async Task Pages_the_stream_at_the_requested_page_size()
    {
        var source = new StreamingToBatchSource<int>(new FiniteStreamingSource(5));

        BatchResult<int> first = await source.ReadBatchAsync(new BatchContext(Exec(), 2, null, 1), CancellationToken.None);
        first.Records.ShouldBe(new[] { 1, 2 });
        first.HasMore.ShouldBeTrue();
        first.NextCursor.ShouldNotBeNull();

        BatchResult<int> second = await source.ReadBatchAsync(new BatchContext(Exec(), 2, first.NextCursor, 2), CancellationToken.None);
        second.Records.ShouldBe(new[] { 3, 4 });
        second.HasMore.ShouldBeTrue();

        BatchResult<int> third = await source.ReadBatchAsync(new BatchContext(Exec(), 2, second.NextCursor, 3), CancellationToken.None);
        third.Records.ShouldBe(new[] { 5 });
        third.HasMore.ShouldBeFalse();
        third.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task A_null_cursor_starts_a_fresh_run_even_mid_stream()
    {
        var inner = new FiniteStreamingSource(3);
        var source = new StreamingToBatchSource<int>(inner);

        await source.ReadBatchAsync(new BatchContext(Exec(), 1, null, 1), CancellationToken.None);
        inner.OpenCount.ShouldBe(1);

        // A null cursor (a fresh run) reopens the stream from the beginning, discarding position.
        BatchResult<int> restarted = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);
        inner.OpenCount.ShouldBe(2);
        restarted.Records.ShouldBe(new[] { 1, 2, 3 });
    }

    [Fact]
    public void Schema_is_forwarded_from_the_inner_streaming_source()
    {
        var inner = new FiniteStreamingSource(1);
        var source = new StreamingToBatchSource<int>(inner);

        source.Schema.ShouldBeSameAs(inner.Schema);
    }

    [Fact]
    public async Task An_empty_stream_reports_no_records_and_no_further_pages()
    {
        var source = new StreamingToBatchSource<int>(new FiniteStreamingSource(0));

        BatchResult<int> result = await source.ReadBatchAsync(new BatchContext(Exec(), 10, null, 1), CancellationToken.None);

        result.Records.ShouldBeEmpty();
        result.HasMore.ShouldBeFalse();
    }
}
