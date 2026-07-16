using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;
using Shouldly;
using Xunit;

namespace NeoReports.AspNetCore.IntegrationTests;

/// <summary>
/// ADR D49 (Epic K, K3): <c>GET /sources/{name}/catalog</c> and <c>GET /sources/{name}/preview</c>.
/// Uses a fake in-memory <see cref="ISchemaExplorer"/> — the real ADO explorers are covered by
/// Testcontainers tests in the per-provider projects; here we verify the endpoint wiring, the
/// honest 404/422/400 states, and that the server caps the preview row count.
/// </summary>
public class SchemaExplorerEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void AddHost(IServiceCollection services, ISchemaExplorer? explorer = null)
    {
        services.AddNeoReports();
        services.AddInMemorySourceRegistry();
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>(), type: "fake"));
        if (explorer is not null)
            services.AddSingleton(explorer);
    }

    private static Task<HttpResponseMessage> RegisterSourceAsync(HttpClient client, string name = "sales-db", string type = "fake") =>
        client.PostAsJsonAsync("/api/sources", new { name, type }, Json);

    [Fact]
    public async Task Catalog_returns_the_sources_tables_columns_and_foreign_keys()
    {
        var explorer = new FakeSchemaExplorer("fake");
        using var host = await TestApp.StartAsync(s => AddHost(s, explorer));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.GetAsync("/api/sources/sales-db/catalog");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        JsonElement tables = body.GetProperty("tables");
        tables.GetArrayLength().ShouldBe(1);
        JsonElement customers = tables[0];
        customers.GetProperty("name").GetString().ShouldBe("customers");
        customers.GetProperty("columns")[0].GetProperty("name").GetString().ShouldBe("id");
        customers.GetProperty("columns")[0].GetProperty("isPrimaryKey").GetBoolean().ShouldBeTrue();
        JsonElement fk = customers.GetProperty("foreignKeys")[0];
        fk.GetProperty("column").GetString().ShouldBe("region_id");
        fk.GetProperty("referencedTable").GetString().ShouldBe("regions");
    }

    [Fact]
    public async Task Catalog_returns_404_for_an_unknown_source()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, new FakeSchemaExplorer("fake")));
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/sources/nope/catalog");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Catalog_returns_422_when_no_explorer_is_registered_for_the_source_type()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, explorer: null)); // no ISchemaExplorer
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.GetAsync("/api/sources/sales-db/catalog");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Preview_returns_rows_and_caps_the_row_count_server_side()
    {
        var explorer = new FakeSchemaExplorer("fake");
        using var host = await TestApp.StartAsync(s => AddHost(s, explorer));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.GetAsync("/api/sources/sales-db/preview?schema=public&table=customers");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("columns").GetArrayLength().ShouldBe(1);
        body.GetProperty("rows").GetArrayLength().ShouldBe(1);
        explorer.LastTop.ShouldBe(50); // the endpoint's cap, not an unbounded value
        explorer.LastTable.ShouldBe("customers");
        explorer.LastSchema.ShouldBe("public");
    }

    [Fact]
    public async Task Preview_returns_400_when_the_table_parameter_is_missing()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, new FakeSchemaExplorer("fake")));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.GetAsync("/api/sources/sales-db/preview?schema=public");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Catalog_returns_502_and_no_driver_detail_when_the_explorer_throws()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, new ThrowingSchemaExplorer()));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.GetAsync("/api/sources/sales-db/catalog");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(ThrowingSchemaExplorer.SecretFragment); // never echo connection-string fragments
    }

    [Fact]
    public async Task Preview_returns_502_and_no_driver_detail_when_the_explorer_throws()
    {
        using var host = await TestApp.StartAsync(s => AddHost(s, new ThrowingSchemaExplorer()));
        var client = host.GetTestClient();
        await RegisterSourceAsync(client);

        var response = await client.GetAsync("/api/sources/sales-db/preview?schema=public&table=customers");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(ThrowingSchemaExplorer.SecretFragment);
    }
}

/// <summary>Minimal in-memory <see cref="ISchemaExplorer"/> for the endpoint tests.</summary>
internal sealed class FakeSchemaExplorer : ISchemaExplorer
{
    public FakeSchemaExplorer(string type) => Type = type;

    private static readonly string[] PreviewColumns = { "id" };
    private static readonly object?[][] PreviewRows = { new object?[] { 1L } };

    public string Type { get; }
    public int LastTop { get; private set; }
    public string? LastSchema { get; private set; }
    public string? LastTable { get; private set; }

    public Task<SchemaCatalog> GetCatalogAsync(SourceDefinition definition, CancellationToken cancellationToken)
    {
        var customers = new CatalogTable(
            "public", "customers",
            new[]
            {
                new CatalogColumn("id", "integer", Nullable: false, IsPrimaryKey: true),
                new CatalogColumn("region_id", "integer", Nullable: true, IsPrimaryKey: false),
            },
            new[] { new ForeignKey("region_id", "public", "regions", "id") });
        return Task.FromResult(new SchemaCatalog(new[] { customers }));
    }

    public Task<TablePreview> PreviewTableAsync(
        SourceDefinition definition, string schema, string table, int top, CancellationToken cancellationToken)
    {
        LastSchema = schema;
        LastTable = table;
        LastTop = top;
        return Task.FromResult(new TablePreview(PreviewColumns, PreviewRows));
    }
}

/// <summary>
/// An <see cref="ISchemaExplorer"/> whose calls always throw a driver-style exception carrying a
/// connection-string fragment — used to prove the endpoint maps the failure to a 502 and never
/// echoes the exception message (which could leak secrets) back to the caller.
/// </summary>
internal sealed class ThrowingSchemaExplorer : ISchemaExplorer
{
    public const string SecretFragment = "Password=hunter2";

    public string Type => "fake";

    public Task<SchemaCatalog> GetCatalogAsync(SourceDefinition definition, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"login failed for Server=db;{SecretFragment}");

    public Task<TablePreview> PreviewTableAsync(
        SourceDefinition definition, string schema, string table, int top, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"login failed for Server=db;{SecretFragment}");
}
