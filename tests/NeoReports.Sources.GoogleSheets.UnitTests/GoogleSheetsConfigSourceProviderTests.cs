using System.Net;
using System.Text;
using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.GoogleSheets.UnitTests;

/// <summary>Tests the dynamic (config-driven) Google Sheets source (ADR D66, <c>type: "googlesheets"</c>).</summary>
public sealed class GoogleSheetsConfigSourceProviderTests
{
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly object _service;
        public SingleServiceProvider(object service) => _service = service;
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(_service) ? _service : null;
    }

    private static ReportSchema Schema(params (string Name, ColumnType Type)[] columns) =>
        new(columns.Select(c => new ReportColumn(c.Name, c.Type)).ToArray());

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ReportExecutionContext Exec() =>
        new("job", "items", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    // For single-page assertions only — a stub that always returns the same non-empty page would
    // make CollectAsync loop forever against it (a real bug found via a runaway testhost.exe during
    // this PR's own development — see GoogleSheetsBatchSourceTests.ReadOnePageAsync for the sibling fix).
    private static Task<BatchResult<ReportRecord>> ReadOnePageAsync(IBatchSource<ReportRecord> source, int pageSize) =>
        source.ReadBatchAsync(new BatchContext(Exec(), pageSize, null, 1), CancellationToken.None);

    private static Dictionary<string, object?> BaseProperties() => new()
    {
        ["spreadsheetId"] = "sheet123",
        ["sheet"] = "Sheet1",
        ["apiKey"] = "key123",
        ["firstColumn"] = "A",
        ["lastColumn"] = "C",
    };

    [Fact]
    public void Create_requires_a_non_empty_spreadsheetId_property()
    {
        var provider = new GoogleSheetsConfigSourceProvider();
        var properties = BaseProperties();
        properties.Remove("spreadsheetId");
        var source = new SourceConfig("googlesheets", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema(("name", ColumnType.String)), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_a_non_empty_apiKey_property()
    {
        var provider = new GoogleSheetsConfigSourceProvider();
        var properties = BaseProperties();
        properties.Remove("apiKey");
        var source = new SourceConfig("googlesheets", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema(("name", ColumnType.String)), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public void Create_requires_firstColumn_and_lastColumn_properties()
    {
        var provider = new GoogleSheetsConfigSourceProvider();
        var properties = BaseProperties();
        properties.Remove("lastColumn");
        var source = new SourceConfig("googlesheets", properties);

        Should.Throw<ConfigurationException>(() => provider.Create(source, Schema(("name", ColumnType.String)), new SingleServiceProvider(new HttpClient())));
    }

    [Fact]
    public async Task Reads_records_matched_by_header_name()
    {
        HttpClient client = StubHttpMessageHandler.CreateClient(_ =>
            JsonResponse("""{"valueRanges":[{"values":[["Name","Amount"]]},{"values":[["Ada",100]]}]}"""), out _);

        var provider = new GoogleSheetsConfigSourceProvider();
        var source = new SourceConfig("googlesheets", BaseProperties());
        IBatchSource<ReportRecord> batchSource = provider.Create(source, Schema(("Name", ColumnType.String), ("Amount", ColumnType.Integer)), new SingleServiceProvider(client));

        BatchResult<ReportRecord> result = await ReadOnePageAsync(batchSource, pageSize: 10);
        IReadOnlyList<ReportRecord> records = result.Records;

        records.Count.ShouldBe(1);
        records[0]["Name"].ShouldBe("Ada");
        records[0]["Amount"].ShouldBe(100L);
    }
}
