using System.Collections.Concurrent;
using NeoReports.Core.Configuration;

namespace NeoReports.Core.SourceRegistry;

/// <summary>
/// In-process <see cref="ISourceRegistryStore"/> — same behavior as
/// <see cref="FileSourceRegistryStore"/>, but definitions are lost on restart. Intended for tests
/// and single-process dev hosts.
/// </summary>
public sealed class InMemorySourceRegistryStore : ISourceRegistryStore
{
    private readonly ConcurrentDictionary<string, SourceDefinition> _definitions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(SourceDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.Name);
        _definitions[definition.Name] = definition;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SourceDefinition?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        return Task.FromResult(_definitions.TryGetValue(name, out SourceDefinition? definition) ? definition : null);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        return Task.FromResult(_definitions.TryRemove(name, out _));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SourceDefinition>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceDefinition>>(
            _definitions.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToArray());

    private static void ValidateName(string name)
    {
        if (!DynamicReportName.IsValid(name))
            throw new ArgumentException($"'{name}' is not a valid source name (must match {DynamicReportName.Pattern}).", nameof(name));
    }
}
