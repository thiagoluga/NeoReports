using NeoReports.Core.Configuration;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// The dynamic-report name validator is the only thing standing between a caller-supplied name and a
/// file name on disk, a URL segment, a job record field and every log line about a run — and it had no
/// tests at all, which is how an under-anchored pattern survived.
/// </summary>
public class DynamicReportNameTests
{
    [Theory]
    [InlineData("sales")]
    [InlineData("Sales")]
    [InlineData("monthly-sales_2026")]
    [InlineData("a")]
    public void An_ordinary_name_is_accepted(string name) =>
        DynamicReportName.IsValid(name).ShouldBeTrue();

    /// <summary>
    /// The reason this file exists. In .NET, <c>$</c> matches at the end of input <b>and</b>
    /// immediately before a trailing newline, so <c>^…$</c> accepted a name ending in one. That name
    /// is remotely creatable (POST /api/reports), and it reaches ReportJobWorker's and
    /// InMemoryJobScheduler's log statements — where, in a plain-text sink, the newline splits the
    /// line and lets the name forge a log entry of its own (CodeQL cs/log-forging).
    /// </summary>
    [Fact]
    public void A_name_ending_in_a_newline_is_rejected()
    {
        DynamicReportName.IsValid("sales" + (char)10).ShouldBeFalse();
        DynamicReportName.IsValid("sales" + (char)13 + (char)10).ShouldBeFalse();
        DynamicReportName.IsValid("sales" + (char)13).ShouldBeFalse();
    }

    [Fact]
    public void A_name_with_an_embedded_control_character_is_rejected()
    {
        DynamicReportName.IsValid("sa" + (char)10 + "les").ShouldBeFalse();
        DynamicReportName.IsValid("sales" + (char)9 + "monthly").ShouldBeFalse();
        DynamicReportName.IsValid("sales" + (char)0).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1sales")]          // must start with a letter
    [InlineData("-sales")]
    [InlineData("sales monthly")]   // no spaces: it becomes a URL segment
    [InlineData("sales.monthly")]   // no dots: it becomes a file name
    [InlineData("../sales")]        // the path-traversal case the type was written for
    [InlineData("sales/../etc")]
    public void A_name_outside_the_grammar_is_rejected(string name) =>
        DynamicReportName.IsValid(name).ShouldBeFalse();

    [Fact]
    public void Null_is_rejected_rather_than_throwing() =>
        DynamicReportName.IsValid(null).ShouldBeFalse();

    [Fact]
    public void The_length_bound_is_enforced()
    {
        DynamicReportName.IsValid("a" + new string('b', 99)).ShouldBeTrue();
        DynamicReportName.IsValid("a" + new string('b', 100)).ShouldBeFalse();
    }
}
