using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
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

        // A handler that returns a Location must point back at the prefix this group was actually
        // mapped under; hardcoding "/api" makes the 202's Location a 404 under MapNeoReports("/v2").
        // The handlers are static method groups with no closure over `prefix`, so the mapped prefix
        // rides along on the request instead. Trailing '/' is trimmed so MapNeoReports("/") does not
        // produce a protocol-relative "//jobs/..." URL.
        string mappedPrefix = prefix.TrimEnd('/');
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Items[MappedPrefixKey] = mappedPrefix;
            return await next(context).ConfigureAwait(false);
        });

        if (options.RequireAuthorization)
        {
            if (string.IsNullOrEmpty(options.AuthorizationPolicy))
                group.RequireAuthorization();
            else
                group.RequireAuthorization(options.AuthorizationPolicy);
        }
        else if (endpoints.ServiceProvider.GetService<IAuthenticationSchemeProvider>() is null)
        {
            // Auth inherits from the host (ADR D20) — the engine does not impose a default. But when
            // no authentication is configured on the host at all AND RequireAuthorization was not set,
            // this management surface (trigger runs, register reports, store source connection
            // strings, mutate schedules, download artifacts) is reachable unauthenticated. That is a
            // valid deployment only behind a trusted boundary; warn once at startup so it is a
            // deliberate choice, not a silent default.
            endpoints.ServiceProvider.GetService<ILoggerFactory>()?
                .CreateLogger("NeoReports.AspNetCore")
                .LogWarning(
                    "NeoReports endpoints mapped at '{Prefix}' with no authentication configured on the host and " +
                    "NeoReportsEndpointOptions.RequireAuthorization not set — the report management API is reachable " +
                    "unauthenticated. Configure the host's authentication/authorization (or set RequireAuthorization) " +
                    "before exposing it beyond a trusted network.",
                    prefix);
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
        group.MapGet("/reports/{name}/config", GetReportConfigAsync);
        group.MapPost("/reports", (HttpContext http, [FromServices] IMutableReportRegistry registry,
                [FromServices] IReportConfigStore configStore, CancellationToken cancellationToken) =>
            CreateReportAsync(http, registry, configStore, rootServices, cancellationToken));
        group.MapPut("/reports/{name}", (string name, HttpContext http, [FromServices] IMutableReportRegistry registry,
                [FromServices] IReportConfigStore configStore, CancellationToken cancellationToken) =>
            ReplaceReportAsync(name, http, registry, configStore, rootServices, cancellationToken));
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

        IReadOnlyDictionary<string, object?>? parameters = NormalizeJsonValues(body?.Parameters);

        if (FirstComplexParameter(parameters) is { } complexParameter)
        {
            return Results.BadRequest(new
            {
                error = $"Parameter '{complexParameter}' is an array or object. Run parameters must be " +
                        "scalars (string, number, boolean, null) — v1 has no way to bind a structured " +
                        "value to a source.",
            });
        }

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
                // result.Error is already scrubbed at the source (a driver exception is reduced to its
                // type name, only NeoReports' own curated messages survive). Log it and still return a
                // generic detail — defence in depth, the same scrub-and-log stance as the schema
                // endpoints — so the response never carries even the run's own reason string.
                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("NeoReports.Run")
                    .LogWarning("Synchronous run of report '{Report}' (job {JobId}) failed: {Reason}", name, jobId, result.Error);
                return Results.Problem(
                    title: "Report run failed.",
                    detail: "The report run failed. See the server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
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
            ApiUrl(http, $"/jobs/{enqueuedId}"),
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Preview opens a live connection and runs SQL, so a bad filter value surfaces the raw
            // driver exception — which names the host, port and database. Every sibling endpoint that
            // touches a source routes failures through SchemaProblem (logged server-side, generic to
            // the caller); this one used to let them escape as an unhandled 500, which on a host
            // running in Development also renders the full stack trace.
            return SchemaProblem(http, ex, name, "Could not run the report preview.");
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
    /// Returns the stored configuration document for a config-origin report, with credential-bearing
    /// property values replaced by <see cref="ReportConfigSecrets.RedactedValue"/> (ADR D86).
    /// <para>
    /// This is the one place a property bag leaves the engine, and it is what makes editing possible
    /// at all: <c>GET /reports/{name}</c> deliberately exposes none of the source's configuration
    /// (D33(c)), so an editor built on it could only ever offer the user a blank form. Sending the
    /// placeholder back on <c>PUT</c> restores the stored value, so a secret still never leaves the
    /// host — which is precisely the round-trip story D33(f) deferred report editing for.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetReportConfigAsync(
        string name, HttpContext http, [FromServices] IReportRegistry registry, CancellationToken cancellationToken)
    {
        if (registry.Find(name) is null)
            return Results.NotFound(new { error = $"No report named '{name}' is registered." });

        IReportConfigStore? configStore = http.RequestServices.GetService<IReportConfigStore>();
        string? document = configStore is not null && DynamicReportName.IsValid(name)
            ? await configStore.TryGetAsync(name, cancellationToken).ConfigureAwait(false)
            : null;

        if (document is null)
        {
            // Code-registered reports have no document to return — their definition lives in the
            // host's source, which is also where it has to be changed.
            return Results.NotFound(new
            {
                error = $"Report '{name}' is code-registered and has no stored configuration document.",
            });
        }

        try
        {
            return Results.Text(ReportConfigSecrets.Redact(document), "application/json");
        }
        catch (ConfigurationException ex)
        {
            // A document that no longer parses is a corrupt store, not a bad request — say so
            // rather than handing the client half a configuration it would silently save back.
            return Results.Problem(
                title: $"The stored configuration for '{name}' could not be read.",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
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

        // A redaction placeholder only means anything against a stored document to restore from
        // (ADR D86), and a create has none. Rejected rather than passed through, which would
        // otherwise persist the literal sentinel as if it were a connection string.
        if (ReportConfigSecrets.ContainsRedactedValue(document))
        {
            return Results.BadRequest(new
            {
                error = $"This configuration contains the redaction placeholder '{ReportConfigSecrets.RedactedValue}', " +
                        "which can only be resolved when replacing an existing report (PUT /reports/{name}). " +
                        "Send the real value instead.",
            });
        }

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
            ApiUrl(http, $"/reports/{config.Name}"), new ReportCreatedResponse(config.Name, columns));
    }

    /// <summary>
    /// Replaces a config-origin report's definition in place (ADR D86), resolving any
    /// <see cref="ReportConfigSecrets.RedactedValue"/> the client sent back against the stored
    /// document.
    /// <para>
    /// Editing used to be delete-then-create from the client, which fails in the worst possible
    /// direction: a configuration the engine rejects arrives *after* the original is already gone,
    /// and the user is left with no report at all. Here nothing is mutated until the replacement has
    /// compiled, so a rejected edit leaves the existing report exactly as it was.
    /// </para>
    /// </summary>
    private static async Task<IResult> ReplaceReportAsync(
        string name,
        HttpContext http,
        IMutableReportRegistry registry,
        IReportConfigStore configStore,
        IServiceProvider rootServices,
        CancellationToken cancellationToken)
    {
        CompiledReport? existing = registry.Find(name);
        if (existing is null)
            return Results.NotFound(new { error = $"No report named '{name}' is registered." });

        string? stored = DynamicReportName.IsValid(name)
            ? await configStore.TryGetAsync(name, cancellationToken).ConfigureAwait(false)
            : null;

        if (stored is null)
        {
            return Results.Conflict(new
            {
                error = $"Report '{name}' is code-registered and cannot be changed at runtime.",
            });
        }

        string document = await ReadBodyAsync(http, cancellationToken).ConfigureAwait(false);

        ReportConfig config;
        try
        {
            document = ReportConfigSecrets.Restore(document, stored);
            config = new JsonReportConfigParser().Parse(document);
        }
        catch (ConfigurationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (!string.Equals(config.Name, name, StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                error = $"The configuration is named '{config.Name}' but the route targets '{name}'. " +
                        "A report cannot be renamed in place — delete it and create the new name.",
            });
        }

        IRecurringReportScheduler? scheduler = http.RequestServices.GetService<IRecurringReportScheduler>();
        if (config.Schedule is not null && scheduler is null)
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
            // Nothing has been touched yet, which is the entire point of compiling first.
            return Results.BadRequest(new { error = ex.Message });
        }

        registry.Replace(compiled);

        try
        {
            // Same rule as create: the ORIGINAL document is persisted, ${VAR} placeholders intact.
            await configStore.SaveAsync(name, document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Put the previous definition back rather than leaving the process running a report the
            // next restart will not rehydrate. Every failure rolls back, not only the file-system
            // ones a create has to worry about: an IReportConfigStore is an interface, a custom one
            // can throw anything, and a registry that disagrees with the store is a report that
            // quietly reverts on the next restart — the hardest kind of bug to trace back to an edit.
            registry.Replace(existing);

            if (ex is OperationCanceledException)
                throw;

            return Results.Problem(
                title: "Failed to persist the dynamic report.",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        await ReconcileScheduleAsync(name, compiled, scheduler, http, cancellationToken).ConfigureAwait(false);

        var columns = compiled.Schema.Columns.Select(c => c.Name).ToArray();
        return Results.Ok(new ReportCreatedResponse(name, columns));
    }

    /// <summary>
    /// Brings the recurring registration in line with a replaced report's declared schedule.
    /// <para>
    /// A runtime schedule override still wins (ADR D41): editing a definition is not the same act as
    /// changing when it runs, so an override set through <c>PUT /reports/{name}/schedule</c> survives
    /// the edit and stays effective. Without one, the declared schedule takes effect immediately —
    /// including its <em>removal</em>, which has to unregister the recurring job rather than leave it
    /// firing for a report that no longer declares it.
    /// </para>
    /// </summary>
    private static async Task ReconcileScheduleAsync(
        string name,
        CompiledReport compiled,
        IRecurringReportScheduler? scheduler,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (scheduler is null)
            return;

        IScheduleOverrideStore? overrides = http.RequestServices.GetService<IScheduleOverrideStore>();
        ScheduleOverrideEntry? overrideEntry = overrides is null
            ? null
            : await overrides.GetAsync(name, cancellationToken).ConfigureAwait(false);

        string? effectiveCron = EffectiveSchedule.Resolve(compiled.Schedule, overrideEntry);
        if (effectiveCron is null)
            await scheduler.RemoveRecurringAsync(name, cancellationToken).ConfigureAwait(false);
        else
            await scheduler.RegisterRecurringAsync(name, effectiveCron, cancellationToken).ConfigureAwait(false);
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
            // ?for={name} dry-runs an *edit* of an existing report: redaction placeholders (ADR D86)
            // are resolved against that report's stored document first, so the Builder's "Validate"
            // button means the same thing while editing as it does while creating. Without this the
            // placeholder would reach a provider as a literal connection string and fail for a
            // reason that has nothing to do with the configuration under test.
            if (http.Request.Query.TryGetValue("for", out StringValues editing)
                && editing.ToString() is { Length: > 0 } editingName
                && DynamicReportName.IsValid(editingName)
                && http.RequestServices.GetService<IReportConfigStore>() is { } store
                && await store.TryGetAsync(editingName, cancellationToken).ConfigureAwait(false) is { } stored)
            {
                document = ReportConfigSecrets.Restore(document, stored);
            }

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

    /// <summary><see cref="HttpContext.Items"/> key carrying the prefix this group was mapped at.</summary>
    private const string MappedPrefixKey = "NeoReports.MappedPrefix";

    /// <summary>
    /// Builds a URL for another endpoint of this API, honouring both the host's path base and the
    /// prefix <see cref="MapNeoReports"/> was called with. Falls back to the documented default only
    /// if the filter that stashes the prefix somehow did not run.
    /// </summary>
    /// <param name="http">The current request.</param>
    /// <param name="relativePath">Path below the prefix, starting with <c>/</c>.</param>
    private static string ApiUrl(HttpContext http, string relativePath)
    {
        string prefix = http.Items.TryGetValue(MappedPrefixKey, out object? mapped) && mapped is string s
            ? s
            : "/api";
        return $"{http.Request.PathBase}{prefix}{relativePath}";
    }

    /// <summary>
    /// Rejects a schedule write for a report whose name an override store cannot key.
    /// <para>
    /// A schedule override is stored by report name, and a store persists it as a file name, so it
    /// only accepts <see cref="DynamicReportName.Pattern"/>. A <b>code-first</b> report is under no
    /// such constraint — <c>sales.daily</c> is perfectly legal — so a legitimately registered report
    /// can reach the store with a name it refuses, which surfaced as an <see cref="ArgumentException"/>
    /// and a <b>500</b>. The read path (<c>ResolveScheduleAsync</c>) already skips the lookup for such
    /// a name, which is what makes the missing guard here an oversight rather than a design.
    /// </para>
    /// </summary>
    /// <param name="name">The report name from the route.</param>
    /// <returns><see langword="null"/> when the name is storable; otherwise the response to return.</returns>
    private static IResult? ScheduleOverridesUnsupportedFor(string name) =>
        DynamicReportName.IsValid(name)
            ? null
            : Results.Conflict(new
            {
                error = $"The schedule for '{name}' cannot be changed over HTTP: overrides are stored " +
                        $"by report name, and a name must match {DynamicReportName.Pattern}. Declare " +
                        "this report's schedule in code, or rename it.",
            });

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

        if (ScheduleOverridesUnsupportedFor(name) is { } unsupported)
            return unsupported;

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

        if (ScheduleOverridesUnsupportedFor(name) is { } unsupported)
            return unsupported;

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
        HttpContext http,
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

        // Multiple outputs: bundle into a zip so a single download carries them all, built to a temp
        // file and streamed from there (constant memory, regardless of total report size).
        return await StreamArtifactsZipAsync(http, artifacts, $"{job.ReportName}-{id}.zip", cancellationToken);
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

        // Same temp-file zip path as the completed-artifacts download; the partial files this route
        // serves are still never reachable from GET /jobs/{id}/artifacts or /download.
        return await StreamArtifactsZipAsync(http, partials, $"{job.ReportName}-{id}-partial.zip", cancellationToken);
    }

    // Builds a ZIP of the given artifacts into a temporary file, then serves that file. Memory stays
    // constant no matter how large the bundled files are — the previous version buffered the whole
    // archive in a MemoryStream (RAM that grew with total report size and multiplied under concurrent
    // downloads). The archive is written with synchronous compression to a FileStream (allowed on a
    // file; the HTTP response body forbids synchronous IO, which is why it can't be composed straight
    // onto the response). The finished temp file is served by path and deleted once the response has
    // completed; a build-time failure deletes the partial file before rethrowing.
    private static async Task<IResult> StreamArtifactsZipAsync(
        HttpContext http, IReadOnlyList<ReportArtifact> artifacts, string downloadName, CancellationToken cancellationToken)
    {
        var zipDir = Path.Join(Path.GetTempPath(), "neoreports-zip");
        Directory.CreateDirectory(zipDir);
        var tempPath = Path.Join(zipDir, Guid.NewGuid().ToString("N") + ".zip");

        // Create the temp zip owner-only (0600) on Unix, where Path.GetTempPath() is a shared,
        // world-readable location (/tmp): the report bundle would otherwise be readable by any other
        // local user for the duration of the download. On Windows GetTempPath() is the per-user,
        // ACL-protected %LOCALAPPDATA%\Temp, and UnixCreateMode is unsupported there (CA1416).
        var writeOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            writeOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var built = false;
        try
        {
            await using (var zipFile = new FileStream(tempPath, writeOptions))
            using (var archive = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (ReportArtifact artifact in artifacts)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(artifact.FileName, CompressionLevel.Optimal);
                    await using Stream entryStream = entry.Open();
                    await using var fileStream = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                }
            }

            built = true;
        }
        finally
        {
            // A failed build (a missing source file, an IO/quota error, cancellation) leaves a
            // partial temp file — remove it before the exception propagates.
            if (!built)
                TryDeleteFile(tempPath);
        }

        // Results.File opens and disposes its own read handle; delete the temp file once the response
        // has been fully written and that handle released.
        http.Response.OnCompleted(() =>
        {
            TryDeleteFile(tempPath);
            return Task.CompletedTask;
        });
        return Results.File(tempPath, "application/zip", downloadName);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp zip — a locked/again-in-use file must never replace the
            // real exception on the failure path, nor fault a response that already succeeded.
        }
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

        await registry.SaveAsync(new SourceDefinition(body.Name, body.Type, NormalizeJsonValues(body.Properties), body.Description), cancellationToken).ConfigureAwait(false);
        return Results.Created(ApiUrl(http, $"/sources/{body.Name}"), ToSourceView(
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

        await registry.SaveAsync(new SourceDefinition(name, body!.Type, NormalizeJsonValues(body.Properties), body.Description), cancellationToken).ConfigureAwait(false);
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

        // A failed health check's Error is the raw connectivity error — for a relational or file
        // source that is a driver/IO exception message that can echo the host, port, database,
        // username or on-disk path. Log the detail server-side and surface only a generic,
        // secret-free reason to the caller (and into the cached reading GET /sources shows), the
        // same scrub-and-log stance the schema/preview endpoints already take (see SchemaProblem).
        string? clientError = ScrubHealthError(http, name, result.Healthy, result.Error);

        ISourceHealthCache? healthCache = http.RequestServices.GetService<ISourceHealthCache>();
        healthCache?.Set(name, new SourceHealthReading(result.Healthy, clientError, result.Latency.TotalMilliseconds, checkedAt));

        return Results.Ok(new SourceHealthResponse(result.Healthy, clientError, result.Latency.TotalMilliseconds, checkedAt));
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
        (GeneratedReportSql? generated, _, IResult? failure) =
            await GenerateFromModelAsync(name, http, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        ReportColumnView[] schema = generated!.Schema.Columns
            .Select(c => new ReportColumnView(c.Name, c.Type.ToString(), c.DisplayName, c.Format, c.Nullable))
            .ToArray();
        return Results.Ok(new GeneratedQuerySqlResponse(generated.Sql, generated.Parameters, schema, generated.KeyColumnName));
    }

    // Shared front half of both query-builder endpoints (query-sql and query-preview): resolve the
    // named source, gate on the Pro IQuerySqlGenerator (422 when absent — D36), read the raw model
    // JSON body, and generate the keyset SQL. Returns the generated SQL plus the resolved definition,
    // or the appropriate failure result (404/409/422/400). The generator's messages are caller-safe.
    private static async Task<(GeneratedReportSql? Generated, SourceDefinition? Definition, IResult? Failure)> GenerateFromModelAsync(
        string name, HttpContext http, CancellationToken cancellationToken)
    {
        (SourceDefinition? definition, IResult? failure) = await ResolveSourceAsync(name, http, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return (null, null, failure);

        // The Pro query builder registers this seam (ADR D49); an MIT-only host has none, so the
        // capability is honestly reported as unavailable rather than faked (D36).
        IQuerySqlGenerator? generator = http.RequestServices.GetService<IQuerySqlGenerator>();
        if (generator is null)
        {
            return (null, null, Results.Problem(
                title: "The visual query builder is not available on this host.",
                detail: "No IQuerySqlGenerator is registered. It ships with the NeoReports.QueryBuilder.Pro package (AddQueryBuilder()), which also requires a valid NeoReports Pro license.",
                statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        string modelJson = await ReadBodyAsync(http, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelJson))
            return (null, null, Results.BadRequest(new { error = "The request body must contain the query model JSON." }));

        try
        {
            return (generator.Generate(modelJson, definition!.Type), definition, null);
        }
        catch (QuerySqlGenerationException ex)
        {
            // The generator's message is caller-safe by contract (no secrets, no raw model echo).
            return (null, null, Results.BadRequest(new { error = ex.Message }));
        }
    }

    private static async Task<IResult> PreviewSourceQueryAsync(string name, HttpContext http, CancellationToken cancellationToken)
    {
        (GeneratedReportSql? generated, SourceDefinition? definition, IResult? failure) =
            await GenerateFromModelAsync(name, http, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        var jobId = Guid.NewGuid().ToString("N");
        ILogger logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NeoReports.QueryPreview");
        var execution = new ReportExecutionContext(jobId, name, null, logger, cancellationToken);

        try
        {
            QueryPreviewResult result = await QueryPreviewRunner.PreviewAsync(
                definition!.Type, definition.Properties, generated!,
                QueryPreviewRunner.MaxRows, execution, http.RequestServices, cancellationToken).ConfigureAwait(false);

            ReportColumnView[] columns = generated!.Schema.Columns
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

    // Two request types carry a caller-supplied bag of `object?` values — a run's `Parameters` and a
    // source's `Properties` — so System.Text.Json materializes each value as a JsonElement. Nothing
    // downstream can use that: an ADO provider rejects it outright
    // ("No mapping exists from object type System.Text.Json.JsonElement"), so every parameterized
    // report failed on the sync and in-memory-job paths — while the Hangfire path happened to work,
    // because it round-trips parameters through JobParameters, which converts them. Convert here
    // instead, at the one boundary where they enter, so every backend behaves the same. Round-tripping
    // through PrimitiveObjectConverter reuses the repo's single definition of "JSON value → CLR
    // primitive" (string/long/double/bool/ISO-8601 DateTime, nested objects left as JsonElement)
    // rather than restating it; these bags are a handful of scalars, so the cost is irrelevant.
    // Source properties had the identical split: FileSourceRegistryStore launders the bag through JSON
    // on write and read, but InMemorySourceRegistryStore keeps it as given — so under
    // AddInMemorySourceRegistry() a source created over HTTP failed later with "requires a non-empty
    // 'connectionString' property", the value being a JsonElement rather than a string.
    private static readonly JsonSerializerOptions ValueBagJson =
        new(JsonSerializerDefaults.Web) { Converters = { new PrimitiveObjectConverter() } };

    /// <summary>
    /// Returns the name of the first parameter whose value is an array or object, or
    /// <see langword="null"/> when every value is a scalar.
    /// <para>
    /// Structured parameter values are out of scope for v1, but nothing rejected them, and what
    /// happened next depended on the backend: the sync and in-memory paths handed the source a
    /// <see cref="JsonElement"/> — the very thing an ADO provider cannot bind — while Hangfire
    /// round-tripped it and handed over raw JSON text. Either way the caller learned about it as a
    /// driver error partway through a run, attributed to the source rather than to the request.
    /// Rejecting at the boundary makes the limit explicit and identical on every backend.
    /// </para>
    /// <para>
    /// Run before <see cref="NormalizeJsonValues"/>'s output is used, and only for run parameters —
    /// source property bags travel the same shape but are a provider's own configuration surface, so
    /// they are left alone here.
    /// </para>
    /// </summary>
    /// <para>
    /// <see cref="PrimitiveObjectConverter"/> turns every scalar into a CLR primitive and leaves
    /// exactly the structured values as a <see cref="JsonElement"/>, so the value-kind check below is
    /// the whole test. <c>FirstOrDefault</c> over a <see cref="KeyValuePair{TKey,TValue}"/> yields a
    /// default pair — <c>Key</c> null — when nothing matches, which is the "no complex parameter"
    /// answer.
    /// </para>
    /// <param name="parameters">The normalized parameter bag.</param>
    private static string? FirstComplexParameter(IReadOnlyDictionary<string, object?>? parameters) =>
        parameters?
            .FirstOrDefault(pair => pair.Value is JsonElement { ValueKind: JsonValueKind.Array or JsonValueKind.Object })
            .Key;

    private static IReadOnlyDictionary<string, object?>? NormalizeJsonValues(
        IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return parameters;

        string json = JsonSerializer.Serialize(parameters, ValueBagJson);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, ValueBagJson);
    }

    // A failed health check's raw Error is the underlying driver/IO exception message, which can echo
    // connection-string fragments (host, port, database, username) or an on-disk path. Log it
    // server-side and hand the caller only a generic, secret-free reason — the same stance
    // SchemaProblem takes. Returns null for a healthy source (no error to show).
    private static string? ScrubHealthError(HttpContext http, string sourceName, bool healthy, string? rawError)
    {
        if (healthy || string.IsNullOrEmpty(rawError))
            return null;

        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("NeoReports.SourceHealth")
            .LogWarning("Health check for source '{Source}' reported unhealthy: {Reason}", sourceName, rawError);
        return "The source could not be reached. See the server logs for details.";
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
