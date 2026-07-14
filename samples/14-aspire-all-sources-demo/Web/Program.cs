using System.Data;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MySqlConnector;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.SourceRegistry;
using NeoReports.Destinations.Local;
using NeoReports.Jobs.DependencyInjection;
using NeoReports.Samples.Shared;
using NeoReports.Sources.MongoDb;
using NeoReports.Sources.MySql;
using NeoReports.Sources.Postgres;
using NeoReports.Sources.Sql;
using NeoReports.UI;
using Npgsql;
using NpgsqlTypes;
using static NeoReports.Core.Building.ReportColumns;
// Import the format entry methods directly so Csv(...) and Xlsx(...) read cleanly and avoid the
// Format class-name collision between the two format packages (ADR D16).
using static NeoReports.Formats.Csv.Format;
using static NeoReports.Formats.Xlsx.Format;
using MongoSource = NeoReports.Sources.MongoDb.Source;
using MySqlSource = NeoReports.Sources.MySql.Source;
using PostgresSource = NeoReports.Sources.Postgres.Source;
using SqlSource = NeoReports.Sources.Sql.Source;

// Sample 14 — the Web half of the combined "all sources" Aspire demo. AppHost.cs provisions all
// four databases (Postgres, MySQL, SQL Server, MongoDB) and injects their connection strings via
// WithReference; this host seeds all four once, registers one working typed report per database,
// registers every one of them as a named source in the Source Registry (D42) so the Builder
// wizard can build brand-new reports against them, and — critically — calls every Add*ConfigSource
// so GET /api/capabilities is never empty and the UI's "Demo mode" banner never appears.
// Run standalone with:
//   dotnet run --project samples/14-aspire-all-sources-demo/Web -- "<pg>" "<mysql>" "<sqlserver>" "<mongo>"

// MongoDB.Driver's GuidSerializer refuses to serialize a Guid at all unless a GuidRepresentation
// is registered explicitly — a process-wide, once-only registration (only the seeding step below
// uses the driver's own typed serializer — reads go through NeoReports' own BsonDocumentMaterializer
// instead, see D44).
BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

var builder = WebApplication.CreateBuilder(args);

string postgresConnectionString = args.Length > 0
    ? args[0]
    : builder.Configuration.GetConnectionString("postgres-db")
        ?? throw new InvalidOperationException(
            "No Postgres connection string. Run via the AppHost (dotnet run --project samples/14-aspire-all-sources-demo/AppHost) " +
            "or pass four connection strings as arguments (postgres, mysql, sqlserver, mongo).");

string mysqlConnectionStringRaw = args.Length > 1
    ? args[1]
    : builder.Configuration.GetConnectionString("mysql-db")
        ?? throw new InvalidOperationException("No MySQL connection string. See the Postgres error above for how to run this sample.");

string sqlServerConnectionString = args.Length > 2
    ? args[2]
    : builder.Configuration.GetConnectionString("sqlserver-db")
        ?? throw new InvalidOperationException("No SQL Server connection string. See the Postgres error above for how to run this sample.");

string mongoConnectionString = args.Length > 3
    ? args[3]
    : builder.Configuration.GetConnectionString("mongodb-db")
        ?? throw new InvalidOperationException("No MongoDB connection string. See the Postgres error above for how to run this sample.");

// GuidFormat=Char36 is required on the MySQL connection string (both here and for the report
// source below) so MySqlConnector reads/writes a CHAR(36) column as a native Guid instead of a
// string — without it, RecordMaterializer's Convert.ChangeType(string, typeof(Guid)) throws,
// since Guid doesn't implement IConvertible.
string mysqlConnectionString = new MySqlConnectionStringBuilder(mysqlConnectionStringRaw) { GuidFormat = MySqlGuidFormat.Char36 }.ConnectionString;

const string Culture = "en-US"; // shared across every currency-formatted column below
const string MongoDatabaseName = "widetransactions";
const string MongoCollectionName = "wide_transactions";
const int SeedRowCount = 15_000; // smaller than the 500,000 single-source samples use — this demo seeds four databases at once
const string LocalDestinationTemplate = "./out/{name}-{date:yyyy-MM-dd}.{ext}"; // shared across all four report registrations below
const string ConnectionStringPropertyKey = "connectionString"; // shared across all four named-source registrations below

// The exact column list every provider-specific SELECT below shares.
const string SelectList =
    "TransactionId, CustomerId, CustomerName, CustomerEmail, CustomerCity, CustomerCountry, " +
    "IsVipCustomer, ProductId, ProductName, ProductCategory, ProductSku, Quantity, UnitPrice, " +
    "DiscountRate, TaxRate, ShippingCost, ProcessingFee, TotalAmount, Currency, PaymentMethod, " +
    "IsRefunded, RefundAmount, IsGift, ShippingCity, ShippingCountry, ShippingPostalCode, CarrierName, " +
    "TrackingNumber, EstimatedDeliveryDays, OrderDate, ShippedDate, DeliveredDate, CreatedAt, UpdatedAt, " +
    "ProcessedAtUtc, SalesRepId, SalesRepName, Region, Channel, Campaign, ReferralCode, DeviceType, " +
    "Browser, OperatingSystem, SessionId, Rating, FeedbackScore, LoyaltyPoints, IsFirstPurchase, " +
    "WarehouseId, Notes";

builder.Services.AddNeoReportsUI();

