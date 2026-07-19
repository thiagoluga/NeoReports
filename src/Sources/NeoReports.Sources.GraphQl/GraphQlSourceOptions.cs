using NeoReports.Sources.Http.Common;

namespace NeoReports.Sources.GraphQl;

/// <summary>
/// Fluent, mutable options for the GraphQL source (ADR D63) — mirrors the shape of
/// <c>NeoReports.Sources.OData.ODataSourceOptions</c>. Every GraphQL server defines its own schema,
/// so the query document, its paging variable names, and the Relay connection's location in the
/// response are all author-supplied; there is nothing this source can synthesize on its own.
/// </summary>
public sealed class GraphQlSourceOptions
{
    /// <summary>
    /// The GraphQL query document. Must declare the configured paging variables and select
    /// <c>pageInfo { hasNextPage endCursor }</c> on the configured connection.
    /// </summary>
    internal string? QueryDocument { get; private set; }

    /// <summary>Static, author-supplied variables merged into every request alongside the paging variables.</summary>
    internal IReadOnlyDictionary<string, object?>? StaticVariables { get; private set; }

    /// <summary>Dotted path to the Relay connection within the response's <c>data</c> object (e.g. <c>"viewer.repositories"</c>).</summary>
    internal string? ConnectionPath { get; private set; }

    /// <summary>Field name of each edge's node object. Default <c>"node"</c>.</summary>
    internal string NodePath { get; private set; } = "node";

    /// <summary>Name of the page-size variable in the query document. Default <c>"first"</c>.</summary>
    internal string FirstVariableName { get; private set; } = "first";

    /// <summary>Name of the cursor variable in the query document. Default <c>"after"</c>.</summary>
    internal string AfterVariableName { get; private set; } = "after";

    /// <summary>
    /// Optional report-column-name to dotted-node-field-path map, for the dynamic (config-driven)
    /// path only — a typed <c>.As&lt;T&gt;()</c> read always matches JSON fields to <c>T</c>'s
    /// properties directly by name.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? FieldMap { get; private set; }

    private readonly MutableHttpAuth _auth = new();

    /// <summary>Sets the GraphQL query document.</summary>
    public GraphQlSourceOptions Query(string document)
    {
        QueryDocument = string.IsNullOrWhiteSpace(document)
            ? throw new ArgumentException("Query document must be provided.", nameof(document))
            : document;
        return this;
    }

    /// <summary>Sets static, author-supplied variables merged into every request alongside the paging variables.</summary>
    public GraphQlSourceOptions Variables(IReadOnlyDictionary<string, object?> variables)
    {
        StaticVariables = variables ?? throw new ArgumentNullException(nameof(variables));
        return this;
    }

    /// <summary>Sets the dotted path to the Relay connection within the response's <c>data</c> object (e.g. <c>"viewer.repositories"</c>).</summary>
    public GraphQlSourceOptions Connection(string dottedPath)
    {
        ConnectionPath = string.IsNullOrWhiteSpace(dottedPath)
            ? throw new ArgumentException("Connection path must be provided.", nameof(dottedPath))
            : dottedPath;
        return this;
    }

    /// <summary>Sets the field name of each edge's node object; defaults to <c>"node"</c>.</summary>
    public GraphQlSourceOptions Node(string fieldName)
    {
        NodePath = string.IsNullOrWhiteSpace(fieldName)
            ? throw new ArgumentException("Node field name must be provided.", nameof(fieldName))
            : fieldName;
        return this;
    }

    /// <summary>Sets the paging variable names, for a schema that names them differently than <c>first</c>/<c>after</c>.</summary>
    public GraphQlSourceOptions PageVariables(string first, string after)
    {
        FirstVariableName = string.IsNullOrWhiteSpace(first)
            ? throw new ArgumentException("First-page variable name must be provided.", nameof(first))
            : first;
        AfterVariableName = string.IsNullOrWhiteSpace(after)
            ? throw new ArgumentException("After-cursor variable name must be provided.", nameof(after))
            : after;
        return this;
    }

    /// <summary>Maps report columns to dotted node field paths (dynamic path only).</summary>
    public GraphQlSourceOptions FieldsFrom(IReadOnlyDictionary<string, string> fieldMap)
    {
        FieldMap = fieldMap ?? throw new ArgumentNullException(nameof(fieldMap));
        return this;
    }

    /// <summary>Adds a static request header, applied to every request.</summary>
    public GraphQlSourceOptions Header(string name, string value)
    {
        _auth.Header(name, value);
        return this;
    }

    /// <summary>Sends a static API key as a request header (static auth only; OAuth2 is out of scope, ADR D61/D63).</summary>
    public GraphQlSourceOptions ApiKey(string headerName, string value)
    {
        _auth.ApiKey(headerName, value);
        return this;
    }

    /// <summary>Sends a static bearer token (<c>Authorization: Bearer &lt;token&gt;</c>) (static auth only; ADR D61/D63).</summary>
    public GraphQlSourceOptions Bearer(string token)
    {
        _auth.Bearer(token);
        return this;
    }

    /// <summary>Projects this instance's auth-related fields into the shared, source-agnostic <see cref="HttpAuth"/> shape.</summary>
    internal HttpAuth ToAuth() => _auth.ToAuth();
}
