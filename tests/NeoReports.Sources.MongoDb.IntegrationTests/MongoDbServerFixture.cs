using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

namespace NeoReports.Sources.MongoDb.IntegrationTests;

/// <summary>
/// Spins up an ephemeral MongoDB container once per test class and seeds a Sales collection.
/// Skipped automatically when Docker is unavailable.
/// </summary>
public sealed class MongoDbServerFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:6.0").Build();

    public string ConnectionString { get; private set; } = string.Empty;
    public static string Database => "salesdb";
    public static string Collection => "Sales";
    public bool Available { get; private set; }
    public int SeededRows { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception)
        {
            // Docker not available in this environment — tests will skip.
            Available = false;
            return;
        }

        ConnectionString = _container.GetConnectionString();
        await SeedAsync();
        Available = true;
    }

    private async Task SeedAsync()
    {
        using var client = new MongoClient(ConnectionString);
        IMongoCollection<BsonDocument> collection = client.GetDatabase(Database).GetCollection<BsonDocument>(Collection);

        // 2500 documents so a pageSize of 1000 yields 3 pages (2 full + 1 partial).
        const int total = 2500;
        var documents = new List<BsonDocument>(total);
        for (var id = 1; id <= total; id++)
        {
            var amount = id % 7 == 0 ? 0.00m : id * 1.5m;
            documents.Add(new BsonDocument
            {
                { "Id", (long)id },
                { "Customer", $"C{id}" },
                { "Amount", amount },
                // BsonDateTime always stores UTC; an Unspecified/Local Kind gets silently shifted by
                // the local machine's offset on serialize, so seed data must be explicitly UTC.
                { "Date", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            });
        }

        await collection.InsertManyAsync(documents);
        SeededRows = total;
    }

    public async Task DisposeAsync()
    {
        if (Available)
            await _container.DisposeAsync();
    }
}
