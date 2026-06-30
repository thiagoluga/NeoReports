using System.Globalization;
using NeoReports.Abstractions;

namespace NeoReports.Samples.DynamicConfigCsv;

/// <summary>
/// Stand-in <see cref="IConfigSourceProvider"/> (id "inmemory") used until the SQL config source
/// lands in A3. It reads the row count from the source <c>properties</c> and emits positional
/// <see cref="ReportRecord"/> rows aligned to the report schema — exactly what a real provider does,
/// minus the database.
/// </summary>
internal sealed class InMemorySalesSourceProvider : IConfigSourceProvider
{
    public string Type => "inmemory";

    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        var count = source.Properties is not null
            && source.Properties.TryGetValue("rows", out var raw)
            && raw is long n
                ? (int)n
                : 5;

        var rows = new List<ReportRecord>(count);
        for (var i = 1; i <= count; i++)
        {
            rows.Add(new ReportRecord(schema, new object?[]
            {
                (long)i,
                $"Customer {i}",
                i * 100.5m,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i - 1),
            }));
        }

        return new InMemoryRecordSource(schema, rows);
    }

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
            var skip = (context.PageNumber - 1) * context.PageSize;
            var page = _rows.Skip(skip).Take(context.PageSize).ToArray();
            var hasMore = skip + page.Length < _rows.Count;
            var nextCursor = hasMore
                ? context.PageNumber.ToString(CultureInfo.InvariantCulture)
                : null;
            return Task.FromResult(new BatchResult<ReportRecord>(page, nextCursor, hasMore));
        }
    }
}
