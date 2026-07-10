using System.Collections.Concurrent;

namespace NeoReports.Core.SourceRegistry;

/// <summary>A cached, timestamped health check outcome for one source (ADR D42).</summary>
/// <param name="Healthy"><c>true</c> when the check succeeded.</param>
/// <param name="Error">Failure detail, when <paramref name="Healthy"/> is <c>false</c>.</param>
/// <param name="LatencyMs">How long the check took, in milliseconds.</param>
/// <param name="CheckedAt">When the check ran (UTC).</param>
public sealed record SourceHealthReading(bool Healthy, string? Error, double LatencyMs, DateTimeOffset CheckedAt);

/// <summary>
/// Caches the most recent health reading per source (ADR D42) — in-process only, deliberately not
/// persisted: a health reading surviving a restart and presented as current would itself be the
/// D36 fabricated-telemetry pattern; "never checked" is the honest state after a fresh start.
/// Single-server (D2), so no cross-process invalidation is needed.
/// </summary>
public interface ISourceHealthCache
{
    /// <summary>Records the outcome of a health check just run for a source.</summary>
    /// <param name="name">The source name.</param>
    /// <param name="reading">The reading to cache.</param>
    void Set(string name, SourceHealthReading reading);

    /// <summary>Reads the most recent cached reading for a source, or <c>null</c> when it was never checked.</summary>
    /// <param name="name">The source name.</param>
    SourceHealthReading? Get(string name);

    /// <summary>Removes any cached reading for a source (called when the source itself is deleted).</summary>
    /// <param name="name">The source name.</param>
    void Remove(string name);
}

/// <summary>Default <see cref="ISourceHealthCache"/> — an in-process, thread-safe map.</summary>
public sealed class InMemorySourceHealthCache : ISourceHealthCache
{
    private readonly ConcurrentDictionary<string, SourceHealthReading> _readings = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Set(string name, SourceHealthReading reading) => _readings[name] = reading;

    /// <inheritdoc />
    public SourceHealthReading? Get(string name) => _readings.TryGetValue(name, out var reading) ? reading : null;

    /// <inheritdoc />
    public void Remove(string name) => _readings.TryRemove(name, out _);
}
