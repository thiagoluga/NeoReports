using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NeoReports.Abstractions;

namespace NeoReports.Sources.MongoDb;

/// <summary>
/// Batch source using keyset pagination over a MongoDB collection (ADR D44): each page reads
/// <c>Find(key &gt; cursor).Sort(key ascending).Limit(pageSize)</c> instead of skip/offset, so
/// pagination doesn't drift under concurrent writes — the same outcome as the relational sources'
/// keyset engine, without sharing its ADO.NET machinery (Mongo has no <c>DbConnection</c>/
/// <c>DbDataReader</c>). Unlike a <c>DbConnection</c>, a <see cref="MongoClient"/> already owns its
/// own pooled connections and is meant to be created once and reused, not per operation — one is
/// built in the constructor and shared across every page. The cursor is the last page's key field,
/// round-tripped through MongoDB Extended JSON so its exact BSON type (not just its textual form)
/// survives the trip — an int64 key decoded back as a double would silently break the <c>Gt</c>
/// comparison.
/// </summary>
/// <typeparam name="T">The row type produced.</typeparam>
public sealed class MongoDbKeysetSource<T> : IBatchSource<T>
{
    private readonly MongoClient _client;
    private readonly string _database;
    private readonly string _collection;
    private readonly string _keyField;
    private readonly int _pageSize;
    private readonly Func<BsonDocument, T> _materialize;

    /// <summary>Creates the source.</summary>
    /// <param name="connectionString">MongoDB connection string.</param>
    /// <param name="database">Database name.</param>
    /// <param name="collection">Collection name.</param>
    /// <param name="keyField">Name of the keyset field, ascending-sorted and used for the <c>Gt</c> page filter.</param>
    /// <param name="pageSize">Maximum documents per page.</param>
    /// <param name="schema">The output schema this source declares.</param>
    /// <param name="materialize">Maps a raw <see cref="BsonDocument"/> to <typeparamref name="T"/>;
    /// when null, reflection over the POCO's longest constructor is used (see
    /// <see cref="BsonDocumentMaterializer{T}"/> — deliberately not MongoDB.Driver's own
    /// <c>BsonClassMap</c> deserialization, which silently rebinds a property named <c>Id</c> to
    /// the document's <c>_id</c> field).</param>
    public MongoDbKeysetSource(
        string connectionString,
        string database,
        string collection,
        string keyField,
        int pageSize,
        ReportSchema schema,
        Func<BsonDocument, T>? materialize = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        _client = new MongoClient(connectionString);
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _keyField = keyField ?? throw new ArgumentNullException(nameof(keyField));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _materialize = materialize ?? new BsonDocumentMaterializer<T>().Materialize;
    }

    /// <inheritdoc />
    public ReportSchema Schema { get; }

    /// <inheritdoc />
    public async Task<BatchResult<T>> ReadBatchAsync(BatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IMongoCollection<BsonDocument> collection = _client.GetDatabase(_database).GetCollection<BsonDocument>(_collection);

        FilterDefinition<BsonDocument> filter = context.Cursor is null
            ? FilterDefinition<BsonDocument>.Empty
            : Builders<BsonDocument>.Filter.Gt(_keyField, DecodeCursor(context.Cursor));

        List<BsonDocument> documents = await collection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending(_keyField))
            .Limit(_pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var records = new List<T>(documents.Count);
        BsonValue? lastKey = null;
        foreach (BsonDocument document in documents)
        {
            records.Add(_materialize(document));
            if (document.TryGetValue(_keyField, out BsonValue value) && value is not BsonNull)
                lastKey = value;
        }

        var hasMore = documents.Count == _pageSize && lastKey is not null;
        var nextCursor = hasMore ? EncodeCursor(lastKey!) : null;
        return new BatchResult<T>(records, nextCursor, hasMore);
    }

    private static string EncodeCursor(BsonValue key) => key.ToJson();

    private static BsonValue DecodeCursor(string cursor) => BsonSerializer.Deserialize<BsonValue>(cursor);
}
