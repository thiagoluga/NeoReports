using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Configuration;
using NeoReports.Core.DependencyInjection;
using NeoReports.Formats.Csv;
using Shouldly;
using Xunit;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// Epic D / D4: <c>GET /reports/{name}</c> — the full safe report definition, and the enriched
/// <c>GET /reports</c> summary (<c>Formats</c>/<c>Destinations</c>).
/// </summary>
public class ReportDetailEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] CsvFormatOnly = { "csv" };

    [Fact]
    public async Task Detail_of_code_first_report_has_origin_code_and_is_not_deletable()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);

        body.GetProperty("name").GetString().ShouldBe("sales");
        body.GetProperty("origin").GetString().ShouldBe("code");
        body.GetProperty("deletable").GetBoolean().ShouldBeFalse();
        body.GetProperty("pageSize").GetInt32().ShouldBe(1000);
        body.GetProperty("formats").EnumerateArray().Select(f => f.GetString()).ShouldBe(CsvFormatOnly);
        body.GetProperty("destinations").EnumerateArray().ShouldBeEmpty();

        var columns = body.GetProperty("columns").EnumerateArray().ToArray();
        columns.Length.ShouldBe(2);
        columns[0].GetProperty("name").GetString().ShouldBe("Id");
        columns[0].GetProperty("type").GetString().ShouldBe("Integer");

        body.GetProperty("failureStrategy").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("retryMaxAttempts").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Detail_of_dynamic_report_has_origin_config_and_is_deletable()
    {
        var configDir = Path.Join(Path.GetTempPath(), "nr-d4-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = await TestApp.StartAsync(services =>
            {
                services.AddDynamicReports(o => o.Directory = configDir);
                services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
                    new[] { new object?[] { 1L } }));
                services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
            });
            var client = host.GetTestClient();

            const string config = """
            {
              "name": "dynamic-one",
              "source": { "type": "inmemory" },
              "columns": [ { "name": "Id", "type": "Integer" } ],
              "outputs": [ { "format": "csv" } ]
            }
            """;
            using var content = new StringContent(config, Encoding.UTF8, "application/json");
            await client.PostAsync("/api/reports", content);

            var body = await client.GetFromJsonAsync<JsonElement>("/api/reports/dynamic-one", Json);

            body.GetProperty("origin").GetString().ShouldBe("config");
            body.GetProperty("deletable").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(configDir))
                Directory.Delete(configDir, recursive: true);
        }
    }

    [Fact]
    public async Task Detail_reflects_dynamic_report_resilience_overrides()
    {
        var configDir = Path.Join(Path.GetTempPath(), "nr-d4-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = await TestApp.StartAsync(services =>
            {
                services.AddDynamicReports(o => o.Directory = configDir);
                services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
                    new[] { new object?[] { 1L } }));
                services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
            });
            var client = host.GetTestClient();

            const string config = """
            {
              "name": "resilient-one",
              "source": { "type": "inmemory" },
              "columns": [ { "name": "Id", "type": "Integer" } ],
              "outputs": [ { "format": "csv" } ],
              "resilience": { "maxAttempts": 4, "backoff": "Exponential", "baseDelaySeconds": 2, "jitter": true, "onFailure": "skip-and-log" }
            }
            """;
            using var content = new StringContent(config, Encoding.UTF8, "application/json");
            await client.PostAsync("/api/reports", content);

            var body = await client.GetFromJsonAsync<JsonElement>("/api/reports/resilient-one", Json);

            body.GetProperty("failureStrategy").GetString().ShouldBe("skip-and-log");
            body.GetProperty("retryMaxAttempts").GetInt32().ShouldBe(4);
            body.GetProperty("retryBackoff").GetString().ShouldBe("Exponential");
            body.GetProperty("retryBaseDelaySeconds").GetDouble().ShouldBe(2);
            body.GetProperty("retryUseJitter").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(configDir))
                Directory.Delete(configDir, recursive: true);
        }
    }

    [Fact]
    public async Task Detail_reflects_abort_thresholds_when_configured()
    {
        var configDir = Path.Join(Path.GetTempPath(), "nr-d37-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = await TestApp.StartAsync(services =>
            {
                services.AddDynamicReports(o => o.Directory = configDir);
                services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
                    new[] { new object?[] { 1L } }));
                services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
            });
            var client = host.GetTestClient();

            const string config = """
            {
              "name": "abort-threshold-one",
              "source": { "type": "inmemory" },
              "columns": [ { "name": "Id", "type": "Integer" } ],
              "outputs": [ { "format": "csv" } ],
              "resilience": { "onFailure": "skip-and-log", "abortWhen": { "consecutiveFailures": 3, "failureRate": 0.5 } }
            }
            """;
            using var content = new StringContent(config, Encoding.UTF8, "application/json");
            await client.PostAsync("/api/reports", content);

            var body = await client.GetFromJsonAsync<JsonElement>("/api/reports/abort-threshold-one", Json);

            body.GetProperty("abortAfterConsecutiveFailures").GetInt32().ShouldBe(3);
            body.GetProperty("abortAtFailureRate").GetDouble().ShouldBe(0.5);
            body.TryGetProperty("abortAfterTotalFailures", out var total).ShouldBeTrue();
            total.ValueKind.ShouldBe(JsonValueKind.Null);
        }
        finally
        {
            if (Directory.Exists(configDir))
                Directory.Delete(configDir, recursive: true);
        }
    }

    [Fact]
    public async Task Detail_omits_abort_thresholds_when_not_configured()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);

        body.TryGetProperty("abortAfterConsecutiveFailures", out var consecutive).ShouldBeTrue();
        consecutive.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Detail_of_unknown_report_returns_404()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/reports/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_response_never_contains_a_properties_key()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var raw = await client.GetStringAsync("/api/reports/sales");

        raw.ToLowerInvariant().ShouldNotContain("\"properties\"");
    }

    [Fact]
    public async Task List_summary_includes_formats_and_destinations()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var reports = await client.GetFromJsonAsync<List<JsonElement>>("/api/reports", Json);

        var sales = reports!.Single(r => r.GetProperty("name").GetString() == "sales");
        sales.GetProperty("formats").EnumerateArray().Select(f => f.GetString()).ShouldBe(CsvFormatOnly);
        sales.GetProperty("destinations").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Origin_is_code_when_no_dynamic_reports_support_is_configured()
    {
        // The default TestApp host never calls AddDynamicReports() — IReportConfigStore is
        // absent. The endpoint must not throw; every report is origin "code".
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);

        body.GetProperty("origin").GetString().ShouldBe("code");
    }
}
