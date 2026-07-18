using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Events;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// ADR D61: a batch-read exception implementing <see cref="IRetryDelayHint"/> (e.g. an HTTP source
/// honoring <c>Retry-After</c>) overrides the configured backoff delay for that one retry, without a
/// second resilience mechanism. Asserted against the actual delay Polly used (recorded on the
/// "retry" job event, D38) rather than wall-clock timing, so the test stays deterministic and fast.
/// </summary>
public class RetryDelayHintTests
{
    private sealed class HintException : NeoReportsException, IRetryDelayHint
    {
        public HintException(TimeSpan? retryAfter) : base("NR-TEST-001", "transient with a delay hint") => RetryAfter = retryAfter;
        public TimeSpan? RetryAfter { get; }
    }

    private sealed class OnceThrowingSource<T> : IBatchSource<T>
    {
        private readonly IReadOnlyList<T> _records;
        private readonly Exception _exception;
        private bool _thrown;

        public OnceThrowingSource(IReadOnlyList<T> records, Exception exception)
        {
            _records = records;
            _exception = exception;
        }

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("_", ColumnType.String) });

        public Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw _exception;
            }

            return Task.FromResult(new BatchResult<T>(_records, null, false));
        }
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static ReportExecutionContext Exec(string jobId) =>
        new(jobId, "r", null, NullLogger.Instance, CancellationToken.None);

    private static CompiledReport Build(IBatchSource<Sale> source, TimeSpan configuredBackoff) =>
        new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Retry(r => r.MaxAttempts(2).Constant(configuredBackoff))
            .Build();

    [Fact]
    public async Task Retry_delay_hint_overrides_the_configured_backoff()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new OnceThrowingSource<Sale>(
            new[] { new Sale(1, "C1", 10m, DateTime.UnixEpoch) }, new HintException(TimeSpan.FromMilliseconds(7)));
        CompiledReport report = Build(source, configuredBackoff: TimeSpan.FromSeconds(30));

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        IReadOnlyList<JobEvent> retryEvents = await store.ListAsync(jobId, JobEventTypes.Retry, 100, 0, CancellationToken.None);
        retryEvents.Count.ShouldBe(1);
        retryEvents[0].Data!["delayMs"].ShouldBe("7");
    }

    [Fact]
    public async Task Retry_delay_hint_of_null_falls_back_to_the_configured_backoff()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new OnceThrowingSource<Sale>(
            new[] { new Sale(1, "C1", 10m, DateTime.UnixEpoch) }, new HintException(retryAfter: null));
        CompiledReport report = Build(source, configuredBackoff: TimeSpan.FromMilliseconds(5));

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        IReadOnlyList<JobEvent> retryEvents = await store.ListAsync(jobId, JobEventTypes.Retry, 100, 0, CancellationToken.None);
        retryEvents[0].Data!["delayMs"].ShouldBe("5");
    }

    [Fact]
    public async Task An_exception_without_the_hint_uses_the_configured_backoff_unaffected()
    {
        var store = new InMemoryJobEventStore();
        var jobId = Guid.NewGuid().ToString("N");
        var source = new OnceThrowingSource<Sale>(
            new[] { new Sale(1, "C1", 10m, DateTime.UnixEpoch) }, new InvalidOperationException("plain transient failure"));
        CompiledReport report = Build(source, configuredBackoff: TimeSpan.FromMilliseconds(5));

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(jobId), new SingleServiceProvider(store), CancellationToken.None);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        IReadOnlyList<JobEvent> retryEvents = await store.ListAsync(jobId, JobEventTypes.Retry, 100, 0, CancellationToken.None);
        retryEvents[0].Data!["delayMs"].ShouldBe("5");
    }
}
