namespace NeoReports.Abstractions;

// Serializer-agnostic configuration model for the dynamic (config-driven) path. These are plain
// records with no JSON coupling; a parser (e.g. the Core JSON parser) maps a document onto them,
// and a compiler turns them into the same runnable report a fluent builder produces. The model
// mirrors the fluent builder one-to-one.

/// <summary>
/// A complete report definition expressed as data (the dynamic path). A compiler turns this into
/// the same runnable report that <c>ReportBuilder&lt;ReportRecord&gt;</c> produces in code.
/// </summary>
/// <param name="Name">Unique report name.</param>
/// <param name="Source">The source the rows are read from.</param>
/// <param name="Columns">Output columns, in order; they define the positional schema.</param>
/// <param name="Outputs">Output formats (at least one).</param>
/// <param name="Destinations">Upload destinations; <c>null</c> or empty means none.</param>
/// <param name="PageSize">Optional page size; the engine default is used when null.</param>
/// <param name="Filter">Optional dynamic filter expression (JsonLogic); evaluated by a later epic.</param>
public sealed record ReportConfig(
    string Name,
    SourceConfig Source,
    IReadOnlyList<ColumnConfig> Columns,
    IReadOnlyList<OutputConfig> Outputs,
    IReadOnlyList<DestinationConfig>? Destinations = null,
    int? PageSize = null,
    string? Filter = null);

/// <summary>A source section: a stable type id plus a free-form property bag the provider reads.</summary>
/// <param name="Type">Stable source type id (e.g. "sql"); resolved to an <see cref="IConfigSourceProvider"/>.</param>
/// <param name="Properties">Provider-specific settings (e.g. connection string, query, key).</param>
public sealed record SourceConfig(
    string Type,
    IReadOnlyDictionary<string, object?>? Properties = null);

/// <summary>
/// A single output column declared as data. Because a dynamic row is positional, the column's
/// position is its index in <see cref="ReportConfig.Columns"/>; the rest mirrors <see cref="ReportColumn"/>.
/// </summary>
/// <param name="Name">Stable column key, unique within the report.</param>
/// <param name="Type">Semantic column type used for formatting and projection.</param>
/// <param name="DisplayName">Optional header label; defaults to <paramref name="Name"/>.</param>
/// <param name="Format">Optional .NET format string for rendering.</param>
/// <param name="Culture">Optional culture name (e.g. "pt-BR") for rendering.</param>
/// <param name="Nullable">Whether the column may contain null values.</param>
public sealed record ColumnConfig(
    string Name,
    ColumnType Type,
    string? DisplayName = null,
    string? Format = null,
    string? Culture = null,
    bool Nullable = true);

/// <summary>An output section: a stable format id plus a free-form property bag the writer reads.</summary>
/// <param name="Format">Stable format id (e.g. "csv", "xlsx"); resolved to an <see cref="IWriterFactory"/>.</param>
/// <param name="Properties">Format-specific options.</param>
public sealed record OutputConfig(
    string Format,
    IReadOnlyDictionary<string, object?>? Properties = null);

/// <summary>A destination section: a stable type id plus a free-form property bag.</summary>
/// <param name="Type">Stable destination type id (e.g. "local", "s3"); resolved to an <see cref="IDestinationFactory"/>.</param>
/// <param name="Properties">Destination-specific options (e.g. path/key template, bucket).</param>
public sealed record DestinationConfig(
    string Type,
    IReadOnlyDictionary<string, object?>? Properties = null);

/// <summary>Parses a serialized report definition (e.g. JSON) into a <see cref="ReportConfig"/>.</summary>
public interface IReportConfigParser
{
    /// <summary>Parses the given document into a report configuration.</summary>
    /// <param name="document">The serialized configuration (e.g. a JSON string).</param>
    /// <returns>The parsed configuration.</returns>
    /// <exception cref="ConfigurationException">Thrown when the document is missing or malformed.</exception>
    ReportConfig Parse(string document);
}

/// <summary>
/// Builds a positional <see cref="ReportRecord"/> source from a <see cref="SourceConfig"/>. The
/// dynamic equivalent of a typed source factory: providers are registered by <see cref="Type"/> and
/// resolved by the config compiler. The output schema is supplied so the provider can align values
/// to columns by name/position.
/// </summary>
public interface IConfigSourceProvider
{
    /// <summary>Stable source type id this provider handles (e.g. "sql"); matched case-insensitively.</summary>
    string Type { get; }

    /// <summary>Creates the batch source that yields positional records aligned to <paramref name="schema"/>.</summary>
    /// <param name="source">The source configuration section.</param>
    /// <param name="schema">The report's output schema (columns in order).</param>
    /// <param name="services">The service provider for resolving dependencies.</param>
    /// <returns>A batch source producing <see cref="ReportRecord"/> rows.</returns>
    IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services);
}
