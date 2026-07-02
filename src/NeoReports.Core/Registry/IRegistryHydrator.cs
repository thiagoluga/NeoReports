namespace NeoReports.Core.Registry;

/// <summary>
/// Populates a registry with additional reports the first time <see cref="IReportRegistry"/> is
/// resolved. Implementations are resolved via <c>IEnumerable&lt;IRegistryHydrator&gt;</c> from DI,
/// so any number of them can run regardless of the order they were registered in — the factory
/// that builds <see cref="IReportRegistry"/> runs them all after compiling code-first config
/// (<c>ReportConfig</c> singletons). Internal: this is a wiring detail of
/// <c>ServiceCollectionExtensions</c>, not part of the public dynamic-registration surface.
/// </summary>
internal interface IRegistryHydrator
{
    /// <summary>Registers whatever reports this hydrator is responsible for into <paramref name="registry"/>.</summary>
    /// <param name="registry">The registry to populate.</param>
    /// <param name="services">The fully built service provider, for resolving providers/factories a compile may need.</param>
    void Hydrate(IMutableReportRegistry registry, IServiceProvider services);
}