// One working, ready-to-run typed report per database — click "Run" immediately, no setup needed.
builder.Services.AddReport<WideTransaction>("wide-transactions-postgres", b => b
    .From(PostgresSource.Postgres(
            postgresConnectionString,
            "SELECT " + SelectList + " FROM wide_transactions " +
            "WHERE (@cursor IS NULL OR TransactionId > @cursor::uuid) ORDER BY TransactionId")
        .Keyset<WideTransaction, Guid>(v => v.TransactionId, pageSize: 5000))
    .Columns(WideTransactionColumns())
    .To(Csv(o => o.Delimiter(',')))
    .To(Xlsx())
    .UploadTo(Destination.Local(LocalDestinationTemplate)));

builder.Services.AddReport<WideTransaction>("wide-transactions-mysql", b => b
    .From(MySqlSource.MySql(
            mysqlConnectionString,
            "SELECT " + SelectList + " FROM wide_transactions " +
            "WHERE (@cursor IS NULL OR TransactionId > @cursor) ORDER BY TransactionId")
        .Keyset<WideTransaction, Guid>(v => v.TransactionId, pageSize: 5000))
    .Columns(WideTransactionColumns())
    .To(Csv(o => o.Delimiter(',')))
    .To(Xlsx())
    .UploadTo(Destination.Local(LocalDestinationTemplate)));

builder.Services.AddReport<WideTransaction>("wide-transactions-sqlserver", b => b
    .From(SqlSource.Sql(
            sqlServerConnectionString,
            "SELECT " + SelectList + " FROM wide_transactions " +
            "WHERE (@cursor IS NULL OR TransactionId > @cursor) ORDER BY TransactionId")
        .Keyset<WideTransaction, Guid>(v => v.TransactionId, pageSize: 5000))
    .Columns(WideTransactionColumns())
    .To(Csv(o => o.Delimiter(',')))
    .To(Xlsx())
    .UploadTo(Destination.Local(LocalDestinationTemplate)));

builder.Services.AddReport<WideTransaction>("wide-transactions-mongodb", b => b
    .From(MongoSource.MongoDb(mongoConnectionString, MongoDatabaseName, MongoCollectionName)
        .Keyset<WideTransaction, Guid>(v => v.TransactionId, pageSize: 5000))
    .Columns(WideTransactionColumns())
    .To(Csv(o => o.Delimiter(',')))
    .To(Xlsx())
    .UploadTo(Destination.Local(LocalDestinationTemplate)));

// Registers a config-source provider for every source type this demo has — GET /api/capabilities
// is never empty, so the UI's "Demo mode" fallback never shows and the Builder wizard's "Save"
// step is never disabled.
builder.Services.AddSqlConfigSource();
builder.Services.AddPostgresConfigSource();
builder.Services.AddMySqlConfigSource();
builder.Services.AddMongoDbConfigSource();

// The Source Registry (D42): four named sources, seeded below once the databases exist, so the
// Builder wizard can build brand-new reports against any of them by name instead of pasting a
// raw connection string.
builder.Services.AddSourceRegistry();

// Dynamic (Builder-created) reports and recurring schedules both need somewhere to persist.
builder.Services.AddDynamicReports();
builder.Services.AddScheduling();

builder.Services.AddNeoReportsInMemoryJobs();
builder.Services.AddNeoReportsArtifacts();
builder.Services.AddInMemoryJobEvents(); // ADR D38 — powers the job Timeline/Retries/rate cards

var app = builder.Build();

app.MapNeoReports("/api");

var uiPath = app.Configuration["NeoReports:UIPath"] ?? NeoReportsUIExtensions.DefaultBasePath;
app.UseNeoReportsUI(uiPath);

// Convenience: the sample root redirects into the UI.
app.MapGet("/", () => Results.Redirect(uiPath));

// Start Kestrel BEFORE seeding: Aspire marks this resource's endpoint reachable as soon as the
// process starts, and seeding four databases can take a while — starting the listener first means
// the UI shell loads immediately instead of the endpoint refusing connections until seeding finishes.
await app.StartAsync();

await Task.WhenAll(
    EnsurePostgresSeededAsync(postgresConnectionString),
    EnsureMySqlSeededAsync(mysqlConnectionString),
    EnsureSqlServerSeededAsync(sqlServerConnectionString),
    EnsureMongoSeededAsync(mongoConnectionString));

await RegisterNamedSourcesAsync(app.Services, postgresConnectionString, mysqlConnectionString, sqlServerConnectionString, mongoConnectionString);

await app.WaitForShutdownAsync();

