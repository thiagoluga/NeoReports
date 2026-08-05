using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class ResilienceTests
{
    private static IReadOnlyList<Sale> Page(params long[] ids) =>
        ids.Select(id => new Sale(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static ReportExecutionContext Exec() =>
        new(Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private static Task<ReportRunResult> Run(CompiledReport report) =>
        ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

    private static CompiledReport Build(
        FakeBatchSource<Sale> source,
        FakeWriterFactory writer,
        Action<ReportBuilder<Sale>>? extra = null)
    {
        var builder = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .Column(v => v.Customer, "Customer")
            .To(new OutputSpec(writer));

        extra?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public async Task Transient_read_failure_is_retried_and_report_completes()
    {
        var source = new FakeBatchSource<Sale>(
            new[] { Page(1, 2), Page(3, 4) },
            new Dictionary<int, int> { [1] = 2 });
        var writer = new FakeWriterFactory();

        var report = Build(source, writer, b => b.Retry(r => r.MaxAttempts(3).Constant(TimeSpan.Zero)));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
        result.Stats.Retries.ShouldBe(2);
        writer.LastWriter!.Rows.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Abort_strategy_fails_report_on_definitive_failure()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2) });
        var writer = new FakeWriterFactory(throwOnBatch: 1);

        var report = Build(source, writer, b => b.OnFailure(f => f.AbortReport()));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task FailureRate_does_not_abort_on_an_early_failure()
    {
        // Both counters are incremented before the ratio is computed, so a first-batch failure always
        // yields 1.0 — which tripped ANY FailureRate below 1, making the threshold behave as "abort on
        // the first failure" whatever it was configured to. Three batches with one failure is 0.33,
        // well under 0.5, but it never got that far (ADR D78).
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3) });
        var writer = new FakeWriterFactory(1);

        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(new AbortThresholdConfig(FailureRate: 0.5))));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.CompletedPartial, "one failure in three batches is not a 50% rate");
        result.SkippedBatches.ShouldBe(1);
    }

    [Fact]
    public async Task FailureRate_still_aborts_once_enough_batches_have_been_seen()
    {
        // The guard delays the judgement, it does not remove it: with the minimum lowered to 2, the
        // same shape aborts as soon as the ratio is meaningful and actually exceeded.
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3), Page(4) });
        var writer = new FakeWriterFactory(1, 2);

        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(
                new AbortThresholdConfig(FailureRate: 0.5) { FailureRateMinimumBatches = 2 })));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
    }

    [Fact]
    public void FailureRateMinimumBatches_defaults_to_ten()
    {
        // Pinned because the default is the whole behaviour for anyone who never sets it, and because
        // it is an init property rather than a constructor parameter — a positional argument would
        // have changed the primary constructor of a frozen-ABI record.
        new AbortThresholdConfig(FailureRate: 0.5).FailureRateMinimumBatches.ShouldBe(10);
    }

    [Fact]
    public async Task Skip_strategy_skips_failed_batch_and_marks_partial()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3) });
        var writer = new FakeWriterFactory(2);

        var report = Build(source, writer, b => b.OnFailure(f => f.SkipBatchAndLog()));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.CompletedPartial);
        result.SkippedBatches.ShouldBe(1);
        writer.LastWriter!.Rows.Select(r => (long)r[0]!).ShouldBe(new long[] { 1, 3 });
    }

    [Fact]
    public async Task Threshold_aborts_even_in_skip_mode()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3), Page(4) });
        var writer = new FakeWriterFactory(1, 2, 3);

        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(t => t.ConsecutiveFailures(3))));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.SkippedBatches.ShouldBe(2);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("threshold");
    }

    [Fact]
    public async Task Data_based_threshold_aborts_the_same_way_as_the_predicate_overload()
    {
        // Same fixture as Threshold_aborts_even_in_skip_mode, using the data-based AbortIf(AbortThresholdConfig)
        // overload (ADR D37) instead of a raw predicate — proves it compiles to the identical escalation.
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3), Page(4) });
        var writer = new FakeWriterFactory(1, 2, 3);

        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(new AbortThresholdConfig(ConsecutiveFailures: 3))));
        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.SkippedBatches.ShouldBe(2);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("threshold");
        report.AbortThresholds.ShouldBe(new AbortThresholdConfig(ConsecutiveFailures: 3));
    }

    [Fact]
    public void CompiledReport_AbortThresholds_is_null_without_escalation()
    {
        var report = Build(new FakeBatchSource<Sale>(new[] { Page(1) }), new FakeWriterFactory());
        report.AbortThresholds.ShouldBeNull();
    }

    [Fact]
    public async Task TotalFailures_threshold_aborts_on_non_consecutive_failures()
    {
        // Fails pages 1, 3, 5 (never two in a row), so ConsecutiveFailures never exceeds 1 — only the
        // TotalFailures accumulator can trip. Aborts when the third total failure lands on page 5.
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3), Page(4), Page(5) });
        var writer = new FakeWriterFactory(1, 3, 5);
        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(new AbortThresholdConfig(TotalFailures: 3))));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
    }

    [Fact]
    public async Task FailureRate_threshold_aborts_when_the_ratio_is_reached()
    {
        // Page 1 succeeds, 2 and 3 fail: ratios are 0, 0.5, then 2/3 ≈ 0.667. A 0.6 threshold trips on
        // page 3. Neither the consecutive (max 2) nor total (2) count would abort, so this isolates rate.
        //
        // FailureRateMinimumBatches is set to 3 rather than left at its default of 10: this fixture is
        // three batches long, which the default deliberately treats as too small a sample to call a
        // rate (ADR D78). Lowering it here keeps the test measuring what it was written to measure —
        // the ratio arithmetic — instead of the guard in front of it, which has its own tests.
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3) });
        var writer = new FakeWriterFactory(2, 3);
        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(
                new AbortThresholdConfig(FailureRate: 0.6) { FailureRateMinimumBatches = 3 })));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
    }

    [Fact]
    public async Task Consecutive_failure_counter_resets_after_an_intervening_success()
    {
        // Fails 1, 2, then 4, 5 with page 3 succeeding in between. The run of consecutive failures
        // never reaches 3 (it resets to 0 at page 3), so a ConsecutiveFailures(3) threshold must NOT
        // abort — the classic off-by-one/never-resets bug. The run completes partial with 4 skips.
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3), Page(4), Page(5) });
        var writer = new FakeWriterFactory(1, 2, 4, 5);
        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(new AbortThresholdConfig(ConsecutiveFailures: 3))));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.CompletedPartial);
        result.SkippedBatches.ShouldBe(4);
    }

    [Fact]
    public async Task Threshold_just_below_the_limit_does_not_abort()
    {
        // Two consecutive failures with a threshold of 3 — the boundary just below abort. The run must
        // complete partial, not fail.
        var source = new FakeBatchSource<Sale>(new[] { Page(1), Page(2), Page(3), Page(4) });
        var writer = new FakeWriterFactory(1, 2);
        var report = Build(source, writer, b => b
            .OnFailure(f => f.SkipBatchAndLog().AbortIf(new AbortThresholdConfig(ConsecutiveFailures: 3))));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.CompletedPartial);
        result.SkippedBatches.ShouldBe(2);
    }

    [Fact]
    public async Task Attempt_timeout_bounds_a_hung_read_and_fails_the_run()
    {
        // Without the per-attempt timeout this run would hang forever on the first read, wedging the
        // worker. The test passing quickly (rather than the xUnit timeout killing it) is the proof.
        var source = new HangingBatchSource();
        var report = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Retry(r => r.Timeout(TimeSpan.FromMilliseconds(100)))
            .OnFailure(f => f.AbortReport())
            .Build();

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        source.WasCancelled.ShouldBeTrue();
    }

    [Fact]
    public async Task Attempt_timeout_does_not_fire_for_a_read_that_completes_in_time()
    {
        var source = new FakeBatchSource<Sale>(new[] { Page(1, 2) });
        var report = Build(source, new FakeWriterFactory(), b => b.Retry(r => r.Timeout(TimeSpan.FromSeconds(30))));

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Completed);
    }

    [Fact]
    public async Task Timed_out_attempt_is_retried_up_to_the_attempt_limit()
    {
        // Every read times out; with 3 attempts the source is read 3 times before the run fails —
        // proving the timeout surfaces as a transient failure the retry strategy handles.
        var source = new HangingBatchSource();
        var report = new ReportBuilder<Sale>("r")
            .From(source)
            .WithPageSize(10)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()))
            .Retry(r => r.MaxAttempts(3).Constant(TimeSpan.Zero).Timeout(TimeSpan.FromMilliseconds(80)))
            .OnFailure(f => f.AbortReport())
            .Build();

        var result = await Run(report);

        result.Status.ShouldBe(ReportRunStatus.Failed);
        source.Calls.ShouldBe(3);
    }

    private sealed class HangingBatchSource : IBatchSource<Sale>
    {
        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

        public bool WasCancelled { get; private set; }
        public int Calls { get; private set; }

        public async Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            Calls++;
            try
            {
                // Far longer than the configured attempt timeout; the timeout cancels this token.
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            return new BatchResult<Sale>(Array.Empty<Sale>(), null, false);
        }
    }
}
