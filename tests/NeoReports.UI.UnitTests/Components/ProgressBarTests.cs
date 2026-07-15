using Bunit;
using NeoReports.UI.Components.UI;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Components;

public sealed class ProgressBarTests : NeoReportsTestContext
{
    [Fact]
    public void Default_renders_hero_bar_class_with_width_from_Pct()
    {
        var cut = Render<ProgressBar>(p => p.Add(x => x.Pct, 64));

        cut.Find("div").ClassList.ShouldBe(["progress-bar"]);
        cut.Find("i").GetAttribute("style").ShouldBe("width:64%");
    }

    [Fact]
    public void Mini_renders_mini_bar_class()
    {
        var cut = Render<ProgressBar>(p => p.Add(x => x.Mini, true).Add(x => x.Pct, 30));

        cut.Find("div").ClassList.ShouldContain("mini-bar");
    }

    [Fact]
    public void Indeterminate_appends_indeterminate_class()
    {
        var cut = Render<ProgressBar>(p => p.Add(x => x.Indeterminate, true));

        cut.Find("div").ClassList.ShouldContain("indeterminate");
    }
}
