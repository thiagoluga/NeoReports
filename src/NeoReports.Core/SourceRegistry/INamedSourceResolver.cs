namespace NeoReports.Core.SourceRegistry;

/// <summary>
/// Implemented by typed by-name sources (e.g. <c>Source.SqlNamed</c>, ADR D42 locked decision 4)
/// that resolve their connection through the <see cref="ISourceRegistry"/> at the start of every
/// run instead of a connection baked in at construction. Typed sources are built by static entry
/// points inside registration lambdas with no <see cref="IServiceProvider"/>, so the Core builder
/// calls <see cref="AttachServices"/> once, right before the source's first read of every run —
/// the only point in the typed pipeline where a service provider is available.
/// </summary>
public interface INamedSourceResolver
{
    /// <summary>The registered source name this instance resolves against.</summary>
    string SourceName { get; }

    /// <summary>Gives the source access to the run's <see cref="IServiceProvider"/>, to resolve <see cref="ISourceRegistry"/> from.</summary>
    /// <param name="services">The host's service provider.</param>
    void AttachServices(IServiceProvider services);
}
