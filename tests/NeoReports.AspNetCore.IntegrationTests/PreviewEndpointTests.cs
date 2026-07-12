using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Preview;
using NeoReports.Formats.Csv;
using Shouldly;
using Xunit;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// G5 (ADR D45): <c>POST /reports/{name}/preview</c> — bounded, read-only sample; structured
/// filters for dynamic SQL-family reports; 400 on filters against a typed report; 404 unknown.
/// </summary>
public class PreviewEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] IdCustomerColumns = { "Id", "Customer" };
    private readonly string _configDir = Path.Join(Path.GetTempPath(), "nr-g5-" + Guid.NewGuid().ToString("N"));

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content);
    }

    [Fact]
    public async Task Unknown_report_returns_404()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/does-not-exist/preview", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unfiltered_preview_of_a_typed_report_returns_rows_and_schema()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/sales/preview", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("rows").GetArrayLength().ShouldBeGreaterThan(0);
        body.GetProperty("schema").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(IdCustomerColumns);
        body.GetProperty("filtersApplied").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task PageSize_is_capped_to_the_request()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/sales/preview", """{ "pageSize": 3 }""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("rows").GetArrayLength().ShouldBe(3);
        body.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Filters_on_a_typed_report_return_400()
    {
        using var host = await TestApp.StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/sales/preview",
            """{ "filters": [ { "column": "Id", "operator": "Equals", "value": 1 } ] }""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Filters_against_a_source_type_with_no_translator_are_ignored_honestly()
    {
        using var host = await StartWithDynamicReportAsync("fake-sql-no-translator", registerTranslator: false);
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/dyn/preview",
            """{ "filters": [ { "column": "Customer", "operator": "Equals", "value": "Acme" } ] }""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("filtersApplied").GetBoolean().ShouldBeFalse();
        body.GetProperty("rows").GetArrayLength().ShouldBe(2); // unfiltered canned data
    }

    [Fact]
    public async Task Filters_against_a_source_type_with_a_translator_are_applied()
    {
        using var host = await StartWithDynamicReportAsync("fake-sql", registerTranslator: true);
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/dyn/preview",
            """{ "filters": [ { "column": "Customer", "operator": "Equals", "value": "Acme" } ] }""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("filtersApplied").GetBoolean().ShouldBeTrue();
        body.GetProperty("rows").GetArrayLength().ShouldBe(1); // filtered canned data
    }

    [Fact]
    public async Task Filter_column_not_in_the_reports_schema_returns_400()
    {
        // A filter's Column is interpolated directly into SQL text by IFilterTranslator — this must
        // be rejected before it ever reaches a translator, not just accepted and passed through.
        using var host = await StartWithDynamicReportAsync("fake-sql", registerTranslator: true);
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/dyn/preview",
            """{ "filters": [ { "column": "Id; DROP TABLE Sales; --", "operator": "Equals", "value": 1 } ] }""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private Task<Microsoft.Extensions.Hosting.IHost> StartWithDynamicReportAsync(string sourceType, bool registerTranslator) =>
        TestApp.StartAsync(async services =>
        {
            services.AddDynamicReports(o => o.Directory = _configDir);
            services.AddSingleton<IConfigSourceProvider>(new FakeFilterableProvider(sourceType));
            services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
            if (registerTranslator)
                services.AddSingleton<IFilterTranslator>(new FakeFilterTranslator(sourceType));

            await using var provider = services.BuildServiceProvider();
            IReportConfigStore store = provider.GetRequiredService<IReportConfigStore>();
            string config = $$"""
            {
              "name": "dyn",
              "source": { "type": "{{sourceType}}", "properties": { "sql": "SELECT * FROM Sales", "key": "Id" } },
              "columns": [
                { "name": "Id", "type": "Integer" },
                { "name": "Customer", "type": "String" }
              ],
              "outputs": [ { "format": "csv" } ]
            }
            """;
            await store.SaveAsync("dyn", config, CancellationToken.None);
        });

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
            Directory.Delete(_configDir, recursive: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Minimal fake <see cref="IConfigSourceProvider"/> that returns different canned rows depending on
/// whether the given SQL text was filtered (contains "WHERE") — enough to prove filters flow through
/// the preview pipeline end to end without needing a real database.
/// </summary>
public sealed class FakeFilterableProvider : IConfigSourceProvider
{
    private static readonly object?[][] Unfiltered = { new object?[] { 1L, "Acme" }, new object?[] { 2L, "Globex" } };
    private static readonly object?[][] Filtered = { new object?[] { 1L, "Acme" } };

    public FakeFilterableProvider(string type) => Type = type;

    public string Type { get; }

    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        var sql = source.Properties!["sql"] as string ?? string.Empty;
        object?[][] rows = sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase) ? Filtered : Unfiltered;
        return new SinglePageSource(rows.Select(v => new ReportRecord(schema, v)).ToArray());
    }

    private sealed class SinglePageSource : IBatchSource<ReportRecord>
    {
        private readonly IReadOnlyList<ReportRecord> _rows;
        private bool _served;

        public SinglePageSource(IReadOnlyList<ReportRecord> rows) => _rows = rows;

        public ReportSchema Schema => throw new NotSupportedException("Not used by the compiler-driven path.");

        public Task<BatchResult<ReportRecord>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            if (_served)
                return Task.FromResult(BatchResult<ReportRecord>.Empty);

            _served = true;
            return Task.FromResult(new BatchResult<ReportRecord>(_rows, null, false));
        }
    }
}

/// <summary>Minimal fake <see cref="IFilterTranslator"/> pairing with <see cref="FakeFilterableProvider"/>.</summary>
public sealed class FakeFilterTranslator : IFilterTranslator
{
    public FakeFilterTranslator(string type) => Type = type;

    public string Type { get; }

    public bool TryTranslate(
        string sql, IReadOnlyList<PreviewFilter> filters, out string translatedSql, out IReadOnlyDictionary<string, object?> parameters)
    {
        if (filters.Count == 0)
        {
            translatedSql = sql;
            parameters = new Dictionary<string, object?>();
            return true;
        }

        translatedSql = $"SELECT * FROM ({sql}) t WHERE t.{filters[0].Column} = @filter0";
        parameters = new Dictionary<string, object?> { ["filter0"] = filters[0].Value };
        return true;
    }
}
