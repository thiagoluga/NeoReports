using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeoReports.UI.Services;

/// <summary>
/// Maps a <see cref="BuilderState"/> to the JSON document shape the engine's
/// <c>POST /api/reports</c>, <c>PUT /api/reports/{name}</c> and <c>POST /api/reports/validate</c>
/// endpoints accept (the same document shape <c>NeoReports.Abstractions.ReportConfig</c> parses).
/// Kept dependency-free from the engine assemblies — the UI talks to NeoReports only over HTTP — so
/// this mirrors the wire shape with <see cref="JsonNode"/> rather than reusing engine types.
/// </summary>
/// <remarks>
/// When <see cref="BuilderState.OriginalDocument"/> is set (an edit, ADR D86) the wizard's fields
/// are written **into** that document instead of a blank one. The wizard has no editor for a
/// JsonLogic filter, per-output properties or sections, a column's format/culture/nullability, or a
/// second destination — regenerating the document from the form would silently delete every one of
/// them, turning "I changed the page size" into "I lost half my report".
/// </remarks>
public static class BuilderConfigMapper
{
    /// <summary>The engine's placeholder for a value it would not show (ADR D86); sent back verbatim to keep it.</summary>
    public const string RedactedValue = "${neoreports:redacted}";

    /// <summary>
    /// A section's placeholder carries the address it came from (<c>${neoreports:redacted:outputs[0]}</c>),
    /// so recognising one is a prefix test, not an equality test.
    /// </summary>
    /// <param name="text">The value to test.</param>
    public static bool IsRedactedPlaceholder(string? text) =>
        text is not null
        && (string.Equals(text, RedactedValue, StringComparison.Ordinal)
            || (text.StartsWith("${neoreports:redacted:", StringComparison.Ordinal) && text.EndsWith('}')));

    private const string PropertiesMember = "properties";
    private const string ConnectionStringProperty = "connectionString";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Serializes <paramref name="state"/> into a report config JSON document.</summary>
    /// <param name="state">The wizard state to map. <see cref="BuilderState.ReportName"/> and the
    /// other "real" fields (source/query/columns/formats/destination/schedule) are used; cosmetic
    /// template metadata is not part of the output.</param>
    public static string ToConfigJson(BuilderState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        JsonObject root = TryParse(state.OriginalDocument) ?? new JsonObject();
        JsonObject? originalSource = Get(root, "source") as JsonObject;

        Set(root, "name", state.ReportName);
        Set(root, "source", BuildSource(state, originalSource));
        Set(root, "columns", BuildColumns(state, Get(root, "columns") as JsonArray));
        Set(root, "outputs", BuildOutputs(state, Get(root, "outputs") as JsonArray));
        Set(root, "destinations", BuildDestinations(state, Get(root, "destinations") as JsonArray));
        Set(root, "pageSize", state.PageSize);
        Set(root, "resilience", BuildResilience(state));
        Set(root, "schedule", string.IsNullOrWhiteSpace(state.ScheduleCron)
            ? null
            : new JsonObject { ["cron"] = state.ScheduleCron.Trim() });
        Set(root, "trackProgress", state.TrackProgress);

        return root.ToJsonString();
    }

