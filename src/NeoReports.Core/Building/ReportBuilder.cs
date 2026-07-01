using System.Linq.Expressions;
using NeoReports.Abstractions;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Sections;
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

    private IBatchSource<TRow>? _batchSource;
    private IStreamingSource<TRow>? _streamingSource;
    private int _pageSize = 1000;

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
        return this;
    }

    /// <summary>Reads from a streaming source (sliced into batches by the pipeline).</summary>
    /// <param name="source">The streaming source.</param>
    public ReportBuilder<TRow> From(IStreamingSource<TRow> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _streamingSource = source;
        _batchSource = null;
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

    /// <summary>Configures retry behavior for batch reads.</summary>
    /// <param name="configure">Action that mutates the retry options.</param>
    public ReportBuilder<TRow> Retry(Action<RetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_retry);
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

        IProjectedBatchReader ReaderFactory(ReportExecutionContext execution) =>
            new TypedBatchReader<TRow>(batchSource, streamingSource, execution, pageSize, projections, sectionedProjections);

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
            _failure.Build());
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

    /// <summary>An output plus its optional per-output view (own filters/columns).</summary>
    private sealed record OutputEntry(OutputSpec Spec, OutputView<TRow>? View);

    /// <summary>A sectioned output plus its section definitions.</summary>
    private sealed record SectionedEntry(SectionedOutputSpec Spec, IReadOnlyList<SectionDefinition<TRow>> Sections);
}
