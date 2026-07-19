using System.Net;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.GraphQl.UnitTests;

/// <summary>Tests the dynamic (config-driven) GraphQL source (ADR D63, <c>type: "graphql"</c>).</summary>
public sealed class GraphQlConfigSourceProviderTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private const string Doc =
        "query($first: Int, $after: String) { viewer { repositories(first: $first, after: $after) " +
        "{ edges { node { id name } } pageInfo { hasNextPage endCursor } } } }";

    private static ReportSchema Schema(params string[] columnNames) =>
        new(columnNames.Select(n => new ReportColumn(n, ColumnType.String)).ToArray());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ReportExecutionContext Exec() =>
        new("job", "items", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static async Task<List<ReportRecord>> CollectAsync(IBatchSource<ReportRecord> source, int pageSize)
    {
        var results = new List<ReportRecord>();
        string? cursor = null;
        var pageNumber = 1;
        while (true)
        {
            var context = new BatchContext(Exec(), pageSize, cursor, pageNumber);
            BatchResult<ReportRecord> result = await source.ReadBatchAsync(context, CancellationToken.None);
            results.AddRange(result.Records);
            if (!result.HasMore)
                break;
            cursor = result.NextCursor;
            pageNumber++;
        }

        return results;
    }

    [Fact]
    public void Create_requires_a_non_empty_url_property()
    {
        var provider = new GraphQlConfigSourceProvider();
        var properties = new Dictionary<string, object?> { ["query"] = Doc, ["connectionPath"] = "viewer.repositories" };
        var source = new SourceConfig("graphql", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_a_non_empty_query_property()
    {
        var provider = new GraphQlConfigSourceProvider();
        var properties = new Dictionary<string, object?> { ["url"] = "http://api.test/graphql", ["connectionPath"] = "viewer.repositories" };
        var source = new SourceConfig("graphql", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_a_non_empty_connectionPath_property()
    {
        var provider = new GraphQlConfigSourceProvider();
        var properties = new Dictionary<string, object?> { ["url"] = "http://api.test/graphql", ["query"] = Doc };
        var source = new SourceConfig("graphql", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public async Task Reads_records_from_the_configured_connection_and_applies_fieldMap()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[{"node":{"id":1,"attributes":{"displayName":"Widget"}}}],"pageInfo":{"hasNextPage":false}}}}}"""), out _);

        var provider = new GraphQlConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/graphql",
            ["query"] = Doc,
            ["connectionPath"] = "viewer.repositories",
            ["fieldMap"] = Json("""{"name":"attributes.displayName"}"""),
        };
        var source = new SourceConfig("graphql", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id", "name"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records.Count.ShouldBe(1);
        records[0]["id"].ShouldBe(1L);
        records[0]["name"].ShouldBe("Widget");
    }

    [Fact]
    public async Task Static_variables_from_json_config_round_trip_with_their_real_types()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[],"pageInfo":{"hasNextPage":false}}}}}"""), out StubHttpMessageHandler handler);

        var provider = new GraphQlConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/graphql",
            ["query"] = Doc,
            ["connectionPath"] = "viewer.repositories",
            ["variables"] = Json("""{"owner":"octocat","minStars":100,"includeArchived":false}"""),
        };
        var source = new SourceConfig("graphql", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id"), new SingleServiceProvider(client));

        await CollectAsync(batchSource, pageSize: 10);

        JsonElement variables = JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("variables");
        variables.GetProperty("owner").ValueKind.ShouldBe(JsonValueKind.String);
        variables.GetProperty("owner").GetString().ShouldBe("octocat");
        variables.GetProperty("minStars").ValueKind.ShouldBe(JsonValueKind.Number);
        variables.GetProperty("minStars").GetInt32().ShouldBe(100);
        variables.GetProperty("includeArchived").ValueKind.ShouldBe(JsonValueKind.False);
    }

    [Fact]
    public async Task A_date_like_string_variable_survives_verbatim_without_datetime_reformatting()
    {
        // Must NOT reuse PrimitiveObjectConverter's opportunistic DateTime-guessing for reads here:
        // a variable configured as a plain date-only string must reach the server exactly as
        // written, not reformatted into a full ISO-8601 timestamp (many GraphQL servers validate a
        // custom Date scalar strictly).
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[],"pageInfo":{"hasNextPage":false}}}}}"""), out StubHttpMessageHandler handler);

        var provider = new GraphQlConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/graphql",
            ["query"] = Doc,
            ["connectionPath"] = "viewer.repositories",
            ["variables"] = Json("""{"since":"2024-01-01"}"""),
        };
        var source = new SourceConfig("graphql", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id"), new SingleServiceProvider(client));

        await CollectAsync(batchSource, pageSize: 10);

        JsonElement variables = JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("variables");
        variables.GetProperty("since").GetString().ShouldBe("2024-01-01");
    }

    [Fact]
    public async Task Custom_firstVariable_and_afterVariable_properties_are_honored()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[],"pageInfo":{"hasNextPage":false}}}}}"""), out StubHttpMessageHandler handler);

        var provider = new GraphQlConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/graphql",
            ["query"] = Doc,
            ["connectionPath"] = "viewer.repositories",
            ["firstVariable"] = "limit",
            ["afterVariable"] = "cursor",
        };
        var source = new SourceConfig("graphql", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id"), new SingleServiceProvider(client));

        await CollectAsync(batchSource, pageSize: 7);

        JsonElement variables = JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("variables");
        variables.GetProperty("limit").GetInt32().ShouldBe(7);
        variables.TryGetProperty("first", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Custom_nodePath_property_is_honored()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"data":{"viewer":{"repositories":{"edges":[{"item":{"id":1}}],"pageInfo":{"hasNextPage":false}}}}}"""), out _);

        var provider = new GraphQlConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/graphql",
            ["query"] = Doc,
            ["connectionPath"] = "viewer.repositories",
            ["nodePath"] = "item",
        };
        var source = new SourceConfig("graphql", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records.Count.ShouldBe(1);
        records[0]["id"].ShouldBe(1L);
    }
}
