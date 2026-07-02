using System.Net;
using System.Net.Http.Json;
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

    /// <summary>Requests cancellation of a running job. Returns whether the engine accepted the request.</summary>
    Task<bool> TryCancelJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a new async run of <paramref name="reportName"/>; returns the new job id, or
    /// <c>null</c> if the trigger failed.
    /// </summary>
    Task<string?> TryRunReportAsync(string reportName, CancellationToken cancellationToken = default);

    /// <summary>Builds the absolute download URL for a completed job's output.</summary>
    string BuildDownloadUrl(string jobId);
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
            logger.LogWarning(ex, "GET {ApiBase}reports unavailable; falling back to sample data.", apiBase);
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
            logger.LogWarning(ex, "GET {ApiBase}jobs/{JobId} unavailable; falling back to sample data.", apiBase, jobId);
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
            logger.LogWarning(ex, "POST {ApiBase}jobs/{JobId}/cancel failed.", apiBase, jobId);
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
                new { parameters = (object?)null }, Json, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken)
                .ConfigureAwait(false);
            return accepted.TryGetProperty("jobId", out var jobId) ? jobId.GetString() : null;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            logger.LogWarning(ex, "POST {ApiBase}reports/{ReportName}/run failed.", apiBase, reportName);
            return null;
        }
    }

    public string BuildDownloadUrl(string jobId) => new Uri(ApiBase, $"jobs/{Uri.EscapeDataString(jobId)}/download").ToString();

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or JsonException or TaskCanceledException;
}
