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
