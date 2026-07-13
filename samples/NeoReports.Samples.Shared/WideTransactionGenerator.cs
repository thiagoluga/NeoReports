using System.Globalization;

namespace NeoReports.Samples.Shared;

/// <summary>
/// Deterministic (seeded) bulk generator for <see cref="WideTransaction"/> rows, shared by every
/// Aspire multi-DB sample (Epic H / D46) so their seed shape and scale are identical. Yields lazily
/// (<c>yield return</c>) so seeding even the default 500,000 rows never materializes more than one
/// row at a time — the same constant-memory principle the engine itself is built on, applied to the
/// seeding step that feeds it.
/// </summary>
public static class WideTransactionGenerator
{
    /// <summary>Large enough to make constant-memory streaming a visible, honest selling point in a
    /// sample run, without making seeding painfully slow on a contributor's machine or in CI.</summary>
    public const int DefaultRowCount = 500_000;

    private static readonly string[] FirstNames =
    {
        "Ana", "Bruno", "Carla", "Diego", "Elena", "Fabio", "Giulia", "Hugo", "Ines", "Joao",
        "Karen", "Lucas", "Marta", "Nuno", "Olivia", "Pedro", "Rita", "Sergio", "Tania", "Victor",
    };

    private static readonly string[] LastNames =
    {
        "Silva", "Santos", "Oliveira", "Souza", "Costa", "Pereira", "Almeida", "Ferreira",
        "Rodrigues", "Carvalho", "Gomes", "Martins", "Araujo", "Melo", "Barbosa",
    };

    private static readonly string[] Cities =
    {
        "Sao Paulo", "Rio de Janeiro", "Belo Horizonte", "Curitiba", "Porto Alegre", "Salvador",
        "Recife", "Fortaleza", "Brasilia", "Lisbon", "Porto", "Madrid", "Berlin", "Austin",
    };

    private static readonly string[] Countries = { "BR", "PT", "ES", "DE", "US", "UK", "FR", "IT" };

    private static readonly string[] ProductCategories =
    {
        "Electronics", "Home & Garden", "Apparel", "Sporting Goods", "Books", "Toys",
        "Groceries", "Automotive", "Beauty", "Office Supplies",
    };

    private static readonly string[] Currencies = { "BRL", "USD", "EUR", "GBP" };
    private static readonly string[] PaymentMethods = { "credit-card", "debit-card", "pix", "boleto", "paypal" };
    private static readonly string[] Carriers = { "Correios", "DHL", "FedEx", "UPS", "Loggi" };
    private static readonly string[] Channels = { "web", "mobile-app", "marketplace", "call-center", "store" };
    private static readonly string[] Regions = { "North", "Northeast", "Central-West", "Southeast", "South" };
    private static readonly string[] DeviceTypes = { "desktop", "mobile", "tablet" };
    private static readonly string[] Browsers = { "Chrome", "Safari", "Firefox", "Edge" };
    private static readonly string[] OperatingSystems = { "Windows", "macOS", "iOS", "Android", "Linux" };

    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Generates <paramref name="rowCount"/> deterministic rows — the same <paramref name="seed"/>
    /// always produces byte-identical output, so re-running a sample's seeding step is idempotent
    /// without needing to persist what was generated.
    /// </summary>
    public static IEnumerable<WideTransaction> Generate(int rowCount = DefaultRowCount, int seed = 42)
    {
        var random = new Random(seed);

        for (long i = 1; i <= rowCount; i++)
        {
            long customerId = 1 + (i % 50_000);
            long productId = 1 + (i % 5_000);
            decimal unitPrice = Math.Round((decimal)(random.NextDouble() * 495 + 5), 2);
            long quantity = 1 + random.Next(10);
            decimal discountRate = Math.Round((decimal)(random.NextDouble() * 0.3), 4);
            decimal taxRate = Math.Round((decimal)(random.NextDouble() * 0.15 + 0.05), 4);
            decimal shippingCost = Math.Round((decimal)(random.NextDouble() * 40), 2);
            decimal processingFee = Math.Round((decimal)(random.NextDouble() * 5), 2);
            decimal subtotal = unitPrice * quantity * (1 - discountRate);
            decimal totalAmount = Math.Round(subtotal * (1 + taxRate) + shippingCost + processingFee, 2);
            bool isRefunded = random.Next(100) < 4;
            DateTime orderDate = Epoch.AddMinutes(random.Next(0, 60 * 24 * 730));

            yield return new WideTransaction(
                TransactionId: NextGuid(random),
                CustomerId: customerId,
                CustomerName: $"{Pick(random, FirstNames)} {Pick(random, LastNames)}",
                CustomerEmail: $"customer{customerId}@example.com",
                CustomerCity: Pick(random, Cities),
                CustomerCountry: Pick(random, Countries),
                IsVipCustomer: random.Next(100) < 12,
                ProductId: productId,
                ProductName: $"Product {productId}",
                ProductCategory: Pick(random, ProductCategories),
                ProductSku: $"SKU-{productId:D6}",
                Quantity: quantity,
                UnitPrice: unitPrice,
                DiscountRate: discountRate,
                TaxRate: taxRate,
                ShippingCost: shippingCost,
                ProcessingFee: processingFee,
                TotalAmount: totalAmount,
                Currency: Pick(random, Currencies),
                PaymentMethod: Pick(random, PaymentMethods),
                IsRefunded: isRefunded,
                RefundAmount: isRefunded ? Math.Round(totalAmount * (decimal)random.NextDouble(), 2) : 0m,
                IsGift: random.Next(100) < 8,
                ShippingCity: Pick(random, Cities),
                ShippingCountry: Pick(random, Countries),
                ShippingPostalCode: random.Next(10000, 99999).ToString(CultureInfo.InvariantCulture),
                CarrierName: Pick(random, Carriers),
                TrackingNumber: $"TRK{i:D9}",
                EstimatedDeliveryDays: 1 + random.Next(10),
                OrderDate: orderDate.Date,
                ShippedDate: orderDate.AddDays(1 + random.Next(3)).Date,
                DeliveredDate: orderDate.AddDays(2 + random.Next(7)).Date,
                CreatedAt: orderDate,
                UpdatedAt: orderDate.AddMinutes(random.Next(0, 1440)),
                ProcessedAtUtc: orderDate.AddSeconds(random.Next(0, 60)),
                SalesRepId: 1 + random.Next(200),
                SalesRepName: $"{Pick(random, FirstNames)} {Pick(random, LastNames)}",
                Region: Pick(random, Regions),
                Channel: Pick(random, Channels),
                Campaign: random.Next(100) < 30 ? $"campaign-{random.Next(1, 25)}" : "",
                ReferralCode: random.Next(100) < 15 ? $"REF{random.Next(1000, 9999)}" : "",
                DeviceType: Pick(random, DeviceTypes),
                Browser: Pick(random, Browsers),
                OperatingSystem: Pick(random, OperatingSystems),
                SessionId: NextGuid(random),
                Rating: random.Next(100) < 60 ? 1 + random.Next(5) : 0,
                FeedbackScore: Math.Round((decimal)(random.NextDouble() * 10), 2),
                LoyaltyPoints: random.Next(0, 5000),
                IsFirstPurchase: random.Next(100) < 20,
                WarehouseId: 1 + random.Next(12),
                Notes: random.Next(100) < 5 ? "Customer requested gift wrapping." : "");
        }
    }

    private static string Pick(Random random, string[] values) => values[random.Next(values.Length)];

    private static Guid NextGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}
