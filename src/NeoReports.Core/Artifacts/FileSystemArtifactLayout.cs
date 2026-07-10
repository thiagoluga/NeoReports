namespace NeoReports.Core.Artifacts;

/// <summary>
/// Shared filesystem primitives for artifact stores that lay files out as
/// <c>{root}/{jobId}/{fileName}</c> with a <c>.mime</c> sidecar. Used by both
/// <see cref="FileSystemArtifactStore"/> and <see cref="FileSystemPartialArtifactStore"/>, which
/// stay separate types (ADR D40) even though the on-disk shape is identical.
/// </summary>
internal static class FileSystemArtifactLayout
{
    public static string JobDir(string root, string jobId)
    {
        // Guard against path traversal from a caller-supplied job id.
        var safe = Path.GetFileName(jobId);
        if (string.IsNullOrEmpty(safe) || safe != jobId)
            throw new ArgumentException("Invalid job id.", nameof(jobId));
        return Path.Join(root, safe);
    }

    public static async Task SaveAsync(string dir, string sourcePath, string fileName, string mimeType, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dir);

        // Strip any path components from the caller-supplied file name so it cannot escape the
        // job directory (e.g. "../x" or an absolute path, which Path.Combine would otherwise honor).
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safeFileName))
            throw new ArgumentException("File name must be a simple file name.", nameof(fileName));
        var target = Path.Join(dir, safeFileName);

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
        }

        // Sidecar with the MIME type so List can report it without guessing from the extension.
        await File.WriteAllTextAsync(target + ".mime", mimeType, cancellationToken).ConfigureAwait(false);
    }

    public static Task<IReadOnlyList<ReportArtifact>> ListAsync(string dir)
    {
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<ReportArtifact>>(Array.Empty<ReportArtifact>());

        IReadOnlyList<ReportArtifact> artifacts = Directory.EnumerateFiles(dir)
            .Where(path => !path.EndsWith(".mime", StringComparison.Ordinal))
            .Select(path =>
            {
                var mimePath = path + ".mime";
                var mime = File.Exists(mimePath) ? File.ReadAllText(mimePath) : "application/octet-stream";
                var info = new FileInfo(path);
                return new ReportArtifact(Path.GetFileName(path), mime, path, info.Length);
            })
            .ToArray();

        return Task.FromResult(artifacts);
    }

    public static void Delete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a locked/permission-denied artifact dir must not fail the caller.
        }
    }
}
