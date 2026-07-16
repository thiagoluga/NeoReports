using System.Text.Json.Serialization;
using NeoReports.Abstractions;

namespace NeoReports.AspNetCore;

/// <summary>Request body for triggering a report run.</summary>
/// <param name="Parameters">Run-time parameters passed to the report (e.g. date ranges).</param>
/// <param name="Filters">
/// Structured preview filters (ADR D45) to apply for this run — additive, ephemeral, never
/// persisted. Only dynamic (config-registered) SQL-family reports honor them; a typed report
/// rejects a non-empty list with 400, the same as <c>POST /reports/{name}/preview</c>.
/// </param>
public sealed record RunReportRequest(
    IReadOnlyDictionary<string, object?>? Parameters, IReadOnlyList<PreviewFilterRequest>? Filters = null);

/// <summary>One structured preview filter row (ADR D45), as sent over HTTP — <see cref="Operator"/>
/// is the string name of a <c>PreviewFilterOperator</c> value.</summary>
/// <param name="Column">Must be one of the report's declared columns.</param>
/// <param name="Operator">
/// One of "Equals", "NotEquals", "GreaterThan", "GreaterThanOrEqual", "LessThan",
/// "LessThanOrEqual", "Contains", "StartsWith" (case-insensitive).
/// </param>
/// <param name="Value">
/// The value to compare against, decoded via <see cref="FilterValueConverter"/> as its literal text
/// form whatever JSON scalar it arrived as — without an explicit converter, <c>System.Text.Json</c>
/// would leave an <c>object?</c>-typed property as a boxed <see cref="System.Text.Json.JsonElement"/>,
/// which no ADO.NET provider can bind as a <c>DbParameter</c> value.
/// </param>
public sealed record PreviewFilterRequest(
    string Column, string Operator, [property: JsonConverter(typeof(FilterValueConverter))] string? Value);

/// <summary>Request body for <c>POST /reports/{name}/preview</c>.</summary>
/// <param name="Filters">Structured filters to apply; omitted or empty for an unfiltered sample.</param>
/// <param name="PageSize">Requested sample size; capped server-side (<c>ReportPreviewRunner.MaxPageSize</c>).</param>
public sealed record PreviewRequest(IReadOnlyList<PreviewFilterRequest>? Filters, int? PageSize);

/// <summary>Response for <c>POST /reports/{name}/preview</c>.</summary>
/// <param name="Rows">The sample rows, schema-projected (each element is one row's values, in <paramref name="Schema"/> order).</param>
/// <param name="Schema">The columns the rows are aligned to.</param>
/// <param name="FiltersApplied">
/// <c>true</c> when the supplied filters were actually applied server-side; <c>false</c> when there
/// were none, or the source type has no filter translator (the sample still ran, unfiltered).
/// </param>
/// <param name="HasMore">Whether more rows exist beyond this sample.</param>
public sealed record PreviewResponse(
    IReadOnlyList<object?[]> Rows, IReadOnlyList<ReportColumnView> Schema, bool FiltersApplied, bool HasMore);

/// <summary>Response for <c>GET /sources/{name}/catalog</c> — the source's introspected schema (ADR D49).</summary>
/// <param name="Tables">Every user table, ordered by schema then name.</param>
public sealed record SchemaCatalogResponse(IReadOnlyList<CatalogTableView> Tables);

/// <summary>One table in a <see cref="SchemaCatalogResponse"/>.</summary>
/// <param name="Schema">The owning schema/database/owner.</param>
/// <param name="Name">The table name.</param>
/// <param name="Columns">The columns, in ordinal order.</param>
/// <param name="ForeignKeys">Outbound foreign keys — enables FK-aware auto-join suggestions.</param>
public sealed record CatalogTableView(
    string Schema, string Name, IReadOnlyList<CatalogColumnView> Columns, IReadOnlyList<ForeignKeyView> ForeignKeys);

/// <summary>One column of a <see cref="CatalogTableView"/>.</summary>
/// <param name="Name">The column name.</param>
/// <param name="DataType">The database's own type name (mapped to a NeoReports type by the UI).</param>
/// <param name="Nullable">Whether the column allows NULL.</param>
/// <param name="IsPrimaryKey">Whether the column is part of the table's primary key.</param>
public sealed record CatalogColumnView(string Name, string DataType, bool Nullable, bool IsPrimaryKey);

/// <summary>One outbound foreign key of a <see cref="CatalogTableView"/>.</summary>
/// <param name="Column">The referencing column on the owning table.</param>
/// <param name="ReferencedSchema">The referenced table's schema.</param>
/// <param name="ReferencedTable">The referenced table.</param>
/// <param name="ReferencedColumn">The referenced column.</param>
public sealed record ForeignKeyView(string Column, string ReferencedSchema, string ReferencedTable, string ReferencedColumn);

