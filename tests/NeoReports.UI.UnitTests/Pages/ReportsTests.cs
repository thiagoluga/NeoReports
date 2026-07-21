using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class ReportsTests : NeoReportsTestContext
{
    private static ApiReportSummary Report(string name) => new(name, 1, ["Id", "Name"]);

    [Fact]
    public void Engine_unreachable_shows_the_unreachable_empty_state()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>(null);

        var cut = Render<Reports>();

        cut.Find(".es-title").TextContent.ShouldBe("Engine unreachable");
    }

    [Fact]
    public void Live_with_no_reports_shows_no_match_empty_state_with_a_new_report_action()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>(Array.Empty<ApiReportSummary>());
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<Reports>();

        cut.Find(".es-title").TextContent.ShouldBe("No reports match your filters");
    }

    [Fact]
    public void Header_new_report_button_navigates_to_the_builder()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>([Report("clientsVip")]);
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<Reports>();
        cut.FindAll("button").First(b => b.TextContent.Contains("New report")).Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("builder");
    }

    [Fact]
    public void Empty_state_new_report_button_navigates_to_the_builder()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>(Array.Empty<ApiReportSummary>());
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<Reports>();
        cut.FindAll("button").Last(b => b.TextContent.Contains("New report")).Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("builder");
    }

    [Fact]
    public void Search_filters_reports_by_name_case_insensitively()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>([Report("clientsVip"), Report("salesDaily")]);
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<Reports>();
        cut.Markup.ShouldContain("clientsVip");
        cut.Markup.ShouldContain("salesDaily");

        cut.Find(".filter-bar input").Input("VIP");

        cut.Markup.ShouldContain("clientsVip");
        cut.Markup.ShouldNotContain("salesDaily");
    }

    [Fact]
    public void Opening_a_report_card_navigates_to_its_escaped_slug()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>([Report("clients vip")]);
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<Reports>();
        cut.Find(".report-card").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"reports/{Uri.EscapeDataString("clients vip")}");
    }

    [Fact]
    public void Running_a_report_triggers_TryRunReportAsync_and_navigates_to_the_new_job()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>([Report("clientsVip")]);
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());
        Api.RunReport = (_, _) => Task.FromResult<string?>("job-42");

        var cut = Render<Reports>();
        cut.Find(".actions .btn").Click();

        Api.LastRunReportName.ShouldBe("clientsVip");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/job-42");
    }

    [Fact]
    public void Running_a_report_that_fails_to_trigger_navigates_to_jobs_failed()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>([Report("clientsVip")]);
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());
        Api.RunReport = (_, _) => Task.FromResult<string?>(null);

        var cut = Render<Reports>();
        cut.Find(".actions .btn").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/failed");
    }

    [Fact]
    public void Count_strip_shows_running_and_failed_counts_computed_from_jobs()
    {
        Api.Reports = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>([Report("a")]);
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(
        [
            new ApiJobView("1", "a", "Running", DateTimeOffset.UtcNow, null, null, null, new ApiJobStats(0, 0, 0, 0, 0)),
            new ApiJobView("2", "a", "Retrying", DateTimeOffset.UtcNow, null, null, null, new ApiJobStats(0, 0, 0, 0, 0)),
            new ApiJobView("3", "a", "Failed", DateTimeOffset.UtcNow, null, null, "boom", new ApiJobStats(0, 0, 0, 0, 0)),
        ]);

        var cut = Render<Reports>();

        var counts = cut.FindAll(".count-strip .c b").Select(e => e.TextContent).ToArray();
        counts.ShouldBe(["2", "1"]);
    }
}
