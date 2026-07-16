using NeoReports.Abstractions;

namespace NeoReports.Core.QueryBuilder;

/// <summary>
/// The generated report SQL for a visually-composed query (ADR D49). MIT-side result — no Pro types.
/// </summary>
/// <param name="Sql">Keyset report SQL — exposes <c>@cursor</c> and ends in <c>ORDER BY</c> the key.</param>
/// <param name="Parameters">The WHERE filter values by parameter name (without prefix). <c>@cursor</c>/<c>@pageSize</c> are bound by the keyset source, not here.</param>
/// <param name="Schema">The report's output schema, derived from the selected columns.</param>
public sealed record GeneratedReportSql(string Sql, IReadOnlyDictionary<string, object?> Parameters, ReportSchema Schema);

/// <summary>
/// The MIT seam between the visual query builder UI/endpoints and the Pro SQL generator (ADR D49,
/// Epic K). The OSS core and the AspNetCore layer depend only on this Core contract — the actual
/// query model and keyset-safe SQL generation live in the commercial <c>NeoReports.QueryBuilder.Pro</c>
/// package, which registers an implementation. A host that doesn't reference/register the Pro package
/// has no implementation, so the query-builder endpoint honestly returns "not available" (D36) — the
/// same capability-gating pattern as <c>ISchemaExplorer</c>/<c>IFilterTranslator</c>.
/// </summary>
/// <remarks>
/// The model is passed as its raw JSON (as the UI serialized it), not a typed model, precisely so the
/// model type can stay Pro-only — the MIT layers never need to reference it. The implementation
/// deserializes and validates it, throwing on a malformed or unsupported model.
/// </remarks>
public interface IQuerySqlGenerator
{
    /// <summary>
    /// Generates keyset-safe report SQL from a visually-composed query.
    /// </summary>
    /// <param name="modelJson">The query model as JSON (the UI's serialized builder state).</param>
    /// <param name="sourceType">The source type id (e.g. "postgres") — selects the SQL dialect.</param>
    /// <returns>The generated SQL, its bind parameters, and the derived output schema.</returns>
    /// <exception cref="QuerySqlGenerationException">The model is malformed, invalid, or targets an unsupported source type.</exception>
    GeneratedReportSql Generate(string modelJson, string sourceType);
}

/// <summary>Thrown by <see cref="IQuerySqlGenerator.Generate"/> when the request can't produce SQL (bad model or unsupported type).</summary>
public sealed class QuerySqlGenerationException : Exception
{
    /// <summary>Creates the exception with a caller-safe message (no secrets, no raw model echo).</summary>
    public QuerySqlGenerationException(string message) : base(message) { }
}
