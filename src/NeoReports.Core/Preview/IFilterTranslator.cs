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
    /// Wraps <paramref name="sql"/> so the filters are applied server-side, and returns the bind
    /// values for them. Values are always returned for parameter binding — never string-concatenated
    /// into <paramref name="translatedSql"/>.
    /// </summary>
    /// <param name="sql">The keyset query to filter.</param>
    /// <param name="filters">The filters to apply; never empty when called.</param>
    /// <param name="schema">
    /// The report's declared output schema — lets a translator look up each filter column's
    /// declared <see cref="ColumnType"/> (e.g. to cast a bind parameter to the column's real SQL
    /// type on a dialect that doesn't implicitly convert across a comparison).
    /// </param>
    /// <param name="translatedSql">The filtered query text.</param>
    /// <param name="parameters">Bind values for the filter parameters referenced in <paramref name="translatedSql"/>.</param>
    /// <returns><c>true</c> when the filters were translated; <c>false</c> when this source type cannot apply them.</returns>
    bool TryTranslate(
        string sql,
        IReadOnlyList<PreviewFilter> filters,
        ReportSchema schema,
        out string translatedSql,
        out IReadOnlyDictionary<string, object?> parameters);
}
