using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class JobsTests : NeoReportsTestContext
{
    private static ApiJobView Job(string id, string reportName, string status = "Completed") =>
        new(id, reportName, status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, new ApiJobStats(0, 0, 0, 0, 0));

    [Fact]
    public void Engine_unreachable_shows_the_unreachable_empty_state()
    {
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(null);

        var cut = Render<Jobs>();

        cut.Find(".es-title").TextContent.ShouldBe("Engine unreachable");
    }

    [Fact]
    public void Live_with_no_matching_jobs_shows_the_no_match_empty_state()
    {
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<Jobs>();

        cut.Find(".es-title").TextContent.ShouldBe("No jobs match this filter");
    }

    [Fact]
    public void Changing_the_status_filter_requests_that_status_from_the_engine()
    {
        var lastStatusArg = default(string?);
        Api.Jobs = (_, _, _, status, _) => { lastStatusArg = status; return Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>()); };

        var cut = Render<Jobs>();
        cut.Find("select").Change("Completed");

        lastStatusArg.ShouldBe("Completed");
    }

    [Fact]
    public void Clicking_a_row_navigates_to_its_route()
    {
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>([Job("job-7", "clientsVip")]);

        var cut = Render<Jobs>();
        cut.Find("tr.clickable").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/completed/job-7");
    }

    [Fact]
    public void A_stale_slow_response_never_overwrites_a_newer_faster_one()
    {
        var slowFirstCall = new TaskCompletionSource<IReadOnlyList<ApiJobView>?>();
        var callCount = 0;
        Api.Jobs = (_, _, _, _, _) =>
        {
            callCount++;
            return callCount == 1
                ? slowFirstCall.Task
                : Task.FromResult<IReadOnlyList<ApiJobView>?>([Job("job-fresh", "freshReport")]);
        };

        var cut = Render<Jobs>();
        cut.Find("select").Change("Completed");

        cut.Markup.ShouldContain("freshReport");

        slowFirstCall.SetResult([Job("job-old", "staleReport")]);
        cut.WaitForState(() => callCount == 2, TimeSpan.FromSeconds(2));

        cut.Markup.ShouldContain("freshReport");
        cut.Markup.ShouldNotContain("staleReport");
    }
}
