using System.Text.Json.Serialization;
using NeoReports.Abstractions;

namespace NeoReports.AspNetCore;

/// <summary>Request body for triggering a report run.</summary>
/// <param name="Parameters">Run-time parameters passed to the report (e.g. date ranges).</param>
public sealed record RunReportRequest(IReadOnlyDictionary<string, object?>? Parameters);

/// <summary>Response returned when a report is triggered asynchronously.</summary>
/// <param name="JobId">Identifier of the queued job.</param>
/// <param name="Status">Initial job status (typically <c>Queued</c>).</param>
public sealed record RunAcceptedResponse(
    string JobId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ReportJobStatus Status);

/// <summary>Summary of a registered report.</summary>
/// <param name="Name">The report name.</param>
/// <param name="OutputCount">Number of configured output formats.</param>
/// <param name="Columns">Output column names, in order.</param>
/// <param name="Formats">Output format ids, in order (e.g. "csv", "xlsx").</param>
/// <param name="Destinations">Destination type ids, in order (e.g. "local", "s3").</param>
public sealed record ReportSummary(
    string Name,
    int OutputCount,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> Destinations);

/// <summary>A single output column in a report's schema.</summary>
/// <param name="Name">Stable column key.</param>
/// <param name="Type">Semantic column type (<see cref="ColumnType"/> name).</param>
/// <param name="DisplayName">Header label, when set.</param>
/// <param name="Format">.NET format string used for rendering, when set.</param>
/// <param name="Nullable">Whether the column may contain null values.</param>
public sealed record ReportColumnView(string Name, string Type, string? DisplayName, string? Format, bool Nullable);

/// <summary>Full, safe definition of a registered report — never includes source/output/destination property bags.</summary>
/// <param name="Name">The report name.</param>
/// <param name="Columns">Output columns, in order.</param>
/// <param name="PageSize">Page size used when reading the source.</param>
/// <param name="Formats">Output format ids, in order.</param>
/// <param name="Destinations">Destination type ids, in order.</param>
/// <param name="FailureStrategy">Stable name of the strategy applied after a batch exhausts its retries.</param>
/// <param name="RetryMaxAttempts">Total number of attempts per batch, including the first.</param>
/// <param name="RetryBackoff">Backoff shape between attempts ("Constant" or "Exponential").</param>
/// <param name="RetryBaseDelaySeconds">Base delay, in seconds, used for the first retry.</param>
/// <param name="RetryUseJitter">Whether randomized jitter is added to retry delays.</param>
/// <param name="Origin">"code" for a report registered at startup, "config" for one registered at runtime (ADR D33).</param>
/// <param name="Deletable">True when the report can be removed via <c>DELETE /reports/{name}</c> (only "config" reports).</param>
/// <param name="AbortAfterConsecutiveFailures">Escalates skip-and-log to abort after this many consecutive batch failures; <c>null</c> when not configured or a custom predicate is used (ADR D37).</param>
/// <param name="AbortAfterTotalFailures">Escalates skip-and-log to abort after this many total batch failures; <c>null</c> when not configured or a custom predicate is used.</param>
/// <param name="AbortAtFailureRate">Escalates skip-and-log to abort once the failure ratio reaches this value; <c>null</c> when not configured or a custom predicate is used.</param>
public sealed record ReportDetailView(
    string Name,
    IReadOnlyList<ReportColumnView> Columns,
    int PageSize,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> Destinations,
    string FailureStrategy,
    int RetryMaxAttempts,
    string RetryBackoff,
    double RetryBaseDelaySeconds,
    bool RetryUseJitter,
    string Origin,
    bool Deletable,
    int? AbortAfterConsecutiveFailures = null,
    int? AbortAfterTotalFailures = null,
    double? AbortAtFailureRate = null);

/// <summary>Response returned when a dynamic report is registered.</summary>
/// <param name="Name">The report name.</param>
/// <param name="Columns">Output column names, in order.</param>
public sealed record ReportCreatedResponse(string Name, IReadOnlyList<string> Columns);

/// <summary>Result of a dry-run compile of a report configuration (<c>POST /reports/validate</c>).</summary>
/// <param name="Valid"><c>true</c> when the document parsed and compiled successfully.</param>
/// <param name="Error">The failure message, when <paramref name="Valid"/> is <c>false</c>.</param>
/// <param name="Name">The report name, when it could be determined.</param>
/// <param name="Columns">Output column names, in order, when the config compiled.</param>
/// <param name="NameTaken"><c>true</c> when a report is already registered under <paramref name="Name"/>.</param>
public sealed record ValidateReportResponse(
    bool Valid, string? Error, string? Name, IReadOnlyList<string>? Columns, bool NameTaken);

/// <summary>A finished output file of a completed job, without its on-disk path.</summary>
/// <param name="FileName">The file name as it would be downloaded.</param>
/// <param name="MimeType">The file's MIME type.</param>
/// <param name="SizeBytes">The file size, in bytes.</param>
public sealed record ArtifactView(string FileName, string MimeType, long SizeBytes);

/// <summary>One structured lifecycle event of a job run, as returned by <c>GET /jobs/{id}/events</c> (ADR D38).</summary>
/// <param name="Sequence">Monotonic, per-job event ordinal (1-based).</param>
/// <param name="At">When the event was recorded (UTC).</param>
/// <param name="Type">One of the closed vocabulary values (e.g. "page-completed", "retry", "run-completed").</param>
/// <param name="Message">Optional free-text detail (e.g. a truncated, sanitized exception message).</param>
/// <param name="Data">Optional structured fields specific to <paramref name="Type"/>.</param>
public sealed record JobEventView(
    int Sequence,
    DateTimeOffset At,
    string Type,
    string? Message,
    IReadOnlyDictionary<string, string>? Data);

/// <summary>What the host can build dynamic reports out of, from its own DI registrations.</summary>
/// <param name="Sources">Registered <c>IConfigSourceProvider</c> type ids, sorted.</param>
/// <param name="Formats">Registered <c>IWriterFactory</c> format ids, sorted.</param>
/// <param name="Destinations">Registered <c>IDestinationFactory</c> type ids, sorted.</param>
public sealed record CapabilitiesResponse(
    IReadOnlyList<string> Sources, IReadOnlyList<string> Formats, IReadOnlyList<string> Destinations);

/// <summary>Status and statistics view of a job.</summary>
/// <param name="Id">Job id.</param>
/// <param name="ReportName">Report the job runs.</param>
/// <param name="Status">Current lifecycle status.</param>
/// <param name="CreatedAt">When the job was created.</param>
/// <param name="StartedAt">When processing started, if it has.</param>
/// <param name="CompletedAt">When the job finished, if it has.</param>
/// <param name="Error">Failure reason, when failed.</param>
/// <param name="Stats">Aggregate counters.</param>
public sealed record JobView(
    string Id,
    string ReportName,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ReportJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    JobStats Stats)
{
    /// <summary>Maps a persisted <see cref="ReportJob"/> to its API view.</summary>
    /// <param name="job">The job to map.</param>
    public static JobView From(ReportJob job) => new(
        job.Id, job.ReportName, job.Status, job.CreatedAt, job.StartedAt, job.CompletedAt, job.Error, job.Stats);
}
