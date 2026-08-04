using Microsoft.Playwright;
using Xunit;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// One running app and one browser, shared by every E2E class: starting Kestrel and launching
/// Chromium are the expensive parts, and each test gets its own isolated browser context anyway.
/// </summary>
public sealed class WebUiFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    /// <summary>The running application under test.</summary>
    public WebUiApp App { get; private set; } = null!;

    /// <summary>The launched browser; only valid when <see cref="Unavailable"/> is null.</summary>
    public IBrowser Browser { get; private set; } = null!;

    /// <summary>Environment variable CI sets to demand a working browser.</summary>
    public const string RequireBrowserVariable = "NEOREPORTS_REQUIRE_BROWSER";

    /// <summary>
    /// Null when Chromium is usable. Locally, a missing browser makes the tests skip so a contributor
    /// who hasn't run `playwright install` can still run the rest of the repo's suites. CI sets
    /// <see cref="RequireBrowserVariable"/>, and then a browser that won't launch is a hard failure —
    /// installing it successfully is not the same as it working, and without this a driver mismatch or
    /// an OOM-killed browser would skip most of this suite while the build stayed green. Same stance,
    /// and same reasoning, as <c>tests/Shared/DockerGate.cs</c>.
    /// </summary>
    public string? Unavailable { get; private set; }

    /// <summary>Starts the app, then launches Chromium (recording why if it is missing).</summary>
    public async Task InitializeAsync()
    {
        App = await WebUiApp.StartAsync();

        try
        {
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (Exception ex) when (ex is PlaywrightException or FileNotFoundException or InvalidOperationException)
        {
            if (Environment.GetEnvironmentVariable(RequireBrowserVariable) == "1")
                throw;

            Unavailable = $"Chromium is not available ({ex.GetType().Name}). Run `playwright install chromium`.";
        }
    }

    /// <summary>A fresh, isolated page (own cookies/storage) for one test.</summary>
    public async Task<IPage> NewPageAsync()
    {
        IBrowserContext context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = App.BaseUrl,
            // Blazor Server renders through a circuit; a viewport avoids layout-dependent hit-testing.
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        return await context.NewPageAsync();
    }

    /// <summary>Closes the browser and stops the app.</summary>
    public async Task DisposeAsync()
    {
        // Each step is independent: a browser that already crashed must not stop Kestrel and the temp
        // directory from being cleaned up, and a teardown error must never replace the failure the
        // test was actually reporting.
        try
        {
            if (Browser is not null)
                await Browser.CloseAsync();
        }
        catch (PlaywrightException ex)
        {
            // Swallowed on purpose — a browser that already died must not stop the steps below from
            // running, and must not replace whatever failure the test was reporting. Still say so:
            // silently discarding it would hide a browser that is crashing on every run.
            Console.WriteLine($"E2E teardown: closing the browser failed and was ignored — {ex.Message}");
        }

        _playwright?.Dispose();

        // Null when StartAsync itself threw — surfacing that original error matters more than this.
        if (App is not null)
            await App.DisposeAsync();
    }
}

/// <summary>Shares one <see cref="WebUiFixture"/> across every E2E class (xUnit runs them in sequence).</summary>
[CollectionDefinition(nameof(WebUiCollection))]
public sealed class WebUiCollection : ICollectionFixture<WebUiFixture>
{
}