    /// <summary>
    /// Hydrates <paramref name="state"/> from a configuration document returned by
    /// <c>GET /api/reports/{name}/config</c>, so the wizard opens on the report as it actually is
    /// rather than on a blank form.
    /// </summary>
    /// <param name="state">The wizard state to fill in.</param>
    /// <param name="document">The (redacted) configuration document.</param>
    /// <param name="resolveRegisteredSourceType">
    /// Given a registered source name, its engine source type — a <c>ref</c>-based document does not
    /// carry one (it belongs to the registry, ADR D42), and the wizard needs it to know whether to
    /// show the SQL editor or the generic property editor.
    /// </param>
    /// <returns><c>false</c> when the document could not be read; <paramref name="state"/> is then untouched.</returns>
    public static bool Hydrate(BuilderState state, string document, Func<string, string?>? resolveRegisteredSourceType = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        JsonObject? root = TryParse(document);
        if (root is null)
            return false;

        state.OriginalDocument = document;

        if (Get(root, "name") is JsonValue name)
            state.ReportName = name.ToString();

        HydrateSource(state, Get(root, "source") as JsonObject, resolveRegisteredSourceType);

        if (Get(root, "columns") is JsonArray columns)
        {
            state.ColumnNames = string.Join(", ", columns
                .OfType<JsonObject>()
                .Select(column => (Get(column, "name") as JsonValue)?.ToString())
                .Where(columnName => !string.IsNullOrWhiteSpace(columnName)));
        }

        if (Get(root, "outputs") is JsonArray outputs)
        {
            string[] formats = outputs
                .OfType<JsonObject>()
                .Select(output => (Get(output, "format") as JsonValue)?.ToString())
                .Where(format => !string.IsNullOrWhiteSpace(format))
                .Select(format => format!)
                .ToArray();

            state.Formats = formats.ToHashSet(StringComparer.Ordinal);

            // The Format step is a set of checkboxes, so it cannot represent "two csv outputs" —
            // nothing stops a config document declaring them, with different writer properties each.
            // Counted so the step can say so, and BuildOutputs keeps every one of them.
            state.AdditionalOutputCount = formats.Length - state.Formats.Count;
        }

        // Only the first destination is editable — the wizard models one. Any others are carried
        // through the save untouched (ToConfigJson patches this document), and counted here so the
        // Destination step can say so instead of pretending the report has exactly one.
        if (Get(root, "destinations") is JsonArray destinations)
        {
            state.AdditionalDestinationCount = Math.Max(0, destinations.Count - 1);
            if (destinations.OfType<JsonObject>().FirstOrDefault() is { } destination)
            {
                state.DestinationType = (Get(destination, "type") as JsonValue)?.ToString() ?? "";
                state.DestinationPath = Get(destination, PropertiesMember) is JsonObject destinationProperties
                    ? (Get(destinationProperties, "path") as JsonValue)?.ToString() ?? ""
                    : "";
            }
        }

        if (TryGetInt(root, "pageSize") is { } pageSize)
            state.PageSize = pageSize;

        if (Get(root, "trackProgress") is JsonValue trackProgress && trackProgress.TryGetValue(out bool tracked))
            state.TrackProgress = tracked;

        HydrateResilience(state, Get(root, "resilience") as JsonObject);

        state.ScheduleCron = Get(root, "schedule") is JsonObject schedule
            ? (Get(schedule, "cron") as JsonValue)?.ToString() ?? ""
            : "";

        return true;
    }

    private static void HydrateSource(BuilderState state, JsonObject? source, Func<string, string?>? resolveRegisteredSourceType)
    {
        if (source is null)
            return;

        state.SourceRef = (Get(source, "ref") as JsonValue)?.ToString() ?? "";
        state.SourceType = (Get(source, "type") as JsonValue)?.ToString() ?? "";
        state.LoadedSourceIdentity = state.SourceIdentity;

        // A ref-based document carries no type, and the shape of the editor below depends on it.
        if (string.IsNullOrEmpty(state.SourceType) && !string.IsNullOrWhiteSpace(state.SourceRef))
            state.SourceType = resolveRegisteredSourceType?.Invoke(state.SourceRef) ?? "";

        if (Get(source, PropertiesMember) is not JsonObject properties)
            return;

        if (state.UsesAdoSqlShape)
        {
            state.SqlQuery = (Get(properties, "sql") as JsonValue)?.ToString() ?? "";
            state.KeyColumn = (Get(properties, "key") as JsonValue)?.ToString() ?? "";
        }
        else
        {
            // Every property is shown, including any the engine redacted — the placeholder is
            // editable text, so replacing it replaces the value and leaving it keeps the stored one.
            state.SourceProperties = properties
                .Where(pair => !string.Equals(pair.Key, ConnectionStringProperty, StringComparison.OrdinalIgnoreCase))
                .Select(pair => new PropertyRow
                {
                    Key = pair.Key,
                    Value = pair.Value?.ToString() ?? "",
                    // An HTTP source's "headers" is an object; the editor is a one-line text box, so
                    // it shows the JSON. Flagged rather than rendered as an ordinary string so the
                    // save path parses it back instead of writing the whole subtree as a JSON
                    // *string* — which silently broke the source and hid any placeholder inside it
                    // from every guard, since none of them look inside a larger string.
                    IsStructured = pair.Value is JsonObject or JsonArray,
                })
                .ToList();
        }

        // The connection is handled through its own field for every source type, so it is never also
        // a row: a ${VAR} placeholder fills the field, and a redacted literal is kept without being
        // shown (there is nothing honest to show — the engine held it back precisely because it may
        // be the secret itself).
        string? connection = (Get(properties, ConnectionStringProperty) as JsonValue)?.ToString();
        if (IsRedactedPlaceholder(connection))
            state.ConnectionStringSentinel = connection;
        else if (connection is not null && connection.StartsWith("${", StringComparison.Ordinal) && connection.EndsWith('}'))
            state.ConnectionStringVariable = connection[2..^1];
    }

