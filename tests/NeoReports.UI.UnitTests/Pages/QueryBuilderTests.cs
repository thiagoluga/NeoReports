using Bunit;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

/// <summary>
/// bUnit coverage for the visual query builder (ADR D49/K5b). Exercises every honest state — engine
/// unreachable, no sources, no source picked, a source type with no schema explorer, the populated
/// explorer, adding a table, generating SQL, the "not available on this host" (MIT-only) path, and
/// a per-table preview — against the fake API client.
/// </summary>
public sealed class QueryBuilderTests : NeoReportsTestContext
{
    private static ApiSourceView Source(string name = "sales-db", string type = "postgres") =>
        new(name, type, "Demo", 0, null, null, null, null);

    private static ApiSchemaCatalog Catalog() => new(new[]
    {
        new ApiCatalogTable("public", "customers",
            new[]
            {
                new ApiCatalogColumn("id", "integer", Nullable: false, IsPrimaryKey: true),
                new ApiCatalogColumn("region_id", "integer", Nullable: true, IsPrimaryKey: false),
            },
            new[] { new ApiForeignKey("region_id", "public", "regions", "id") }),
        new ApiCatalogTable("public", "regions",
            new[] { new ApiCatalogColumn("id", "integer", Nullable: false, IsPrimaryKey: true) },
            Array.Empty<ApiForeignKey>()),
    });

    private void WithSources(IReadOnlyList<ApiSourceView>? sources) =>
        Api.Sources = _ => Task.FromResult(sources);

    private void WithCatalog(ApiSchemaCatalog? catalog) =>
        Api.SourceCatalog = (_, _) => Task.FromResult(catalog);

    [Fact]
    public void Engine_unreachable_shows_a_warning_banner()
    {
        WithSources(null);

        var cut = Render<QueryBuilder>();

        cut.Markup.ShouldContain("The engine API is unreachable.");
    }

    [Fact]
    public void No_sources_registered_shows_the_empty_state()
    {
        WithSources(Array.Empty<ApiSourceView>());

        var cut = Render<QueryBuilder>();

        cut.Find(".es-title").TextContent.ShouldBe("No sources registered");
    }

    [Fact]
    public void Sources_present_but_none_picked_prompts_to_pick_one()
    {
        WithSources(new[] { Source() });

        var cut = Render<QueryBuilder>();

        cut.Find(".es-title").TextContent.ShouldBe("Pick a source to begin");
    }

