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
        cut.FindAll(".edit-link").First().Click();

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
    public void Editing_with_unreachable_validation_reports_unavailable_and_never_deletes()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Api.ValidateReport = (_, _) => Task.FromResult<ApiValidationResult?>(null);

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        Api.LastDeletedReportName.ShouldBeNull();
        cut.Markup.ShouldContain("The engine is not reachable right now.");
    }

    [Fact]
    public void Editing_with_an_invalid_config_reports_the_validation_error_and_never_deletes()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Api.ValidateReport = (_, _) => Task.FromResult<ApiValidationResult?>(
            new ApiValidationResult(false, "Query references an unknown column.", null, null, false));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        Api.LastDeletedReportName.ShouldBeNull();
        cut.Markup.ShouldContain("Query references an unknown column.");
    }

    [Fact]
    public void Editing_when_deleting_the_original_fails_reports_that_nothing_changed_and_never_creates()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Api.ValidateReport = (_, _) => Task.FromResult<ApiValidationResult?>(new ApiValidationResult(true, null, "clientsVip", [], true));
        Api.DeleteReport = (_, _) => Task.FromResult(false);

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        Api.LastCreateReportConfigJson.ShouldBeNull();
        cut.Markup.ShouldContain("Could not remove the existing \"clientsVip\" report to replace it. Nothing was changed.");
    }

    [Fact]
    public void Editing_full_success_deletes_the_original_then_creates_the_replacement()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Wizard.ReportName = "clientsVip";
        Api.ValidateReport = (_, _) => Task.FromResult<ApiValidationResult?>(new ApiValidationResult(true, null, "clientsVip", [], true));
        Api.DeleteReport = (_, _) => Task.FromResult(true);
        Api.CreateReport = (_, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Created, "clientsVip", null));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        Api.LastDeletedReportName.ShouldBe("clientsVip");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("reports/clientsVip");
    }

    [Fact]
    public void Editing_when_recreate_fails_after_delete_explains_the_original_is_already_gone()
    {
        Wizard.IsEditing = true;
        Wizard.EditingOriginalName = "clientsVip";
        Api.ValidateReport = (_, _) => Task.FromResult<ApiValidationResult?>(new ApiValidationResult(true, null, "clientsVip", [], true));
        Api.DeleteReport = (_, _) => Task.FromResult(true);
        Api.CreateReport = (_, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Invalid, null, "Bad query."));

        var cut = RenderReview();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save report")).Click();

        cut.Markup.ShouldContain("was removed but the replacement could not be created: Bad query. Recreate it from the Builder.");
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
