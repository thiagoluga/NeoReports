using System.Net;
using Shouldly;
using Xunit;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// The app boots and serves, checked over plain HTTP before any browser is involved — so a hosting or
/// DI failure is reported as itself rather than as a mysterious browser timeout.
/// </summary>
[Collection(nameof(WebUiCollection))]
public class AppBootTests
{
    private readonly WebUiFixture _fixture;

    public AppBootTests(WebUiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_host_listens_and_the_root_redirects_into_the_ui()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.GetAsync(_fixture.App.BaseUrl + "/");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldBe(_fixture.App.UiPath);
    }

    [Fact]
    public async Task The_ui_shell_is_served()
    {
        using var client = new HttpClient();

        string html = await client.GetStringAsync(_fixture.App.Ui());

        // The Blazor Server shell: without this script no circuit is ever established and every
        // interactive test below would fail for a reason that has nothing to do with the UI.
        html.ShouldContain("blazor.server.js");
    }

    [Fact]
    public async Task The_ui_packages_own_static_assets_are_served()
    {
        using var client = new HttpClient();

        // The UI ships as a Razor Class Library, so its CSS lives under _content/. A regression that
        // stops those being served leaves every page rendering — unstyled — so nothing else here
        // would notice.
        // Served under the UI's mounted base path — _Host.cshtml links it relatively, so it resolves
        // beneath wherever UseNeoReportsUI put the app, not at the site root.
        using HttpResponseMessage response = await client.GetAsync(
            _fixture.App.Ui("/_content/NeoReports.UI/css/neoreports.css"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_engine_api_is_mounted_in_the_same_host()
    {
        using var client = new HttpClient();

        using HttpResponseMessage response = await client.GetAsync(_fixture.App.BaseUrl + "/api/reports");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
