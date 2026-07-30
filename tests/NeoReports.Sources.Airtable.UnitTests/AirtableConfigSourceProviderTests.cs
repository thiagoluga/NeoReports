using System.Net;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Airtable.UnitTests;

/// <summary>Tests the dynamic (config-driven) Airtable source (ADR D65, <c>type: "airtable"</c>).</summary>
public sealed class AirtableConfigSourceProviderTests
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
    public void Create_requires_a_non_empty_baseId_property()
    {
        var provider = new AirtableConfigSourceProvider();
        var source = new SourceConfig("airtable", new Dictionary<string, object?> { ["table"] = "Projects", ["bearerToken"] = "token123" });

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("name"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_a_non_empty_table_property()
    {
        var provider = new AirtableConfigSourceProvider();
        var source = new SourceConfig("airtable", new Dictionary<string, object?> { ["baseId"] = "appXXX", ["bearerToken"] = "token123" });

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("name"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public async Task Reads_records_from_the_fields_envelope_and_applies_fieldMap()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse("""{"records":[{"id":"rec1","fields":{"Project Name":"Alpha"}}]}"""), out _);

        var provider = new AirtableConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["baseId"] = "appXXX",
            ["table"] = "Projects",
            ["bearerToken"] = "token123",
            ["fieldMap"] = Json("""{"name":"Project Name"}"""),
        };
        var source = new SourceConfig("airtable", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("name"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records.Count.ShouldBe(1);
        records[0]["name"].ShouldBe("Alpha");
    }
}