// Every column in the shared 51-column WideTransaction table/collection, reused across all four
// typed report registrations above.
static ColumnDefinition<WideTransaction>[] WideTransactionColumns() => new[]
{
    Col<WideTransaction, Guid>(v => v.TransactionId, "Transaction ID"),
    Col<WideTransaction, long>(v => v.CustomerId, "Customer ID"),
    Col<WideTransaction, string>(v => v.CustomerName, "Customer name"),
    Col<WideTransaction, string>(v => v.CustomerEmail, "Customer email"),
    Col<WideTransaction, string>(v => v.CustomerCity, "Customer city"),
    Col<WideTransaction, string>(v => v.CustomerCountry, "Customer country"),
    Col<WideTransaction, bool>(v => v.IsVipCustomer, "VIP customer"),
    Col<WideTransaction, long>(v => v.ProductId, "Product ID"),
    Col<WideTransaction, string>(v => v.ProductName, "Product name"),
    Col<WideTransaction, string>(v => v.ProductCategory, "Product category"),
    Col<WideTransaction, string>(v => v.ProductSku, "Product SKU"),
    Col<WideTransaction, long>(v => v.Quantity, "Quantity"),
    Col<WideTransaction, decimal>(v => v.UnitPrice, "Unit price", format: "C2", culture: Culture),
    Col<WideTransaction, decimal>(v => v.DiscountRate, "Discount rate", format: "P2"),
    Col<WideTransaction, decimal>(v => v.TaxRate, "Tax rate", format: "P2"),
    Col<WideTransaction, decimal>(v => v.ShippingCost, "Shipping cost", format: "C2", culture: Culture),
    Col<WideTransaction, decimal>(v => v.ProcessingFee, "Processing fee", format: "C2", culture: Culture),
    Col<WideTransaction, decimal>(v => v.TotalAmount, "Total amount", format: "C2", culture: Culture),
    Col<WideTransaction, string>(v => v.Currency, "Currency"),
    Col<WideTransaction, string>(v => v.PaymentMethod, "Payment method"),
    Col<WideTransaction, bool>(v => v.IsRefunded, "Refunded"),
    Col<WideTransaction, decimal>(v => v.RefundAmount, "Refund amount", format: "C2", culture: Culture),
    Col<WideTransaction, bool>(v => v.IsGift, "Gift"),
    Col<WideTransaction, string>(v => v.ShippingCity, "Shipping city"),
    Col<WideTransaction, string>(v => v.ShippingCountry, "Shipping country"),
    Col<WideTransaction, string>(v => v.ShippingPostalCode, "Shipping postal code"),
    Col<WideTransaction, string>(v => v.CarrierName, "Carrier"),
    Col<WideTransaction, string>(v => v.TrackingNumber, "Tracking number"),
    Col<WideTransaction, long>(v => v.EstimatedDeliveryDays, "Est. delivery days"),
    Col<WideTransaction, DateTime>(v => v.OrderDate, "Order date", format: "yyyy-MM-dd"),
    Col<WideTransaction, DateTime>(v => v.ShippedDate, "Shipped date", format: "yyyy-MM-dd"),
    Col<WideTransaction, DateTime>(v => v.DeliveredDate, "Delivered date", format: "yyyy-MM-dd"),
    Col<WideTransaction, DateTime>(v => v.CreatedAt, "Created at", format: "yyyy-MM-dd HH:mm:ss"),
    Col<WideTransaction, DateTime>(v => v.UpdatedAt, "Updated at", format: "yyyy-MM-dd HH:mm:ss"),
    Col<WideTransaction, DateTime>(v => v.ProcessedAtUtc, "Processed at (UTC)", format: "yyyy-MM-dd HH:mm:ss"),
    Col<WideTransaction, long>(v => v.SalesRepId, "Sales rep ID"),
    Col<WideTransaction, string>(v => v.SalesRepName, "Sales rep name"),
    Col<WideTransaction, string>(v => v.Region, "Region"),
    Col<WideTransaction, string>(v => v.Channel, "Channel"),
    Col<WideTransaction, string>(v => v.Campaign, "Campaign"),
    Col<WideTransaction, string>(v => v.ReferralCode, "Referral code"),
    Col<WideTransaction, string>(v => v.DeviceType, "Device type"),
    Col<WideTransaction, string>(v => v.Browser, "Browser"),
    Col<WideTransaction, string>(v => v.OperatingSystem, "Operating system"),
    Col<WideTransaction, Guid>(v => v.SessionId, "Session ID"),
    Col<WideTransaction, long>(v => v.Rating, "Rating"),
    Col<WideTransaction, decimal>(v => v.FeedbackScore, "Feedback score", format: "N2"),
    Col<WideTransaction, long>(v => v.LoyaltyPoints, "Loyalty points"),
    Col<WideTransaction, bool>(v => v.IsFirstPurchase, "First purchase"),
    Col<WideTransaction, long>(v => v.WarehouseId, "Warehouse ID"),
    Col<WideTransaction, string>(v => v.Notes, "Notes"),
};

// Registers the four Aspire-provisioned databases as named sources (ADR D42) so the Builder
// wizard can reference them by name instead of a pasted connection string. Runs after seeding so
// a health check performed right after creation immediately succeeds. Properties carry only the
// connection (+database/collection for Mongo) — sql/key/pageSize are report-local overlays the
// Builder supplies per report, merged in at resolve time (RefBatchSource), not part of the source.
static async Task RegisterNamedSourcesAsync(
    IServiceProvider services, string pgConnectionString, string mysqlConnectionString, string sqlServerConnectionString, string mongoConnectionString)
{
    var registry = services.GetRequiredService<ISourceRegistry>();

    await registry.SaveAsync(new SourceDefinition(
        "postgres-demo", "postgres",
        new Dictionary<string, object?> { [ConnectionStringPropertyKey] = pgConnectionString },
        "Aspire-provisioned PostgreSQL — wide_transactions table"), CancellationToken.None);

    await registry.SaveAsync(new SourceDefinition(
        "mysql-demo", "mysql",
        new Dictionary<string, object?> { [ConnectionStringPropertyKey] = mysqlConnectionString },
        "Aspire-provisioned MySQL — wide_transactions table"), CancellationToken.None);

    await registry.SaveAsync(new SourceDefinition(
        "sqlserver-demo", "sql",
        new Dictionary<string, object?> { [ConnectionStringPropertyKey] = sqlServerConnectionString },
        "Aspire-provisioned SQL Server — wide_transactions table"), CancellationToken.None);

    await registry.SaveAsync(new SourceDefinition(
        "mongodb-demo", "mongodb",
        new Dictionary<string, object?>
        {
            [ConnectionStringPropertyKey] = mongoConnectionString,
            ["database"] = MongoDatabaseName,
            ["collection"] = MongoCollectionName,
        },
        "Aspire-provisioned MongoDB — wide_transactions collection"), CancellationToken.None);
}

