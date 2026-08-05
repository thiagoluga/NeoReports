namespace NeoReports.Destinations.S3;

/// <summary>
/// Guards the values substituted into an <see cref="S3Destination"/> key template.
/// <para>
/// The template itself is trusted author configuration and <b>may</b> contain <c>/</c> — that is how
/// a key hierarchy is written, and nothing here restricts it. The values filling its
/// <c>{name}</c>/<c>{ext}</c>/<c>{paramName}</c> tokens are a different matter: run-time parameters
/// arrive in the body of a report-run request. With a template such as
/// <c>reports/{tenant}/{name}.{ext}</c>, a caller posting a <c>tenant</c> containing <c>/</c> moves
/// the object into a prefix the template never described — a cross-tenant write wherever a shared
/// bucket relies on prefix isolation.
/// </para>
/// <para>
/// This is deliberately narrower than the Local destination's guard. S3 keys are literal: <c>..</c>
/// is not collapsed and there is no drive or alternate-data-stream syntax, so none of that is a
/// traversal risk here and none of it is rejected. The only thing a substituted value must not do is
/// introduce hierarchy the author did not write.
/// </para>
/// </summary>
internal static class S3KeySegment
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it introduces no key separator; throws
    /// <see cref="ArgumentException"/> otherwise.
    /// </summary>
    /// <param name="tokenName">The template token being filled, for the error message.</param>
    /// <param name="value">The substituted value to validate.</param>
    public static string EnsureSafe(string tokenName, string value)
    {
        if (!value.Contains('/', StringComparison.Ordinal))
            return value;

        throw new ArgumentException(
            $"The S3 destination key template token '{{{tokenName}}}' resolved to \"{value}\", which " +
            "contains '/'. A substituted value may not introduce key hierarchy, because that would let " +
            "it place the object under a prefix the template did not describe. Put the hierarchy in the " +
            "template itself (e.g. \"reports/{tenant}/{name}.{ext}\") rather than inside a value.",
            nameof(value));
    }
}
