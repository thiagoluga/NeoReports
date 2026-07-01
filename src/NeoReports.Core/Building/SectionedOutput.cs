using NeoReports.Core.Sections;

namespace NeoReports.Core.Building;

/// <summary>
/// A configured sectioned output: the factory that produces a single-file, multi-section writer
/// (e.g. an XLSX workbook) plus its options. Section (sheet) definitions are supplied on the builder.
/// </summary>
public sealed class SectionedOutputSpec
{
    /// <summary>Creates a sectioned output specification.</summary>
    /// <param name="factory">Factory that creates the sectioned writer.</param>
    /// <param name="options">Format-specific options; <c>null</c> is treated as empty.</param>
    public SectionedOutputSpec(ISectionedWriterFactory factory, IReadOnlyDictionary<string, object?>? options = null)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Options = options ?? new Dictionary<string, object?>();
    }

    /// <summary>Factory that creates the sectioned writer.</summary>
    public ISectionedWriterFactory Factory { get; }

    /// <summary>Format-specific options.</summary>
    public IReadOnlyDictionary<string, object?> Options { get; }
}

/// <summary>Collects the sections (e.g. worksheets) of a sectioned output, each with its own view.</summary>
/// <typeparam name="TRow">The report's row type.</typeparam>
public sealed class SectionBuilder<TRow>
{
    internal List<SectionDefinition<TRow>> Sections { get; } = [];

    /// <summary>Adds a named section with its own filters and/or columns.</summary>
    /// <param name="name">Section (sheet) name.</param>
    /// <param name="configureView">Configures the section's filters and columns.</param>
    public SectionBuilder<TRow> Section(string name, Action<OutputView<TRow>> configureView)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Section name must be provided.", nameof(name));
        ArgumentNullException.ThrowIfNull(configureView);

        var view = new OutputView<TRow>();
        configureView(view);
        Sections.Add(new SectionDefinition<TRow>(name, view));
        return this;
    }
}

/// <summary>One section's name and its view (filters + columns).</summary>
internal sealed record SectionDefinition<TRow>(string Name, OutputView<TRow> View);
