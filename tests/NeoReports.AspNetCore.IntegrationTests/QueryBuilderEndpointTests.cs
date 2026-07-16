using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.QueryBuilder;
using Shouldly;
using Xunit;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// ADR D49 (Epic K, K5a): <c>POST /sources/{name}/query-sql</c>. Uses a fake in-memory
/// <see cref="IQuerySqlGenerator"/> — the real keyset SQL generation is covered by unit tests in the
/// Pro package. Here we verify the endpoint wiring, that the raw model JSON and the source's type
/// reach the generator, and the honest 404/422/400 states (D36 capability gating).
/// </summary>
public class QueryBuilderEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void AddHost(IServiceCollection services, IQuerySqlGenerator? generator = null)
    {
        services.AddNeoReports();
        services.AddInMemorySourceRegistry();
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>(), type: "fake"));
        if (generator is not null)
            services.AddSingleton(generator);
    }

    private static Task<HttpResponseMessage> RegisterSourceAsync(HttpClient client, string name = "sales-db", string type = "fake") =>
        client.PostAsJsonAsync("/api/sources", new { name, type }, Json);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Generate_returns_the_sql_parameters_and_schema()
    {
        var generator = new FakeQuerySqlGenerator();
        using var host = await TestApp.StartAsync(s => AddHost(s, generator));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        const string model = """{"tables":[{"schema":"public","table":"orders","alias":"o"}]}""";
        var response = await client.PostAsync("/api/sources/sales-db/query-sql", JsonBody(model));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("sql").GetString().ShouldBe("SELECT 1");
        body.GetProperty("parameters").GetProperty("qbfilter0").GetInt32().ShouldBe(42);
        JsonElement schema = body.GetProperty("schema");
        schema.GetArrayLength().ShouldBe(1);
        schema[0].GetProperty("name").GetString().ShouldBe("total");

        // The raw model JSON and the source's own type both reach the generator unmodified.
        generator.LastModelJson.ShouldBe(model);
        generator.LastSourceType.ShouldBe("fake");
    }

    [Fact]
    public async Task Generate_returns_404_for_an_unknown_source()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, new FakeQuerySqlGenerator()));
        var client = host.GetTestClient();

        var response = await client.PostAsync("/api/sources/nope/query-sql", JsonBody("{}"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Generate_returns_422_when_no_generator_is_registered()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, generator: null)); // MIT-only host
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.PostAsync("/api/sources/sales-db/query-sql", JsonBody("{}"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Generate_returns_400_when_the_body_is_empty()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, new FakeQuerySqlGenerator()));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.PostAsync("/api/sources/sales-db/query-sql", JsonBody("   "));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Generate_returns_400_when_the_model_is_invalid()
    {
        var generator = new FakeQuerySqlGenerator { Throw = new QuerySqlGenerationException("A query must select at least one column.") };
        using var host = await TestApp.StartAsync(s => AddHost(s, generator));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.PostAsync("/api/sources/sales-db/query-sql", JsonBody("""{"tables":[]}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("error").GetString().ShouldBe("A query must select at least one column.");
    }
}

/// <summary>Minimal in-memory <see cref="IQuerySqlGenerator"/> for the endpoint tests.</summary>
internal sealed class FakeQuerySqlGenerator : IQuerySqlGenerator
{
    public string? LastModelJson { get; private set; }
    public string? LastSourceType { get; private set; }
    public QuerySqlGenerationException? Throw { get; init; }

    public GeneratedReportSql Generate(string modelJson, string sourceType)
    {
        LastModelJson = modelJson;
        LastSourceType = sourceType;
        if (Throw is not null)
            throw Throw;

        var schema = new ReportSchema(new[] { new ReportColumn("total", ColumnType.Integer) });
        return new GeneratedReportSql(
            "SELECT 1",
            new Dictionary<string, object?> { ["qbfilter0"] = 42 },
            schema);
    }
}
