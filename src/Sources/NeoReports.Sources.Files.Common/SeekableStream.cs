namespace NeoReports.Sources.Files.Common;

/// <summary>
/// Guarantees a readable, seekable <see cref="Stream"/> for file formats whose reader needs random
/// access (ADR D60) — the mirror-image of the <c>DocumentFormat.OpenXml</c> finding in D59. Unlike
/// OpenXml, which transparently buffers a non-seekable stream, <c>Parquet.Net</c>'s reader throws
/// immediately ("stream must be readable and seekable") when handed one — the Parquet footer lives at
/// the end of the file and is read by seeking, so a forward-only body (an S3 <c>GetObject</c>
/// response) cannot be consumed directly. A local <see cref="FileStream"/> is already seekable and is
/// returned untouched at no cost; a non-seekable body is copied once into a self-deleting temp file
/// and that is returned instead. This is genuinely reusable file-source infrastructure — any future
/// binary format with a trailing directory would need the same guarantee — so it lives here alongside
/// <see cref="S3Stream"/> rather than inside a single format package.
/// </summary>
public static class SeekableStream
{
    /// <summary>
    /// Returns <paramref name="stream"/> unchanged when it is already seekable; otherwise copies it in
    /// full into a temp file opened with <see cref="FileOptions.DeleteOnClose"/> (so the OS removes the
    /// file automatically once the returned stream is disposed — no manual temp-file bookkeeping) and
    /// returns that, rewound to the start. The original stream is always disposed once it has been
    /// consumed or the copy has failed; callers dispose only the returned stream.
    /// </summary>
    /// <param name="stream">The stream to make seekable. Ownership transfers to this method.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    public static async Task<Stream> EnsureSeekableAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek)
            return stream;

        // A security review caught that Path.GetTempFileName() followed by reopening the same path
        // with FileMode.Create reintroduces a TOCTOU window GetTempFileName()'s own atomic creation
        // exists to avoid (the second open follows whatever is at that path by the time it runs,
        // including a symlink swapped in during the gap). Path.GetRandomFileName() + FileMode.CreateNew
        // creates and opens the file in one atomic operation instead — no separate "create a name" step,
        // and CreateNew fails outright (rather than silently succeeding) if anything already exists at
        // that path.
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var temp = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await stream.CopyToAsync(temp, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Disposing the temp FileStream removes the file (DeleteOnClose), leaving nothing behind.
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            // The forward-only source is fully drained (or the copy failed) — either way it is no
            // longer needed and its owner is this method, so dispose it here rather than leaking it.
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        temp.Position = 0;
        return temp;
    }
}
