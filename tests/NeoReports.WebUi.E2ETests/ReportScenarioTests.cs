using System.IO.Compression;
using System.Text;
using Shouldly;
using Xunit;
using static NeoReports.WebUi.E2ETests.ReportApi;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// Report shapes run end to end against the live host, each asserting the <b>bytes that came out</b>
/// rather than only that the job reached Completed — a run can report success and still deliver a
/// truncated, empty or malformed file.
/// </summary>
[Collection(nameof(WebUiCollection))]
public class ReportScenarioTests
{
    private readonly WebUiFixture _fixture;

    public ReportScenarioTests(WebUiFixture fixture) => _fixture = fixture;

    private static string Name(string prefix) => $"e2e-{prefix}-{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>Every ColumnType the sample source synthesises and the writers branch on.</summary>
    private static readonly IReadOnlyList<Column> AllColumnTypes = new[]
    {
        new Column("Id", "Integer"), new Column("Customer", "String"), new Column("Amount", "Decimal"),
        new Column("Fee", "Money"), new Column("Created", "DateTime"), new Column("Day", "Date"),
        new Column("At", "Time"), new Column("Version", "Timestamp"), new Column("Ref", "Uuid"),
        new Column("Active", "Boolean"),
    };

    /// <summary>A sheet row, tolerating a namespace prefix and a self-closing tag; `[ />]` keeps it
    /// from also matching &lt;rowBreaks&gt;.</summary>
    private const string RowTag = @"<(?:\w+:)?row[ />]";

    private static string[] Lines(byte[] csv) =>
        Encoding.UTF8.GetString(csv).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public async Task Every_column_type_round_trips_into_the_csv()
    {
        using var api = new ReportApi(_fixture.App);
        string name = Name("types");
        await api.RegisterAsync(
            name,
            AllColumnTypes,
            new[] { "csv" },
            rows: 3);

        Job job = await api.RunToCompletionAsync(name);
        string[] lines = Lines(await api.DownloadAsync(job.Id));

        lines.Length.ShouldBe(4); // header + 3 rows
        lines[0].Split(',').ShouldBe(AllColumnTypes.Select(c => c.Name));

        // Every declared column produced a value — a type the writer mishandled would leave a gap.
        // (Each of these values is comma-free under the invariant culture the writer defaults to, so
        // splitting on ',' is exact rather than approximate.)
        string[] first = lines[1].Split(',');
        first.Length.ShouldBe(AllColumnTypes.Count);
        first.ShouldAllBe(v => v.Length > 0);
    }

    [Fact]
    public async Task A_report_larger_than_one_page_delivers_every_row()
    {
        using var api = new ReportApi(_fixture.App);
        string name = Name("paged");

        // 250 rows at 10 per page is 25 batches: this is the keyset/pagination loop doing real work,
        // and the row count is what a dropped or duplicated page would break.
        await api.RegisterAsync(name, new[] { new Column("Id", "Integer") }, new[] { "csv" }, rows: 250, pageSize: 10);

        Job job = await api.RunToCompletionAsync(name);
        string[] lines = Lines(await api.DownloadAsync(job.Id));

        lines.Length.ShouldBe(251); // header + 250 rows
        lines.Skip(1).Select(l => l.Split(',')[0]).ShouldBeUnique();
    }

    [Fact]
    public async Task A_report_with_no_rows_still_delivers_a_header_only_file()
    {
        using var api = new ReportApi(_fixture.App);
        string name = Name("empty");
        await api.RegisterAsync(
            name, new[] { new Column("Id", "Integer"), new Column("Customer", "String") }, new[] { "csv" }, rows: 0);

        Job job = await api.RunToCompletionAsync(name);
        string[] lines = Lines(await api.DownloadAsync(job.Id));

        // An empty result is a legitimate outcome, not a failure — the file must still be well-formed.
        lines.ShouldHaveSingleItem().ShouldBe("Id,Customer");
    }

