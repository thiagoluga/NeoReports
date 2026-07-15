using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class SystemMemoryTests : NeoReportsTestContext
{
    [Fact]
    public void Engine_unreachable_shows_the_unreachable_empty_state()
    {
        Api.Memory = _ => Task.FromResult<ApiMemory?>(null);

        var cut = Render<SystemMemory>();

        cut.Find(".es-title").TextContent.ShouldBe("Engine unreachable");
    }

    [Fact]
    public void Live_reading_formats_bytes_into_human_readable_units()
    {
        Api.Memory = _ => Task.FromResult<ApiMemory?>(new ApiMemory(500, 2048, 3_145_728, DateTimeOffset.UtcNow, 0));
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<SystemMemory>();

        cut.Markup.ShouldContain("500 B");
        cut.Markup.ShouldContain("2 KB");
        cut.Markup.ShouldContain("3 MB");
    }

    [Fact]
    public void No_running_jobs_shows_the_no_jobs_running_empty_state()
    {
        Api.Memory = _ => Task.FromResult<ApiMemory?>(new ApiMemory(0, 0, 0, DateTimeOffset.UtcNow, 0));
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<SystemMemory>();

        cut.FindAll(".es-title").ShouldContain(e => e.TextContent == "No jobs running");
    }

    [Fact]
    public void Running_and_retrying_jobs_are_combined_with_their_raw_status_label()
    {
        Api.Memory = _ => Task.FromResult<ApiMemory?>(new ApiMemory(0, 0, 0, DateTimeOffset.UtcNow, 2));
        Api.Jobs = (_, _, _, status, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(status switch
        {
            "Running" => [new ApiJobView("job-r", "clientsVip", "Running", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, new ApiJobStats(0, 0, 0, 0, 0))],
            "Retrying" => [new ApiJobView("job-t", "salesDaily", "Retrying", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, new ApiJobStats(0, 0, 0, 0, 0))],
            _ => Array.Empty<ApiJobView>(),
        });

        var cut = Render<SystemMemory>();

        cut.Markup.ShouldContain("clientsVip");
        cut.Markup.ShouldContain("salesDaily");
        cut.Markup.ShouldContain("2 in progress");
    }

    [Fact]
    public void Clicking_a_running_job_row_navigates_to_its_live_page()
    {
        Api.Memory = _ => Task.FromResult<ApiMemory?>(new ApiMemory(0, 0, 0, DateTimeOffset.UtcNow, 1));
        Api.Jobs = (_, _, _, status, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(status == "Running"
            ? [new ApiJobView("job-1", "clientsVip", "Running", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, new ApiJobStats(0, 0, 0, 0, 0))]
            : Array.Empty<ApiJobView>());

        var cut = Render<SystemMemory>();
        cut.Find("tr.clickable").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/job-1");
    }
}
