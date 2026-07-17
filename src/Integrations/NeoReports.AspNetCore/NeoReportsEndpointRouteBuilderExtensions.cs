using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Abstractions;
using NeoReports.Core;
using NeoReports.Core.Artifacts;
using NeoReports.Core.Configuration;
using NeoReports.Core.Events;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Preview;
using NeoReports.Core.QueryBuilder;
using NeoReports.Core.Registry;
using NeoReports.Core.Scheduling;
using NeoReports.Core.Schema;
using NeoReports.Core.SourceRegistry;

namespace NeoReports.AspNetCore;

/// <summary>Maps the NeoReports HTTP endpoints (trigger, status, cancel, download).</summary>
public static class NeoReportsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the NeoReports API under <paramref name="prefix"/>:
    /// <list type="bullet">
    /// <item><c>POST {prefix}/reports/{name}/run</c> — async (202 + jobId) or <c>?mode=sync</c> (streams a single output)</item>
    /// <item><c>POST {prefix}/reports/{name}/preview</c> — read-only sample of one page, no output writing, no job record (ADR D45); optional structured filters, SQL-family dynamic reports only</item>
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
    /// <item><c>GET  {prefix}/jobs/{id}/partial-artifacts</c> — best-effort partial output of a Failed/Cancelled job (ADR D40); completely separate from the completed-artifacts surface</item>
    /// <item><c>GET  {prefix}/jobs/{id}/partial-artifacts/download</c> — download the partial output</item>
    /// <item><c>PUT  {prefix}/reports/{name}/schedule</c> — set a runtime recurring-schedule override (ADR D41); works for both origins; 409 when no recurring scheduler is registered</item>
    /// <item><c>DELETE {prefix}/reports/{name}/schedule</c> — clear the runtime override (tombstones a declared schedule, or removes a prior override)</item>
    /// <item><c>GET  {prefix}/sources</c> / <c>GET {prefix}/sources/{name}</c> — list/read registered sources (ADR D42); never returns properties</item>
    /// <item><c>POST {prefix}/sources</c> / <c>PUT {prefix}/sources/{name}</c> — register / full-replace a source; 409 when a report still references it on delete</item>
    /// <item><c>DELETE {prefix}/sources/{name}</c> — remove a source; blocked (409) while any registered report references it</item>
    /// <item><c>POST {prefix}/sources/{name}/health</c> — run an on-demand health check now; cached + timestamped; 422 when the type has no registered check</item>
    /// <item><c>GET  {prefix}/sources/{name}/catalog</c> — introspect the source's schema (tables/columns/PK/FK, ADR D49); 422 when the type has no registered explorer</item>
    /// <item><c>GET  {prefix}/sources/{name}/preview?schema=&amp;table=</c> — first rows of one table (ADR D49); row count is fixed server-side</item>
    /// <item><c>POST {prefix}/sources/{name}/query-sql</c> — generate keyset-safe report SQL from a visual query model (ADR D49, Pro); 422 when no query-builder generator is registered</item>
    /// <item><c>POST {prefix}/sources/{name}/query-preview</c> — bounded, read-only sample of a visual query's result (ADR D49, K6); generates the SQL server-side, runs one capped page, never executes raw caller SQL</item>
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

        // Compiling a report — Create and Validate below — must resolve IConfigSourceProvider
        // (and, for a Ref-based source, ISourceRegistry) through the app's ROOT provider, never
        // http.RequestServices: Create's compiled report is registered into the singleton
        // IMutableReportRegistry and outlives the request, so a RefBatchSource that captured the
        // request's scoped IServiceProvider throws ObjectDisposedException the first time a later,
        // fully-async job run tries to resolve its source — the scope is long gone by then.
        // IEndpointRouteBuilder.ServiceProvider is the app's root provider, captured once here.
        IServiceProvider rootServices = endpoints.ServiceProvider;

        group.MapPost("/reports/{name}/run", RunReportAsync);
        group.MapPost("/reports/{name}/preview", PreviewReportAsync);
        group.MapGet("/reports", ListReports);
        group.MapGet("/reports/{name}", GetReportDetailAsync);
        group.MapPost("/reports", (HttpContext http, [FromServices] IMutableReportRegistry registry,
                [FromServices] IReportConfigStore configStore, CancellationToken cancellationToken) =>
            CreateReportAsync(http, registry, configStore, rootServices, cancellationToken));
        group.MapPost("/reports/validate", (HttpContext http, [FromServices] IReportRegistry registry,
                CancellationToken cancellationToken) =>
            ValidateReportAsync(http, registry, rootServices, cancellationToken));
        group.MapDelete("/reports/{name}", DeleteReportAsync);
        group.MapGet("/capabilities", GetCapabilities);
        group.MapGet("/jobs", ListJobsAsync);
        group.MapGet("/jobs/{id}", GetJobAsync);
        group.MapPost("/jobs/{id}/cancel", CancelJobAsync);
        group.MapGet("/jobs/{id}/download", DownloadAsync);
        group.MapGet("/jobs/{id}/artifacts", GetJobArtifactsAsync);
        group.MapGet("/jobs/{id}/events", GetJobEventsAsync);
        group.MapGet("/system/memory", GetMemoryAsync);
        group.MapGet("/jobs/{id}/partial-artifacts", GetPartialArtifactsAsync);
        group.MapGet("/jobs/{id}/partial-artifacts/download", DownloadPartialArtifactsAsync);
        group.MapPut("/reports/{name}/schedule", SetScheduleAsync);
        group.MapDelete("/reports/{name}/schedule", ClearScheduleAsync);
        group.MapGet("/sources", ListSourcesAsync);
        group.MapGet("/sources/{name}", GetSourceAsync);
        group.MapPost("/sources", CreateSourceAsync);
        group.MapPut("/sources/{name}", ReplaceSourceAsync);
        group.MapDelete("/sources/{name}", DeleteSourceAsync);
        group.MapPost("/sources/{name}/health", CheckSourceHealthAsync);
        group.MapGet("/sources/{name}/catalog", GetSourceCatalogAsync);
        group.MapGet("/sources/{name}/preview", PreviewSourceTableAsync);
        group.MapPost("/sources/{name}/query-sql", GenerateSourceQuerySqlAsync);
        group.MapPost("/sources/{name}/query-preview", PreviewSourceQueryAsync);

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

        if (body?.Filters is { Count: > 0 })
        {
            // Applying filters to a full run (not just a preview sample) needs a temporary compiled
            // variant threaded through the job/scheduler pipeline — a larger, separate piece of work
            // deferred past this pass; POST .../preview already supports filters fully.
            return Results.BadRequest(new
            {
                error = "Filters are not yet supported on a full run — use POST /reports/{name}/preview instead.",
            });
        }

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

    private static async Task<IResult> PreviewReportAsync(
        string name,
        PreviewRequest? body,
        IReportRegistry registry,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        CompiledReport? report = registry.Find(name);
        if (report is null)
            return Results.NotFound(new { error = $"No report named '{name}' is registered." });

        IReadOnlyList<PreviewFilter> filters;
        try
        {
            filters = ParseFilters(body?.Filters);
        }
        catch (ConfigurationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var jobId = Guid.NewGuid().ToString("N");
        ILogger logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NeoReports.Preview");
        var execution = new ReportExecutionContext(jobId, name, null, logger, cancellationToken);

        try
        {
            PreviewResult result = await ReportPreviewRunner.PreviewAsync(
                report, filters, body?.PageSize ?? ReportPreviewRunner.MaxPageSize, execution, http.RequestServices, cancellationToken)
                .ConfigureAwait(false);

            ReportColumnView[] columns = result.Schema.Columns
                .Select(c => new ReportColumnView(c.Name, c.Type.ToString(), c.DisplayName, c.Format, c.Nullable))
                .ToArray();

            return Results.Ok(new PreviewResponse(result.Rows, columns, result.FiltersApplied, result.HasMore));
        }
        catch (ConfigurationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static IReadOnlyList<PreviewFilter> ParseFilters(IReadOnlyList<PreviewFilterRequest>? filters)
    {
        if (filters is null || filters.Count == 0)
            return Array.Empty<PreviewFilter>();

        var result = new List<PreviewFilter>(filters.Count);
        foreach (PreviewFilterRequest f in filters)
        {
            if (!Enum.TryParse(f.Operator, ignoreCase: true, out PreviewFilterOperator op))
                throw new ConfigurationException($"Unknown filter operator '{f.Operator}'.");

            result.Add(new PreviewFilter(f.Column, op, f.Value));
        }

        return result;
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

        (string? scheduleCron, DateTimeOffset? nextRunAt, bool scheduleOverridden) =
            await ResolveScheduleAsync(report, http, cancellationToken).ConfigureAwait(false);

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
            AbortAtFailureRate: report.AbortThresholds?.FailureRate,
            ScheduleCron: scheduleCron,
            NextRunAt: nextRunAt,
            ScheduleOverridden: scheduleOverridden,
            SourceRef: report.SourceRef);

        return Results.Ok(detail);
    }

    /// <summary>
    /// Resolves a report's effective schedule for display: the override store and recurring
    /// scheduler are both optional (ADR D41) — a host that never called <c>AddScheduling</c>/a Jobs
    /// recurring scheduler simply shows "not scheduled" for every report, never a fabricated value.
    /// </summary>
    private static async Task<(string? Cron, DateTimeOffset? NextRunAt, bool Overridden)> ResolveScheduleAsync(
        CompiledReport report, HttpContext http, CancellationToken cancellationToken)
    {
        IScheduleOverrideStore? overrides = http.RequestServices.GetService<IScheduleOverrideStore>();
        ScheduleOverrideEntry? overrideEntry = null;
        if (overrides is not null && DynamicReportName.IsValid(report.Name))
            overrideEntry = await overrides.GetAsync(report.Name, cancellationToken).ConfigureAwait(false);

        string? effectiveCron = EffectiveSchedule.Resolve(report.Schedule, overrideEntry);
        if (effectiveCron is null)
            return (null, null, EffectiveSchedule.IsOverridden(overrideEntry));

        IRecurringReportScheduler? scheduler = http.RequestServices.GetService<IRecurringReportScheduler>();
        DateTimeOffset? nextRunAt = scheduler is null
            ? null
            : await scheduler.GetNextOccurrenceAsync(report.Name, cancellationToken).ConfigureAwait(false);

        return (effectiveCron, nextRunAt, EffectiveSchedule.IsOverridden(overrideEntry));
    }

    private static async Task<IResult> CreateReportAsync(
        HttpContext http,
        IMutableReportRegistry registry,
        IReportConfigStore configStore,
        IServiceProvider rootServices,
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

        if (config.Schedule is not null && http.RequestServices.GetService<IRecurringReportScheduler>() is null)
        {
            return Results.BadRequest(new
            {
                error = "This report declares a schedule, but no recurring scheduler is registered on this host. " +
                        "Register one (e.g. AddNeoReportsInMemoryJobs/AddNeoReportsHangfireJobs) or omit 'schedule'.",
            });
        }

        CompiledReport compiled;
        try
        {
            ReportConfig substituted = ReportConfigEnvironment.Substitute(config);
            compiled = ReportConfigCompiler.Compile(substituted, rootServices);
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

        // Effective at registration (ADR D41): a declared schedule starts firing immediately,
        // without waiting for the next host restart's reconciliation pass.
        if (compiled.Schedule is { } declaredSchedule)
        {
            IRecurringReportScheduler? scheduler = http.RequestServices.GetService<IRecurringReportScheduler>();
            if (scheduler is not null)
                await scheduler.RegisterRecurringAsync(config.Name, declaredSchedule.Cron, cancellationToken).ConfigureAwait(false);
        }

        var columns = compiled.Schema.Columns.Select(c => c.Name).ToArray();
        return Results.Created(
            $"{http.Request.PathBase}/api/reports/{config.Name}", new ReportCreatedResponse(config.Name, columns));
    }

    private static async Task<IResult> ValidateReportAsync(
        HttpContext http, IReportRegistry registry, IServiceProvider rootServices, CancellationToken cancellationToken)
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
            CompiledReport compiled = ReportConfigCompiler.Compile(substituted, rootServices);
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
        HttpContext http,
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

        // Recurring registration and any override are removed first (ADR D41), before the report
        // itself disappears, so no new firing can race the delete.
        IRecurringReportScheduler? scheduler = http.RequestServices.GetService<IRecurringReportScheduler>();
        if (scheduler is not null)
            await scheduler.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);

        IScheduleOverrideStore? overrides = http.RequestServices.GetService<IScheduleOverrideStore>();
        if (overrides is not null)
            await overrides.RemoveAsync(name, cancellationToken).ConfigureAwait(false);

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
        bool scheduling = http.RequestServices.GetService<IRecurringReportScheduler>() is not null;

        return Results.Ok(new CapabilitiesResponse(sources, formats, destinations, scheduling));
    }

    private static async Task<IResult> SetScheduleAsync(
        string name, SetScheduleRequest? body, HttpContext http,
        [FromServices] IReportRegistry registry, CancellationToken cancellationToken)
    {
        if (!registry.Contains(name))
            return Results.NotFound(new { error = $"No report named '{name}' is registered." });

        if (body is null || string.IsNullOrWhiteSpace(body.Cron))
            return Results.BadRequest(new { error = "A 'cron' expression must be provided." });

        IRecurringReportScheduler? scheduler = http.RequestServices.GetService<IRecurringReportScheduler>();
        IScheduleOverrideStore? overrides = http.RequestServices.GetService<IScheduleOverrideStore>();
        if (scheduler is null || overrides is null)
        {
            return Results.Conflict(new
            {
                error = "No recurring scheduler is registered on this host. Register one " +
                        "(e.g. AddNeoReportsInMemoryJobs/AddNeoReportsHangfireJobs) and AddScheduling to use schedules.",
            });
        }

        try
        {
            CronValidation.Validate(body.Cron);
        }
        catch (ConfigurationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // The override is durable; registering with the scheduler is what actually starts firing.
        // Neither the declared schedule nor the config document is ever patched (ADR D41).
        await overrides.SaveAsync(name, new ScheduleOverrideEntry(body.Cron), cancellationToken).ConfigureAwait(false);
        await scheduler.RegisterRecurringAsync(name, body.Cron, cancellationToken).ConfigureAwait(false);

        return Results.Ok();
    }

    private static async Task<IResult> ClearScheduleAsync(
        string name, HttpContext http,
        [FromServices] IReportRegistry registry, CancellationToken cancellationToken)
    {
        CompiledReport? report = registry.Find(name);
        if (report is null)
            return Results.NotFound(new { error = $"No report named '{name}' is registered." });

        IRecurringReportScheduler? scheduler = http.RequestServices.GetService<IRecurringReportScheduler>();
        IScheduleOverrideStore? overrides = http.RequestServices.GetService<IScheduleOverrideStore>();
        if (scheduler is null || overrides is null)
            return Results.Conflict(new { error = "No recurring scheduler is registered on this host." });

        // A declared schedule needs an explicit "unscheduled" tombstone — merely removing any prior
        // override would let the declaration re-apply on the next reconciliation. A report with no
        // declaration has nothing to tombstone, so the override entry (if any) is just removed —
        // "delete" always means "stops firing" either way (ADR D41).
        if (report.Schedule is not null)
            await overrides.SaveAsync(name, new ScheduleOverrideEntry(null), cancellationToken).ConfigureAwait(false);
        else
            await overrides.RemoveAsync(name, cancellationToken).ConfigureAwait(false);

        await scheduler.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
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

    private static async Task<IResult> GetPartialArtifactsAsync(
        string id, IReportJobScheduler scheduler, HttpContext http, CancellationToken cancellationToken)
    {
        ReportJob? job = await scheduler.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return Results.NotFound(new { error = $"No job with id '{id}'." });

        // Only Failed and Cancelled runs ever capture partials (ADR D40) — a CompletedPartial run
        // (SkipBatchAndLog) legitimately published to the real destinations and has no partial.
        if (job.Status is not (ReportJobStatus.Failed or ReportJobStatus.Cancelled))
            return Results.Ok(Array.Empty<ArtifactView>());

        // Optional: hosts that never call AddPartialArtifacts() have no IPartialArtifactStore —
        // every job simply has no captured partials, not an error.
        IPartialArtifactStore? partialStore = http.RequestServices.GetService<IPartialArtifactStore>();
        if (partialStore is null)
            return Results.Ok(Array.Empty<ArtifactView>());

        IReadOnlyList<ReportArtifact> partials = await partialStore.ListAsync(id, cancellationToken).ConfigureAwait(false);
        ArtifactView[] views = partials
            .Select(a => new ArtifactView(a.FileName, a.MimeType, a.SizeBytes))
            .ToArray();

        return Results.Ok(views);
    }

    private static async Task<IResult> DownloadPartialArtifactsAsync(
        string id, IReportJobScheduler scheduler, HttpContext http, CancellationToken cancellationToken)
    {
        ReportJob? job = await scheduler.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return Results.NotFound(new { error = $"No job with id '{id}'." });

        if (job.Status is not (ReportJobStatus.Failed or ReportJobStatus.Cancelled))
            return Results.NotFound(new { error = $"Job '{id}' has no partial output (status: {job.Status})." });

        IPartialArtifactStore? partialStore = http.RequestServices.GetService<IPartialArtifactStore>();
        if (partialStore is null)
            return Results.NotFound(new { error = "Partial-artifact capture is not enabled on this host." });

        IReadOnlyList<ReportArtifact> partials = await partialStore.ListAsync(id, cancellationToken).ConfigureAwait(false);
        if (partials.Count == 0)
            return Results.NotFound(new { error = $"No partial output was captured for job '{id}'." });

        if (partials.Count == 1)
        {
            ReportArtifact single = partials[0];
            return Results.File(single.Path, single.MimeType, single.FileName);
        }

        // Completely separate from the completed-artifacts zip helper above — this route, and the
        // files it serves, are never reachable from GET /jobs/{id}/artifacts or /download.
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ReportArtifact partial in partials)
            {
                ZipArchiveEntry entry = archive.CreateEntry(partial.FileName, CompressionLevel.Optimal);
                await using Stream entryStream = entry.Open();
                await using var fileStream = new FileStream(partial.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }
        }

        zip.Position = 0;
        return Results.File(zip, "application/zip", $"{job.ReportName}-{id}-partial.zip");
    }

    private static async Task<IResult> ListSourcesAsync(
        HttpContext http, [FromServices] IReportRegistry reportRegistry, CancellationToken cancellationToken)
    {
        ISourceRegistry? registry = http.RequestServices.GetService<ISourceRegistry>();
        if (registry is null)
            return Results.Ok(Array.Empty<SourceView>());

        IReadOnlyList<SourceDefinition> definitions = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
        ISourceHealthCache? healthCache = http.RequestServices.GetService<ISourceHealthCache>();
        SourceView[] views = definitions.Select(d => ToSourceView(d, reportRegistry, healthCache)).ToArray();
        return Results.Ok(views);
    }

    private static async Task<IResult> GetSourceAsync(
        string name, HttpContext http, [FromServices] IReportRegistry reportRegistry, CancellationToken cancellationToken)
    {
        ISourceRegistry? registry = http.RequestServices.GetService<ISourceRegistry>();
        SourceDefinition? definition = registry is null ? null : await registry.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (definition is null)
            return Results.NotFound(new { error = $"No source named '{name}' is registered." });

        ISourceHealthCache? healthCache = http.RequestServices.GetService<ISourceHealthCache>();
        return Results.Ok(ToSourceView(definition, reportRegistry, healthCache));
    }

    private static async Task<IResult> CreateSourceAsync(HttpContext http, CancellationToken cancellationToken)
    {
        ISourceRegistry? registry = http.RequestServices.GetService<ISourceRegistry>();
        if (registry is null)
            return Results.Conflict(new { error = "No source registry is configured on this host. Register one with AddSourceRegistry()/AddInMemorySourceRegistry()." });

        SourceRequest? body = await ReadJsonBodyAsync<SourceRequest>(http, cancellationToken).ConfigureAwait(false);
        IResult? validationError = ValidateSourceRequest(http, body, name: null);
        if (validationError is not null)
            return validationError;

        if (await registry.GetAsync(body!.Name, cancellationToken).ConfigureAwait(false) is not null)
            return Results.Conflict(new { error = $"A source named '{body.Name}' already exists." });

        await registry.SaveAsync(new SourceDefinition(body.Name, body.Type, body.Properties, body.Description), cancellationToken).ConfigureAwait(false);
        return Results.Created($"{http.Request.PathBase}/api/sources/{body.Name}", ToSourceView(
            new SourceDefinition(body.Name, body.Type, Description: body.Description), reportRegistry: http.RequestServices.GetRequiredService<IReportRegistry>(), healthCache: null));
    }

    private static async Task<IResult> ReplaceSourceAsync(string name, HttpContext http, CancellationToken cancellationToken)
    {
        ISourceRegistry? registry = http.RequestServices.GetService<ISourceRegistry>();
        if (registry is null)
            return Results.Conflict(new { error = "No source registry is configured on this host. Register one with AddSourceRegistry()/AddInMemorySourceRegistry()." });

        SourceRequest? body = await ReadJsonBodyAsync<SourceRequest>(http, cancellationToken).ConfigureAwait(false);
        IResult? validationError = ValidateSourceRequest(http, body, name);
        if (validationError is not null)
            return validationError;

        if (await registry.GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
            return Results.NotFound(new { error = $"No source named '{name}' is registered." });

        await registry.SaveAsync(new SourceDefinition(name, body!.Type, body.Properties, body.Description), cancellationToken).ConfigureAwait(false);
        IReportRegistry reportRegistry = http.RequestServices.GetRequiredService<IReportRegistry>();
        ISourceHealthCache? healthCache = http.RequestServices.GetService<ISourceHealthCache>();
        return Results.Ok(ToSourceView(new SourceDefinition(name, body.Type, Description: body.Description), reportRegistry, healthCache));
    }

    private static async Task<IResult> DeleteSourceAsync(
        string name, HttpContext http, [FromServices] IReportRegistry reportRegistry, CancellationToken cancellationToken)
    {
        ISourceRegistry? registry = http.RequestServices.GetService<ISourceRegistry>();
        if (registry is null)
            return Results.Conflict(new { error = "No source registry is configured on this host." });

        if (await registry.GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
            return Results.NotFound(new { error = $"No source named '{name}' is registered." });

        int referencedByCount = reportRegistry.Reports.Count(r => string.Equals(r.SourceRef, name, StringComparison.Ordinal));
        if (referencedByCount > 0)
        {
            return Results.Conflict(new
            {
                error = $"Source '{name}' is referenced by {referencedByCount} registered report(s) and cannot be deleted.",
            });
        }

        await registry.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        http.RequestServices.GetService<ISourceHealthCache>()?.Remove(name);
        return Results.NoContent();
    }

    private static async Task<IResult> CheckSourceHealthAsync(string name, HttpContext http, CancellationToken cancellationToken)
    {
        ISourceRegistry? registry = http.RequestServices.GetService<ISourceRegistry>();
        if (registry is null)
            return Results.Conflict(new { error = "No source registry is configured on this host." });

        SourceDefinition? definition;
        try
        {
            definition = await registry.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (ConfigurationException ex)
        {
            return Results.Problem(title: "Could not resolve the source.", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        if (definition is null)
            return Results.NotFound(new { error = $"No source named '{name}' is registered." });

        ISourceHealthCheck? check = http.RequestServices.GetServices<ISourceHealthCheck>()
            .FirstOrDefault(c => string.Equals(c.Type, definition.Type, StringComparison.OrdinalIgnoreCase));
        if (check is null)
        {
            return Results.Problem(
                title: "Health check not supported for this source type.",
                detail: $"No ISourceHealthCheck is registered for type '{definition.Type}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        SourceHealthResult result = await check.CheckAsync(definition, http.RequestServices, cancellationToken).ConfigureAwait(false);
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;

        ISourceHealthCache? healthCache = http.RequestServices.GetService<ISourceHealthCache>();
        healthCache?.Set(name, new SourceHealthReading(result.Healthy, result.Error, result.Latency.TotalMilliseconds, checkedAt));

        return Results.Ok(new SourceHealthResponse(result.Healthy, result.Error, result.Latency.TotalMilliseconds, checkedAt));
    }

    // The UI's preview size cap (ADR D49). The engine (AdoSchemaExplorer) also clamps to its own
    // hard ceiling, so this endpoint's cap and that ceiling are independent defenses.
    private const int SchemaPreviewTop = 50;

    private static async Task<IResult> GetSourceCatalogAsync(string name, HttpContext http, CancellationToken cancellationToken)
    {
        (SourceDefinition? definition, ISchemaExplorer? explorer, IResult? failure) =
            await ResolveExplorerAsync(name, http, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        try
        {
            SchemaCatalog catalog = await explorer!.GetCatalogAsync(definition!, cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToCatalogResponse(catalog));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SchemaProblem(http, ex, name, "Could not read the source's schema.");
        }
    }

    private static async Task<IResult> PreviewSourceTableAsync(
        string name, string? schema, string? table, HttpContext http, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
            return Results.BadRequest(new { error = "A 'table' query parameter is required." });

        (SourceDefinition? definition, ISchemaExplorer? explorer, IResult? failure) =
            await ResolveExplorerAsync(name, http, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        try
        {
            TablePreview preview = await explorer!
                .PreviewTableAsync(definition!, schema ?? string.Empty, table, SchemaPreviewTop, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new TablePreviewResponse(preview.Columns, preview.Rows));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SchemaProblem(http, ex, name, "Could not preview the table.");
        }
    }

    private static async Task<IResult> GenerateSourceQuerySqlAsync(string name, HttpContext http, CancellationToken cancellationToken)
    {
        (SourceDefinition? definition, IResult? failure) = await ResolveSourceAsync(name, http, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        // The Pro query builder registers this seam (ADR D49); an MIT-only host has none, so the
        // capability is honestly reported as unavailable rather than faked (D36).
        IQuerySqlGenerator? generator = http.RequestServices.GetService<IQuerySqlGenerator>();
        if (generator is null)
        {
            return Results.Problem(
                title: "The visual query builder is not available on this host.",
                detail: "No IQuerySqlGenerator is registered. It ships with the NeoReports.QueryBuilder.Pro package (AddQueryBuilder()).",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        string modelJson = await ReadBodyAsync(http, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelJson))
            return Results.BadRequest(new { error = "The request body must contain the query model JSON." });

        try
        {
            GeneratedReportSql generated = generator.Generate(modelJson, definition!.Type);
            ReportColumnView[] schema = generated.Schema.Columns
                .Select(c => new ReportColumnView(c.Name, c.Type.ToString(), c.DisplayName, c.Format, c.Nullable))
                .ToArray();
            return Results.Ok(new GeneratedQuerySqlResponse(generated.Sql, generated.Parameters, schema));
        }
        catch (QuerySqlGenerationException ex)
        {
            // The generator's message is caller-safe by contract (no secrets, no raw model echo).
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> PreviewSourceQueryAsync(string name, HttpContext http, CancellationToken cancellationToken)
    {
        (SourceDefinition? definition, IResult? failure) = await ResolveSourceAsync(name, http, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        // Same capability gate as query-sql: the SQL is generated server-side from the model by the
        // Pro generator (injection-safe by construction), never taken as raw caller SQL, so the
        // preview never runs anything the visual builder couldn't already compile.
        IQuerySqlGenerator? generator = http.RequestServices.GetService<IQuerySqlGenerator>();
        if (generator is null)
        {
            return Results.Problem(
                title: "The visual query builder is not available on this host.",
                detail: "No IQuerySqlGenerator is registered. It ships with the NeoReports.QueryBuilder.Pro package (AddQueryBuilder()).",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        string modelJson = await ReadBodyAsync(http, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelJson))
            return Results.BadRequest(new { error = "The request body must contain the query model JSON." });

        GeneratedReportSql generated;
        try
        {
            generated = generator.Generate(modelJson, definition!.Type);
        }
        catch (QuerySqlGenerationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var jobId = Guid.NewGuid().ToString("N");
        ILogger logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NeoReports.QueryPreview");
        var execution = new ReportExecutionContext(jobId, name, null, logger, cancellationToken);

        try
        {
            QueryPreviewResult result = await QueryPreviewRunner.PreviewAsync(
                definition!.Type, definition.Properties, generated,
                QueryPreviewRunner.MaxRows, execution, http.RequestServices, cancellationToken).ConfigureAwait(false);

            ReportColumnView[] columns = generated.Schema.Columns
                .Select(c => new ReportColumnView(c.Name, c.Type.ToString(), c.DisplayName, c.Format, c.Nullable))
                .ToArray();
            return Results.Ok(new QueryPreviewResponse(columns, result.Rows, result.Truncated));
        }
        catch (ConfigurationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A driver exception's message can echo connection-string fragments — log it, return a
            // generic secret-free 502 (the same guard the schema/table-preview endpoints use).
            return SchemaProblem(http, ex, name, "Could not run the query preview.");
        }
    }

    // A schema/preview call opens a live DB connection built from the source's connection string and
    // runs catalog/preview SQL — a driver exception's message can echo connection-string fragments
    // (a malformed string, an auth error), so it is logged server-side and never returned to the
    // caller. The response carries only a generic, secret-free detail; operators diagnose from logs
    // (or the source's own health check, which is the purpose-built place for a connection error).
    private static IResult SchemaProblem(HttpContext http, Exception ex, string sourceName, string title)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("NeoReports.Schema")
            .LogWarning(ex, "{Title} (source '{Source}').", title, sourceName);
        return Results.Problem(
            title: title,
            detail: "The source's database could not be read. See the server logs for details.",
            statusCode: StatusCodes.Status502BadGateway);
    }

    // Resolves the named source and its registered ISchemaExplorer, or returns the appropriate
    // failure result (409 no registry, 404 unknown source, 422 no explorer for the source type,
    // 500 on a resolve error) — shared by both schema-explorer endpoints.
    private static async Task<(SourceDefinition? Definition, ISchemaExplorer? Explorer, IResult? Failure)> ResolveExplorerAsync(
        string name, HttpContext http, CancellationToken cancellationToken)
    {
        (SourceDefinition? definition, IResult? failure) = await ResolveSourceAsync(name, http, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return (null, null, failure);

        ISchemaExplorer? explorer = http.RequestServices.GetServices<ISchemaExplorer>()
            .FirstOrDefault(e => string.Equals(e.Type, definition!.Type, StringComparison.OrdinalIgnoreCase));
        if (explorer is null)
        {
            return (null, null, Results.Problem(
                title: "Schema exploration not supported for this source type.",
                detail: $"No ISchemaExplorer is registered for type '{definition!.Type}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        return (definition, explorer, null);
    }

    // Resolves the named source through the registry, or returns the appropriate failure result
    // (409 no registry, 404 unknown source, 500 on a resolve error) — shared by every endpoint that
    // acts on a named source's live capabilities (schema explorer, query builder).
    private static async Task<(SourceDefinition? Definition, IResult? Failure)> ResolveSourceAsync(
        string name, HttpContext http, CancellationToken cancellationToken)
    {
        ISourceRegistry? registry = http.RequestServices.GetService<ISourceRegistry>();
        if (registry is null)
            return (null, Results.Conflict(new { error = "No source registry is configured on this host." }));

        SourceDefinition? definition;
        try
        {
            definition = await registry.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (ConfigurationException ex)
        {
            return (null, Results.Problem(
                title: "Could not resolve the source.", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError));
        }

        if (definition is null)
            return (null, Results.NotFound(new { error = $"No source named '{name}' is registered." }));

        return (definition, null);
    }

    private static SchemaCatalogResponse ToCatalogResponse(SchemaCatalog catalog) => new(
        catalog.Tables.Select(t => new CatalogTableView(
            t.Schema,
            t.Name,
            t.Columns.Select(c => new CatalogColumnView(c.Name, c.DataType, c.Nullable, c.IsPrimaryKey)).ToArray(),
            t.ForeignKeys.Select(f => new ForeignKeyView(f.Column, f.ReferencedSchema, f.ReferencedTable, f.ReferencedColumn)).ToArray()))
            .ToArray());

    private static SourceView ToSourceView(SourceDefinition definition, IReportRegistry reportRegistry, ISourceHealthCache? healthCache)
    {
        int referencedByCount = reportRegistry.Reports.Count(r => string.Equals(r.SourceRef, definition.Name, StringComparison.Ordinal));
        SourceHealthReading? reading = healthCache?.Get(definition.Name);
        string? lastHealthStatus = reading switch
        {
            null => null,
            { Healthy: true } => "healthy",
            _ => "unhealthy",
        };

        return new SourceView(
            Name: definition.Name,
            Type: definition.Type,
            Description: definition.Description,
            ReferencedByCount: referencedByCount,
            LastHealthStatus: lastHealthStatus,
            LastHealthError: reading?.Error,
            LastHealthLatencyMs: reading?.LatencyMs,
            LastCheckedAt: reading?.CheckedAt);
    }

    private static IResult? ValidateSourceRequest(HttpContext http, SourceRequest? body, string? name)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Type))
            return Results.BadRequest(new { error = "A source request requires a non-empty 'name' and 'type'." });

        if (name is not null && !string.Equals(body.Name, name, StringComparison.Ordinal))
            return Results.BadRequest(new { error = $"The request body's name '{body.Name}' does not match the URL segment '{name}'." });

        if (!DynamicReportName.IsValid(body.Name))
        {
            return Results.BadRequest(new
            {
                error = $"'{body.Name}' is not a valid source name. Names must match {DynamicReportName.Pattern}.",
            });
        }

        bool typeRegistered = http.RequestServices.GetServices<IConfigSourceProvider>()
            .Any(p => string.Equals(p.Type, body.Type, StringComparison.OrdinalIgnoreCase));
        if (!typeRegistered)
            return Results.BadRequest(new { error = $"No source provider is registered for type '{body.Type}'." });

        return null;
    }

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
