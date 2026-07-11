using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;

namespace NeoReports.Sources.Common;

/// <summary>
/// Shared property-bag readers for <c>IConfigSourceProvider</c> implementations across the
/// relational provider packages (Postgres, MySQL, Oracle) — the same parsing
/// <c>SqlConfigSourceProvider</c> does inline, extracted so it isn't triplicated.
/// </summary>
public static class AdoConfigProperties
{
    /// <summary>Reads a required, non-empty string property, or throws <see cref="ConfigurationException"/>.</summary>
    public static string RequireString(IReadOnlyDictionary<string, object?>? properties, string key, string sourceTypeLabel)
    {
        if (properties is not null
            && properties.TryGetValue(key, out var value)
            && value is string text
            && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new ConfigurationException($"The {sourceTypeLabel} source requires a non-empty '{key}' property.");
    }

    /// <summary>Reads an optional integer property (accepting numeric JSON, strings, or CLR numeric types).</summary>
    public static int? OptionalInt(IReadOnlyDictionary<string, object?>? properties, string key, string sourceTypeLabel)
    {
        if (properties is null || !properties.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            double d => (int)d,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetInt32(),
            _ => throw new ConfigurationException(
                $"The {sourceTypeLabel} source property '{key}' must be an integer (was {value.GetType().Name})."),
        };
    }

    /// <summary>
    /// Materializes a positional <see cref="ReportRecord"/> by reading, for each schema column,
    /// the result-set column whose name matches it (case-insensitive) — the dynamic path's
    /// row-shape, shared across every relational <c>IConfigSourceProvider</c>.
    /// </summary>
    public static ReportRecord MaterializeReportRecord(
        DbDataReader reader, IReadOnlyDictionary<string, int> ordinalByName, ReportSchema schema)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            values[i] = ordinalByName.TryGetValue(schema.Columns[i].Name, out int ordinal) && !reader.IsDBNull(ordinal)
                ? reader.GetValue(ordinal)
                : null;
        }

        return new ReportRecord(schema, values);
    }

    /// <summary>
    /// The full body of a relational provider's <c>IConfigSourceProvider.Create</c> — reads
    /// <c>connectionString</c>/<c>sql</c>/<c>key</c>/<c>pageSize</c> from
    /// <paramref name="source"/>'s properties and produces an <see cref="AdoKeysetSource{T}"/> of
    /// positional <see cref="ReportRecord"/>s, shared so every provider's own <c>Create</c> is a
    /// one-line call.
    /// </summary>
    /// <param name="connectionFactory">Given the resolved connection string, creates a new, unopened connection.</param>
    /// <param name="source">The dynamic-path source config being compiled.</param>
    /// <param name="schema">The report's declared output schema.</param>
    /// <param name="sourceTypeLabel">Human-readable provider name for error messages (e.g. "MySQL").</param>
    /// <param name="parameterPrefix">Bind-variable prefix the provider expects (<c>@</c> for SQL
    /// Server/Postgres/MySQL, <c>:</c> for Oracle).</param>
    /// <param name="configureCommand">Optional hook run on each page's command right after
    /// creation — e.g. Oracle needs <c>((OracleCommand)command).BindByName = true</c>.</param>
    public static IBatchSource<ReportRecord> CreateAdoConfigSource(
        Func<string, DbConnection> connectionFactory, SourceConfig source, ReportSchema schema, string sourceTypeLabel,
        string parameterPrefix = "@", Action<DbCommand>? configureCommand = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        IReadOnlyDictionary<string, object?>? properties = source.Properties;
        string connectionString = RequireString(properties, "connectionString", sourceTypeLabel);
        string sql = RequireString(properties, "sql", sourceTypeLabel);
        string key = RequireString(properties, "key", sourceTypeLabel);
        int pageSize = OptionalInt(properties, "pageSize", sourceTypeLabel) ?? 1000;

        return new AdoKeysetSource<ReportRecord>(
            () => connectionFactory(connectionString), sql, key, pageSize, schema,
            parameters: null,
            materialize: (reader, ordinals) => MaterializeReportRecord(reader, ordinals, schema),
            parameterPrefix, configureCommand);
    }
}
