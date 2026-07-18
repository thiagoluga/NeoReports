using NeoReports.Abstractions;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Parquet.UnitTests;

/// <summary>
/// Tests the Parquet source's typed public surface end to end against real files written by
/// <c>Parquet.Net</c>'s own serializer (this repo has no Parquet <i>writer</i> of its own to
/// round-trip against, unlike CSV/XLSX — ADR D60). The read engine has no <c>InternalsVisibleTo</c>
/// (this repo has no such convention), so correctness is verified through
/// <c>Source.ParquetFile(...).As&lt;T&gt;()</c>.
/// </summary>
public sealed class ParquetSourceReadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nr-parquet-tests", Guid.NewGuid().ToString("N"));

    public ParquetSourceReadingTests() => Directory.CreateDirectory(_dir);

    private static ReportExecutionContext Exec() =>
        new("job", "sales", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

    private static async Task<List<T>> CollectAsync<T>(IStreamingSource<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source.ReadAsync(Exec(), CancellationToken.None))
            results.Add(item);
        return results;
    }

    private string Path2(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Reads_simple_rows_with_mixed_native_types()
    {
        var expected = new[]
        {
            new Sale { Id = 1, Customer = "C1", Amount = 10.5m, Date = new DateTime(2026, 1, 1), Active = true },
            new Sale { Id = 2, Customer = "C2", Amount = 20m, Date = new DateTime(2026, 1, 2), Active = false },
        };
        var path = await ParquetTestFile.WriteFileAsync(Path2("sales.parquet"), expected);

        List<Sale> rows = await CollectAsync(Source.ParquetFile(path).As<Sale>());

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(expected[0]);
        rows[1].ShouldBe(expected[1]);
    }

    [Fact]
    public async Task Streams_across_multiple_row_groups()
    {
        // RowGroupSize = 2 over 5 rows forces 3 row groups (2 + 2 + 1); the source must yield every
        // row across all of them in order — the core constant-memory, row-group-at-a-time guarantee.
        var expected = Enumerable.Range(1, 5)
            .Select(i => new CustomerNote { Id = i, Customer = $"C{i}" })
            .ToArray();
        var path = await ParquetTestFile.WriteFileAsync(Path2("many.parquet"), expected, rowGroupSize: 2);

        List<CustomerNote> rows = await CollectAsync(Source.ParquetFile(path).As<CustomerNote>());

        rows.Count.ShouldBe(5);
        rows.Select(r => r.Id).ShouldBe(new long[] { 1, 2, 3, 4, 5 });
        rows[4].Customer.ShouldBe("C5");
    }

    [Fact]
    public async Task A_typed_row_reads_only_its_declared_subset_of_file_columns()
    {
        // The file carries five columns; a POCO with only Id must still read, ignoring the rest.
        var path = await ParquetTestFile.WriteFileAsync(Path2("wide.parquet"), new[]
        {
            new Sale { Id = 7, Customer = "C7", Amount = 1m, Date = new DateTime(2026, 1, 1), Active = true },
        });

        List<IdOnly> rows = await CollectAsync(Source.ParquetFile(path).As<IdOnly>());

        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe(7L);
    }

    [Fact]
    public async Task Column_names_are_matched_to_properties_case_insensitively()
    {
        var path = await ParquetTestFile.WriteFileAsync(Path2("ci.parquet"), new[]
        {
            new Sale { Id = 3, Customer = "C3", Amount = 2m, Date = new DateTime(2026, 1, 1), Active = true },
        });

        // LowerNote's properties are lowercase; the file's columns are PascalCase.
        List<LowerNote> rows = await CollectAsync(Source.ParquetFile(path).As<LowerNote>());

        rows[0].id.ShouldBe(3L);
        rows[0].customer.ShouldBe("C3");
    }

    [Fact]
    public async Task An_init_only_record_is_a_valid_row_type()
    {
        var path = await ParquetTestFile.WriteFileAsync(Path2("init.parquet"), new[]
        {
            new Sale { Id = 9, Customer = "C9", Amount = 1m, Date = new DateTime(2026, 1, 1), Active = true },
        });

        List<InitSale> rows = await CollectAsync(Source.ParquetFile(path).As<InitSale>());

        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe(9L);
        rows[0].Customer.ShouldBe("C9");
    }

    [Fact]
    public async Task An_empty_file_yields_no_rows()
    {
        var path = await ParquetTestFile.WriteFileAsync(Path2("empty.parquet"), Array.Empty<CustomerNote>());

        List<CustomerNote> rows = await CollectAsync(Source.ParquetFile(path).As<CustomerNote>());

        rows.ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}

public sealed record LowerNote
{
    public long id { get; init; }
    public string customer { get; init; } = "";
}
