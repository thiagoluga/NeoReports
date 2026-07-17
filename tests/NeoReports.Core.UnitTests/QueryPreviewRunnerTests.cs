using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Preview;
using NeoReports.Core.QueryBuilder;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// ADR D49 (K6): <see cref="QueryPreviewRunner"/> runs one bounded page of a generated query against
/// a registered source's own <c>IConfigSourceProvider</c>. These tests cover the config it composes
/// (generated SQL + a valid key + the row cap as page size), the run-time parameter merge, the row
/// clamp, and the truncation signal — without any real database.
/// </summary>
public class QueryPreviewRunnerTests
{
    private static readonly ReportSchema Schema = new(new[]
    {
        new ReportColumn("Id", ColumnType.Integer),
        new ReportColumn("Name", ColumnType.String),
    });

    private static ReportExecutionContext Execution(IReadOnlyDictionary<string, object?>? parameters = null) =>
        new("job-1", "adhoc", parameters, NullLogger.Instance, CancellationToken.None);

    private static ServiceProvider Services(CapturingProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigSourceProvider>(provider);
        return services.BuildServiceProvider();
    }

    private static GeneratedReportSql Query(
        string sql, IReadOnlyDictionary<string, object?>? parameters = null, string keyColumnName = "Id") =>
        new(sql, parameters ?? new Dictionary<string, object?>(), Schema, keyColumnName);

    [Fact]
    public async Task Composes_the_source_config_with_the_generated_sql_key_and_page_size()
    {
        var provider = new CapturingProvider(rowCount: 2, type: "fake");
        using ServiceProvider services = Services(provider);

        await QueryPreviewRunner.PreviewAsync(
            "fake", new Dictionary<string, object?> { ["connectionString"] = "cs" },
            Query("SELECT * ... ORDER BY t0.\"Id\"", keyColumnName: "Id"),
            requestedRows: 25, Execution(), services, CancellationToken.None);

        provider.LastConfig.ShouldNotBeNull();
        provider.LastConfig!.Type.ShouldBe("fake");
        provider.LastConfig.Properties!["connectionString"].ShouldBe("cs");
        provider.LastConfig.Properties["sql"].ShouldBe("SELECT * ... ORDER BY t0.\"Id\"");
        // The key is always the generator's own KeyColumnName (ADR D49/K6c) — always a real result
        // column, whether or not it was one the user picked as an output column.
        provider.LastConfig.Properties["key"].ShouldBe("Id");
        provider.LastConfig.Properties["pageSize"].ShouldBe(25);
    }

    [Fact]
    public async Task Uses_the_generators_key_column_name_even_when_not_the_first_schema_column()
    {
        var provider = new CapturingProvider(rowCount: 1, type: "fake");
        using ServiceProvider services = Services(provider);

        await QueryPreviewRunner.PreviewAsync(
            "fake", null, Query("SELECT 1", keyColumnName: "__neoreports_key"),
            requestedRows: 10, Execution(), services, CancellationToken.None);

        provider.LastConfig!.Properties!["key"].ShouldBe("__neoreports_key");
    }

    [Fact]
    public async Task Merges_the_generated_parameters_into_the_execution()
    {
        var provider = new CapturingProvider(rowCount: 1, type: "fake");
        using ServiceProvider services = Services(provider);

        await QueryPreviewRunner.PreviewAsync(
            "fake", null, Query("SELECT 1", new Dictionary<string, object?> { ["qbfilter0"] = "Porto" }),
            requestedRows: 10,
            Execution(new Dictionary<string, object?> { ["runParam"] = 7 }), services, CancellationToken.None);

        provider.LastParameters.TryGetValue("qbfilter0", out object? filterValue).ShouldBeTrue();
        filterValue.ShouldBe("Porto");
        provider.LastParameters.TryGetValue("runParam", out object? runValue).ShouldBeTrue();
        runValue.ShouldBe(7);
    }

