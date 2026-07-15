using Bunit;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

/// <summary>
/// Covers the Preview screen's filter wiring end to end at the UI layer — including the exact
/// "This source type doesn't support server-side filters" banner the maintainer reported seeing
/// on a Postgres-sourced report (2026-07-15). These tests prove the banner text is driven purely
/// by <see cref="ApiPreviewData.FiltersApplied"/> in the engine's own response, not by any
/// source-type check in the page itself (ADR D45) — so if that banner shows up unexpectedly for a
/// source with a registered filter translator, the defect is in the engine's preview endpoint /
/// filter-translator resolution for that source, not in this Blazor page. See DECISIONS.md D53.
/// </summary>
public sealed class ReportPreviewTests : NeoReportsTestContext
{
    private static readonly ApiReportColumn[] Columns =
        [new("Id", "Integer", null, null, false), new("TransactionId", "String", null, null, false)];

    private static ApiReportDetail Detail(string origin) => new(
        Name: "vip",
        Columns: Columns,
        PageSize: 1000,
        Formats: ["csv"],
        Destinations: ["local"],
        FailureStrategy: "abort",
        RetryMaxAttempts: 1,
        RetryBackoff: "Constant",
        RetryBaseDelaySeconds: 1,
        RetryUseJitter: false,
        Origin: origin,
        Deletable: origin == "config");

    private static ApiPreviewResult OkResult(bool filtersApplied, IReadOnlyList<object?[]>? rows = null) =>
        new(ApiPreviewOutcome.Ok, new ApiPreviewData(rows ?? Array.Empty<object?[]>(), Columns, filtersApplied, false), null);

    private void SetupDynamicReport(bool filtersApplied)
    {
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(Detail("config"));
        Api.Preview = (_, _, _, _) => Task.FromResult(OkResult(filtersApplied));
    }

    [Fact]
    public void Code_origin_report_hides_the_filter_editor_and_shows_the_not_filterable_banner()
    {
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(Detail("code"));
        Api.Preview = (_, _, _, _) => Task.FromResult(OkResult(false));

        var cut = Render<ReportPreview>(p => p.Add(x => x.Name, "vip"));

        cut.Markup.ShouldContain("Filters aren't available for this report");
        cut.Find("h1").TextContent.ShouldBe("Preview · vip");
        cut.Markup.ShouldNotContain("Add filter");
        cut.Markup.ShouldNotContain("Structured, closed-operator filters");
    }

    [Fact]
    public void Dynamic_origin_report_shows_the_filter_editor_and_no_banner_before_any_filter_is_applied()
    {
        SetupDynamicReport(filtersApplied: false);

        var cut = Render<ReportPreview>(p => p.Add(x => x.Name, "vip"));

        cut.Markup.ShouldContain("Add filter");
        cut.Markup.ShouldNotContain("This source type doesn't support server-side filters");
        Api.LastPreviewFilters.ShouldBeEmpty();
    }

    [Fact]
    public void Applying_a_filter_sends_exactly_the_configured_column_operator_and_value()
    {
        SetupDynamicReport(filtersApplied: true);

        var cut = Render<ReportPreview>(p => p.Add(x => x.Name, "vip"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Add filter")).Click();
        cut.Find("select.input.mono").Change("TransactionId");
        cut.Find("input.input[placeholder='value']").Change("abc-123");
        cut.FindAll("button").First(b => b.TextContent.Contains("Apply")).Click();

        Api.LastPreviewFilters.ShouldHaveSingleItem();
        var filter = Api.LastPreviewFilters!.Single();
        filter.Column.ShouldBe("TransactionId");
        filter.Operator.ShouldBe("Equals");
        filter.Value.ShouldBe("abc-123");
    }

    [Fact]
    public void Server_reports_FiltersApplied_false_after_a_real_filter_shows_the_exact_unsupported_banner()
    {
        SetupDynamicReport(filtersApplied: false);

        var cut = Render<ReportPreview>(p => p.Add(x => x.Name, "vip"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Add filter")).Click();
        cut.Find("input.input[placeholder='value']").Change("abc-123");
        cut.FindAll("button").First(b => b.TextContent.Contains("Apply")).Click();

        cut.Markup.ShouldContain(
            "This source type doesn't support server-side filters — showing the unfiltered sample instead.");
        cut.FindAll("button").First(b => b.TextContent.Contains("Run now")).HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public void Server_reports_FiltersApplied_true_hides_the_banner_and_disables_Run()
    {
        SetupDynamicReport(filtersApplied: true);

        var cut = Render<ReportPreview>(p => p.Add(x => x.Name, "vip"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Add filter")).Click();
        cut.Find("input.input[placeholder='value']").Change("abc-123");
        cut.FindAll("button").First(b => b.TextContent.Contains("Apply")).Click();

        cut.Markup.ShouldNotContain("This source type doesn't support server-side filters");
        cut.Markup.ShouldContain("Run with these filters");
        cut.Markup.ShouldContain("Filtered runs aren't supported yet");
        cut.FindAll("button").First(b => b.TextContent.Contains("Run with these filters")).HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void Changing_page_size_reloads_immediately_without_needing_Apply()
    {
        SetupDynamicReport(filtersApplied: false);

        var cut = Render<ReportPreview>(p => p.Add(x => x.Name, "vip"));
        cut.Find("select[value='50']").Change("100");

        Api.LastPreviewPageSize.ShouldBe(100);
    }

    [Fact]
    public void Not_found_shows_an_empty_state_naming_the_requested_report()
    {
        Api.ReportDetail = (_, _) => Task.FromResult<ApiReportDetail?>(null);

        var cut = Render<ReportPreview>(p => p.Add(x => x.Name, "ghost"));

        cut.Find(".es-title").TextContent.ShouldBe("Report not found");
        cut.Markup.ShouldContain("ghost");
    }
}
