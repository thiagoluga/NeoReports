using System.Linq.Expressions;

namespace NeoReports.Core.Building;

/// <summary>
/// Configures a single output "view": its own filters and/or columns over the report's source. When
/// a view declares columns they replace the report's columns for that output; report-level filters
/// still apply and the view's filters are added on top. This lets one source read produce several
/// filtered/projected outputs (e.g. an "approved" file and a "rejected" file).
/// </summary>
/// <typeparam name="TRow">The report's row type.</typeparam>
public sealed class OutputView<TRow>
{
    internal List<Func<TRow, bool>> ViewFilters { get; } = [];

    internal List<ColumnDefinition<TRow>> ViewColumns { get; } = [];

    /// <summary>Adds a filter that a row must pass to appear in this output.</summary>
    /// <param name="predicate">A predicate over the report row.</param>
    public OutputView<TRow> Where(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ViewFilters.Add(predicate);
        return this;
    }

    /// <summary>Declares a column for this output from a member selector.</summary>
    /// <typeparam name="TProp">The selected member type.</typeparam>
    /// <param name="selector">A member access expression, e.g. <c>v =&gt; v.Total</c>.</param>
    /// <param name="displayName">Optional header label; defaults to the member name.</param>
    /// <param name="format">Optional .NET format string for rendering.</param>
    /// <param name="culture">Optional culture name (e.g. "pt-BR") for rendering.</param>
    public OutputView<TRow> Column<TProp>(
        Expression<Func<TRow, TProp>> selector, string? displayName = null, string? format = null, string? culture = null)
    {
        ViewColumns.Add(ReportColumns.Col(selector, displayName, format, culture));
        return this;
    }

    /// <summary>Declares the output columns explicitly (see <see cref="ReportColumns.Col{T, TProp}"/>).</summary>
    /// <param name="columns">Column definitions.</param>
    public OutputView<TRow> Columns(params ColumnDefinition<TRow>[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ViewColumns.AddRange(columns);
        return this;
    }
}
