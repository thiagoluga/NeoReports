using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// The Builder wizard driven in a real browser: each step is a separate route whose state lives in a
/// scoped service on the circuit, so this is precisely the flow component-level tests cannot cover —
/// a broken step transition or a lost selection only shows up over a live circuit.
/// </summary>
[Collection(nameof(WebUiCollection))]
public class BuilderWizardTests
{
    private readonly WebUiFixture _fixture;

    public BuilderWizardTests(WebUiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Walks the wizard end to end and saves, exactly as a user would: source type, name + columns,
    /// format, destination, then Save on the review step.
    /// </summary>
    private static async Task CreateReportThroughWizardAsync(UiPage ui, string name, params string[] formats)
    {
        // Step 1 — source.
        await ui.Page.GetByText("inmemory").First.ClickAsync();
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        // Step 2 — name and the output columns the engine will project.
        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Configure the source" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        await ui.Page.GetByPlaceholder("monthly-sales").FillAsync(name);
        await ui.Page.GetByPlaceholder("Id, Customer, Amount").FillAsync("Id, Customer");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        // Step 3 — formats. The cards come from the engine's registered IWriterFactory instances.
        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Choose formats" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        // Wait for the cards themselves: this step fetches the engine's capabilities inside
        // OnInitializedAsync, so the first render — the one the heading above proves — shows the
        // "no formats registered" empty state. Probing with CountAsync() before that second render
        // lands would silently skip every toggle and leave the wizard's defaults in place.
        await ui.Page.Locator(".sel-card").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });

        // Converge on exactly the requested set rather than assuming what starts selected — the wizard
        // pre-selects every format it knows, so a plain "click the ones I want" would deselect them.
        // The card's label is a display name ("CSV", "Excel"); the extension is the one text that maps
        // 1:1 to the engine's format id.
        foreach (string format in new[] { "csv", "xlsx" })
        {
            ILocator card = ui.Page.Locator(".sel-card")
                .Filter(new LocatorFilterOptions { HasText = $".{format}" });
            if (await card.CountAsync() == 0)
                continue;

            bool isSelected = (await card.First.GetAttributeAsync("class"))!.Contains("selected", StringComparison.Ordinal);
            if (isSelected != formats.Contains(format))
                await card.First.ClickAsync();
        }
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        // Step 4 — destination. The wizard defaults to None, which is what this test wants: a report
        // that produces a downloadable artifact without uploading anywhere.
        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Choose a destination" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        // Step 5 — review and save.
        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Review and save" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Save report" }).ClickAsync();
    }

    [SkippableFact]
    public async Task The_source_picker_is_populated_by_the_engine_and_advances_to_configure()
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        await using var ui = await UiPage.OpenAsync(_fixture, "/builder");

        ILocator continueButton = ui.Page.GetByRole(AriaRole.Button, new() { Name = "Continue" });
        await continueButton.WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });

        // The host registered one IConfigSourceProvider ("inmemory"), so the engine offers it here —
        // this asserts the picker is populated from the live engine, not from demo data. (The wizard
        // auto-selects a lone provider, so asserting Continue's enabled state here would pass whether
        // or not the click did anything — the meaningful assertion is that the step advances.)
        await ui.Page.GetByText("inmemory").First.ClickAsync();
        await continueButton.ClickAsync();

        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Configure the source" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        await ui.AssertNoCircuitErrorAsync();
    }

    [SkippableFact]
    public async Task The_wizard_keeps_its_state_across_steps_and_back_navigation()
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        await using var ui = await UiPage.OpenAsync(_fixture, "/builder");

        await ui.Page.GetByText("inmemory").First.ClickAsync();
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Configure the source" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });

        // Name the report, then go back a step and forward again. D69 fixed a bug where any Back
        // navigation reset the whole wizard; this is that regression, exercised over a real circuit.
        await ui.Page.GetByPlaceholder("monthly-sales").FillAsync("wizard-state-check");
        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Back" }).ClickAsync();

        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Choose the data source" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });

        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Configure the source" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });

        (await ui.Page.GetByPlaceholder("monthly-sales").InputValueAsync()).ShouldBe("wizard-state-check");
        await ui.AssertNoCircuitErrorAsync();
    }

    [SkippableFact]
    public async Task A_report_created_through_the_wizard_is_registered_and_can_be_run()
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        string name = "e2e-wizard-" + Guid.NewGuid().ToString("N")[..6];

        await using var ui = await UiPage.OpenAsync(_fixture, "/builder");
        await CreateReportThroughWizardAsync(ui, name, "csv");

        // Saving navigates away from the wizard; the new report must be listed.
        await ui.Page.WaitForFunctionAsync(
            "() => !location.pathname.includes('/builder')",
            null,
            new PageWaitForFunctionOptions { Timeout = UiPage.Timeout });
        await ui.AssertNoCircuitErrorAsync();

        // It is a real registration, not just UI state: the engine's own API returns it, with the
        // shape the wizard collected.
        using var api = new ReportApi(_fixture.App);
        ReportApi.Report created = (await api.ReportsAsync()).Where(r => r.Name == name).ShouldHaveSingleItem();
        created.Formats.ShouldBe(new[] { "csv" });
        created.Columns.ShouldBe(new[] { "Id", "Customer" });

        // And it actually runs — a report you can save but not run would be a hollow pass.
        ReportApi.Job job = await api.RunToCompletionAsync(name);
        (await api.DownloadAsync(job.Id)).Length.ShouldBeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Cancelling_the_wizard_returns_to_the_reports_list()
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        await using var ui = await UiPage.OpenAsync(_fixture, "/builder");

        await ui.Page.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        await ui.Page.GetByRole(AriaRole.Heading, new() { Name = "Reports" }).First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        await ui.AssertNoCircuitErrorAsync();
    }

}
