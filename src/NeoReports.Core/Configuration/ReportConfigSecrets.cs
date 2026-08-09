using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NeoReports.Abstractions;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Redacts credential-bearing values out of a stored report configuration document so it can be
/// handed back to an editor, and restores them again when that editor sends the document back
/// (ADR D86).
/// </summary>
/// <remarks>
/// <para>
/// D33(c) settled that GET responses never echo property bags, because a bag may hold a secret;
/// D33(f) deferred report editing for exactly that reason ("needs a secrets round-trip story").
/// This is that story. A redacted value is replaced by the reserved sentinel
/// <see cref="RedactedValue"/>, and <see cref="Restore"/> puts the stored value back when the
/// sentinel comes home unchanged — so an editor can round-trip a property it was never allowed to
/// see, and the alternative (making the user retype a connection string to change a page size) goes
/// away without a secret ever leaving the host.
/// </para>
/// <para>
/// The sentinel deliberately does not match <see cref="ReportConfigEnvironment"/>'s
/// <c>${VAR}</c> pattern (the colon is not legal in an environment variable name), so a sentinel
/// that somehow reaches compilation fails as an unknown property value rather than being resolved
/// as an environment lookup.
/// </para>
/// </remarks>
public static partial class ReportConfigSecrets
{
    /// <summary>
    /// The placeholder a redacted value is replaced by. Reserved: <c>POST /reports</c> rejects it
    /// outright (there is nothing to restore from), and <c>PUT /reports/{name}</c> swaps it back for
    /// the stored value.
    /// </summary>
    public const string RedactedValue = "${neoreports:redacted}";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Substring match, case-insensitive. A denylist would fail open — one unlisted key name and a
    // literal secret ships to the client — so the bias is the other way: match generously and accept
    // that innocent keys get hidden too (e.g. "oauth2TokenEndpoint" contains "token"). Over-matching
    // costs only visibility, never correctness, because Restore puts the value back untouched.
    private static readonly string[] SecretKeyFragments =
    [
        "password", "passwd", "pwd", "passphrase", "secret", "token", "apikey", "api_key",
        "accesskey", "access_key", "privatekey", "private_key", "credential", "connectionstring",
        "connection_string", "signature", "auth", "sas",
    ];

