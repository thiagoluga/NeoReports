using NeoReports.Abstractions;
using NeoReports.Core.Building;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Unit tests for <see cref="FailureStrategyBuilder"/>'s data-based abort-threshold overload
/// (ADR D37): validation and introspection, independent of a full pipeline run (see
/// <c>ResilienceTests</c> for the end-to-end escalation behavior).
/// </summary>
public class FailureStrategyBuilderTests
{
    [Fact]
    public void AbortThresholds_is_null_when_never_configured()
    {
        var builder = new FailureStrategyBuilder().SkipBatchAndLog();
        builder.AbortThresholds.ShouldBeNull();
    }

    [Fact]
    public void AbortThresholds_is_null_when_a_custom_predicate_is_used()
    {
        var builder = new FailureStrategyBuilder().SkipBatchAndLog().AbortIf(t => t.ConsecutiveFailures(3));
        builder.AbortThresholds.ShouldBeNull();
    }

    [Fact]
    public void AbortThresholds_reflects_the_data_based_overload()
    {
        var thresholds = new AbortThresholdConfig(ConsecutiveFailures: 3, TotalFailures: 10, FailureRate: 0.5);
        var builder = new FailureStrategyBuilder().SkipBatchAndLog().AbortIf(thresholds);

        builder.AbortThresholds.ShouldBe(thresholds);
    }

    [Fact]
    public void AbortThresholds_reverts_to_null_after_a_later_predicate_overload_call()
    {
        var builder = new FailureStrategyBuilder().SkipBatchAndLog()
            .AbortIf(new AbortThresholdConfig(ConsecutiveFailures: 3))
            .AbortIf(t => t.TotalFailures(10));

        builder.AbortThresholds.ShouldBeNull();
    }

    [Fact]
    public void Rejects_an_empty_threshold_config()
    {
        var builder = new FailureStrategyBuilder().SkipBatchAndLog();
        Should.Throw<ConfigurationException>(() => builder.AbortIf(new AbortThresholdConfig()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_non_positive_consecutive_count(int value)
    {
        var builder = new FailureStrategyBuilder().SkipBatchAndLog();
        Should.Throw<ConfigurationException>(() => builder.AbortIf(new AbortThresholdConfig(ConsecutiveFailures: value)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_non_positive_total_count(int value)
    {
        var builder = new FailureStrategyBuilder().SkipBatchAndLog();
        Should.Throw<ConfigurationException>(() => builder.AbortIf(new AbortThresholdConfig(TotalFailures: value)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Rejects_an_out_of_range_failure_rate(double value)
    {
        var builder = new FailureStrategyBuilder().SkipBatchAndLog();
        Should.Throw<ConfigurationException>(() => builder.AbortIf(new AbortThresholdConfig(FailureRate: value)));
    }

    [Fact]
    public void Accepts_a_failure_rate_of_exactly_one()
    {
        var builder = new FailureStrategyBuilder().SkipBatchAndLog();
        Should.NotThrow(() => builder.AbortIf(new AbortThresholdConfig(FailureRate: 1)));
    }
}
