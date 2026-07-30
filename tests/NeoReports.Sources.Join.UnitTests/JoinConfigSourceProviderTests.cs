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
    private static readonly string[] ByNameOrder = { "alice", "carol" };

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

    [Fact]
    public async Task Kind_defaults_to_inner_when_omitted()
    {
        List<ReportRecord> rows = await RunAsync(kind: null);

        rows.Select(r => (long)r["id"]!).ShouldBe(Matched); // no 'kind' → inner drops customer 2
    }

    [Fact]
    public async Task Join_keys_on_a_text_column()
    {
        // Same 3-column schema, but the key column holds strings — exercises the non-numeric key path.
        var source = MergeJoinConfig("name", "inner", "byName-left", "byName-right");
        IBatchSource<ReportRecord> joined = new JoinConfigSourceProvider().Create(source, Schema, BuildServices());

        List<ReportRecord> rows = await DrainAsync(joined);

        rows.Select(r => (string)r["name"]!).ShouldBe(ByNameOrder);
        ((long)rows[0]["id"]!).ShouldBe(10L); // left id + right item merged on the text key
        rows[0]["item"].ShouldBe("x");
    }

    [Fact]
    public async Task Source_is_rerunnable()
    {
        var source = MergeJoinConfig("id", "inner", "customers", "orders");
        IBatchSource<ReportRecord> joined = new JoinConfigSourceProvider().Create(source, Schema, BuildServices());

        List<ReportRecord> first = await DrainAsync(joined);
        List<ReportRecord> second = await DrainAsync(joined); // second drain resets the stream

        second.Select(r => (long)r["id"]!).ShouldBe(first.Select(r => (long)r["id"]!));
    }

    [Fact]
    public void Missing_key_property_throws()
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = Child("customers"),
            ["right"] = Child("orders"),
        };
        var source = new SourceConfig("merge-join", properties);

        Should.Throw<ConfigurationException>(() => new JoinConfigSourceProvider().Create(source, Schema, BuildServices()));
    }

    [Fact]
    public void Key_column_absent_from_schema_throws()
    {
        var source = MergeJoinConfig("nope", "inner", "customers", "orders");

        Should.Throw<ConfigurationException>(() => new JoinConfigSourceProvider().Create(source, Schema, BuildServices()));
    }

    [Fact]
    public void Unknown_child_source_type_throws()
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = "id",
            ["left"] = JsonDocument.Parse("{\"type\":\"ghost\"}").RootElement.Clone(),
            ["right"] = Child("orders"),
        };
        var source = new SourceConfig("merge-join", properties);

        Should.Throw<ConfigurationException>(() => new JoinConfigSourceProvider().Create(source, Schema, BuildServices()));
    }

    [Fact]
    public void Non_object_child_property_throws()
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = "id",
            ["left"] = "not-an-object",
            ["right"] = Child("orders"),
        };
        var source = new SourceConfig("merge-join", properties);

        Should.Throw<ConfigurationException>(() => new JoinConfigSourceProvider().Create(source, Schema, BuildServices()));
    }

    [Fact]
    public async Task Child_properties_of_every_json_kind_are_read()
    {
        // The child carries properties of each JSON kind (string, number, bool, null, nested object);
        // the inline provider ignores the extras but they must all parse without error.
        JsonElement richLeft = JsonDocument.Parse(
            "{\"type\":\"inline\",\"properties\":{\"tag\":\"customers\",\"n\":5,\"flag\":true,\"off\":false,\"x\":null,\"nested\":{\"a\":1}}}")
            .RootElement.Clone();
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = "id",
            ["left"] = richLeft,
            ["right"] = Child("orders"),
        };
        var source = new SourceConfig("merge-join", properties);

        IBatchSource<ReportRecord> joined = new JoinConfigSourceProvider().Create(source, Schema, BuildServices());
        List<ReportRecord> rows = await DrainAsync(joined);

        rows.Select(r => (long)r["id"]!).ShouldBe(Matched);
    }

    [Fact]
    public void Bad_kind_value_throws()
    {
        var source = MergeJoinConfig("id", "outer", "customers", "orders");

        Should.Throw<ConfigurationException>(() => new JoinConfigSourceProvider().Create(source, Schema, BuildServices()));
    }

    private static async Task<List<ReportRecord>> RunAsync(string? kind)
    {
        var source = MergeJoinConfig("id", kind, "customers", "orders");
        IBatchSource<ReportRecord> joined = new JoinConfigSourceProvider().Create(source, Schema, BuildServices());
        return await DrainAsync(joined);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigSourceProvider, InlineProvider>();
        return services.BuildServiceProvider();
    }

    // Mirrors what JsonReportConfigParser yields: scalars as CLR primitives, nested objects as JsonElement.
    private static SourceConfig MergeJoinConfig(string key, string? kind, string leftTag, string rightTag)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = key,
            ["left"] = Child(leftTag),
            ["right"] = Child(rightTag),
        };
        if (kind is not null)
            properties["kind"] = kind;
        return new SourceConfig("merge-join", properties);
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
            if (pageNumber > 1000)
                throw new Xunit.Sdk.XunitException("drain did not terminate within 1000 pages - likely a non-advancing cursor.");
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
                // Keyed on the text "name" column (ordered by name). id/item are the payload.
                "byName-left" => new[]
                {
                    new object?[] { 10L, "alice", null },
                    new object?[] { 30L, "carol", null },
                },
                "byName-right" => new[]
                {
                    new object?[] { null, "alice", "x" },
                    new object?[] { null, "carol", "z" },
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
