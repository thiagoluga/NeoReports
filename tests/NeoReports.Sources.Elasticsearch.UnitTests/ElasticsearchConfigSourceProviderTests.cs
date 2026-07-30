using System.Net;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Elasticsearch.UnitTests;

/// <summary>Tests the dynamic (config-driven) Elasticsearch/OpenSearch source (ADR D64, <c>type: "elasticsearch"</c>).</summary>
public sealed class ElasticsearchConfigSourceProviderTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private const string SortDsl = """[{"_id":"asc"}]""";

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
            if (pageNumber > 1000)
                throw new Xunit.Sdk.XunitException("drain did not terminate within 1000 pages - likely a non-advancing cursor.");
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
        var provider = new ElasticsearchConfigSourceProvider();
        var properties = new Dictionary<string, object?> { ["index"] = "orders", ["sort"] = Json(SortDsl) };
        var source = new SourceConfig("elasticsearch", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_a_non_empty_index_property()
    {
        var provider = new ElasticsearchConfigSourceProvider();
        var properties = new Dictionary<string, object?> { ["url"] = "http://es.test", ["sort"] = Json(SortDsl) };
        var source = new SourceConfig("elasticsearch", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_a_non_empty_sort_property()
    {
        var provider = new ElasticsearchConfigSourceProvider();
        var properties = new Dictionary<string, object?> { ["url"] = "http://es.test", ["index"] = "orders" };
        var source = new SourceConfig("elasticsearch", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public async Task Reads_records_from_hits_and_applies_fieldMap()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, _) =>
            JsonResponse("""{"hits":{"hits":[{"_source":{"id":1,"attributes":{"displayName":"Widget"}},"sort":[1]}]}}"""), out _);

        var provider = new ElasticsearchConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://es.test",
            ["index"] = "orders",
            ["sort"] = Json(SortDsl),
            ["fieldMap"] = Json("""{"name":"attributes.displayName"}"""),
        };
        var source = new SourceConfig("elasticsearch", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id", "name"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records.Count.ShouldBe(1);
        records[0]["id"].ShouldBe(1L);
        records[0]["name"].ShouldBe("Widget");
    }

    [Fact]
    public async Task Configured_static_query_is_sent()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient((_, body) =>
        {
            body.ShouldNotBeNull();
            body.ShouldContain("\"status\":\"open\"");
            return JsonResponse("""{"hits":{"hits":[]}}""");
        }, out _);

        var provider = new ElasticsearchConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://es.test",
            ["index"] = "orders",
            ["sort"] = Json(SortDsl),
            ["query"] = Json("""{"term":{"status":"open"}}"""),
        };
        var source = new SourceConfig("elasticsearch", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id"), new SingleServiceProvider(client));

        await CollectAsync(batchSource, pageSize: 10);
    }
}
