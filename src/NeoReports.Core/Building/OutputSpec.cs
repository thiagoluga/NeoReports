using NeoReports.Abstractions;

namespace NeoReports.Core.Building;

/// <summary>
/// A configured output: the writer factory that produces the format plus its options. Format
/// packages (CSV, XLSX) return one of these from their fluent entry points.
/// </summary>
public sealed class OutputSpec
{
    /// <summary>Creates an output specification.</summary>
    /// <param name="factory">Factory that creates the writer.</param>
    /// <param name="options">Format-specific options; <c>null</c> is treated as empty.</param>
    public OutputSpec(IWriterFactory factory, IReadOnlyDictionary<string, object?>? options = null)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Options = options ?? new Dictionary<string, object?>();
    }

    /// <summary>Factory that creates the writer for this output.</summary>
    public IWriterFactory Factory { get; }

    /// <summary>Format-specific options.</summary>
    public IReadOnlyDictionary<string, object?> Options { get; }
}

/// <summary>
/// A configured destination: the destination factory plus its options. Destination packages
/// (Local, S3) return one of these from their fluent entry points.
/// </summary>
public sealed class DestinationSpec
{
    /// <summary>Creates a destination specification.</summary>
    /// <param name="factory">Factory that creates the destination.</param>
    /// <param name="options">Destination-specific options; <c>null</c> is treated as empty.</param>
    public DestinationSpec(IDestinationFactory factory, IReadOnlyDictionary<string, object?>? options = null)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Options = options ?? new Dictionary<string, object?>();
    }

    /// <summary>Factory that creates the destination.</summary>
    public IDestinationFactory Factory { get; }

    /// <summary>Destination-specific options.</summary>
    public IReadOnlyDictionary<string, object?> Options { get; }
}
