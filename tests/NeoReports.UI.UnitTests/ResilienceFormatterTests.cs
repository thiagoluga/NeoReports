using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests;

/// <summary>ADR D37: <see cref="ResilienceFormatter.FormatAbortThresholds"/>.</summary>
public class ResilienceFormatterTests
{
    private static ApiReportDetail Detail(
        int? consecutive = null, int? total = null, double? rate = null) => new(
        Name: "r",
        Columns: Array.Empty<ApiReportColumn>(),
        PageSize: 1000,
        Formats: Array.Empty<string>(),
        Destinations: Array.Empty<string>(),
        FailureStrategy: "skip-and-log",
        RetryMaxAttempts: 1,
        RetryBackoff: "Constant",
        RetryBaseDelaySeconds: 1,
        RetryUseJitter: false,
        Origin: "code",
        Deletable: false,
        AbortAfterConsecutiveFailures: consecutive,
        AbortAfterTotalFailures: total,
        AbortAtFailureRate: rate);

    [Fact]
    public void Returns_null_when_no_threshold_is_set() =>
        ResilienceFormatter.FormatAbortThresholds(Detail()).ShouldBeNull();

    [Fact]
    public void Renders_a_single_threshold() =>
        ResilienceFormatter.FormatAbortThresholds(Detail(consecutive: 3))
            .ShouldBe("abort after 3 consecutive failure(s)");

    [Fact]
    public void Renders_every_configured_threshold_joined_by_or() =>
        ResilienceFormatter.FormatAbortThresholds(Detail(consecutive: 3, total: 10, rate: 0.5))
            .ShouldBe("abort after 3 consecutive failure(s) or 10 total failure(s) or 50% failure rate");
}
