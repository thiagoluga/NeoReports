using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>ADR D42: <c>/sources</c> CRUD and <c>POST /sources/{name}/health</c>.</summary>
public class SourceEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void AddSourceRegistryHost(IServiceCollection services)
    {
        services.AddNeoReports();
        services.AddInMemorySourceRegistry();
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>(), type: "fake"));
        services.AddSingleton<ISourceHealthCheck, FakeSourceHealthCheck>();
    }

    /// <summary>
    /// A source's <c>properties</c> bag is typed <c>object?</c>, so System.Text.Json hands each value
    /// over as a <c>JsonElement</c>. <c>FileSourceRegistryStore</c> launders that away by serializing,
    /// but <c>InMemorySourceRegistryStore</c> keeps what it is given — so a source created over HTTP
    /// used to fail at read time with "requires a non-empty 'connectionString' property", the value
    /// being a JsonElement rather than a string. The stored bag is asserted directly because the
    /// source view deliberately never echoes properties back (they hold secrets).
    /// </summary>
    [Theory]
    [InlineData("post")]
    [InlineData("put")]
    public async Task Source_properties_are_stored_as_CLR_values_not_JsonElements(string verb)
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        var body = new
        {
            name = "sales-db",
            type = "fake",
            properties = new Dictionary<string, object?>
            {
                ["connectionString"] = "Server=localhost;Database=sales",
                ["commandTimeout"] = 30,
                ["pooling"] = true,
            },
        };

        if (verb == "put")
        {
            // PUT updates in place and 404s on an unknown name, so the source has to exist first.
            (await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json))
                .IsSuccessStatusCode.ShouldBeTrue();
        }

        HttpResponseMessage response = verb == "post"
            ? await client.PostAsJsonAsync("/api/sources", body, Json)
            : await client.PutAsJsonAsync("/api/sources/sales-db", body, Json);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());

        var registry = host.Services.GetRequiredService<ISourceRegistry>();
        SourceDefinition stored = (await registry.GetAsync("sales-db", CancellationToken.None)).ShouldNotBeNull();
        IReadOnlyDictionary<string, object?> properties = stored.Properties.ShouldNotBeNull();

        properties["connectionString"].ShouldBeOfType<string>().ShouldBe("Server=localhost;Database=sales");
        properties["commandTimeout"].ShouldBeOfType<long>().ShouldBe(30L);
        properties["pooling"].ShouldBeOfType<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task List_returns_an_empty_array_when_no_registry_is_configured()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/sources");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sources = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        sources.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task List_returns_registered_sources_with_referenced_by_count()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json);

        var response = await client.GetAsync("/api/sources");
        var sources = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        sources.GetArrayLength().ShouldBe(1);
        var view = sources[0];
        view.GetProperty("name").GetString().ShouldBe("sales-db");
        view.GetProperty("type").GetString().ShouldBe("fake");
        view.GetProperty("referencedByCount").GetInt32().ShouldBe(0);
        view.GetProperty("lastHealthStatus").ValueKind.ShouldBe(JsonValueKind.Null);
        view.TryGetProperty("properties", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Get_returns_404_for_an_unknown_source()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/sources/does-not-exist");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_returns_a_registered_source_without_its_properties()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/sources",
            new { name = "sales-db", type = "fake", properties = new Dictionary<string, object?> { ["secret"] = "shh" } },
            Json);

        var response = await client.GetAsync("/api/sources/sales-db");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        view.GetProperty("name").GetString().ShouldBe("sales-db");
        view.TryGetProperty("properties", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Create_returns_201_with_a_location_header()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldContain("sales-db");
    }

    [Fact]
    public async Task Create_returns_409_when_no_registry_is_configured()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_returns_400_for_an_invalid_name()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/sources", new { name = "1-bad-name", type = "fake" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_returns_400_for_an_unknown_provider_type()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "no-such-type" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_returns_409_for_a_duplicate_name()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json);
        var response = await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Replace_updates_an_existing_source()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake", description = "old" }, Json);

        var response = await client.PutAsJsonAsync("/api/sources/sales-db", new { name = "sales-db", type = "fake", description = "new" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/sources/sales-db", Json);
        detail.GetProperty("description").GetString().ShouldBe("new");
    }

    [Fact]
    public async Task Replace_returns_404_for_an_unknown_source()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        var response = await client.PutAsJsonAsync("/api/sources/does-not-exist", new { name = "does-not-exist", type = "fake" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replace_returns_400_when_the_body_name_does_not_match_the_url()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json);

        var response = await client.PutAsJsonAsync("/api/sources/sales-db", new { name = "other-name", type = "fake" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_returns_404_for_an_unknown_source()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        var response = await client.DeleteAsync("/api/sources/does-not-exist");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_is_blocked_while_referenced_then_allowed_once_the_report_is_removed()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            AddSourceRegistryHost(services);
            services.AddSingleton<IWriterFactory>(new NeoReports.Formats.Csv.CsvWriterFactory(new NeoReports.Formats.Csv.CsvOptions()));
            services.AddDynamicReports(o => o.Directory = Path.Join(Path.GetTempPath(), "nr-f3-" + Guid.NewGuid().ToString("N")));
        });
        var client = host.GetTestClient();

        await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json);

        const string document = """
        {
          "name": "dyn-ref",
          "source": { "ref": "sales-db" },
          "columns": [ { "name": "Id", "type": "Integer" } ],
          "outputs": [ { "format": "csv" } ]
        }
        """;
        var created = await client.PostAsync("/api/reports", JsonContent.Create(JsonSerializer.Deserialize<JsonElement>(document)));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var blocked = await client.DeleteAsync("/api/sources/sales-db");
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var reportDeleted = await client.DeleteAsync("/api/reports/dyn-ref");
        reportDeleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var allowed = await client.DeleteAsync("/api/sources/sales-db");
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Health_returns_404_for_an_unknown_source()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        var response = await client.PostAsync("/api/sources/does-not-exist/health", content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_returns_422_when_no_health_check_is_registered_for_the_type()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddNeoReports();
            services.AddInMemorySourceRegistry();
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>(), type: "no-health"));
        });
        var client = host.GetTestClient();

        await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "no-health" }, Json);

        var response = await client.PostAsync("/api/sources/sales-db/health", content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Health_returns_200_and_the_get_endpoint_reflects_the_cached_result()
    {
        using var host = await TestApp.StartAsync(AddSourceRegistryHost);
        var client = host.GetTestClient();

        await client.PostAsJsonAsync("/api/sources", new { name = "sales-db", type = "fake" }, Json);

        var health = await client.PostAsync("/api/sources/sales-db/health", content: null);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await health.Content.ReadFromJsonAsync<JsonElement>(Json);
        result.GetProperty("healthy").GetBoolean().ShouldBeTrue();

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/sources/sales-db", Json);
        detail.GetProperty("lastHealthStatus").GetString().ShouldBe("healthy");
        detail.GetProperty("lastCheckedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Health_scrubs_the_raw_error_and_never_returns_connection_detail()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddNeoReports();
            services.AddInMemorySourceRegistry();
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>(), type: "leaky"));
            services.AddSingleton<ISourceHealthCheck, FakeUnhealthySourceHealthCheck>();
        });
        var client = host.GetTestClient();

        await client.PostAsJsonAsync("/api/sources", new { name = "db", type = "leaky" }, Json);

        var health = await client.PostAsync("/api/sources/db/health", content: null);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await health.Content.ReadFromJsonAsync<JsonElement>(Json);

        result.GetProperty("healthy").GetBoolean().ShouldBeFalse();
        var error = result.GetProperty("error").GetString();
        error.ShouldNotBeNull();
        error!.ShouldNotContain(FakeUnhealthySourceHealthCheck.SecretHost);
        error.ShouldNotContain(FakeUnhealthySourceHealthCheck.SecretUser);
        error.ShouldContain("server logs");

        // The cached reading GET /sources surfaces must be scrubbed too — identical generic string,
        // with neither the host nor the user leaking.
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/sources/db", Json);
        detail.GetProperty("lastHealthStatus").GetString().ShouldBe("unhealthy");
        var cachedError = detail.GetProperty("lastHealthError").GetString();
        cachedError.ShouldBe(error);
        cachedError!.ShouldNotContain(FakeUnhealthySourceHealthCheck.SecretHost);
        cachedError!.ShouldNotContain(FakeUnhealthySourceHealthCheck.SecretUser);
    }
}

/// <summary>Unhealthy fake whose raw error embeds connection detail, to prove the endpoint scrubs it.</summary>
public sealed class FakeUnhealthySourceHealthCheck : ISourceHealthCheck
{
    public const string SecretHost = "db.internal.corp:1433";
    public const string SecretUser = "sa";

    public string Type => "leaky";

    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        Task.FromResult(new SourceHealthResult(
            Healthy: false,
            Error: $"Login failed for user '{SecretUser}'. Server={SecretHost};Database=payroll",
            Latency: TimeSpan.FromMilliseconds(3)));
}

/// <summary>Always-healthy fake health check for the <c>"fake"</c> source type.</summary>
public sealed class FakeSourceHealthCheck : ISourceHealthCheck
{
    public string Type => "fake";

    public Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken) =>
        Task.FromResult(new SourceHealthResult(Healthy: true, Error: null, Latency: TimeSpan.FromMilliseconds(1)));
}