    [Fact]
    public async Task Clamps_the_requested_rows_to_the_max_and_reports_truncation_when_the_page_fills()
    {
        var provider = new CapturingProvider(rowCount: QueryPreviewRunner.MaxRows + 50, type: "fake");
        using ServiceProvider services = Services(provider);

        QueryPreviewResult result = await QueryPreviewRunner.PreviewAsync(
            "fake", null, Query("SELECT 1"),
            requestedRows: 10_000, Execution(), services, CancellationToken.None);

        // Requested beyond the ceiling → clamped to MaxRows (both the page-size property and the cap).
        provider.LastConfig!.Properties!["pageSize"].ShouldBe(QueryPreviewRunner.MaxRows);
        result.Rows.Count.ShouldBe(QueryPreviewRunner.MaxRows);
        result.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Reports_no_truncation_when_the_page_is_not_full()
    {
        var provider = new CapturingProvider(rowCount: 3, type: "fake");
        using ServiceProvider services = Services(provider);

        QueryPreviewResult result = await QueryPreviewRunner.PreviewAsync(
            "fake", null, Query("SELECT 1"),
            requestedRows: 50, Execution(), services, CancellationToken.None);

        result.Rows.Count.ShouldBe(3);
        result.Truncated.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Throws_when_the_generators_key_column_name_is_empty(string keyColumnName)
    {
        // IQuerySqlGenerator is a public extensibility seam (ADR D49) — a non-conforming
        // implementation must fail fast here, not silently write a blank "key" property that
        // surfaces as a confusing error deep inside the source provider.
        using ServiceProvider services = Services(new CapturingProvider(rowCount: 0, type: "fake"));

        await Should.ThrowAsync<ArgumentException>(() => QueryPreviewRunner.PreviewAsync(
            "fake", null, Query("SELECT 1", keyColumnName: keyColumnName),
            requestedRows: 10, Execution(), services, CancellationToken.None));
    }

    [Fact]
    public async Task Throws_when_no_provider_is_registered_for_the_source_type()
    {
        using ServiceProvider services = Services(new CapturingProvider(rowCount: 0, type: "fake"));

        await Should.ThrowAsync<ConfigurationException>(() => QueryPreviewRunner.PreviewAsync(
            "mongodb", null, Query("SELECT 1"),
            requestedRows: 10, Execution(), services, CancellationToken.None));
    }

    /// <summary>Captures the composed <see cref="SourceConfig"/> and the run-time parameters, and serves a fixed row count in one page.</summary>
    private sealed class CapturingProvider : IConfigSourceProvider
    {
        private readonly int _rowCount;

        public CapturingProvider(int rowCount, string type)
        {
            _rowCount = rowCount;
            Type = type;
        }

        public string Type { get; }
        public SourceConfig? LastConfig { get; private set; }
        public IReadOnlyDictionary<string, object?> LastParameters { get; private set; } =
            new Dictionary<string, object?>();

        public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
        {
            LastConfig = source;
            ReportRecord[] rows = Enumerable.Range(0, _rowCount)
                .Select(i => new ReportRecord(schema, new object?[] { (long)i, $"row-{i}" }))
                .ToArray();
            return new CapturingSource(rows, p => LastParameters = p);
        }

        private sealed class CapturingSource : IBatchSource<ReportRecord>
        {
            private readonly IReadOnlyList<ReportRecord> _rows;
            private readonly Action<IReadOnlyDictionary<string, object?>> _captureParameters;

            public CapturingSource(IReadOnlyList<ReportRecord> rows, Action<IReadOnlyDictionary<string, object?>> captureParameters)
            {
                _rows = rows;
                _captureParameters = captureParameters;
            }

            public ReportSchema Schema => throw new NotSupportedException();

            public Task<BatchResult<ReportRecord>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
            {
                _captureParameters(context.Execution.Parameters);
                return Task.FromResult(new BatchResult<ReportRecord>(_rows, null, false));
            }
        }
    }
}
