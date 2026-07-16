using NeoReports.Core.SourceRegistry;

namespace NeoReports.Core.Schema;

/// <summary>
/// Per-source-type seam that introspects a registered source's database schema and previews table
/// data (ADR D49, Epic K). Resolved from DI by <see cref="Type"/>, exactly like
/// <c>IConfigSourceProvider</c>/<c>ISourceHealthCheck</c>/<c>IFilterTranslator</c>. Implemented once
/// in <c>NeoReports.Sources.Common</c> (<c>AdoSchemaExplorer</c>) for the SQL family, parametrized
/// by the dialect's catalog queries. A source type with no registered explorer (e.g. MongoDB in
/// this pass) means the interactive query builder isn't offered for that source — an honest
/// capability gap (D36), never a fabricated schema.
/// </summary>
/// <remarks>
/// Introspection exposes nothing a report couldn't already read: the resolved
/// <see cref="SourceDefinition"/>'s connection string is the same one the report runs under. All
/// identifiers this explorer emits into SQL (table/schema names in <see cref="PreviewTableAsync"/>)
/// come from the database's own catalog and are quoted per dialect, so the preview query is never
/// built from unescaped caller text.
/// </remarks>
public interface ISchemaExplorer
{
    /// <summary>Source type id this explorer handles (e.g. "postgres"); matched case-insensitively.</summary>
    string Type { get; }

    /// <summary>
    /// Reads the full catalog — every user table with its columns (name/type/nullable/primary-key)
    /// and outbound foreign keys — from the source's system catalog (<c>information_schema</c> or the
    /// dialect equivalent). Never reads the report author's own declared SQL.
    /// </summary>
    /// <param name="definition">The resolved (already <c>${VAR}</c>-substituted) source definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SchemaCatalog> GetCatalogAsync(SourceDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the first <paramref name="top"/> rows of one table, raw (no column masking in this pass,
    /// ADR D49). <paramref name="schema"/>/<paramref name="table"/> are quoted per dialect before use.
    /// </summary>
    /// <param name="definition">The resolved (already <c>${VAR}</c>-substituted) source definition.</param>
    /// <param name="schema">The table's schema/owner, as returned by <see cref="GetCatalogAsync"/>.</param>
    /// <param name="table">The table name, as returned by <see cref="GetCatalogAsync"/>.</param>
    /// <param name="top">Maximum rows to return. The caller caps this at the UI's preview size (e.g. 50); the implementation additionally enforces its own hard ceiling as defense in depth, so a bypassing caller can never turn a preview into an unbounded scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TablePreview> PreviewTableAsync(
        SourceDefinition definition, string schema, string table, int top, CancellationToken cancellationToken);
}

/// <summary>A source's introspected catalog: every user table, ordered by schema then name.</summary>
/// <param name="Tables">The tables, each with its columns and outbound foreign keys.</param>
public sealed record SchemaCatalog(IReadOnlyList<CatalogTable> Tables);

/// <summary>One table (or view) in a <see cref="SchemaCatalog"/>.</summary>
/// <param name="Schema">The owning schema (Postgres/SQL Server) or database (MySQL) or owner (Oracle).</param>
/// <param name="Name">The table name.</param>
/// <param name="Columns">The columns, in ordinal order.</param>
/// <param name="ForeignKeys">Outbound foreign keys (this table's columns referencing another table) — enables FK-aware auto-join suggestions.</param>
public sealed record CatalogTable(
    string Schema,
    string Name,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<ForeignKey> ForeignKeys);

/// <summary>One column of a <see cref="CatalogTable"/>.</summary>
/// <param name="Name">The column name.</param>
/// <param name="DataType">The database's own type name (e.g. "integer", "varchar", "timestamp") — shown as-is; the UI maps it to a NeoReports <c>ColumnType</c> when deriving the report schema.</param>
/// <param name="Nullable">Whether the column allows NULL.</param>
/// <param name="IsPrimaryKey">Whether the column is part of the table's primary key.</param>
public sealed record CatalogColumn(string Name, string DataType, bool Nullable, bool IsPrimaryKey);

/// <summary>One outbound foreign key: <see cref="Column"/> on the owning table references <see cref="ReferencedTable"/>.<see cref="ReferencedColumn"/>.</summary>
/// <param name="Column">The referencing column on the owning table.</param>
/// <param name="ReferencedSchema">The referenced table's schema.</param>
/// <param name="ReferencedTable">The referenced table.</param>
/// <param name="ReferencedColumn">The referenced column.</param>
public sealed record ForeignKey(string Column, string ReferencedSchema, string ReferencedTable, string ReferencedColumn);

/// <summary>A bounded table preview: column names plus the sampled rows (ADR D49).</summary>
/// <param name="Columns">The selected column names, in order.</param>
/// <param name="Rows">The rows, each a positional <c>object?[]</c> aligned to <see cref="Columns"/>.</param>
public sealed record TablePreview(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);
