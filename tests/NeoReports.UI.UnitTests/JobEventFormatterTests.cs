using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests;

/// <summary>ADR D38: <see cref="JobEventFormatter"/>.</summary>
public class JobEventFormatterTests
{
    private static ApiJobEvent Event(
        string type, int sequence = 1, string? message = null, IReadOnlyDictionary<string, string>? data = null) =>
        new(sequence, DateTimeOffset.UtcNow, type, message, data);

    [Fact]
    public void ToTimelineRow_maps_run_started_with_no_detail()
    {
        var row = JobEventFormatter.ToTimelineRow(Event("run-started"));
        row.Kind.ShouldBe("info");
        row.Text.ShouldBe("Run started");
        row.Detail.ShouldBeNull();
    }

    [Fact]
    public void ToTimelineRow_maps_page_completed_with_cumulative_detail()
    {
        var data = new Dictionary<string, string> { ["page"] = "3", ["recordsWritten"] = "42", ["elapsedMs"] = "1500" };
        var row = JobEventFormatter.ToTimelineRow(Event("page-completed", data: data));

        row.Kind.ShouldBe("ok");
        row.Text.ShouldBe("Page 3 written");
        row.Detail.ShouldBe("42 rows written so far · 1500ms elapsed");
    }

    [Fact]
    public void ToTimelineRow_maps_retry_with_page_and_attempt_and_message()
    {
        var data = new Dictionary<string, string> { ["page"] = "2", ["attempt"] = "1", ["exceptionType"] = "InvalidOperationException" };
        var row = JobEventFormatter.ToTimelineRow(Event("retry", data: data, message: "boom"));

        row.Kind.ShouldBe("warn");
        row.Text.ShouldBe("Retry on page 2 (attempt 1)");
        row.Detail.ShouldBe("InvalidOperationException · boom");
    }

    [Fact]
    public void ToTimelineRow_maps_run_failed_with_the_message_as_detail()
    {
        var row = JobEventFormatter.ToTimelineRow(Event("run-failed", message: "everything broke"));
        row.Kind.ShouldBe("err");
        row.Detail.ShouldBe("everything broke");
    }

    [Fact]
    public void ToTimelineRow_falls_back_for_an_unknown_type()
    {
        var row = JobEventFormatter.ToTimelineRow(Event("some-future-type", message: "x"));
        row.Kind.ShouldBe("info");
        row.Text.ShouldBe("some-future-type");
    }

    [Fact]
    public void ToRateSeries_extracts_only_page_completed_events_in_order()
    {
        var events = new[]
        {
            Event("run-started", 1),
            Event("page-completed", 2, data: new Dictionary<string, string> { ["elapsedMs"] = "100", ["recordsWritten"] = "10" }),
            Event("retry", 3),
            Event("page-completed", 4, data: new Dictionary<string, string> { ["elapsedMs"] = "300", ["recordsWritten"] = "25" }),
        };

        var series = JobEventFormatter.ToRateSeries(events);

        series.Count.ShouldBe(2);
        series[0].ShouldBe(new JobEventFormatter.RatePoint(100, 10));
        series[1].ShouldBe(new JobEventFormatter.RatePoint(300, 25));
    }

    [Fact]
    public void ToIntervalRates_computes_rows_per_second_between_consecutive_points()
    {
        var series = new[]
        {
            new JobEventFormatter.RatePoint(0, 0),
            new JobEventFormatter.RatePoint(1000, 10), // 10 rows in 1s => 10 rows/s
            new JobEventFormatter.RatePoint(3000, 30), // 20 rows in 2s => 10 rows/s
        };

        var rates = JobEventFormatter.ToIntervalRates(series);

        rates.Count.ShouldBe(2);
        rates[0].ShouldBe(10.0);
        rates[1].ShouldBe(10.0);
    }

    [Fact]
    public void ToIntervalRates_returns_empty_for_fewer_than_two_points()
    {
        JobEventFormatter.ToIntervalRates(Array.Empty<JobEventFormatter.RatePoint>()).ShouldBeEmpty();
        JobEventFormatter.ToIntervalRates(new[] { new JobEventFormatter.RatePoint(0, 0) }).ShouldBeEmpty();
    }
}
