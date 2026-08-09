using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NeoReports.UI.Services;

/// <summary>A report registered with the NeoReports engine, as returned by <c>GET /api/reports</c>.</summary>
public sealed record ApiReportSummary(string Name, int OutputCount, IReadOnlyList<string> Columns);

/// <summary>Aggregate counters for a job, mirroring <c>NeoReports.Abstractions.JobStats</c>. <see cref="TotalRecords"/>
/// is the pre-run row count (ADR D47) — null when tracking was off, unsupported, or the count failed; an older
/// engine without the field also deserializes it to null, so this is forward/backward compatible.</summary>
public sealed record ApiJobStats(
    long RecordsRead, long RecordsWritten, long BytesWritten, int Retries, int BatchesProcessed, long? TotalRecords = null);

/// <summary>A job's status view, as returned by <c>GET /api/jobs/{id}</c>. <see cref="Status"/> is the
/// <c>ReportJobStatus</c> enum member name (e.g. "Running", "Completed", "Failed").</summary>
public sealed record ApiJobView(
    string Id,
    string ReportName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    ApiJobStats Stats);

/// <summary>What the engine can build dynamic reports out of, as returned by <c>GET /api/capabilities</c>.</summary>
public sealed record ApiCapabilities(
    IReadOnlyList<string> Sources, IReadOnlyList<string> Formats, IReadOnlyList<string> Destinations, bool Scheduling = false);

/// <summary>Result of <c>POST /api/reports/validate</c> — a dry-run compile, never a registration.</summary>
public sealed record ApiValidationResult(
    bool Valid, string? Error, string? Name, IReadOnlyList<string>? Columns, bool NameTaken);

/// <summary>Outcome of a <c>POST /api/reports</c> call.</summary>
public enum ApiCreateOutcome
{
    /// <summary>201 — the report was registered.</summary>
    Created,

    /// <summary>409 — a report with that name already exists.</summary>
    NameTaken,

    /// <summary>400 — the config document was rejected (bad name, unknown provider, etc.).</summary>
    Invalid,

