namespace NeoReports.Core.SourceRegistry;

/// <summary>Result of an on-demand source health check (ADR D42).</summary>
/// <param name="Healthy"><c>true</c> when the check succeeded.</param>
/// <param name="Error">Failure detail, when <paramref name="Healthy"/> is <c>false</c>.</param>
/// <param name="Latency">How long the check took.</param>
public sealed record SourceHealthResult(bool Healthy, string? Error, TimeSpan Latency);

/// <summary>
/// Checks whether a registered source is reachable (ADR D42). Resolved from DI by
/// <see cref="Type"/>, exactly like <c>IConfigSourceProvider</c> — provider-type-extensible,
/// nothing source-package-specific lives in Core. Checks run **on demand only** (no background
/// poller — a stale reading presented as current is the D36 fabricated-telemetry pattern this
/// project deliberately avoids); the caller is responsible for bounding the check with a timeout.
/// </summary>
public interface ISourceHealthCheck
{
    /// <summary>Provider type id this check handles (matches <c>IConfigSourceProvider.Type</c>); matched case-insensitively.</summary>
    string Type { get; }

    /// <summary>Runs the check against the given (already <c>${VAR}</c>-substituted) definition.</summary>
    /// <param name="definition">The substituted source definition to check.</param>
    /// <param name="services">The service provider, for resolving dependencies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken);
}
