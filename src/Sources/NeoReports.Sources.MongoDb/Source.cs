using System.Linq.Expressions;
using NeoReports.Abstractions;
using NeoReports.Sources.Common;

namespace NeoReports.Sources.MongoDb;

/// <summary>Fluent entry points for MongoDB sources.</summary>
public static class Source
{
    /// <summary>Begins configuring a MongoDB source.</summary>
    /// <param name="connectionString">MongoDB connection string.</param>
    /// <param name="database">Database name.</param>
    /// <param name="collection">Collection name.</param>
    public static MongoDbSourceBuilder MongoDb(string connectionString, string database, string collection) =>
        new(connectionString, database, collection);
}

/// <summary>Intermediate builder that captures the connection details before the keyset key/page size are chosen.</summary>
public sealed class MongoDbSourceBuilder
{
    private readonly string _connectionString;
    private readonly string _database;
    private readonly string _collection;

    internal MongoDbSourceBuilder(string connectionString, string database, string collection)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <summary>
    /// Completes the source with a keyset key selector and page size. The key selector names the
    /// field used for keyset pagination and to compute the next cursor.
    /// </summary>
    /// <typeparam name="T">The row type produced.</typeparam>
    /// <typeparam name="TKey">The key field type.</typeparam>
    /// <param name="keySelector">Member selector for the key field, e.g. <c>v =&gt; v.Id</c>.</param>
    /// <param name="pageSize">Maximum documents per page. Default 1000.</param>
    public IBatchSource<T> Keyset<T, TKey>(Expression<Func<T, TKey>> keySelector, int pageSize = 1000)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var keyField = MemberSelector.GetMemberName(keySelector);
        var schema = new ReportSchema(new[] { new ReportColumn(keyField, ColumnType.String) });

        return new MongoDbKeysetSource<T>(_connectionString, _database, _collection, keyField, pageSize, schema);
    }
}
