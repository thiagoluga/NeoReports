using System.Data.Common;

namespace NeoReports.Sources.Common;

/// <summary>
/// Provider-specific extension knobs for <see cref="AdoKeysetSource{T}"/> and
/// <see cref="AdoNamedKeysetSource{T}"/> that most relational providers never need to override —
/// SQL Server/PostgreSQL/MySQL use every default, only Oracle sets <see cref="ParameterPrefix"/>
/// and <see cref="ConfigureCommand"/>. Bundled into one type so those two engines' constructors
/// stay within a reasonable parameter count as more providers add their own quirks.
/// </summary>
public sealed class AdoProviderOptions
{
    /// <summary>Bind-variable prefix the provider expects (<c>@</c> for SQL Server/Postgres/MySQL, <c>:</c> for Oracle).</summary>
    public string ParameterPrefix { get; init; } = "@";

    /// <summary>Optional hook run on each page's command right after creation — e.g. Oracle needs <c>((OracleCommand)command).BindByName = true</c>.</summary>
    public Action<DbCommand>? ConfigureCommand { get; init; }

    /// <summary>Static parameters bound on every page, besides the cursor.</summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    /// <summary>
    /// Text appended right after the wrapped query when <see cref="AdoKeysetSource{T}"/> counts rows
    /// for progress tracking (ADR D47) — <c>SELECT COUNT(*) FROM (&lt;sql&gt;&lt;suffix&gt;) q</c>. Every
    /// keyset query ends in <c>ORDER BY</c>, which SQL Server rejects inside a bare derived table;
    /// its provider sets this to <c>" OFFSET 0 ROWS"</c>, the same derived-table fix
    /// <c>AdoFilterTranslator</c> already established for filtered previews (D45/G7). Postgres/MySQL/
    /// Oracle accept a derived table ending in <c>ORDER BY</c> as-is, so they leave this empty.
    /// </summary>
    public string CountInnerSuffix { get; init; } = "";
}
