using Microsoft.Playwright;
using Shouldly;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// A browser page opened on the UI, with the waiting rules a Blazor Server app needs.
/// <para>
/// Interactivity only exists once the SignalR circuit is up, and the shell HTML arrives before that —
/// so navigating and immediately clicking races the circuit. <see cref="OpenAsync"/> waits for the
/// circuit to be established before handing the page over, which removes that whole class of flake.
/// </para>
/// </summary>
public sealed class UiPage : IAsyncDisposable
{
    /// <summary>Generous enough for a cold circuit on a loaded CI runner, short enough to fail fast.</summary>
    public const int Timeout = 15_000;

    private UiPage(IPage page) => Page = page;

    /// <summary>The underlying Playwright page.</summary>
    public IPage Page { get; }

    /// <summary>Opens a UI route and waits until the Blazor circuit is live.</summary>
    public static async Task<UiPage> OpenAsync(WebUiFixture fixture, string route)
    {
        IPage page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.App.Ui(route), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The host renders with ServerPrerendered, so the markup — and `window.Blazor` — exist well
        // before the circuit attaches; waiting on those would let a click land on inert HTML and be
        // dropped silently. Blazor strips its `<!--Blazor:` prerender comment markers when it takes
        // over the component, so their absence is the signal that handlers are actually wired.
        await page.WaitForFunctionAsync(
            "() => window.Blazor !== undefined && !document.documentElement.innerHTML.includes('<!--Blazor:')",
            null,
            new PageWaitForFunctionOptions { Timeout = Timeout });

        return new UiPage(page);
    }

    /// <summary>
    /// Fails if Blazor's disconnect/error overlay is showing. Blazor renders that instead of crashing
    /// the page, so an unhandled exception in a component otherwise leaves a green-looking test.
    /// </summary>
    public async Task AssertNoCircuitErrorAsync()
    {
        // Read the INLINE style, not the computed one: Blazor reveals this element by setting
        // `style.display = 'block'` itself, whereas the computed value also reflects the stylesheet —
        // so a page whose CSS failed to load would look permanently faulted.
        bool errored = await Page.EvaluateAsync<bool>(
            "() => { const ui = document.getElementById('blazor-error-ui'); " +
            "return ui !== null && ui.style.display === 'block'; }");

        errored.ShouldBeFalse("The Blazor error UI is visible — the circuit faulted (see the app's logs).");
    }

    /// <summary>Waits for text to appear anywhere on the page.</summary>
    public Task WaitForTextAsync(string text) =>
        Page.GetByText(text).First.WaitForAsync(new LocatorWaitForOptions { Timeout = Timeout });

    public async ValueTask DisposeAsync()
    {
        IBrowserContext context = Page.Context;
        try
        {
            await Page.CloseAsync();
        }
        finally
        {
            // Closing the context is what actually frees the browser resources, so it must happen even
            // if the page is already gone — and a throw here would replace the test's real failure.
            await context.CloseAsync();
        }
    }
}
