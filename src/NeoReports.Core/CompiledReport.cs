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
        Func<ReportExecutionContext, IServiceProvider, IProjectedBatchReader> readerFactory,
        IReadOnlyList<OutputSpec> outputs,
        IReadOnlyList<ReportSchema> outputSchemas,
        IReadOnlyList<CompiledSectionedOutput> sectionedOutputs,
        IReadOnlyList<DestinationSpec> destinations,
        RetryOptions retry,
        IFailureStrategy failureStrategy,
        AbortThresholdConfig? abortThresholds = null,
        ScheduleConfig? schedule = null,
        string? sourceRef = null,
        bool trackProgress = true,
        Func<ReportExecutionContext, IServiceProvider, CancellationToken, Task<long?>>? countRows = null,
        TimeSpan? deadline = null)
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
        SourceRef = sourceRef;
        TrackProgress = trackProgress;
        RowCountFactory = countRows;
        Deadline = deadline;

        // Both collections produce a delivered artifact, so both belong in the count and the format
        // list. Counting only Outputs made a report with one plain and one sectioned output look
        // single-output: the API's sync guard let it through, the runner then wrote two files, and the
        // caller silently received whichever one directory enumeration happened to yield first.
        OutputFormats = outputs.Select(o => o.Factory.Format)
            .Concat(sectionedOutputs.Select(s => s.Spec.Factory.Format))
            .ToArray();
        DestinationTypes = destinations.Select(d => d.Factory.Type).ToArray();
    }

    /// <summary>The unique report name it was registered under.</summary>
    public string Name { get; }

    /// <summary>The output schema (columns in order).</summary>
    public ReportSchema Schema { get; }

    /// <summary>Page size used when reading the source.</summary>
    public int PageSize { get; }

    /// <summary>Number of configured outputs (formats).</summary>
    public int OutputCount => Outputs.Count + SectionedOutputs.Count;

    /// <summary>Format id of each configured output, in order (e.g. "csv", "xlsx").</summary>
    public IReadOnlyList<string> OutputFormats { get; }

    /// <summary>Type id of each configured destination, in order (e.g. "local", "s3").</summary>
    public IReadOnlyList<string> DestinationTypes { get; }

    /// <summary>Creates a fresh per-execution reader for this report, given the run's <see cref="IServiceProvider"/>.</summary>
    internal Func<ReportExecutionContext, IServiceProvider, IProjectedBatchReader> ReaderFactory { get; }

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

    /// <summary>
    /// The name of the registered source (ADR D42) this report reads from, or <c>null</c> when the
    /// source is declared inline. Populated by the dynamic path's <c>SourceConfig.Ref</c> (F2) and
    /// the typed path's by-name authoring (F5, <c>Source.SqlNamed</c>). "Used in N reports" for a
    /// source is always the count of registered reports whose <see cref="SourceRef"/> matches its
    /// name — no separate usage store exists; the number is always derivable truth.
    /// </summary>
    public string? SourceRef { get; }

    /// <summary>
    /// Whether progress tracking is enabled for this report (ADR D47) — declared via
    /// <c>ReportBuilder&lt;T&gt;.TrackProgress(bool)</c> or the config document's
    /// <c>TrackProgress</c>, default <c>true</c>. Does not by itself guarantee a real percentage:
    /// <see cref="RowCountFactory"/> is also <c>null</c> when the source doesn't support counting.
    /// </summary>
    public bool TrackProgress { get; }

    /// <summary>
    /// Counts the source's total rows for this report (ADR D47), or <c>null</c> when progress
    /// tracking is disabled or the source doesn't implement <c>ISourceRowCounter</c> — the runner
    /// needs only this one check, not <see cref="TrackProgress"/> separately.
    /// </summary>
    internal Func<ReportExecutionContext, IServiceProvider, CancellationToken, Task<long?>>? RowCountFactory { get; }

    /// <summary>
    /// An overall wall-clock deadline for a whole run, declared via <c>ReportBuilder&lt;T&gt;.Deadline(...)</c>,
    /// or <c>null</c> (the default) for no deadline. Complements the per-attempt read timeout
    /// (<c>RetryOptions.Timeout</c>, which bounds each read): the deadline bounds the entire report —
    /// reads, writes and uploads together — so a run that never hangs on any single step but drags on
    /// overall is still stopped. Enforced cooperatively by <c>ReportRunner.RunAsync</c>: on expiry the
    /// run is cancelled (it surfaces as a cancelled run), for any work that honors cancellation.
    /// </summary>
    public TimeSpan? Deadline { get; }
}
