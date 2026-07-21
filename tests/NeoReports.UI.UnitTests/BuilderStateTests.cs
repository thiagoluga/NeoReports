using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests;

/// <summary>
/// <see cref="BuilderState"/> — found during a 2026-07 UI audit that <c>Reset()</c>, despite
/// existing exactly for this purpose ("Reset everything (when starting a new report)"), was never
/// called anywhere: the Scoped wizard state leaked from one report into the next "New report".
/// </summary>
public class BuilderStateTests
{
    [Fact]
    public void Reset_clears_every_field_edited_by_the_wizard()
    {
        var state = new BuilderState
        {
            Formats = ["json"],
            ReportName = "leftover-report",
            SourceType = "postgres",
            SourceRef = "sales-db",
            ConnectionStringVariable = "SALES_DB",
            SqlQuery = "SELECT * FROM Sales",
            KeyColumn = "SaleId",
            SourceProperties = new List<PropertyRow> { new() { Key = "url", Value = "https://leftover.example.com" } },
            PageSize = 5000,
            TrackProgress = false,
            ColumnNames = "SaleId, Amount",
            DestinationType = "s3",
            DestinationPath = "s3://bucket/key",
            RetryMaxAttempts = 9,
            RetryBackoff = "Exponential",
            RetryBaseDelaySeconds = 4.5,
            RetryJitter = true,
            FailureStrategy = "skip-and-log",
            AbortOnConsecutiveFailures = true,
            AbortConsecutiveFailures = 7,
            AbortOnTotalFailures = true,
            AbortTotalFailures = 20,
            AbortOnFailureRate = true,
            AbortFailureRatePercent = 75,
            ScheduleCron = "0 6 * * 1",
            EngineAvailable = true,
            IsEditing = true,
            EditingOriginalName = "leftover-report",
        };

        state.Reset();

        state.Formats.ShouldBe(new HashSet<string> { "csv", "xlsx" });
        state.ReportName.ShouldBe("");
        state.SourceType.ShouldBe("sql");
        state.SourceRef.ShouldBe("");
        state.ConnectionStringVariable.ShouldBe("");
        state.SqlQuery.ShouldBe("");
        state.KeyColumn.ShouldBe("Id");
        state.SourceProperties.ShouldBeEmpty();
        state.PageSize.ShouldBe(1000);
        state.TrackProgress.ShouldBeTrue();
        state.ColumnNames.ShouldBe("Id");
        state.DestinationType.ShouldBe("");
        state.DestinationPath.ShouldBe("");
        state.RetryMaxAttempts.ShouldBe(1);
        state.RetryBackoff.ShouldBe("Constant");
        state.RetryBaseDelaySeconds.ShouldBe(1);
        state.RetryJitter.ShouldBeFalse();
        state.FailureStrategy.ShouldBe("abort");
        state.AbortOnConsecutiveFailures.ShouldBeFalse();
        state.AbortConsecutiveFailures.ShouldBe(3);
        state.AbortOnTotalFailures.ShouldBeFalse();
        state.AbortTotalFailures.ShouldBe(10);
        state.AbortOnFailureRate.ShouldBeFalse();
        state.AbortFailureRatePercent.ShouldBe(50);
        state.ScheduleCron.ShouldBe("");
        state.EngineAvailable.ShouldBeFalse();
        state.IsEditing.ShouldBeFalse();
        state.EditingOriginalName.ShouldBe("");
    }
}