static async Task EnsurePostgresSeededAsync(string connectionString)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using (var createTable = connection.CreateCommand())
    {
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS wide_transactions (
                TransactionId UUID PRIMARY KEY,
                CustomerId BIGINT NOT NULL,
                CustomerName TEXT NOT NULL,
                CustomerEmail TEXT NOT NULL,
                CustomerCity TEXT NOT NULL,
                CustomerCountry TEXT NOT NULL,
                IsVipCustomer BOOLEAN NOT NULL,
                ProductId BIGINT NOT NULL,
                ProductName TEXT NOT NULL,
                ProductCategory TEXT NOT NULL,
                ProductSku TEXT NOT NULL,
                Quantity BIGINT NOT NULL,
                UnitPrice NUMERIC(18,4) NOT NULL,
                DiscountRate NUMERIC(18,4) NOT NULL,
                TaxRate NUMERIC(18,4) NOT NULL,
                ShippingCost NUMERIC(18,4) NOT NULL,
                ProcessingFee NUMERIC(18,4) NOT NULL,
                TotalAmount NUMERIC(18,4) NOT NULL,
                Currency TEXT NOT NULL,
                PaymentMethod TEXT NOT NULL,
                IsRefunded BOOLEAN NOT NULL,
                RefundAmount NUMERIC(18,4) NOT NULL,
                IsGift BOOLEAN NOT NULL,
                ShippingCity TEXT NOT NULL,
                ShippingCountry TEXT NOT NULL,
                ShippingPostalCode TEXT NOT NULL,
                CarrierName TEXT NOT NULL,
                TrackingNumber TEXT NOT NULL,
                EstimatedDeliveryDays BIGINT NOT NULL,
                OrderDate TIMESTAMP NOT NULL,
                ShippedDate TIMESTAMP NOT NULL,
                DeliveredDate TIMESTAMP NOT NULL,
                CreatedAt TIMESTAMP NOT NULL,
                UpdatedAt TIMESTAMP NOT NULL,
                ProcessedAtUtc TIMESTAMP NOT NULL,
                SalesRepId BIGINT NOT NULL,
                SalesRepName TEXT NOT NULL,
                Region TEXT NOT NULL,
                Channel TEXT NOT NULL,
                Campaign TEXT NOT NULL,
                ReferralCode TEXT NOT NULL,
                DeviceType TEXT NOT NULL,
                Browser TEXT NOT NULL,
                OperatingSystem TEXT NOT NULL,
                SessionId UUID NOT NULL,
                Rating BIGINT NOT NULL,
                FeedbackScore NUMERIC(18,4) NOT NULL,
                LoyaltyPoints BIGINT NOT NULL,
                IsFirstPurchase BOOLEAN NOT NULL,
                WarehouseId BIGINT NOT NULL,
                Notes TEXT NOT NULL
            );
            """;
        await createTable.ExecuteNonQueryAsync();
    }

    await using (var countCommand = connection.CreateCommand())
    {
        countCommand.CommandText = "SELECT COUNT(*) FROM wide_transactions";
        var existingRows = (long)(await countCommand.ExecuteScalarAsync() ?? 0L);
        if (existingRows > 0)
        {
            Console.WriteLine($"[postgres] wide_transactions already has {existingRows:N0} rows — skipping seed.");
            return;
        }
    }

    Console.WriteLine($"[postgres] Seeding wide_transactions with {SeedRowCount:N0} rows...");
    var started = DateTime.UtcNow;

    await using var writer = await connection.BeginBinaryImportAsync(
        "COPY wide_transactions (" +
        "TransactionId, CustomerId, CustomerName, CustomerEmail, CustomerCity, CustomerCountry, IsVipCustomer, " +
        "ProductId, ProductName, ProductCategory, ProductSku, Quantity, UnitPrice, DiscountRate, TaxRate, " +
        "ShippingCost, ProcessingFee, TotalAmount, Currency, PaymentMethod, IsRefunded, RefundAmount, IsGift, " +
        "ShippingCity, ShippingCountry, ShippingPostalCode, CarrierName, TrackingNumber, EstimatedDeliveryDays, " +
        "OrderDate, ShippedDate, DeliveredDate, CreatedAt, UpdatedAt, ProcessedAtUtc, SalesRepId, SalesRepName, " +
        "Region, Channel, Campaign, ReferralCode, DeviceType, Browser, OperatingSystem, SessionId, Rating, " +
        "FeedbackScore, LoyaltyPoints, IsFirstPurchase, WarehouseId, Notes" +
        ") FROM STDIN (FORMAT BINARY)");

    foreach (var row in WideTransactionGenerator.Generate(SeedRowCount))
    {
        await writer.StartRowAsync();
        await writer.WriteAsync(row.TransactionId, NpgsqlDbType.Uuid);
        await writer.WriteAsync(row.CustomerId, NpgsqlDbType.Bigint);
        await writer.WriteAsync(row.CustomerName, NpgsqlDbType.Text);
        await writer.WriteAsync(row.CustomerEmail, NpgsqlDbType.Text);
        await writer.WriteAsync(row.CustomerCity, NpgsqlDbType.Text);
        await writer.WriteAsync(row.CustomerCountry, NpgsqlDbType.Text);
        await writer.WriteAsync(row.IsVipCustomer, NpgsqlDbType.Boolean);
        await writer.WriteAsync(row.ProductId, NpgsqlDbType.Bigint);
        await writer.WriteAsync(row.ProductName, NpgsqlDbType.Text);
        await writer.WriteAsync(row.ProductCategory, NpgsqlDbType.Text);
        await writer.WriteAsync(row.ProductSku, NpgsqlDbType.Text);
        await writer.WriteAsync(row.Quantity, NpgsqlDbType.Bigint);
        await writer.WriteAsync(row.UnitPrice, NpgsqlDbType.Numeric);
        await writer.WriteAsync(row.DiscountRate, NpgsqlDbType.Numeric);
        await writer.WriteAsync(row.TaxRate, NpgsqlDbType.Numeric);
        await writer.WriteAsync(row.ShippingCost, NpgsqlDbType.Numeric);
        await writer.WriteAsync(row.ProcessingFee, NpgsqlDbType.Numeric);
        await writer.WriteAsync(row.TotalAmount, NpgsqlDbType.Numeric);
        await writer.WriteAsync(row.Currency, NpgsqlDbType.Text);
        await writer.WriteAsync(row.PaymentMethod, NpgsqlDbType.Text);
        await writer.WriteAsync(row.IsRefunded, NpgsqlDbType.Boolean);
        await writer.WriteAsync(row.RefundAmount, NpgsqlDbType.Numeric);
        await writer.WriteAsync(row.IsGift, NpgsqlDbType.Boolean);
        await writer.WriteAsync(row.ShippingCity, NpgsqlDbType.Text);
        await writer.WriteAsync(row.ShippingCountry, NpgsqlDbType.Text);
        await writer.WriteAsync(row.ShippingPostalCode, NpgsqlDbType.Text);
        await writer.WriteAsync(row.CarrierName, NpgsqlDbType.Text);
        await writer.WriteAsync(row.TrackingNumber, NpgsqlDbType.Text);
        await writer.WriteAsync(row.EstimatedDeliveryDays, NpgsqlDbType.Bigint);
        await writer.WriteAsync(AsUnspecified(row.OrderDate), NpgsqlDbType.Timestamp);
        await writer.WriteAsync(AsUnspecified(row.ShippedDate), NpgsqlDbType.Timestamp);
        await writer.WriteAsync(AsUnspecified(row.DeliveredDate), NpgsqlDbType.Timestamp);
        await writer.WriteAsync(AsUnspecified(row.CreatedAt), NpgsqlDbType.Timestamp);
        await writer.WriteAsync(AsUnspecified(row.UpdatedAt), NpgsqlDbType.Timestamp);
        await writer.WriteAsync(AsUnspecified(row.ProcessedAtUtc), NpgsqlDbType.Timestamp);
        await writer.WriteAsync(row.SalesRepId, NpgsqlDbType.Bigint);
        await writer.WriteAsync(row.SalesRepName, NpgsqlDbType.Text);
        await writer.WriteAsync(row.Region, NpgsqlDbType.Text);
        await writer.WriteAsync(row.Channel, NpgsqlDbType.Text);
        await writer.WriteAsync(row.Campaign, NpgsqlDbType.Text);
        await writer.WriteAsync(row.ReferralCode, NpgsqlDbType.Text);
        await writer.WriteAsync(row.DeviceType, NpgsqlDbType.Text);
        await writer.WriteAsync(row.Browser, NpgsqlDbType.Text);
        await writer.WriteAsync(row.OperatingSystem, NpgsqlDbType.Text);
        await writer.WriteAsync(row.SessionId, NpgsqlDbType.Uuid);
        await writer.WriteAsync(row.Rating, NpgsqlDbType.Bigint);
        await writer.WriteAsync(row.FeedbackScore, NpgsqlDbType.Numeric);
        await writer.WriteAsync(row.LoyaltyPoints, NpgsqlDbType.Bigint);
        await writer.WriteAsync(row.IsFirstPurchase, NpgsqlDbType.Boolean);
        await writer.WriteAsync(row.WarehouseId, NpgsqlDbType.Bigint);
        await writer.WriteAsync(row.Notes, NpgsqlDbType.Text);
    }

    await writer.CompleteAsync();
    Console.WriteLine($"[postgres] Seeded in {(DateTime.UtcNow - started).TotalSeconds:N1}s.");
}

// Postgres's TIMESTAMP (without time zone) rejects a DateTimeKind.Utc value outright — the
// generator's rows are all UTC-based, but a naive column has no time-zone concept to validate
// against, so the Kind tag is simply dropped rather than the value being reinterpreted.
static DateTime AsUnspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

static async Task EnsureMySqlSeededAsync(string connectionString)
{
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();

    await using (var createTable = connection.CreateCommand())
    {
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS wide_transactions (
                TransactionId CHAR(36) PRIMARY KEY,
                CustomerId BIGINT NOT NULL,
                CustomerName TEXT NOT NULL,
                CustomerEmail TEXT NOT NULL,
                CustomerCity TEXT NOT NULL,
                CustomerCountry TEXT NOT NULL,
                IsVipCustomer BOOLEAN NOT NULL,
                ProductId BIGINT NOT NULL,
                ProductName TEXT NOT NULL,
                ProductCategory TEXT NOT NULL,
                ProductSku TEXT NOT NULL,
                Quantity BIGINT NOT NULL,
                UnitPrice DECIMAL(18,4) NOT NULL,
                DiscountRate DECIMAL(18,4) NOT NULL,
                TaxRate DECIMAL(18,4) NOT NULL,
                ShippingCost DECIMAL(18,4) NOT NULL,
                ProcessingFee DECIMAL(18,4) NOT NULL,
                TotalAmount DECIMAL(18,4) NOT NULL,
                Currency TEXT NOT NULL,
                PaymentMethod TEXT NOT NULL,
                IsRefunded BOOLEAN NOT NULL,
                RefundAmount DECIMAL(18,4) NOT NULL,
                IsGift BOOLEAN NOT NULL,
                ShippingCity TEXT NOT NULL,
                ShippingCountry TEXT NOT NULL,
                ShippingPostalCode TEXT NOT NULL,
                CarrierName TEXT NOT NULL,
                TrackingNumber TEXT NOT NULL,
                EstimatedDeliveryDays BIGINT NOT NULL,
                OrderDate DATETIME NOT NULL,
                ShippedDate DATETIME NOT NULL,
                DeliveredDate DATETIME NOT NULL,
                CreatedAt DATETIME NOT NULL,
                UpdatedAt DATETIME NOT NULL,
                ProcessedAtUtc DATETIME NOT NULL,
                SalesRepId BIGINT NOT NULL,
                SalesRepName TEXT NOT NULL,
                Region TEXT NOT NULL,
                Channel TEXT NOT NULL,
                Campaign TEXT NOT NULL,
                ReferralCode TEXT NOT NULL,
                DeviceType TEXT NOT NULL,
                Browser TEXT NOT NULL,
                OperatingSystem TEXT NOT NULL,
                SessionId CHAR(36) NOT NULL,
                Rating BIGINT NOT NULL,
                FeedbackScore DECIMAL(18,4) NOT NULL,
                LoyaltyPoints BIGINT NOT NULL,
                IsFirstPurchase BOOLEAN NOT NULL,
                WarehouseId BIGINT NOT NULL,
                Notes TEXT NOT NULL
            );
            """;
        await createTable.ExecuteNonQueryAsync();
    }

    await using (var countCommand = connection.CreateCommand())
    {
        countCommand.CommandText = "SELECT COUNT(*) FROM wide_transactions";
        var existingRows = Convert.ToInt64(await countCommand.ExecuteScalarAsync());
        if (existingRows > 0)
        {
            Console.WriteLine($"[mysql] wide_transactions already has {existingRows:N0} rows — skipping seed.");
            return;
        }
    }

    Console.WriteLine($"[mysql] Seeding wide_transactions with {SeedRowCount:N0} rows...");
    var started = DateTime.UtcNow;

    const int batchSize = 200;
    var batch = new List<WideTransaction>(batchSize);

    foreach (var row in WideTransactionGenerator.Generate(SeedRowCount))
    {
        batch.Add(row);
        if (batch.Count == batchSize)
        {
            await InsertMySqlBatchAsync(connection, batch);
            batch.Clear();
        }
    }

    if (batch.Count > 0)
        await InsertMySqlBatchAsync(connection, batch);

    Console.WriteLine($"[mysql] Seeded in {(DateTime.UtcNow - started).TotalSeconds:N1}s.");
}

