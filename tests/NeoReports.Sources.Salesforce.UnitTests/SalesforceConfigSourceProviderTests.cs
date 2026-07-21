using System.Net;
using System.Text;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Salesforce.UnitTests;

/// <summary>Tests the dynamic (config-driven) Salesforce source (ADR D67, <c>type: "salesforce"</c>).</summary>
public sealed class SalesforceConfigSourceProviderTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static ReportSchema Schema(params string[] columnNames) =>
        new(columnNames.Select(n => new ReportColumn(n, ColumnType.String)).ToArray());

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ReportExecutionContext Exec() =>
        new("job", "items", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static Task<BatchResult<ReportRecord>> ReadOnePageAsync(IBatchSource<ReportRecord> source, int pageSize) =>
        source.ReadBatchAsync(new BatchContext(Exec(), pageSize, null, 1), CancellationToken.None);

    private static Dictionary<string, object?> BaseProperties() => new()
    {
        ["instanceUrl"] = "https://myorg.my.salesforce.com",
        ["soql"] = "SELECT Id, Name FROM Account",
        ["bearerToken"] = "token123",
    };

    [Fact]
    public void Create_requires_a_non_empty_instanceUrl_property()
    {
        var provider = new SalesforceConfigSourceProvider();
        var properties = BaseProperties();
        properties.Remove("instanceUrl");
        var source = new SourceConfig("salesforce", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Name"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_a_non_empty_soql_property()
    {
        var provider = new SalesforceConfigSourceProvider();
        var properties = BaseProperties();
        properties.Remove("soql");
        var source = new SourceConfig("salesforce", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Name"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_a_non_empty_bearerToken_property()
    {
        var provider = new SalesforceConfigSourceProvider();
        var properties = BaseProperties();
        properties.Remove("bearerToken");
        var source = new SourceConfig("salesforce", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema("Name"), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public async Task Reads_records_matched_by_field_name_directly_no_envelope()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse("""{"totalSize":1,"done":true,"records":[{"attributes":{"type":"Account"},"Id":"001","Name":"Acme"}]}"""), out _);

        var provider = new SalesforceConfigSourceProvider();
        var source = new SourceConfig("salesforce", BaseProperties());
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema("Id", "Name"), new SingleServiceProvider(client));

        BatchResult<ReportRecord> result = await ReadOnePageAsync(batchSource, pageSize: 10);

        result.Records.Single()["Id"].ShouldBe("001");
        result.Records.Single()["Name"].ShouldBe("Acme");
    }
}
