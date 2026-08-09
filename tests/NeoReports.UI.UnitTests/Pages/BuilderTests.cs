using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class BuilderTests : NeoReportsTestContext
{
    private void SetupEngineAvailable(IReadOnlyList<string> sources, IReadOnlyList<ApiSourceView>? registered = null)
    {
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities(sources, ["csv"], ["local"]));
        Api.Sources = _ => Task.FromResult<IReadOnlyList<ApiSourceView>?>(registered ?? Array.Empty<ApiSourceView>());
    }

    /// <summary>Arms the wizard to open on <paramref name="name"/> with the given stored document (ADR D86).</summary>
    private void SetupEditing(string name, string configDocument)
    {
        Api.ReportConfig = (_, _) => Task.FromResult<string?>(configDocument);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("edit", name));
    }

    [Fact]
    public void No_engine_capabilities_shows_demo_mode_banner_and_no_source_pickers()
    {
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], [], []));

        var cut = Render<Builder>();

        cut.Markup.ShouldContain("Demo mode");
        cut.FindAll(".sel-card").ShouldBeEmpty();
        Wizard.EngineAvailable.ShouldBeFalse();
    }

    [Fact]
    public void Engine_available_defaults_SourceType_to_sql_when_registered_and_not_editing()
    {
        SetupEngineAvailable(["postgres", "sql", "mongo"]);

        Render<Builder>();

        Wizard.SourceType.ShouldBe("sql");
    }

    [Fact]
    public void Clicking_an_inline_source_type_card_selects_it()
    {
        SetupEngineAvailable(["postgres", "mongo"]);

        var cut = Render<Builder>();
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("mongo")).Click();

        Wizard.SourceType.ShouldBe("mongo");
    }

    [Fact]
    public void Registered_sources_are_offered_and_selecting_one_sets_SourceRef_and_SourceType()
    {
        SetupEngineAvailable(["postgres"], [new ApiSourceView("postgres-demo", "postgres", "Demo DB", 2, "healthy", null, null, null)]);

        var cut = Render<Builder>();
        cut.Markup.ShouldContain("Use a registered source");
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("postgres-demo")).Click();

        Wizard.SourceRef.ShouldBe("postgres-demo");
        Wizard.SourceType.ShouldBe("postgres");
    }

    [Fact]
    public void Selecting_a_registered_source_hides_the_inline_type_picker()
    {
        SetupEngineAvailable(["postgres"], [new ApiSourceView("postgres-demo", "postgres", null, 0, null, null, null, null)]);

        var cut = Render<Builder>();
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("postgres-demo")).Click();

        Wizard.SourceRef.ShouldBe("postgres-demo");
        cut.WaitForState(() => !cut.Markup.Contains("Engine source type"), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Enter_connection_manually_clears_SourceRef()
    {
        SetupEngineAvailable(["postgres"], [new ApiSourceView("postgres-demo", "postgres", null, 0, null, null, null, null)]);
        Wizard.SourceRef = "postgres-demo";

        var cut = Render<Builder>();
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("Enter connection manually")).Click();

        Wizard.SourceRef.ShouldBe("");
    }

    [Fact]
    public void Navigating_to_builder_with_no_query_param_resets_the_wizard()
    {
        // Simulates any external "start fresh" entry point (New report, the Topbar's persistent
        // Builder link, etc.) — resetting is the default so a future entry point that forgets any
        // special convention still gets safe (blank-wizard) behavior instead of silently reusing a
        // previous report's fields.
        SetupEngineAvailable(["sql"]);
        Render<Builder>();
        Wizard.SqlQuery = "SELECT Id FROM Sales";
        Wizard.ReportName = "leftover-report";

        Render<Builder>();

        Wizard.SqlQuery.ShouldBe("");
        Wizard.ReportName.ShouldBe("");
    }

    [Fact]
    public void Navigating_to_builder_with_resume_true_preserves_wizard_state()
    {
        // Simulates clicking "Back"/"Change"/an "edit" link from a later wizard step — the real
        // Blazor router constructs a fresh Builder instance on every route change, and only those
        // 3 internal links pass ?resume=true.
        SetupEngineAvailable(["sql"]);
        Render<Builder>();
        Wizard.SqlQuery = "SELECT Id FROM Sales";
        Wizard.ReportName = "in-progress-report";

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("resume", true));
        Render<Builder>();

        Wizard.SqlQuery.ShouldBe("SELECT Id FROM Sales");
        Wizard.ReportName.ShouldBe("in-progress-report");
    }

    [Fact]
    public void Edit_mode_takes_priority_over_a_stray_resume_true_query_param()
    {
        SetupEngineAvailable(["sql"]);
        SetupEditing("clientsVip", """{"name":"clientsVip","source":{"type":"sql"}}""");
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("resume", true));

        Render<Builder>();

        Wizard.IsEditing.ShouldBeTrue();
        Wizard.ReportName.ShouldBe("clientsVip");
    }

    [Fact]
    public void Switching_the_inline_source_type_clears_stale_generic_property_rows()
    {
        SetupEngineAvailable(["http", "elasticsearch"]);
        Wizard.SourceType = "http";
        Wizard.SourceProperties = [new() { Key = "url", Value = "https://leftover.example.com" }];

        var cut = Render<Builder>();
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("elasticsearch")).Click();

        Wizard.SourceType.ShouldBe("elasticsearch");
        Wizard.SourceProperties.ShouldBeEmpty();
    }

    [Fact]
    public void Re_selecting_the_same_inline_source_type_keeps_its_property_rows()
    {
        SetupEngineAvailable(["http"]);
        var cut = Render<Builder>();
        Wizard.SourceType = "http";
        Wizard.SourceProperties = [new() { Key = "url", Value = "https://keep.example.com" }];

        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("http")).Click();

        Wizard.SourceProperties.ShouldHaveSingleItem();
    }

    [Fact]
    public void Selecting_a_registered_source_of_a_different_type_clears_stale_property_rows()
    {
        SetupEngineAvailable(["http"], [new ApiSourceView("sf-demo", "salesforce", null, 0, null, null, null, null)]);
        Wizard.SourceType = "http";
        Wizard.SourceProperties = [new() { Key = "url", Value = "https://leftover.example.com" }];

        var cut = Render<Builder>();
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("sf-demo")).Click();

        Wizard.SourceType.ShouldBe("salesforce");
        Wizard.SourceProperties.ShouldBeEmpty();
    }

    // A stored document with neither a type nor a ref (only reachable by hand-editing one) must not
    // fall through to the "sql" default the create path uses — that would silently repoint the
    // report at a source nobody chose.
    [Fact]
    public void Editing_with_no_source_type_confirmed_yet_disables_Continue()
    {
        SetupEngineAvailable(["sql"]);
        SetupEditing("clientsVip", """{"name":"clientsVip","source":{"properties":{}}}""");

        var cut = Render<Builder>();
        var continueButton = cut.FindAll("button").First(b => b.TextContent.Contains("Continue"));
        continueButton.HasAttribute("disabled").ShouldBeTrue();

        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("sql")).Click();
        continueButton = cut.FindAll("button").First(b => b.TextContent.Contains("Continue"));
        continueButton.HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void Demo_mode_leaves_Continue_enabled_despite_a_blank_source_type()
    {
        SetupEngineAvailable([]);

        var cut = Render<Builder>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Continue")).HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void Continue_navigates_to_configure_step_without_any_validation()
    {
        SetupEngineAvailable([]);

        var cut = Render<Builder>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Continue")).Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("builder/configure");
    }

    // The bug this whole flow existed to have: opening "Edit" used to land the user on a blank form
    // for everything the report actually reads from. Every field below was empty before ADR D86.
    [Fact]
    public void EditName_hydrates_the_whole_wizard_from_the_stored_configuration()
    {
        SetupEngineAvailable(["sql"]);
        SetupEditing("clientsVip", """
            {
              "name": "clientsVip",
              "source": {
                "type": "sql",
                "properties": {
                  "sql": "SELECT Id, Customer FROM Clients ORDER BY Id",
                  "key": "Id",
                  "connectionString": "${CLIENTS_DB}"
                }
              },
              "columns": [{ "name": "Id", "type": "Integer" }, { "name": "Customer", "type": "String" }],
              "outputs": [{ "format": "csv" }, { "format": "xlsx" }],
              "destinations": [{ "type": "local", "properties": { "path": "./out/{name}.{ext}" } }],
              "pageSize": 500,
              "trackProgress": false,
              "resilience": {
                "maxAttempts": 3, "backoff": "Exponential", "baseDelaySeconds": 2, "jitter": true,
                "onFailure": "skip-and-log", "abortWhen": { "consecutiveFailures": 4, "failureRate": 0.25 }
              },
              "schedule": { "cron": "0 6 * * 1" }
            }
            """);

        Render<Builder>();

        Wizard.IsEditing.ShouldBeTrue();
        Wizard.EditingOriginalName.ShouldBe("clientsVip");
        Wizard.ReportName.ShouldBe("clientsVip");
        Wizard.SourceType.ShouldBe("sql");
        Wizard.SqlQuery.ShouldBe("SELECT Id, Customer FROM Clients ORDER BY Id");
        Wizard.KeyColumn.ShouldBe("Id");
        Wizard.ConnectionStringVariable.ShouldBe("CLIENTS_DB");
        Wizard.ColumnNames.ShouldBe("Id, Customer");
        Wizard.PageSize.ShouldBe(500);
        Wizard.TrackProgress.ShouldBeFalse();
        Wizard.Formats.SetEquals(["csv", "xlsx"]).ShouldBeTrue();
        Wizard.DestinationType.ShouldBe("local");
        Wizard.DestinationPath.ShouldBe("./out/{name}.{ext}");
        Wizard.RetryMaxAttempts.ShouldBe(3);
        Wizard.RetryBackoff.ShouldBe("Exponential");
        Wizard.RetryBaseDelaySeconds.ShouldBe(2);
        Wizard.RetryJitter.ShouldBeTrue();
        Wizard.FailureStrategy.ShouldBe("skip-and-log");
        Wizard.AbortOnConsecutiveFailures.ShouldBeTrue();
        Wizard.AbortConsecutiveFailures.ShouldBe(4);
        Wizard.AbortOnTotalFailures.ShouldBeFalse();
        Wizard.AbortOnFailureRate.ShouldBeTrue();
        Wizard.AbortFailureRatePercent.ShouldBe(25);
        Wizard.ScheduleCron.ShouldBe("0 6 * * 1");
    }

    [Fact]
    public void Editing_a_ref_based_report_takes_the_source_type_from_the_registry()
    {
        SetupEngineAvailable(["sql"], [new ApiSourceView("clients-db", "postgres", null, 1, null, null, null, null)]);
        SetupEditing("clientsVip", """
            {"name":"clientsVip","source":{"ref":"clients-db","properties":{"sql":"SELECT 1","key":"Id"}}}
            """);

        Render<Builder>();

        // The document carries no type — a ref's type belongs to the registry (D42) — and without it
        // the Configure step would offer a generic property editor instead of the SQL one.
        Wizard.SourceRef.ShouldBe("clients-db");
        Wizard.SourceType.ShouldBe("postgres");
        Wizard.SqlQuery.ShouldBe("SELECT 1");
    }

    [Fact]
    public void Editing_a_report_whose_secrets_were_redacted_keeps_them_without_showing_them()
    {
        SetupEngineAvailable(["http"]);
        SetupEditing("apiFeed", """
            {
              "name": "apiFeed",
              "source": {
                "type": "http",
                "properties": {
                  "url": "https://api.example.com/items",
                  "bearerToken": "${neoreports:redacted}",
                  "connectionString": "${neoreports:redacted}"
                }
              }
            }
            """);

        Render<Builder>();

        Wizard.ConnectionStringRedacted.ShouldBeTrue();
        Wizard.ConnectionStringVariable.ShouldBe("");
        // The connection has its own field, so it is never also a row; the other redacted property
        // stays visible and editable, placeholder and all.
        Wizard.SourceProperties.Select(row => row.Key).ShouldBe(["url", "bearerToken"]);
        Wizard.SourceProperties.Single(row => row.Key == "bearerToken").Value.ShouldBe("${neoreports:redacted}");
    }

    [Fact]
    public void EditName_for_a_report_with_no_stored_config_falls_back_to_a_blank_wizard()
    {
        // Code-registered reports have no document to return (the engine 404s) — a blank "new
        // report" wizard is the honest outcome, not a form half-filled from somewhere else.
        Api.ReportConfig = (_, _) => Task.FromResult<string?>(null);
        SetupEngineAvailable(["sql"]);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("edit", "codeReport"));

        Render<Builder>();

        Wizard.IsEditing.ShouldBeFalse();
        Wizard.ReportName.ShouldBe("");
    }
}
