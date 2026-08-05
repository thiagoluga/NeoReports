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

        string fullPath;
        var tempPath = string.Empty;
        try
        {
            var resolved = PathTemplate.Expand(
                _pathTemplate,
                context.Execution.ReportName,
                extension,
                context.Execution.StartedAt,
                context.Execution.Parameters,
                // Substituted values (report name, extension, run-time parameters) must each be a
                // single path segment — a run-time parameter is caller-controlled, so a "../.." in
                // one would otherwise let Path.GetFullPath below escape the directory the template
                // intended and overwrite an arbitrary file. The template's own literal separators
                // are unaffected. A rejection surfaces here as a failed upload, not a partial write.
                LocalPathSegment.EnsureSafe);

            fullPath = Path.GetFullPath(resolved);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");

            await using (var source = file.OpenRead())
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            // Atomic publish: overwrite any existing file in one move.
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancellation is not a destination failure: reporting it as one attributes the
            // operator's (or the deadline's) stop to this destination and buries the real reason.
            // The filter is on the caller's own token, exactly as ReportJobWorker does since #240 —
            // an OperationCanceledException from anything else (an SDK's internal timeout, say) is
            // still a genuine transport failure and is still reported as one. Rethrowing only when
            // OUR token tripped also leaves the runner's multi-destination loop free to carry on
            // reporting per-destination results (ADR D78).
            if (tempPath.Length > 0)
                TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            if (tempPath.Length > 0)
                TryDelete(tempPath);
            return UploadResult.Fail($"Local upload failed: {ex.Message}");
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
