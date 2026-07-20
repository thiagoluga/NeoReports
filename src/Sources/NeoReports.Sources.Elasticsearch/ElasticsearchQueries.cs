using System.Text.Json;

namespace NeoReports.Sources.Elasticsearch;

/// <summary>
/// Shared default query and request-body-writing logic used by both the search and count requests
/// (ADR D64) — kept in one place so the "default to match_all" behavior can't drift between
/// <see cref="ElasticsearchBatchSource{T}"/> and <see cref="ElasticsearchRowCounter"/>.
/// </summary>
internal static class ElasticsearchQueries
{
    /// <summary>The Elasticsearch/OpenSearch "match everything" query, used when no static query is configured.</summary>
    public static readonly JsonElement MatchAll = JsonDocument.Parse("""{"match_all":{}}""").RootElement.Clone();

    /// <summary>Writes a <c>"query"</c> property, defaulting to <see cref="MatchAll"/> when <paramref name="staticQuery"/> is unset.</summary>
    public static void WriteQuery(Utf8JsonWriter writer, JsonElement? staticQuery)
    {
        writer.WritePropertyName("query");
        (staticQuery ?? MatchAll).WriteTo(writer);
    }
}