    private static void HydrateResilience(BuilderState state, JsonObject? resilience)
    {
        if (resilience is null)
            return;

        if (TryGetInt(resilience, "maxAttempts") is { } maxAttempts)
            state.RetryMaxAttempts = maxAttempts;
        if ((Get(resilience, "backoff") as JsonValue)?.ToString() is { Length: > 0 } backoff)
            state.RetryBackoff = backoff;
        if (Get(resilience, "baseDelaySeconds") is JsonValue baseDelay && baseDelay.TryGetValue(out double seconds))
            state.RetryBaseDelaySeconds = seconds;
        if (Get(resilience, "jitter") is JsonValue jitter && jitter.TryGetValue(out bool useJitter))
            state.RetryJitter = useJitter;
        if ((Get(resilience, "onFailure") as JsonValue)?.ToString() is { Length: > 0 } onFailure)
            state.FailureStrategy = onFailure;

        if (Get(resilience, "abortWhen") is not JsonObject abortWhen)
            return;

        if (TryGetInt(abortWhen, "consecutiveFailures") is { } consecutive)
            (state.AbortOnConsecutiveFailures, state.AbortConsecutiveFailures) = (true, consecutive);
        if (TryGetInt(abortWhen, "totalFailures") is { } total)
            (state.AbortOnTotalFailures, state.AbortTotalFailures) = (true, total);
        if (Get(abortWhen, "failureRate") is JsonValue rate && rate.TryGetValue(out double failureRate))
            (state.AbortOnFailureRate, state.AbortFailureRatePercent) = (true, failureRate * 100);
    }

    private static JsonObject BuildSource(BuilderState state, JsonObject? original)
    {
        // Carrying the stored source forward is only safe while it is still the same one: an HTTP
        // source's "url"/"strategy" mean nothing to Postgres, and a stale credential reference even
        // less. On a switch, start clean.
        bool sameSource = original is not null
            && state.OriginalDocument is not null
            && string.Equals(state.LoadedSourceIdentity, state.SourceIdentity, StringComparison.Ordinal);

        JsonObject source = sameSource ? original!.DeepClone().AsObject() : new JsonObject();
        JsonObject carried = Get(source, PropertiesMember) is JsonObject existing
            ? existing.DeepClone().AsObject()
            : new JsonObject();

        // Detached first: a JsonNode belongs to one parent, so re-assigning the very node already
        // sitting at "properties" is not a no-op.
        Remove(source, PropertiesMember);

        bool usesRef = !string.IsNullOrWhiteSpace(state.SourceRef);
        Set(source, "type", usesRef ? null : state.SourceType);
        Set(source, "ref", usesRef ? state.SourceRef.Trim() : null);
        Set(source, PropertiesMember, BuildSourceProperties(state, carried, usesRef));
        return source;
    }

    private static JsonObject BuildSourceProperties(BuilderState state, JsonObject carried, bool usesRef)
    {
        // The connection has its own field on the Configure step, so the carried-over value never
        // survives on its own: it is re-derived below, from that field or from the "keep what is
        // stored" flag. Leaving it in place would make naming a new variable a silent no-op.
        Remove(carried, ConnectionStringProperty);

        JsonObject properties = state.UsesAdoSqlShape
            ? AdoSourceProperties(state, carried)
            : GenericSourceProperties(state, carried);

        if (usesRef)
        {
            // A registered source supplies the connection; sending one here would shadow it.
            Remove(properties, ConnectionStringProperty);
        }
        else if (!HasMember(properties, ConnectionStringProperty))
        {
            // Never overwrites a property the user typed by hand (e.g. an explicit
            // "connectionString" row in the generic editor) — an explicit property always wins.
            if (!string.IsNullOrWhiteSpace(state.ConnectionStringVariable))
                Set(properties, ConnectionStringProperty, $"${{{state.ConnectionStringVariable.Trim()}}}");
            else if (state.ConnectionStringKept)
                Set(properties, ConnectionStringProperty, state.ConnectionStringSentinel);
        }

        return properties;
    }

    // The SQL family has dedicated editors for its query and key column; every other stored property
    // is carried through untouched, since nothing in the wizard could have changed it.
    private static JsonObject AdoSourceProperties(BuilderState state, JsonObject carried)
    {
        Set(carried, "sql", state.SqlQuery);
        Set(carried, "key", state.KeyColumn);
        return carried;
    }

