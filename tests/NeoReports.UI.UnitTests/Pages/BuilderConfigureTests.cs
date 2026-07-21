using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class BuilderConfigureTests : NeoReportsTestContext
{
    [Fact]
    public void Typing_in_report_name_and_sql_query_updates_the_wizard_state()
    {
        var cut = Render<BuilderConfigure>();

        cut.Find("input.mono[placeholder='monthly-sales']").Input("clientsVip");
        cut.Find("textarea").Input("SELECT Id FROM Sales");

        Wizard.ReportName.ShouldBe("clientsVip");
        Wizard.SqlQuery.ShouldBe("SELECT Id FROM Sales");
    }

    [Fact]
    public void Registered_source_hides_the_connection_string_field()
    {
        Wizard.SourceRef = "postgres-demo";
        Wizard.SourceType = "postgres";

        var cut = Render<BuilderConfigure>();

        cut.Markup.ShouldContain("resolved from");
        cut.Markup.ShouldNotContain("Connection string variable");
    }

    [Fact]
    public void Validate_success_shows_the_valid_banner_with_columns()
    {
        Api.ValidateReport = (_, _) => Task.FromResult<ApiValidationResult?>(
            new ApiValidationResult(true, null, "clientsVip", ["Id", "Name"], false));

        var cut = Render<BuilderConfigure>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Validate")).Click();

        cut.Markup.ShouldContain("Valid");
        cut.Markup.ShouldContain("Id, Name");
    }

    [Fact]
    public void Validate_rejection_shows_the_invalid_banner_with_the_engines_error()
    {
        Api.ValidateReport = (_, _) => Task.FromResult<ApiValidationResult?>(
            new ApiValidationResult(false, "Unknown source type 'foo'.", null, null, false));

        var cut = Render<BuilderConfigure>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Validate")).Click();

        cut.Markup.ShouldContain("Invalid configuration");
        cut.Markup.ShouldContain("Unknown source type");
    }

    [Fact]
    public void Validate_when_engine_unreachable_shows_the_could_not_reach_banner()
    {
        Api.ValidateReport = (_, _) => Task.FromResult<ApiValidationResult?>(null);

        var cut = Render<BuilderConfigure>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Validate")).Click();

        cut.Markup.ShouldContain("Could not reach the engine to validate.");
    }

    [Fact]
    public void Turning_off_TrackProgress_shows_the_no_completion_percentage_warning()
    {
        var cut = Render<BuilderConfigure>();
        cut.Markup.ShouldNotContain("No completion percentage.");

        cut.Find(".cgr .sw").Click();

        Wizard.TrackProgress.ShouldBeFalse();
        cut.WaitForState(() => cut.Markup.Contains("No completion percentage."));
    }

    [Fact]
    public void Skip_and_log_failure_strategy_reveals_abort_threshold_controls()
    {
        var cut = Render<BuilderConfigure>();
        cut.Markup.ShouldNotContain("Abort when");

        cut.Find("select.input.mono").Change("skip-and-log");

        cut.Markup.ShouldContain("Abort when");
        cut.Markup.ShouldContain("consecutive failures");
    }

    [Fact]
    public void Abort_threshold_number_input_is_disabled_until_its_switch_is_on()
    {
        Wizard.FailureStrategy = "skip-and-log";

        var cut = Render<BuilderConfigure>();
        var consecutiveInput = cut.FindAll(".cgr .ctl.col input[type='number']")[0];
        consecutiveInput.HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void Non_ado_source_type_shows_the_generic_property_editor_not_sql_fields()
    {
        Wizard.SourceType = "http";

        var cut = Render<BuilderConfigure>();

        cut.Markup.ShouldNotContain("SQL query");
        cut.Markup.ShouldNotContain("Key column");
        cut.Markup.ShouldContain("Source properties");
        cut.Markup.ShouldContain("url, strategy, recordsPath");
    }

    [Fact]
    public void Non_ado_source_type_seeds_one_blank_property_row()
    {
        Wizard.SourceType = "elasticsearch";

        var cut = Render<BuilderConfigure>();

        cut.FindAll("input.mono[placeholder='url']").Count.ShouldBe(1);
    }

    [Fact]
    public void Typing_a_property_key_and_value_updates_the_wizard_state()
    {
        Wizard.SourceType = "http";

        var cut = Render<BuilderConfigure>();

        cut.Find("input.mono[placeholder='url']").Input("url");
        cut.Find("input.mono[placeholder='https://api.example.com']").Input("https://sales.example.com");

        Wizard.SourceProperties.ShouldHaveSingleItem();
        Wizard.SourceProperties[0].Key.ShouldBe("url");
        Wizard.SourceProperties[0].Value.ShouldBe("https://sales.example.com");
    }

    [Fact]
    public void Add_property_appends_a_row_and_trash_removes_it()
    {
        Wizard.SourceType = "http";

        var cut = Render<BuilderConfigure>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add property")).Click();

        Wizard.SourceProperties.Count.ShouldBe(2);

        cut.FindAll(".btn.icon-only.outline")[0].Click();

        Wizard.SourceProperties.Count.ShouldBe(1);
    }

    [Fact]
    public void Unknown_source_type_falls_back_to_the_generic_hint_message()
    {
        Wizard.SourceType = "some-future-provider";

        var cut = Render<BuilderConfigure>();

        cut.Markup.ShouldContain("check the source package's docs for its required properties");
    }

    [Fact]
    public void Ado_shape_type_still_shows_the_sql_fields()
    {
        Wizard.SourceType = "postgres";

        var cut = Render<BuilderConfigure>();

        cut.Markup.ShouldContain("SQL query");
        cut.Markup.ShouldContain("Key column");
        cut.Markup.ShouldNotContain("Add property");
    }

    [Fact]
    public void Continue_and_Back_navigate_without_any_validation()
    {
        var cut = Render<BuilderConfigure>();
        var nav = Services.GetRequiredService<NavigationManager>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Continue")).Click();
        nav.Uri.ShouldEndWith("builder/format");

        cut.FindAll("button").First(b => b.TextContent.Contains("Back")).Click();
        nav.Uri.ShouldEndWith("builder");
    }
}
