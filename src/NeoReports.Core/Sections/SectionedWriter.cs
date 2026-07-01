using NeoReports.Abstractions;

namespace NeoReports.Core.Sections;

/// <summary>
/// One section of a sectioned output (e.g. a worksheet in a workbook): its name and column schema.
/// </summary>
/// <param name="Name">Stable section name (e.g. the sheet name).</param>
/// <param name="Schema">The section's column schema; sections may differ in columns.</param>
public sealed record ReportSection(string Name, ReportSchema Schema);

/// <summary>Inputs given to a sectioned writer when it is initialized.</summary>
public sealed class SectionedWriterContext
{
    /// <summary>Creates a context for one sectioned output.</summary>
    /// <param name="execution">The ambient execution context.</param>
    /// <param name="output">Destination stream for the single produced file.</param>
    /// <param name="sections">The sections to write, in order (name + schema).</param>
    /// <param name="options">Format-specific options; <c>null</c> is treated as empty.</param>
    public SectionedWriterContext(
        ReportExecutionContext execution,
        Stream output,
        IReadOnlyList<ReportSection> sections,
        IReadOnlyDictionary<string, object?>? options)
    {
        Execution = execution;
        Output = output;
        Sections = sections;
        Options = options ?? new Dictionary<string, object?>();
    }

    /// <summary>The ambient execution context.</summary>
    public ReportExecutionContext Execution { get; }

    /// <summary>Destination stream for the single produced file.</summary>
    public Stream Output { get; }

    /// <summary>The sections to write, in order.</summary>
    public IReadOnlyList<ReportSection> Sections { get; }

    /// <summary>Format-specific options.</summary>
    public IReadOnlyDictionary<string, object?> Options { get; }
}

/// <summary>
/// A writer that packs several sections into a <b>single</b> file (e.g. an XLSX workbook with one
/// worksheet per section). It receives already-projected rows per section, in the same single pass
/// over the source. Lives in Core (an engine concern) so packages — including commercial ones — can
/// implement it without touching the frozen <c>Abstractions</c> ABI.
/// </summary>
public interface IReportSectionedWriter : IAsyncDisposable
{
    /// <summary>Stable format id (e.g. "xlsx-workbook").</summary>
    string Format { get; }

    /// <summary>MIME type of the produced file.</summary>
    string MimeType { get; }

    /// <summary>File extension (without the dot) of the produced file.</summary>
    string FileExtension { get; }

    /// <summary>Initializes the writer for all sections (e.g. creates the worksheets and headers).</summary>
    /// <param name="context">Output stream, sections, and options.</param>
    /// <param name="cancellationToken">Token that cancels initialization.</param>
    Task InitializeAsync(SectionedWriterContext context, CancellationToken cancellationToken);

    /// <summary>Writes a page of projected rows to one section.</summary>
    /// <param name="sectionIndex">Index of the section (aligned to <see cref="SectionedWriterContext.Sections"/>).</param>
    /// <param name="rows">Projected rows for that section, in its schema column order.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    Task WriteSectionRowsAsync(int sectionIndex, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken);

    /// <summary>Flushes and finalizes the single output file (e.g. saves the workbook).</summary>
    /// <param name="cancellationToken">Token that cancels finalization.</param>
    Task FinalizeAsync(CancellationToken cancellationToken);
}

/// <summary>Creates an <see cref="IReportSectionedWriter"/> from registration-time configuration.</summary>
public interface ISectionedWriterFactory
{
    /// <summary>Stable format id (e.g. "xlsx-workbook").</summary>
    string Format { get; }

    /// <summary>Creates a sectioned writer instance.</summary>
    /// <param name="options">Format-specific options captured at registration time.</param>
    /// <param name="services">The service provider for resolving dependencies.</param>
    IReportSectionedWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services);
}
