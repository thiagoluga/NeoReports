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
using NeoReports.Core.Scheduling;
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

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client, HttpMethod method, string url, string json, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        // Added raw rather than through EntityTagHeaderValue so a test can send a malformed or weak
        // validator on purpose, which the typed header would refuse to construct.
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

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
    public async Task A_store_failure_of_any_kind_rolls_the_registry_back()
    {
        var store = new ThrowOnSecondSaveStore(Path.Join(_configDir, "throwing"));
        using IHost host = await TestApp.StartAsync(services =>
        {
            // Registered before AddDynamicReports so its TryAddSingleton does not win.
            services.AddSingleton<IReportConfigStore>(store);
            services.AddDynamicReports(o => o.Directory = _configDir);
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
                new[] { new object?[] { 1L, "Acme" } }));
            services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
        });
        HttpClient client = await CreateSalesAsync(host);

        string edited = Original.Replace("\"pageSize\": 100", "\"pageSize\": 250", StringComparison.Ordinal);
        HttpResponseMessage response = await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", edited);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        // The registry must not keep a definition the store never accepted: it would serve the edit
        // until the next restart and then silently revert to the old one, which is about the hardest
        // symptom there is to trace back to an edit.
        JsonElement detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("pageSize").GetInt32().ShouldBe(100);
    }

    /// <summary>
    /// An <see cref="IReportConfigStore"/> is an interface — a custom one can fail with anything, not
    /// only the <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> a file-backed one
    /// raises. Throws on the replace, never on the create that sets the test up.
    /// </summary>
    private sealed class ThrowOnSecondSaveStore(string directory) : IReportConfigStore
    {
        private readonly FileReportConfigStore _inner = new(directory);
        private int _saves;

        public Task SaveAsync(string name, string configDocument, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _saves) > 1
                ? throw new InvalidOperationException("the store said no")
                : _inner.SaveAsync(name, configDocument, cancellationToken);

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken) => _inner.DeleteAsync(name, cancellationToken);

        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) => _inner.ExistsAsync(name, cancellationToken);

        public Task<IReadOnlyList<(string Name, string Document)>> ListAsync(CancellationToken cancellationToken) => _inner.ListAsync(cancellationToken);

        public Task<string?> TryGetAsync(string name, CancellationToken cancellationToken) => _inner.TryGetAsync(name, cancellationToken);
    }

    /// <summary>
    /// A replaced report's schedule has to reach the scheduler, in both directions. Nothing covered
    /// this: inverting the branch that chooses between registering and removing left the whole suite
    /// green, so an edit that added a cron could have quietly never run, and one that removed a cron
    /// could have gone on firing on the old schedule until the next restart.
    /// </summary>
    [Fact]
    public async Task Replacing_a_report_reconciles_its_schedule_in_both_directions()
    {
        var scheduler = new RecordingScheduler();
        using IHost host = await TestApp.StartAsync(services =>
        {
            services.AddSingleton<IRecurringReportScheduler>(scheduler);
            services.AddDynamicReports(o => o.Directory = _configDir);
            services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(
                new[] { new object?[] { 1L, "Acme" } }));
            services.AddSingleton<IWriterFactory>(new CsvWriterFactory(new CsvOptions()));
        });
        HttpClient client = await CreateSalesAsync(host);

        string scheduled = Original.Replace(
            "\"pageSize\": 100", "\"pageSize\": 100,\n  \"schedule\": { \"cron\": \"0 6 * * *\" }", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", scheduled))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        scheduler.Registered.ShouldContain(("sales", "0 6 * * *"));

        // And back: dropping the schedule has to unregister it, not just stop mentioning it.
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", Original))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        scheduler.Removed.ShouldContain("sales");
    }

    private sealed class RecordingScheduler : IRecurringReportScheduler
    {
        public List<(string Name, string Cron)> Registered { get; } = [];

        public List<string> Removed { get; } = [];

        public Task RegisterRecurringAsync(string reportName, string cron, CancellationToken cancellationToken)
        {
            Registered.Add((reportName, cron));
            return Task.CompletedTask;
        }

        public Task RemoveRecurringAsync(string reportName, CancellationToken cancellationToken)
        {
            Removed.Add(reportName);
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetNextOccurrenceAsync(string reportName, CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task<IReadOnlyList<string>> ListRegisteredNamesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    // ---- Optimistic concurrency (ADR D87) --------------------------------------------------------

    [Fact]
    public async Task Config_returns_an_etag_and_an_unchanged_report_still_matches_it()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        HttpResponseMessage config = await client.GetAsync("/api/reports/sales/config");
        string? etag = config.Headers.ETag?.ToString();

        etag.ShouldNotBeNullOrWhiteSpace();
        // Reading twice without saving must give the same validator, or every editor would be told
        // its document went stale the moment it reloaded.
        (await client.GetAsync("/api/reports/sales/config")).Headers.ETag?.ToString().ShouldBe(etag);
    }

    [Fact]
    public async Task A_put_carrying_the_current_etag_is_applied()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        HttpResponseMessage config = await client.GetAsync("/api/reports/sales/config");
        string etag = config.Headers.ETag!.ToString();

        string edited = Original.Replace("\"pageSize\": 100", "\"pageSize\": 250", StringComparison.Ordinal);
        HttpResponseMessage response = await SendJsonAsync(
            client, HttpMethod.Put, "/api/reports/sales", edited, etag);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("pageSize").GetInt32().ShouldBe(250);
    }

    /// <summary>
    /// The window D86 recorded and could not close: the address a placeholder carries names a slot of
    /// the document as it was at the GET, and Restore resolves it against the document as it is now.
    /// </summary>
    [Fact]
    public async Task A_put_carrying_a_stale_etag_is_refused_before_anything_is_restored()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        HttpResponseMessage config = await client.GetAsync("/api/reports/sales/config");
        string staleEtag = config.Headers.ETag!.ToString();

        // Another editor saves first.
        string theirs = Original.Replace("\"pageSize\": 100", "\"pageSize\": 500", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", theirs)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        string mine = Original.Replace("\"pageSize\": 100", "\"pageSize\": 250", StringComparison.Ordinal);
        HttpResponseMessage response = await SendJsonAsync(
            client, HttpMethod.Put, "/api/reports/sales", mine, staleEtag);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        // And the first editor's save is what survives — the refused one changed nothing.
        JsonElement detail = await client.GetFromJsonAsync<JsonElement>("/api/reports/sales", Json);
        detail.GetProperty("pageSize").GetInt32().ShouldBe(500);
    }

    [Fact]
    public async Task A_stale_save_is_told_to_reload_in_the_shape_the_client_reads()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string stale = (await client.GetAsync("/api/reports/sales/config")).Headers.ETag!.ToString();
        string theirs = Original.Replace("\"pageSize\": 100", "\"pageSize\": 500", StringComparison.Ordinal);
        await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", theirs);

        HttpResponseMessage response = await SendJsonAsync(
            client, HttpMethod.Put, "/api/reports/sales", Original, stale);

        // `error`, like every other rejection on this endpoint — a ProblemDetails body reads as null
        // to the client, which then says "the configuration was rejected" and never says "reload".
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync(), Json);
        string message = body.GetProperty("error").GetString()!;
        message.ShouldContain("Reload");
        message.ShouldContain("sales");
    }

    [Fact]
    public async Task A_successful_save_hands_back_a_validator_the_next_save_can_use()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string first = (await client.GetAsync("/api/reports/sales/config")).Headers.ETag!.ToString();

        string edited = Original.Replace("\"pageSize\": 100", "\"pageSize\": 250", StringComparison.Ordinal);
        HttpResponseMessage saved = await SendJsonAsync(
            client, HttpMethod.Put, "/api/reports/sales", edited, first);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);

        string next = saved.Headers.ETag!.ToString();
        next.ShouldNotBe(first);

        // Saving again from the same page must work: the editor's own save is not a conflict with
        // itself, and it is reachable by a retry or a double-click.
        string again = Original.Replace("\"pageSize\": 100", "\"pageSize\": 300", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", again, next)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The validator must carry nothing the caller was not already given. Hashing the STORED document
    /// made it an offline verification oracle for the redacted values: the two forms differ only in
    /// those values, so a caller could reconstruct candidates and confirm a guessed connection string
    /// without touching the database.
    /// </summary>
    [Fact]
    public async Task The_entity_tag_is_computable_from_the_body_the_caller_was_given()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        HttpResponseMessage config = await client.GetAsync("/api/reports/sales/config");
        string body = await config.Content.ReadAsStringAsync();
        string etag = config.Headers.ETag!.ToString();

        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body));
        string reproduced = '"' + Convert.ToBase64String(hash, 0, 16)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=') + '"';

        etag.ShouldBe(reproduced);
        // And the guard that gives it teeth: the plaintext is not in the body to begin with.
        body.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task Changing_only_a_secret_does_not_invalidate_an_open_editor()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string mine = (await client.GetAsync("/api/reports/sales/config")).Headers.ETag!.ToString();

        // Someone rotates the password and changes nothing else. No address moved, and a placeholder
        // means "whatever is stored now" — so this is not a conflict, and refusing it would be noise.
        string rotated = Original.Replace("hunter2", "rotated-secret", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", rotated)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", Original, mine)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_put_with_no_if_match_still_works_for_clients_from_before_D87()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        // Someone else saves, so any validator the caller might have had is stale — but it sends none,
        // which states no precondition. Requiring the header would break every existing client.
        string theirs = Original.Replace("\"pageSize\": 100", "\"pageSize\": 500", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", theirs)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        string mine = Original.Replace("\"pageSize\": 100", "\"pageSize\": 250", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", mine)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("*")]                                        // RFC 9110: "if the resource exists"
    [InlineData("\"nonsense\", *")]
    public async Task A_wildcard_if_match_is_accepted(string ifMatch)
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string edited = Original.Replace("\"pageSize\": 100", "\"pageSize\": 250", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", edited, ifMatch)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_weak_validator_never_satisfies_if_match()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        HttpResponseMessage config = await client.GetAsync("/api/reports/sales/config");
        string weak = $"W/{config.Headers.ETag}";

        string edited = Original.Replace("\"pageSize\": 100", "\"pageSize\": 250", StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", edited, weak)).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Removing_one_section_does_not_hand_another_its_credential()
    {
        using var host = await StartAsync();
        HttpClient client = host.GetTestClient();

        // Two sections of the same kind in one report is legal — two S3 buckets, two CSV outputs with
        // different writer options. This is the end-to-end shape of the bug two earlier pairing
        // designs had: touch the first section, and the second silently inherits the first's secret.
        string document = Original
            .Replace("\"sales\"", "\"twoOutputs\"", StringComparison.Ordinal)
            .Replace(
                "[ { \"format\": \"csv\" } ]",
                "[ { \"format\": \"csv\", \"properties\": { \"apiKey\": \"KEY-ALPHA\" } }," +
                "  { \"format\": \"csv\", \"properties\": { \"apiKey\": \"KEY-BETA\" } } ]",
                StringComparison.Ordinal);
        (await SendJsonAsync(client, HttpMethod.Post, "/api/reports", document)).StatusCode.ShouldBe(HttpStatusCode.Created);

        string redacted = await client.GetStringAsync("/api/reports/twoOutputs/config");
        redacted.ShouldContain("${neoreports:redacted:outputs[1]}");

        // Drop the first output. Counting occurrences would now make the survivor the *first* csv and
        // resolve its placeholder against KEY-ALPHA.
        string edited = redacted.Replace(
            "{\"format\":\"csv\",\"properties\":{\"apiKey\":\"${neoreports:redacted:outputs[0]}\"}},",
            string.Empty,
            StringComparison.Ordinal);
        edited.ShouldNotBe(redacted, "the output the test removes must have been found");

        (await SendJsonAsync(client, HttpMethod.Put, "/api/reports/twoOutputs", edited)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var store = host.Services.GetRequiredService<IReportConfigStore>();
        using JsonDocument stored = JsonDocument.Parse((await store.TryGetAsync("twoOutputs", CancellationToken.None))!);
        JsonElement[] outputs = stored.RootElement.GetProperty("outputs").EnumerateArray().ToArray();
        outputs.Length.ShouldBe(1);
        outputs[0].GetProperty("properties").GetProperty("apiKey").GetString().ShouldBe("KEY-BETA");
    }

    [Fact]
    public async Task Validate_for_rejects_a_document_that_is_not_the_report_it_targets()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        // "?for=" means "dry-run an edit of this report", so the document has to BE that report —
        // the same check PUT enforces. Without it an arbitrary document could be compiled with
        // another report's restored credentials, which is not what a dry run is for.
        string foreign = (await client.GetStringAsync("/api/reports/sales/config"))
            .Replace("\"name\":\"sales\"", "\"name\":\"somethingElse\"", StringComparison.Ordinal);

        JsonElement result = await (await SendJsonAsync(client, HttpMethod.Post, "/api/reports/validate?for=sales", foreign))
            .Content.ReadFromJsonAsync<JsonElement>(Json);

        result.GetProperty("valid").GetBoolean().ShouldBeFalse();
        result.GetProperty("error").GetString()!.ShouldContain("targets 'sales'");
    }

    [Fact]
    public async Task Validate_for_does_not_report_the_reports_own_name_as_taken()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string redacted = await client.GetStringAsync("/api/reports/sales/config");

        JsonElement result = await (await SendJsonAsync(client, HttpMethod.Post, "/api/reports/validate?for=sales", redacted))
            .Content.ReadFromJsonAsync<JsonElement>(Json);

        // Its own name is not taken by anyone else; reporting it as taken put "name already taken"
        // under every successful edit validation in the Builder.
        result.GetProperty("valid").GetBoolean().ShouldBeTrue();
        result.GetProperty("nameTaken").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Validate_without_for_still_reports_an_existing_name_as_taken()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        JsonElement result = await (await SendJsonAsync(client, HttpMethod.Post, "/api/reports/validate", Original))
            .Content.ReadFromJsonAsync<JsonElement>(Json);

        result.GetProperty("nameTaken").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_corrupt_stored_document_is_a_500_on_put_not_a_400_blamed_on_the_client()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        var store = host.Services.GetRequiredService<IReportConfigStore>();
        await store.SaveAsync("sales", "{ this is not json", CancellationToken.None);

        HttpResponseMessage response = await SendJsonAsync(client, HttpMethod.Put, "/api/reports/sales", Original);

        // GET .../config already answers this exact condition with a 500; PUT reported it as a bad
        // request, blaming the caller for a document on disk they never sent.
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Validate_for_a_report_with_no_stored_document_says_that_plainly()
    {
        using var host = await StartAsync();
        HttpClient client = await CreateSalesAsync(host);

        string redacted = await client.GetStringAsync("/api/reports/sales/config");

        JsonElement result = await (await SendJsonAsync(client, HttpMethod.Post, "/api/reports/validate?for=gone", redacted))
            .Content.ReadFromJsonAsync<JsonElement>(Json);

        // Skipping the restore silently produced "still holds the redaction placeholder" about a
        // document the caller sent correctly, for the single real problem that the report is gone.
        result.GetProperty("valid").GetBoolean().ShouldBeFalse();
        result.GetProperty("error").GetString()!.ShouldContain("no stored configuration for 'gone'");
        result.GetProperty("nameTaken").GetBoolean().ShouldBeFalse();
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
