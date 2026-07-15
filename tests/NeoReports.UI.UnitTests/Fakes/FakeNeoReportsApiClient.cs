using NeoReports.UI.Services;

namespace NeoReports.UI.UnitTests.Fakes;

/// <summary>
/// Hand-written <see cref="INeoReportsApiClient"/> test double for bUnit component tests. Every
/// member defaults to the engine's own "unreachable" shape (null / false), matching the real
/// client's contract, so a test only has to set up what its scenario actually needs. Each method
/// also records its last call arguments (e.g. <see cref="LastPreviewFilters"/>) so tests can assert
/// on what a page actually sent, without a mocking framework — following the plain field-backed
/// fake style already used under tests/NeoReports.Core.UnitTests/Fakes.
/// </summary>
public sealed class FakeNeoReportsApiClient : INeoReportsApiClient
{
    public Func<CancellationToken, Task<IReadOnlyList<ApiReportSummary>?>> Reports { get; set; } = _ => Task.FromResult<IReadOnlyList<ApiReportSummary>?>(null);
    public Func<string, CancellationToken, Task<ApiJobView?>> Job { get; set; } = (_, _) => Task.FromResult<ApiJobView?>(null);
    public Func<string?, DateTimeOffset?, int?, string?, CancellationToken, Task<IReadOnlyList<ApiJobView>?>> Jobs { get; set; } =
        (_, _, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobView>?>(null);
    public Func<string, CancellationToken, Task<bool>> CancelJob { get; set; } = (_, _) => Task.FromResult(false);
    public Func<string, CancellationToken, Task<string?>> RunReport { get; set; } = (_, _) => Task.FromResult<string?>(null);
    public Func<string, string> DownloadUrl { get; set; } = id => $"/api/jobs/{id}/download";
    public Func<CancellationToken, Task<ApiCapabilities?>> Capabilities { get; set; } = _ => Task.FromResult<ApiCapabilities?>(null);
    public Func<string, CancellationToken, Task<ApiValidationResult?>> ValidateReport { get; set; } = (_, _) => Task.FromResult<ApiValidationResult?>(null);
    public Func<string, CancellationToken, Task<ApiCreateResult>> CreateReport { get; set; } =
        (_, _) => Task.FromResult(new ApiCreateResult(ApiCreateOutcome.Unavailable, null, null));
    public Func<string, CancellationToken, Task<bool>> DeleteReport { get; set; } = (_, _) => Task.FromResult(false);
    public Func<string, CancellationToken, Task<ApiReportDetail?>> ReportDetail { get; set; } = (_, _) => Task.FromResult<ApiReportDetail?>(null);
    public Func<string, CancellationToken, Task<IReadOnlyList<ApiArtifact>?>> JobArtifacts { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>(null);
    public Func<string, string?, int?, CancellationToken, Task<IReadOnlyList<ApiJobEvent>?>> JobEvents { get; set; } =
        (_, _, _, _) => Task.FromResult<IReadOnlyList<ApiJobEvent>?>(null);
    public Func<CancellationToken, Task<ApiMemory?>> Memory { get; set; } = _ => Task.FromResult<ApiMemory?>(null);
    public Func<string, CancellationToken, Task<IReadOnlyList<ApiArtifact>?>> PartialArtifacts { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<ApiArtifact>?>(null);
    public Func<string, string> PartialDownloadUrl { get; set; } = id => $"/api/jobs/{id}/partial-download";
    public Func<string, string, CancellationToken, Task<bool>> SetSchedule { get; set; } = (_, _, _) => Task.FromResult(false);
    public Func<string, CancellationToken, Task<bool>> ClearSchedule { get; set; } = (_, _) => Task.FromResult(false);
    public Func<CancellationToken, Task<IReadOnlyList<ApiSourceView>?>> Sources { get; set; } = _ => Task.FromResult<IReadOnlyList<ApiSourceView>?>(null);
    public Func<string, CancellationToken, Task<ApiSourceView?>> Source { get; set; } = (_, _) => Task.FromResult<ApiSourceView?>(null);
    public Func<string, string, IReadOnlyDictionary<string, object?>?, string?, CancellationToken, Task<ApiSourceSaveResult>> CreateSource { get; set; } =
        (_, _, _, _, _) => Task.FromResult(new ApiSourceSaveResult(ApiSourceSaveOutcome.Unavailable, null));
    public Func<string, string, IReadOnlyDictionary<string, object?>?, string?, CancellationToken, Task<ApiSourceSaveResult>> ReplaceSource { get; set; } =
        (_, _, _, _, _) => Task.FromResult(new ApiSourceSaveResult(ApiSourceSaveOutcome.Unavailable, null));
    public Func<string, CancellationToken, Task<ApiSourceDeleteResult>> DeleteSource { get; set; } =
        (_, _) => Task.FromResult(new ApiSourceDeleteResult(ApiSourceDeleteOutcome.Unavailable, null));
    public Func<string, CancellationToken, Task<ApiSourceHealth?>> CheckSourceHealth { get; set; } = (_, _) => Task.FromResult<ApiSourceHealth?>(null);
    public Func<string, IReadOnlyList<ApiPreviewFilter>?, int?, CancellationToken, Task<ApiPreviewResult>> Preview { get; set; } =
        (_, _, _, _) => Task.FromResult(new ApiPreviewResult(ApiPreviewOutcome.Unavailable, null, null));

    public IReadOnlyList<ApiPreviewFilter>? LastPreviewFilters { get; private set; }
    public int? LastPreviewPageSize { get; private set; }
    public string? LastRunReportName { get; private set; }
    public string? LastDeletedReportName { get; private set; }
    public string? LastCreateReportConfigJson { get; private set; }
    public string? LastDeletedSourceName { get; private set; }
    public (string Name, string Cron)? LastSetSchedule { get; private set; }

    public Task<IReadOnlyList<ApiReportSummary>?> TryGetReportsAsync(CancellationToken cancellationToken = default) => Reports(cancellationToken);

    public Task<ApiJobView?> TryGetJobAsync(string jobId, CancellationToken cancellationToken = default) => Job(jobId, cancellationToken);

    public Task<IReadOnlyList<ApiJobView>?> TryListJobsAsync(
        string? report = null, DateTimeOffset? since = null, int? limit = null, string? status = null,
        CancellationToken cancellationToken = default) => Jobs(report, since, limit, status, cancellationToken);

    public Task<bool> TryCancelJobAsync(string jobId, CancellationToken cancellationToken = default) => CancelJob(jobId, cancellationToken);

    public Task<string?> TryRunReportAsync(string reportName, CancellationToken cancellationToken = default)
    {
        LastRunReportName = reportName;
        return RunReport(reportName, cancellationToken);
    }

    public string BuildDownloadUrl(string jobId) => DownloadUrl(jobId);

    public Task<ApiCapabilities?> TryGetCapabilitiesAsync(CancellationToken cancellationToken = default) => Capabilities(cancellationToken);

    public Task<ApiValidationResult?> TryValidateReportAsync(string configJson, CancellationToken cancellationToken = default) =>
        ValidateReport(configJson, cancellationToken);

    public Task<ApiCreateResult> TryCreateReportAsync(string configJson, CancellationToken cancellationToken = default)
    {
        LastCreateReportConfigJson = configJson;
        return CreateReport(configJson, cancellationToken);
    }

    public Task<bool> TryDeleteReportAsync(string name, CancellationToken cancellationToken = default)
    {
        LastDeletedReportName = name;
        return DeleteReport(name, cancellationToken);
    }

    public Task<ApiReportDetail?> TryGetReportDetailAsync(string name, CancellationToken cancellationToken = default) => ReportDetail(name, cancellationToken);

    public Task<IReadOnlyList<ApiArtifact>?> TryGetJobArtifactsAsync(string jobId, CancellationToken cancellationToken = default) =>
        JobArtifacts(jobId, cancellationToken);

    public Task<IReadOnlyList<ApiJobEvent>?> TryGetJobEventsAsync(
        string jobId, string? type = null, int? limit = null, CancellationToken cancellationToken = default) =>
        JobEvents(jobId, type, limit, cancellationToken);

    public Task<ApiMemory?> TryGetMemoryAsync(CancellationToken cancellationToken = default) => Memory(cancellationToken);

    public Task<IReadOnlyList<ApiArtifact>?> TryGetPartialArtifactsAsync(string jobId, CancellationToken cancellationToken = default) =>
        PartialArtifacts(jobId, cancellationToken);

    public string BuildPartialDownloadUrl(string jobId) => PartialDownloadUrl(jobId);

    public Task<bool> TrySetScheduleAsync(string name, string cron, CancellationToken cancellationToken = default)
    {
        LastSetSchedule = (name, cron);
        return SetSchedule(name, cron, cancellationToken);
    }

    public Task<bool> TryClearScheduleAsync(string name, CancellationToken cancellationToken = default) => ClearSchedule(name, cancellationToken);

    public Task<IReadOnlyList<ApiSourceView>?> TryListSourcesAsync(CancellationToken cancellationToken = default) => Sources(cancellationToken);

    public Task<ApiSourceView?> TryGetSourceAsync(string name, CancellationToken cancellationToken = default) => Source(name, cancellationToken);

    public Task<ApiSourceSaveResult> TryCreateSourceAsync(
        string name, string type, IReadOnlyDictionary<string, object?>? properties, string? description,
        CancellationToken cancellationToken = default) => CreateSource(name, type, properties, description, cancellationToken);

    public Task<ApiSourceSaveResult> TryReplaceSourceAsync(
        string name, string type, IReadOnlyDictionary<string, object?>? properties, string? description,
        CancellationToken cancellationToken = default) => ReplaceSource(name, type, properties, description, cancellationToken);

    public Task<ApiSourceDeleteResult> TryDeleteSourceAsync(string name, CancellationToken cancellationToken = default)
    {
        LastDeletedSourceName = name;
        return DeleteSource(name, cancellationToken);
    }

    public Task<ApiSourceHealth?> TryCheckSourceHealthAsync(string name, CancellationToken cancellationToken = default) =>
        CheckSourceHealth(name, cancellationToken);

    public Task<ApiPreviewResult> TryPreviewReportAsync(
        string reportName, IReadOnlyList<ApiPreviewFilter>? filters, int? pageSize, CancellationToken cancellationToken = default)
    {
        LastPreviewFilters = filters;
        LastPreviewPageSize = pageSize;
        return Preview(reportName, filters, pageSize, cancellationToken);
    }
}
