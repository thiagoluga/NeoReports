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
    public void Continue_navigates_to_configure_step_without_any_validation()
    {
        SetupEngineAvailable([]);

        var cut = Render<Builder>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Continue")).Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("builder/configure");
    }

    [Fact]
    public void EditName_hydrates_the_wizard_from_an_existing_deletable_report()
    {
        var detail = new ApiReportDetail(
            Name: "clientsVip",
            Columns: [new ApiReportColumn("Id", "Integer", null, null, false)],
            PageSize: 500,
            Formats: ["csv", "xlsx"],
            Destinations: ["local"],
            FailureStrategy: "skip-and-log",
            RetryMaxAttempts: 3,
            RetryBackoff: "Exponential",
            RetryBaseDelaySeconds: 2,
            RetryUseJitter: true,
            Origin: "config",
            Deletable: true);
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(detail);
        SetupEngineAvailable(["sql"]);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("edit", "clientsVip"));

        Render<Builder>();

        Wizard.IsEditing.ShouldBeTrue();
        Wizard.EditingOriginalName.ShouldBe("clientsVip");
        Wizard.ReportName.ShouldBe("clientsVip");
        Wizard.PageSize.ShouldBe(500);
        Wizard.Formats.SetEquals(["csv", "xlsx"]).ShouldBeTrue();
        Wizard.SourceType.ShouldBe("");
    }

    [Fact]
    public void EditName_for_a_non_deletable_report_falls_back_to_a_blank_wizard()
    {
        var detail = new ApiReportDetail(
            Name: "codeReport", Columns: [], PageSize: 1000, Formats: ["csv"], Destinations: [],
            FailureStrategy: "abort", RetryMaxAttempts: 1, RetryBackoff: "Constant", RetryBaseDelaySeconds: 1,
            RetryUseJitter: false, Origin: "code", Deletable: false);
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(detail);
        SetupEngineAvailable(["sql"]);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("edit", "codeReport"));

        Render<Builder>();

        Wizard.IsEditing.ShouldBeFalse();
        Wizard.ReportName.ShouldBe("");
    }
}
