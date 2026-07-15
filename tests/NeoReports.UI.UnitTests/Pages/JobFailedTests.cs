using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class JobFailedTests : NeoReportsTestContext
{
    private static ApiJobView FailedJob(string id = "job-1", string reportName = "clientsVip", string? error = "boom") => new(
        id, reportName, "Failed", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow, error, new ApiJobStats(100, 40, 512, 2, 3));

    private void SetupBasics(ApiJobView job)
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(job);
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(Array.Empty<ApiJobEvent>());
        Api.PartialArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>(Array.Empty<ApiArtifact>());
    }

    [Fact]
    public void No_id_given_shows_the_no_job_id_text()
    {
        var cut = Render<JobFailed>(p => p.Add(x => x.Id, (string?)null));

        cut.Markup.ShouldContain("No job id given.");
    }

    [Fact]
    public void Unknown_job_shows_not_found_naming_the_id()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(null);

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "ghost-job"));

        cut.Find(".es-title").TextContent.ShouldBe("Job not found");
        cut.Markup.ShouldContain("ghost-job");
    }

    [Fact]
    public void Known_job_shows_the_error_message_and_resume_stays_disabled()
    {
        SetupBasics(FailedJob(error: "Connection timed out"));

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));

        cut.Markup.ShouldContain("Connection timed out");
        cut.FindAll("button").First(b => b.TextContent.Contains("Resume")).HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void No_retry_events_shows_the_no_retries_message()
    {
        SetupBasics(FailedJob());

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));

        cut.Markup.ShouldContain("No retries so far.");
    }

    [Fact]
    public void Retry_events_are_filtered_from_the_full_event_list_and_counted_separately()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(FailedJob());
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(
        [
            new ApiJobEvent(1, DateTimeOffset.UtcNow, "run-started", null, null),
            new ApiJobEvent(2, DateTimeOffset.UtcNow, "retry", "attempt 2", null),
            new ApiJobEvent(3, DateTimeOffset.UtcNow, "retry", "attempt 3", null),
        ]);
        Api.PartialArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>(Array.Empty<ApiArtifact>());

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));

        cut.Markup.ShouldContain("3 event(s)");
        cut.Markup.ShouldContain("2 retry(ies)");
    }

    [Fact]
    public void No_partial_output_shows_the_honest_no_capture_message()
    {
        SetupBasics(FailedJob());

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));

        cut.Markup.ShouldContain("No partial output was captured for this job.");
    }

    [Fact]
    public void Partial_output_shows_the_best_effort_banner_and_file_list()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(FailedJob());
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(Array.Empty<ApiJobEvent>());
        Api.PartialArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>([new ApiArtifact("partial.csv", "text/csv", 128)]);

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));

        cut.Markup.ShouldContain("Best-effort output");
        cut.Markup.ShouldContain("partial.csv");
    }

    [Fact]
    public void Retry_triggers_a_run_and_navigates_to_the_new_job()
    {
        SetupBasics(FailedJob());
        Api.RunReport = (_, _) => Task.FromResult<string?>("job-2");

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));
        cut.FindAll("button").First(b => b.TextContent == "Retry").Click();

        Api.LastRunReportName.ShouldBe("clientsVip");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/job-2");
    }

    [Fact]
    public void Retry_when_the_trigger_fails_does_not_navigate()
    {
        SetupBasics(FailedJob());
        Api.RunReport = (_, _) => Task.FromResult<string?>(null);
        var nav = Services.GetRequiredService<NavigationManager>();
        var uriBefore = nav.Uri;

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));
        cut.FindAll("button").First(b => b.TextContent == "Retry").Click();

        nav.Uri.ShouldBe(uriBefore);
    }

    [Fact]
    public void Download_partial_navigates_to_the_built_partial_download_url()
    {
        Api.Job = (_, _) => Task.FromResult<ApiJobView?>(FailedJob());
        Api.JobEvents = (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(Array.Empty<ApiJobEvent>());
        Api.PartialArtifacts = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>([new ApiArtifact("partial.csv", "text/csv", 128)]);
        Api.PartialDownloadUrl = id => $"/api/jobs/{id}/partial-download";

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Download")).Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/api/jobs/job-1/partial-download");
    }

    [Fact]
    public void Edit_navigates_to_the_builder()
    {
        SetupBasics(FailedJob());

        var cut = Render<JobFailed>(p => p.Add(x => x.Id, "job-1"));
        cut.FindAll("button").First(b => b.TextContent == "Edit").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("builder");
    }
}
