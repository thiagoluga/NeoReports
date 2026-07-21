using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class DashboardTests : NeoReportsTestContext
{
    [Fact]
    public void New_report_navigates_to_the_builder()
    {
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<Dashboard>();
        cut.FindAll("button").First(b => b.TextContent.Contains("New report")).Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("builder");
    }

    [Fact]
    public void Engine_unreachable_shows_unreachable_states_and_hides_the_sources_card()
    {
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(null);

        var cut = Render<Dashboard>();

        cut.Find(".es-title").TextContent.ShouldBe("Engine unreachable");
        cut.Markup.ShouldContain("Engine unreachable");
        cut.Markup.ShouldNotContain("Most referenced sources");
    }

    [Fact]
    public void Live_with_no_jobs_shows_no_recent_jobs_empty_state()
    {
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());
        Api.Sources = _ => Task.FromResult<IReadOnlyList<ApiSourceView>?>(Array.Empty<ApiSourceView>());

        var cut = Render<Dashboard>();

        cut.Find(".es-title").TextContent.ShouldBe("No recent jobs");
    }

    [Fact]
    public void Live_with_jobs_renders_a_row_per_job_and_navigates_on_click()
    {
        var job = new ApiJobView(
            "job-1", "clientsVip", "Completed", DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
            null, new ApiJobStats(100, 100, 1024, 0, 1));
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>([job]);
        Api.Sources = _ => Task.FromResult<IReadOnlyList<ApiSourceView>?>(Array.Empty<ApiSourceView>());

        var cut = Render<Dashboard>();

        cut.Markup.ShouldContain("clientsVip");
        cut.Find("tr.clickable").Click();

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri
            .ShouldEndWith("jobs/completed/job-1");
    }

    [Fact]
    public void Most_referenced_sources_card_shows_only_sources_with_at_least_one_reference_sorted_desc()
    {
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());
        Api.Sources = _ => Task.FromResult<IReadOnlyList<ApiSourceView>?>(
        [
            new ApiSourceView("unused-db", "sql", null, 0, null, null, null, null),
            new ApiSourceView("postgres-demo", "postgres", null, 3, "healthy", null, null, null),
            new ApiSourceView("mysql-demo", "mysql", null, 7, "healthy", null, null, null),
        ]);

        var cut = Render<Dashboard>();

        cut.Markup.ShouldContain("Most referenced sources");
        cut.Markup.ShouldNotContain("unused-db");
        var names = cut.FindAll(".name.mono").Select(e => e.TextContent).ToArray();
        names.ShouldBe(["mysql-demo", "postgres-demo"]);
    }
}
