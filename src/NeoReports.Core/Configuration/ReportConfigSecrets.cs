using System.Globalization;
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
/// This is that story. A redacted value is replaced by a reserved sentinel, and <see cref="Restore"/>
/// puts the stored value back when the sentinel comes home unchanged — so an editor can round-trip a
/// property it was never allowed to see, and the alternative (making the user retype a connection
/// string to change a page size) goes away without a secret ever leaving the host.
/// </para>
/// <para>
/// <b>The sentinel carries the address it came from</b> — <c>${neoreports:redacted:destinations[1]}</c>
/// — so restoring never has to guess which stored section an incoming one corresponds to. Two earlier
/// designs did guess, and both were wrong: pairing by section id alone put one S3 bucket's access key
/// into another, and pairing by id-then-occurrence did the same as soon as an earlier section's type
/// changed and shifted the count. There is no reliable identity to pair on — a report may declare two
/// destinations of the same type to different buckets — so the address is carried instead of inferred.
/// The source is a singleton and needs no address.
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
    /// The placeholder a redacted <c>source.properties</c> value is replaced by. Reserved:
    /// <c>POST /reports</c> rejects it outright (there is nothing to restore from), and
    /// <c>PUT /reports/{name}</c> swaps it back for the stored value.
    /// </summary>
    public const string RedactedValue = "${neoreports:redacted}";

    private const string PropertiesMember = "properties";
    private const string AddressedPrefix = "${neoreports:redacted:";
    private const string OutputsMember = "outputs";
    private const string DestinationsMember = "destinations";
    private const string SourceMember = "source";

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
        "password", "passwd", "pwd", "passphrase", "secret", "token", "key", "credential",
        "connectionstring", "connection_string", "signature", "auth", "sas", "cookie", "session",
    ];

    // "key" as a whole word is the ADO keyset column, not a credential — hiding it would blind an
    // editor to its own report's pagination. Every other name CONTAINING "key" is credential-shaped
    // (apiKey, accessKey, privateKey, accountKey, sharedKey, licenseKey), which is why the fragment
    // list carries the bare substring and this is the single exception carved out of it.
    private const string KeysetColumnProperty = "key";

    // Query-parameter names that carry a credential without matching a fragment above: Azure SAS
    // ("sv"/"sig"), OAuth's "?code=", Google's "?key=". AWS/GCS pre-signed URLs are already covered
    // ("X-Amz-Signature" contains "signature", "AWSAccessKeyId" contains "key").
    //
    // "key" is listed here even though the fragment list carries the substring, because the keyset
    // carve-out in IsSecretKey suppresses the bare word — and that carve-out is about property-bag
    // keys, not query parameters, where "?key=" is Google's API key and nothing else.
    private static readonly string[] CredentialQueryParameters = ["sig", "sv", "code", "key"];

    // Whole-value ${VAR} placeholders, the same shape ReportConfigEnvironment resolves. A value in
    // this form is not a secret — the secret lives in the environment — so it is returned as-is and
    // stays editable, which is the shape the Builder writes for every connection string it manages.
    [GeneratedRegex(@"^\$\{[A-Za-z_][A-Za-z0-9_]*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentPlaceholder();

    /// <summary>
    /// Returns <paramref name="document"/> with every credential-bearing property value replaced by
    /// a redaction placeholder, at any depth inside a property bag.
    /// </summary>
    /// <param name="document">The stored configuration document.</param>
    /// <exception cref="ConfigurationException">Thrown when the document is not valid JSON.</exception>
    public static string Redact(string document)
    {
        JsonObject root = ParseObject(document);

        foreach ((JsonObject bag, string? address) in PropertyBagSlots(root))
            RedactMembers(bag, SentinelFor(address));

        return root.ToJsonString();
    }

    /// <summary>True when any property value in the document is a redaction placeholder, at any depth.</summary>
    /// <param name="document">The configuration document to inspect.</param>
    /// <exception cref="ConfigurationException">Thrown when the document is not valid JSON.</exception>
    public static bool ContainsRedactedValue(string document) =>
        PropertyBagSlots(ParseObject(document)).Any(slot => ContainsRedactedNode(slot.Bag));

    /// <summary>True when <paramref name="text"/> is a redaction placeholder, addressed or not.</summary>
    /// <param name="text">The value to test.</param>
    public static bool IsRedactedPlaceholder(string? text) =>
        text is not null
        && (string.Equals(text, RedactedValue, StringComparison.Ordinal)
            || (text.StartsWith(AddressedPrefix, StringComparison.Ordinal) && text.EndsWith('}')));

    /// <summary>
    /// True when a parsed property-bag value holds a redaction placeholder, at any depth. The parser
    /// keeps nested objects and arrays as a <see cref="JsonElement"/>, so a check that only compared
    /// strings would miss a placeholder inside <c>headers</c> or a child source — which is exactly
    /// how one reached disk during development.
    /// </summary>
    /// <param name="value">A property-bag value as <c>IReadOnlyDictionary&lt;string, object?&gt;</c> holds it.</param>
    /// <remarks>
    /// Deliberately a <b>substring</b> test on strings, unlike <see cref="IsRedactedPlaceholder"/>.
    /// This is the last-resort guard before compilation, and a placeholder can end up embedded in a
    /// larger string — an editor that rewrites a nested object as JSON text carries one along inside
    /// it. Restoring still requires an exact match; only rejecting is this permissive.
    /// </remarks>
    public static bool HoldsRedactedValue(object? value) => value switch
    {
        string text => text.Contains(RedactedValue, StringComparison.Ordinal)
            || text.Contains(AddressedPrefix, StringComparison.Ordinal),
        JsonElement { ValueKind: JsonValueKind.String } element => HoldsRedactedValue(element.GetString()),
        JsonElement { ValueKind: JsonValueKind.Object } element =>
            element.EnumerateObject().Any(property => HoldsRedactedValue(property.Value)),
        JsonElement { ValueKind: JsonValueKind.Array } element =>
            element.EnumerateArray().Any(item => HoldsRedactedValue(item)),
        _ => false,
    };

    /// <summary>
    /// Returns <paramref name="document"/> with every redaction placeholder replaced by the value it
    /// addresses in <paramref name="storedDocument"/>, at any depth.
    /// </summary>
    /// <param name="document">The incoming document, as edited by the client.</param>
    /// <param name="storedDocument">The document currently persisted for this report.</param>
    /// <exception cref="ConfigurationException">
    /// Thrown when either document is not valid JSON, or when a placeholder addresses nothing —
    /// better a rejected edit than one that silently drops a credential.
    /// </exception>
    public static string Restore(string document, string storedDocument)
    {
        JsonObject root = ParseObject(document);
        JsonObject stored = ParseObject(storedDocument);

        // An address may be claimed by at most one incoming bag. Many placeholders inside one bag
        // share its address (accessKey and secretKey of the same destination), which is fine; two
        // *different* bags naming the same one is not — duplicating a section by hand copies its
        // placeholders too, and resolving both would hand a second destination a credential the
        // editor cannot see, which is the outcome the address exists to prevent.
        var claims = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach ((JsonObject bag, string? _) in PropertyBagSlots(root))
            RestoreMembers(bag, new RestoreContext(stored, bag, claims), []);

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

    private static string SentinelFor(string? address) =>
        address is null ? RedactedValue : AddressedPrefix + address + "}";

    private static string? AddressOf(string sentinel) =>
        string.Equals(sentinel, RedactedValue, StringComparison.Ordinal)
            ? null
            : sentinel[AddressedPrefix.Length..^1];

    // A property-bag value is not always a scalar. An HTTP source declares "headers" as an object —
    // Authorization lives in there — and a merge-join source nests whole child sources, each with
    // its own "properties" and connection string. A walk that stopped at the top level handed both
    // back in plaintext, which is the one outcome this file exists to prevent.
    private static void RedactMembers(JsonObject owner, string sentinel)
    {
        foreach (string key in owner.Select(pair => pair.Key).ToArray())
            Replace(owner, key, RedactValue(key, owner[key], sentinel));
    }

    private static JsonNode? RedactValue(string key, JsonNode? value, string sentinel)
    {
        switch (value)
        {
            // A secret-named key hides its whole subtree rather than being descended into:
            // "credentials": { "user": …, "pass": … } is a credential whatever its inner keys happen
            // to be called, and guessing at them is the failure mode this design exists to avoid.
            case JsonObject or JsonArray when IsSecretKey(key):
                return sentinel;

            case JsonObject nested:
                RedactMembers(nested, sentinel);
                return nested;

            // Elements inherit the enclosing key: an array under "tokens" is judged the way a single
            // "tokens" string would be, and an array of objects is descended into by inner key.
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    JsonNode? original = array[i];
                    JsonNode? redacted = RedactValue(key, original, sentinel);
                    if (!ReferenceEquals(original, redacted))
                        array[i] = redacted;
                }

                return array;

            default:
                return ShouldRedact(key, value) ? sentinel : value;
        }
    }

    /// <param name="Stored">The whole stored document — a placeholder names the slot it came from.</param>
    /// <param name="Bag">The incoming property bag currently being restored.</param>
    /// <param name="Claims">Which bag has already claimed each address, so no two can share one.</param>
    private sealed record RestoreContext(JsonObject Stored, JsonObject Bag, Dictionary<string, JsonObject> Claims);

    private static void RestoreMembers(JsonObject owner, RestoreContext context, IReadOnlyList<object> path)
    {
        foreach (string key in owner.Select(pair => pair.Key).ToArray())
            Replace(owner, key, RestoreValue(owner[key], context, [.. path, key]));
    }

    // Inside a property bag an array is ordered data, so an element's address is its index. Elements
    // go through the same RestoreValue as members, which is what makes a redacted *scalar* element
    // work — `"mirrors": ["…", "…"]` with a credential in one of them.
    private static void RestoreElements(JsonArray array, RestoreContext context, IReadOnlyList<object> path)
    {
        for (var i = 0; i < array.Count; i++)
        {
            JsonNode? restored = RestoreValue(array[i], context, [.. path, i]);
            if (!ReferenceEquals(array[i], restored))
                array[i] = restored;
        }
    }

    /// <summary>
    /// Returns the value to keep at one position: the value the placeholder addresses in the stored
    /// document when the incoming value is a placeholder, otherwise the incoming value with its
    /// children restored in place.
    /// </summary>
    /// <param name="value">The incoming value.</param>
    /// <param name="context">The stored document, the bag being restored, and the addresses claimed so far.</param>
    /// <param name="path">Where inside its bag this value sits, as member names and array indices.</param>
    private static JsonNode? RestoreValue(JsonNode? value, RestoreContext context, IReadOnlyList<object> path)
    {
        if (value is JsonValue sentinelValue
            && sentinelValue.TryGetValue(out string? text)
            && IsRedactedPlaceholder(text))
        {
            string? address = AddressOf(text);
            ClaimAddress(context, address);

            if (!TryResolveStored(context.Stored, address, path, out JsonNode? original))
            {
                throw new ConfigurationException(
                    $"The property at '{Describe(address, path)}' was sent as a redacted placeholder, " +
                    "but the stored configuration has no value to restore for it. Send the real value instead.");
            }

            return original?.DeepClone();
        }

        switch (value)
        {
            case JsonObject nested:
                RestoreMembers(nested, context, path);
                break;

            case JsonArray array:
                RestoreElements(array, context, path);
                break;

            default:
                break;
        }

        return value;
    }

    private static void ClaimAddress(RestoreContext context, string? address)
    {
        string claimed = address ?? SourceMember;
        if (context.Claims.TryGetValue(claimed, out JsonObject? owner) && !ReferenceEquals(owner, context.Bag))
        {
            throw new ConfigurationException(
                $"Two different sections both sent a redacted placeholder for '{claimed}'. A section copied " +
                "from another one needs its own value — the placeholder only stands for the value of the " +
                "section it came from.");
        }

        context.Claims[claimed] = context.Bag;
    }

    /// <summary>
    /// Follows a placeholder's address to its stored property bag and then <paramref name="path"/>
    /// inside it. Returns <c>false</c> when any step is missing; a stored JSON <c>null</c> resolves
    /// successfully to <c>null</c>, which is not the same thing.
    /// </summary>
    private static bool TryResolveStored(
        JsonObject stored, string? address, IReadOnlyList<object> path, out JsonNode? value)
    {
        value = null;

        JsonObject? bag = StoredBagAt(stored, address);
        if (bag is null)
            return false;

        JsonNode? node = bag;
        foreach (object step in path)
        {
            switch (step)
            {
                case string key when node is JsonObject owner && HasMember(owner, key):
                    node = Member(owner, key);
                    break;

                case int index when node is JsonArray array && index >= 0 && index < array.Count:
                    node = array[index];
                    break;

                default:
                    return false;
            }
        }

        value = node;
        return true;
    }

    // The address is the stored slot, not the incoming one: an editor may remove, insert, reorder or
    // retype sections, and the placeholder still names where its value came from.
    private static JsonObject? StoredBagAt(JsonObject stored, string? address)
    {
        if (address is null)
            return Member(stored, SourceMember) is JsonObject source
                ? Member(source, PropertiesMember) as JsonObject
                : null;

        int open = address.IndexOf('[', StringComparison.Ordinal);
        if (open < 0 || !address.EndsWith(']')
            || !int.TryParse(address[(open + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            return null;
        }

        string arrayName = address[..open];
        if (!string.Equals(arrayName, OutputsMember, StringComparison.Ordinal)
            && !string.Equals(arrayName, DestinationsMember, StringComparison.Ordinal))
        {
            return null;
        }

        return Member(stored, arrayName) is JsonArray sections
            && index < sections.Count
            && sections[index] is JsonObject section
                ? Member(section, PropertiesMember) as JsonObject
                : null;
    }

    private static string Describe(string? address, IReadOnlyList<object> path)
    {
        string root = address ?? SourceMember;
        return path.Aggregate(root, (current, step) =>
            step is int index ? $"{current}[{index}]" : $"{current}.{step}");
    }

    private static bool ContainsRedactedNode(JsonNode? value) => value switch
    {
        JsonObject nested => nested.Any(pair => ContainsRedactedNode(pair.Value)),
        JsonArray array => array.Any(ContainsRedactedNode),
        JsonValue scalar => scalar.TryGetValue(out string? text) && IsRedactedPlaceholder(text),
        _ => false,
    };

    // Re-assigning the very node already sitting at that key would reparent it, which JsonNode does
    // not allow; only a genuine replacement is written back.
    private static void Replace(JsonObject owner, string key, JsonNode? value)
    {
        if (!ReferenceEquals(owner[key], value))
            owner[key] = value;
    }

    private static bool IsSecretKey(string key) =>
        !string.Equals(key, KeysetColumnProperty, StringComparison.OrdinalIgnoreCase)
        && SecretKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool ShouldRedact(string key, JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
            return false;

        // A credential-named key hides whatever it holds, string or not. Reading it as text first
        // and giving up when that fails let `"apiKey": 8675309123456` through in plaintext — a
        // numeric token is still a token, and the generous key matching above exists precisely so
        // the shape of the value never has to be guessed.
        if (!jsonValue.TryGetValue(out string? text))
            return IsSecretKey(key);

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

    /// <summary>
    /// The three places a config document holds a free-form property bag, each with the address a
    /// placeholder found in it carries. The source is a singleton and needs none; sections carry
    /// their raw array index, so a section without a bag does not shift the ones after it.
    /// </summary>
    private static IEnumerable<(JsonObject Bag, string? Address)> PropertyBagSlots(JsonObject root)
    {
        if (Member(root, SourceMember) is JsonObject source && Member(source, PropertiesMember) is JsonObject sourceBag)
            yield return (sourceBag, null);

        foreach ((JsonObject Bag, string? Address) output in SectionSlots(root, OutputsMember))
            yield return output;

        foreach ((JsonObject Bag, string? Address) destination in SectionSlots(root, DestinationsMember))
            yield return destination;
    }

    private static IEnumerable<(JsonObject Bag, string? Address)> SectionSlots(JsonObject root, string arrayName)
    {
        if (Member(root, arrayName) is not JsonArray array)
            yield break;

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject section && Member(section, PropertiesMember) is JsonObject bag)
                yield return (bag, $"{arrayName}[{i}]");
        }
    }

    // The JSON parser matches member names case-insensitively (JsonReportConfigParser), so anything
    // reading the document structurally has to as well — a document written with "Source" must not
    // slip past redaction because JsonObject's own indexer is ordinal.
    private static JsonNode? Member(JsonObject owner, string name) =>
        owner.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool HasMember(JsonObject owner, string name) =>
        owner.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
}
