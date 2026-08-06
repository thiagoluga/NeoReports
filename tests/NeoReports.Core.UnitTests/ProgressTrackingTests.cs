using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Configuration;
using NeoReports.Core.Events;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// ADR D47: the opt-out source row count that powers a real completion percentage. Covers the
/// typed builder, the dynamic-path parser/compiler, and the runner's count-then-run behavior.
/// Complements <see cref="JobEventEmissionTests"/> (unaffected event-log mechanics).
/// </summary>
public class ProgressTrackingTests
{
    private static IReadOnlyList<Sale> Page(params long[] ids) =>
        ids.Select(id => new Sale(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static ReportExecutionContext Exec(string? jobId = null) =>
        new(jobId ?? Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    // --- ReportBuilder<T>.TrackProgress ---

    // RowCountFactory is internal (no InternalsVisibleTo in this repo), so these assert the
    // observable effect of a run — whether the counter was actually invoked and whether
    // JobStats.TotalRecords came back populated — rather than the internal wiring itself.

    [Fact]
    public void Default_is_enabled_on_the_compiled_report()
    {
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1) }, count: 5);
        CompiledReport report = new ReportBuilder<Sale>("r")
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        report.TrackProgress.ShouldBeTrue();
    }

    [Fact]
    public async Task Default_enabled_counts_the_source_when_the_run_executes()
    {
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1) }, count: 5);
        CompiledReport report = new ReportBuilder<Sale>("r")
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        source.CountCalls.ShouldBe(1);
        result.Stats.TotalRecords.ShouldBe(5);
    }

    [Fact]
    public async Task TrackProgress_false_never_counts_the_source()
    {
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1) }, count: 5);
        CompiledReport report = new ReportBuilder<Sale>("r")
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .TrackProgress(false)
            .Build();

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        report.TrackProgress.ShouldBeFalse();
        source.CountCalls.ShouldBe(0);
        result.Stats.TotalRecords.ShouldBeNull();
    }

    [Fact]
    public async Task A_source_that_cannot_count_runs_normally_with_no_total()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1) }); // no ISourceRowCounter
        CompiledReport report = new ReportBuilder<Sale>("r")
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        report.TrackProgress.ShouldBeTrue(); // default stays true...
        result.Status.ShouldBe(ReportRunStatus.Completed); // ...but there is nothing to call, and it never throws.
        result.Stats.TotalRecords.ShouldBeNull();
    }

    private sealed record MappedRow(long Id);

    [Fact]
    public async Task A_mapped_source_still_counts_through_the_wrapper()
    {
        var inner = new FakeCountingBatchSource<Sale>(new[] { Page(1) }, count: 7);
        CompiledReport report = new ReportBuilder<MappedRow>("r")
            .From(inner, (Sale s) => new MappedRow(s.Id))
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

        inner.CountCalls.ShouldBe(1);
        result.Stats.TotalRecords.ShouldBe(7);
    }

    /// <summary>
    /// Regression: MappingBatchSource/RefBatchSource unconditionally implement ISourceRowCounter so
    /// a report built over a mapped/ref source still gets a real total when its inner source can
    /// count — but a mapped source whose inner CAN'T count must degrade silently (null, no throw),
    /// not log a "row count failed" warning on every single run for a source that never claimed to
    /// support counting in the first place.
    /// </summary>
    [Fact]
    public async Task A_mapped_source_over_a_non_counting_inner_degrades_silently_with_no_warning()
    {
        var recordedWarnings = new List<string>();
        var logger = new CapturingLogger(recordedWarnings);
        var inner = new FakeBatchSource<Sale>(new[] { Page(1) }); // no ISourceRowCounter
        CompiledReport report = new ReportBuilder<MappedRow>("r")
            .From(inner, (Sale s) => new MappedRow(s.Id))
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Build();

        var execution = new ReportExecutionContext(Guid.NewGuid().ToString("N"), "r", null, logger, CancellationToken.None);
        ReportRunResult result = await ReportRunner.ExecuteAsync(report, execution, new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.TotalRecords.ShouldBeNull();
        recordedWarnings.ShouldBeEmpty(); // no "Progress row count failed" — nothing actually failed.
    }

    private sealed class CapturingLogger(List<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                warnings.Add(formatter(state, exception));
        }
    }

    // --- Dynamic path: parser + compiler ---

    private static readonly JsonReportConfigParser Parser = new();

    private static ServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(new[] { new object?[] { 1L } }))
            .AddSingleton<IWriterFactory>(new FakeWriterFactory("csv", "csv"))
            .BuildServiceProvider();

    private static string ConfigJson(string? trackProgressJson = null) => $$"""
    {
      "name": "sales",
      "source": { "type": "inmemory" },
      "columns": [ { "name": "Id", "type": "Integer" } ],
      "outputs": [ { "format": "csv" } ]
      {{(trackProgressJson is null ? "" : $", \"trackProgress\": {trackProgressJson}")}}
    }
    """;

    [Fact]
    public void Parser_leaves_TrackProgress_null_when_the_key_is_absent()
    {
        ReportConfig config = Parser.Parse(ConfigJson());
        config.TrackProgress.ShouldBeNull();
    }

    [Fact]
    public void Parser_reads_an_explicit_false()
    {
        ReportConfig config = Parser.Parse(ConfigJson("false"));
        config.TrackProgress.ShouldBe(false);
    }

    [Fact]
    public void Parser_reads_an_explicit_true()
    {
        ReportConfig config = Parser.Parse(ConfigJson("true"));
        config.TrackProgress.ShouldBe(true);
    }

    [Fact]
    public void Parser_matches_the_key_case_insensitively()
    {
        var json = """
        { "name": "sales", "source": { "type": "inmemory" },
          "columns": [ { "name": "Id", "type": "Integer" } ],
          "outputs": [ { "format": "csv" } ], "TrackProgress": false }
        """;
        Parser.Parse(json).TrackProgress.ShouldBe(false);
    }

    [Fact]
    public void Compiler_leaves_the_engine_default_true_when_the_config_omits_TrackProgress()
    {
        ReportConfig config = Parser.Parse(ConfigJson());
        using var services = BuildServices();

        CompiledReport report = ReportConfigCompiler.Compile(config, services);
        report.TrackProgress.ShouldBeTrue();
    }

    [Fact]
    public void Compiler_applies_an_explicit_false()
    {
        ReportConfig config = Parser.Parse(ConfigJson("false"));
        using var services = BuildServices();

        CompiledReport report = ReportConfigCompiler.Compile(config, services);
        report.TrackProgress.ShouldBeFalse();
    }

    [Fact]
    public void Compiler_applies_an_explicit_true()
    {
        ReportConfig config = Parser.Parse(ConfigJson("true"));
        using var services = BuildServices();

        CompiledReport report = ReportConfigCompiler.Compile(config, services);
        report.TrackProgress.ShouldBeTrue();
    }

    // --- ReportRunner: count-then-run ---

    private static CompiledReport BuildReport(FakeCountingBatchSource<Sale> source, bool trackProgress = true) =>
        new ReportBuilder<Sale>("r")
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .TrackProgress(trackProgress)
            .Build();

    [Fact]
    public async Task A_successful_count_populates_events_and_JobStats()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1, 2) }, count: 42);
        var report = BuildReport(source);

        ReportRunResult result = await ReportRunner.ExecuteAsync(
            report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.TotalRecords.ShouldBe(42);
        source.CountCalls.ShouldBe(1);

        var events = await store.ListAsync(jobId, null, 100, 0, CancellationToken.None);
        // run-started is emitted BEFORE the count (so a crash mid-count still leaves an event for
        // restart detection, D38) — it never carries totalRecords; page-completed does, from page 1.
        events.Single(e => e.Type == JobEventTypes.RunStarted).Data.ShouldBeNull();
        events.Where(e => e.Type == JobEventTypes.PageCompleted)
            .ShouldAllBe(e => e.Data!["totalRecords"] == "42");
    }

    [Fact]
    public async Task A_count_that_throws_degrades_to_indeterminate_without_changing_the_run_status()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1) }, throws: new InvalidOperationException("count failed"));
        var report = BuildReport(source);

        ReportRunResult result = await ReportRunner.ExecuteAsync(
            report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed); // unaffected by the count failure
        result.Stats.TotalRecords.ShouldBeNull();

        var events = await store.ListAsync(jobId, null, 100, 0, CancellationToken.None);
        events.ShouldAllBe(e => e.Data == null || !e.Data.ContainsKey("totalRecords"));
    }

    [Fact]
    public async Task Cancelling_the_count_cancels_the_run()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1) }, count: 1);
        var report = BuildReport(source);
        var execution = new ReportExecutionContext(
            Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(
            () => ReportRunner.ExecuteAsync(report, execution, new EmptyServiceProvider(), cts.Token));
    }

    // --- ADR D80: reconciliation ---

    private static CompiledReport ReconcileReport(IBatchSource<Sale> source, FakeWriterFactory? writer = null) =>
        new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(writer ?? new FakeWriterFactory()))
            .Build();

    private static async Task<IReadOnlyList<JobEvent>> RunAndReadEventsAsync(CompiledReport report, string jobId)
    {
        var store = new InMemoryJobEventStore();
        await ReportRunner.ExecuteAsync(
            report,
            new ReportExecutionContext(jobId, "r", null, NullLogger.Instance, CancellationToken.None),
            new SingleServiceProvider(store),
            CancellationToken.None);

        return await store.ListAsync(jobId, JobEventTypes.RowCountMismatch, 100, 0, CancellationToken.None);
    }

    [Fact]
    public async Task A_completed_run_that_read_fewer_rows_than_counted_reports_a_mismatch()
    {
        // Two rows delivered against a count of five. A non-unique keyset key loses rows exactly like
        // this — silently, invisibly to the source — and reconciliation is the only place it surfaces.
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1, 2) }, count: 5);

        IReadOnlyList<JobEvent> events = await RunAndReadEventsAsync(ReconcileReport(source), jobId);

        JobEvent mismatch = events.ShouldHaveSingleItem();
        mismatch.Data!["expected"].ShouldBe("5");
        mismatch.Data!["read"].ShouldBe("2");
    }

    [Fact]
    public async Task A_run_whose_count_matches_reports_nothing()
    {
        // The check must stay quiet on the ordinary case, or the event becomes noise nobody reads.
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1, 2) }, count: 2);

        (await RunAndReadEventsAsync(ReconcileReport(source), jobId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_source_that_cannot_count_reports_nothing()
    {
        // No count, nothing to reconcile against — and emitting a mismatch here would accuse every
        // source that simply does not implement ISourceRowCounter.
        //
        // FakeBatchSource, not FakeCountingBatchSource with count: null — that fake maps null to 0,
        // so it expresses "counted zero" rather than "cannot count", and the first draft of this test
        // failed against correct code because of it.
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeBatchSource<Sale>(new[] { Page(1, 2) });

        (await RunAndReadEventsAsync(ReconcileReport(source), jobId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_partial_run_reports_no_mismatch()
    {
        // CompletedPartial skipped batches on purpose, so reading fewer rows is the expected outcome
        // and is already reported as Partial (D75). Flagging it again would be double-counting a
        // known cause as if it were an unknown one.
        var jobId = Guid.NewGuid().ToString("N");
        var source = new FakeCountingBatchSource<Sale>(new[] { Page(1), Page(2), Page(3) }, count: 3);
        CompiledReport report = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory(2)))
            .OnFailure(f => f.SkipBatchAndLog())
            .Build();

        (await RunAndReadEventsAsync(report, jobId)).ShouldBeEmpty();
    }
}
