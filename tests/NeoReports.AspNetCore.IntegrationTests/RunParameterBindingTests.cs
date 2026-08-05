using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using Shouldly;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// Run-time parameters posted to the run endpoint must reach the source as CLR values. The request
/// body types them as <c>object?</c>, and <c>System.Text.Json</c> materializes an untyped value as a
/// <see cref="JsonElement"/> — which no ADO provider can bind (`No mapping exists from object type
/// System.Text.Json.JsonElement to a known managed provider native type`), so every parameterized
/// report failed on the sync and in-memory-job paths while the Hangfire path happened to work (it
/// round-trips parameters through <c>JobParameters</c>, which converts them).
/// </summary>
public class RunParameterBindingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Records the parameters the pipeline handed the source on the first read.</summary>
    private sealed class CapturingSource : IBatchSource<Sale>
    {
        public IReadOnlyDictionary<string, object?>? Captured { get; private set; }

        public ReportSchema Schema { get; } = new(new[] { new ReportColumn("Id", ColumnType.Integer) });

        public Task<BatchResult<Sale>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            Captured ??= context.Execution.Parameters;
            return Task.FromResult(BatchResult<Sale>.Empty);
        }
    }

    [Fact]
    public async Task Run_time_parameters_reach_the_source_as_clr_values_not_json_elements()
    {
        var source = new CapturingSource();
        using var host = await TestApp.StartAsync(services =>
            services.AddReport<Sale>("params", b => b
                .From(source)
                .Column(v => v.Id, "ID")
                .To(Csv())));
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/api/reports/params/run?mode=sync",
            new { parameters = new Dictionary<string, object?> { ["tenant"] = "acme", ["count"] = 42 } },
            Json);

        response.IsSuccessStatusCode.ShouldBeTrue();
        source.Captured.ShouldNotBeNull();
        // The exact CLR shapes an ADO provider can bind — a JsonElement here would throw at bind time.
        source.Captured!["tenant"].ShouldBeOfType<string>().ShouldBe("acme");
        source.Captured!["count"].ShouldBeOfType<long>().ShouldBe(42L);
    }

    /// <summary>
    /// Structured parameter values are out of scope for v1, but nothing rejected them and what
    /// happened next depended on the backend: sync and in-memory handed the source a JsonElement (the
    /// very thing an ADO provider cannot bind) while Hangfire handed over raw JSON text. Either way
    /// the caller saw a driver error partway through a run, blamed on the source rather than on the
    /// request. Both modes are covered because the divergence between them is the point (ADR D72).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("?mode=sync")]
    public async Task An_array_parameter_is_refused_at_the_boundary(string query)
    {
        var source = new CapturingSource();
        using var host = await TestApp.StartAsync(services =>
            services.AddReport<Sale>("params", b => b
                .From(source)
                .Column(v => v.Id, "ID")
                .To(Csv())));
        var client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/reports/params/run{query}",
            new { parameters = new Dictionary<string, object?> { ["tenants"] = new[] { "acme", "globex" } } },
            Json);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("tenants");

        // Refused before anything ran — the point is that the caller learns about it now, not as a
        // driver error attributed to the source halfway through.
        source.Captured.ShouldBeNull();
    }

    [Fact]
    public async Task An_object_parameter_is_refused_at_the_boundary()
    {
        var source = new CapturingSource();
        using var host = await TestApp.StartAsync(services =>
            services.AddReport<Sale>("params", b => b
                .From(source)
                .Column(v => v.Id, "ID")
                .To(Csv())));
        var client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/reports/params/run",
            new { parameters = new Dictionary<string, object?> { ["range"] = new { from = "2026-01-01", to = "2026-02-01" } } },
            Json);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("range");
        source.Captured.ShouldBeNull();
    }

    [Fact]
    public async Task A_null_parameter_is_still_accepted()
    {
        // null is a scalar and binds fine (DBNull at the provider); the guard keys on array/object
        // only, so this pins that it did not over-reach into rejecting an optional parameter.
        var source = new CapturingSource();
        using var host = await TestApp.StartAsync(services =>
            services.AddReport<Sale>("params", b => b
                .From(source)
                .Column(v => v.Id, "ID")
                .To(Csv())));
        var client = host.GetTestClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/reports/params/run?mode=sync",
            new { parameters = new Dictionary<string, object?> { ["since"] = null } },
            Json);

        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        source.Captured.ShouldNotBeNull();
        source.Captured!["since"].ShouldBeNull();
    }
}
