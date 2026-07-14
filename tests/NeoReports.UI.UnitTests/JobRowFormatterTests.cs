using NeoReports.UI.Models;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests;

/// <summary>
/// <see cref="JobRowFormatter"/> — shared row/route mapping used by both Dashboard.razor's "Recent
/// jobs" card and Jobs.razor's full list (found duplicated and drifting during a 2026-07 UI audit:
/// Dashboard's progress column used to just echo the raw status string instead of a real percentage).
/// </summary>
public class JobRowFormatterTests
{
    private static ApiJobView Job(
        string status, string id = "job1", string report = "sales",
        DateTimeOffset? started = null, DateTimeOffset? completed = null, ApiJobStats? stats = null) =>
        new(id, report, status, DateTimeOffset.UtcNow, started, completed, null,
            stats ?? new ApiJobStats(0, 0, 0, 0, 0));

    [Theory]
    [InlineData("Running", "job1")]
    [InlineData("Retrying", "job1")]
    [InlineData("Queued", "job1")]
    public void Route_for_non_terminal_statuses_goes_to_the_running_page(string status, string id)
    {
        JobRowFormatter.ToRow(Job(status, id)).Route.ShouldBe($"jobs/{id}");
    }

    [Fact]
    public void Route_for_completed_goes_to_the_completed_page()
    {
        JobRowFormatter.ToRow(Job("Completed", "job2")).Route.ShouldBe("jobs/completed/job2");
    }

    [Fact]
    public void Route_for_failed_goes_to_the_failed_page()
    {
        JobRowFormatter.ToRow(Job("Failed", "job3")).Route.ShouldBe("jobs/failed/job3");
    }

    [Fact]
    public void Route_for_cancelled_reuses_the_failed_page()
    {
        // Matches JobRunning.razor's own terminal-state redirect (`job.Status is "Failed" or
        // "Cancelled"`) — there is no separate cancelled-job screen.
        JobRowFormatter.ToRow(Job("Cancelled", "job4")).Route.ShouldBe("jobs/failed/job4");
    }

    [Fact]
    public void ProgressLabel_is_a_dash_for_a_running_job_even_with_a_known_total()
    {
        // JobStats only persists once, at job completion — a Running job's Stats read zero
        // regardless of real progress, so a percentage here would be fabricated, not real.
        var stats = new ApiJobStats(400, 400, 0, 0, 0, TotalRecords: 1000);
        JobRowFormatter.ToRow(Job("Running", stats: stats)).ProgressLabel.ShouldBe("—");
    }

    [Fact]
    public void ProgressLabel_computes_a_real_percentage_for_a_completed_job()
    {
        var stats = new ApiJobStats(250, 250, 0, 0, 0, TotalRecords: 1000);
        JobRowFormatter.ToRow(Job("Completed", stats: stats)).ProgressLabel.ShouldBe("25%");
    }

    [Fact]
    public void ProgressLabel_is_a_dash_when_the_total_is_unknown()
    {
        var stats = new ApiJobStats(250, 250, 0, 0, 0, TotalRecords: null);
        JobRowFormatter.ToRow(Job("Completed", stats: stats)).ProgressLabel.ShouldBe("—");
    }

    [Fact]
    public void ProgressLabel_is_a_dash_when_the_total_is_zero_not_a_stalled_zero_percent()
    {
        var stats = new ApiJobStats(0, 0, 0, 0, 0, TotalRecords: 0);
        JobRowFormatter.ToRow(Job("Completed", stats: stats)).ProgressLabel.ShouldBe("—");
    }

    [Fact]
    public void ProgressLabel_clamps_to_100_percent_when_read_exceeds_the_total()
    {
        var stats = new ApiJobStats(1200, 1200, 0, 0, 0, TotalRecords: 1000);
        JobRowFormatter.ToRow(Job("Completed", stats: stats)).ProgressLabel.ShouldBe("100%");
    }

    [Fact]
    public void ProgressLabel_is_a_dash_for_a_failed_job_with_no_total()
    {
        JobRowFormatter.ToRow(Job("Failed")).ProgressLabel.ShouldBe("—");
    }

    [Theory]
    [InlineData("Completed", JobStatus.Ok)]
    [InlineData("Failed", JobStatus.Failed)]
    [InlineData("Running", JobStatus.Running)]
    [InlineData("Retrying", JobStatus.Running)]
    [InlineData("Paused", JobStatus.Paused)]
    [InlineData("Queued", JobStatus.Queued)]
    [InlineData("Cancelled", JobStatus.Failed)]
    public void Status_maps_the_wire_status_string_to_the_UI_enum(string wireStatus, JobStatus expected)
    {
        JobRowFormatter.ToRow(Job(wireStatus)).Status.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Completed", "Completed")]
    [InlineData("Cancelled", "Cancelled")]
    [InlineData("Failed", null)]
    [InlineData("Running", null)]
    [InlineData("Queued", null)]
    public void StatusLabel_overrides_the_badge_default_only_for_completed_and_cancelled(string wireStatus, string? expected)
    {
        // Cancelled shares Failed's badge styling (via Status) but keeps its own word — a
        // deliberate stop isn't the same story as an error.
        JobRowFormatter.ToRow(Job(wireStatus)).StatusLabel.ShouldBe(expected);
    }

    [Fact]
    public void Started_is_a_dash_when_the_job_never_started()
    {
        JobRowFormatter.ToRow(Job("Queued", started: null)).Started.ShouldBe("—");
    }

    [Fact]
    public void Duration_is_a_dash_when_the_job_has_not_completed()
    {
        JobRowFormatter.ToRow(Job("Running", started: DateTimeOffset.UtcNow)).Duration.ShouldBe("—");
    }
}
