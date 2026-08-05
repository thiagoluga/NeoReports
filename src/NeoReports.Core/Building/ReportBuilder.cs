using System.Linq.Expressions;
using NeoReports.Abstractions;
using NeoReports.Core.Resilience;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Scheduling;
using NeoReports.Core.Sections;
using NeoReports.Core.SourceRegistry;
using NeoReports.Core.Sources;

namespace NeoReports.Core.Building;

/// <summary>
/// Fluent, strongly typed builder for a single report. The pipeline is generic over
/// <typeparamref name="TRow"/>; mapping from a different source type is expressed through the
/// <c>From(source, map)</c> overloads so the builder stays single-generic and composes with the
/// <c>AddReport&lt;TRow&gt;("name", b =&gt; ...)</c> registration pattern.
/// </summary>
/// <typeparam name="TRow">The report's row type (the registered POCO).</typeparam>
public sealed class ReportBuilder<TRow>
{
    private readonly string _name;
    private readonly List<Func<TRow, bool>> _filters = new();
    private readonly List<ColumnDefinition<TRow>> _columns = new();
    private readonly List<OutputEntry> _outputs = new();
    private readonly List<SectionedEntry> _sectioned = [];
    private readonly List<DestinationSpec> _destinations = new();
    private readonly RetryOptions _retry = new();
    private readonly FailureStrategyBuilder _failure = new();
    private TimeSpan? _deadline;

    private IBatchSource<TRow>? _batchSource;
    private IStreamingSource<TRow>? _streamingSource;
    private int _pageSize = 1000;
    private ScheduleConfig? _schedule;
    private string? _sourceRef;
    private bool _trackProgress = true;
    private ISourceRowCounter? _rowCounter;

    /// <summary>
    /// True when <see cref="From(IBatchSource{TRow})"/> was given a named by-name source
    /// (<see cref="INamedSourceResolver"/>, e.g. <c>Source.SqlNamed</c>) — checked at registration
    /// time by <c>AddReport</c> so a missing source registry fails fast (ADR D42).
    /// </summary>
    internal bool RequiresSourceRegistry { get; private set; }

    /// <summary>Creates a builder for a report registered under <paramref name="name"/>.</summary>
    /// <param name="name">Unique report name.</param>
    public ReportBuilder(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Report name must be provided.", nameof(name));
        _name = name;
    }

    /// <summary>Reads from a batch (keyset) source.</summary>
    /// <param name="source">The batch source.</param>
    public ReportBuilder<TRow> From(IBatchSource<TRow> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _batchSource = source;
        _streamingSource = null;
        _rowCounter = source as ISourceRowCounter;
        if (source is INamedSourceResolver named)
        {
            RequiresSourceRegistry = true;
            _sourceRef = named.SourceName;
        }
        return this;
    }

    /// <summary>Reads from a batch source whose records are mapped to the report row type.</summary>
    /// <typeparam name="TSource">The source record type.</typeparam>
    /// <param name="source">The batch source.</param>
    /// <param name="map">Projection from a source record to the report row.</param>
    public ReportBuilder<TRow> From<TSource>(IBatchSource<TSource> source, Func<TSource, TRow> map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        _batchSource = new MappingBatchSource<TSource, TRow>(source, map);
        _streamingSource = null;
        _rowCounter = _batchSource as ISourceRowCounter;
        return this;
    }

    /// <summary>Reads from a streaming source (sliced into batches by the pipeline).</summary>
    /// <param name="source">The streaming source.</param>
    public ReportBuilder<TRow> From(IStreamingSource<TRow> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _streamingSource = source;
        _batchSource = null;
        _rowCounter = source as ISourceRowCounter;
        return this;
    }

    /// <summary>Reads from a streaming source whose records are mapped to the report row type.</summary>
    /// <typeparam name="TSource">The source record type.</typeparam>
    /// <param name="source">The streaming source.</param>
    /// <param name="map">Projection from a source record to the report row.</param>
    public ReportBuilder<TRow> From<TSource>(IStreamingSource<TSource> source, Func<TSource, TRow> map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        _streamingSource = new MappingStreamingSource<TSource, TRow>(source, map);
        _batchSource = null;
        _rowCounter = _streamingSource as ISourceRowCounter;
        return this;
    }

