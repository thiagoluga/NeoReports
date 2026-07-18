using System.Net;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Http.UnitTests;

/// <summary>Tests the dynamic (config-driven) HTTP source (ADR D61, <c>type: "http"</c>).</summary>
public sealed class HttpConfigSourceProviderTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

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
        var provider = new HttpConfigSourceProvider();
        var source = new SourceConfig("http", new Dictionary<string, object?>());

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_rejects_a_whitespace_only_url()
    {
        var provider = new HttpConfigSourceProvider();
        var source = new SourceConfig("http", new Dictionary<string, object?> { ["url"] = "   " });

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_rejects_an_unrecognized_pagination_strategy()
    {
        var provider = new HttpConfigSourceProvider();
        var properties = new Dictionary<string, object?> { ["url"] = "http://api.test/items", ["strategy"] = "pge" };
        var source = new SourceConfig("http", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public async Task Reads_records_by_matching_schema_column_names_to_json_fields()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => JsonResponse("""[{"id":1,"name":"A"}]"""), out _);

        var provider = new HttpConfigSourceProvider();
        var source = new SourceConfig("http", new Dictionary<string, object?> { ["url"] = "http://api.test/items" });
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id", "name"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records.Count.ShouldBe(1);
        records[0]["id"].ShouldBe(1L);
        records[0]["name"].ShouldBe("A");
    }

    [Fact]
    public async Task FieldMap_overrides_the_default_column_name_json_path()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => JsonResponse("""[{"id":1,"attributes":{"displayName":"Widget"}}]"""), out _);

        var provider = new HttpConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/items",
            ["fieldMap"] = Json("""{"name":"attributes.displayName"}"""),
        };
        var source = new SourceConfig("http", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id", "name"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records[0]["name"].ShouldBe("Widget");
    }

    [Fact]
    public async Task Page_strategy_is_read_from_properties_and_paginates()
    {
        var responses = new Dictionary<int, string>
        {
            [1] = """{"items":[{"id":1}]}""",
            [2] = """{"items":[{"id":2}]}""",
            [3] = """{"items":[]}""",
        };
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            int page = ParseQueryInt(request.RequestUri!, "page");
            return JsonResponse(responses[page]);
        }, out StubHttpMessageHandler handler);

        var provider = new HttpConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/items",
            ["strategy"] = "Page",
            ["recordsPath"] = "items",
        };
        var source = new SourceConfig("http", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 1);

        records.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(3); // page 1, page 2, page 3 (empty, ends pagination)
    }

    private static int ParseQueryInt(Uri uri, string key)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            if (Uri.UnescapeDataString(kv[0]) == key)
                return int.Parse(Uri.UnescapeDataString(kv[1]));
        }

        throw new KeyNotFoundException(key);
    }

    [Fact]
    public async Task Missing_column_in_the_response_materializes_as_null()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""[{"id":1}]"""), out _);

        var provider = new HttpConfigSourceProvider();
        var source = new SourceConfig("http", new Dictionary<string, object?> { ["url"] = "http://api.test/items" });
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id", "name"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records[0]["name"].ShouldBeNull();
    }
}
