namespace NeoReports.Destinations.Local;

/// <summary>
/// Guards the values substituted into a <see cref="LocalDestination"/> path template. The template
/// itself is trusted author configuration (it may legitimately be an absolute path), but the values
/// filling its <c>{name}</c>/<c>{ext}</c>/<c>{paramName}</c> tokens can be partly caller-controlled —
/// run-time parameters in particular arrive from a report-run request. A value must therefore be an
/// inert single path segment: it may not carry a directory separator, a drive/alternate-data-stream
/// colon, or a <c>..</c> component, any of which would let <c>Path.GetFullPath</c> escape the
/// directory the template's own literal structure intended.
/// </summary>
internal static class LocalPathSegment
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it is a safe single path segment; throws
    /// <see cref="ArgumentException"/> otherwise.
    /// </summary>
    /// <param name="tokenName">The template token being filled, for the error message.</param>
    /// <param name="value">The substituted value to validate.</param>
    public static string EnsureSafe(string tokenName, string value)
    {
        if (value.Length == 0)
            return value;

        // Reject a standalone "."/".." component and any directory separator. With separators
        // rejected the value is a single segment, so the standalone dot check fully covers
        // traversal; a bare "a..b" (no separator) is an ordinary, safe file-name fragment and stays
        // allowed. ':' is rejected only on Windows, where it introduces a drive prefix ("C:..") or an
        // NTFS alternate-data-stream ("name:stream") that redirects the write off the intended path;
        // on other platforms ':' is an ordinary, safe file-name character (e.g. a "HH:mm:ss" value).
        var rejected = value is "." or ".."
            || value.IndexOfAny(UniversalSeparators) >= 0
            || (OperatingSystem.IsWindows() && value.Contains(':', StringComparison.Ordinal));

        if (rejected)
        {
            throw new ArgumentException(
                $"The Local destination path template token '{{{tokenName}}}' resolved to \"{value}\", " +
                "which contains a path separator or is a '..' component. A substituted value must be a " +
                "single path segment so it cannot escape the target directory; put any directory " +
                "structure in the template itself (e.g. \"{name}/{ext}\") instead of in the value.",
                nameof(value));
        }

        return value;
    }

    // '/' and '\' are treated as separators on every platform: '\' is the Windows separator, and
    // rejecting it on other platforms too (where it is a legal file-name char) is harmless
    // over-strictness that also guards a path later consumed on Windows.
    private static readonly char[] UniversalSeparators = ['/', '\\'];
}
