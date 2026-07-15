using Bunit;
using NeoReports.UI.Components.UI;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Components;

public sealed class SwitchTests : NeoReportsTestContext
{
    [Fact]
    public void Renders_on_class_when_Value_is_true()
    {
        var cut = Render<Switch>(p => p.Add(x => x.Value, true));

        cut.Find("button").ClassList.ShouldContain("on");
        cut.Find("button").HasAttribute("aria-pressed").ShouldBeTrue();
    }

    [Fact]
    public void Clicking_toggles_Value_and_invokes_ValueChanged()
    {
        var newValue = (bool?)null;
        var cut = Render<Switch>(p => p
            .Add(x => x.Value, false)
            .Add(x => x.ValueChanged, v => newValue = v));

        cut.Find("button").Click();

        newValue.ShouldBe(true);
    }
}
