using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// Every top-level screen loads in a real browser against the real host. This is the breadth pass: it
/// catches a route that 500s, a component that throws on first render, and — via
/// <see cref="UiPage.AssertNoCircuitErrorAsync"/> — a Blazor circuit that died, which a plain HTTP
/// check cannot see because the shell HTML returns 200 either way.
/// </summary>
[Collection(nameof(WebUiCollection))]
public class NavigationTests
{
    private readonly WebUiFixture _fixture;

    public NavigationTests(WebUiFixture fixture) => _fixture = fixture;

    public static TheoryData<string, string> Screens() => new()
    {
        { "", "Dashboard" },
        { "/reports", "Reports" },
        { "/jobs", "Jobs" },
        { "/sources", "Sources" },
        { "/system/memory", "Memory" },
        { "/builder", "Choose the data source" },
    };

    [SkippableTheory]
    [MemberData(nameof(Screens))]
    public async Task Screen_loads_and_shows_its_heading(string route, string heading)
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        await using var ui = await UiPage.OpenAsync(_fixture, route);

        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = heading }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });

        await ui.AssertNoCircuitErrorAsync();
    }

    [SkippableFact]
    public async Task The_left_nav_moves_between_screens_without_a_full_reload()
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        await using var ui = await UiPage.OpenAsync(_fixture, "");

        // Client-side routing over the circuit: if the link fell back to a full navigation the page
        // would still show Jobs, so assert the URL too rather than only the heading.
        await ui.Page.GetByRole(AriaRole.Link, new() { Name = "Jobs" }).First.ClickAsync();

        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Jobs" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        ui.Page.Url.ShouldContain("/jobs");

        await ui.AssertNoCircuitErrorAsync();
    }
}