static async Task InsertMySqlBatchAsync(MySqlConnection connection, List<WideTransaction> rows)
{
    var sql = new System.Text.StringBuilder(
        "INSERT INTO wide_transactions (" +
        "TransactionId, CustomerId, CustomerName, CustomerEmail, CustomerCity, CustomerCountry, IsVipCustomer, " +
        "ProductId, ProductName, ProductCategory, ProductSku, Quantity, UnitPrice, DiscountRate, TaxRate, " +
        "ShippingCost, ProcessingFee, TotalAmount, Currency, PaymentMethod, IsRefunded, RefundAmount, IsGift, " +
        "ShippingCity, ShippingCountry, ShippingPostalCode, CarrierName, TrackingNumber, EstimatedDeliveryDays, " +
        "OrderDate, ShippedDate, DeliveredDate, CreatedAt, UpdatedAt, ProcessedAtUtc, SalesRepId, SalesRepName, " +
        "Region, Channel, Campaign, ReferralCode, DeviceType, Browser, OperatingSystem, SessionId, Rating, " +
        "FeedbackScore, LoyaltyPoints, IsFirstPurchase, WarehouseId, Notes" +
        ") VALUES ");

    var values = new List<object>(rows.Count * 51);
    for (var r = 0; r < rows.Count; r++)
    {
        if (r > 0) sql.Append(',');
        sql.Append('(');
        for (var c = 0; c < 51; c++)
        {
            if (c > 0) sql.Append(',');
            sql.Append('@').Append('p').Append(r).Append('_').Append(c);
        }
        sql.Append(')');

        var row = rows[r];
        values.AddRange(new object[]
        {
            row.TransactionId, row.CustomerId, row.CustomerName, row.CustomerEmail, row.CustomerCity,
            row.CustomerCountry, row.IsVipCustomer, row.ProductId, row.ProductName, row.ProductCategory,
            row.ProductSku, row.Quantity, row.UnitPrice, row.DiscountRate, row.TaxRate, row.ShippingCost,
            row.ProcessingFee, row.TotalAmount, row.Currency, row.PaymentMethod, row.IsRefunded,
            row.RefundAmount, row.IsGift, row.ShippingCity, row.ShippingCountry, row.ShippingPostalCode,
            row.CarrierName, row.TrackingNumber, row.EstimatedDeliveryDays, row.OrderDate, row.ShippedDate,
            row.DeliveredDate, row.CreatedAt, row.UpdatedAt, row.ProcessedAtUtc, row.SalesRepId,
            row.SalesRepName, row.Region, row.Channel, row.Campaign, row.ReferralCode, row.DeviceType,
            row.Browser, row.OperatingSystem, row.SessionId, row.Rating, row.FeedbackScore, row.LoyaltyPoints,
            row.IsFirstPurchase, row.WarehouseId, row.Notes,
        });
    }

    await using var command = connection.CreateCommand();
    command.CommandText = sql.ToString();
    for (var i = 0; i < values.Count; i++)
        command.Parameters.AddWithValue($"@p{i / 51}_{i % 51}", values[i]);

    await command.ExecuteNonQueryAsync();
}