    [Fact]
    public void Source_type_without_a_schema_explorer_says_so()
    {
        WithSources(new[] { Source("mongo", "mongodb") });
        WithCatalog(null); // 422/404 → the client returns null

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "mongo"));

        cut.Markup.ShouldContain("No schema explorer for this source type.");
    }

    [Fact]
    public void Populated_catalog_renders_the_tables_and_columns()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));

        cut.Markup.ShouldContain("public.customers");
        cut.Markup.ShouldContain("public.regions");
        cut.Markup.ShouldContain("region_id");
        // Nothing added yet → the notebook prompts to add a table.
        cut.Find(".es-title").TextContent.ShouldBe("Add a table to include it in the query");
    }

    [Fact]
    public void Adding_a_table_makes_it_the_from_and_defaults_the_key_to_the_primary_key()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));

        cut.FindAll("button[title='Add to query']")[0].Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("From"));
        // The FROM key defaults to the table's PK (id).
        cut.Markup.ShouldContain("customers");
        var keySelect = cut.FindAll("select").First(s => s.GetAttribute("value") == "id");
        keySelect.ShouldNotBeNull();
    }

    [Fact]
    public async Task Generate_sends_the_model_to_the_source_and_renders_the_sql()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        Api.QuerySql = (_, _, _) => Task.FromResult(new ApiQuerySqlResult(
            ApiQuerySqlOutcome.Ok,
            new ApiGeneratedQuerySql(
                "SELECT t0.\"id\" FROM \"public\".\"customers\" t0 ORDER BY t0.\"id\"",
                new Dictionary<string, object?>(),
                new[] { new ApiReportColumn("id", "Integer", null, null, false) }),
            null));

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        cut.FindAll("button[title='Add to query']")[0].Click();
        // Select the id column so the query has an output column.
        cut.WaitForState(() => cut.FindAll("select").Any(s => s.QuerySelectorAll("option").Any(o => o.TextContent == "t0.id")));

        // Generate.
        var generate = cut.FindAll("button").First(b => b.TextContent.Contains("Generate SQL"));
        await cut.InvokeAsync(() => generate.Click());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("FROM \"public\".\"customers\" t0"));
        Api.LastQuerySql.ShouldNotBeNull();
        Api.LastQuerySql!.Value.Source.ShouldBe("sales-db");
        Api.LastQuerySql!.Value.ModelJson.ShouldContain("\"customers\"");
        Api.LastQuerySql!.Value.ModelJson.ShouldContain("\"from\"");
    }

    [Fact]
    public async Task Generate_on_an_mit_only_host_says_the_builder_is_unavailable()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        Api.QuerySql = (_, _, _) => Task.FromResult(new ApiQuerySqlResult(ApiQuerySqlOutcome.NotSupported, null, null));

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        cut.FindAll("button[title='Add to query']")[0].Click();

        var generate = cut.FindAll("button").First(b => b.TextContent.Contains("Generate SQL"));
        await cut.InvokeAsync(() => generate.Click());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("isn't available on this host"));
    }

    [Fact]
    public async Task Generate_surfaces_an_invalid_model_error()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        Api.QuerySql = (_, _, _) => Task.FromResult(new ApiQuerySqlResult(
            ApiQuerySqlOutcome.Invalid, null, "A query must select at least one column."));

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        cut.FindAll("button[title='Add to query']")[0].Click();

        var generate = cut.FindAll("button").First(b => b.TextContent.Contains("Generate SQL"));
        await cut.InvokeAsync(() => generate.Click());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("A query must select at least one column."));
    }

    [Fact]
    public void Previewing_a_table_requests_and_renders_the_sample()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        Api.SourceTablePreview = (_, _, _, _) => Task.FromResult<ApiTablePreview?>(
            new ApiTablePreview(new[] { "id" }, new[] { new object?[] { 1L }, new object?[] { 2L } }));

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        cut.FindAll("button[title='Preview 50 rows']")[0].Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Preview · public.customers"));
        Api.LastTablePreview.ShouldNotBeNull();
        Api.LastTablePreview!.Value.Table.ShouldBe("customers");
    }

    [Fact]
    public async Task Aliases_never_collide_after_a_middle_table_is_removed()
    {
        // Regression: aliases used to derive from the table count, so remove-then-add reused an alias.
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        string? sentModel = null;
        Api.QuerySql = (_, json, _) =>
        {
            sentModel = json;
            return Task.FromResult(new ApiQuerySqlResult(ApiQuerySqlOutcome.Invalid, null, "stop"));
        };

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        // Add customers (t0/FROM) then regions (t1/join).
        cut.FindAll("button[title='Add to query']")[0].Click();
        cut.WaitForAssertion(() => cut.FindAll("button[title='Add to query']").Count.ShouldBeGreaterThan(0));
        cut.FindAll("button[title='Add to query']")[1].Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Join"));
        // Remove the join (t1), then add regions again — must NOT reuse alias t1.
        cut.FindAll("button[title='Remove join']")[0].Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldNotContain("Join"));
        cut.FindAll("button[title='Add to query']")[1].Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Join"));

        var generate = cut.FindAll("button").First(b => b.TextContent.Contains("Generate SQL"));
        await cut.InvokeAsync(() => generate.Click());

        sentModel.ShouldNotBeNull();
        // The join's table alias is t2 (the third alias handed out), never a reused t1.
        sentModel!.ShouldContain("\"alias\":\"t2\"");
        sentModel!.ShouldNotContain("\"alias\":\"t1\"");
    }

    [Fact]
    public async Task Run_preview_sends_the_model_and_renders_the_result_grid()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        Api.QueryPreview = (_, _, _) => Task.FromResult(new ApiQueryPreviewResult(
            ApiQuerySqlOutcome.Ok,
            new ApiQueryPreview(
                new[] { new ApiReportColumn("id", "Integer", null, null, false) },
                new[] { new object?[] { 1L }, new object?[] { 2L } },
                Truncated: false),
            null));

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        cut.FindAll("button[title='Add to query']")[0].Click();

        var run = cut.FindAll("button").First(b => b.TextContent.Contains("Run preview"));
        await cut.InvokeAsync(() => run.Click());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Query result"));
        Api.LastQueryPreview.ShouldNotBeNull();
        Api.LastQueryPreview!.Value.Source.ShouldBe("sales-db");
        Api.LastQueryPreview!.Value.ModelJson.ShouldContain("\"customers\"");
    }

    [Fact]
    public async Task Run_preview_notes_truncation_when_the_page_fills()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        Api.QueryPreview = (_, _, _) => Task.FromResult(new ApiQueryPreviewResult(
            ApiQuerySqlOutcome.Ok,
            new ApiQueryPreview(
                new[] { new ApiReportColumn("id", "Integer", null, null, false) },
                new[] { new object?[] { 1L } },
                Truncated: true),
            null));

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        cut.FindAll("button[title='Add to query']")[0].Click();
        var run = cut.FindAll("button").First(b => b.TextContent.Contains("Run preview"));
        await cut.InvokeAsync(() => run.Click());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("more rows exist"));
    }

    [Fact]
    public async Task Run_preview_on_an_mit_only_host_says_the_builder_is_unavailable()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        Api.QueryPreview = (_, _, _) => Task.FromResult(new ApiQueryPreviewResult(ApiQuerySqlOutcome.NotSupported, null, null));

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        cut.FindAll("button[title='Add to query']")[0].Click();
        var run = cut.FindAll("button").First(b => b.TextContent.Contains("Run preview"));
        await cut.InvokeAsync(() => run.Click());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("isn't available on this host"));
    }

    [Fact]
    public async Task Run_preview_surfaces_an_invalid_model_error()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        Api.QueryPreview = (_, _, _) => Task.FromResult(new ApiQueryPreviewResult(
            ApiQuerySqlOutcome.Invalid, null, "A query must select at least one column."));

        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));
        cut.FindAll("button[title='Add to query']")[0].Click();
        var run = cut.FindAll("button").First(b => b.TextContent.Contains("Run preview"));
        await cut.InvokeAsync(() => run.Click());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("A query must select at least one column."));
    }

    [Fact]
    public void Raw_sql_tab_shows_the_honest_caveat_banner()
    {
        WithSources(new[] { Source() });
        WithCatalog(Catalog());
        var cut = Render<QueryBuilder>(p => p.Add(x => x.Source, "sales-db"));

        var rawTab = cut.FindAll("button").First(b => b.TextContent.Contains("Raw SQL"));
        rawTab.Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Raw SQL — you own the query."));
        cut.Markup.ShouldContain("won't be 100%");
    }
}
