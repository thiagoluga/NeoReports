using NeoReports.AspNetCore;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Destinations.Local;
using NeoReports.Licensing;
using NeoReports.QueryBuilder.Pro;
using NeoReports.Samples.AllSourcesShared;
using NeoReports.Samples.AspireProDemo.Web;
using NeoReports.Samples.Shared;
using NeoReports.Sources.Join.Pro;
using NeoReports.UI;
using NeoReports.Xlsx.Pro;
using static NeoReports.Core.Building.ReportColumns;
using static NeoReports.Xlsx.Pro.Format;
using MongoSource = NeoReports.Sources.MongoDb.Source;
using PostgresSource = NeoReports.Sources.Postgres.Source;

// Sample 15 — sample 14's all-sources demo plus the three commercial Pro packages. Everything the
// two demos share — connection-string resolution, seeding, the 51-column definitions, the four
// typed reports, the named-source registrations and every non-Pro capability provider — comes from
// AllSourcesShared, so the only difference between this file and sample 14's Program.cs is the
// "Pro" block below plus its usings.
// Run standalone with:
//   dotnet run --project samples/15-aspire-pro-demo/Web -- "<pg>" "<mysql>" "<sqlserver>" "<mongo>"

AllSourcesDemo.RegisterMongoGuidSerializer();

var builder = WebApplication.CreateBuilder(args);

var connections = AllSourcesDemo.ResolveConnections(
    args, builder.Configuration, "samples/15-aspire-pro-demo/AppHost");

AllSourcesDemo.AddSharedServices(builder.Services);
AllSourcesDemo.AddWideTransactionReports(builder.Services, connections);

// ---- Pro (PolyForm Small Business, ADR D29/D49) ----------------------------------------------
// The license is read from NEOREPORTS_LICENSE_KEY. Registering it explicitly first means a missing
// or expired key fails here, with one clear message, rather than at whichever Pro Add*() ran first.
builder.Services.AddNeoReportsProLicense();

// Registers the "xlsx-workbook" sectioned writer factory so a JSON-configured (dynamic) report can
// name it (D27). Note it is an ISectionedWriterFactory, not an IWriterFactory, so it deliberately
// does NOT appear in GET /api/capabilities.Formats or the Builder wizard's Format step — that list
// is flat single-file formats only. The code-first equivalent is the ToSections(...) report below.
builder.Services.AddXlsxWorkbook(o => o.AutoFilter());

// The config-driven "merge-join" source type, so a JSON-configured report can declare two nested
// child sources and join them (D28/D29). Its children are inline source configs, not references to
// the named sources in the registry. The code-first equivalent is the Join.MergeJoin report below.
builder.Services.AddMergeJoinConfigSource();

// The visual query builder's SQL generator (D49) — without it the /query-builder screen honestly
// reports the capability as unavailable instead of faking it. This is the only one of the three
// that lights up a UI screen; the other two are code-first/config-first, so the two reports below
// exist to actually exercise them rather than leave them registered but unused.
builder.Services.AddQueryBuilder();

// Pro report 1 — one XLSX workbook with a worksheet per section, from a single source read
// (NeoReports.Xlsx.Pro). The core Xlsx package can only write one flat file per output; splitting
// the same read across named sheets is what ToSections + XlsxWorkbook add. The SELECT lists only
// the columns the sheets show — WideTransaction's other properties materialize as their default
// (RecordMaterializer matches constructor parameters to result columns by name).
builder.Services.AddReport<WideTransaction>("wide-transactions-workbook", b => b
    .From(PostgresSource.Postgres(
            connections.Postgres,
            "SELECT TransactionId, CustomerName, ProductName, Region, Channel, TotalAmount, Currency, " +
            "IsRefunded, IsGift, IsVipCustomer, OrderDate FROM wide_transactions " +
            "WHERE (@cursor IS NULL OR TransactionId > @cursor::uuid) ORDER BY TransactionId")
        .Keyset<WideTransaction, Guid>(v => v.TransactionId, pageSize: 5000))
    .ToSections(XlsxWorkbook(o => o.AutoFilter()), s => s
        .Section("VIP", v => v.Where(t => t.IsVipCustomer).Columns(WorkbookColumns()))
        .Section("Refunded", v => v.Where(t => t.IsRefunded).Columns(WorkbookColumns()))
        .Section("Gifts", v => v.Where(t => t.IsGift).Columns(WorkbookColumns())))
    .UploadTo(Destination.Local(AllSourcesDemo.LocalDestinationTemplate)));

// Pro report 2 — a keyset merge-join across two different databases
// (NeoReports.Sources.Join.Pro). No SQL JOIN can span PostgreSQL and MongoDB; this streams both
// sides in TransactionId order and merges them, so memory stays constant regardless of row count.
builder.Services.AddReport<JoinedTransaction>("transactions-postgres-joined-mongodb", b => b
    .From(Join.MergeJoin(
        PostgresSource.Postgres(
                connections.Postgres,
                "SELECT TransactionId, CustomerName, TotalAmount, Currency FROM wide_transactions " +
                "WHERE (@cursor IS NULL OR TransactionId > @cursor::uuid) ORDER BY TransactionId")
            .Keyset<WideTransaction, Guid>(v => v.TransactionId, pageSize: 5000),
        left => left.TransactionId,
        MongoSource.MongoDb(connections.Mongo, AllSourcesDemo.MongoDatabaseName, AllSourcesDemo.MongoCollectionName)
            .Keyset<WideTransaction, Guid>(v => v.TransactionId, pageSize: 5000),
        right => right.TransactionId,
        (left, rights) => new JoinedTransaction(
            left.TransactionId,
            left.CustomerName,
            left.TotalAmount,
            left.Currency,
            rights.Count > 0 ? rights[0].ProductName : null,
            rights.Count > 0 ? rights[0].WarehouseId : (long?)null)))
    .Columns(
        Col<JoinedTransaction, Guid>(t => t.TransactionId, "Transaction ID"),
        Col<JoinedTransaction, string>(t => t.CustomerName, "Customer name"),
        Col<JoinedTransaction, decimal>(t => t.TotalAmount, "Total amount"),
        Col<JoinedTransaction, string>(t => t.Currency, "Currency"),
        Col<JoinedTransaction, string?>(t => t.MongoProductName, "Product name (MongoDB)"),
        Col<JoinedTransaction, long?>(t => t.MongoWarehouseId, "Warehouse ID (MongoDB)"))
    .To(NeoReports.Formats.Csv.Format.Csv())
    .UploadTo(Destination.Local(AllSourcesDemo.LocalDestinationTemplate)));
// ---- end Pro ---------------------------------------------------------------------------------

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

await AllSourcesDemo.SeedAndRegisterAsync(app.Services, connections);

await app.WaitForShutdownAsync();

// The subset of columns every worksheet of the Pro workbook report shows — the sheets differ by
// filter, not by shape.
static ColumnDefinition<WideTransaction>[] WorkbookColumns() =>
[
    Col<WideTransaction, Guid>(t => t.TransactionId, "Transaction ID"),
    Col<WideTransaction, string>(t => t.CustomerName, "Customer name"),
    Col<WideTransaction, string>(t => t.ProductName, "Product name"),
    Col<WideTransaction, string>(t => t.Region, "Region"),
    Col<WideTransaction, string>(t => t.Channel, "Channel"),
    Col<WideTransaction, decimal>(t => t.TotalAmount, "Total amount"),
    Col<WideTransaction, string>(t => t.Currency, "Currency"),
    Col<WideTransaction, DateTime>(t => t.OrderDate, "Order date", format: "yyyy-MM-dd"),
];
