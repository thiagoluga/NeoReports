using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NeoReports.Abstractions;

namespace NeoReports.QueryBuilder.Pro;

/// <summary>Thrown when a <see cref="QueryModel"/> can't produce valid keyset SQL (a builder-state bug).</summary>
public sealed class QueryModelException : Exception
{
    /// <summary>Creates the exception with a message describing the invalid model.</summary>
    public QueryModelException(string message) : base(message) { }
}

/// <summary>The generated report SQL, its bind parameters, and the derived output schema (ADR D49).</summary>
/// <param name="Sql">Keyset report SQL — exposes <c>@cursor</c> and ends in <c>ORDER BY</c> the key.</param>
/// <param name="Parameters">The WHERE filter values, by parameter name (without prefix). <c>@cursor</c>/<c>@pageSize</c> are bound by the keyset source, not here.</param>
/// <param name="Schema">The report's output schema, derived from the selected columns.</param>
public sealed record GeneratedQuery(string Sql, IReadOnlyDictionary<string, object?> Parameters, ReportSchema Schema);

/// <summary>
/// Turns a visually-composed <see cref="QueryModel"/> into keyset-safe report SQL (ADR D49, Epic K).
/// <para>
/// Injection-safe by construction: every table/column identifier comes from the model and is quoted
/// per dialect; every table alias is validated against a strict identifier pattern (it's the one
/// token used unquoted); every WHERE value is emitted as a bind-parameter placeholder and returned
/// separately — never concatenated into the SQL text. The keyset wrapper
/// (<c>(@cursor IS NULL OR key &gt; @cursor) ... ORDER BY key</c>) is appended from the chosen key,
/// so a generated query is always a valid keyset query.
/// </para>
/// </summary>
public static class KeysetSqlGenerator
{
    private const string FilterParameterPrefix = "qbfilter";
    private static readonly Regex SafeAlias = new(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.Compiled);

