namespace NeoReports.Abstractions;

// Factories let sources/formats/destinations be registered by a stable string id.
// Built-in plugins use exactly these contracts — there is no privileged internal API.

/// <summary>Creates an <see cref="IReportSource"/> from registration-time configuration.</summary>
public interface ISourceFactory
{
    /// <summary>Stable source type id (e.g. "sql").</summary>
    string Type { get; }
}

/// <summary>Creates an <see cref="IReportWriter"/> from registration-time configuration.</summary>
public interface IWriterFactory
{
    /// <summary>Stable format id (e.g. "csv", "xlsx").</summary>
    string Format { get; }

    /// <summary>Creates a writer instance.</summary>
    /// <param name="options">Format-specific options captured at registration time.</param>
    /// <param name="services">The service provider for resolving dependencies.</param>
    /// <returns>A new writer.</returns>
    IReportWriter Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services);
}

/// <summary>Creates an <see cref="IReportDestination"/> from registration-time configuration.</summary>
public interface IDestinationFactory
{
    /// <summary>Stable destination type id (e.g. "local", "s3").</summary>
    string Type { get; }

    /// <summary>Creates a destination instance.</summary>
    /// <param name="options">Destination-specific options captured at registration time.</param>
    /// <param name="services">The service provider for resolving dependencies.</param>
    /// <returns>A new destination.</returns>
    IReportDestination Create(IReadOnlyDictionary<string, object?> options, IServiceProvider services);
}
