using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Samples.Shared;
using NeoReports.Sources.MongoDb;
using static NeoReports.Core.Building.ReportColumns;
// Import the format entry methods directly so Csv(...) and Xlsx(...) read cleanly and avoid the
// Format class-name collision between the two format packages (ADR D16).
using static NeoReports.Formats.Csv.Format;
using static NeoReports.Formats.Xlsx.Format;

// Sample 13 — the ReportRunner half of the MongoDB Aspire sample. Started by AppHost.cs with the
// "widetransactions" connection string injected via WithReference; run standalone with:
//   dotnet run --project samples/13-aspire-mongodb-wide/ReportRunner -- "<connection-string>"

const string DatabaseName = "widetransactions";
const string CollectionName = "wide_transactions";

// MongoDB.Driver's GuidSerializer refuses to serialize a Guid at all unless a GuidRepresentation
// is registered explicitly — recent driver versions dropped the old ambiguous-by-default behavior.
// Standard is the cross-driver-portable BSON binary subtype 4 encoding; this is a process-wide,
// once-only registration (only the seeding step below uses the driver's own typed serializer —
// reads go through NeoReports' own BsonDocumentMaterializer instead, see D44).
BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

string connectionString = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("ConnectionStrings__widetransactions")
        ?? throw new InvalidOperationException(
            "No connection string. Run via the AppHost (dotnet run --project samples/13-aspire-mongodb-wide/AppHost) " +
            "or pass one as the first argument.");

await EnsureSeededAsync(connectionString);

const string Culture = "en-US"; // shared across every currency-formatted column below

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddReport<WideTransaction>("wide-transactions", b => b
    .From(Source.MongoDb(connectionString, DatabaseName, CollectionName)
        .Keyset<WideTransaction, Guid>(v => v.TransactionId, pageSize: 5000))
    .Columns(
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
        Col<WideTransaction, string>(v => v.Notes, "Notes"))
    .To(Csv(o => o.Delimiter(',')))
    .To(Xlsx())
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}")));

var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IReportRunner>();

Console.WriteLine("Running wide-transactions report against MongoDB...");
var result = await runner.RunAsync("wide-transactions");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.RemotePath} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;

static async Task EnsureSeededAsync(string connectionString)
{
    using var client = new MongoClient(connectionString);
    var database = client.GetDatabase(DatabaseName);
    var collection = database.GetCollection<WideTransaction>(CollectionName);

    var existingRows = await collection.EstimatedDocumentCountAsync();
    if (existingRows > 0)
    {
        Console.WriteLine($"{CollectionName} already has {existingRows:N0} documents — skipping seed.");
        return;
    }

    Console.WriteLine($"Seeding {CollectionName} with {WideTransactionGenerator.DefaultRowCount:N0} documents...");
    var started = DateTime.UtcNow;

    const int batchSize = 5000;
    var batch = new List<WideTransaction>(batchSize);

    foreach (var row in WideTransactionGenerator.Generate())
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

    Console.WriteLine($"Seeded in {(DateTime.UtcNow - started).TotalSeconds:N1}s.");
}
