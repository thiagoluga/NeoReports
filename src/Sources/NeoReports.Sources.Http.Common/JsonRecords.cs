using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NeoReports.Sources.Http.Common;

/// <summary>
/// Minimal dotted-path JSON traversal (ADR D61) — no JSONPath library; the repo's established
/// preference (D58's hand-rolled CSV parser) for a need this small: "the array lives at
/// <c>data.items</c>", "the field lives at <c>author.name</c>". Full JSONPath filter expressions
/// are out of scope (query pushdown belongs to P5's OData/GraphQL).
/// </summary>
public static class JsonRecords
{
    /// <summary>Splits a dotted path into its segments; an empty/null path yields no segments (the root).</summary>
    public static string[] SplitPath(string? path) =>
        string.IsNullOrEmpty(path) ? Array.Empty<string>() : path.Split('.', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Resolves the records array within an already-parsed response body — used by the paginated
    /// strategies, where one page's body is bounded by the page size and safe to materialize whole.
    /// </summary>
    /// <param name="root">The parsed response body.</param>
    /// <param name="recordsPath">Dotted path to the array; empty means <paramref name="root"/> itself.</param>
    /// <exception cref="HttpSourceException">Thrown when the path doesn't resolve to a JSON array.</exception>
    public static JsonElement GetArray(JsonElement root, string recordsPath)
    {
        JsonElement current = root;
        foreach (var segment in SplitPath(recordsPath))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                throw new HttpSourceException(null, null,
                    $"The configured records path '{recordsPath}' was not found in the response (missing segment '{segment}').");
            }

            current = next;
        }

        if (current.ValueKind != JsonValueKind.Array)
        {
            throw new HttpSourceException(null, null,
                $"The configured records path '{recordsPath}' does not resolve to a JSON array (found {current.ValueKind}).");
        }

