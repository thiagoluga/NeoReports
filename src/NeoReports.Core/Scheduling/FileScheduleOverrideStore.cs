using System.Text.Json;
using NeoReports.Core.Configuration;

namespace NeoReports.Core.Scheduling;

/// <summary>
/// File-backed <see cref="IScheduleOverrideStore"/>: one <c>{reportName}.json</c> file per override
/// in a configured directory, containing the serialized <see cref="ScheduleOverrideEntry"/> (a null
/// <c>cron</c> is the tombstone). Writes go through a temp file plus an atomic move, the same
/// pattern <see cref="FileReportConfigStore"/> uses.
/// </summary>
public sealed class FileScheduleOverrideStore : IScheduleOverrideStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;

    /// <summary>Creates a store rooted at <paramref name="directory"/>. The directory is created on first write, not here.</summary>
    /// <param name="directory">Directory where <c>{reportName}.json</c> override entries are persisted.</param>
    public FileScheduleOverrideStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <inheritdoc />
    public async Task SaveAsync(string reportName, ScheduleOverrideEntry entry, CancellationToken cancellationToken)
    {
        ValidateName(reportName);
        ArgumentNullException.ThrowIfNull(entry);

        Directory.CreateDirectory(_directory);
        string finalPath = GetPath(reportName);
        string tempPath = finalPath + ".tmp";
        string document = JsonSerializer.Serialize(entry, Json);
        await File.WriteAllTextAsync(tempPath, document, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    /// <inheritdoc />
    public async Task<ScheduleOverrideEntry?> GetAsync(string reportName, CancellationToken cancellationToken)
    {
        ValidateName(reportName);
        string path = GetPath(reportName);
        if (!File.Exists(path))
            return null;

        string document = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ScheduleOverrideEntry>(document, Json);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string reportName, CancellationToken cancellationToken)
    {
        ValidateName(reportName);
        string path = GetPath(reportName);
        if (!File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string ReportName, ScheduleOverrideEntry Entry)>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<(string, ScheduleOverrideEntry)>();

        var results = new List<(string, ScheduleOverrideEntry)>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string document = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            ScheduleOverrideEntry? entry = JsonSerializer.Deserialize<ScheduleOverrideEntry>(document, Json);
            if (entry is not null)
                results.Add((name, entry));
        }

        return results;
    }

    // Path.Join (unlike Path.Combine) never drops _directory even if reportName were ever rooted.
    private string GetPath(string reportName) => Path.Join(_directory, $"{reportName}.json");

    private static void ValidateName(string reportName)
    {
        if (!DynamicReportName.IsValid(reportName))
            throw new ArgumentException($"'{reportName}' is not a valid report name (must match {DynamicReportName.Pattern}).", nameof(reportName));
    }
}