/// <summary>Response for <c>GET /sources/{name}/preview</c> — a bounded table sample (ADR D49).</summary>
/// <param name="Columns">The selected column names, in order.</param>
/// <param name="Rows">The rows, each a positional array aligned to <paramref name="Columns"/>.</param>
public sealed record TablePreviewResponse(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

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
/// <param name="ScheduleCron">The report's effective recurring-run cron expression (override if present, else declared), or <c>null</c> when not scheduled (ADR D41).</param>
/// <param name="NextRunAt">The next occurrence in UTC, computed via Cronos from the active registration — never fabricated; <c>null</c> when not scheduled or no recurring scheduler is registered.</param>
/// <param name="ScheduleOverridden">True when a runtime override (including an "unscheduled" tombstone) is in effect, overriding the report's own declared schedule.</param>
/// <param name="SourceRef">The named source's name (ADR D42), when this report references one — a <c>Ref</c>-based dynamic source, or a code-first report built with <c>Source.SqlNamed</c>/its equivalents; <c>null</c> when the source is an inline connection. Not a secret — same reasoning as <c>GET /sources</c> listing source names freely (D33 only hides the property bag).</param>
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
    double? AbortAtFailureRate = null,
    string? ScheduleCron = null,
    DateTimeOffset? NextRunAt = null,
    bool ScheduleOverridden = false,
    string? SourceRef = null);

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

/// <summary>
/// Process-level memory reading, as returned by <c>GET /system/memory</c> (ADR D39). Deliberately
/// process-wide, not per-job — a single worker process runs multiple jobs, so "memory used by this
/// job" cannot be measured honestly; run a job alone and watch this screen to estimate its footprint.
/// </summary>
/// <param name="WorkingSetBytes">The process's current working set (<see cref="Environment.WorkingSet"/>).</param>
/// <param name="GcHeapSizeBytes">Current GC heap size.</param>
/// <param name="GcCommittedBytes">Total memory committed by the GC.</param>
/// <param name="MeasuredAt">When this reading was taken (UTC) — one reading per request, no background sampling.</param>
/// <param name="RunningJobs">Number of jobs currently in the <c>Running</c> or <c>Retrying</c> status; <c>0</c> when no job store is registered.</param>
public sealed record MemoryView(
    long WorkingSetBytes,
    long GcHeapSizeBytes,
    long GcCommittedBytes,
    DateTimeOffset MeasuredAt,
    int RunningJobs);

/// <summary>What the host can build dynamic reports out of, from its own DI registrations.</summary>
/// <param name="Sources">Registered <c>IConfigSourceProvider</c> type ids, sorted.</param>
/// <param name="Formats">Registered <c>IWriterFactory</c> format ids, sorted.</param>
/// <param name="Destinations">Registered <c>IDestinationFactory</c> type ids, sorted.</param>
/// <param name="Scheduling">True when a recurring-run scheduler is registered (ADR D41); when false, the schedule endpoints reject rather than silently drop schedule input.</param>
public sealed record CapabilitiesResponse(
    IReadOnlyList<string> Sources, IReadOnlyList<string> Formats, IReadOnlyList<string> Destinations, bool Scheduling = false);

/// <summary>Request body for setting a report's runtime schedule override (<c>PUT {prefix}/reports/{name}/schedule</c>).</summary>
/// <param name="Cron">A 5-field cron expression, evaluated in UTC (ADR D41).</param>
public sealed record SetScheduleRequest(string Cron);

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

/// <summary>
/// Full view of a registered source (ADR D42) — never carries <c>Properties</c>, the D33
/// property-bag rule at its most literal, since that is precisely where secrets live.
/// </summary>
/// <param name="Name">The source name.</param>
/// <param name="Type">Provider type id (e.g. "sql").</param>
/// <param name="Description">Optional free-text description.</param>
/// <param name="ReferencedByCount">Number of registered reports whose source resolves to this one — always derivable, never separately tracked.</param>
/// <param name="LastHealthStatus">"healthy" or "unhealthy", or <c>null</c> when never checked.</param>
/// <param name="LastHealthError">Failure detail from the last check, when unhealthy.</param>
/// <param name="LastHealthLatencyMs">Latency of the last check, in milliseconds.</param>
/// <param name="LastCheckedAt">When the last check ran (UTC), or <c>null</c> when never checked.</param>
public sealed record SourceView(
    string Name,
    string Type,
    string? Description,
    int ReferencedByCount,
    string? LastHealthStatus,
    string? LastHealthError,
    double? LastHealthLatencyMs,
    DateTimeOffset? LastCheckedAt);

/// <summary>Request body for <c>POST /sources</c> and <c>PUT /sources/{name}</c> (full replace).</summary>
/// <param name="Name">The source name (for POST; for PUT, must match the URL segment or the request is rejected).</param>
/// <param name="Type">Provider type id (e.g. "sql").</param>
/// <param name="Properties">Provider-specific settings, e.g. connection string as <c>${VAR}</c>. Write-only — never returned by any GET.</param>
/// <param name="Description">Optional free-text description.</param>
public sealed record SourceRequest(
    string Name, string Type, IReadOnlyDictionary<string, object?>? Properties = null, string? Description = null);

/// <summary>Response for <c>POST /sources/{name}/health</c> — the result of a check that just ran.</summary>
/// <param name="Healthy"><c>true</c> when the check succeeded.</param>
/// <param name="Error">Failure detail, when <paramref name="Healthy"/> is <c>false</c>.</param>
/// <param name="LatencyMs">How long the check took, in milliseconds.</param>
/// <param name="CheckedAt">When the check ran (UTC).</param>
public sealed record SourceHealthResponse(bool Healthy, string? Error, double LatencyMs, DateTimeOffset CheckedAt);
