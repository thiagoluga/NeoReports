using MySqlConnector;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Destinations.Local;
using NeoReports.Jobs.DependencyInjection;
using NeoReports.Samples.Shared;
using NeoReports.Sources.MySql;
using NeoReports.UI;
using static NeoReports.Core.Building.ReportColumns;
// Import the format entry methods directly so Csv(...) and Xlsx(...) read cleanly and avoid the
// Format class-name collision between the two format packages (ADR D16).
using static NeoReports.Formats.Csv.Format;
using static NeoReports.Formats.Xlsx.Format;

// Sample 11 — the Web half of the MySQL Aspire sample. AppHost.cs provisions "mysql" and injects
// the "widetransactions" connection string via WithReference; this host seeds the database once,
// registers the typed "wide-transactions" report, and mounts the full NeoReports UI — Aspire's
// only job is standing up the database and starting this UI, everything else (running the
// report, watching progress, downloading the file) happens by clicking through it.
// Run standalone with:
//   dotnet run --project samples/11-aspire-mysql-wide/Web -- "<connection-string>"
//
// GuidFormat=Char36 is required on the connection string (both here and for the report source
// below) so MySqlConnector reads/writes a CHAR(36) column as a native Guid instead of a string —
// without it, RecordMaterializer's Convert.ChangeType(string, typeof(Guid)) throws, since Guid
// doesn't implement IConvertible.

var builder = WebApplication.CreateBuilder(args);

string baseConnectionString = args.Length > 0
    ? args[0]
    : builder.Configuration.GetConnectionString("widetransactions")
        ?? throw new InvalidOperationException(
            "No connection string. Run via the AppHost (dotnet run --project samples/11-aspire-mysql-wide/AppHost) " +
            "or pass one as the first argument.");

var connectionStringBuilder = new MySqlConnectionStringBuilder(baseConnectionString) { GuidFormat = MySqlGuidFormat.Char36 };
string connectionString = connectionStringBuilder.ConnectionString;

const string Culture = "en-US"; // shared across every currency-formatted column below

builder.Services.AddNeoReportsUI();

builder.Services.AddReport<WideTransaction>("wide-transactions", b => b
    .From(Source.MySql(
            connectionString,
            "SELECT TransactionId, CustomerId, CustomerName, CustomerEmail, CustomerCity, CustomerCountry, " +
            "IsVipCustomer, ProductId, ProductName, ProductCategory, ProductSku, Quantity, UnitPrice, " +
            "DiscountRate, TaxRate, ShippingCost, ProcessingFee, TotalAmount, Currency, PaymentMethod, " +
            "IsRefunded, RefundAmount, IsGift, ShippingCity, ShippingCountry, ShippingPostalCode, CarrierName, " +
            "TrackingNumber, EstimatedDeliveryDays, OrderDate, ShippedDate, DeliveredDate, CreatedAt, UpdatedAt, " +
            "ProcessedAtUtc, SalesRepId, SalesRepName, Region, Channel, Campaign, ReferralCode, DeviceType, " +
            "Browser, OperatingSystem, SessionId, Rating, FeedbackScore, LoyaltyPoints, IsFirstPurchase, " +
            "WarehouseId, Notes FROM wide_transactions " +
            "WHERE (@cursor IS NULL OR TransactionId > @cursor) ORDER BY TransactionId")
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
// process starts, and the seed can take a while on a 500,000-row table — starting the listener
// first means the UI shell loads immediately instead of the endpoint refusing connections until
// seeding finishes.
await app.StartAsync();
await EnsureSeededAsync(connectionString);
await app.WaitForShutdownAsync();

static async Task EnsureSeededAsync(string connectionString)
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
            Console.WriteLine($"wide_transactions already has {existingRows:N0} rows — skipping seed.");
            return;
        }
    }

    Console.WriteLine($"Seeding wide_transactions with {WideTransactionGenerator.DefaultRowCount:N0} rows...");
    var started = DateTime.UtcNow;

    const int batchSize = 200;
    var batch = new List<WideTransaction>(batchSize);

    foreach (var row in WideTransactionGenerator.Generate())
    {
        batch.Add(row);
        if (batch.Count == batchSize)
        {
            await InsertBatchAsync(connection, batch);
            batch.Clear();
        }
    }

    if (batch.Count > 0)
        await InsertBatchAsync(connection, batch);

    Console.WriteLine($"Seeded in {(DateTime.UtcNow - started).TotalSeconds:N1}s.");
}

static async Task InsertBatchAsync(MySqlConnection connection, List<WideTransaction> rows)
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
