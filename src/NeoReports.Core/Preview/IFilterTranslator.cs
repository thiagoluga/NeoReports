using NeoReports.Abstractions;

namespace NeoReports.Core.Preview;

/// <summary>
/// Per-source-type seam that turns structured <see cref="PreviewFilter"/> rows into a filtered
/// variant of a keyset query (ADR D45). Resolved from DI by <see cref="Type"/>, exactly like
/// <c>IConfigSourceProvider</c>/<c>ISourceHealthCheck</c>. Implemented once in
/// <c>NeoReports.Sources.Common</c> for the SQL family (Sql/Postgres/MySql/Oracle) — the
/// WHERE-fragment-append logic is identical ADO.NET regardless of dialect — and never implemented
/// for MongoDB in this pass; a source type with no registered translator means previews for that
/// report run unfiltered, and the caller reports filters as ignored rather than silently dropped.
/// </summary>
public interface IFilterTranslator
{
    /// <summary>Source type id this translator handles (e.g. "postgres"); matched case-insensitively.</summary>
    string Type { get; }

    /// <summary>
    /// Applies the filters against the source's effective <paramref name="properties"/> bag, and
    /// returns the property keys to overwrite to express the filtered query plus (when the source
    /// type has a bind-parameter mechanism) the bind values for them. Values are always returned
    /// for parameter binding — never string-concatenated into a returned property — but a
    /// translator with no bind-parameter mechanism (e.g. an OData translator that inlines the whole
    /// expression into a URL) always returns an empty <paramref name="parameters"/> dictionary.
    /// </summary>
    /// <param name="properties">
    /// The source's effective property bag (e.g. a SQL-family source's <c>"sql"</c> property) —
    /// whatever this translator's source type needs to build a filtered variant of. A translator
    /// that requires a specific key (e.g. <c>AdoFilterTranslator</c>'s <c>"sql"</c>) reads it from
    /// here itself and throws <see cref="ConfigurationException"/> when it's absent.
    /// </param>
    /// <param name="filters">The filters to apply; never empty when called.</param>
    /// <param name="schema">
    /// The report's declared output schema — lets a translator look up each filter column's
    /// declared <see cref="ColumnType"/> (e.g. to cast a bind parameter to the column's real SQL
    /// type on a dialect that doesn't implicitly convert across a comparison).
    /// </param>
    /// <param name="propertyOverrides">
    /// The property keys to merge/overwrite into a copy of <paramref name="properties"/> to apply
    /// the filter (e.g. <c>{["sql"] = translatedSql}</c> for the SQL family, <c>{["filter"] = ...}</c>
    /// for OData).
    /// </param>
    /// <param name="parameters">
    /// Bind values for the filter parameters referenced in <paramref name="propertyOverrides"/>;
    /// empty when the translator has no bind-parameter mechanism.
    /// </param>
    /// <returns><c>true</c> when the filters were translated; <c>false</c> when this source type cannot apply them.</returns>
    bool TryTranslate(
        IReadOnlyDictionary<string, object?> properties,
        IReadOnlyList<PreviewFilter> filters,
        ReportSchema schema,
        out IReadOnlyDictionary<string, object?> propertyOverrides,
        out IReadOnlyDictionary<string, object?> parameters);
}