        return current;
    }

    /// <summary>Reads a dotted field path within a single record element (field maps, cursor tokens, totals).</summary>
    /// <param name="record">The record (or response body) to read from.</param>
    /// <param name="dottedPath">Dotted field path; empty means <paramref name="record"/> itself.</param>
    /// <param name="value">The resolved value, when found.</param>
    public static bool TryGetField(JsonElement record, string dottedPath, out JsonElement value)
    {
        JsonElement current = record;
        foreach (var segment in SplitPath(dottedPath))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                value = default;
                return false;
            }

            current = next;
        }

        value = current;
        return true;
    }

    /// <summary>
    /// Streams the elements of the records array from a response body one at a time, so the
    /// <c>HttpPaginationStrategy.None</c> strategy stays constant-memory even for a large
    /// single response — only ever one element's bytes are materialized. Uses a growable buffer
    /// refilled from the stream, retrying a token read when the buffer doesn't yet hold enough data
    /// (<c>isFinalBlock</c> false), since <see cref="Utf8JsonReader"/> is a ref struct and cannot
    /// itself span an <c>await</c>/<c>yield</c> boundary. Each element is parsed from an isolated
    /// byte slice located via <see cref="Utf8JsonReader.TokenStartIndex"/>/<see cref="Utf8JsonReader.TrySkip"/>
    /// rather than read directly off the continuing reader — verified empirically that
    /// <c>JsonDocument.ParseValue</c>/<c>JsonSerializer.Deserialize&lt;T&gt;(ref reader)</c> both
    /// enforce "exactly one value, nothing after it" on whatever remains in the reader's span
    /// regardless of the incoming <see cref="JsonReaderState"/>'s array nesting, throwing on the
    /// <c>,</c>/<c>]</c> that legitimately follows every element past the first; an isolated slice
    /// has nothing after it, so the same validation is satisfied trivially instead of worked around.
    /// </summary>
    /// <remarks>
    /// Property-name matching while descending <paramref name="recordsPath"/> does not track
    /// object depth — it assumes the path is a direct, unambiguous chain of property names from
    /// the response root, the common shape for a "the array is at <c>data.items</c>" REST response.
    /// A response with an unrelated property sharing a path segment's name at a shallower depth
    /// than the real target is a known, accepted limitation of this streaming reader (the
    /// paginated strategies' <see cref="GetArray"/> above has no such limitation, since it walks
    /// an already-parsed tree).
    /// </remarks>
    public static async IAsyncEnumerable<JsonElement> StreamArrayAsync(
        Stream stream, string recordsPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string[] segments = SplitPath(recordsPath);
        var buffer = new byte[4096];
        var bufferLength = 0;
        var state = new JsonReaderState(new JsonReaderOptions { AllowTrailingCommas = false });
        var segmentIndex = 0;
        var inArray = false;
        var isFinalBlock = false;

        while (true)
        {
            if (!isFinalBlock)
            {
                if (bufferLength == buffer.Length)
                    Array.Resize(ref buffer, buffer.Length * 2);

                int read = await stream.ReadAsync(buffer.AsMemory(bufferLength, buffer.Length - bufferLength), cancellationToken)
                    .ConfigureAwait(false);
                bufferLength += read;
                isFinalBlock = read == 0;
            }

            var consumed = 0;
            var needMoreData = false;

            while (!needMoreData)
            {
                if (!inArray)
                {
                    var navigator = new Utf8JsonReader(buffer.AsSpan(consumed, bufferLength - consumed), isFinalBlock, state);
                    if (!TryRead(ref navigator, isFinalBlock, out needMoreData))
                        break;

                    if (segmentIndex < segments.Length)
                    {
                        if (navigator.TokenType == JsonTokenType.PropertyName && navigator.ValueTextEquals(segments[segmentIndex]))
                            segmentIndex++;
                    }
                    else if (navigator.TokenType == JsonTokenType.StartArray)
                    {
                        inArray = true;
                    }
                    else
                    {
                        // Reached once, on the token immediately following the last matched path
                        // segment's PropertyName (JSON grammar guarantees a value follows a property
                        // name with nothing else in between) — if that value isn't an array, the
                        // configured path doesn't resolve to one; throw now instead of continuing to
                        // scan forward and silently latching onto an unrelated array elsewhere in the
                        // response (mirrors GetArray's ValueKind check for the paginated strategies).
                        throw new HttpSourceException(null, null,
                            $"The configured records path '{recordsPath}' does not resolve to a JSON array (found {navigator.TokenType}).");
                    }

                    consumed += (int)navigator.BytesConsumed;
                    state = navigator.CurrentState;
                    continue;
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(consumed, bufferLength - consumed), isFinalBlock, state);
                if (!TryRead(ref reader, isFinalBlock, out needMoreData))
                    break;

                if (reader.TokenType == JsonTokenType.EndArray)
                    yield break;

                // JsonDocument.ParseValue/JsonSerializer.Deserialize<T>(ref reader) both apply their
                // own "exactly one value, nothing after it" validation to the reader's remaining span
                // regardless of the incoming state's array nesting — verified empirically (both threw
                // on the ',' or ']' that legitimately follows an array element, past the first).
                // TokenStartIndex + TrySkip instead locate the exact byte range of just this one
                // element (skipping the leading separator TokenStartIndex already excludes), which is
                // then parsed in isolation — a standalone slice has genuinely nothing after it, so the
                // same validation that broke the reader-continuation approach is satisfied trivially.
                long valueStart = reader.TokenStartIndex;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    bool skipped;
                    try
                    {
                        skipped = reader.TrySkip();
                    }
                    catch (JsonException) when (!isFinalBlock)
                    {
                        needMoreData = true;
                        break;
                    }
                    catch (JsonException ex)
                    {
                        // isFinalBlock: no more bytes are coming, so this is a genuine malformed/
                        // truncated element (e.g. an unterminated string cut off mid-value), not
                        // "insufficient data buffered so far" — surface it as the same source-level
                        // failure a bad response gets elsewhere, not a raw framework exception.
                        throw new HttpSourceException(null, null,
                            "The response body contains malformed or truncated JSON while reading the records array.", ex);
                    }

                    if (!skipped)
                    {
                        needMoreData = true;
                        break;
                    }
                }

                long valueEnd = reader.BytesConsumed;
                ReadOnlySpan<byte> elementBytes = buffer.AsSpan(consumed + (int)valueStart, (int)(valueEnd - valueStart));

                // JsonDocument.Parse has no ReadOnlySpan<byte> overload (it owns memory it can outlive
                // this call with; a span can't provide that), so the isolated slice is copied once.
                JsonElement element;
                using (JsonDocument document = JsonDocument.Parse(elementBytes.ToArray()))
                    element = document.RootElement.Clone();

                consumed += (int)valueEnd;
                state = reader.CurrentState;

                yield return element;
            }

            if (consumed > 0)
            {
                Buffer.BlockCopy(buffer, consumed, buffer, 0, bufferLength - consumed);
                bufferLength -= consumed;
            }

            if (isFinalBlock && needMoreData)
            {
                // The stream ended (no more bytes) while still needing more data — either the
                // configured records path was never found before the response ran out, or an
                // element was cut off mid-parse (a dropped connection/truncated body). Both are
                // genuine failures, not "zero rows": throwing here (instead of yield break) keeps a
                // truncated read from being silently reported as a successful, if short, run.
                if (!inArray)
                {
                    throw new HttpSourceException(null, null,
                        $"The configured records path '{recordsPath}' was not found in the response.");
                }

                throw new HttpSourceException(null, null,
                    "The response body ended before the records array was fully read (the connection may have been dropped or the body truncated).");
            }
        }
    }

    /// <summary>
    /// Reads the next token, distinguishing "not enough data buffered yet" (returns <c>false</c>,
    /// <paramref name="needMoreData"/> <c>true</c>) from a genuinely malformed body once no more
    /// bytes are coming (<paramref name="isFinalBlock"/>), which is wrapped into
    /// <see cref="HttpSourceException"/> instead of leaking a raw <see cref="JsonException"/>.
    /// </summary>
    private static bool TryRead(ref Utf8JsonReader reader, bool isFinalBlock, out bool needMoreData)
    {
        try
        {
            if (!reader.Read())
            {
                needMoreData = true;
                return false;
            }
        }
        catch (JsonException) when (!isFinalBlock)
        {
            needMoreData = true;
            return false;
        }
        catch (JsonException ex)
        {
            throw new HttpSourceException(null, null, "The response body contains malformed or truncated JSON.", ex);
        }

        needMoreData = false;
        return true;
    }
}
