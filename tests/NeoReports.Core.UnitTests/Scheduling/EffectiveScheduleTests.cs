using NeoReports.Abstractions;
using NeoReports.Core.Scheduling;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.Scheduling;

/// <summary>ADR D41: declared × override × tombstone resolution matrix.</summary>
public class EffectiveScheduleTests
{
    [Fact]
    public void No_declaration_and_no_override_is_unscheduled()
    {
        EffectiveSchedule.Resolve(null, null).ShouldBeNull();
        EffectiveSchedule.IsOverridden(null).ShouldBeFalse();
    }

    [Fact]
    public void Declaration_alone_is_effective()
    {
        EffectiveSchedule.Resolve(new ScheduleConfig("0 6 * * 1"), null).ShouldBe("0 6 * * 1");
        EffectiveSchedule.IsOverridden(null).ShouldBeFalse();
    }

    [Fact]
    public void Override_wins_over_declaration()
    {
        var declared = new ScheduleConfig("0 6 * * 1");
        var overrideEntry = new ScheduleOverrideEntry("0 0 * * *");

        EffectiveSchedule.Resolve(declared, overrideEntry).ShouldBe("0 0 * * *");
        EffectiveSchedule.IsOverridden(overrideEntry).ShouldBeTrue();
    }

    [Fact]
    public void Tombstone_override_wins_over_declaration_as_unscheduled()
    {
        var declared = new ScheduleConfig("0 6 * * 1");
        var tombstone = new ScheduleOverrideEntry(null);

        EffectiveSchedule.Resolve(declared, tombstone).ShouldBeNull();
        EffectiveSchedule.IsOverridden(tombstone).ShouldBeTrue();
    }

    [Fact]
    public void Override_with_no_declaration_is_still_effective()
    {
        var overrideEntry = new ScheduleOverrideEntry("*/5 * * * *");
        EffectiveSchedule.Resolve(null, overrideEntry).ShouldBe("*/5 * * * *");
    }
}
