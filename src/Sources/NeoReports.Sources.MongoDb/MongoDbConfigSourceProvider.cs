using MongoDB.Bson;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.MongoDb;

/// <summary>
/// Config-driven MongoDB source for the dynamic path (<c>type: "mongodb"</c>). Reads its settings
/// from the source <c>properties</c> (<c>connectionString</c>, <c>database</c>, <c>collection</c>,
/// <c>key</c>, optional <c>pageSize</c>) and produces an <see cref="IBatchSource{T}"/> of positional
/// <see cref="ReportRecord"/>s.
/// </summary>
public sealed class MongoDbConfigSourceProvider : IConfigSourceProvider
{
    private const string SourceTypeLabel = "MongoDB";

    /// <inheritdoc />
    public string Type => "mongodb";

    /// <inheritdoc />
    public IBatchSource<ReportRecord> Create(SourceConfig source, ReportSchema schema, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);

        IReadOnlyDictionary<string, object?>? properties = source.Properties;
        string connectionString = AdoConfigProperties.RequireString(properties, "connectionString", SourceTypeLabel);
        string database = AdoConfigProperties.RequireString(properties, "database", SourceTypeLabel);
        string collection = AdoConfigProperties.RequireString(properties, "collection", SourceTypeLabel);
        string key = AdoConfigProperties.RequireString(properties, "key", SourceTypeLabel);
        int pageSize = AdoConfigProperties.OptionalInt(properties, "pageSize", SourceTypeLabel) ?? 1000;

        return new MongoDbKeysetSource<ReportRecord>(
            connectionString, database, collection, key, pageSize, schema,
            materialize: document => MaterializeReportRecord(document, schema));
    }

    /// <summary>
    /// Materializes a positional <see cref="ReportRecord"/> by reading, for each schema column, the
    /// document field whose name matches it — the dynamic path's row shape, mirroring the
    /// relational providers' <c>AdoConfigProperties.MaterializeReportRecord</c>.
    /// </summary>
    private static ReportRecord MaterializeReportRecord(BsonDocument document, ReportSchema schema)
    {
        var values = new object?[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            var name = schema.Columns[i].Name;
            values[i] = document.TryGetValue(name, out BsonValue value) && value is not BsonNull
                ? BsonTypeMapper.MapToDotNetValue(value)
                : null;
        }

        return new ReportRecord(schema, values);
    }
}
