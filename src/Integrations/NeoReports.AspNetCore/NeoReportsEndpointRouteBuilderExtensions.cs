using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core;
using NeoReports.Core.Artifacts;
using NeoReports.Core.Configuration;
using NeoReports.Core.Events;
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
    /// <item><c>GET  {prefix}/reports/{name}</c> — full report definition (columns, formats, destinations, retry/failure strategy, origin)</item>
    /// <item><c>POST {prefix}/reports</c> — register a report at runtime from a config document (ADR D33)</item>
    /// <item><c>POST {prefix}/reports/validate</c> — dry-run compile a config document; never registers or persists</item>
    /// <item><c>DELETE {prefix}/reports/{name}</c> — remove a runtime-registered report (code-first reports return 409)</item>
    /// <item><c>GET  {prefix}/capabilities</c> — source/format/destination type ids the host has registered</item>
    /// <item><c>GET  {prefix}/jobs</c> — list jobs, filterable by status/report/since, paged</item>
    /// <item><c>GET  {prefix}/jobs/{id}</c> — job status + stats</item>
    /// <item><c>POST {prefix}/jobs/{id}/cancel</c> — request cancellation</item>
    /// <item><c>GET  {prefix}/jobs/{id}/download</c> — download the finished result</item>
    /// <item><c>GET  {prefix}/jobs/{id}/artifacts</c> — list finished output files (name/mime/size, never the on-disk path)</item>
    /// <item><c>GET  {prefix}/jobs/{id}/events</c> — structured per-job lifecycle events (ADR D38); <c>[]</c> when no event store is registered</item>
    /// <item><c>GET  {prefix}/system/memory</c> — process-level memory reading + running-job count (ADR D39)</item>
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
        group.MapGet("/reports/{name}", GetReportDetailAsync);
        group.MapPost("/reports", CreateReportAsync);
        group.MapPost("/reports/validate", ValidateReportAsync);
        group.MapDelete("/reports/{name}", DeleteReportAsync);
        group.MapGet("/capabilities", GetCapabilities);
        group.MapGet("/jobs", ListJobsAsync);
        group.MapGet("/jobs/{id}", GetJobAsync);
        group.MapPost("/jobs/{id}/cancel", CancelJobAsync);
        group.MapGet("/jobs/{id}/download", DownloadAsync);
        group.MapGet("/jobs/{id}/artifacts", GetJobArtifactsAsync);
        group.MapGet("/jobs/{id}/events", GetJobEventsAsync);
        group.MapGet("/system/memory", GetMemoryAsync);

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
            .Select(r => new ReportSummary(
                r.Name, r.OutputCount, r.Schema.Columns.Select(c => c.Name).ToArray(), r.OutputFormats, r.DestinationTypes))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToArray();
        return Results.Ok(reports);
    }

    private static async Task<IResult> GetReportDetailAsync(
        string name, HttpContext http, [FromServices] IReportRegistry registry, CancellationToken cancellationToken)
    {
        CompiledReport? report = registry.Find(name);
        if (report is null)
            return Results.NotFound(new { error = $"No report named '{name}' is registered." });

        // The config store is optional: hosts that never call AddDynamicReports() have no
        // IReportConfigStore registered, and every report is origin "code" there.
        IReportConfigStore? configStore = http.RequestServices.GetService<IReportConfigStore>();
        bool isConfigOrigin = configStore is not null
            && DynamicReportName.IsValid(name)
            && await configStore.ExistsAsync(name, cancellationToken).ConfigureAwait(false);

        ReportColumnView[] columns = report.Schema.Columns
            .Select(c => new ReportColumnView(c.Name, c.Type.ToString(), c.DisplayName, c.Format, c.Nullable))
            .ToArray();

        var detail = new ReportDetailView(
            Name: report.Name,
            Columns: columns,
            PageSize: report.PageSize,
            Formats: report.OutputFormats,
            Destinations: report.DestinationTypes,
            FailureStrategy: report.FailureStrategy.Name,
            RetryMaxAttempts: report.Retry.Attempts,
            RetryBackoff: report.Retry.Backoff.ToString(),
            RetryBaseDelaySeconds: report.Retry.BaseDelay.TotalSeconds,
            RetryUseJitter: report.Retry.UseJitter,
            Origin: isConfigOrigin ? "config" : "code",
            Deletable: isConfigOrigin,
            AbortAfterConsecutiveFailures: report.AbortThresholds?.ConsecutiveFailures,
            AbortAfterTotalFailures: report.AbortThresholds?.TotalFailures,
            AbortAtFailureRate: report.AbortThresholds?.FailureRate);

        return Results.Ok(detail);
    }

    private static async Task<IResult> CreateReportAsync(
        HttpContext http,
        [FromServices] IMutableReportRegistry registry,
        [FromServices] IReportConfigStore configStore,
        CancellationToken cancellationToken)
    {
        string document = await ReadBodyAsync(http, cancellationToken).ConfigureAwait(false);

        ReportConfig config;
        try
        {
            config = new JsonReportConfigParser().Parse(document);
        }
        catch (ConfigurationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (!DynamicReportName.IsValid(config.Name))
        {
            return Results.BadRequest(new
            {
                error = $"'{config.Name}' is not a valid report name. Names must match {DynamicReportName.Pattern}.",
            });
        }

        if (registry.Contains(config.Name))
            return Results.Conflict(new { error = $"A report named '{config.Name}' already exists." });

        CompiledReport compiled;
        try
        {
            ReportConfig substituted = ReportConfigEnvironment.Substitute(config);
            compiled = ReportConfigCompiler.Compile(substituted, http.RequestServices);
        }
        catch (ConfigurationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        try
        {
            registry.Register(compiled);
        }
        catch (ConfigurationException ex)
        {
            // A concurrent request registered the same name between the Contains check above and
            // here; the registry's own duplicate-name guard is the source of truth.
            return Results.Conflict(new { error = ex.Message });
        }

        try
        {
            // The store persists the ORIGINAL document (with any ${VAR} placeholders unresolved),
            // never the substituted one — a secret must never reach disk.
            await configStore.SaveAsync(config.Name, document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            registry.Unregister(config.Name);
            return Results.Problem(
                title: "Failed to persist the dynamic report.",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var columns = compiled.Schema.Columns.Select(c => c.Name).ToArray();
        return Results.Created(
            $"{http.Request.PathBase}/api/reports/{config.Name}", new ReportCreatedResponse(config.Name, columns));
    }

    private static async Task<IResult> ValidateReportAsync(
        HttpContext http, [FromServices] IReportRegistry registry, CancellationToken cancellationToken)
    {
        string document = await ReadBodyAsync(http, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(document))
            return Results.BadRequest(new { error = "Report configuration document is empty." });

        string? name = null;
        try
        {
            ReportConfig config = new JsonReportConfigParser().Parse(document);
            name = config.Name;

            if (!DynamicReportName.IsValid(config.Name))
            {
                return Results.Ok(new ValidateReportResponse(
                    Valid: false,
                    Error: $"'{config.Name}' is not a valid report name. Names must match {DynamicReportName.Pattern}.",
                    Name: config.Name,
                    Columns: null,
                    NameTaken: registry.Contains(config.Name)));
            }

            ReportConfig substituted = ReportConfigEnvironment.Substitute(config);
            CompiledReport compiled = ReportConfigCompiler.Compile(substituted, http.RequestServices);
            var columns = compiled.Schema.Columns.Select(c => c.Name).ToArray();

            return Results.Ok(new ValidateReportResponse(
                Valid: true, Error: null, Name: config.Name, Columns: columns, NameTaken: registry.Contains(config.Name)));
        }
        catch (ConfigurationException ex)
        {
            return Results.Ok(new ValidateReportResponse(
                Valid: false, Error: ex.Message, Name: name, Columns: null,
                NameTaken: name is not null && registry.Contains(name)));
        }
    }

    private static async Task<IResult> DeleteReportAsync(
        string name,
        [FromServices] IMutableReportRegistry registry,
        [FromServices] IReportConfigStore configStore,
        CancellationToken cancellationToken)
    {
        if (!registry.Contains(name))
            return Results.NotFound(new { error = $"No report named '{name}' is registered." });

        bool inStore = DynamicReportName.IsValid(name) &&
            await configStore.ExistsAsync(name, cancellationToken).ConfigureAwait(false);
        if (!inStore)
        {
            return Results.Conflict(new
            {
                error = $"Report '{name}' is code-registered and cannot be deleted at runtime.",
            });
        }

        // Store first: if the process dies between the two calls, the report stays registered
        // until restart but won't rehydrate on the next one — self-healing. The opposite order
        // would resurrect a "deleted" report on the next rehydration.
        await configStore.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        registry.Unregister(name);
        return Results.NoContent();
    }

    private static IResult GetCapabilities(HttpContext http)
    {
        var sources = http.RequestServices.GetServices<IConfigSourceProvider>().Select(p => p.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var formats = http.RequestServices.GetServices<IWriterFactory>().Select(f => f.Format)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var destinations = http.RequestServices.GetServices<IDestinationFactory>().Select(f => f.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToArray();

        return Results.Ok(new CapabilitiesResponse(sources, formats, destinations));
    }

    private static async Task<string> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(http.Request.Body);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ListJobsAsync(
        string? status,
        string? report,
        string? since,
        int? limit,
        int? offset,
        [FromServices] IJobStore jobStore,
        CancellationToken cancellationToken)
    {
        ReportJobStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse(status, ignoreCase: true, out ReportJobStatus parsedStatus))
                return Results.BadRequest(new { error = $"'{status}' is not a valid job status." });

            statusFilter = parsedStatus;
        }

        DateTimeOffset? sinceFilter = null;
        if (!string.IsNullOrEmpty(since))
        {
            if (!DateTimeOffset.TryParse(
                since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsedSince))
            {
                return Results.BadRequest(new { error = $"'{since}' is not a valid ISO-8601 timestamp." });
            }

            sinceFilter = parsedSince;
        }

        var query = new JobQuery
        {
            Status = statusFilter,
            ReportName = report,
            Since = sinceFilter,
            Limit = Math.Clamp(limit ?? 50, 1, 200),
            Offset = Math.Max(offset ?? 0, 0),
        };

        IReadOnlyList<ReportJob> jobs = await jobStore.ListAsync(query, cancellationToken).ConfigureAwait(false);
        JobView[] ordered = jobs.OrderByDescending(j => j.CreatedAt).Select(JobView.From).ToArray();
        return Results.Ok(ordered);
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

    private static async Task<IResult> GetJobArtifactsAsync(
        string id, IReportJobScheduler scheduler, IReportArtifactStore artifactStore, CancellationToken cancellationToken)
    {
        ReportJob? job = await scheduler.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return Results.NotFound(new { error = $"No job with id '{id}'." });

        // Kept out of JobView so the frequent status poll never touches the file system; a
        // non-completed job simply has no artifacts yet rather than being an error.
        if (job.Status is not ReportJobStatus.Completed)
            return Results.Ok(Array.Empty<ArtifactView>());

        IReadOnlyList<ReportArtifact> artifacts = await artifactStore.ListAsync(id, cancellationToken).ConfigureAwait(false);
        ArtifactView[] views = artifacts
            .Select(a => new ArtifactView(a.FileName, a.MimeType, a.SizeBytes))
            .ToArray();

        return Results.Ok(views);
    }

    private static async Task<IResult> GetJobEventsAsync(
        string id, string? type, int? limit, int? offset,
        IReportJobScheduler scheduler, HttpContext http, CancellationToken cancellationToken)
    {
        ReportJob? job = await scheduler.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return Results.NotFound(new { error = $"No job with id '{id}'." });

        // Optional: hosts that never call AddJobEvents()/AddInMemoryJobEvents() have no
        // IJobEventStore registered — every job simply has no recorded events (ADR D38), not an error.
        IJobEventStore? store = http.RequestServices.GetService<IJobEventStore>();
        if (store is null)
            return Results.Ok(Array.Empty<JobEventView>());

        int effectiveLimit = Math.Clamp(limit ?? 200, 1, 1000);
        int effectiveOffset = Math.Max(0, offset ?? 0);

        IReadOnlyList<JobEvent> events = await store.ListAsync(id, type, effectiveLimit, effectiveOffset, cancellationToken).ConfigureAwait(false);
        JobEventView[] views = events
            .Select(e => new JobEventView(e.Sequence, e.At, e.Type, e.Message, e.Data))
            .ToArray();

        return Results.Ok(views);
    }

    private static async Task<IResult> GetMemoryAsync(HttpContext http, CancellationToken cancellationToken)
    {
        // Optional: hosts that never register a job stack (typed-only, no AddNeoReportsInMemoryJobs
        // / AddNeoReportsHangfireJobs) have no IJobStore — RunningJobs is simply 0, not an error.
        IJobStore? jobStore = http.RequestServices.GetService<IJobStore>();
        var runningJobs = 0;
        if (jobStore is not null)
        {
            IReadOnlyList<ReportJob> running = await jobStore.ListAsync(
                new JobQuery { Status = ReportJobStatus.Running, Limit = 1000 }, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ReportJob> retrying = await jobStore.ListAsync(
                new JobQuery { Status = ReportJobStatus.Retrying, Limit = 1000 }, cancellationToken).ConfigureAwait(false);
            runningJobs = running.Count + retrying.Count;
        }

        // One reading per request — no background poller, no time series (D39, CLAUDE.md's "no
        // general metrics dashboard").
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        var view = new MemoryView(
            Environment.WorkingSet, gc.HeapSizeBytes, gc.TotalCommittedBytes, DateTimeOffset.UtcNow, runningJobs);

        return Results.Ok(view);
    }
}
