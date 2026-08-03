using NeoReports.Sources.Http.Common;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

/// <summary>
/// The health-check path is documented as being probed <b>relative to the source's base URL</b>, so it
/// must land under the whole base path. Relative-<c>Uri</c> resolution does not do that — it replaces
/// the base's last segment, and drops the base path entirely for a leading <c>/</c> — the bug the
/// Elasticsearch (D64), HubSpot, Airtable and Salesforce packages each hit separately.
/// </summary>
public class HttpHealthProbeUrlTests
{
    [Theory]
    // Base with a path and no trailing slash: the last segment must survive.
    [InlineData("https://api.example.com/v1/orders", "ping", "https://api.example.com/v1/orders/ping")]
    // A leading slash on the configured path must not reset to the host root.
    [InlineData("https://api.example.com/v1/orders", "/ping", "https://api.example.com/v1/orders/ping")]
    // Trailing slash on the base is equivalent.
    [InlineData("https://api.example.com/v1/orders/", "ping", "https://api.example.com/v1/orders/ping")]
    // Base at the host root still works.
    [InlineData("https://api.example.com", "ping", "https://api.example.com/ping")]
    // Multi-segment probe paths.
    [InlineData("https://api.example.com/v1", "health/live", "https://api.example.com/v1/health/live")]
    public void Health_path_is_appended_under_the_whole_base_path(string baseUrl, string path, string expected) =>
        HttpHealthProbe.CombineUrl(baseUrl, path).ShouldBe(expected);

    [Fact]
    public void No_path_probes_the_base_url_itself() =>
        HttpHealthProbe.CombineUrl("https://api.example.com/v1/orders", null)
            .ShouldBe("https://api.example.com/v1/orders");

    [Fact]
    public void A_query_on_the_configured_path_stays_a_query() =>
        HttpHealthProbe.CombineUrl("https://api.example.com/v1", "health?deep=1")
            .ShouldBe("https://api.example.com/v1/health?deep=1");

    [Fact]
    public void An_absolute_probe_url_is_used_as_given() =>
        HttpHealthProbe.CombineUrl("https://api.example.com/v1", "https://status.example.com/health")
            .ShouldBe("https://status.example.com/health");
}
