using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class RetryDefaultTests
{
    private static IReadOnlyList<Sale> Page(params long[] ids) =>
        ids.Select(id => new Sale(id, $"C{id}", id * 10m, DateTime.UnixEpoch)).ToArray();

    private static ReportBuilder<Sale> Builder() =>
        new ReportBuilder<Sale>("r")
            .From(new FakeBatchSource<Sale>(new[] { Page(1) }))
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory()));

    [Fact]
    public void Retry_is_off_by_default()
    {
        var report = Builder().Build();
        report.Retry.Attempts.ShouldBe(1); // one attempt = no retries
    }

    [Fact]
    public void Parameterless_Retry_enables_a_sensible_production_default()
    {
        var report = Builder().Retry().Build();

        report.Retry.Attempts.ShouldBe(3);
        report.Retry.Backoff.ShouldBe(RetryBackoff.Exponential);
        report.Retry.BaseDelay.ShouldBe(TimeSpan.FromSeconds(1));
        report.Retry.UseJitter.ShouldBeTrue();
    }

    [Fact]
    public void Retry_with_configure_still_tunes_the_knobs()
    {
        var report = Builder().Retry(r => r.MaxAttempts(5).Constant(TimeSpan.FromMilliseconds(250))).Build();

        report.Retry.Attempts.ShouldBe(5);
        report.Retry.Backoff.ShouldBe(RetryBackoff.Constant);
        report.Retry.BaseDelay.ShouldBe(TimeSpan.FromMilliseconds(250));
    }
}
