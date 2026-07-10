using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Sections;

namespace NeoReports.Core;

/// <summary>A compiled sectioned output: the writer factory plus its sections (name + schema), in order.</summary>
internal sealed record CompiledSectionedOutput(SectionedOutputSpec Spec, IReadOnlyList<ReportSection> Sections);

/// <summary>
/// An immutable, type-erased report definition produced by <c>ReportBuilder&lt;T&gt;</c> and held
/// by the registry. The generic row type is captured inside <see cref="ReaderFactory"/>, so the
/// pipeline can execute the report without knowing <c>T</c>.
/// </summary>
public sealed class CompiledReport
{
    internal CompiledReport(
        string name,
        ReportSchema schema,
        int pageSize,
        Func<ReportExecutionContext, IProjectedBatchReader> readerFactory,
        IReadOnlyList<OutputSpec> outputs,
        IReadOnlyList<ReportSchema> outputSchemas,
        IReadOnlyList<CompiledSectionedOutput> sectionedOutputs,
        IReadOnlyList<DestinationSpec> destinations,
        RetryOptions retry,
        IFailureStrategy failureStrategy,
        AbortThresholdConfig? abortThresholds = null,
        ScheduleConfig? schedule = null)
    {
        Name = name;
        Schema = schema;
        PageSize = pageSize;
        ReaderFactory = readerFactory;
        Outputs = outputs;
        OutputSchemas = outputSchemas;
        SectionedOutputs = sectionedOutputs;
        Destinations = destinations;
        Retry = retry;
        FailureStrategy = failureStrategy;
        AbortThresholds = abortThresholds;
        Schedule = schedule;

        OutputFormats = outputs.Select(o => o.Factory.Format).ToArray();
        DestinationTypes = destinations.Select(d => d.Factory.Type).ToArray();
    }

    /// <summary>The unique report name it was registered under.</summary>
    public string Name { get; }

    /// <summary>The output schema (columns in order).</summary>
    public ReportSchema Schema { get; }

    /// <summary>Page size used when reading the source.</summary>
    public int PageSize { get; }

    /// <summary>Number of configured outputs (formats).</summary>
    public int OutputCount => Outputs.Count;

    /// <summary>Format id of each configured output, in order (e.g. "csv", "xlsx").</summary>
    public IReadOnlyList<string> OutputFormats { get; }

    /// <summary>Type id of each configured destination, in order (e.g. "local", "s3").</summary>
    public IReadOnlyList<string> DestinationTypes { get; }

    /// <summary>Creates a fresh per-execution reader for this report.</summary>
    internal Func<ReportExecutionContext, IProjectedBatchReader> ReaderFactory { get; }

    /// <summary>Configured outputs (formats).</summary>
    internal IReadOnlyList<OutputSpec> Outputs { get; }

    /// <summary>Schema of each output, aligned to <see cref="Outputs"/> (each output may have its own columns).</summary>
    internal IReadOnlyList<ReportSchema> OutputSchemas { get; }

    /// <summary>Configured sectioned outputs (single file, several sections — e.g. an XLSX workbook).</summary>
    internal IReadOnlyList<CompiledSectionedOutput> SectionedOutputs { get; }

    /// <summary>Configured destinations.</summary>
    internal IReadOnlyList<DestinationSpec> Destinations { get; }

    /// <summary>Declarative retry configuration.</summary>
    public RetryOptions Retry { get; }

    /// <summary>Strategy applied after a batch exhausts its retries.</summary>
    public IFailureStrategy FailureStrategy { get; }

    /// <summary>
    /// The escalation thresholds configured via <c>FailureStrategyBuilder.AbortIf(AbortThresholdConfig)</c>,
    /// or <c>null</c> when none were set, or a raw predicate was used instead (not introspectable — ADR D37).
    /// </summary>
    public AbortThresholdConfig? AbortThresholds { get; }

    /// <summary>
    /// The recurring-run schedule declared via <c>ReportBuilder&lt;T&gt;.Schedule(cron)</c> or the
    /// config document's <c>Schedule</c>, or <c>null</c> when the report has no declared schedule
    /// (ADR D41). A runtime override, if any, takes precedence over this at effective-schedule
    /// resolution time (<c>EffectiveSchedule.Resolve</c>) — this property always reflects only the
    /// declaration, never the override.
    /// </summary>
    public ScheduleConfig? Schedule { get; }
}
