using NeoReports.Abstractions;

namespace NeoReports.Destinations.Local;

/// <summary>Creates <see cref="LocalDestination"/> instances from a captured path template.</summary>
public sealed class LocalDestinationFactory : IDestinationFactory
{
    private readonly string _pathTemplate;

    /// <summary>Creates the factory.</summary>
    /// <param name="pathTemplate">Path template applied to every created destination.</param>
    public LocalDestinationFactory(string pathTemplate) => _pathTemplate = pathTemplate;

    /// <inheritdoc />
    public string Type => "local";

    /// <inheritdoc />
    public IReportDestination Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services) =>
        new LocalDestination(_pathTemplate);
}
