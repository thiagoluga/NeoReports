using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Destinations.Local;
using NeoReports.Samples.Shared;
using NeoReports.Sources.Postgres;
using Npgsql;
using NpgsqlTypes;
using static NeoReports.Core.Building.ReportColumns;
// Import the format entry methods directly so Csv(...) and Xlsx(...) read cleanly and avoid the
// Format class-name collision between the two format packages (ADR D16).
using static NeoReports.Formats.Csv.Format;
using static NeoReports.Formats.Xlsx.Format;

// Sample 10 — the ReportRunner half of the Postgres Aspire sample. Started by AppHost.cs with the
// "widetransactions" connection string injected via WithReference; run standalone with:
//   dotnet run --project samples/10-aspire-postgres-wide/ReportRunner -- "<connection-string>"

string connectionString = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("ConnectionStrings__widetransactions")
        ?? throw new InvalidOperationException(
            "No connection string. Run via the AppHost (dotnet run --project samples/10-aspire-postgres-wide/AppHost) " +
            "or pass one as the first argument.");

await EnsureSeededAsync(connectionString);

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

services.AddReport<WideTransaction>("wide-transactions", b => b
    .From(Source.Postgres(
            connectionString,
            "SELECT TransactionId, CustomerId, CustomerName, CustomerEmail, CustomerCity, CustomerCountry, " +
            "IsVipCustomer, ProductId, ProductName, ProductCategory, ProductSku, Quantity, UnitPrice, " +
            "DiscountRate, TaxRate, ShippingCost, ProcessingFee, TotalAmount, Currency, PaymentMethod, " +
            "IsRefunded, RefundAmount, IsGift, ShippingCity, ShippingCountry, ShippingPostalCode, CarrierName, " +
            "TrackingNumber, EstimatedDeliveryDays, OrderDate, ShippedDate, DeliveredDate, CreatedAt, UpdatedAt, " +
            "ProcessedAtUtc, SalesRepId, SalesRepName, Region, Channel, Campaign, ReferralCode, DeviceType, " +
            "Browser, OperatingSystem, SessionId, Rating, FeedbackScore, LoyaltyPoints, IsFirstPurchase, " +
            "WarehouseId, Notes FROM wide_transactions " +
            "WHERE (@cursor IS NULL OR TransactionId > @cursor::uuid) ORDER BY TransactionId")
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
        Col<WideTransaction, decimal>(v => v.UnitPrice, "Unit price", format: "C2", culture: "en-US"),
        Col<WideTransaction, decimal>(v => v.DiscountRate, "Discount rate", format: "P2"),
        Col<WideTransaction, decimal>(v => v.TaxRate, "Tax rate", format: "P2"),
        Col<WideTransaction, decimal>(v => v.ShippingCost, "Shipping cost", format: "C2", culture: "en-US"),
        Col<WideTransaction, decimal>(v => v.ProcessingFee, "Processing fee", format: "C2", culture: "en-US"),
        Col<WideTransaction, decimal>(v => v.TotalAmount, "Total amount", format: "C2", culture: "en-US"),
        Col<WideTransaction, string>(v => v.Currency, "Currency"),
        Col<WideTransaction, string>(v => v.PaymentMethod, "Payment method"),
        Col<WideTransaction, bool>(v => v.IsRefunded, "Refunded"),
        Col<WideTransaction, decimal>(v => v.RefundAmount, "Refund amount", format: "C2", culture: "en-US"),
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

Console.WriteLine("Running wide-transactions report against PostgreSQL...");
var result = await runner.RunAsync("wide-transactions");

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Records read/written: {result.Stats.RecordsRead}/{result.Stats.RecordsWritten}");
foreach (var upload in result.Uploads)
    Console.WriteLine($"Uploaded: {upload.RemotePath} (success={upload.Success})");

return result.Status == ReportRunStatus.Failed ? 1 : 0;

static async Task EnsureSeededAsync(string connectionString)
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
            Console.WriteLine($"wide_transactions already has {existingRows:N0} rows — skipping seed.");
            return;
        }
    }

    Console.WriteLine($"Seeding wide_transactions with {WideTransactionGenerator.DefaultRowCount:N0} rows...");
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

    foreach (var row in WideTransactionGenerator.Generate())
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
    Console.WriteLine($"Seeded in {(DateTime.UtcNow - started).TotalSeconds:N1}s.");
}

// Postgres's TIMESTAMP (without time zone) rejects a DateTimeKind.Utc value outright — the
// generator's rows are all UTC-based, but a naive column has no time-zone concept to validate
// against, so the Kind tag is simply dropped rather than the value being reinterpreted.
static DateTime AsUnspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
