using System.Net;
using System.Text;
using System.Text.Json;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.HubSpot.UnitTests;

/// <summary>Tests the dynamic (config-driven) HubSpot source (ADR D65, <c>type: "hubspot"</c>).</summary>
public sealed class HubSpotConfigSourceProviderTests
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
    public void Create_requires_a_non_empty_objectType_property()
    {
        var provider = new HubSpotConfigSourceProvider();
        var source = new SourceConfig("hubspot", new Dictionary<string, object?> { ["bearerToken"] = "token123" });

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("firstname"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public async Task Reads_records_from_the_properties_envelope_and_applies_fieldMap()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse("""{"results":[{"id":"1","properties":{"firstname":"Ada","email_address":"ada@example.com"}}]}"""), out _);

        var provider = new HubSpotConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["objectType"] = "contacts",
            ["bearerToken"] = "token123",
            ["fieldMap"] = Json("""{"email":"email_address"}"""),
        };
        var source = new SourceConfig("hubspot", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("firstname", "email"), new SingleServiceProvider(client));

        List<ReportRecord> records = await CollectAsync(batchSource, pageSize: 10);

        records.Count.ShouldBe(1);
        records[0]["firstname"].ShouldBe("Ada");
        records[0]["email"].ShouldBe("ada@example.com");
    }

    [Fact]
    public async Task Configured_properties_array_is_sent_as_a_comma_separated_query_param()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(request =>
        {
            Uri.UnescapeDataString(request.RequestUri!.Query).ShouldContain("properties=firstname,email_address");
            return JsonResponse("""{"results":[]}""");
        }, out _);

        var provider = new HubSpotConfigSourceProvider();
        var properties = new Dictionary<string, object?>
        {
            ["objectType"] = "contacts",
            ["bearerToken"] = "token123",
            ["properties"] = Json("""["firstname","email_address"]"""),
        };
        var source = new SourceConfig("hubspot", properties);
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("firstname"), new SingleServiceProvider(client));

        await CollectAsync(batchSource, pageSize: 10);
    }
}
