using Bunit;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class SourcesListTests : NeoReportsTestContext
{
    private static ApiSourceView Source(
        string name = "postgres-demo", string type = "postgres", int refs = 0,
        string? healthStatus = null, string? healthError = null) =>
        new(name, type, "Demo", refs, healthStatus, healthError, healthStatus is not null ? 12.5 : null,
            healthStatus is not null ? DateTimeOffset.UtcNow : null);

    private void SetupBasics(IReadOnlyList<ApiSourceView>? sources, IReadOnlyList<string>? types = null)
    {
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities(types ?? ["postgres", "sql"], [], []));
        Api.Sources = _ => Task.FromResult(sources);
    }

    [Fact]
    public void No_registered_provider_types_shows_the_empty_state()
    {
        SetupBasics(Array.Empty<ApiSourceView>(), types: []);

        var cut = Render<SourcesList>();

        cut.Find(".es-title").TextContent.ShouldBe("No source types registered");
    }

    [Fact]
    public void Engine_unreachable_for_sources_shows_the_unreachable_text()
    {
        SetupBasics(null);

        var cut = Render<SourcesList>();

        cut.Markup.ShouldContain("Engine unreachable.");
    }

    [Fact]
    public void No_sources_registered_shows_the_empty_state_with_an_add_action()
    {
        SetupBasics(Array.Empty<ApiSourceView>());

        var cut = Render<SourcesList>();

        cut.Find(".es-title").TextContent.ShouldBe("No sources registered yet");
    }

    [Fact]
    public void Health_summary_line_counts_healthy_unhealthy_and_never_checked()
    {
        SetupBasics(
        [
            Source("a", healthStatus: "healthy"),
            Source("b", healthStatus: "unhealthy"),
            Source("c", healthStatus: null),
        ]);

        var cut = Render<SourcesList>();

        cut.Markup.ShouldContain("1 healthy · 1 unhealthy · 1 never checked");
    }

    [Fact]
    public void Health_badge_maps_healthy_unhealthy_and_never_checked_correctly()
    {
        SetupBasics([Source("a", healthStatus: "healthy"), Source("b", healthStatus: "unhealthy"), Source("c")]);

        var cut = Render<SourcesList>();

        var badges = cut.FindAll(".badge").ToArray();
        badges.ShouldContain(b => b.ClassList.Contains("success") && b.TextContent.Contains("Healthy"));
        badges.ShouldContain(b => b.ClassList.Contains("danger") && b.TextContent.Contains("Unhealthy"));
        badges.ShouldContain(b => b.ClassList.Contains("gray") && b.TextContent.Contains("Never checked"));
    }

    [Fact]
    public void Add_source_form_requires_a_name_before_calling_the_engine()
    {
        SetupBasics(Array.Empty<ApiSourceView>());
        var createCalled = false;
        Api.CreateSource = (_, _, _, _, _) => { createCalled = true; return Task.FromResult(new ApiSourceSaveResult(ApiSourceSaveOutcome.Saved, null)); };

        var cut = Render<SourcesList>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add source")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Register source")).Click();

        createCalled.ShouldBeFalse();
        cut.Markup.ShouldContain("Name and type are required.");
    }

    [Fact]
    public void Successful_create_closes_the_form_and_reloads_the_list()
    {
        var reloaded = false;
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities(["postgres"], [], []));
        Api.Sources = _ =>
        {
            var result = reloaded
                ? new List<ApiSourceView> { Source("sales-db") }
                : [];
            reloaded = true;
            return Task.FromResult<IReadOnlyList<ApiSourceView>?>(result);
        };
        Api.CreateSource = (_, _, _, _, _) => Task.FromResult(new ApiSourceSaveResult(ApiSourceSaveOutcome.Saved, null));

        var cut = Render<SourcesList>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add source")).Click();
        cut.Find("input.mono[placeholder='sales-db']").Input("sales-db");
        cut.FindAll("button").First(b => b.TextContent.Contains("Register source")).Click();

        cut.Markup.ShouldContain("sales-db");
        cut.Markup.ShouldNotContain("Register source");
    }

    [Fact]
    public void Failed_create_shows_the_engines_error_and_keeps_the_form_open()
    {
        SetupBasics(Array.Empty<ApiSourceView>());
        Api.CreateSource = (_, _, _, _, _) => Task.FromResult(new ApiSourceSaveResult(ApiSourceSaveOutcome.NameTaken, null));

        var cut = Render<SourcesList>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add source")).Click();
        cut.Find("input.mono[placeholder='sales-db']").Input("sales-db");
        cut.FindAll("button").First(b => b.TextContent.Contains("Register source")).Click();

        cut.Markup.ShouldContain("A source with that name already exists.");
        cut.Markup.ShouldContain("Register source");
    }

    [Fact]
    public void First_delete_click_only_confirms_second_click_deletes()
    {
        SetupBasics([Source("sales-db")]);
        Api.DeleteSource = (_, _) => Task.FromResult(new ApiSourceDeleteResult(ApiSourceDeleteOutcome.Deleted, null));

        var cut = Render<SourcesList>();
        var deleteButton = cut.FindAll("button").First(b => b.TextContent.Contains("Delete"));
        deleteButton.Click();

        cut.FindAll("button").ShouldContain(b => b.TextContent.Contains("Confirm delete"));
        Api.LastDeletedSourceName.ShouldBeNull();

        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm delete")).Click();

        Api.LastDeletedSourceName.ShouldBe("sales-db");
    }

    [Fact]
    public void Delete_blocked_by_references_shows_the_engines_error()
    {
        SetupBasics([Source("sales-db", refs: 2)]);
        Api.DeleteSource = (_, _) => Task.FromResult(new ApiSourceDeleteResult(ApiSourceDeleteOutcome.Referenced, "Still referenced by 2 report(s)."));

        var cut = Render<SourcesList>();
        var deleteButton = cut.FindAll("button").First(b => b.TextContent.Contains("Delete"));
        deleteButton.Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm delete")).Click();

        cut.Markup.ShouldContain("Still referenced by 2 report(s).");
    }

    [Fact]
    public void Check_now_failure_shows_the_honest_could_not_run_message()
    {
        SetupBasics([Source("sales-db")]);
        Api.CheckSourceHealth = (_, _) => Task.FromResult<ApiSourceHealth?>(null);

        var cut = Render<SourcesList>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Check now")).Click();

        cut.Markup.ShouldContain("Could not run the health check");
    }

    [Fact]
    public void Edit_form_prefills_name_and_type_but_leaves_properties_blank()
    {
        SetupBasics([Source("sales-db", type: "postgres")]);

        var cut = Render<SourcesList>();
        cut.Find("button.icon-only.outline").Click();

        cut.Find("input.mono[disabled]").GetAttribute("value").ShouldBe("sales-db");
        cut.Markup.ShouldContain("Properties are write-only; re-enter them to change the source.");
    }

    [Fact]
    public void Saving_the_edit_form_replaces_the_source_by_name_not_creates_a_new_one()
    {
        SetupBasics([Source("sales-db", type: "postgres")]);
        var replaceCalled = false;
        Api.ReplaceSource = (name, type, _, _, _) => { replaceCalled = true; return Task.FromResult(new ApiSourceSaveResult(ApiSourceSaveOutcome.Saved, null)); };

        var cut = Render<SourcesList>();
        cut.Find("button.icon-only.outline").Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();

        replaceCalled.ShouldBeTrue();
    }

    [Fact]
    public void Failed_edit_shows_the_engines_error_and_keeps_the_form_open()
    {
        SetupBasics([Source("sales-db", type: "postgres")]);
        Api.ReplaceSource = (_, _, _, _, _) => Task.FromResult(new ApiSourceSaveResult(ApiSourceSaveOutcome.NotFound, null));

        var cut = Render<SourcesList>();
        cut.Find("button.icon-only.outline").Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Save changes")).Click();

        cut.Markup.ShouldContain("That source no longer exists.");
        cut.Markup.ShouldContain("Save changes");
    }
}
