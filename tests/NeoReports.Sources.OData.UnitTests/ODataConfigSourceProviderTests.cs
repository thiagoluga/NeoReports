using System.Net;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.OData.UnitTests;

/// <summary>Tests the dynamic (config-driven) OData source (ADR D62, <c>type: "odata"</c>).</summary>
public sealed class ODataConfigSourceProviderTests
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
        var provider = new ODataConfigSourceProvider();
        var source = new SourceConfig("odata", new Dictionary<string, object?>());

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_rejects_a_whitespace_only_url()
    {
        var provider = new ODataConfigSourceProvider();
        var source = new SourceConfig("odata", new Dictionary<string, object?> { ["url"] = "   " });

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_rejects_an_unrecognized_pagination_strategy()
    {
        var provider = new ODataConfigSourceProvider();
        var properties = new Dictionary<string, object?> { ["url"] = "http://api.test/items", ["strategy"] = "pge" };
        var source = new SourceConfig("odata", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Id"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public async Task Reads_records_from_the_default_value_root_array()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(
            _ => JsonResponse("""{"value":[{"id":1,"name":"A"}]}"""), out _);

        var provider = new ODataConfigSourceProvider();
        var source = new SourceConfig("odata", new Dictionary<string, object?> { ["url"] = "http://api.test/items" });
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
            _ => JsonResponse("""{"value":[{"id":1,"attributes":{"displayName":"Widget"}}]}"""), out _);

        var provider = new ODataConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/items",
            ["fieldMap"] = Json("""{"name":"attributes.displayName"}"""),
        };
        var source = new SourceConfig("odata", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id", "name"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records[0]["name"].ShouldBe("Widget");
    }

    [Fact]
    public async Task Skip_strategy_is_read_from_properties_and_paginates()
    {
        var responses = new Dictionary<int, string>
        {
            [0] = """{"value":[{"id":1}]}""",
            [1] = """{"value":[]}""",
        };
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            int skip = HttpTestHelpers.ParseQueryInt(request.RequestUri!, "$skip");
            return JsonResponse(responses[skip]);
        }, out StubHttpMessageHandler handler);

        var provider = new ODataConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/items",
            ["strategy"] = "Skip",
        };
        var source = new SourceConfig("odata", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 1);

        records.Count.ShouldBe(1);
        handler.Requests.Count.ShouldBe(2); // skip=0 (one record, == top so keeps going), skip=1 (empty, ends pagination)
    }

    [Fact]
    public async Task Static_filter_select_orderby_properties_are_applied()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ => JsonResponse("""{"value":[]}"""), out StubHttpMessageHandler handler);

        var provider = new ODataConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["url"] = "http://api.test/items",
            ["filter"] = "Amount gt 100",
            ["select"] = "Id,Name",
            ["orderby"] = "Id desc",
        };
        var source = new SourceConfig("odata", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("id"), new SingleServiceProvider(client));

        await CollectAsync(batchSource, pageSize: 10);

        string query = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query);
        query.ShouldContain("$filter=Amount gt 100");
        query.ShouldContain("$select=Id,Name");
        query.ShouldContain("$orderby=Id desc");
    }
}
