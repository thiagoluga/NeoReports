using System.Net.Http.Json;
using System.Text;
using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// The headline flow: a user opens the UI, runs a report, watches the job finish and gets a file.
/// Everything here happens through the browser against the live host — the click travels over the
/// SignalR circuit, the engine runs for real, and the artifact is served by the real endpoint.
/// </summary>
[Collection(nameof(WebUiCollection))]
public class GenerateReportTests
{
    private readonly WebUiFixture _fixture;

    public GenerateReportTests(WebUiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Registers a dynamic report over the API. Creating it through the Builder wizard is covered
    /// separately; seeding it here keeps this test about running and delivering, and keeps it
    /// independent of the wizard's own state.
    /// </summary>
    private async Task<string> SeedReportAsync(string name, params string[] formats)
    {
        string outputs = string.Join(",", formats.Select(f => $$"""{"format":"{{f}}"}"""));
        string config = $$"""
        {
          "name": "{{name}}",
          "source": { "type": "inmemory", "properties": { "rows": 25 } },
          "columns": [ { "name": "Id", "type": "Integer" }, { "name": "Customer", "type": "String" } ],
          "outputs": [ {{outputs}} ],
          "destinations": [ { "type": "local" } ],
          "pageSize": 10
        }
        """;

        using var client = new HttpClient();
        using var body = new StringContent(config, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(_fixture.App.BaseUrl + "/api/reports", body);
        response.IsSuccessStatusCode.ShouldBeTrue($"seeding '{name}' failed: {await response.Content.ReadAsStringAsync()}");
        return name;
    }

    /// <summary>
    /// Filters the Reports list down to one report and clicks its Run action. Scoping matters: the
    /// host is shared by every test in the collection, so reports accumulate and a bare "first Run
    /// button" would start whichever report happens to sort first — a different one each run.
    /// </summary>
    private static async Task RunFromReportsPageAsync(UiPage ui, string name)
    {
        // Scope to the card that carries this report's name. Filtering the list first and taking the
        // first Run button would be a race: the filter re-renders over the circuit, so the click can
        // land while the unfiltered list is still shown — and the list is name-ordered, so it would
        // deterministically start whichever report sorts first, not this one.
        ILocator card = ui.Page.Locator(".report-card").Filter(new LocatorFilterOptions { HasText = name });
        await card.WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        await card.GetByRole(AriaRole.Button, new() { Name = "Run" }).ClickAsync();
    }

    [SkippableFact]
    public async Task Running_a_report_from_the_reports_page_produces_a_completed_job_and_a_file()
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        string name = await SeedReportAsync("e2e-run-" + Guid.NewGuid().ToString("N")[..6], "csv");

        await using var ui = await UiPage.OpenAsync(_fixture, "/reports");

        // The report the API registered must be visible to the UI without a manual refresh.
        await ui.WaitForTextAsync(name);

        // Run it the way a user does — search for it, then the card's primary action.
        await RunFromReportsPageAsync(ui, name);

        // The run is asynchronous. Let it finish before opening the Jobs screen: that page loads its
        // list once on init, so navigating straight after the click would render an empty list and the
        // assertion below would be timing, not behaviour.
        using var api = new ReportApi(_fixture.App);
        ReportApi.Job job = await api.WaitForReportCompletionAsync(name);

        await using var jobs = await UiPage.OpenAsync(_fixture, "/jobs");
        await jobs.WaitForTextAsync(name);

        // Scope the status to THIS job's row. A page-wide text match also hits the status filter's
        // hidden <option value="Completed">, which is present before any job has ever run — so the
        // assertion would pass on an empty Jobs page.
        ILocator row = jobs.Page.Locator("tr", new PageLocatorOptions { HasText = name });
        (await row.First.InnerTextAsync()).ShouldContain("Completed");

        await jobs.AssertNoCircuitErrorAsync();

        (await api.DownloadAsync(job.Id)).Length.ShouldBeGreaterThan(0);
    }

    [SkippableFact]
    public async Task A_report_detail_page_shows_the_declared_columns()
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        string name = await SeedReportAsync("e2e-detail-" + Guid.NewGuid().ToString("N")[..6], "csv");

        await using var ui = await UiPage.OpenAsync(_fixture, "/reports");
        await ui.WaitForTextAsync(name);

        ILocator card = ui.Page.Locator(".report-card").Filter(new LocatorFilterOptions { HasText = name });
        await card.WaitForAsync(new LocatorWaitForOptions { Timeout = UiPage.Timeout });
        await card.ClickAsync();

        // Blazor routes client-side, so there is no navigation "Load" event to wait on — poll the
        // URL the router actually set instead.
        await ui.Page.WaitForFunctionAsync(
            "() => location.pathname.includes('/reports/')",
            null,
            new PageWaitForFunctionOptions { Timeout = UiPage.Timeout });
        await ui.WaitForTextAsync("Customer");
        await ui.AssertNoCircuitErrorAsync();
    }

    [SkippableFact]
    public async Task A_multi_format_report_delivers_every_format_it_declares()
    {
        Skip.If(_fixture.Unavailable is not null, _fixture.Unavailable);
        string name = await SeedReportAsync("e2e-multi-" + Guid.NewGuid().ToString("N")[..6], "csv", "xlsx");

        await using var ui = await UiPage.OpenAsync(_fixture, "/reports");
        await RunFromReportsPageAsync(ui, name);

        using var api = new ReportApi(_fixture.App);
        ReportApi.Job job = await api.WaitForReportCompletionAsync(name);

        (await api.ArtifactsAsync(job.Id)).Select(a => Path.GetExtension(a.FileName))
            .ShouldBe(new[] { ".csv", ".xlsx" }, ignoreOrder: true);
    }

}
