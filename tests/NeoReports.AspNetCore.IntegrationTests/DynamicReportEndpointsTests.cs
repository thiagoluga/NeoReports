using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeoReports.Abstractions;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Configuration;
using NeoReports.Core.DependencyInjection;
using NeoReports.Formats.Csv;
using NeoReports.Jobs.DependencyInjection;
using Shouldly;
using Xunit;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// Epic D / D2: <c>POST /reports</c>, <c>POST /reports/validate</c>, <c>DELETE /reports/{name}</c>,
/// and <c>GET /capabilities</c> — the runtime report-registration endpoints (ADR D33).
/// </summary>
public class DynamicReportEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] IdCustomerColumns = { "Id", "Customer" };
    private static readonly string[] InMemorySourceOnly = { "inmemory" };
    private static readonly string[] CsvFormatOnly = { "csv" };

    private readonly string _configDir = Path.Join(Path.GetTempPath(), "nr-d2-" + Guid.NewGuid().ToString("N"));

    private static string ConfigNamed(string name, IReadOnlyDictionary<string, object?>? sourceProperties = null) => $$"""
    {
      "name": "{{name}}",
      "source": { "type": "inmemory"{{(sourceProperties is null ? "" : "," + PropertiesJson(sourceProperties))}} },
      "columns": [
        { "name": "Id", "type": "Integer" },
        { "name": "Customer", "type": "String" }
      ],
      "outputs": [ { "format": "csv" } ]
    }
    """;

    private static string PropertiesJson(IReadOnlyDictionary<string, object?> properties) =>
        "\"properties\": {" + string.Join(",", properties.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";

    private Task<IHost> StartAsync([System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        TestApp.StartAsync(services =>
        {
            services.AddDynamicReports(o => o.Directory = _configDir);
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
                new[] { new object?[] { 1L, "Acme" }, new object?[] { 2L, "Globex" } }));
            services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
        }, testName);

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content);
    }

    [Fact]
    public async Task Create_report_returns_201_and_report_is_runnable()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports", ConfigNamed("alpha"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldEndWith("/api/reports/alpha");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("name").GetString().ShouldBe("alpha");
        body.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ShouldBe(IdCustomerColumns);

        var reports = await client.GetFromJsonAsync<List<JsonElement>>("/api/reports", Json);
        reports!.ShouldContain(r => r.GetProperty("name").GetString() == "alpha");

        var run = await client.PostAsJsonAsync("/api/reports/alpha/run?mode=sync", new { }, Json);
        run.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await run.Content.ReadAsStringAsync()).ShouldContain("Globex");
    }

    [Fact]
    public async Task Create_report_with_invalid_json_returns_400()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports", "{ not valid json");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_report_with_unknown_source_type_returns_400()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        const string config = """
        {
          "name": "bad",
          "source": { "type": "does-not-exist" },
          "columns": [ { "name": "Id", "type": "Integer" } ],
          "outputs": [ { "format": "csv" } ]
        }
        """;

        var response = await PostJsonAsync(client, "/api/reports", config);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_report_with_duplicate_name_returns_409()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        await PostJsonAsync(client, "/api/reports", ConfigNamed("dup"));
        var second = await PostJsonAsync(client, "/api/reports", ConfigNamed("dup"));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a b")]
    [InlineData("1abc")]
    public async Task Create_report_with_invalid_name_returns_400_and_no_file_created(string invalidName)
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports", ConfigNamed(invalidName));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Directory.Exists(_configDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Create_report_with_missing_env_var_returns_400()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var config = ConfigNamed("needs-env", new Dictionary<string, object?> { ["key"] = "${NR_D2_MISSING_VAR}" });

        var response = await PostJsonAsync(client, "/api/reports", config);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_report_with_set_env_var_succeeds_and_stores_placeholder()
    {
        Environment.SetEnvironmentVariable("NR_D2_SET_VAR", "resolved");
        try
        {
            using var host = await StartAsync();
            var client = host.GetTestClient();

            var config = ConfigNamed("with-env", new Dictionary<string, object?> { ["key"] = "${NR_D2_SET_VAR}" });
            var response = await PostJsonAsync(client, "/api/reports", config);

            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            var stored = await File.ReadAllTextAsync(Path.Join(_configDir, "with-env.json"));
            stored.ShouldContain("${NR_D2_SET_VAR}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NR_D2_SET_VAR", null);
        }
    }

    [Fact]
    public async Task Validate_valid_config_returns_200_with_columns()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/validate", ConfigNamed("preview"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("valid").GetBoolean().ShouldBeTrue();
        body.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ShouldBe(IdCustomerColumns);
        body.GetProperty("nameTaken").GetBoolean().ShouldBeFalse();

        // Validation must never register or persist.
        var reports = await client.GetFromJsonAsync<List<JsonElement>>("/api/reports", Json);
        reports!.ShouldNotContain(r => r.GetProperty("name").GetString() == "preview");
        Directory.Exists(_configDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Validate_broken_config_returns_200_with_valid_false()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        const string config = """
        {
          "name": "broken",
          "source": { "type": "does-not-exist" },
          "columns": [ { "name": "Id", "type": "Integer" } ],
          "outputs": [ { "format": "csv" } ]
        }
        """;

        var response = await PostJsonAsync(client, "/api/reports/validate", config);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("valid").GetBoolean().ShouldBeFalse();
        body.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Validate_taken_name_reports_NameTaken_true()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        await PostJsonAsync(client, "/api/reports", ConfigNamed("existing"));
        var response = await PostJsonAsync(client, "/api/reports/validate", ConfigNamed("existing"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("valid").GetBoolean().ShouldBeTrue();
        body.GetProperty("nameTaken").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Validate_empty_body_returns_400()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await PostJsonAsync(client, "/api/reports/validate", string.Empty);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_dynamic_report_returns_204_and_removes_it()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        await PostJsonAsync(client, "/api/reports", ConfigNamed("removable"));

        var delete = await client.DeleteAsync("/api/reports/removable");

        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Exists(Path.Join(_configDir, "removable.json")).ShouldBeFalse();
        var reports = await client.GetFromJsonAsync<List<JsonElement>>("/api/reports", Json);
        reports!.ShouldNotContain(r => r.GetProperty("name").GetString() == "removable");

        // Re-creating the same name must succeed.
        var recreate = await PostJsonAsync(client, "/api/reports", ConfigNamed("removable"));
        recreate.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Delete_code_first_report_returns_409()
    {
        using var host = await TestApp.StartAsync(services =>
        {
            services.AddDynamicReports(o => o.Directory = _configDir);
            services.AddReport<Sale>("code-first", b => b
                .From(new InMemorySource(rows: 1, pageSize: 10))
                .Column(v => v.Id, "ID")
                .To(NeoReports.Formats.Csv.Format.Csv()));
        });
        var client = host.GetTestClient();

        var response = await client.DeleteAsync("/api/reports/code-first");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var reports = await client.GetFromJsonAsync<List<JsonElement>>("/api/reports", Json);
        reports!.ShouldContain(r => r.GetProperty("name").GetString() == "code-first");
    }

    [Fact]
    public async Task Delete_unknown_report_returns_404()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var response = await client.DeleteAsync("/api/reports/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Capabilities_reflects_registered_providers()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/capabilities", Json);

        body.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ShouldBe(InMemorySourceOnly);
        body.GetProperty("formats").EnumerateArray().Select(s => s.GetString()).ShouldBe(CsvFormatOnly);
        body.GetProperty("destinations").EnumerateArray().ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
            Directory.Delete(_configDir, recursive: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Minimal in-memory <see cref="IConfigSourceProvider"/> for the D2 endpoint tests.</summary>
public sealed class FakeConfigSourceProvider : IConfigSourceProvider
{
    private readonly IReadOnlyList<object?[]> _rows;

    public FakeConfigSourceProvider(IReadOnlyList<object?[]> rows, string type = "inmemory")
    {
        _rows = rows;
        Type = type;
    }

    public string Type { get; }

    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services) =>
        new SinglePageSource(_rows.Select(values => new ReportRecord(schema, values)).ToArray());

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
