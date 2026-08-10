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

    private const string PropertiesMember = "properties";

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
        "api-key", "accesskey", "access_key", "privatekey", "private_key", "credential",
        "connectionstring", "connection_string", "signature", "auth", "sas", "cookie", "session",
    ];

    // Query-parameter names that carry a credential without matching a fragment above: Azure SAS
    // ("sv"/"sig"), Google's "?key=", OAuth's "?code=". AWS/GCS pre-signed URLs are already covered
    // ("X-Amz-Signature" contains "signature", "AWSAccessKeyId" contains "accesskey").
    //
    // "key" is credential-shaped only as a QUERY parameter. As a property-bag key it is the ADO
    // keyset column, which is why it is deliberately absent from the fragment list.
    private static readonly string[] CredentialQueryParameters = ["sig", "sv", "code", "key"];

    // Whole-value ${VAR} placeholders, the same shape ReportConfigEnvironment resolves. A value in
    // this form is not a secret — the secret lives in the environment — so it is returned as-is and
    // stays editable, which is the shape the Builder writes for every connection string it manages.
    [GeneratedRegex(@"^\$\{[A-Za-z_][A-Za-z0-9_]*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentPlaceholder();

    /// <summary>
    /// Returns <paramref name="document"/> with every credential-bearing property value replaced by
    /// <see cref="RedactedValue"/>, at any depth inside a property bag.
    /// </summary>
    /// <param name="document">The stored configuration document.</param>
    /// <exception cref="ConfigurationException">Thrown when the document is not valid JSON.</exception>
    public static string Redact(string document)
    {
        JsonObject root = ParseObject(document);

        foreach (JsonObject bag in PropertyBags(root))
            RedactMembers(bag);

        return root.ToJsonString();
    }

    /// <summary>True when any property value in the document is the redaction sentinel, at any depth.</summary>
    /// <param name="document">The configuration document to inspect.</param>
    /// <exception cref="ConfigurationException">Thrown when the document is not valid JSON.</exception>
    public static bool ContainsRedactedValue(string document) =>
        PropertyBags(ParseObject(document)).Any(ContainsRedactedNode);

    /// <summary>
    /// True when a parsed property-bag value holds the redaction sentinel, at any depth. The parser
    /// keeps nested objects and arrays as a <see cref="JsonElement"/>, so a check that only compared
    /// strings would miss a sentinel inside <c>headers</c> or a child source — which is exactly how
    /// one reached disk during development.
    /// </summary>
    /// <param name="value">A property-bag value as <c>IReadOnlyDictionary&lt;string, object?&gt;</c> holds it.</param>
    public static bool HoldsRedactedValue(object? value) => value switch
    {
        string text => string.Equals(text, RedactedValue, StringComparison.Ordinal),
        JsonElement { ValueKind: JsonValueKind.String } element =>
            string.Equals(element.GetString(), RedactedValue, StringComparison.Ordinal),
        JsonElement { ValueKind: JsonValueKind.Object } element =>
            element.EnumerateObject().Any(property => HoldsRedactedValue(property.Value)),
        JsonElement { ValueKind: JsonValueKind.Array } element =>
            element.EnumerateArray().Any(item => HoldsRedactedValue(item)),
        _ => false,
    };

    /// <summary>
    /// Returns <paramref name="document"/> with every <see cref="RedactedValue"/> replaced by the
    /// value the same property holds in <paramref name="storedDocument"/>, at any depth.
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

        foreach ((JsonObject bag, JsonObject? storedBag, string section) in PairedPropertyBags(root, stored))
            RestoreMembers(bag, storedBag, section);

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

    // A property-bag value is not always a scalar. An HTTP source declares "headers" as an object —
    // Authorization lives in there — and a merge-join source nests whole child sources, each with
    // its own "properties" and connection string. A walk that stopped at the top level handed both
    // back in plaintext, which is the one outcome this file exists to prevent.
    private static void RedactMembers(JsonObject owner)
    {
        foreach (string key in owner.Select(pair => pair.Key).ToArray())
            Replace(owner, key, RedactValue(key, owner[key]));
    }

    private static JsonNode? RedactValue(string key, JsonNode? value)
    {
        switch (value)
        {
            // A secret-named key hides its whole subtree rather than being descended into:
            // "credentials": { "user": …, "pass": … } is a credential whatever its inner keys happen
            // to be called, and guessing at them is the failure mode this design exists to avoid.
            case JsonObject or JsonArray when IsSecretKey(key):
                return RedactedValue;

            case JsonObject nested:
                RedactMembers(nested);
                return nested;

            // Elements inherit the enclosing key: an array under "tokens" is judged the way a single
            // "tokens" string would be, and an array of objects is descended into by inner key.
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    JsonNode? original = array[i];
                    JsonNode? redacted = RedactValue(key, original);
                    if (!ReferenceEquals(original, redacted))
                        array[i] = redacted;
                }

                return array;

            default:
                return ShouldRedact(key, value) ? RedactedValue : value;
        }
    }

    private static void RestoreMembers(JsonObject bag, JsonObject? storedBag, string section)
    {
        foreach (string key in bag.Select(pair => pair.Key).ToArray())
        {
            JsonNode? stored = storedBag is null ? null : Member(storedBag, key);
            bool storedExists = storedBag is not null && HasMember(storedBag, key);

            Replace(bag, key, RestoreValue(bag[key], stored, storedExists, $"The '{section}' property '{key}'", $"{section}.{key}"));
        }
    }

    /// <summary>
    /// Returns the value to keep at one position: the stored counterpart when the incoming value is
    /// the sentinel, otherwise the incoming value with its children restored in place.
    /// </summary>
    /// <param name="value">The incoming value.</param>
    /// <param name="stored">The stored counterpart, or <c>null</c> when there is none (or it is a JSON null).</param>
    /// <param name="storedExists">Whether a stored counterpart is present at all — a stored JSON null is not "missing".</param>
    /// <param name="sentinelLabel">How to name this position, as a sentence opener, if the sentinel cannot be resolved.</param>
    /// <param name="childSection">Section label to pass down to children.</param>
    private static JsonNode? RestoreValue(
        JsonNode? value, JsonNode? stored, bool storedExists, string sentinelLabel, string childSection)
    {
        if (IsRedacted(value))
        {
            if (!storedExists)
            {
                throw new ConfigurationException(
                    $"{sentinelLabel} was sent as a redacted placeholder, but the stored configuration has " +
                    "no value to restore for it. Send the real value instead.");
            }

            return stored?.DeepClone();
        }

        switch (value)
        {
            case JsonObject nested:
                RestoreMembers(nested, stored as JsonObject, childSection);
                break;

            case JsonArray array:
                RestoreElements(array, stored as JsonArray, childSection);
                break;

            default:
                break;
        }

        return value;
    }

    // Inside a property bag an array is ordered data, not a set of identified sections, so index is
    // the only pairing available; an editor that reorders one has to send the real values.
    //
    // Elements go through the same RestoreValue as members, which is what makes a redacted *scalar*
    // element work — `"mirrors": ["…", "…"]` with a credential in one of them. Descending only into
    // object elements left that one holding the literal sentinel, and it got persisted: found by
    // round-tripping a real report, after every flat-bag test had passed.
    private static void RestoreElements(JsonArray array, JsonArray? storedArray, string section)
    {
        for (var i = 0; i < array.Count; i++)
        {
            bool storedExists = storedArray is not null && i < storedArray.Count;
            JsonNode? stored = storedExists ? storedArray![i] : null;
            JsonNode? restored = RestoreValue(array[i], stored, storedExists, $"The element '{section}[{i}]'", $"{section}[{i}]");

            if (!ReferenceEquals(array[i], restored))
                array[i] = restored;
        }
    }

    private static bool ContainsRedactedNode(JsonNode? value) => value switch
    {
        JsonObject nested => nested.Any(pair => ContainsRedactedNode(pair.Value)),
        JsonArray array => array.Any(ContainsRedactedNode),
        _ => IsRedacted(value),
    };

    // Re-assigning the very node already sitting at that key would reparent it, which JsonNode does
    // not allow; only a genuine replacement is written back.
    private static void Replace(JsonObject owner, string key, JsonNode? value)
    {
        if (!ReferenceEquals(owner[key], value))
            owner[key] = value;
    }

    private static bool IsSecretKey(string key) =>
        SecretKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool ShouldRedact(string key, JsonNode? value)
    {
        // A JSON null is a null JsonNode, never a JsonValue, so reaching here with a successful
        // string read means `text` is non-null.
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? text))
            return false;

        if (EnvironmentPlaceholder().IsMatch(text))
            return false;

        if (IsSecretKey(key))
            return true;

        // Value-based, and independent of the key name: a URL can be the credential itself, and the
        // keys it lives under are the most innocuous ones there are — "url", "baseUrl",
        // "instanceUrl", all required properties of the shipped HTTP-family sources. Two shapes:
        // userinfo ("https://user:pass@host/…"), and a signed or keyed query, which is the entire
        // point of an Azure SAS or an S3/GCS pre-signed URL.
        return Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            && (!string.IsNullOrEmpty(uri.UserInfo) || HasCredentialQueryParameter(uri));
    }

    private static bool HasCredentialQueryParameter(Uri uri)
    {
        string query = uri.Query;
        if (query.Length <= 1)
            return false;

        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2)[0])
            .Any(name => IsSecretKey(name) || CredentialQueryParameters.Contains(name, StringComparer.OrdinalIgnoreCase));
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
        if (Member(root, "source") is JsonObject source && Member(source, PropertiesMember) is JsonObject sourceBag)
        {
            JsonObject? storedBag = stored is not null
                && Member(stored, "source") is JsonObject storedSource
                    ? Member(storedSource, PropertiesMember) as JsonObject
                    : null;

            yield return (sourceBag, storedBag, "source");
        }

        foreach ((JsonObject Bag, JsonObject? Stored, string Section) output in SectionBags(root, stored, "outputs", "format"))
            yield return output;

        foreach ((JsonObject Bag, JsonObject? Stored, string Section) destination in SectionBags(root, stored, "destinations", "type"))
            yield return destination;
    }

    /// <summary>
    /// Pairs each section with its stored counterpart by identity (an output's <c>format</c>, a
    /// destination's <c>type</c>) and then by occurrence: the nth section carrying a given id pairs
    /// with the nth stored section carrying it.
    /// </summary>
    /// <remarks>
    /// Identity alone is not a key — nothing stops a report declaring two <c>s3</c> destinations to
    /// different buckets. Matching on first-by-identity would restore bucket A's access key into
    /// bucket B on any edit, silently and with both sections looking perfectly ordinary.
    /// </remarks>
    private static IEnumerable<(JsonObject Bag, JsonObject? Stored, string Section)> SectionBags(
        JsonObject root, JsonObject? stored, string arrayName, string identityKey)
    {
        if (Member(root, arrayName) is not JsonArray array)
            yield break;

        JsonArray? storedArray = stored is null ? null : Member(stored, arrayName) as JsonArray;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // A section without a property bag has nothing to redact or restore, so it is filtered out
        // rather than iterated and skipped — the occurrence counter below then counts only the
        // sections this walk actually yields, which is what Restore pairs against.
        var sections = array
            .OfType<JsonObject>()
            .Where(section => Member(section, PropertiesMember) is JsonObject)
            .Select(section => (Section: section, Bag: (JsonObject)Member(section, PropertiesMember)!));

        foreach ((JsonObject section, JsonObject bag) in sections)
        {
            string identity = Identity(section, identityKey) ?? string.Empty;
            seen.TryGetValue(identity, out int occurrence);
            seen[identity] = occurrence + 1;

            JsonObject? storedSection = storedArray?
                .OfType<JsonObject>()
                .Where(candidate => string.Equals(Identity(candidate, identityKey) ?? string.Empty, identity, StringComparison.OrdinalIgnoreCase))
                .Skip(occurrence)
                .FirstOrDefault();

            yield return (
                bag,
                storedSection is null ? null : Member(storedSection, PropertiesMember) as JsonObject,
                $"{arrayName}[{identity}]");
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
