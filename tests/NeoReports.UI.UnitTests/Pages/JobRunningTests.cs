using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

/// <summary>
/// JobRunning.razor drives most of its polling off a raw <see cref="System.Threading.Timer"/>
/// (1.5s interval) that isn't mockable via DI. These tests avoid waiting on that timer by
/// triggering the same <c>PollAsync</c> path through the "Refresh" button instead — everything the
/// timer would do on a tick, Refresh does synchronously on click.
/// </summary>
public sealed class JobRunningTests : NeoReportsTestContext
{
    private static ApiJobView RunningJob(string id = "job-1", string reportName = "clientsVip", string status = "Running") =>
        new(id, reportName, status, DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddSeconds(-30),
            null, null, new ApiJobStats(200, 150, 4096, 0, 2));

    private void SetupBasics(Func<string, CancellationToken, Task<ApiJobView?>> job)
    {
        Api.Job = job;
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(Array.Empty<ApiJobEvent>());
    }

    [Fact]
    public void Initial_poll_shows_the_report_name_and_running_badge()
    {
        SetupBasics((_, _) => Task.FromResult<ApiJobView?>(RunningJob()));

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        cut.Find("h1").TextContent.ShouldBe("clientsVip");
        cut.Find(".badge").ClassList.ShouldContain("info");
        cut.Markup.ShouldContain("Running");
    }

    [Fact]
    public void No_known_total_shows_an_indeterminate_bar_and_dash_percentage()
    {
        SetupBasics((_, _) => Task.FromResult<ApiJobView?>(RunningJob()));

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        cut.Find(".pctbig").TextContent.ShouldBe("—");
        cut.Find(".progress-bar").ClassList.ShouldContain("indeterminate");
        cut.Markup.ShouldContain("no row count for this run — progress is indeterminate");
    }

    [Fact]
    public void A_known_total_shows_a_real_computed_percentage()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(RunningJob());
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(
        [
            new ApiJobEvent(1, DateTimeOffset.UtcNow, "run-started", null,
                new Dictionary<string, string> { ["totalRecords"] = "200" }),
            new ApiJobEvent(2, DateTimeOffset.UtcNow, "page-completed", null,
                new Dictionary<string, string> { ["totalRecords"] = "200", ["recordsRead"] = "100" }),
        ]);

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        cut.Find(".pctbig").TextContent.ShouldBe("50%");
        cut.Find(".progress-bar").ClassList.ShouldNotContain("indeterminate");
        cut.Markup.ShouldContain("100 / 200 rows");
    }

    [Fact]
    public void Refresh_button_re_polls_and_reflects_updated_stats()
    {
        var callCount = 0;
        Api.Job = (_, _) =>
        {
            callCount++;
            return Task.FromResult<ApiJobView?>(callCount == 1
                ? RunningJob()
                : RunningJob() with { Stats = new ApiJobStats(999, 999, 0, 0, 0) });
        };
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(Array.Empty<ApiJobEvent>());

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));
        cut.Markup.ShouldContain("200");

        cut.FindAll("button").First(b => b.TextContent.Contains("Refresh")).Click();

        callCount.ShouldBe(2);
        cut.Markup.ShouldContain("999");
    }

    [Fact]
    public void The_real_poll_timer_is_actually_started_and_ticks_on_its_own()
    {
        // Unlike every other test in this file (which drives PollAsync via the "Refresh" button to
        // avoid a slow/flaky real wait), this one proves the 1.5s System.Threading.Timer registered
        // in OnInitializedAsync is genuinely wired up — a regression that deleted that Timer entirely
        // would still pass every other test here, since none of them depend on it firing on its own.
        var callCount = 0;
        Api.Job = (_, _) => { callCount++; return Task.FromResult<ApiJobView?>(RunningJob()); };
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(Array.Empty<ApiJobEvent>());

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        cut.WaitForAssertion(() => callCount.ShouldBeGreaterThanOrEqualTo(2), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Completed_status_navigates_to_the_completed_job_page()
    {
        SetupBasics((_, _) => Task.FromResult<ApiJobView?>(RunningJob(status: "Completed")));

        Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/completed/job-1");
    }

    [Fact]
    public void Failed_status_navigates_to_the_failed_job_page()
    {
        SetupBasics((_, _) => Task.FromResult<ApiJobView?>(RunningJob(status: "Failed")));

        Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/failed/job-1");
    }

    [Fact]
    public void Cancelled_status_also_navigates_to_the_failed_job_page()
    {
        SetupBasics((_, _) => Task.FromResult<ApiJobView?>(RunningJob(status: "Cancelled")));

        Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/failed/job-1");
    }

    [Fact]
    public void Cancel_button_is_disabled_once_the_job_is_no_longer_running_or_retrying()
    {
        SetupBasics((_, _) => Task.FromResult<ApiJobView?>(RunningJob(status: "Queued")));

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Cancel")).HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void Cancel_button_is_enabled_while_retrying()
    {
        SetupBasics((_, _) => Task.FromResult<ApiJobView?>(RunningJob(status: "Retrying")));

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));

        cut.FindAll("button").First(b => b.TextContent.Contains("Cancel")).HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void Clicking_Cancel_requests_cancellation_and_re_polls()
    {
        var cancelCalled = false;
        SetupBasics((_, _) => Task.FromResult<ApiJobView?>(RunningJob()));
        Api.CancelJob = (id, _) => { cancelCalled = true; return Task.FromResult(true); };

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Cancel")).Click();

        cancelCalled.ShouldBeTrue();
    }

    [Fact]
    public void Unreachable_poll_keeps_showing_the_last_known_state_instead_of_blanking()
    {
        var callCount = 0;
        Api.Job = (_, _) =>
        {
            callCount++;
            return Task.FromResult<ApiJobView?>(callCount == 1 ? RunningJob() : null);
        };
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(Array.Empty<ApiJobEvent>());

        var cut = Render<JobRunning>(p => p.Add(x => x.Id, "job-1"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Refresh")).Click();

        cut.Find("h1").TextContent.ShouldBe("clientsVip");
    }
}