static async Task EnsureSqlServerSeededAsync(string connectionString)
{
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    await using (var createTable = connection.CreateCommand())
    {
        createTable.CommandText = """
            IF OBJECT_ID('wide_transactions', 'U') IS NULL
            CREATE TABLE wide_transactions (
                TransactionId UNIQUEIDENTIFIER PRIMARY KEY,
                CustomerId BIGINT NOT NULL,
                CustomerName NVARCHAR(200) NOT NULL,
                CustomerEmail NVARCHAR(200) NOT NULL,
                CustomerCity NVARCHAR(100) NOT NULL,
                CustomerCountry NVARCHAR(10) NOT NULL,
                IsVipCustomer BIT NOT NULL,
                ProductId BIGINT NOT NULL,
                ProductName NVARCHAR(200) NOT NULL,
                ProductCategory NVARCHAR(100) NOT NULL,
                ProductSku NVARCHAR(50) NOT NULL,
                Quantity BIGINT NOT NULL,
                UnitPrice DECIMAL(18,4) NOT NULL,
                DiscountRate DECIMAL(18,4) NOT NULL,
                TaxRate DECIMAL(18,4) NOT NULL,
                ShippingCost DECIMAL(18,4) NOT NULL,
                ProcessingFee DECIMAL(18,4) NOT NULL,
                TotalAmount DECIMAL(18,4) NOT NULL,
                Currency NVARCHAR(10) NOT NULL,
                PaymentMethod NVARCHAR(50) NOT NULL,
                IsRefunded BIT NOT NULL,
                RefundAmount DECIMAL(18,4) NOT NULL,
                IsGift BIT NOT NULL,
                ShippingCity NVARCHAR(100) NOT NULL,
                ShippingCountry NVARCHAR(10) NOT NULL,
                ShippingPostalCode NVARCHAR(20) NOT NULL,
                CarrierName NVARCHAR(50) NOT NULL,
                TrackingNumber NVARCHAR(50) NOT NULL,
                EstimatedDeliveryDays BIGINT NOT NULL,
                OrderDate DATETIME2 NOT NULL,
                ShippedDate DATETIME2 NOT NULL,
                DeliveredDate DATETIME2 NOT NULL,
                CreatedAt DATETIME2 NOT NULL,
                UpdatedAt DATETIME2 NOT NULL,
                ProcessedAtUtc DATETIME2 NOT NULL,
                SalesRepId BIGINT NOT NULL,
                SalesRepName NVARCHAR(200) NOT NULL,
                Region NVARCHAR(50) NOT NULL,
                Channel NVARCHAR(50) NOT NULL,
                Campaign NVARCHAR(50) NOT NULL,
                ReferralCode NVARCHAR(50) NOT NULL,
                DeviceType NVARCHAR(50) NOT NULL,
                Browser NVARCHAR(50) NOT NULL,
                OperatingSystem NVARCHAR(50) NOT NULL,
                SessionId UNIQUEIDENTIFIER NOT NULL,
                Rating BIGINT NOT NULL,
                FeedbackScore DECIMAL(18,4) NOT NULL,
                LoyaltyPoints BIGINT NOT NULL,
                IsFirstPurchase BIT NOT NULL,
                WarehouseId BIGINT NOT NULL,
                Notes NVARCHAR(500) NOT NULL
            );
            """;
        await createTable.ExecuteNonQueryAsync();
    }

    await using (var countCommand = connection.CreateCommand())
    {
        countCommand.CommandText = "SELECT COUNT(*) FROM wide_transactions";
        var existingRows = (int)(await countCommand.ExecuteScalarAsync() ?? 0);
        if (existingRows > 0)
        {
            Console.WriteLine($"[sqlserver] wide_transactions already has {existingRows:N0} rows — skipping seed.");
            return;
        }
    }

    Console.WriteLine($"[sqlserver] Seeding wide_transactions with {SeedRowCount:N0} rows...");
    var started = DateTime.UtcNow;

    var table = BuildEmptySqlServerTable();
    using var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = "wide_transactions", BatchSize = 5000 };
    foreach (DataColumn column in table.Columns)
        bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);

    const int flushEvery = 5000;
    var buffered = 0;
    foreach (var row in WideTransactionGenerator.Generate(SeedRowCount))
    {
        AddSqlServerRow(table, row);
        buffered++;
        if (buffered == flushEvery)
        {
            await bulkCopy.WriteToServerAsync(table);
            table.Rows.Clear();
            buffered = 0;
        }
    }

    if (buffered > 0)
        await bulkCopy.WriteToServerAsync(table);

    Console.WriteLine($"[sqlserver] Seeded in {(DateTime.UtcNow - started).TotalSeconds:N1}s.");
}

