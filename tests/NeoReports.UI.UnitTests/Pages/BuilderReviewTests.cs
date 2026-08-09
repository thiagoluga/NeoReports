using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class BuilderReviewTests : NeoReportsTestContext
{
    private IRenderedComponent<BuilderReview> RenderReview()
    {
        Wizard.EngineAvailable = true;
        return Render<BuilderReview>();
    }

    [Fact]
    public void Source_edit_link_passes_resume_true_so_step_1_keeps_the_wizard_state()
    {
        Wizard.SourceType = "sql";
        Wizard.SqlQuery = "SELECT Id FROM Sales";

        var cut = RenderReview();
        cut.FindAll(".edit-link")[0].Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("builder?resume=true");

        Render<Builder>();
        Wizard.SqlQuery.ShouldBe("SELECT Id FROM Sales");
    }

    [Fact]
    public void Creating_a_new_report_navigates_to_its_detail_page_on_success()
    {
        Wizard.ReportName = "clientsVip";
        Api.CreateReport = (_, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Created, "clientsVip", null));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        Api.LastCreateReportConfigJson.ShouldNotBeNull();
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("reports/clientsVip");
    }

    [Fact]
    public void Name_taken_shows_the_engines_default_error_message()
    {
        Wizard.ReportName = "clientsVip";
        Api.CreateReport = (_, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.NameTaken, null, null));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        cut.Markup.ShouldContain("A report with this name already exists.");
    }

    [Fact]
    public void Run_now_creates_then_runs_and_navigates_to_the_new_job()
    {
        Wizard.ReportName = "clientsVip";
        Api.CreateReport = (_, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Created, "clientsVip", null));
        Api.RunReport = (_, _) => Task.FromResult<string?>("job-1");

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Run now")).Click();

        Api.LastRunReportName.ShouldBe("clientsVip");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/job-1");
    }

    [Fact]
    public void Run_now_when_the_trigger_fails_after_a_successful_save_reports_the_report_name()
    {
        Wizard.ReportName = "clientsVip";
        Api.CreateReport = (_, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Created, "clientsVip", null));
        Api.RunReport = (_, _) => Task.FromResult<string?>(null);

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Run now")).Click();

        cut.Markup.ShouldContain("Report 'clientsVip' was saved but could not be started.");
    }

    [Fact]
    public void Editing_with_an_unreachable_engine_reports_unavailable()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Api.ReplaceReport = (_, _, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Unavailable, null, null));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        cut.Markup.ShouldContain("The engine is not reachable right now.");
    }

    [Fact]
    public void Editing_with_an_invalid_config_reports_the_engine_error_and_leaves_the_report_alone()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Api.ReplaceReport = (_, _, _) =>
            Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Invalid, null, "Query references an unknown column."));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        // The whole point of replacing in one call: a rejected edit deletes nothing. The old flow
        // deleted first and could leave the user with no report at all.
        Api.LastDeletedReportName.ShouldBeNull();
        Api.LastCreateReportConfigJson.ShouldBeNull();
        cut.Markup.ShouldContain("Query references an unknown column.");
    }

    [Fact]
    public void Editing_saves_through_a_single_replace_call_and_never_deletes()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Wizard.ReportName = "clientsVip";
        Api.ReplaceReport = (name, _, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Created, name, null));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        Api.LastReplaceReport!.Value.Name.ShouldBe("clientsVip");
        Api.LastDeletedReportName.ShouldBeNull();
        Api.LastCreateReportConfigJson.ShouldBeNull();
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("reports/clientsVip");
    }

    [Fact]
    public void Editing_sends_the_stored_document_patched_rather_than_a_regenerated_one()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Wizard.ReportName = "clientsVip";
        Wizard.PageSize = 250;
        Wizard.OriginalDocument = """
            {"name":"clientsVip","source":{"type":"sql","properties":{"sql":"SELECT 1","key":"Id"}},
             "columns":[{"name":"Id","type":"Integer"}],"outputs":[{"format":"csv"}],"pageSize":1000,
             "filter":{"==":[{"var":"Active"},true]}}
            """;
        Wizard.LoadedSourceIdentity = "type:sql";
        Wizard.SourceType = "sql";
        Wizard.SqlQuery = "SELECT 1";
        Wizard.KeyColumn = "Id";
        Wizard.ColumnNames = "Id";
        Wizard.Formats = ["csv"];
        Api.ReplaceReport = (name, _, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Created, name, null));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        using JsonDocument sent = JsonDocument.Parse(Api.LastReplaceReport!.Value.ConfigJson);
        sent.RootElement.GetProperty("pageSize").GetInt32().ShouldBe(250);
        // The wizard has no filter editor and no column-type editor. Regenerating the document from
        // the form would have deleted both without a word.
        sent.RootElement.TryGetProperty("filter", out _).ShouldBeTrue();
        sent.RootElement.GetProperty("columns").EnumerateArray().Single()
            .GetProperty("type").GetString().ShouldBe("Integer");
    }

    [Fact]
    public void Save_and_run_are_disabled_when_the_engine_is_unavailable()
    {
        Wizard.EngineAvailable = false;

        var cut = Render<BuilderReview>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).HasAttribute("disabled").ShouldBeTrue();
        cut.FindAll("button").First(b => b.TextContent.Contains("Run now")).HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void Schedule_preset_buttons_set_the_cron_and_Clear_only_shows_when_a_schedule_is_set()
    {
        Wizard.ScheduleCron = "";
        var cut = RenderReview();
        cut.FindAll("button").ShouldNotContain(b => b.TextContent == "Clear");

        cut.FindAll("button").First(b => b.TextContent.Contains("Daily 06:00")).Click();

        Wizard.ScheduleCron.ShouldBe("0 6 * * *");
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent == "Clear"));
    }
}
