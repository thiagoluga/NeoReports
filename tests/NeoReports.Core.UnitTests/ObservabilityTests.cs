using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class ObservabilityTests
{
    private static Sale[] Page(params long[] ids) =>
        ids.Select(id => new Sale(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static CompiledReport Build(FakeBatchSource<Sale> source, FakeWriterFactory writer, Action<ReportBuilder<Sale>>? extra = null)
    {
        var builder = new ReportBuilder<Sale>("sales")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(writer));
        extra?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public async Task Aborted_run_logs_an_error_and_correlates_the_scope_with_the_job()
    {
        var logger = new CapturingLogger();
        // A unique job id per test: the runner writes its output under a temp dir keyed on the job id
        // (Path.Combine(GetTempPath(), "neoreports", jobId)), so a fixed id would let parallel test
        // classes collide on — and clean up — each other's files (an intermittent FileNotFound).
        var jobId = Guid.NewGuid().ToString("N");
        var execution = new ReportExecutionContext(jobId, "sales", null, logger, CancellationToken.None);

        var report = Build(
            new FakeBatchSource<Sale>(new[] { Page(1), Page(2) }),
            new FakeWriterFactory(throwOnBatch: 1),
            b => b.OnFailure(f => f.AbortReport()));

        var result = await ReportRunner.ExecuteAsync(report, execution, new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Failed);

        // The abort was surfaced through ILogger at Error, not only the event stream.
        var error = logger.Entries.ShouldHaveSingleItem();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldContain("sales");

        // Every line the run produced is inside a scope carrying the job id and report name, so a
        // host's sink can group a concurrent run's output.
        logger.Scopes.Any(s => s.TryGetValue("JobId", out var v) && Equals(v, jobId)).ShouldBeTrue();
        logger.Scopes.Any(s => s.TryGetValue("ReportName", out var v) && Equals(v, "sales")).ShouldBeTrue();
    }

    [Fact]
    public async Task Aborted_read_failure_also_logs_an_error()
    {
        var logger = new CapturingLogger();
        var execution = new ReportExecutionContext(Guid.NewGuid().ToString("N"), "sales", null, logger, CancellationToken.None);

        // Fails the first read definitively (no retry configured), so the run aborts on the read path.
        var source = new FakeBatchSource<Sale>(new[] { Page(1) }, new Dictionary<int, int> { [1] = 1 });
        var report = Build(source, new FakeWriterFactory(), b => b.OnFailure(f => f.AbortReport()));

        var result = await ReportRunner.ExecuteAsync(report, execution, new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("read failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_run_logs_no_error()
    {
        var logger = new CapturingLogger();
        var execution = new ReportExecutionContext(Guid.NewGuid().ToString("N"), "sales", null, logger, CancellationToken.None);

        var report = Build(new FakeBatchSource<Sale>(new[] { Page(1, 2) }), new FakeWriterFactory());

        var result = await ReportRunner.ExecuteAsync(report, execution, new EmptyServiceProvider(), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public List<IReadOnlyDictionary<string, object>> Scopes { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object>> pairs)
                Scopes.Add(new Dictionary<string, object>(pairs));
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
