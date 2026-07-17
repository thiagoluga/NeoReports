using System.Text.Json;
using System.Text.Json.Serialization;
using NeoReports.Core.QueryBuilder;

namespace NeoReports.QueryBuilder.Pro;

/// <summary>
/// The Pro implementation of the MIT <see cref="IQuerySqlGenerator"/> seam (ADR D49): deserializes
/// the UI's query model JSON into a <see cref="QueryModel"/>, resolves the dialect from the source
/// type, and delegates to <see cref="KeysetSqlGenerator"/>. Registering this (via
/// <c>AddQueryBuilder()</c>) is what turns the query-builder endpoint on for a host — without it the
/// endpoint honestly reports the capability as unavailable (D36).
/// </summary>
public sealed class QuerySqlGenerator : IQuerySqlGenerator
{
    // Case-insensitive + string enums so the UI's camelCase JSON with string enum values
    // ("Inner"/"Sum"/"Equals") binds to the PascalCase records and their enum members.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public GeneratedReportSql Generate(string modelJson, string sourceType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

        SqlDialect dialect = SqlDialect.ForType(sourceType)
            ?? throw new QuerySqlGenerationException($"The query builder does not support source type '{sourceType}'.");

        QueryModel model;
        try
        {
            model = JsonSerializer.Deserialize<QueryModel>(modelJson, JsonOptions)
                ?? throw new QuerySqlGenerationException("The query model was empty.");
        }
        catch (JsonException)
        {
            // Never echo the raw model back — just say it didn't parse.
            throw new QuerySqlGenerationException("The query model could not be parsed.");
        }

        // A parseable-but-incomplete model (e.g. `{}` or one missing `from`/`key`) binds absent
        // constructor params to null. Reject it as a caller error here — otherwise the null would
        // surface as a NullReferenceException deep in the generator, which is neither a
        // QueryModelException nor caller-safe, and would leak out as an opaque 500.
        RequireModel(model);

        try
        {
            GeneratedQuery generated = KeysetSqlGenerator.Generate(model, dialect);
            return new GeneratedReportSql(generated.Sql, generated.Parameters, generated.Schema, generated.KeyColumnName);
        }
        catch (QueryModelException ex)
        {
            throw new QuerySqlGenerationException(ex.Message);
        }
    }

    // Guards the required top-level members that JSON deserialization can leave null when their keys
    // are absent. The nested contents are validated by KeysetSqlGenerator; this only ensures the
    // generator never dereferences a null the binder produced from a missing key.
    private static void RequireModel(QueryModel model)
    {
        if (model.From is null ||
            model.Joins is null ||
            model.Select is null ||
            model.Where is null ||
            model.GroupBy is null ||
            model.Key is null ||
            string.IsNullOrWhiteSpace(model.KeyDataType))
        {
            throw new QuerySqlGenerationException("The query model is incomplete: it must have a source table, selected columns, and a key column.");
        }
    }
}
