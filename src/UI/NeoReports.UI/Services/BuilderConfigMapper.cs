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
            state.Formats = outputs
                .OfType<JsonObject>()
                .Select(output => (Get(output, "format") as JsonValue)?.ToString())
                .Where(format => !string.IsNullOrWhiteSpace(format))
                .Select(format => format!)
                .ToHashSet(StringComparer.Ordinal);
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
                state.DestinationPath = Get(destination, "properties") is JsonObject destinationProperties
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

        if (Get(source, "properties") is not JsonObject properties)
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
                .Where(pair => !string.Equals(pair.Key, "connectionString", StringComparison.OrdinalIgnoreCase))
                .Select(pair => new PropertyRow { Key = pair.Key, Value = pair.Value?.ToString() ?? "" })
                .ToList();
        }

        // The connection is handled through its own field for every source type, so it is never also
        // a row: a ${VAR} placeholder fills the field, and a redacted literal is kept without being
        // shown (there is nothing honest to show — the engine held it back precisely because it may
        // be the secret itself).
        string? connection = (Get(properties, "connectionString") as JsonValue)?.ToString();
        if (string.Equals(connection, RedactedValue, StringComparison.Ordinal))
            state.ConnectionStringRedacted = true;
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
        bool usesRef = !string.IsNullOrWhiteSpace(state.SourceRef);

        // Carrying the stored bag forward is only safe while the source is still the same one:
        // an HTTP source's "url"/"strategy" mean nothing to Postgres, and a stale credential
        // reference even less. On a switch, start clean.
        bool sameSource = original is not null
            && state.OriginalDocument is not null
            && string.Equals(state.LoadedSourceIdentity, state.SourceIdentity, StringComparison.Ordinal);

        JsonObject source = sameSource ? original!.DeepClone().AsObject() : new JsonObject();
        JsonObject properties = Get(source, "properties") is JsonObject existing
            ? existing.DeepClone().AsObject()
            : new JsonObject();

        // Detached first: a JsonNode belongs to one parent, so re-assigning the very node already
        // sitting at "properties" is not a no-op.
        Remove(source, "properties");

        // The connection has its own field on the Configure step, so the carried-over value never
        // survives on its own: it is re-derived below, from that field or from the "keep what is
        // stored" flag. Leaving it in place would make naming a new variable a silent no-op.
        Remove(properties, "connectionString");

        if (state.UsesAdoSqlShape)
        {
            Set(properties, "sql", state.SqlQuery);
            Set(properties, "key", state.KeyColumn);
        }
        else
        {
            // The generic editor shows the whole bag, so its rows are the whole bag. Anything the
            // user deleted there is meant to be gone.
            var stored = new JsonObject();
            foreach (string key in properties.Select(pair => pair.Key).ToArray())
            {
                JsonNode? value = properties[key];
                properties.Remove(key);
                stored[key] = value;
            }

            // Last-write-wins on a duplicate (post-trim) key, rather than a throw: nothing in the
            // editor stops a user typing two rows with the same key, and a crash on Validate/Save
            // would cost them the whole in-progress wizard over a mistake they can fix by deleting
            // the extra row.
            foreach (PropertyRow row in state.SourceProperties.Where(row => !string.IsNullOrWhiteSpace(row.Key)))
            {
                string key = row.Key.Trim();

                // A row the user did not touch keeps its original JSON type. Every row is text in
                // the editor, so writing them all back as strings would quietly turn `90` into
                // `"90"` and `true` into `"true"` on every edit — a change to the document that
                // nobody asked for and that only some providers happen to tolerate.
                JsonNode? storedValue = Get(stored, key);
                bool unchanged = storedValue is not null
                    && string.Equals(storedValue.ToString(), row.Value, StringComparison.Ordinal);
                Set(properties, key, unchanged ? storedValue!.DeepClone() : JsonValue.Create(row.Value));
            }
        }

        // Never overwrites a property the user typed by hand (e.g. an explicit "connectionString"
        // row in the generic editor) — an explicit property always wins over this derived one.
        if (!usesRef && !HasMember(properties, "connectionString"))
        {
            if (!string.IsNullOrWhiteSpace(state.ConnectionStringVariable))
                Set(properties, "connectionString", $"${{{state.ConnectionStringVariable.Trim()}}}");
            else if (state.ConnectionStringKept)
                Set(properties, "connectionString", RedactedValue);
        }
        else if (usesRef)
        {
            // A registered source supplies the connection; sending one here would shadow it.
            Remove(properties, "connectionString");
        }

        Set(source, "type", usesRef ? null : state.SourceType);
        Set(source, "ref", usesRef ? state.SourceRef.Trim() : null);
        Set(source, "properties", properties);
        return source;
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
            // Same reasoning as columns: a sectioned XLSX output (worksheet-per-section) is defined
            // entirely by properties the wizard cannot show, and must not be flattened by an edit.
            JsonObject? stored = original?
                .OfType<JsonObject>()
                .FirstOrDefault(output => string.Equals((Get(output, "format") as JsonValue)?.ToString(), format, StringComparison.Ordinal));

            outputs.Add(stored is not null ? stored.DeepClone() : new JsonObject { ["format"] = format });
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
            JsonObject? properties = Get(destination, "properties") is JsonObject stored
                ? stored.DeepClone().AsObject()
                : null;
            Remove(destination, "properties");

            if (string.IsNullOrWhiteSpace(state.DestinationPath))
                Remove(properties, "path");
            else
                Set(properties ??= new JsonObject(), "path", state.DestinationPath);

            Set(destination, "type", state.DestinationType);
            Set(destination, "properties", properties is { Count: > 0 } ? properties : null);
            destinations.Add(destination);
        }

        foreach (JsonNode? extra in (original ?? []).Skip(1))
            destinations.Add(extra?.DeepClone());

        return destinations.Count > 0 ? destinations : null;
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

    private static void Set(JsonObject owner, string name, JsonNode? value)
    {
        string? existing = owner
            .Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && !string.Equals(existing, name, StringComparison.Ordinal))
            owner.Remove(existing);

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

    private static int? TryGetInt(JsonObject owner, string name) =>
        Get(owner, name) is JsonValue value
        && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
}