    // Whole-value ${VAR} placeholders, the same shape ReportConfigEnvironment resolves. A value in
    // this form is not a secret — the secret lives in the environment — so it is returned as-is and
    // stays editable, which is the shape the Builder writes for every connection string it manages.
    [GeneratedRegex(@"^\$\{[A-Za-z_][A-Za-z0-9_]*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentPlaceholder();

    /// <summary>
    /// Returns <paramref name="document"/> with every credential-bearing property value replaced by
    /// <see cref="RedactedValue"/>.
    /// </summary>
    /// <param name="document">The stored configuration document.</param>
    /// <exception cref="ConfigurationException">Thrown when the document is not valid JSON.</exception>
    public static string Redact(string document)
    {
        JsonObject root = ParseObject(document);

        foreach (JsonObject bag in PropertyBags(root))
        {
            foreach (string key in bag.Select(pair => pair.Key).ToArray())
            {
                if (ShouldRedact(key, bag[key]))
                    bag[key] = RedactedValue;
            }
        }

        return root.ToJsonString();
    }

    /// <summary>True when any property value in the document is the redaction sentinel.</summary>
    /// <param name="document">The configuration document to inspect.</param>
    /// <exception cref="ConfigurationException">Thrown when the document is not valid JSON.</exception>
    public static bool ContainsRedactedValue(string document) =>
        PropertyBags(ParseObject(document))
            .SelectMany(bag => bag)
            .Any(pair => IsRedacted(pair.Value));

    /// <summary>
    /// Returns <paramref name="document"/> with every <see cref="RedactedValue"/> replaced by the
    /// value the same property holds in <paramref name="storedDocument"/>.
    /// </summary>
    /// <param name="document">The incoming document, as edited by the client.</param>
    /// <param name="storedDocument">The document currently persisted for this report.</param>
    /// <exception cref="ConfigurationException">
    /// Thrown when either document is not valid JSON, or when a sentinel has no counterpart to
    /// restore from — better a rejected edit than one that silently drops a credential.
    /// </exception>
    public static string Restore(string document, string storedDocument)
    {
        JsonObject root = ParseObject(document);
        JsonObject stored = ParseObject(storedDocument);

        // Sections are paired by identity (the source is a singleton; outputs by format id;
        // destinations by type id) rather than by array index, so reordering or adding an output in
        // the editor cannot restore a secret into the wrong section.
        foreach ((JsonObject bag, JsonObject? storedBag, string section) in PairedPropertyBags(root, stored))
        {
            foreach (string key in bag.Select(pair => pair.Key).ToArray())
            {
                if (!IsRedacted(bag[key]))
                    continue;

                if (storedBag is null || !HasMember(storedBag, key))
                {
                    throw new ConfigurationException(
                        $"The '{section}' property '{key}' was sent as a redacted placeholder, but the stored " +
                        "configuration has no value to restore for it. Send the real value instead.");
                }

                bag[key] = Member(storedBag, key)?.DeepClone();
            }
        }

        return root.ToJsonString();
    }

    private static JsonObject ParseObject(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
            throw new ConfigurationException("Report configuration document is empty.");

        try
        {
            return JsonNode.Parse(document, documentOptions: ParseOptions) as JsonObject
                ?? throw new ConfigurationException("Report configuration JSON must be an object.");
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"Invalid report configuration JSON: {ex.Message}", ex);
        }
    }

    private static bool ShouldRedact(string key, JsonNode? value)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? text) || text is null)
            return false;

        if (EnvironmentPlaceholder().IsMatch(text))
            return false;

        if (SecretKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Value-based, and independent of the key name: a URL carrying userinfo
        // ("https://user:pass@host/…") is a credential wherever it is spelled, including under a key
        // as innocuous as "url".
        return Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsRedacted(JsonNode? value) =>
        value is JsonValue jsonValue
        && jsonValue.TryGetValue(out string? text)
        && string.Equals(text, RedactedValue, StringComparison.Ordinal);

    private static IEnumerable<JsonObject> PropertyBags(JsonObject root) =>
        PairedPropertyBags(root, stored: null).Select(pair => pair.Bag);

    // Walks the three places a config document holds a free-form property bag, pairing each with its
    // counterpart in `stored` when one is given. Section names double as the label in restore errors.
    private static IEnumerable<(JsonObject Bag, JsonObject? Stored, string Section)> PairedPropertyBags(
        JsonObject root, JsonObject? stored)
    {
        if (Member(root, "source") is JsonObject source && Member(source, "properties") is JsonObject sourceBag)
        {
            JsonObject? storedBag = stored is not null
                && Member(stored, "source") is JsonObject storedSource
                    ? Member(storedSource, "properties") as JsonObject
                    : null;

            yield return (sourceBag, storedBag, "source");
        }

        foreach (var pair in SectionBags(root, stored, "outputs", "format"))
            yield return pair;

        foreach (var pair in SectionBags(root, stored, "destinations", "type"))
            yield return pair;
    }

    private static IEnumerable<(JsonObject Bag, JsonObject? Stored, string Section)> SectionBags(
        JsonObject root, JsonObject? stored, string arrayName, string identityKey)
    {
        if (Member(root, arrayName) is not JsonArray array)
            yield break;

        JsonArray? storedArray = stored is null ? null : Member(stored, arrayName) as JsonArray;

        foreach (JsonNode? element in array)
        {
            if (element is not JsonObject section || Member(section, "properties") is not JsonObject bag)
                continue;

            string? identity = Identity(section, identityKey);
            JsonObject? storedSection = storedArray?
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    string.Equals(Identity(candidate, identityKey), identity, StringComparison.OrdinalIgnoreCase));

            yield return (bag, storedSection is null ? null : Member(storedSection, "properties") as JsonObject, $"{arrayName}[{identity}]");
        }
    }

    // The JSON parser matches member names case-insensitively (JsonReportConfigParser), so anything
    // reading the document structurally has to as well — a document written with "Source" must not
    // slip past redaction because JsonObject's own indexer is ordinal.
    private static JsonNode? Member(JsonObject owner, string name) =>
        owner.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Identity(JsonObject section, string identityKey) =>
        Member(section, identityKey) is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool HasMember(JsonObject owner, string name) =>
        owner.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
}
