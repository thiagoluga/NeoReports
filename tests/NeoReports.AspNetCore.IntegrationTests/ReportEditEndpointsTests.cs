using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeoReports.Abstractions;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Configuration;
using NeoReports.Core.DependencyInjection;
using NeoReports.Formats.Csv;
using Shouldly;
using Xunit;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// ADR D86: <c>GET /reports/{name}/config</c> and <c>PUT /reports/{name}</c> — reading a report's
/// stored configuration back for editing, and replacing it in one step.
/// </summary>
public class ReportEditEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _configDir = Path.Join(Path.GetTempPath(), "nr-d86-" + Guid.NewGuid().ToString("N"));

    private const string Original = """
    {
      "name": "sales",
      "source": {
        "type": "inmemory",
        "properties": { "connectionString": "Server=db;Password=hunter2", "sql": "SELECT Id FROM Sales" }
      },
      "columns": [ { "name": "Id", "type": "Integer" }, { "name": "Customer", "type": "String" } ],
      "outputs": [ { "format": "csv" } ],
      "pageSize": 100
    }
    """;

    private Task<IHost> StartAsync([System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        TestApp.StartAsync(services =>
        {
            services.AddDynamicReports(o => o.Directory = _configDir);
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
                new[] { new object?[] { 1L, "Acme" }, new object?[] { 2L, "Globex" } }));
            services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));

            // A code-registered report, to prove the edit endpoints refuse one: its definition lives
            // in the host's source, which is where it has to be changed.
            services.AddReport<Sale>("static", b => b
                .From(new InMemorySource(rows: 3, pageSize: 3))
                .Column(v => v.Id, "ID")
                .Column(v => v.Customer, "Customer")
                .To(Csv()));
        }, testName: testName);

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string url, string json)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return await client.SendAsync(request);
    }

    private static async Task<HttpClient> CreateSalesAsync(IHost host)
    {
        HttpClient client = host.GetTestClient();
        HttpResponseMessage created = await SendJsonAsync(client, HttpMethod.Post, "/api/reports", Original);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return client;
    }

    [Fact]
    public async Task Config_returns_the_stored_document_with_credentials_redacted()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        JsonElement body = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales/config", Json);

        JsonElement properties = body.GetProperty("source").GetProperty("properties");
        properties.GetProperty("connectionString").GetString().ShouldBe(ReportConfigSecrets.RedactedValue);
        // Everything the editor actually needs comes back — this is the whole reason the endpoint
        // exists, since GET /reports/{name} exposes none of it (D33(c)).
        properties.GetProperty("sql").GetString().ShouldBe("SELECT Id FROM Sales");
        body.GetProperty("source").GetProperty("type").GetString().ShouldBe("inmemory");
        body.GetProperty("pageSize").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task Config_for_a_code_registered_report_is_404_not_an_empty_document()
    {
        using var host = await StartAsync();
        HttpClient client = host.GetTestClient();

        // "static" is registered in code by TestApp; it has no stored document, and there is nothing
        // an editor could usefully do with it.
        HttpResponseMessage response = await client.GetAsync("/api/reports/static/config");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Config_for_an_unknown_report_is_404()
    {
        using var host = await StartAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/api/reports/nope/config");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_restores_the_redacted_connection_string_from_the_stored_document()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string edited = (await client.GetStringAsync("/api/reports/sales/config"))
            .Replace("\"pageSize\":100", "\"pageSize\":250", StringComparison.Ordinal);
        HttpResponseMessage replaced = await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", edited);
        replaced.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Changing a page size must not cost the user a connection string they were never shown.
        var store = host.Services.GetRequiredService<IReportConfigStore>();
        string stored = (await store.TryGetAsync("sales", CancellationToken.None))!;
        using JsonDocument doc = JsonDocument.Parse(stored);
        doc.RootElement.GetProperty("source").GetProperty("properties").GetProperty("connectionString")
            .GetString().ShouldBe("Server=db;Password=hunter2");
        doc.RootElement.GetProperty("pageSize").GetInt32().ShouldBe(250);
    }

    [Fact]
    public async Task Put_applies_the_edit_to_the_running_report()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string edited = Original.Replace("\"Customer\", \"type\": \"String\"", "\"Renamed\", \"type\": \"String\"", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", edited)).StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .ShouldBe(["Id", "Renamed"]);
    }

    [Fact]
    public async Task A_rejected_edit_leaves_the_existing_report_exactly_as_it_was()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        // The failure the old delete-then-create flow could not survive: the replacement is invalid,
        // and the user must still have their report afterwards.
        string broken = Original.Replace("\"type\": \"inmemory\"", "\"type\": \"no-such-provider\"", StringComparison.Ordinal);
        HttpResponseMessage response = await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", broken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("pageSize").GetInt32().ShouldBe(100);
        HttpResponseMessage run = await client.PostAsJsonAsync("/api/reports/sales/run?mode=sync", new { }, Json);
        run.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Put_rejects_a_document_whose_name_does_not_match_the_route()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        HttpResponseMessage response = await SendJsonAsync(
            client, HttpMethod.Put, "/api/reports/sales", Original.Replace("\"sales\"", "\"renamed\"", StringComparison.Ordinal));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("cannot be renamed in place");
    }

    [Fact]
    public async Task Put_rejects_a_placeholder_that_has_nothing_to_restore_from()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string invented = Original.Replace(
            "\"sql\": \"SELECT Id FROM Sales\"",
            $"\"sql\": \"SELECT Id FROM Sales\", \"apiKey\": \"{ReportConfigSecrets.RedactedValue}\"",
            StringComparison.Ordinal);
        HttpResponseMessage response = await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", invented);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("apiKey");
    }

    [Fact]
    public async Task Put_on_a_code_registered_report_is_409()
    {
        using var host = await StartAsync();

        HttpResponseMessage response = await SendJsonAsync(
            host.GetTestClient(), HttpMethod.Put, "/api/reports/static", Original.Replace("\"sales\"", "\"static\"", StringComparison.Ordinal));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Put_on_an_unknown_report_is_404()
    {
        using var host = await StartAsync();

        HttpResponseMessage response = await SendJsonAsync(
            host.GetTestClient(), HttpMethod.Put, "/api/reports/nope", Original.Replace("\"sales\"", "\"nope\"", StringComparison.Ordinal));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_rejects_a_redaction_placeholder_outright()
    {
        using var host = await StartAsync();
        HttpClient client = host.GetTestClient();

        // There is no stored document to resolve it against, so accepting it would persist the
        // literal sentinel as if it were a connection string.
        string document = Original
            .Replace("\"sales\"", "\"fresh\"", StringComparison.Ordinal)
            .Replace("Server=db;Password=hunter2", ReportConfigSecrets.RedactedValue, StringComparison.Ordinal);
        HttpResponseMessage response = await SendJsonAsync(client, HttpMethod.Post, "/api/reports", document);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("redaction placeholder");
    }

    [Fact]
    public async Task Validate_for_an_existing_report_resolves_the_placeholder_before_compiling()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string redacted = await client.GetStringAsync("/api/reports/sales/config");

        // Without ?for=, the placeholder reaches the compiler as a literal and the dry run fails for
        // a reason that has nothing to do with the configuration under test.
        JsonElement blind = await (await SendJsonAsync(client, HttpMethod.Post, "/api/reports/validate", redacted))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        blind.GetProperty("valid").GetBoolean().ShouldBeFalse();

        JsonElement scoped = await (await SendJsonAsync(client, HttpMethod.Post, "/api/reports/validate?for=sales", redacted))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        scoped.GetProperty("valid").GetBoolean().ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
            Directory.Delete(_configDir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
