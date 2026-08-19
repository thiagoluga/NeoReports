using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;

namespace NeoReports.AspNetCore;

/// <summary>
/// The entity-tag for a report configuration document (ADR D87). Lets an editor detect that the
/// document changed under it between reading the configuration and saving it back.
/// </summary>
/// <remarks>
/// <para>
/// Computed over the <b>redacted</b> form — the same bytes the client was given — and never over the
/// stored document. Hashing the stored document made the tag a free, offline <i>verification oracle</i>
/// for the very values the endpoint exists to withhold: the redacted body and the stored document are
/// byte-identical apart from the redacted values, so a holder of the body could reconstruct candidate
/// documents, hash them, and confirm a guessed connection string without ever contacting the database.
/// </para>
/// <para>
/// It is still the right validator. What a placeholder's address can be invalidated by is a change to
/// the document's <i>structure</i> — sections added, removed or reordered — and that structure is
/// wholly visible in the redacted body. A change to a secret <i>value</i> moves no address, and an
/// editor that sends a placeholder back is asking for whatever is stored now, so resolving to the
/// newer secret is the correct outcome rather than a conflict.
/// </para>
/// </remarks>
internal static class ReportConfigETag
{
    /// <summary>The strong entity-tag for a stored document, quoted, ready for a header.</summary>
    /// <param name="storedDocument">The document as the config store holds it.</param>
    /// <exception cref="ConfigurationException">Thrown when the document is not readable.</exception>
    /// <remarks>
    /// Takes the <i>stored</i> document and redacts it here rather than offering an overload for the
    /// already-redacted body: one entry point means no call site can hash the wrong form, which is the
    /// mistake this whole type is now shaped to prevent.
    /// </remarks>
    internal static string For(string storedDocument)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ReportConfigSecrets.Redact(storedDocument)));

        // Base64url so the value needs no escaping inside the quotes, and truncated to 128 bits —
        // this detects an edit, it does not authenticate one, and a shorter header reads better in a
        // log. Collisions at this width are not reachable by a report being edited twice.
        return '"' + Convert.ToBase64String(hash, 0, 16).Replace('+', '-').Replace('/', '_').TrimEnd('=') + '"';
    }

    /// <summary>
    /// Whether the request may proceed under RFC 9110 <c>If-Match</c>: no header at all means "no
    /// precondition" (D87 keeps the header optional so clients from before it still work), <c>*</c>
    /// means "if the resource exists", and otherwise one of the supplied tags has to match.
    /// </summary>
    /// <param name="ifMatch">The raw <c>If-Match</c> header values, as the request carries them.</param>
    /// <param name="storedDocument">The document the request is about to be applied to.</param>
    /// <exception cref="ConfigurationException">Thrown when the document is not readable.</exception>
    internal static bool Allows(StringValues ifMatch, string storedDocument)
    {
        if (StringValues.IsNullOrEmpty(ifMatch))
            return true;

        string[] candidates = ifMatch
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        // "*" first, and before computing anything: the tag now costs a parse of the stored document,
        // and a wildcard has already said it does not care what the tag is.
        if (candidates.Contains("*", StringComparer.Ordinal))
            return true;

        // A weak tag ("W/…") never satisfies If-Match, which requires strong comparison — it simply
        // fails to equal the strong tag rather than needing a rule of its own.
        if (candidates.Length > 0)
            return candidates.Contains(For(storedDocument), StringComparer.Ordinal);

        // A header present but empty carries no tag to match, so it states no precondition either.
        return true;
    }
}
