using NeoReports.Abstractions;

namespace NeoReports.Destinations.Local;

/// <summary>
/// Writes the finished report file to the local filesystem at a path resolved from a template.
/// The write is atomic: content goes to a temporary file in the target directory and is moved
/// into place only after a successful copy, so a failure never leaves a partial file.
/// </summary>
public sealed class LocalDestination : IReportDestination
{
    private readonly string _pathTemplate;

    /// <summary>Creates the destination.</summary>
    /// <param name="pathTemplate">Path template (e.g. <c>./out/{name}-{date:yyyy-MM-dd}.{ext}</c>).</param>
    public LocalDestination(string pathTemplate)
    {
        if (string.IsNullOrWhiteSpace(pathTemplate))
            throw new ArgumentException("Path template must be provided.", nameof(pathTemplate));
        _pathTemplate = pathTemplate;
    }

    /// <inheritdoc />
    public string Type => "local";

    /// <inheritdoc />
    public async Task<UploadResult> UploadAsync(ReportFile file, DestinationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(context);

        var extension = Path.GetExtension(file.FileName).TrimStart('.');
        var resolved = PathTemplate.Expand(
            _pathTemplate,
            context.Execution.ReportName,
            extension,
            context.Execution.StartedAt,
            context.Execution.Parameters);

        var fullPath = Path.GetFullPath(resolved);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var source = file.OpenRead())
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            // Atomic publish: overwrite any existing file in one move.
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return UploadResult.Fail($"Local upload to '{fullPath}' failed: {ex.Message}");
        }

        return UploadResult.Ok(new Uri(fullPath).AbsoluteUri, fullPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
