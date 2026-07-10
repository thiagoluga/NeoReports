using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Core.Configuration;

namespace NeoReports.Core.SourceRegistry;

/// <summary>
/// File-backed <see cref="ISourceRegistryStore"/>: one <c>{name}.json</c> file per source in a
/// configured directory. Writes go through a temp file plus an atomic move, the same pattern
/// <see cref="FileReportConfigStore"/> uses. A corrupt file is logged (sanitized) and skipped at
/// load rather than crashing the host — the same resilience <c>FileStoreRegistryHydrator</c> gives
/// dynamic reports.
/// </summary>
public sealed class FileSourceRegistryStore : ISourceRegistryStore
{
    private static readonly JsonSerializerOptions Json = CreateOptions();

    private readonly string _directory;
    private readonly ILogger<FileSourceRegistryStore> _logger;

    /// <summary>Creates a store rooted at <paramref name="directory"/>. The directory is created on first write, not here.</summary>
    /// <param name="directory">Directory where <c>{name}.json</c> source definitions are persisted.</param>
    /// <param name="logger">Logger used to report (and skip) corrupt files at load.</param>
    public FileSourceRegistryStore(string directory, ILogger<FileSourceRegistryStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _logger = logger ?? NullLogger<FileSourceRegistryStore>.Instance;
    }

    /// <inheritdoc />
    public async Task SaveAsync(SourceDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.Name);

        Directory.CreateDirectory(_directory);
        string finalPath = GetPath(definition.Name);
        string tempPath = finalPath + ".tmp";
        string document = JsonSerializer.Serialize(definition, Json);
        await File.WriteAllTextAsync(tempPath, document, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    /// <inheritdoc />
    public async Task<SourceDefinition?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        string path = GetPath(name);
        if (!File.Exists(path))
            return null;

        string document = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return TryDeserialize(name, document);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        string path = GetPath(name);
        if (!File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<SourceDefinition>();

        var results = new List<SourceDefinition>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string document = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            SourceDefinition? definition = TryDeserialize(name, document);
            if (definition is not null)
                results.Add(definition);
        }

        return results;
    }

    private SourceDefinition? TryDeserialize(string name, string document)
    {
        try
        {
            return JsonSerializer.Deserialize<SourceDefinition>(document, Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Skipping corrupt source definition file for '{Name}'.", name);
            return null;
        }
    }

    // Path.Join (unlike Path.Combine) never drops _directory even if name were ever rooted.
    private string GetPath(string name) => Path.Join(_directory, $"{name}.json");

    private static void ValidateName(string name)
    {
        if (!DynamicReportName.IsValid(name))
            throw new ArgumentException($"'{name}' is not a valid source name (must match {DynamicReportName.Pattern}).", nameof(name));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new PrimitiveObjectConverter());
        return options;
    }
}
