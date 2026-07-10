namespace NeoReports.Core.SourceRegistry;

/// <summary>
/// A named, persisted source instance (ADR D42) — a specific connection, distinct from the
/// provider *type* capabilities report (<c>GET /capabilities</c>). Properties may hold
/// <c>${VAR}</c> placeholders (D33), substituted at resolve time by <see cref="ISourceRegistry"/>
/// — never baked into a compiled report — so rotating a connection string takes effect on the
/// next run of every referencing report without recompiles.
/// </summary>
/// <param name="Name">Unique source name; validated with the same rules as a dynamic report name.</param>
/// <param name="Type">Stable provider type id (e.g. "sql"), matching an <c>IConfigSourceProvider.Type</c>.</param>
/// <param name="Properties">Provider-specific settings (e.g. connection string, as <c>${VAR}</c>).</param>
/// <param name="Description">Optional free-text description.</param>
public sealed record SourceDefinition(
    string Name,
    string Type,
    IReadOnlyDictionary<string, object?>? Properties = null,
    string? Description = null);
