using NeoReports.Abstractions;

namespace NeoReports.Samples.WebUiLive;

/// <summary>
/// Self-contained <see cref="IConfigSourceProvider"/> (id "inmemory") so this sample needs no
/// external database, cloud account, or credentials to demonstrate the full dynamic-registration
/// flow (Epic D / ADR D33) — register a report from the Builder, validate it, run it, and download
/// a real file, all against synthetic in-memory rows. Values are generated from the requested
/// schema rather than a fixed shape, so any column names/count typed into the Builder work without
/// a mismatch.
/// </summary>
internal sealed class InMemorySalesSourceProvider : IConfigSourceProvider
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
