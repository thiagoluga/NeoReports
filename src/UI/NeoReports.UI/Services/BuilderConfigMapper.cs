using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoReports.UI.Services;

/// <summary>
/// Maps a <see cref="BuilderState"/> to the JSON document shape the engine's
/// <c>POST /api/reports</c> and <c>POST /api/reports/validate</c> endpoints accept (the same
/// document shape <c>NeoReports.Abstractions.ReportConfig</c> parses). Kept dependency-free from
/// the engine assemblies — the UI talks to NeoReports only over HTTP — so this mirrors the wire
/// shape with local records rather than reusing engine types.
/// </summary>
public static class BuilderConfigMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes <paramref name="state"/> into a report config JSON document.</summary>
    /// <param name="state">The wizard state to map. <see cref="BuilderState.ReportName"/> and the
    /// other "real" fields (source/query/columns/formats/destination) are used; cosmetic fields
    /// (schedule, template metadata) are not part of the output.</param>
    public static string ToConfigJson(BuilderState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var columns = state.ColumnNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => new ColumnDocument(name, "String"))
            .ToArray();

        var sourceProperties = new Dictionary<string, object?>
        {
            ["sql"] = state.SqlQuery,
            ["key"] = state.KeyColumn,
        };
        if (!string.IsNullOrWhiteSpace(state.ConnectionStringVariable))
            sourceProperties["connectionString"] = $"${{{state.ConnectionStringVariable}}}";

        var outputs = state.Formats
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(format => new OutputDocument(format))
            .ToArray();

        DestinationDocument[]? destinations = string.IsNullOrWhiteSpace(state.DestinationType)
            ? null
            : new[] { new DestinationDocument(state.DestinationType, BuildDestinationProperties(state)) };

        var resilience = new ResilienceDocument(
            MaxAttempts: state.RetryMaxAttempts,
            Backoff: state.RetryBackoff,
            BaseDelaySeconds: state.RetryBaseDelaySeconds,
            Jitter: state.RetryJitter,
            OnFailure: state.FailureStrategy,
            AbortWhen: BuildAbortWhen(state));

        var document = new ConfigDocument(
            Name: state.ReportName,
            Source: new SourceDocument(state.SourceType, sourceProperties),
            Columns: columns,
            Outputs: outputs,
            Destinations: destinations,
            PageSize: state.PageSize,
            Resilience: resilience);

        return JsonSerializer.Serialize(document, Json);
    }

    private static Dictionary<string, object?>? BuildDestinationProperties(BuilderState state) =>
        string.IsNullOrWhiteSpace(state.DestinationPath)
            ? null
            : new Dictionary<string, object?> { ["path"] = state.DestinationPath };

    // Only meaningful (and only legal engine-side) alongside "skip-and-log" — omitted entirely
    // otherwise, rather than sent and rejected by the compiler (ADR D37).
    private static AbortWhenDocument? BuildAbortWhen(BuilderState state)
    {
        if (!string.Equals(state.FailureStrategy, "skip-and-log", StringComparison.OrdinalIgnoreCase))
            return null;

        int? consecutive = state.AbortOnConsecutiveFailures ? state.AbortConsecutiveFailures : null;
        int? total = state.AbortOnTotalFailures ? state.AbortTotalFailures : null;
        double? rate = state.AbortOnFailureRate ? state.AbortFailureRatePercent / 100.0 : null;

        return consecutive is null && total is null && rate is null
            ? null
            : new AbortWhenDocument(consecutive, total, rate);
    }

    private sealed record ConfigDocument(
        string Name,
        SourceDocument Source,
        IReadOnlyList<ColumnDocument> Columns,
        IReadOnlyList<OutputDocument> Outputs,
        IReadOnlyList<DestinationDocument>? Destinations,
        int PageSize,
        ResilienceDocument Resilience);

    private sealed record SourceDocument(string Type, IReadOnlyDictionary<string, object?> Properties);

    private sealed record ColumnDocument(string Name, string Type);

    private sealed record OutputDocument(string Format);

    private sealed record DestinationDocument(string Type, IReadOnlyDictionary<string, object?>? Properties);

    private sealed record ResilienceDocument(
        int MaxAttempts, string Backoff, double BaseDelaySeconds, bool Jitter, string OnFailure, AbortWhenDocument? AbortWhen);

    private sealed record AbortWhenDocument(int? ConsecutiveFailures, int? TotalFailures, double? FailureRate);
}
