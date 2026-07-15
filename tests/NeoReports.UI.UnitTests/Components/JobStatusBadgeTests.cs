using Bunit;
using NeoReports.UI.Components.UI;
using NeoReports.UI.Models;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Components;

public sealed class JobStatusBadgeTests : NeoReportsTestContext
{
    [Fact]
    public void Running_status_maps_to_the_info_variant_with_live_pulsing_dot()
    {
        var cut = Render<JobStatusBadge>(p => p.Add(x => x.Status, JobStatus.Running));

        cut.Find(".badge").ClassList.ShouldContain("info");
        cut.Find(".badge").ClassList.ShouldContain("live");
        cut.Markup.ShouldContain("Running");
    }

    [Fact]
    public void Failed_status_maps_to_the_danger_variant_without_a_live_dot()
    {
        var cut = Render<JobStatusBadge>(p => p.Add(x => x.Status, JobStatus.Failed));

        cut.Find(".badge").ClassList.ShouldContain("danger");
        cut.Find(".badge").ClassList.ShouldNotContain("live");
    }

    [Fact]
    public void LabelOverride_replaces_the_mapped_label_text_but_not_the_variant()
    {
        var cut = Render<JobStatusBadge>(p => p
            .Add(x => x.Status, JobStatus.Running)
            .Add(x => x.LabelOverride, "Retrying"));

        cut.Find(".badge").ClassList.ShouldContain("info");
        cut.Markup.ShouldContain("Retrying");
        cut.Markup.ShouldNotContain(">Running<");
    }
}
