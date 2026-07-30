namespace NeoReports.Core;

/// <summary>
/// Writes a file atomically: the content goes to a temp file first and is then moved over the final
/// path, so a reader never observes a half-written document and a crash mid-write leaves the previous
/// version intact.
/// </summary>
internal static class AtomicFileWrite
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="finalPath"/> through a unique temp file
    /// in the same directory (so the move stays on one volume and therefore atomic).
    /// </summary>
    /// <param name="finalPath">The destination path.</param>
    /// <param name="content">The text to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteAsync(string finalPath, string content, CancellationToken cancellationToken)
    {
        // The temp name must be unique per write, not derived from the destination alone: two
        // concurrent saves of the same document would otherwise open and move the SAME temp file,
        // failing with a sharing violation or a FileNotFound when the second move finds the first
        // already took it. The ".tmp" suffix also keeps it out of the "*.json" listings these stores do.
        string tempPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            // A unique temp name can't be reclaimed by the next attempt the way a fixed one was, so
            // clean it up here rather than leaving an orphan behind on every failed save.
            TryDelete(tempPath);
            throw;
        }
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
            // Best-effort cleanup: a leftover temp file must never mask the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
