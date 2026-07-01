using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Sources.Join.Pro;
using Shouldly;
using Xunit;

namespace NeoReports.Sources.Join.UnitTests;

/// <summary>
/// B2.4: the config-driven merge-join source (<c>type: "merge-join"</c>) composes two nested sources
/// against the shared report schema, joining on one column. Both sides materialize the same positional
/// <see cref="ReportRecord"/> (each filling the columns it owns, the rest null); the join overlays the
/// right side's non-null columns onto the matching left row. A fake "inline" child provider stands in
/// for real sources so the test needs no database.
/// </summary>
public class JoinConfigSourceProviderTests
{
    private static readonly long[] Matched = { 1, 3 };
    private static readonly long[] All = { 1, 2, 3 };

    // Combined schema: customers own id+name, orders own item; the join key is "id".
    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("id", ColumnType.Integer),
        new ReportColumn("name", ColumnType.String),
        new ReportColumn("item", ColumnType.String),
    });

    [Fact]
    public async Task Inner_join_merges_matched_rows_and_drops_unmatched_left()
    {
        List<ReportRecord> rows = await RunAsync("inner");

        rows.Select(r => (long)r["id"]!).ShouldBe(Matched); // customer 2 has no order → dropped
        Row(rows, 1).ShouldBe(("A", "x"));
        Row(rows, 3).ShouldBe(("C", "z"));
    }

    [Fact]
    public async Task Left_outer_join_keeps_unmatched_left_rows_with_null_right_columns()
    {
        List<ReportRecord> rows = await RunAsync("leftOuter");

        rows.Select(r => (long)r["id"]!).ShouldBe(All);
        Row(rows, 2).ShouldBe(("B", null)); // unmatched → left kept, right column null
    }

    private static async Task<List<ReportRecord>> RunAsync(string kind)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigSourceProvider, InlineProvider>();
        using ServiceProvider provider = services.BuildServiceProvider();

        // Mirrors what JsonReportConfigParser yields: scalars as CLR primitives, nested objects as JsonElement.
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = "id",
            ["kind"] = kind,
            ["left"] = Child("customers"),
            ["right"] = Child("orders"),
        };
        var source = new SourceConfig("merge-join", properties);

        IBatchSource<ReportRecord> joined = new JoinConfigSourceProvider().Create(source, Schema, provider);
        return await DrainAsync(joined);
    }

    private static JsonElement Child(string tag) =>
        JsonDocument.Parse("{\"type\":\"inline\",\"properties\":{\"tag\":\"" + tag + "\"}}").RootElement.Clone();

    private static (string? Name, string? Item) Row(IEnumerable<ReportRecord> rows, long id)
    {
        ReportRecord record = rows.Single(r => (long)r["id"]! == id);
        return ((string?)record["name"], (string?)record["item"]);
    }

    private static async Task<List<ReportRecord>> DrainAsync(IBatchSource<ReportRecord> source)
    {
        var exec = new ReportExecutionContext("job", "r", null, NullLogger.Instance, CancellationToken.None);
        var rows = new List<ReportRecord>();
        string? cursor = null;
        var pageNumber = 0;
        while (true)
        {
            pageNumber++;
            BatchResult<ReportRecord> result = await source
                .ReadBatchAsync(new BatchContext(exec, pageSize: 2, cursor, pageNumber), CancellationToken.None);
            rows.AddRange(result.Records);
            if (!result.HasMore)
                break;
            cursor = result.NextCursor;
        }

        return rows;
    }

    /// <summary>Fake child source: returns preset rows (ordered by id) for the requested "tag".</summary>
    private sealed class InlineProvider : IConfigSourceProvider
    {
        public string Type => "inline";

        public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
        {
            var tag = (string)source.Properties!["tag"]!;
            object?[][] raw = tag switch
            {
                // id, name, item — customers fill id+name; orders fill id+item. Ordered by id.
                "customers" => new[]
                {
                    new object?[] { 1L, "A", null },
                    new object?[] { 2L, "B", null },
                    new object?[] { 3L, "C", null },
                },
                "orders" => new[]
                {
                    new object?[] { 1L, null, "x" },
                    new object?[] { 3L, null, "z" }, // no order for customer 2
                },
                _ => throw new InvalidOperationException($"Unknown tag '{tag}'."),
            };

            var records = raw.Select(v => new ReportRecord(schema, v)).ToList();
            return new ListBatchSource(schema, records);
        }
    }

    /// <summary>Pages a fixed list of records in chunks of two (cursor = next index).</summary>
    private sealed class ListBatchSource : IBatchSource<ReportRecord>
    {
        private const int PageSize = 2;
        private readonly IReadOnlyList<ReportRecord> _rows;

        public ListBatchSource(ReportSchema schema, IReadOnlyList<ReportRecord> rows)
        {
            Schema = schema;
            _rows = rows;
        }

        public ReportSchema Schema { get; }

        public Task<BatchResult<ReportRecord>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            var start = context.Cursor is null ? 0 : int.Parse(context.Cursor, CultureInfo.InvariantCulture);
            if (start >= _rows.Count)
                return Task.FromResult(new BatchResult<ReportRecord>(Array.Empty<ReportRecord>(), null, false));

            var take = Math.Min(PageSize, _rows.Count - start);
            var page = _rows.Skip(start).Take(take).ToArray();
            var end = start + take;
            var hasMore = end < _rows.Count;
            var next = hasMore ? end.ToString(CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new BatchResult<ReportRecord>(page, next, hasMore));
        }
    }
}
