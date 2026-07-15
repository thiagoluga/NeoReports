using Bunit;
using NeoReports.UI.Components.UI;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Components;

public sealed class FilterBarTests : NeoReportsTestContext
{
    [Fact]
    public void Typing_in_the_input_invokes_SearchChanged_with_the_new_value()
    {
        var reported = (string?)null;
        var cut = Render<FilterBar>(p => p
            .Add(x => x.Search, "")
            .Add(x => x.SearchChanged, v => reported = v));

        cut.Find("input").Input("acme");

        reported.ShouldBe("acme");
    }

    [Fact]
    public void Placeholder_and_initial_Search_value_are_rendered()
    {
        var cut = Render<FilterBar>(p => p
            .Add(x => x.Search, "existing")
            .Add(x => x.Placeholder, "Search reports"));

        var input = cut.Find("input");
        input.GetAttribute("placeholder").ShouldBe("Search reports");
        input.GetAttribute("value").ShouldBe("existing");
    }
}