    /// <summary>The engine wasn't reachable, or returned an unexpected status.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="INeoReportsApiClient.TryCreateReportAsync"/>.</summary>
public sealed record ApiCreateResult(ApiCreateOutcome Outcome, string? Name, string? Error);

/// <summary>A single output column, as returned by <c>GET /api/reports/{name}</c>.</summary>
public sealed record ApiReportColumn(string Name, string Type, string? DisplayName, string? Format, bool Nullable);

/// <summary>The full, safe definition of a registered report, as returned by <c>GET /api/reports/{name}</c>.</summary>
public sealed record ApiReportDetail(
    string Name,
    IReadOnlyList<ApiReportColumn> Columns,
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

/// <summary>A finished output file of a completed job, as returned by <c>GET /api/jobs/{id}/artifacts</c>.</summary>
public sealed record ApiArtifact(string FileName, string MimeType, long SizeBytes);

/// <summary>One structured lifecycle event of a job run, as returned by <c>GET /api/jobs/{id}/events</c> (ADR D38).</summary>
public sealed record ApiJobEvent(
    int Sequence, DateTimeOffset At, string Type, string? Message, IReadOnlyDictionary<string, string>? Data);

/// <summary>Process-level memory reading, as returned by <c>GET /api/system/memory</c> (ADR D39).</summary>
public sealed record ApiMemory(
    long WorkingSetBytes, long GcHeapSizeBytes, long GcCommittedBytes, DateTimeOffset MeasuredAt, int RunningJobs);

/// <summary>
/// A registered source (ADR D42), as returned by <c>GET /api/sources[/{name}]</c>. Never carries
/// its property bag — write-only, since that's precisely where secrets live (D33).
/// </summary>
public sealed record ApiSourceView(
    string Name,
    string Type,
    string? Description,
    int ReferencedByCount,
    string? LastHealthStatus,
    string? LastHealthError,
    double? LastHealthLatencyMs,
    DateTimeOffset? LastCheckedAt);

/// <summary>Outcome of a <c>POST</c>/<c>PUT /api/sources</c> call.</summary>
public enum ApiSourceSaveOutcome
{
    /// <summary>201/200 — the source was created or replaced.</summary>
    Saved,

    /// <summary>409 — a source with that name already exists (create only).</summary>
    NameTaken,

    /// <summary>400 — the request was rejected (bad name, unknown provider type, name mismatch).</summary>
    Invalid,

    /// <summary>404 — no source exists under that name (replace only).</summary>
    NotFound,

    /// <summary>The engine wasn't reachable, or returned an unexpected status.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="INeoReportsApiClient.TryCreateSourceAsync"/>/<see cref="INeoReportsApiClient.TryReplaceSourceAsync"/>.</summary>
public sealed record ApiSourceSaveResult(ApiSourceSaveOutcome Outcome, string? Error);

/// <summary>Outcome of a <c>DELETE /api/sources/{name}</c> call.</summary>
public enum ApiSourceDeleteOutcome
{
    /// <summary>204 — the source was removed.</summary>
    Deleted,

    /// <summary>409 — still referenced by at least one registered report.</summary>
    Referenced,

    /// <summary>404 — no source exists under that name.</summary>
    NotFound,

    /// <summary>The engine wasn't reachable, or returned an unexpected status.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="INeoReportsApiClient.TryDeleteSourceAsync"/>. <see cref="Error"/> carries the
/// engine's own message (e.g. naming the referencing report count) for <see cref="ApiSourceDeleteOutcome.Referenced"/>.</summary>
public sealed record ApiSourceDeleteResult(ApiSourceDeleteOutcome Outcome, string? Error);

/// <summary>Result of a health check that just ran, as returned by <c>POST /api/sources/{name}/health</c>.</summary>
public sealed record ApiSourceHealth(bool Healthy, string? Error, double LatencyMs, DateTimeOffset CheckedAt);

/// <summary>
/// One structured preview filter row (ADR D45), sent to <c>POST /api/reports/{name}/preview</c>.
/// <see cref="Operator"/> is the string name of a <c>PreviewFilterOperator</c> value (e.g. "Equals",
/// "Contains") — see <see cref="INeoReportsApiClient"/>'s preview docs for the closed list.
/// </summary>
public sealed record ApiPreviewFilter(string Column, string Operator, object? Value);

/// <summary>Result of a successful <c>POST /api/reports/{name}/preview</c> call.</summary>
public sealed record ApiPreviewData(
    IReadOnlyList<object?[]> Rows, IReadOnlyList<ApiReportColumn> Schema, bool FiltersApplied, bool HasMore);

/// <summary>Outcome of a <c>POST /api/reports/{name}/preview</c> call.</summary>
public enum ApiPreviewOutcome
{
    /// <summary>200 — the sample was returned.</summary>
    Ok,

    /// <summary>404 — no report with that name is registered.</summary>
    NotFound,

    /// <summary>400 — non-empty filters against a typed (code-first) report, or an invalid filter.</summary>
    Invalid,

    /// <summary>The engine wasn't reachable, or returned an unexpected status.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="INeoReportsApiClient.TryPreviewReportAsync"/>.</summary>
public sealed record ApiPreviewResult(ApiPreviewOutcome Outcome, ApiPreviewData? Data, string? Error);

/// <summary>A source's introspected schema, from <c>GET /api/sources/{name}/catalog</c> (ADR D49/K3).</summary>
public sealed record ApiSchemaCatalog(IReadOnlyList<ApiCatalogTable> Tables);

/// <summary>One table in an <see cref="ApiSchemaCatalog"/>.</summary>
public sealed record ApiCatalogTable(
    string Schema, string Name, IReadOnlyList<ApiCatalogColumn> Columns, IReadOnlyList<ApiForeignKey> ForeignKeys);

/// <summary>One column of an <see cref="ApiCatalogTable"/>.</summary>
public sealed record ApiCatalogColumn(string Name, string DataType, bool Nullable, bool IsPrimaryKey);

/// <summary>One outbound foreign key of an <see cref="ApiCatalogTable"/> — drives FK-aware auto-join suggestions.</summary>
public sealed record ApiForeignKey(string Column, string ReferencedSchema, string ReferencedTable, string ReferencedColumn);

/// <summary>A bounded table sample, from <c>GET /api/sources/{name}/preview</c> (ADR D49/K3).</summary>
public sealed record ApiTablePreview(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>The keyset-safe SQL a visual query compiles to, from <c>POST /api/sources/{name}/query-sql</c> (ADR D49/K5a).</summary>
/// <param name="KeyColumnName">The result-set column name the keyset key must be read from (ADR D49/K6c) — the value to send as the <c>key</c> source property when creating a report from this query.</param>
public sealed record ApiGeneratedQuerySql(
    string Sql, IReadOnlyDictionary<string, object?> Parameters, IReadOnlyList<ApiReportColumn> Schema, string KeyColumnName);

/// <summary>Outcome of a <c>POST /api/sources/{name}/query-sql</c> call.</summary>
public enum ApiQuerySqlOutcome
{
    /// <summary>200 — the SQL was generated.</summary>
    Ok,

    /// <summary>404 — no source with that name is registered.</summary>
    NotFound,

    /// <summary>422 — the host has no query-builder generator (an MIT-only host, D36).</summary>
    NotSupported,

    /// <summary>400 — the query model was empty or invalid.</summary>
    Invalid,

    /// <summary>The engine wasn't reachable, or returned an unexpected status.</summary>
    Unavailable,
}

/// <summary>Result of <see cref="INeoReportsApiClient.TryGenerateQuerySqlAsync"/>. <see cref="Error"/> carries the engine's caller-safe message on <see cref="ApiQuerySqlOutcome.Invalid"/>.</summary>
public sealed record ApiQuerySqlResult(ApiQuerySqlOutcome Outcome, ApiGeneratedQuerySql? Data, string? Error);

/// <summary>A bounded sample of a visual query's result, from <c>POST /api/sources/{name}/query-preview</c> (ADR D49/K6).</summary>
public sealed record ApiQueryPreview(
    IReadOnlyList<ApiReportColumn> Columns, IReadOnlyList<object?[]> Rows, bool Truncated);

/// <summary>Result of <see cref="INeoReportsApiClient.TryPreviewQueryAsync"/> — shares the query-sql outcome mapping (404/422/400/unavailable).</summary>
public sealed record ApiQueryPreviewResult(ApiQuerySqlOutcome Outcome, ApiQueryPreview? Data, string? Error);

/// <summary>
/// Reads and drives NeoReports engine jobs/reports over its HTTP API (<c>MapNeoReports</c>). Every
/// call is best-effort: on a network error, timeout, or unexpected shape it logs and returns a
/// "not available" result so pages can fall back to sample data instead of failing.
/// </summary>
public interface INeoReportsApiClient
{
    /// <summary>Lists registered reports, or <c>null</c> if the engine API isn't reachable.</summary>
    Task<IReadOnlyList<ApiReportSummary>?> TryGetReportsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one job's status, or <c>null</c> if it doesn't exist or the API isn't reachable.</summary>
    Task<ApiJobView?> TryGetJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Lists jobs, newest first, or <c>null</c> if the engine API isn't reachable.</summary>
    /// <param name="report">Restrict to a single report name, or <c>null</c> for any.</param>
    /// <param name="since">Only jobs created at or after this instant, or <c>null</c> for any.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ApiJobView>?> TryListJobsAsync(
        string? report = null, DateTimeOffset? since = null, int? limit = null, string? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation of a running job. Returns whether the engine accepted the request.</summary>
    Task<bool> TryCancelJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a new async run of <paramref name="reportName"/>; returns the new job id, or
    /// <c>null</c> if the trigger failed.
    /// </summary>
    Task<string?> TryRunReportAsync(string reportName, CancellationToken cancellationToken = default);

    /// <summary>Builds the absolute download URL for a completed job's output.</summary>
    string BuildDownloadUrl(string jobId);

    /// <summary>Reads what the engine can build dynamic reports out of, or <c>null</c> if the engine API isn't reachable.</summary>
    Task<ApiCapabilities?> TryGetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Dry-run compiles a report config document. Returns <c>null</c> only when the engine API
    /// itself isn't reachable — a rejected/invalid config still returns a result with
    /// <see cref="ApiValidationResult.Valid"/> <c>false</c>.
    /// </summary>
    /// <param name="editingReportName">
    /// When set, validates the document as an *edit* of that report: the engine resolves any
    /// redaction placeholder (ADR D86) against its stored configuration first, so a config the user
    /// never touched does not fail validation on a value they were never shown.
    /// </param>
    Task<ApiValidationResult?> TryValidateReportAsync(
        string configJson, string? editingReportName = null, CancellationToken cancellationToken = default);

    /// <summary>Registers a report at runtime from a config document.</summary>
    Task<ApiCreateResult> TryCreateReportAsync(string configJson, CancellationToken cancellationToken = default);

    /// <summary>Replaces an existing config-origin report in place (<c>PUT /api/reports/{name}</c>, ADR D86).</summary>
    /// <param name="name">The report to replace.</param>
    /// <param name="configJson">The full replacement configuration document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ApiCreateResult> TryReplaceReportAsync(string name, string configJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// The report's stored configuration document, with credential-bearing values redacted
    /// (<c>GET /api/reports/{name}/config</c>, ADR D86), or <c>null</c> when the report has none —
    /// a code-registered report, or an unreachable engine.
    /// </summary>
    /// <param name="name">The report name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> TryGetReportConfigAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Removes a runtime-registered report. Returns whether the engine accepted the request.</summary>
    Task<bool> TryDeleteReportAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Reads the full definition of one report, or <c>null</c> if it doesn't exist or the API isn't reachable.</summary>
    Task<ApiReportDetail?> TryGetReportDetailAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a completed job's output files, or <c>null</c> if the job doesn't exist or the API
    /// isn't reachable. An empty (non-null) list means the job exists but isn't complete yet.
    /// </summary>
    Task<IReadOnlyList<ApiArtifact>?> TryGetJobArtifactsAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a job's recorded lifecycle events (ADR D38), or <c>null</c> if the job doesn't exist or
    /// the engine API isn't reachable. An empty (non-null) list means the job exists but either has
    /// no events yet, or no event store is registered on the host (indistinguishable from the wire —
    /// callers show a single honest "no events" state either way).
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="type">Optional exact-match filter on the event type.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ApiJobEvent>?> TryGetJobEventsAsync(
        string jobId, string? type = null, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>Reads the current process-level memory reading (ADR D39), or <c>null</c> if the engine API isn't reachable.</summary>
    Task<ApiMemory?> TryGetMemoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a Failed/Cancelled job's best-effort partial output (ADR D40), or <c>null</c> if the
    /// job doesn't exist or the API isn't reachable. Empty covers both "no partial-artifact store
    /// registered on this host" and "registered, but nothing was captured" — indistinguishable
    /// over the wire, so callers show one honest empty state either way.
    /// </summary>
    Task<IReadOnlyList<ApiArtifact>?> TryGetPartialArtifactsAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Builds the absolute download URL for a Failed/Cancelled job's partial output.</summary>
    string BuildPartialDownloadUrl(string jobId);

    /// <summary>
    /// Sets a runtime schedule override for a report (ADR D41), for either origin. Returns
    /// <c>false</c> on 404 (unknown report), 400 (invalid cron), 409 (no recurring scheduler
    /// registered on this host), or if the engine API isn't reachable.
    /// </summary>
    /// <param name="name">The report name.</param>
    /// <param name="cron">A 5-field cron expression, evaluated in UTC.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> TrySetScheduleAsync(string name, string cron, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a report's runtime schedule override (tombstones a declared schedule, or removes a
    /// prior override — ADR D41). Returns <c>false</c> on 404, 409, or if the engine API isn't reachable.
    /// </summary>
    /// <param name="name">The report name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> TryClearScheduleAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists registered sources (ADR D42), or <c>null</c> if the engine API isn't reachable.
    /// An empty (non-null) list means either no sources are registered or no registry is configured
    /// on this host — indistinguishable over the wire, so callers show one honest empty state either way.</summary>
    Task<IReadOnlyList<ApiSourceView>?> TryListSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one registered source's metadata (never its properties), or <c>null</c> if it doesn't exist or the API isn't reachable.</summary>
    Task<ApiSourceView?> TryGetSourceAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Registers a new source definition.</summary>
    Task<ApiSourceSaveResult> TryCreateSourceAsync(
        string name, string type, IReadOnlyDictionary<string, object?>? properties, string? description,
        CancellationToken cancellationToken = default);

    /// <summary>Fully replaces an existing source definition's type/properties/description.</summary>
    Task<ApiSourceSaveResult> TryReplaceSourceAsync(
        string name, string type, IReadOnlyDictionary<string, object?>? properties, string? description,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a source definition. Blocked (409) while any registered report still references it.</summary>
    Task<ApiSourceDeleteResult> TryDeleteSourceAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an on-demand health check for a registered source (ADR D42), or <c>null</c> if the
    /// engine API isn't reachable, the source doesn't exist (404), or no health check is
    /// registered for its type (422).
    /// </summary>
    Task<ApiSourceHealth?> TryCheckSourceHealthAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a bounded, read-only sample of one page of a report (ADR D45) — no output writing, no
    /// job record. <paramref name="filters"/> only take effect for a dynamic (config-registered)
    /// report whose source type has a registered filter translator; pass <c>null</c> or an empty
    /// list for an unfiltered sample. A closed set of operator names: "Equals", "NotEquals",
    /// "GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual", "Contains", "StartsWith".
    /// </summary>
    /// <param name="reportName">The report name.</param>
    /// <param name="filters">Structured filters to apply, or <c>null</c>/empty for an unfiltered sample.</param>
    /// <param name="pageSize">Requested sample size; capped server-side.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ApiPreviewResult> TryPreviewReportAsync(
        string reportName, IReadOnlyList<ApiPreviewFilter>? filters, int? pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Introspects a registered source's schema — its tables, columns (type/nullable/PK), and outbound
    /// foreign keys — via <c>GET /api/sources/{name}/catalog</c> (ADR D49). Returns <c>null</c> when the
    /// engine is unreachable, the source is unknown, or its type has no schema explorer (e.g. MongoDB).
    /// </summary>
    /// <param name="sourceName">The registered source's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ApiSchemaCatalog?> TryGetSourceCatalogAsync(string sourceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a bounded sample of one of a source's tables via <c>GET /api/sources/{name}/preview</c>
    /// (ADR D49); the row count is capped server-side. Returns <c>null</c> when the engine is
    /// unreachable, the source/table is unknown, or the source type has no schema explorer.
    /// </summary>
    /// <param name="sourceName">The registered source's name.</param>
    /// <param name="schema">The table's owning schema (may be empty).</param>
    /// <param name="table">The table name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ApiTablePreview?> TryPreviewSourceTableAsync(
        string sourceName, string schema, string table, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compiles a visually-composed query model into keyset-safe report SQL via
    /// <c>POST /api/sources/{name}/query-sql</c> (ADR D49, Pro). The model is passed as the raw JSON the
    /// builder serialized. The result distinguishes success from an unknown source (404), a host with no
    /// query-builder generator (422 — an MIT-only host), an invalid model (400), or an unreachable engine.
    /// </summary>
    /// <param name="sourceName">The registered source's name (its type selects the SQL dialect).</param>
    /// <param name="modelJson">The query model as JSON (the builder's serialized state).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ApiQuerySqlResult> TryGenerateQuerySqlAsync(
        string sourceName, string modelJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a bounded, read-only sample of a visually-composed query via
    /// <c>POST /api/sources/{name}/query-preview</c> (ADR D49, K6). The model is generated to SQL
    /// server-side (never raw), then one capped page is read. The result distinguishes success from an
    /// unknown source (404), a host with no query-builder generator (422), an invalid model (400), or an
    /// unreachable/erroring engine (including a 502 when the source's database can't be read).
    /// </summary>
    /// <param name="sourceName">The registered source's name (its type selects the SQL dialect).</param>
    /// <param name="modelJson">The query model as JSON (the builder's serialized state).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ApiQueryPreviewResult> TryPreviewQueryAsync(
        string sourceName, string modelJson, CancellationToken cancellationToken = default);
}

/// <summary>Locates the NeoReports engine API the UI calls.</summary>
public sealed class NeoReportsApiOptions
{
    /// <summary>
    /// Path the engine's endpoints are mapped under (see <c>MapNeoReports</c>). Resolved against the
    /// current request's scheme and host — independent of the UI's own mount path
    /// (<see cref="NeoReportsUIExtensions.UseNeoReportsUI"/>), since the two are separate route
    /// branches in the same host application.
    /// </summary>
    public string ApiPrefix { get; set; } = "/api";
}

internal sealed class NeoReportsApiClient(
    HttpClient http,
    NavigationManager navigation,
    IOptions<NeoReportsApiOptions> options,
    ILogger<NeoReportsApiClient> logger) : INeoReportsApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Uri ApiBase => new(
        new Uri(navigation.BaseUri).GetLeftPart(UriPartial.Authority) + options.Value.ApiPrefix.TrimEnd('/') + "/");

    public async Task<IReadOnlyList<ApiReportSummary>?> TryGetReportsAsync(CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            return await http.GetFromJsonAsync<IReadOnlyList<ApiReportSummary>>(
                new Uri(apiBase, "reports"), Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}reports unavailable; falling back to sample data.", Sanitize(apiBase.ToString()));
            return null;
        }
    }

    public async Task<ApiJobView?> TryGetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"jobs/{Uri.EscapeDataString(jobId)}"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ApiJobView>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}jobs/{JobId} unavailable; falling back to sample data.", Sanitize(apiBase.ToString()), Sanitize(jobId));
            return null;
        }
    }

    public async Task<IReadOnlyList<ApiJobView>?> TryListJobsAsync(
        string? report = null, DateTimeOffset? since = null, int? limit = null, string? status = null,
        CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            var query = new List<string>();
            if (!string.IsNullOrEmpty(report))
                query.Add($"report={Uri.EscapeDataString(report)}");
            if (since is not null)
                query.Add($"since={Uri.EscapeDataString(since.Value.ToString("O"))}");
            if (limit is not null)
                query.Add($"limit={limit.Value}");
            if (!string.IsNullOrEmpty(status))
                query.Add($"status={Uri.EscapeDataString(status)}");

            string queryString = query.Count > 0 ? "?" + string.Join("&", query) : "";
            return await http.GetFromJsonAsync<IReadOnlyList<ApiJobView>>(
                new Uri(apiBase, $"jobs{queryString}"), Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}jobs unavailable; falling back to sample data.", Sanitize(apiBase.ToString()));
            return null;
        }
    }

    public async Task<bool> TryCancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.PostAsync(
                new Uri(apiBase, $"jobs/{Uri.EscapeDataString(jobId)}/cancel"), content: null, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}jobs/{JobId}/cancel failed.", Sanitize(apiBase.ToString()), Sanitize(jobId));
            return false;
        }
    }

    public async Task<string?> TryRunReportAsync(string reportName, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.PostAsJsonAsync(
                new Uri(apiBase, $"reports/{Uri.EscapeDataString(reportName)}/run"),
                new RunRequestBody(null), Json, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken)
                .ConfigureAwait(false);
            return accepted.TryGetProperty("jobId", out var jobId) ? jobId.GetString() : null;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}reports/{ReportName}/run failed.", Sanitize(apiBase.ToString()), Sanitize(reportName));
            return null;
        }
    }

    public string BuildDownloadUrl(string jobId) => new Uri(ApiBase, $"jobs/{Uri.EscapeDataString(jobId)}/download").ToString();

    public string BuildPartialDownloadUrl(string jobId) =>
        new Uri(ApiBase, $"jobs/{Uri.EscapeDataString(jobId)}/partial-artifacts/download").ToString();

    public async Task<ApiCapabilities?> TryGetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            return await http.GetFromJsonAsync<ApiCapabilities>(
                new Uri(apiBase, "capabilities"), Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}capabilities unavailable; falling back to sample data.", Sanitize(apiBase.ToString()));
            return null;
        }
    }

    public async Task<ApiValidationResult?> TryValidateReportAsync(
        string configJson, string? editingReportName = null, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        string path = string.IsNullOrWhiteSpace(editingReportName)
            ? "reports/validate"
            : $"reports/validate?for={Uri.EscapeDataString(editingReportName)}";
        try
        {
            using var content = new StringContent(configJson, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(apiBase, path), content, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<ApiValidationResult>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}reports/validate failed.", Sanitize(apiBase.ToString()));
            return null;
        }
    }

    public async Task<ApiCreateResult> TryCreateReportAsync(string configJson, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var content = new StringContent(configJson, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(apiBase, "reports"), content, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
                string? name = body.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
                return new ApiCreateResult(ApiCreateOutcome.Created, name, null);
            }

            string? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            ApiCreateOutcome outcome = response.StatusCode switch
            {
                HttpStatusCode.Conflict => ApiCreateOutcome.NameTaken,
                HttpStatusCode.BadRequest => ApiCreateOutcome.Invalid,
                _ => ApiCreateOutcome.Unavailable,
            };
            return new ApiCreateResult(outcome, null, error);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}reports failed.", Sanitize(apiBase.ToString()));
            return new ApiCreateResult(ApiCreateOutcome.Unavailable, null, null);
        }
    }

    public async Task<ApiCreateResult> TryReplaceReportAsync(
        string name, string configJson, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var content = new StringContent(configJson, Encoding.UTF8, "application/json");
            using var response = await http.PutAsync(
                new Uri(apiBase, $"reports/{Uri.EscapeDataString(name)}"), content, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new ApiCreateResult(ApiCreateOutcome.Created, name, null);

            string? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            // A 409 here is not "name taken" (the name is this report's own) but "this report is
            // code-registered", which is a different message entirely — hence Invalid, not NameTaken.
            ApiCreateOutcome outcome = response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.Conflict => ApiCreateOutcome.Invalid,
                _ => ApiCreateOutcome.Unavailable,
            };
            return new ApiCreateResult(outcome, null, error);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "PUT {ApiBase}reports/{Name} failed.", Sanitize(apiBase.ToString()), Sanitize(name));
            return new ApiCreateResult(ApiCreateOutcome.Unavailable, null, null);
        }
    }

    public async Task<string?> TryGetReportConfigAsync(string name, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"reports/{Uri.EscapeDataString(name)}/config"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}reports/{Name}/config failed.", Sanitize(apiBase.ToString()), Sanitize(name));
            return null;
        }
    }

    public async Task<bool> TryDeleteReportAsync(string name, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.DeleteAsync(
                new Uri(apiBase, $"reports/{Uri.EscapeDataString(name)}"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "DELETE {ApiBase}reports/{Name} failed.", Sanitize(apiBase.ToString()), Sanitize(name));
            return false;
        }
    }

    public async Task<bool> TrySetScheduleAsync(string name, string cron, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.PutAsJsonAsync(
                new Uri(apiBase, $"reports/{Uri.EscapeDataString(name)}/schedule"), new { cron }, Json, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "PUT {ApiBase}reports/{Name}/schedule failed.", Sanitize(apiBase.ToString()), Sanitize(name));
            return false;
        }
    }

    public async Task<bool> TryClearScheduleAsync(string name, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.DeleteAsync(
                new Uri(apiBase, $"reports/{Uri.EscapeDataString(name)}/schedule"), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "DELETE {ApiBase}reports/{Name}/schedule failed.", Sanitize(apiBase.ToString()), Sanitize(name));
            return false;
        }
    }

    public async Task<ApiReportDetail?> TryGetReportDetailAsync(string name, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"reports/{Uri.EscapeDataString(name)}"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ApiReportDetail>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}reports/{Name} unavailable; falling back to sample data.", Sanitize(apiBase.ToString()), Sanitize(name));
            return null;
        }
    }

    public async Task<IReadOnlyList<ApiArtifact>?> TryGetJobArtifactsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"jobs/{Uri.EscapeDataString(jobId)}/artifacts"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IReadOnlyList<ApiArtifact>>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}jobs/{JobId}/artifacts unavailable; falling back to sample data.", Sanitize(apiBase.ToString()), Sanitize(jobId));
            return null;
        }
    }

    public async Task<IReadOnlyList<ApiJobEvent>?> TryGetJobEventsAsync(
        string jobId, string? type = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        var query = new List<string>();
        if (type is not null)
            query.Add($"type={Uri.EscapeDataString(type)}");
        if (limit is int l)
            query.Add($"limit={l}");
        var queryString = query.Count > 0 ? "?" + string.Join('&', query) : "";

        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"jobs/{Uri.EscapeDataString(jobId)}/events{queryString}"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IReadOnlyList<ApiJobEvent>>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}jobs/{JobId}/events unavailable; falling back to sample data.", Sanitize(apiBase.ToString()), Sanitize(jobId));
            return null;
        }
    }

    public async Task<ApiMemory?> TryGetMemoryAsync(CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            return await http.GetFromJsonAsync<ApiMemory>(
                new Uri(apiBase, "system/memory"), Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}system/memory unavailable.", Sanitize(apiBase.ToString()));
            return null;
        }
    }

    public async Task<IReadOnlyList<ApiArtifact>?> TryGetPartialArtifactsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"jobs/{Uri.EscapeDataString(jobId)}/partial-artifacts"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IReadOnlyList<ApiArtifact>>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}jobs/{JobId}/partial-artifacts unavailable.", Sanitize(apiBase.ToString()), Sanitize(jobId));
            return null;
        }
    }

    public async Task<IReadOnlyList<ApiSourceView>?> TryListSourcesAsync(CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            return await http.GetFromJsonAsync<IReadOnlyList<ApiSourceView>>(
                new Uri(apiBase, "sources"), Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}sources unavailable.", Sanitize(apiBase.ToString()));
            return null;
        }
    }

    public async Task<ApiSourceView?> TryGetSourceAsync(string name, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"sources/{Uri.EscapeDataString(name)}"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ApiSourceView>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}sources/{Name} unavailable.", Sanitize(apiBase.ToString()), Sanitize(name));
            return null;
        }
    }

    public Task<ApiSourceSaveResult> TryCreateSourceAsync(
        string name, string type, IReadOnlyDictionary<string, object?>? properties, string? description,
        CancellationToken cancellationToken = default) =>
        SaveSourceAsync(HttpMethod.Post, "sources", name, type, properties, description, cancellationToken);

    public Task<ApiSourceSaveResult> TryReplaceSourceAsync(
        string name, string type, IReadOnlyDictionary<string, object?>? properties, string? description,
        CancellationToken cancellationToken = default) =>
        SaveSourceAsync(HttpMethod.Put, $"sources/{Uri.EscapeDataString(name)}", name, type, properties, description, cancellationToken);

    private async Task<ApiSourceSaveResult> SaveSourceAsync(
        HttpMethod method, string path, string name, string type, IReadOnlyDictionary<string, object?>? properties,
        string? description, CancellationToken cancellationToken)
    {
        var apiBase = ApiBase;
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(apiBase, path))
            {
                Content = JsonContent.Create(new SourceRequestBody(name, type, properties, description), options: Json),
            };
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                return new ApiSourceSaveResult(ApiSourceSaveOutcome.Saved, null);

            string? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            ApiSourceSaveOutcome outcome = response.StatusCode switch
            {
                HttpStatusCode.Conflict => ApiSourceSaveOutcome.NameTaken,
                HttpStatusCode.BadRequest => ApiSourceSaveOutcome.Invalid,
                HttpStatusCode.NotFound => ApiSourceSaveOutcome.NotFound,
                _ => ApiSourceSaveOutcome.Unavailable,
            };
            return new ApiSourceSaveResult(outcome, error);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "{Method} {ApiBase}{Path} failed.", method, Sanitize(apiBase.ToString()), Sanitize(path));
            return new ApiSourceSaveResult(ApiSourceSaveOutcome.Unavailable, null);
        }
    }

    public async Task<ApiSourceDeleteResult> TryDeleteSourceAsync(string name, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.DeleteAsync(
                new Uri(apiBase, $"sources/{Uri.EscapeDataString(name)}"), cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NoContent)
                return new ApiSourceDeleteResult(ApiSourceDeleteOutcome.Deleted, null);

            string? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            ApiSourceDeleteOutcome outcome = response.StatusCode switch
            {
                HttpStatusCode.Conflict => ApiSourceDeleteOutcome.Referenced,
                HttpStatusCode.NotFound => ApiSourceDeleteOutcome.NotFound,
                _ => ApiSourceDeleteOutcome.Unavailable,
            };
            return new ApiSourceDeleteResult(outcome, error);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "DELETE {ApiBase}sources/{Name} failed.", Sanitize(apiBase.ToString()), Sanitize(name));
            return new ApiSourceDeleteResult(ApiSourceDeleteOutcome.Unavailable, null);
        }
    }

    public async Task<ApiSourceHealth?> TryCheckSourceHealthAsync(string name, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.PostAsync(
                new Uri(apiBase, $"sources/{Uri.EscapeDataString(name)}/health"), content: null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<ApiSourceHealth>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}sources/{Name}/health failed.", Sanitize(apiBase.ToString()), Sanitize(name));
            return null;
        }
    }

    public async Task<ApiPreviewResult> TryPreviewReportAsync(
        string reportName, IReadOnlyList<ApiPreviewFilter>? filters, int? pageSize, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.PostAsJsonAsync(
                new Uri(apiBase, $"reports/{Uri.EscapeDataString(reportName)}/preview"),
                new PreviewRequestBody(filters, pageSize), Json, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<ApiPreviewData>(Json, cancellationToken).ConfigureAwait(false);
                return new ApiPreviewResult(ApiPreviewOutcome.Ok, data, null);
            }

            string? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            ApiPreviewOutcome outcome = response.StatusCode switch
            {
                HttpStatusCode.NotFound => ApiPreviewOutcome.NotFound,
                HttpStatusCode.BadRequest => ApiPreviewOutcome.Invalid,
                _ => ApiPreviewOutcome.Unavailable,
            };
            return new ApiPreviewResult(outcome, null, error);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}reports/{ReportName}/preview failed.", Sanitize(apiBase.ToString()), Sanitize(reportName));
            return new ApiPreviewResult(ApiPreviewOutcome.Unavailable, null, null);
        }
    }

    public async Task<ApiSchemaCatalog?> TryGetSourceCatalogAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"sources/{Uri.EscapeDataString(sourceName)}/catalog"), cancellationToken).ConfigureAwait(false);
            // 404 (unknown source) and 422 (no explorer for the type) both mean "no catalog to show".
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<ApiSchemaCatalog>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}sources/{Source}/catalog unavailable.", Sanitize(apiBase.ToString()), Sanitize(sourceName));
            return null;
        }
    }

    public async Task<ApiTablePreview?> TryPreviewSourceTableAsync(
        string sourceName, string schema, string table, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        var query = $"schema={Uri.EscapeDataString(schema)}&table={Uri.EscapeDataString(table)}";
        try
        {
            using var response = await http.GetAsync(
                new Uri(apiBase, $"sources/{Uri.EscapeDataString(sourceName)}/preview?{query}"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<ApiTablePreview>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "GET {ApiBase}sources/{Source}/preview unavailable.", Sanitize(apiBase.ToString()), Sanitize(sourceName));
            return null;
        }
    }

    public async Task<ApiQuerySqlResult> TryGenerateQuerySqlAsync(
        string sourceName, string modelJson, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri(apiBase, $"sources/{Uri.EscapeDataString(sourceName)}/query-sql"))
            {
                Content = new StringContent(modelJson, Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<ApiGeneratedQuerySql>(Json, cancellationToken).ConfigureAwait(false);
                return new ApiQuerySqlResult(ApiQuerySqlOutcome.Ok, data, null);
            }

            string? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return new ApiQuerySqlResult(MapQueryOutcome(response.StatusCode), null, error);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}sources/{Source}/query-sql failed.", Sanitize(apiBase.ToString()), Sanitize(sourceName));
            return new ApiQuerySqlResult(ApiQuerySqlOutcome.Unavailable, null, null);
        }
    }

    // The query-builder endpoints (query-sql, query-preview) share the same honest status contract:
    // 404 unknown source, 422 no Pro generator (D36), 400 invalid model, anything else (incl. a 502
    // when the source's database can't be read) is "unavailable".
    private static ApiQuerySqlOutcome MapQueryOutcome(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => ApiQuerySqlOutcome.NotFound,
        HttpStatusCode.UnprocessableEntity => ApiQuerySqlOutcome.NotSupported,
        HttpStatusCode.BadRequest => ApiQuerySqlOutcome.Invalid,
        _ => ApiQuerySqlOutcome.Unavailable,
    };

    public async Task<ApiQueryPreviewResult> TryPreviewQueryAsync(
        string sourceName, string modelJson, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri(apiBase, $"sources/{Uri.EscapeDataString(sourceName)}/query-preview"))
            {
                Content = new StringContent(modelJson, Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<ApiQueryPreview>(Json, cancellationToken).ConfigureAwait(false);
                return new ApiQueryPreviewResult(ApiQuerySqlOutcome.Ok, data, null);
            }

            string? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return new ApiQueryPreviewResult(MapQueryOutcome(response.StatusCode), null, error);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}sources/{Source}/query-preview failed.", Sanitize(apiBase.ToString()), Sanitize(sourceName));
            return new ApiQueryPreviewResult(ApiQuerySqlOutcome.Unavailable, null, null);
        }
    }

    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
            return body.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or JsonException or TaskCanceledException;

    /// <summary>Strips CR/LF from a value before it reaches a log message template — jobId/reportName
    /// ultimately originate from the URL or an HTTP response, so an unsanitized value could forge
    /// extra log lines (CWE-117).</summary>
    private static string Sanitize(string value) => value.Replace('\r', '_').Replace('\n', '_');

    private sealed record RunRequestBody(object? Parameters);

    private sealed record PreviewRequestBody(IReadOnlyList<ApiPreviewFilter>? Filters, int? PageSize);

    private sealed record SourceRequestBody(
        string Name, string Type, IReadOnlyDictionary<string, object?>? Properties, string? Description);
}
