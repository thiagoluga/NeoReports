namespace NeoReports.Core.Configuration;

/// <summary>
/// File-backed <see cref="IReportConfigStore"/>: one <c>{name}.json</c> file per report in a
/// configured directory. Writes go through a temp file plus an atomic move so a crash mid-write
/// never leaves a truncated document; leftover <c>.tmp</c> files are naturally excluded by
/// <see cref="ListAsync"/>'s <c>*.json</c> filter.
/// </summary>
public sealed class FileReportConfigStore : IReportConfigStore
{
    private readonly string _directory;

    /// <summary>Creates a store rooted at <paramref name="directory"/>. The directory is created on first write, not here.</summary>
    /// <param name="directory">Directory where <c>{name}.json</c> documents are persisted.</param>
    public FileReportConfigStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <inheritdoc />
    public async Task SaveAsync(string name, string configDocument, CancellationToken cancellationToken)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(configDocument);

        Directory.CreateDirectory(_directory);
        string finalPath = GetPath(name);
        string tempPath = finalPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, configDocument, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, finalPath, overwrite: true);
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
    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        return Task.FromResult(File.Exists(GetPath(name)));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string Name, string Document)>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<(string, string)>();

        var results = new List<(string, string)>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string document = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            results.Add((name, document));
        }

        return results;
    }

    private string GetPath(string name) => Path.Combine(_directory, $"{name}.json");

    private static void ValidateName(string name)
    {
        if (!DynamicReportName.IsValid(name))
            throw new ArgumentException($"'{name}' is not a valid dynamic report name (must match {DynamicReportName.Pattern}).", nameof(name));
    }
}
