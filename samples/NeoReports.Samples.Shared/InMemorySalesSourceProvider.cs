using NeoReports.Abstractions;

namespace NeoReports.Samples.Shared;

/// <summary>
/// Self-contained <see cref="IConfigSourceProvider"/> (id "inmemory") so dynamic-config samples
/// need no external database, cloud account, or credentials to demonstrate the full config-driven
/// flow. Values are generated from the requested schema rather than a fixed shape, so any column
/// names/count/types declared in a report's config work without a mismatch (promoted from sample
/// 09's provider during Epic H — strictly more capable than the fixed-to-a-specific-schema version
/// sample 04 used to carry on its own).
/// </summary>
public sealed class InMemorySalesSourceProvider : IConfigSourceProvider
{
    public string Type => "inmemory";

    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        int rowCount = source.Properties is not null
            && source.Properties.TryGetValue("rows", out object? raw)
            && raw is long n
                ? (int)n
                : 25;

        var rows = new List<ReportRecord>(rowCount);
        for (var row = 1; row <= rowCount; row++)
        {
            var values = new object?[schema.Columns.Count];
            for (var i = 0; i < schema.Columns.Count; i++)
                values[i] = SampleValue(schema.Columns[i], row);

            rows.Add(new ReportRecord(schema, values));
        }

        return new InMemoryRecordSource(schema, rows);
    }

    private static object? SampleValue(ReportColumn column, int row) => column.Type switch
    {
        ColumnType.Integer or ColumnType.Timestamp => (long)row,
        ColumnType.Decimal or ColumnType.Money => row * 100.5m,
        ColumnType.Boolean => row % 2 == 0,
        ColumnType.Date => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(row - 1).Date,
        ColumnType.DateTime => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(row - 1),
        ColumnType.Time => TimeSpan.FromMinutes(row),
        ColumnType.Uuid => Guid.NewGuid(),
        _ => $"{column.Name} {row}",
    };

    /// <summary>Serves the pre-built records one page at a time, driven by the page number.</summary>
    private sealed class InMemoryRecordSource : IBatchSource<ReportRecord>
    {
        private readonly IReadOnlyList<ReportRecord> _rows;

        public InMemoryRecordSource(ReportSchema schema, IReadOnlyList<ReportRecord> rows)
        {
            Schema = schema;
            _rows = rows;
        }

        public ReportSchema Schema { get; }

        public Task<BatchResult<ReportRecord>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
        {
            int skip = (context.PageNumber - 1) * context.PageSize;
            ReportRecord[] page = _rows.Skip(skip).Take(context.PageSize).ToArray();
            bool hasMore = skip + page.Length < _rows.Count;
            string? nextCursor = hasMore ? context.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult(new BatchResult<ReportRecord>(page, nextCursor, hasMore));
        }
    }
}