    // The generic editor shows the whole bag, so its rows are the whole bag: anything the user
    // deleted there is meant to be gone, and `carried` only supplies the original JSON types.
    private static JsonObject GenericSourceProperties(BuilderState state, JsonObject carried)
    {
        var properties = new JsonObject();

        // Last-write-wins on a duplicate (post-trim) key, rather than a throw: nothing in the editor
        // stops a user typing two rows with the same key, and a crash on Validate/Save would cost
        // them the whole in-progress wizard over a mistake they can fix by deleting the extra row.
        foreach (PropertyRow row in state.SourceProperties.Where(row => !string.IsNullOrWhiteSpace(row.Key)))
        {
            string key = row.Key.Trim();

            // A row the user did not touch keeps its original JSON type. Every row is text in the
            // editor, so writing them all back as strings would quietly turn `90` into `"90"` and
            // `true` into `"true"` on every edit — a change to the document that nobody asked for
            // and that only some providers happen to tolerate.
            //
            // "present but null" is tested with HasMember rather than a null node, because a JSON
            // null IS a null node: `"proxy": null` hydrates to an empty row, and comparing against
            // a null stored value would call it changed and write `""` back every time.
            bool present = HasMember(carried, key);
            JsonNode? stored = present ? Get(carried, key) : null;
            bool unchanged = present && string.Equals(stored?.ToString() ?? string.Empty, row.Value, StringComparison.Ordinal);

            if (unchanged)
                SetNode(properties, key, stored?.DeepClone());
            else
                SetNode(properties, key, EditedValue(row));
        }

        return properties;
    }

    private static JsonArray BuildColumns(BuilderState state, JsonArray? original)
    {
        var columns = new JsonArray();
        foreach (string name in state.ColumnNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Reusing the stored column keeps its declared type, header, format and culture. Without
            // this an edit would rewrite every column as an untyped String — a silent downgrade of
            // the report's output, from a form that never showed the type in the first place.
            JsonObject? stored = original?
                .OfType<JsonObject>()
                .FirstOrDefault(column => string.Equals((Get(column, "name") as JsonValue)?.ToString(), name, StringComparison.Ordinal));

            columns.Add(stored is not null
                ? stored.DeepClone()
                : new JsonObject { ["name"] = name, ["type"] = "String" });
        }

        return columns;
    }

    private static JsonArray BuildOutputs(BuilderState state, JsonArray? original)
    {
        var outputs = new JsonArray();
        foreach (string format in state.Formats.OrderBy(f => f, StringComparer.Ordinal))
        {
            // EVERY stored output of a kept format is kept, not just the first. The checkbox is per
            // format, so deselecting "csv" means all csv outputs go — but leaving it selected must
            // not silently discard the second one, which a first-match lookup did.
            //
            // Same reasoning as columns: a sectioned XLSX output (worksheet-per-section) is defined
            // entirely by properties the wizard cannot show, and must not be flattened by an edit.
            JsonObject[] stored = original?
                .OfType<JsonObject>()
                .Where(output => string.Equals((Get(output, "format") as JsonValue)?.ToString(), format, StringComparison.Ordinal))
                .ToArray() ?? [];

            if (stored.Length == 0)
            {
                outputs.Add(new JsonObject { ["format"] = format });
                continue;
            }

            foreach (JsonObject output in stored)
                outputs.Add(output.DeepClone());
        }

        return outputs;
    }

    // The wizard edits the FIRST destination — the one Hydrate showed — and passes any others
    // through untouched. Matching by index rather than by type is what makes "change local to s3"
    // mean changing this destination, instead of quietly editing a different one further down.
    private static JsonArray? BuildDestinations(BuilderState state, JsonArray? original)
    {
        var destinations = new JsonArray();

        if (!string.IsNullOrWhiteSpace(state.DestinationType))
        {
            JsonObject? first = original?.OfType<JsonObject>().FirstOrDefault();
            bool sameDestination = first is not null
                && string.Equals((Get(first, "type") as JsonValue)?.ToString(), state.DestinationType, StringComparison.Ordinal);

            // On a type switch the stored properties are dropped: an S3 bucket and region are not
            // configuration a local-filesystem destination should inherit.
            JsonObject destination = sameDestination ? first!.DeepClone().AsObject() : new JsonObject();
            JsonObject? properties = Get(destination, PropertiesMember) is JsonObject stored
                ? stored.DeepClone().AsObject()
                : null;
            Remove(destination, PropertiesMember);

            if (string.IsNullOrWhiteSpace(state.DestinationPath))
                Remove(properties, "path");
            else
                Set(properties ??= new JsonObject(), "path", state.DestinationPath);

            Set(destination, "type", state.DestinationType);
            Set(destination, PropertiesMember, properties is { Count: > 0 } ? properties : null);
            destinations.Add(destination);
        }

        foreach (JsonNode? extra in (original ?? []).Skip(1))
            destinations.Add(extra?.DeepClone());

        return destinations.Count > 0 ? destinations : null;
    }