    [Fact]
    public async Task An_xlsx_report_delivers_a_readable_workbook()
    {
        using var api = new ReportApi(_fixture.App);
        string name = Name("xlsx");
        await api.RegisterAsync(
            name, new[] { new Column("Id", "Integer"), new Column("Customer", "String") }, new[] { "xlsx" }, rows: 5);

        Job job = await api.RunToCompletionAsync(name);
        byte[] bytes = await api.DownloadAsync(job.Id);

        // An .xlsx is a zip package; opening it proves the streaming writer closed the archive
        // properly, which a length check alone would not.
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        archive.GetEntry("xl/workbook.xml").ShouldNotBeNull();

        // Count the rows: both the workbook part and the sheet part are written before any data
        // arrives, so asserting they merely exist would pass on a run that wrote no rows at all.
        ZipArchiveEntry sheet = archive.Entries.First(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal));
        using var reader = new StreamReader(sheet.Open());
        string xml = await reader.ReadToEndAsync();
        System.Text.RegularExpressions.Regex.Matches(xml, RowTag).Count.ShouldBe(6); // header + 5
    }

    [Fact]
    public async Task A_report_without_a_destination_still_produces_a_downloadable_artifact()
    {
        using var api = new ReportApi(_fixture.App);
        string name = Name("nodest");
        await api.RegisterAsync(
            name, new[] { new Column("Id", "Integer") }, new[] { "csv" }, rows: 5, withDestination: false);

        // The scenario is only meaningful if the report really has none — otherwise this silently
        // becomes a duplicate of the plain csv test.
        ReportApi.Report registered = (await api.ReportsAsync()).Single(r => r.Name == name);
        registered.Destinations.ShouldBeEmpty();

        Job job = await api.RunToCompletionAsync(name);

        // Uploading is optional; the artifact the API serves is produced either way.
        (await api.ArtifactsAsync(job.Id)).ShouldHaveSingleItem();
        Lines(await api.DownloadAsync(job.Id)).Length.ShouldBe(6);
    }

    [Fact]
    public async Task Each_format_of_a_multi_format_report_carries_the_same_rows()
    {
        using var api = new ReportApi(_fixture.App);
        string name = Name("both");
        await api.RegisterAsync(
            name, new[] { new Column("Id", "Integer") }, new[] { "csv", "xlsx" }, rows: 7);

        Job job = await api.RunToCompletionAsync(name);
        (await api.ArtifactsAsync(job.Id)).Count.ShouldBe(2);

        // A multi-output job downloads as a zip of its files — that bundle is what a user receives.
        using var bundle = new ZipArchive(new MemoryStream(await api.DownloadAsync(job.Id)), ZipArchiveMode.Read);
        bundle.Entries.Count.ShouldBe(2);

        // The two writers consume the same batches, so they must not disagree on how many rows there
        // were — a divergence here is the multi-output write path losing data on one branch.
        ZipArchiveEntry csvEntry = bundle.Entries.Single(e => e.FullName.EndsWith(".csv", StringComparison.Ordinal));
        using (var csvReader = new StreamReader(csvEntry.Open()))
        {
            string csv = await csvReader.ReadToEndAsync();
            csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(8); // header + 7
        }

        ZipArchiveEntry xlsxEntry = bundle.Entries.Single(e => e.FullName.EndsWith(".xlsx", StringComparison.Ordinal));
        using var xlsxBytes = new MemoryStream();
        await using (Stream entryStream = xlsxEntry.Open())
            await entryStream.CopyToAsync(xlsxBytes);
        xlsxBytes.Position = 0;
        using var workbook = new ZipArchive(xlsxBytes, ZipArchiveMode.Read);
        ZipArchiveEntry sheet = workbook.Entries.First(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal));
        using var sheetReader = new StreamReader(sheet.Open());
        string xml = await sheetReader.ReadToEndAsync();
        System.Text.RegularExpressions.Regex.Matches(xml, RowTag).Count.ShouldBe(8);
    }
}
