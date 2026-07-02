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

/// <summary>Aggregate counters for a job, mirroring <c>NeoReports.Abstractions.JobStats</c>.</summary>
public sealed record ApiJobStats(long RecordsRead, long RecordsWritten, long BytesWritten, int Retries, int BatchesProcessed);

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
    IReadOnlyList<string> Sources, IReadOnlyList<string> Formats, IReadOnlyList<string> Destinations);

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
        string? report = null, DateTimeOffset? since = null, int? limit = null, CancellationToken cancellationToken = default);

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
    Task<ApiValidationResult?> TryValidateReportAsync(string configJson, CancellationToken cancellationToken = default);

    /// <summary>Registers a report at runtime from a config document.</summary>
    Task<ApiCreateResult> TryCreateReportAsync(string configJson, CancellationToken cancellationToken = default);

    /// <summary>Removes a runtime-registered report. Returns whether the engine accepted the request.</summary>
    Task<bool> TryDeleteReportAsync(string name, CancellationToken cancellationToken = default);
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
        string? report = null, DateTimeOffset? since = null, int? limit = null, CancellationToken cancellationToken = default)
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

    public async Task<ApiValidationResult?> TryValidateReportAsync(string configJson, CancellationToken cancellationToken = default)
    {
        var apiBase = ApiBase;
        try
        {
            using var content = new StringContent(configJson, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(apiBase, "reports/validate"), content, cancellationToken)
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
}