    /// <summary>
    /// The node an edited row becomes. A row that was rendered from an object or array is parsed
    /// back, so editing an HTTP source's <c>headers</c> keeps it an object rather than turning the
    /// whole subtree into a JSON string — which broke the source and, worse, hid any placeholder
    /// inside it from guards that only ever compare whole values.
    /// </summary>
    /// <remarks>
    /// Unparseable text from a structured row is still sent as the string the user typed. The engine
    /// rejects it at compile time with a message about that property, which is a better outcome than
    /// this layer silently discarding an edit it could not make sense of.
    /// </remarks>
    private static JsonNode? EditedValue(PropertyRow row)
    {
        if (!row.IsStructured)
            return JsonValue.Create(row.Value);

        try
        {
            return JsonNode.Parse(row.Value, documentOptions: ParseOptions);
        }
        catch (JsonException)
        {
            return JsonValue.Create(row.Value);
        }
    }

    private static JsonObject BuildResilience(BuilderState state)
    {
        var resilience = new JsonObject
        {
            ["maxAttempts"] = state.RetryMaxAttempts,
            ["backoff"] = state.RetryBackoff,
            ["baseDelaySeconds"] = state.RetryBaseDelaySeconds,
            ["jitter"] = state.RetryJitter,
            ["onFailure"] = state.FailureStrategy,
        };

        // Only meaningful (and only legal engine-side) alongside "skip-and-log" — omitted entirely
        // otherwise, rather than sent and rejected by the compiler (ADR D37).
        if (!string.Equals(state.FailureStrategy, "skip-and-log", StringComparison.OrdinalIgnoreCase))
            return resilience;

        var abortWhen = new JsonObject();
        if (state.AbortOnConsecutiveFailures)
            abortWhen["consecutiveFailures"] = state.AbortConsecutiveFailures;
        if (state.AbortOnTotalFailures)
            abortWhen["totalFailures"] = state.AbortTotalFailures;
        if (state.AbortOnFailureRate)
            abortWhen["failureRate"] = state.AbortFailureRatePercent / 100.0;

        if (abortWhen.Count > 0)
            resilience["abortWhen"] = abortWhen;

        return resilience;
    }

    private static JsonObject? TryParse(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
            return null;

        try
        {
            return JsonNode.Parse(document, documentOptions: ParseOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The engine matches member names case-insensitively, so a hand-written document may spell them
    // any way it likes; reading and writing through these keeps a patched document from ending up
    // with both "Source" and "source".
    private static JsonNode? Get(JsonObject owner, string name) =>
        owner.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool HasMember(JsonObject owner, string name) =>
        owner.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));

    private static void Remove(JsonObject? owner, string name)
    {
        if (owner is null)
            return;

        foreach (string key in owner.Where(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
            owner.Remove(key);
    }

    /// <summary>Writes a member, treating <c>null</c> as "omit this member entirely".</summary>
    private static void Set(JsonObject owner, string name, JsonNode? value)
    {
        RemoveCaseVariant(owner, name);

        if (value is null)
            owner.Remove(name);
        else
            owner[name] = value;
    }

    private static void Set(JsonObject owner, string name, string? value) =>
        Set(owner, name, value is null ? null : JsonValue.Create(value));

    private static void Set(JsonObject owner, string name, int value) =>
        Set(owner, name, JsonValue.Create(value));

    private static void Set(JsonObject owner, string name, bool value) =>
        Set(owner, name, JsonValue.Create(value));

    /// <summary>
    /// Writes a member, treating <c>null</c> as the JSON value <c>null</c> rather than as an
    /// omission — the distinction <see cref="Set"/> deliberately collapses, and the one a
    /// round-tripped <c>"key": null</c> depends on.
    /// </summary>
    private static void SetNode(JsonObject owner, string name, JsonNode? value)
    {
        RemoveCaseVariant(owner, name);
        owner[name] = value;
    }

    private static void RemoveCaseVariant(JsonObject owner, string name)
    {
        string? existing = owner
            .Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && !string.Equals(existing, name, StringComparison.Ordinal))
            owner.Remove(existing);
    }

    private static int? TryGetInt(JsonObject owner, string name) =>
        Get(owner, name) is JsonValue value
        && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
}
