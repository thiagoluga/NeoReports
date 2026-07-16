namespace NeoReports.QueryBuilder.Pro;

/// <summary>How a joined table is combined with the ones before it.</summary>
public enum JoinKind
{
    /// <summary>Inner join — rows must match on both sides.</summary>
    Inner,

    /// <summary>Left outer join — every left row is kept; unmatched right columns are NULL.</summary>
    Left,
}

/// <summary>An aggregate applied to a selected column, or <see cref="None"/> for a plain column.</summary>
public enum QueryAggregate
{
    /// <summary>No aggregate — select the column value.</summary>
    None,

    /// <summary><c>count(...)</c>.</summary>
    Count,

    /// <summary><c>sum(...)</c>.</summary>
    Sum,

    /// <summary><c>avg(...)</c>.</summary>
    Avg,

    /// <summary><c>min(...)</c>.</summary>
    Min,

    /// <summary><c>max(...)</c>.</summary>
    Max,
}

/// <summary>A WHERE comparison operator (mirrors the preview filter operators, ADR D45).</summary>
public enum QueryFilterOperator
{
    /// <summary><c>=</c>.</summary>
    Equals,

    /// <summary><c>&lt;&gt;</c>.</summary>
    NotEquals,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterThanOrEqual,

    /// <summary><c>&lt;</c>.</summary>
    LessThan,

    /// <summary><c>&lt;=</c>.</summary>
    LessThanOrEqual,

    /// <summary><c>LIKE '%value%'</c>.</summary>
    Contains,

    /// <summary><c>LIKE 'value%'</c>.</summary>
    StartsWith,
}

/// <summary>A reference to a column of one table in the query, by that table's <see cref="TableAlias"/>.</summary>
/// <param name="TableAlias">The alias of the owning table (as assigned in <see cref="QueryTable.Alias"/>).</param>
/// <param name="Column">The column name (as returned by the catalog).</param>
public sealed record QueryColumnRef(string TableAlias, string Column);

/// <summary>A table in the FROM/JOIN, with the alias used to qualify its columns.</summary>
/// <param name="Schema">The table's schema/owner (as returned by the catalog); may be empty.</param>
/// <param name="Name">The table name.</param>
/// <param name="Alias">A short alias assigned by the builder (e.g. <c>t0</c>); validated to a safe identifier.</param>
public sealed record QueryTable(string Schema, string Name, string Alias);

/// <summary>One join: <see cref="Left"/> (a column of an earlier table) equals <see cref="Right"/> (a column of <see cref="Table"/>).</summary>
/// <param name="Kind">Inner or left.</param>
/// <param name="Table">The newly joined table.</param>
/// <param name="Left">The join column on an already-present table.</param>
/// <param name="Right">The join column on <see cref="Table"/>.</param>
public sealed record QueryJoin(JoinKind Kind, QueryTable Table, QueryColumnRef Left, QueryColumnRef Right);

/// <summary>A selected output column.</summary>
/// <param name="Source">The source column.</param>
/// <param name="OutputName">The report column name (also the SQL alias).</param>
/// <param name="DataType">The catalog's declared DB type name (mapped to a <c>ColumnType</c> for the schema).</param>
/// <param name="Aggregate">An optional aggregate wrapping the column.</param>
public sealed record QuerySelectColumn(
    QueryColumnRef Source, string OutputName, string DataType, QueryAggregate Aggregate = QueryAggregate.None);

/// <summary>One WHERE condition. <see cref="Value"/> is always bound as a parameter, never inlined.</summary>
/// <param name="Column">The column to compare.</param>
/// <param name="Operator">The comparison operator.</param>
/// <param name="Value">The comparison value (its literal text form; bound as a parameter).</param>
/// <param name="DataType">The column's declared DB type (drives the parameter cast on dialects that need it).</param>
public sealed record QueryFilter(
    QueryColumnRef Column, QueryFilterOperator Operator, string Value, string DataType);

/// <summary>
/// The structured, visually-composed query (ADR D49). The <see cref="KeysetSqlGenerator"/> turns it
/// into keyset-safe report SQL. When <see cref="GroupBy"/> is non-empty, <see cref="Key"/> must be
/// one of the grouped columns (you can't keyset-paginate an aggregated result on a non-grouped key).
/// </summary>
/// <param name="From">The primary table.</param>
/// <param name="Joins">Joins, applied in order after <see cref="From"/>.</param>
/// <param name="Select">The output columns (at least one).</param>
/// <param name="Where">WHERE conditions (ANDed together); may be empty.</param>
/// <param name="GroupBy">GROUP BY columns; empty for a non-aggregated query.</param>
/// <param name="Key">The keyset key column — the query is ordered by it and paginated on it.</param>
/// <param name="KeyDataType">The key column's declared DB type (drives the cursor cast).</param>
public sealed record QueryModel(
    QueryTable From,
    IReadOnlyList<QueryJoin> Joins,
    IReadOnlyList<QuerySelectColumn> Select,
    IReadOnlyList<QueryFilter> Where,
    IReadOnlyList<QueryColumnRef> GroupBy,
    QueryColumnRef Key,
    string KeyDataType);