    /// <summary>Generates the report SQL, bind parameters, and output schema for <paramref name="model"/>.</summary>
    /// <param name="model">The visually-composed query.</param>
    /// <param name="dialect">The target provider's SQL knobs (see <see cref="SqlDialect"/>).</param>
    public static GeneratedQuery Generate(QueryModel model, SqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(dialect);
        Validate(model);

        var aliases = new HashSet<string>(StringComparer.Ordinal) { ValidateAlias(model.From.Alias) };
        foreach (QueryJoin join in model.Joins)
            aliases.Add(ValidateAlias(join.Table.Alias));

        string selectList = string.Join(", ", model.Select.Select(c => RenderSelect(c, dialect, aliases)));

        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(selectList);
        sql.Append("\nFROM ").Append(RenderTable(model.From, dialect));
        foreach (QueryJoin join in model.Joins)
        {
            string keyword = join.Kind == JoinKind.Left ? "LEFT JOIN" : "INNER JOIN";
            sql.Append('\n').Append(keyword).Append(' ').Append(RenderTable(join.Table, dialect))
               .Append(" ON ").Append(RenderRef(join.Left, dialect, aliases))
               .Append(" = ").Append(RenderRef(join.Right, dialect, aliases));
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var conditions = new List<string>();
        for (var i = 0; i < model.Where.Count; i++)
            conditions.Add(RenderFilter(model.Where[i], i, dialect, aliases, parameters));

        string keyExpr = RenderRef(model.Key, dialect, aliases);
        string cursorToken = dialect.ParameterPrefix + "cursor";
        string cursorComparand = ApplyCast(cursorToken, SqlTypeMap.ToColumnType(model.KeyDataType), dialect);
        conditions.Add($"({cursorToken} IS NULL OR {keyExpr} > {cursorComparand})");
        sql.Append("\nWHERE ").Append(string.Join(" AND ", conditions));

        if (model.GroupBy.Count > 0)
            sql.Append("\nGROUP BY ").Append(string.Join(", ", model.GroupBy.Select(g => RenderRef(g, dialect, aliases))));

        sql.Append("\nORDER BY ").Append(keyExpr);

        return new GeneratedQuery(sql.ToString(), parameters, DeriveSchema(model));
    }

    private static void Validate(QueryModel model)
    {
        if (model.Select.Count == 0)
            throw new QueryModelException("A query must select at least one column.");

        bool hasAggregate = model.Select.Any(c => c.Aggregate != QueryAggregate.None);
        bool grouped = model.GroupBy.Count > 0;

        if (hasAggregate && !grouped)
            throw new QueryModelException("An aggregated query must group by at least one column.");

        if (grouped)
        {
            var groupSet = new HashSet<(string, string)>(model.GroupBy.Select(g => (g.TableAlias, g.Column)));
            foreach (QuerySelectColumn c in model.Select.Where(c => c.Aggregate == QueryAggregate.None))
            {
                if (!groupSet.Contains((c.Source.TableAlias, c.Source.Column)))
                    throw new QueryModelException(
                        $"Selected column '{c.Source.TableAlias}.{c.Source.Column}' must be aggregated or grouped.");
            }

            if (!groupSet.Contains((model.Key.TableAlias, model.Key.Column)))
                throw new QueryModelException("The keyset key column must be one of the grouped columns.");
        }
    }

    private static string ValidateAlias(string alias)
    {
        if (string.IsNullOrEmpty(alias) || !SafeAlias.IsMatch(alias))
            throw new QueryModelException($"Table alias '{alias}' is not a valid identifier.");
        return alias;
    }

    private static string RenderTable(QueryTable table, SqlDialect dialect)
    {
        string qualified = string.IsNullOrEmpty(table.Schema)
            ? dialect.QuoteIdentifier(table.Name)
            : dialect.QuoteIdentifier(table.Schema) + "." + dialect.QuoteIdentifier(table.Name);
        return qualified + " " + ValidateAlias(table.Alias);
    }

    private static string RenderRef(QueryColumnRef reference, SqlDialect dialect, HashSet<string> aliases)
    {
        if (!aliases.Contains(ValidateAlias(reference.TableAlias)))
            throw new QueryModelException($"Column reference uses unknown table alias '{reference.TableAlias}'.");
        return reference.TableAlias + "." + dialect.QuoteIdentifier(reference.Column);
    }

    private static string RenderSelect(QuerySelectColumn column, SqlDialect dialect, HashSet<string> aliases)
    {
        string expr = RenderRef(column.Source, dialect, aliases);
        string wrapped = column.Aggregate switch
        {
            QueryAggregate.None => expr,
            QueryAggregate.Count => $"count({expr})",
            QueryAggregate.Sum => $"sum({expr})",
            QueryAggregate.Avg => $"avg({expr})",
            QueryAggregate.Min => $"min({expr})",
            QueryAggregate.Max => $"max({expr})",
            _ => throw new QueryModelException($"Unknown aggregate '{column.Aggregate}'."),
        };
        return $"{wrapped} AS {dialect.QuoteIdentifier(column.OutputName)}";
    }

    private static string RenderFilter(
        QueryFilter filter, int index, SqlDialect dialect, HashSet<string> aliases, Dictionary<string, object?> parameters)
    {
        string column = RenderRef(filter.Column, dialect, aliases);
        string paramName = FilterParameterPrefix + index.ToString(CultureInfo.InvariantCulture);
        string token = dialect.ParameterPrefix + paramName;

        (string op, string? value) = filter.Operator switch
        {
            QueryFilterOperator.Equals => ("=", filter.Value),
            QueryFilterOperator.NotEquals => ("<>", filter.Value),
            QueryFilterOperator.GreaterThan => (">", filter.Value),
            QueryFilterOperator.GreaterThanOrEqual => (">=", filter.Value),
            QueryFilterOperator.LessThan => ("<", filter.Value),
            QueryFilterOperator.LessThanOrEqual => ("<=", filter.Value),
            QueryFilterOperator.Contains => ("LIKE", $"%{filter.Value}%"),
            QueryFilterOperator.StartsWith => ("LIKE", $"{filter.Value}%"),
            _ => throw new QueryModelException($"Unknown filter operator '{filter.Operator}'."),
        };

        // LIKE always compares text to text (the wildcarded value is a string) — casting only applies
        // to the type-sensitive comparisons.
        string comparand = op == "LIKE" ? token : ApplyCast(token, SqlTypeMap.ToColumnType(filter.DataType), dialect);
        parameters[paramName] = value;
        return $"{column} {op} {comparand}";
    }

    private static string ApplyCast(string token, ColumnType type, SqlDialect dialect) =>
        dialect.CastParameter?.Invoke(type, token) ?? token;

    private static ReportSchema DeriveSchema(QueryModel model)
    {
        var columns = model.Select.Select(c => new ReportColumn(
            c.OutputName,
            c.Aggregate switch
            {
                QueryAggregate.Count => ColumnType.Integer,
                QueryAggregate.Sum or QueryAggregate.Avg => ColumnType.Decimal,
                // Min/Max/None keep the source column's own mapped type.
                _ => SqlTypeMap.ToColumnType(c.DataType),
            })).ToArray();
        return new ReportSchema(columns);
    }
}
