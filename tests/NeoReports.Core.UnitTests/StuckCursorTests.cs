using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Sources;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// ADR D72: the page loop is driven purely by <c>HasMore</c>, so a source that claims more data
/// without moving its cursor would make the runner re-issue the identical read forever — a job that
/// neither finishes nor fails. These cover the guard, and the invariant it depends on.
/// </summary>
public class StuckCursorTests
{
    private static ReportExecutionContext Exec() =>
        new(Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    /// <summary>
    /// Always answers "here is a page, there is more", handing back the cursor it was given.
    /// <para>
    /// It throws past <see cref="ReadLimit"/> reads on purpose. Without the guard under test this
    /// source loops forever, and a hanging test is far worse than a failing one — it burns a CI
    /// runner and, since every page writes rows, grows the test host until something dies. The limit
    /// converts "the guard is gone" from a hang into an ordinary red test, which also makes the
    /// guard's absence verifiable by reverting it.
    /// </para>
    /// </summary>
    private sealed class EchoingCursorSource : IBatchSource<Sale>
    {
        private const int ReadLimit = 50;

        public int ReadCalls { get; private set; }

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

        public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            ReadCalls++;
            if (ReadCalls > ReadLimit)
                throw new InvalidOperationException($"The page loop did not stop: {ReadLimit} reads with an unchanged cursor.");

            var rows = new[] { new Sale(ReadCalls, $"C{ReadCalls}", 1m, DateTime.UnixEpoch) };

            // "token" on the first page (there is no incoming cursor yet), then the very cursor it
            // just received — which is what Facebook Graph does on its last page.
            return Task.FromResult(new BatchResult<Sale>(rows, context.Cursor ?? "token", true));
        }
    }

    [Fact]
    public async Task A_source_that_never_advances_its_cursor_fails_the_run_instead_of_looping()
    {
        var source = new EchoingCursorSource();
        CompiledReport report = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        ReportRunResult result = await ReportRunner.ExecuteAsync(
            report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("cursor");

        // Page 1 hands back a new cursor ("token"), so it is legitimate; page 2 echoes it and is
        // where the guard fires. Anything more than two reads means the loop ran on.
        source.ReadCalls.ShouldBe(2);
    }

    [Fact]
    public async Task The_rows_of_the_last_readable_page_are_still_written()
    {
        // The guard runs after the batch is written: those rows were delivered correctly and only the
        // *next* read is impossible, so failing must not also discard a good page.
        var source = new EchoingCursorSource();
        var writer = new FakeWriterFactory();
        CompiledReport report = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(writer))
            .Build();

        ReportRunResult result = await ReportRunner.ExecuteAsync(
            report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        result.Stats.RecordsWritten.ShouldBe(2);
    }

    [Fact]
    public async Task A_streaming_source_pages_to_the_end_because_its_cursor_advances()
    {
        // StreamingToBatchSource keeps its position in a retained enumerator, not in the cursor, and
        // used to emit a constant token for every page — which the guard above would read as stuck,
        // breaking every file-backed source at page 2. It counts pages instead, so the invariant
        // ("more data means a different cursor") holds for it too. This is the regression test for
        // that interaction, not for streaming in general.
        var rows = Enumerable.Range(1, 25)
            .Select(i => new Sale(i, $"C{i}", i, DateTime.UnixEpoch))
            .ToArray();

        var source = new StreamingToBatchSource<Sale>(new FakeStreamingSource<Sale>(rows));
        CompiledReport report = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        ReportRunResult result = await ReportRunner.ExecuteAsync(
            report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.RecordsWritten.ShouldBe(25);
    }

    /// <summary>Yields a fixed set of rows one at a time.</summary>
    private sealed class FakeStreamingSource<T> : IStreamingSource<T>
    {
        private readonly IReadOnlyList<T> _rows;

        public FakeStreamingSource(IReadOnlyList<T> rows) => _rows = rows;

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

        public async IAsyncEnumerable<T> ReadAsync(
            ReportExecutionContext execution,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (T row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
                await Task.Yield();
            }
        }
    }
}