static DataTable BuildEmptySqlServerTable()
{
    var table = new DataTable();
    table.Columns.Add("TransactionId", typeof(Guid));
    table.Columns.Add("CustomerId", typeof(long));
    table.Columns.Add("CustomerName", typeof(string));
    table.Columns.Add("CustomerEmail", typeof(string));
    table.Columns.Add("CustomerCity", typeof(string));
    table.Columns.Add("CustomerCountry", typeof(string));
    table.Columns.Add("IsVipCustomer", typeof(bool));
    table.Columns.Add("ProductId", typeof(long));
    table.Columns.Add("ProductName", typeof(string));
    table.Columns.Add("ProductCategory", typeof(string));
    table.Columns.Add("ProductSku", typeof(string));
    table.Columns.Add("Quantity", typeof(long));
    table.Columns.Add("UnitPrice", typeof(decimal));
    table.Columns.Add("DiscountRate", typeof(decimal));
    table.Columns.Add("TaxRate", typeof(decimal));
    table.Columns.Add("ShippingCost", typeof(decimal));
    table.Columns.Add("ProcessingFee", typeof(decimal));
    table.Columns.Add("TotalAmount", typeof(decimal));
    table.Columns.Add("Currency", typeof(string));
    table.Columns.Add("PaymentMethod", typeof(string));
    table.Columns.Add("IsRefunded", typeof(bool));
    table.Columns.Add("RefundAmount", typeof(decimal));
    table.Columns.Add("IsGift", typeof(bool));
    table.Columns.Add("ShippingCity", typeof(string));
    table.Columns.Add("ShippingCountry", typeof(string));
    table.Columns.Add("ShippingPostalCode", typeof(string));
    table.Columns.Add("CarrierName", typeof(string));
    table.Columns.Add("TrackingNumber", typeof(string));
    table.Columns.Add("EstimatedDeliveryDays", typeof(long));
    table.Columns.Add("OrderDate", typeof(DateTime));
    table.Columns.Add("ShippedDate", typeof(DateTime));
    table.Columns.Add("DeliveredDate", typeof(DateTime));
    table.Columns.Add("CreatedAt", typeof(DateTime));
    table.Columns.Add("UpdatedAt", typeof(DateTime));
    table.Columns.Add("ProcessedAtUtc", typeof(DateTime));
    table.Columns.Add("SalesRepId", typeof(long));
    table.Columns.Add("SalesRepName", typeof(string));
    table.Columns.Add("Region", typeof(string));
    table.Columns.Add("Channel", typeof(string));
    table.Columns.Add("Campaign", typeof(string));
    table.Columns.Add("ReferralCode", typeof(string));
    table.Columns.Add("DeviceType", typeof(string));
    table.Columns.Add("Browser", typeof(string));
    table.Columns.Add("OperatingSystem", typeof(string));
    table.Columns.Add("SessionId", typeof(Guid));
    table.Columns.Add("Rating", typeof(long));
    table.Columns.Add("FeedbackScore", typeof(decimal));
    table.Columns.Add("LoyaltyPoints", typeof(long));
    table.Columns.Add("IsFirstPurchase", typeof(bool));
    table.Columns.Add("WarehouseId", typeof(long));
    table.Columns.Add("Notes", typeof(string));
    return table;
}

