using System.Net.Http.Json;
using System.Text;
using Shouldly;

namespace NeoReports.WebUi.E2ETests;

/// <summary>
/// Thin client for the engine's own HTTP API, used by the E2E tests to set a scenario up and to verify
/// what a UI action really produced. The UI drives the behaviour under test; this checks the result
/// against the engine rather than against the screen that just rendered it.
/// </summary>
public sealed class ReportApi : IDisposable
{
    private readonly HttpClient _client = new();
    private readonly string _baseUrl;

    /// <summary>Creates a client for the running app.</summary>
    public ReportApi(WebUiApp app) => _baseUrl = app.BaseUrl;

    /// <summary>One column of a report's schema.</summary>
    public readonly record struct Column(string Name, string Type);

    /// <summary>Registers a dynamic report backed by the in-memory sample source.</summary>
    /// <param name="name">Report name.</param>
    /// <param name="columns">Schema columns; the source synthesises a value per type.</param>
    /// <param name="formats">Output formats (e.g. csv, xlsx).</param>
    /// <param name="rows">How many rows the source should yield.</param>
    /// <param name="pageSize">Batch size, so a scenario can force several pages.</param>
    /// <param name="withDestination">Whether to attach the local destination.</param>
    public async Task RegisterAsync(
        string name,
        IReadOnlyList<Column> columns,
        IReadOnlyList<string> formats,
        int rows = 25,
        int pageSize = 10,
        bool withDestination = true)
    {
        // Built with plain interpolation rather than raw strings: the JSON is brace-dense, and raw
        // interpolated literals need the `$` count to out-number every run of closing braces.
        string cols = string.Join(",", columns.Select(c => "{\"name\":\"" + c.Name + "\",\"type\":\"" + c.Type + "\"}"));
        string outs = string.Join(",", formats.Select(f => "{\"format\":\"" + f + "\"}"));
        string dests = withDestination ? ",\"destinations\":[{\"type\":\"local\"}]" : string.Empty;
        string config =
            "{\"name\":\"" + name + "\"," +
            "\"source\":{\"type\":\"inmemory\",\"properties\":{\"rows\":" + rows + "}}," +
            "\"columns\":[" + cols + "],\"outputs\":[" + outs + "]" + dests + "," +
            "\"pageSize\":" + pageSize + "}";

        using var body = new StringContent(config, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _client.PostAsync(_baseUrl + "/api/reports", body);
        response.IsSuccessStatusCode.ShouldBeTrue(
            $"registering '{name}' failed: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Triggers a run and returns once its job reaches a terminal state.</summary>
    public async Task<Job> RunToCompletionAsync(string name)
    {
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _client.PostAsync($"{_baseUrl}/api/reports/{name}/run", body);
        response.IsSuccessStatusCode.ShouldBeTrue($"running '{name}' failed: {response.StatusCode}");

        // Poll the id the API just handed back rather than searching the job list by report name:
        // exact, and immune to another run of the same report existing.
        Accepted accepted = (await response.Content.ReadFromJsonAsync<Accepted>())!;
        return await WaitForCompletionAsync(accepted.JobId);
    }

    /// <summary>Polls one job until it reaches a terminal state, failing loudly on anything but success.</summary>
    public async Task<Job> WaitForCompletionAsync(string jobId)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            Job job = (await _client.GetFromJsonAsync<Job>($"{_baseUrl}/api/jobs/{jobId}"))!;
            switch (job.Status)
            {
                case "Completed":
                    return job;
                // Cancelled is terminal too; without this the poll would burn its whole budget and
                // then report a timeout instead of what actually happened.
                case "Failed":
                case "Cancelled":
                    throw new Xunit.Sdk.XunitException($"Job {jobId} ended as {job.Status}: {job.Error}");
                default:
                    await Task.Delay(500);
                    break;
            }
        }

        throw new Xunit.Sdk.XunitException($"Job {jobId} did not finish within 30s.");
    }

    /// <summary>
    /// Polls until the named report has a completed job. Used when the run was started through the
    /// UI, so there is no job id to poll — prefer <see cref="WaitForCompletionAsync(string)"/> when
    /// the run was triggered here.
    /// </summary>
    public async Task<Job> WaitForReportCompletionAsync(string reportName)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var jobs = await _client.GetFromJsonAsync<List<Job>>(_baseUrl + "/api/jobs?limit=100");
            Job? job = jobs!.FirstOrDefault(j => j.ReportName == reportName);
            if (job is { Status: "Completed" })
                return job;
            if (job is { Status: "Failed" or "Cancelled" })
                throw new Xunit.Sdk.XunitException($"Report '{reportName}' ended as {job.Status}: {job.Error}");
            await Task.Delay(500);
        }

        throw new Xunit.Sdk.XunitException($"Report '{reportName}' did not complete within 30s.");
    }

    /// <summary>The artifacts a completed job produced.</summary>
    public async Task<IReadOnlyList<Artifact>> ArtifactsAsync(string jobId) =>
        (await _client.GetFromJsonAsync<List<Artifact>>($"{_baseUrl}/api/jobs/{jobId}/artifacts"))!;

    /// <summary>
    /// Downloads what a user gets from the job: the file itself for a single-output run, and a zip of
    /// every file when the run produced several (there is no per-artifact route).
    /// </summary>
    public Task<byte[]> DownloadAsync(string jobId) =>
        _client.GetByteArrayAsync($"{_baseUrl}/api/jobs/{jobId}/download");

    /// <summary>Every registered report.</summary>
    public async Task<IReadOnlyList<Report>> ReportsAsync() =>
        (await _client.GetFromJsonAsync<List<Report>>(_baseUrl + "/api/reports"))!;

    /// <summary>A registered report as the engine sees it.</summary>
    public sealed record Report(
        string Name, IReadOnlyList<string> Formats, IReadOnlyList<string> Columns, IReadOnlyList<string> Destinations);

    /// <summary>The run endpoint's 202 body.</summary>
    private sealed record Accepted(string JobId);

    /// <summary>A job as the engine sees it.</summary>
    public sealed record Job(string Id, string ReportName, string Status, string? Error);

    /// <summary>One produced file.</summary>
    public sealed record Artifact(string FileName);

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
