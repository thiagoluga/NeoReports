using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Artifacts;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;

namespace NeoReports.AspNetCore;

/// <summary>Maps the NeoReports HTTP endpoints (trigger, status, cancel, download).</summary>
public static class NeoReportsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the NeoReports API under <paramref name="prefix"/>:
    /// <list type="bullet">
    /// <item><c>POST {prefix}/reports/{name}/run</c> — async (202 + jobId) or <c>?mode=sync</c> (streams a single output)</item>
    /// <item><c>GET  {prefix}/reports</c> — list registered reports</item>
    /// <item><c>GET  {prefix}/jobs/{id}</c> — job status + stats</item>
    /// <item><c>POST {prefix}/jobs/{id}/cancel</c> — request cancellation</item>
    /// <item><c>GET  {prefix}/jobs/{id}/download</c> — download the finished result</item>
    /// </list>
    /// Authorization is inherited from the host; set <see cref="NeoReportsEndpointOptions.RequireAuthorization"/>
    /// to apply <c>RequireAuthorization</c> to the group.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">URL prefix (default <c>/api</c>).</param>
    /// <param name="configure">Optional options callback.</param>
    /// <returns>The route group, for further customization.</returns>
    public static RouteGroupBuilder MapNeoReports(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/api",
        Action<NeoReportsEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = new NeoReportsEndpointOptions();
        configure?.Invoke(options);

        var group = endpoints.MapGroup(prefix);
        if (options.RequireAuthorization)
        {
            if (string.IsNullOrEmpty(options.AuthorizationPolicy))
                group.RequireAuthorization();
            else
                group.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapPost("/reports/{name}/run", RunReportAsync);
        group.MapGet("/reports", ListReports);
        group.MapGet("/jobs/{id}", GetJobAsync);
        group.MapPost("/jobs/{id}/cancel", CancelJobAsync);
        group.MapGet("/jobs/{id}/download", DownloadAsync);

        return group;
    }

    private static async Task<IResult> RunReportAsync(
        string name,
        string? mode,
        RunReportRequest? body,
        IReportRegistry registry,
        IReportJobScheduler scheduler,
        IReportRunner runner,
        IReportArtifactStore artifactStore,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var report = registry.Find(name);
        if (report is null)
            return Results.NotFound(new { error = $"No report named '{name}' is registered." });

        var parameters = body?.Parameters;

        if (string.Equals(mode, "sync", StringComparison.OrdinalIgnoreCase))
        {
            // Sync streams a single file in the response; multi-output cannot be streamed (CA-10).
            if (report.OutputCount != 1)
                return Results.BadRequest(new
                {
                    error = "Synchronous mode supports single-output reports only. " +
                            $"Report '{name}' has {report.OutputCount} outputs; use async mode and download.",
                });

            var jobId = Guid.NewGuid().ToString("N");
            var result = await runner.RunAsync(name, parameters, jobId, cancellationToken).ConfigureAwait(false);
            if (result.Status == ReportRunStatus.Failed)
            {
                await artifactStore.DeleteAsync(jobId, CancellationToken.None).ConfigureAwait(false);
                return Results.Problem(
                    title: "Report run failed.", detail: result.Error, statusCode: StatusCodes.Status500InternalServerError);
            }

            var artifacts = await artifactStore.ListAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (artifacts.Count == 0)
                return Results.Problem(
                    title: "Report produced no output.", statusCode: StatusCodes.Status500InternalServerError);

            var artifact = artifacts[0];
            // Stream the file by path (ASP.NET opens and disposes it), then delete the stored copy
            // once the response finishes.
            http.Response.OnCompleted(async () => await artifactStore.DeleteAsync(jobId, CancellationToken.None).ConfigureAwait(false));
            return Results.File(artifact.Path, artifact.MimeType, artifact.FileName);
        }

        var enqueuedId = await scheduler.EnqueueAsync(
            new ReportJobRequest(name, parameters), cancellationToken).ConfigureAwait(false);
        return Results.Accepted(
            $"{http.Request.PathBase}/api/jobs/{enqueuedId}",
            new RunAcceptedResponse(enqueuedId, ReportJobStatus.Queued));
    }

    private static IResult ListReports(IReportRegistry registry)
    {
        var reports = registry.Reports
            .Select(r => new ReportSummary(r.Name, r.OutputCount, r.Schema.Columns.Select(c => c.Name).ToArray()))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToArray();
        return Results.Ok(reports);
    }

    private static async Task<IResult> GetJobAsync(
        string id, IReportJobScheduler scheduler, CancellationToken cancellationToken)
    {
        var job = await scheduler.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return job is null
            ? Results.NotFound(new { error = $"No job with id '{id}'." })
            : Results.Ok(JobView.From(job));
    }

    private static async Task<IResult> CancelJobAsync(
        string id, IReportJobScheduler scheduler, CancellationToken cancellationToken)
    {
        var job = await scheduler.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return Results.NotFound(new { error = $"No job with id '{id}'." });

        var accepted = await scheduler.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        return accepted
            ? Results.Accepted()
            : Results.Conflict(new { error = $"Job '{id}' is not in a cancellable state (status: {job.Status})." });
    }

    private static async Task<IResult> DownloadAsync(
        string id,
        IReportJobScheduler scheduler,
        IReportArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        var job = await scheduler.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return Results.NotFound(new { error = $"No job with id '{id}'." });

        if (job.Status is not (ReportJobStatus.Completed))
            return Results.Conflict(new { error = $"Job '{id}' is not complete (status: {job.Status})." });

        var artifacts = await artifactStore.ListAsync(id, cancellationToken).ConfigureAwait(false);
        if (artifacts.Count == 0)
            return Results.NotFound(new { error = $"No artifacts stored for job '{id}'." });

        if (artifacts.Count == 1)
        {
            // Stream by path so ASP.NET owns the file handle's lifetime.
            var single = artifacts[0];
            return Results.File(single.Path, single.MimeType, single.FileName);
        }

        // Multiple outputs: bundle into a zip so a single download carries them all. The
        // MemoryStream is handed to Results.File, which disposes it after writing the response.
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var artifact in artifacts)
            {
                var entry = archive.CreateEntry(artifact.FileName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fileStream = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }
        }

        zip.Position = 0;
        return Results.File(zip, "application/zip", $"{job.ReportName}-{id}.zip");
    }
}