    /// <summary>Sets the page size used when reading the source.</summary>
    /// <param name="pageSize">Records per page (at least 1).</param>
    public ReportBuilder<TRow> WithPageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
        return this;
    }

    /// <summary>
    /// Enables or disables progress tracking (ADR D47). When enabled (the default) and the source
    /// can count itself (<c>ISourceRowCounter</c>), the engine counts the source's total rows once
    /// before the run and reports a real completion percentage. When disabled, or when the source
    /// cannot count, progress is indeterminate (counters remain exact either way).
    /// </summary>
    /// <param name="enabled">Whether to count the source before running. Default true.</param>
    public ReportBuilder<TRow> TrackProgress(bool enabled = true)
    {
        _trackProgress = enabled;
        return this;
    }

    /// <summary>Adds a typed row filter; only rows passing every filter are written.</summary>
    /// <param name="predicate">A predicate over the report row.</param>
    public ReportBuilder<TRow> Filter(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _filters.Add(predicate);
        return this;
    }

    /// <summary>Declares the output columns, in order.</summary>
    /// <param name="columns">Column definitions (see <see cref="ReportColumns.Col{T, TProp}"/>).</param>
    public ReportBuilder<TRow> Columns(params ColumnDefinition<TRow>[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns.AddRange(columns);
        return this;
    }

    /// <summary>
    /// Declares a single output column from a member selector. The member type drives the inferred
    /// <see cref="ColumnType"/>; <typeparamref name="TProp"/> is inferred from the selector.
    /// </summary>
    /// <typeparam name="TProp">The selected member type.</typeparam>
    /// <param name="selector">A member access expression, e.g. <c>v =&gt; v.Total</c>.</param>
    /// <param name="displayName">Optional header label; defaults to the member name.</param>
    /// <param name="format">Optional .NET format string for rendering.</param>
    /// <param name="culture">Optional culture name (e.g. "pt-BR") for rendering.</param>
    public ReportBuilder<TRow> Column<TProp>(
        Expression<Func<TRow, TProp>> selector,
        string? displayName = null,
        string? format = null,
        string? culture = null)
    {
        _columns.Add(ReportColumns.Col(selector, displayName, format, culture));
        return this;
    }

    /// <summary>Adds an output format. It uses the report's filters and columns.</summary>
    /// <param name="output">The output specification (e.g. from a format package).</param>
    public ReportBuilder<TRow> To(OutputSpec output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _outputs.Add(new OutputEntry(output, null));
        return this;
    }

    /// <summary>
    /// Adds an output format with its own "view": its own filters and/or columns over the same
    /// source. Several such outputs are all produced from a single read (e.g. an "approved" file and
    /// a "rejected" file with different columns).
    /// </summary>
    /// <param name="output">The output specification (e.g. from a format package).</param>
    /// <param name="configureView">Configures this output's filters and columns.</param>
    public ReportBuilder<TRow> To(OutputSpec output, Action<OutputView<TRow>> configureView)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(configureView);
        var view = new OutputView<TRow>();
        configureView(view);
        _outputs.Add(new OutputEntry(output, view));
        return this;
    }

    /// <summary>
    /// Adds a sectioned output: a single file with several named sections (e.g. an XLSX workbook with
    /// one worksheet per section), each with its own filters and/or columns, all produced from one
    /// source read.
    /// </summary>
    /// <param name="output">The sectioned output specification (from a format package).</param>
    /// <param name="configureSections">Declares the sections (name + view).</param>
    /// <exception cref="ConfigurationException">Thrown when no sections are declared.</exception>
    public ReportBuilder<TRow> ToSections(SectionedOutputSpec output, Action<SectionBuilder<TRow>> configureSections)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(configureSections);

        var sections = new SectionBuilder<TRow>();
        configureSections(sections);
        if (sections.Sections.Count == 0)
            throw new ConfigurationException($"Report '{_name}' has a sectioned output with no sections. Call Section(...).");

        _sectioned.Add(new SectionedEntry(output, sections.Sections));
        return this;
    }

    /// <summary>Adds a destination the finished files are uploaded to.</summary>
    /// <param name="destination">The destination specification (e.g. from a destination package).</param>
    public ReportBuilder<TRow> UploadTo(DestinationSpec destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _destinations.Add(destination);
        return this;
    }

    /// <summary>
    /// Enables retries with a sensible production default — <b>3 attempts, exponential backoff from
    /// 1s, with jitter</b>. Retries are <b>off by default</b> (a report with no <c>Retry(...)</c> call
    /// makes a single attempt per batch), so a transient source blip aborts the report under the
    /// default failure strategy; this one-call overload turns on resilience without spelling out the
    /// knobs. Use <see cref="Retry(Action{RetryOptions})"/> to tune them.
    /// </summary>
    public ReportBuilder<TRow> Retry() =>
        Retry(r => r.MaxAttempts(3).Exponential(TimeSpan.FromSeconds(1)).WithJitter());

    /// <summary>
    /// Configures retry behavior for batch reads. Retries are <b>off by default</b> (one attempt per
    /// batch) — call this (or the parameterless <see cref="Retry()"/>) to enable them; for a
    /// production deployment against a network source, enabling retries is strongly recommended.
    /// </summary>
    /// <param name="configure">Action that mutates the retry options.</param>
    public ReportBuilder<TRow> Retry(Action<RetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_retry);
        return this;
    }

    /// <summary>
    /// Sets an overall wall-clock deadline for the whole run — reads, writes and uploads together.
    /// Complements the per-attempt read timeout (<c>Retry(r =&gt; r.Timeout(...))</c>): the deadline
    /// bounds the entire report so a run that never hangs on a single step but drags on overall is
    /// still stopped. Off by default. On expiry the run is cooperatively cancelled (for work that
    /// honors cancellation) and surfaces as a cancelled run.
    /// </summary>
    /// <param name="deadline">A positive overall deadline.</param>
    public ReportBuilder<TRow> Deadline(TimeSpan deadline)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        _deadline = deadline;
        return this;
    }

    /// <summary>Configures what happens after a batch exhausts its retries.</summary>
    /// <param name="configure">Action that mutates the failure-strategy builder.</param>
    public ReportBuilder<TRow> OnFailure(Action<FailureStrategyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_failure);
        return this;
    }

    /// <summary>
    /// Declares a recurring-run schedule (ADR D41). The cron expression is validated immediately
    /// and evaluated strictly in UTC by whatever <c>IRecurringReportScheduler</c> is registered; a
    /// host with no recurring scheduler simply never runs this report on its own.
    /// </summary>
    /// <param name="cron">A 5-field cron expression, evaluated in UTC.</param>
    /// <exception cref="ConfigurationException">Thrown when the cron expression is invalid.</exception>
    public ReportBuilder<TRow> Schedule(string cron)
    {
        CronValidation.Validate(cron);
        _schedule = new ScheduleConfig(cron);
        return this;
    }

    /// <summary>
    /// Sets the source-registry reference name this report's source resolves to (ADR D42), for
    /// computed reference counting. Internal: set by the dynamic path's <c>SourceConfig.Ref</c>
    /// compiler wiring and the typed path's by-name source authoring — never called directly by
    /// report authors, who declare a ref through those paths instead.
    /// </summary>
    /// <param name="sourceRef">The registered source name, or <c>null</c> for an inline source.</param>
    internal ReportBuilder<TRow> WithSourceRef(string? sourceRef)
    {
        _sourceRef = sourceRef;
        return this;
    }

    /// <summary>Validates the configuration and produces an immutable compiled report.</summary>
    /// <exception cref="ConfigurationException">Thrown when the configuration is incomplete.</exception>
    public CompiledReport Build()
    {
        if (_batchSource is null && _streamingSource is null)
            throw new ConfigurationException($"Report '{_name}' has no source. Call From(...).");

        bool anyViewColumns =
            _outputs.Any(o => o.View is { ViewColumns.Count: > 0 }) ||
            _sectioned.Any(s => s.Sections.Any(sd => sd.View.ViewColumns.Count > 0));
        if (_columns.Count == 0 && !anyViewColumns)
            throw new ConfigurationException($"Report '{_name}' has no columns. Call Columns(...) or give a view its own columns.");

        // Caught here as well as in the runner so the mistake surfaces at registration rather than
        // partway through the first run. The runner's guard is the load-bearing one — it also covers
        // a custom IFailureStrategy and the dynamic config path, neither of which is a
        // SkipAndLogStrategy — but by then a report is already half written (D79).
        if (_failure.Build() is SkipAndLogStrategy && _outputs.Count + _sectioned.Count > 1)
        {
            throw new ConfigurationException(
                $"Report '{_name}' configures SkipBatchAndLog with {_outputs.Count + _sectioned.Count} outputs. " +
                "A skipped batch would remain in the outputs already written and be missing from the rest, so " +
                "the delivered files would disagree with each other and with the run's stats (D11 batch " +
                "atomicity). Use a single output, or AbortReport.");
        }

        var reportSchema = new ReportSchema(_columns.Select(c => c.Column).ToList());

        var outputSpecs = new OutputSpec[_outputs.Count];
        var outputSchemas = new ReportSchema[_outputs.Count];
        var projections = new OutputProjection<TRow>[_outputs.Count];
        for (var i = 0; i < _outputs.Count; i++)
        {
            (ReportSchema schema, OutputProjection<TRow> projection) = ResolveView(_outputs[i].View, $"output #{i + 1}");
            outputSpecs[i] = _outputs[i].Spec;
            outputSchemas[i] = schema;
            projections[i] = projection;
        }

        var sectionedOutputs = new CompiledSectionedOutput[_sectioned.Count];
        var sectionedProjections = new IReadOnlyList<OutputProjection<TRow>>[_sectioned.Count];
        for (var s = 0; s < _sectioned.Count; s++)
        {
            SectionedEntry entry = _sectioned[s];
            var sectionMetas = new ReportSection[entry.Sections.Count];
            var sectionProjections = new OutputProjection<TRow>[entry.Sections.Count];
            for (var sec = 0; sec < entry.Sections.Count; sec++)
            {
                SectionDefinition<TRow> def = entry.Sections[sec];
                (ReportSchema schema, OutputProjection<TRow> projection) = ResolveView(def.View, $"section '{def.Name}'");
                sectionMetas[sec] = new ReportSection(def.Name, schema);
                sectionProjections[sec] = projection;
            }

            sectionedOutputs[s] = new CompiledSectionedOutput(entry.Spec, sectionMetas);
            sectionedProjections[s] = sectionProjections;
        }

        IBatchSource<TRow>? batchSource = _batchSource;
        IStreamingSource<TRow>? streamingSource = _streamingSource;
        int pageSize = _pageSize;

        IProjectedBatchReader ReaderFactory(ReportExecutionContext execution, IServiceProvider services)
        {
            // The only point in the typed pipeline where an IServiceProvider is available to a
            // by-name source (Source.SqlNamed) — it re-resolves its connection through the source
            // registry at the start of every run rather than baking one in at construction (D42).
            if (batchSource is INamedSourceResolver resolver)
                resolver.AttachServices(services);

            return new TypedBatchReader<TRow>(batchSource, streamingSource, execution, pageSize, projections, sectionedProjections);
        }

        Func<ReportExecutionContext, IServiceProvider, CancellationToken, Task<long?>>? countRows =
            BuildRowCountFactory(_trackProgress ? _rowCounter : null);

        return new CompiledReport(
            _name,
            reportSchema,
            pageSize,
            ReaderFactory,
            outputSpecs,
            outputSchemas,
            sectionedOutputs,
            _destinations.ToArray(),
            _retry,
            _failure.Build(),
            _failure.AbortThresholds,
            _schedule,
            _sourceRef,
            _trackProgress,
            countRows,
            _deadline);
    }

    private (ReportSchema Schema, OutputProjection<TRow> Projection) ResolveView(OutputView<TRow>? view, string what)
    {
        List<ColumnDefinition<TRow>> columns = view is { ViewColumns.Count: > 0 } ? view.ViewColumns : _columns;
        if (columns.Count == 0)
        {
            throw new ConfigurationException(
                $"Report '{_name}' {what} has no columns. Add report Columns(...) or give it its own columns.");
        }

        Func<TRow, bool>[] filters = view is null || view.ViewFilters.Count == 0
            ? _filters.ToArray()
            : _filters.Concat(view.ViewFilters).ToArray();

        return (
            new ReportSchema(columns.Select(c => c.Column).ToList()),
            new OutputProjection<TRow>(filters, columns.Select(c => c.Getter).ToArray()));
    }

    /// <summary>
    /// Composes the row-count delegate <see cref="CompiledReport"/> runs (ADR D47), or <c>null</c>
    /// when tracking is disabled or the source doesn't implement <see cref="ISourceRowCounter"/>.
    /// </summary>
    private static Func<ReportExecutionContext, IServiceProvider, CancellationToken, Task<long?>>? BuildRowCountFactory(
        ISourceRowCounter? rowCounter)
    {
        if (rowCounter is null)
            return null;

        return (execution, services, cancellationToken) =>
        {
            // Defensive: ReaderFactory (which attaches services to a by-name source) normally runs
            // before the runner counts, but a named counter attached here too costs nothing.
            if (rowCounter is INamedSourceResolver named)
                named.AttachServices(services);
            return rowCounter.CountAsync(execution, cancellationToken);
        };
    }

    /// <summary>An output plus its optional per-output view (own filters/columns).</summary>
    private sealed record OutputEntry(OutputSpec Spec, OutputView<TRow>? View);

    /// <summary>A sectioned output plus its section definitions.</summary>
    private sealed record SectionedEntry(SectionedOutputSpec Spec, IReadOnlyList<SectionDefinition<TRow>> Sections);
}
