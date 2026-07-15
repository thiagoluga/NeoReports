using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class ReportDetailTests : NeoReportsTestContext
{
    private static ApiReportDetail Detail(
        string name = "clientsVip", bool deletable = true, string origin = "config",
        string? scheduleCron = null, bool scheduleOverridden = false, string? sourceRef = null) => new(
        Name: name,
        Columns: [new ApiReportColumn("Id", "Integer", null, null, false)],
        PageSize: 1000,
        Formats: ["csv"],
        Destinations: ["local"],
        FailureStrategy: "abort",
        RetryMaxAttempts: 1,
        RetryBackoff: "Constant",
        RetryBaseDelaySeconds: 1,
        RetryUseJitter: false,
        Origin: origin,
        Deletable: deletable,
        ScheduleCron: scheduleCron,
        ScheduleOverridden: scheduleOverridden,
        SourceRef: sourceRef);

    private void SetupLiveWithNoHistory(ApiReportDetail detail)
    {
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(detail);
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], [], [], true));
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());
    }

    [Fact]
    public void Shows_a_loading_state_while_the_detail_fetch_is_in_flight()
    {
        var tcs = new TaskCompletionSource<ApiReportDetail?>();
        Api.ReportDetail = (_, _) => tcs.Task;
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(null);

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));

        cut.Find(".es-title").TextContent.ShouldBe("Loading…");
    }

    [Fact]
    public void Not_found_shows_an_empty_state_naming_the_requested_slug()
    {
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(null);
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(null);

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "missingReport"));

        cut.Find(".es-title").TextContent.ShouldBe("Report not found");
        cut.Markup.ShouldContain("missingReport");
    }

    [Fact]
    public void SourceRef_chip_shows_only_for_a_Ref_based_report()
    {
        SetupLiveWithNoHistory(Detail(sourceRef: "sales-db"));
        var withRef = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        withRef.Markup.ShouldContain("source: sales-db");

        SetupLiveWithNoHistory(Detail(sourceRef: null));
        var withoutRef = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        withoutRef.Markup.ShouldNotContain("source:");
    }

    [Fact]
    public void Deletable_report_shows_edit_and_delete_buttons_non_deletable_does_not()
    {
        SetupLiveWithNoHistory(Detail(deletable: true));
        var deletable = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        deletable.Markup.ShouldContain("Delete report");
        deletable.Markup.ShouldContain("Edit");

        SetupLiveWithNoHistory(Detail(deletable: false));
        var notDeletable = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        notDeletable.Markup.ShouldNotContain("Delete report");
    }

    [Fact]
    public void First_delete_click_only_asks_for_confirmation_second_click_deletes()
    {
        SetupLiveWithNoHistory(Detail());
        Api.DeleteReport = (_, _) => Task.FromResult(true);

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        var deleteButton = cut.FindAll("button").First(b => b.TextContent.Contains("Delete report"));
        deleteButton.Click();

        cut.FindAll("button").ShouldContain(b => b.TextContent.Contains("Confirm delete"));
        Api.LastDeletedReportName.ShouldBeNull();

        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm delete")).Click();

        Api.LastDeletedReportName.ShouldBe("clientsVip");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("reports");
    }

    [Fact]
    public void Scheduling_unsupported_by_host_hides_schedule_controls_and_explains_why()
    {
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(Detail());
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], [], [], false));
        Api.Jobs = (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(Array.Empty<ApiJobView>());

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));

        cut.Markup.ShouldContain("Scheduling is not supported by this host's job scheduler.");
        cut.Markup.ShouldNotContain("Hourly");
    }

    [Fact]
    public void Existing_schedule_shows_a_Clear_button_no_schedule_does_not()
    {
        SetupLiveWithNoHistory(Detail(scheduleCron: "0 6 * * *"));
        var withSchedule = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        withSchedule.FindAll("button").ShouldContain(b => b.TextContent == "Clear");

        SetupLiveWithNoHistory(Detail(scheduleCron: null));
        var withoutSchedule = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        withoutSchedule.FindAll("button").ShouldNotContain(b => b.TextContent == "Clear");
    }

    [Fact]
    public void Run_now_triggers_TryRunReportAsync_and_navigates_to_the_new_job()
    {
        SetupLiveWithNoHistory(Detail());
        Api.RunReport = (_, _) => Task.FromResult<string?>("job-99");

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Run now")).Click();

        Api.LastRunReportName.ShouldBe("clientsVip");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("jobs/job-99");
    }

    [Fact]
    public void No_run_history_shows_the_no_runs_yet_empty_state()
    {
        SetupLiveWithNoHistory(Detail());

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));

        cut.FindAll(".es-title").ShouldContain(e => e.TextContent == "No runs yet");
    }

    [Fact]
    public void Configuration_card_shows_the_reports_real_page_size()
    {
        // A PageSize under 1000 avoids a thousands separator, which is culture-dependent (the app
        // itself formats with the current culture, same as JobCompleted.razor's BufferText).
        SetupLiveWithNoHistory(Detail() with { PageSize = 500 });

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));

        cut.Markup.ShouldContain("500 rows/page");
    }

    [Fact]
    public void Set_schedule_sends_the_trimmed_cron_and_reloads_the_detail_on_success()
    {
        SetupLiveWithNoHistory(Detail(scheduleCron: null));
        Api.SetSchedule = (_, _, _) => Task.FromResult(true);

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        cut.Find("input[placeholder='0 6 * * 1']").Input("  0 8 * * *  ");
        cut.FindAll("button").First(b => b.TextContent.Contains("Set schedule")).Click();

        Api.LastSetSchedule.ShouldBe(("clientsVip", "0 8 * * *"));
    }

    [Fact]
    public void Set_schedule_rejection_shows_the_honest_error_banner()
    {
        SetupLiveWithNoHistory(Detail(scheduleCron: null));
        Api.SetSchedule = (_, _, _) => Task.FromResult(false);

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        cut.Find("input[placeholder='0 6 * * 1']").Input("0 8 * * *");
        cut.FindAll("button").First(b => b.TextContent.Contains("Set schedule")).Click();

        cut.Markup.ShouldContain("The engine rejected the schedule (invalid cron, unknown report, or scheduling isn't supported).");
    }

    [Fact]
    public void Clear_schedule_calls_the_engine_and_removes_the_Clear_button_on_success()
    {
        SetupLiveWithNoHistory(Detail(scheduleCron: "0 6 * * *"));
        var cleared = false;
        Api.ClearSchedule = (name, _) => { cleared = true; return Task.FromResult(true); };
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(cleared ? Detail(scheduleCron: null) : Detail(scheduleCron: "0 6 * * *"));

        var cut = Render<ReportDetail>(p => p.Add(x => x.Slug, "clientsVip"));
        cut.FindAll("button").First(b => b.TextContent == "Clear").Click();

        cleared.ShouldBeTrue();
        cut.WaitForState(() => !cut.FindAll("button").Any(b => b.TextContent == "Clear"));
    }
}
