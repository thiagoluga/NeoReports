using System.Text.Json.Serialization;
using NeoReports.Abstractions;

namespace NeoReports.AspNetCore;

/// <summary>Request body for triggering a report run.</summary>
/// <param name="Parameters">Run-time parameters passed to the report (e.g. date ranges).</param>
public sealed record RunReportRequest(IReadOnlyDictionary<string, object?>? Parameters);

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
public sealed record ReportSummary(string Name, int OutputCount, IReadOnlyList<string> Columns);

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