static void AddSqlServerRow(DataTable table, WideTransaction row) => table.Rows.Add(
    row.TransactionId, row.CustomerId, row.CustomerName, row.CustomerEmail, row.CustomerCity,
    row.CustomerCountry, row.IsVipCustomer, row.ProductId, row.ProductName, row.ProductCategory,
    row.ProductSku, row.Quantity, row.UnitPrice, row.DiscountRate, row.TaxRate, row.ShippingCost,
    row.ProcessingFee, row.TotalAmount, row.Currency, row.PaymentMethod, row.IsRefunded,
    row.RefundAmount, row.IsGift, row.ShippingCity, row.ShippingCountry, row.ShippingPostalCode,
    row.CarrierName, row.TrackingNumber, row.EstimatedDeliveryDays, row.OrderDate, row.ShippedDate,
    row.DeliveredDate, row.CreatedAt, row.UpdatedAt, row.ProcessedAtUtc, row.SalesRepId,
    row.SalesRepName, row.Region, row.Channel, row.Campaign, row.ReferralCode, row.DeviceType,
    row.Browser, row.OperatingSystem, row.SessionId, row.Rating, row.FeedbackScore, row.LoyaltyPoints,
    row.IsFirstPurchase, row.WarehouseId, row.Notes);

static async Task EnsureMongoSeededAsync(string connectionString)
{
    using var client = new MongoClient(connectionString);
    var database = client.GetDatabase(MongoDatabaseName);
    var collection = database.GetCollection<WideTransaction>(MongoCollectionName);

    var existingRows = await collection.EstimatedDocumentCountAsync();
    if (existingRows > 0)
    {
        Console.WriteLine($"[mongodb] {MongoCollectionName} already has {existingRows:N0} documents — skipping seed.");
        return;
    }

    Console.WriteLine($"[mongodb] Seeding {MongoCollectionName} with {SeedRowCount:N0} documents...");
    var started = DateTime.UtcNow;

    const int batchSize = 5000;
    var batch = new List<WideTransaction>(batchSize);

    foreach (var row in WideTransactionGenerator.Generate(SeedRowCount))
    {
        batch.Add(row);
        if (batch.Count == batchSize)
        {
            await collection.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false });
            batch.Clear();
        }
    }

    if (batch.Count > 0)
        await collection.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false });

    await collection.Indexes.CreateOneAsync(new CreateIndexModel<WideTransaction>(
        Builders<WideTransaction>.IndexKeys.Ascending(v => v.TransactionId)));

    Console.WriteLine($"[mongodb] Seeded in {(DateTime.UtcNow - started).TotalSeconds:N1}s.");
}
