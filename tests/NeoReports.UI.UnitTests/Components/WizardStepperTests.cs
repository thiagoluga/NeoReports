using Bunit;
using NeoReports.UI.Components.UI;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Components;

public sealed class WizardStepperTests : NeoReportsTestContext
{
    private static readonly string[] Steps = ["Source", "Columns", "Format", "Destination", "Review"];

    [Fact]
    public void Steps_before_Current_are_marked_done_and_render_a_check_icon()
    {
        var cut = Render<WizardStepper>(p => p.Add(x => x.Steps, Steps).Add(x => x.Current, 2));

        var wsteps = cut.FindAll(".wstep");
        wsteps[0].ClassList.ShouldContain("done");
        wsteps[1].ClassList.ShouldContain("done");
        wsteps[0].QuerySelector(".wnum i.ti-check").ShouldNotBeNull();
    }

    [Fact]
    public void Current_step_is_marked_active_and_later_steps_are_pending()
    {
        var cut = Render<WizardStepper>(p => p.Add(x => x.Steps, Steps).Add(x => x.Current, 2));

        var wsteps = cut.FindAll(".wstep");
        wsteps[2].ClassList.ShouldContain("active");
        wsteps[3].ClassList.ShouldNotContain("done");
        wsteps[3].ClassList.ShouldNotContain("active");
    }

    [Fact]
    public void Compact_label_shows_1_based_step_of_total()
    {
        var cut = Render<WizardStepper>(p => p.Add(x => x.Steps, Steps).Add(x => x.Current, 2));

        cut.Find(".wizard-compact").TextContent.ShouldBe("Step 3 of 5");
    }
}
