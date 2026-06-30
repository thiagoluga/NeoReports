using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Formats.Csv.Format;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Epic A / A1: the dynamic path runs on the <b>same</b> pipeline as the typed path. The row type is
/// a positional <see cref="ReportRecord"/>; columns are declared with <c>Positional(...)</c> and the
/// getters read by index. The proof is byte-identical CSV output for the same data, plus the dynamic
/// row supporting filters and name/position access (prep for the JsonLogic filter in A4).
/// </summary>
public class DynamicPathTests
{
    private static readonly Sale[] Data =
    {
        new(1, "Acme", 1234.50m, new DateTime(2026, 1, 2)),
        new(2, "Globex", 0.99m, new DateTime(2026, 1, 3)),
        new(3, "Initech", 42m, new DateTime(2026, 1, 4)),
    };

    // The dynamic schema mirrors the typed columns exactly (name, type, header, format, culture),
    // so identical projected values must yield identical writer bytes.
    private static readonly ReportSchema DynamicSchema = new(new ReportColumn[]
    {
        new("Id", ColumnType.Integer, Nullable: false, DisplayName: "Sale ID"),
        new("Customer", ColumnType.String, DisplayName: "Customer"),
        new("Amount", ColumnType.Decimal, Nullable: false, DisplayName: "Amount", Format: "C2", Culture: "pt-BR"),
        new("Date", ColumnType.DateTime, Nullable: false, DisplayName: "Sale Date", Format: "yyyy-MM-dd"),
    });

    private static ReportRecord ToRecord(Sale s) =>
        new(DynamicSchema, new object?[] { s.Id, s.Customer, s.Amount, s.Date });

    private static ReportExecutionContext Exec() =>
        new(Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private static Task<ReportRunResult> Run(CompiledReport report) =>
        ReportRunner.ExecuteAsync(report, Exec(), new EmptyServiceProvider(), CancellationToken.None);

    private static CompiledReport TypedReport(CapturingDestinationFactory dest) =>
        new ReportBuilder<Sale>("typed")
            .From(new FakeBatchSource<Sale>(new[] { Data }))
            .Column(v => v.Id, "Sale ID")
            .Column(v => v.Customer, "Customer")
            .Column(v => v.Amount, "Amount", format: "C2", culture: "pt-BR")
            .Column(v => v.Date, "Sale Date", format: "yyyy-MM-dd")
            .To(Csv())
            .UploadTo(new DestinationSpec(dest))
            .Build();

    private static CompiledReport DynamicReport(CapturingDestinationFactory dest, Func<ReportRecord, bool>? filter = null)
    {
        var records = Data.Select(ToRecord).ToArray();
        var builder = new ReportBuilder<ReportRecord>("dynamic")
            .From(new FakeBatchSource<ReportRecord>(new[] { records }))
            .Columns(
                Positional(0, "Id", ColumnType.Integer, nullable: false, displayName: "Sale ID"),
                Positional(1, "Customer", ColumnType.String),
                Positional(2, "Amount", ColumnType.Decimal, nullable: false, displayName: "Amount", format: "C2", culture: "pt-BR"),
                Positional(3, "Date", ColumnType.DateTime, nullable: false, displayName: "Sale Date", format: "yyyy-MM-dd"))
            .To(Csv())
            .UploadTo(new DestinationSpec(dest));

        if (filter is not null)
            builder.Filter(filter);

        return builder.Build();
    }

    [Fact]
    public async Task Dynamic_path_produces_byte_identical_csv_to_the_typed_path()
    {
        var typedDest = new CapturingDestinationFactory();
        var dynamicDest = new CapturingDestinationFactory();

        var typedResult = await Run(TypedReport(typedDest));
        var dynamicResult = await Run(DynamicReport(dynamicDest));

        typedResult.Status.ShouldBe(ReportRunStatus.Completed);
        dynamicResult.Status.ShouldBe(ReportRunStatus.Completed);
        dynamicResult.Stats.RecordsRead.ShouldBe(Data.Length);
        dynamicResult.Stats.RecordsWritten.ShouldBe(Data.Length);

        var typedBytes = typedDest.LastDestination!.Files["typed.csv"];
        var dynamicBytes = dynamicDest.LastDestination!.Files["dynamic.csv"];

        // Same columns + same values through the same writer ⇒ identical bytes.
        dynamicBytes.ShouldBe(typedBytes);

        var text = Encoding.UTF8.GetString(dynamicBytes);
        text.ShouldContain("Sale ID,Customer,Amount,Sale Date");
        text.ShouldContain("Initech");
    }

    [Fact]
    public async Task Dynamic_filter_reads_the_record_by_name_and_excludes_rows()
    {
        var dest = new CapturingDestinationFactory();

        // Name access on the positional record (the shape the JsonLogic filter will target in A4).
        var report = DynamicReport(dest, filter: r => (long)r["Id"]! % 2 == 1);
        var result = await Run(report);

        result.Stats.RecordsRead.ShouldBe(Data.Length);
        result.Stats.RecordsWritten.ShouldBe(2); // Ids 1 and 3

        var lines = Encoding.UTF8.GetString(dest.LastDestination!.Files["dynamic.csv"])
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(3); // header + 2 rows
        lines[1].ShouldContain("Acme");
        lines[2].ShouldContain("Initech");
    }

    [Fact]
    public void ReportRecord_exposes_values_by_position_and_name()
    {
        var record = ToRecord(Data[0]);

        record.Count.ShouldBe(4);
        record[0].ShouldBe(1L);
        record["Customer"].ShouldBe("Acme");
        record.Schema.ShouldBeSameAs(DynamicSchema);

        record.TryGet("Amount", out var amount).ShouldBeTrue();
        amount.ShouldBe(1234.50m);
        record.TryGet("Missing", out var missing).ShouldBeFalse();
        missing.ShouldBeNull();
    }

    [Fact]
    public void ReportRecord_rejects_a_value_count_that_does_not_match_the_schema()
    {
        Should.Throw<ArgumentException>(() => new ReportRecord(DynamicSchema, new object?[] { 1L, "only-two" }));
        Should.Throw<KeyNotFoundException>(() => _ = ToRecord(Data[0])["Nope"]);
    }
}
