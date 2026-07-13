using System.Data;
using Microsoft.Data.SqlClient;
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.DependencyInjection;
using NeoReports.Destinations.Local;
using NeoReports.Jobs.DependencyInjection;
using NeoReports.Samples.Shared;
using NeoReports.Sources.Sql;
using NeoReports.UI;
using static NeoReports.Core.Building.ReportColumns;
// Import the format entry methods directly so Csv(...) and Xlsx(...) read cleanly and avoid the
// Format class-name collision between the two format packages (ADR D16).
using static NeoReports.Formats.Csv.Format;
using static NeoReports.Formats.Xlsx.Format;

// Sample 12 — the Web half of the SQL Server Aspire sample. AppHost.cs provisions "sqlserver" and
// injects the "widetransactions" connection string via WithReference; this host seeds the
// database once, registers the typed "wide-transactions" report, and mounts the full NeoReports
// UI — Aspire's only job is standing up the database and starting this UI, everything else
// (running the report, watching progress, downloading the file) happens by clicking through it.
// Run standalone with:
//   dotnet run --project samples/12-aspire-sqlserver-wide/Web -- "<connection-string>"

var builder = WebApplication.CreateBuilder(args);

string connectionString = args.Length > 0
    ? args[0]
    : builder.Configuration.GetConnectionString("widetransactions")
        ?? throw new InvalidOperationException(
            "No connection string. Run via the AppHost (dotnet run --project samples/12-aspire-sqlserver-wide/AppHost) " +
            "or pass one as the first argument.");

const string Culture = "en-US"; // shared across every currency-formatted column below

builder.Services.AddNeoReportsUI();

builder.Services.AddReport<WideTransaction>("wide-transactions", b => b
    .From(Source.Sql(
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
            Console.WriteLine($"wide_transactions already has {existingRows:N0} rows — skipping seed.");
            return;
        }
    }

    Console.WriteLine($"Seeding wide_transactions with {WideTransactionGenerator.DefaultRowCount:N0} rows...");
    var started = DateTime.UtcNow;

    var table = BuildEmptyTable();
    using var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = "wide_transactions", BatchSize = 5000 };
    foreach (DataColumn column in table.Columns)
        bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);

    const int flushEvery = 5000;
    var buffered = 0;
    foreach (var row in WideTransactionGenerator.Generate())
    {
        AddRow(table, row);
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

    Console.WriteLine($"Seeded in {(DateTime.UtcNow - started).TotalSeconds:N1}s.");
}

static DataTable BuildEmptyTable()
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

static void AddRow(DataTable table, WideTransaction row) => table.Rows.Add(
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
