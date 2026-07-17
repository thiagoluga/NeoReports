using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Preview;
using NeoReports.Core.QueryBuilder;
using Shouldly;
using Xunit;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// ADR D49 (Epic K, K6): <c>POST /sources/{name}/query-preview</c>. Uses the same fakes as the
/// query-sql endpoint tests — a fake <see cref="IQuerySqlGenerator"/> plus a fake
/// <see cref="IConfigSourceProvider"/> that serves preset rows — so these verify the endpoint wiring:
/// that the SQL is generated server-side and run through the source's provider, the server-side row
/// cap and truncation flag, and the honest 404/422/400 states. The real keyset execution against a
/// live database is covered by the per-provider Testcontainers read tests.
/// </summary>
public class QueryPreviewEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void AddHost(IServiceCollection services, IReadOnlyList<object?[]> rows, bool withGenerator = true)
    {
        services.AddNeoReports();
        services.AddInMemorySourceRegistry();
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(rows, type: "fake"));
        if (withGenerator)
            services.AddSingleton<IQuerySqlGenerator>(new FakeQuerySqlGenerator());
    }

    private static Task<HttpResponseMessage> RegisterSourceAsync(HttpClient client, string name = "sales-db", string type = "fake") =>
        client.PostAsJsonAsync("/api/sources", new { name, type }, Json);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private const string Model = """{"tables":[{"schema":"public","table":"orders","alias":"o"}]}""";

    [Fact]
    public async Task Preview_returns_the_generated_query_columns_and_rows()
    {
        var rows = new object?[][] { new object?[] { 42L } };
        using var host = await TestApp.StartAsync(s => AddHost(s, rows));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.PostAsync("/api/sources/sales-db/query-preview", JsonBody(Model));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        JsonElement columns = body.GetProperty("columns");
        columns.GetArrayLength().ShouldBe(1);
        columns[0].GetProperty("name").GetString().ShouldBe("total");
        JsonElement bodyRows = body.GetProperty("rows");
        bodyRows.GetArrayLength().ShouldBe(1);
        bodyRows[0][0].GetInt64().ShouldBe(42);
        body.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_caps_the_row_count_and_reports_truncation()
    {
        object?[][] rows = Enumerable.Range(0, QueryPreviewRunner.MaxRows + 25)
            .Select(i => new object?[] { (long)i })
            .ToArray();
        using var host = await TestApp.StartAsync(s => AddHost(s, rows));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.PostAsync("/api/sources/sales-db/query-preview", JsonBody(Model));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("rows").GetArrayLength().ShouldBe(QueryPreviewRunner.MaxRows);
        body.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Preview_returns_404_for_an_unknown_source()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, Array.Empty<object?[]>()));
        var client = host.GetTestClient();

        var response = await client.PostAsync("/api/sources/nope/query-preview", JsonBody(Model));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Preview_returns_422_when_no_generator_is_registered()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, Array.Empty<object?[]>(), withGenerator: false));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.PostAsync("/api/sources/sales-db/query-preview", JsonBody(Model));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Preview_returns_400_when_the_body_is_empty()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, Array.Empty<object?[]>()));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.PostAsync("/api/sources/sales-db/query-preview", JsonBody("   "));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preview_returns_400_when_the_model_is_invalid()
    {
        using var host = await TestApp.StartAsync(s =>
        {
            s.AddNeoReports();
            s.AddInMemorySourceRegistry();
            s.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>(), type: "fake"));
            s.AddSingleton<IQuerySqlGenerator>(new FakeQuerySqlGenerator
            {
                Throw = new QuerySqlGenerationException("A query must select at least one column."),
            });
        });
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.PostAsync("/api/sources/sales-db/query-preview", JsonBody("""{"tables":[]}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("error").GetString().ShouldBe("A query must select at least one column.");
    }
}
