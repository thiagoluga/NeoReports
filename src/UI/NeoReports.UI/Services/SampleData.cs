// Sample data for the starter — in production this comes from a service/repository.
// English copy (en-US) — technical terms stay in English by design.

using NeoReports.UI.Models;

namespace NeoReports.UI.Services;

public static class SampleData
{
    // Sample-data literals that repeat across rows, named once (S1192).
    private const string SalesDb = "sales-db";
    private const string IconTable = "table";
    private const string TintCoral = "coral";
    private const string TintPurple = "purple";
    private const string Custom = "custom";

    public static readonly IReadOnlyList<ReportSummary> Reports = Array.AsReadOnly(new ReportSummary[]
    {
        new("monthly-sales", "Monthly sales · retail",
            "Monthly snapshot of sales per store, grouped by category. Includes returns and manual adjustments.",
            new[] { "sales", "monthly", "BI" }, JobStatus.Running,
            SalesDb, new[] { "CSV", "XLSX" }, "SharePoint",
            "First Monday", "06:00 BRT · America/Sao_Paulo", "2 min ago", "14:32:06 · 42,184 rows",
            "teal", IconTable, "View job", "eye"),
        new("acme-invoice", "Monthly invoice · Acme",
            "Consolidated monthly invoice per contract. Signed PDF delivered by email.",
            new[] { "billing", "critical" }, JobStatus.Ok,
            "billing-pg", new[] { "PDF" }, "email + S3",
            "5th of month · 08:00", "next: Jun 5 · in 9 days", "14 min ago", "14:21:04 · 1m 02s · 2.1k rows",
            TintCoral, "file-text"),
        new("daily-audit-log", "Daily audit log",
            "Exports the previous day's audit log to cold storage and SIEM ingestion.",
            new[] { "compliance", "audit", "critical" }, JobStatus.Failed,
            "audit-mongo", new[] { "JSON" }, "Azure Blob",
            "Every day · 01:00 BRT", "next: tomorrow · in 11h", "1h ago", "13:30:00 · 15 retries exhausted",
            TintPurple, "braces", "Retry", "refresh"),
        new("consolidated-statement", "Consolidated statement",
            "Financial statement per entity, with accounting adjustments and automatic reconciliation.",
            new[] { "finance", "monthly" }, JobStatus.Ok,
            "finance-db", new[] { "XLSX" }, "SharePoint",
            "Every Friday · 18:00", "next: Fri May 30 · in 4 days", "2h ago", "12:46:21 · 4m 51s · 18.3k rows",
            "teal", IconTable),
        new("low-stock-alert", "Low stock · alert",
            "Checks SKUs below minimum and emails the purchasing team.",
            new[] { "ops", "inventory" }, JobStatus.Paused,
            "inventory-db", new[] { "CSV" }, "email",
            "Every 6h", "paused until May 28", "3h ago", "11:30:00 · paused by operator",
            "warn", "bell", "Resume", "player-play"),
        new("regional-sales", "Regional sales",
            "Pipeline with 3 variants: full BI dataset, filtered sales cut, and leadership drop alert.",
            new[] { "sales", "BI", "pipeline" }, JobStatus.Ok,
            SalesDb, new[] { "XLSX", "CSV", "PDF" }, "3 destinations",
            "Every day · 06:00", "next: Wed 28 · in 1 day", "yesterday", "06:00:00 · 3 variants",
            TintPurple, "stack-2", "Open", "arrow-right", IsPipeline: true),
    });

    public static readonly IReadOnlyList<SourceSummary> Sources = Array.AsReadOnly(new SourceSummary[]
    {
        new(SalesDb, SalesDb, "SQL Server",
            "Server=tcp:sales.prod.acme.local,1433;Initial Catalog=sales;Encrypt=true",
            Health.Ok, "keyset · 1,000 rows/page", "86ms", 12, "2 min ago", "teal", "database"),
        new("billing-pg", "billing-pg", "PostgreSQL",
            "postgresql://billing.prod.acme.local:5432/billing?sslmode=require",
            Health.Ok, "keyset · 500 rows/page", "112ms", 4, "14 min ago", "info", "brand-postgresql"),
        new("audit-mongo", "audit-mongo", "MongoDB",
            "mongodb+srv://audit.prod.acme.local/auditdb?replicaSet=rs0&retryWrites=true",
            Health.Error, "cursor · 100 docs/batch", "— · 4 errors 1h", 2, "1h ago", TintPurple, "brand-mongodb"),
        new("finance-db", "finance-db", "SQL Server",
            "Server=tcp:finance.prod.acme.local,1433;Initial Catalog=finance;Encrypt=true",
            Health.Ok, "offset · 2,000 rows/page", "94ms", 8, "2h ago", "teal", "database"),
        new("inventory-db", "inventory-db", "MySQL",
            "mysql://inventory.prod.acme.local:3306/inventory?ssl=true",
            Health.Warn, "keyset · 500 rows/page", "612ms", 6, "3h ago", "warn", "brand-mysql"),
        new("product-pg", "product-pg", "PostgreSQL",
            "postgresql://product.prod.acme.local:5432/product?sslmode=require",
            Health.Ok, "keyset · 1,000 rows/page", "78ms", 3, "5h ago", "info", "brand-postgresql"),
    });

    public static readonly IReadOnlyList<ColumnDef> Columns = Array.AsReadOnly(new ColumnDef[]
    {
        new("id", "num", true, "rename", "Sale ID"),
        new("customer_id", "num", true),
        new("total", "num", true, "format", "$ #,##0.00"),
        new("created_at", "date", true, "format", "MM/DD/YYYY HH:mm"),
        new("status_id", "num", true),
        new("region", "text", true),
        new("metadata", "text", false),
        new("notes", "text", false),
        new("updated_at", "date", false),
        new("tenant_id", "text", false),
    });

    public static readonly IReadOnlyList<FormatOption> Formats = Array.AsReadOnly(new FormatOption[]
    {
        new("csv", "CSV", ".csv", "teal", IconTable, "Delimited tabular text."),
        new("xlsx", "Excel", ".xlsx", "teal", "file-spreadsheet", "Spreadsheet with native types, sheets, formulas."),
        new("pdf", "PDF", ".pdf", TintCoral, "file-text", "Paginated document with fixed layout."),
        new("json", "JSON", ".json", TintPurple, "braces", "Hierarchical structure · one line per record."),
        new("txt", "Text", ".txt", "gray", "file", "Plain text · customizable template."),
        new("xml", "XML", ".xml", TintPurple, "code", "Structured document · schema-validatable."),
    });

    public static readonly IReadOnlyList<DestinationOption> Destinations = Array.AsReadOnly(new DestinationOption[]
    {
        new("download", "Download", "gray", "download", "Serve the file via a temporary signed URL."),
        new("s3", "AWS S3", "info", "brand-aws", "Upload to an S3 bucket with IAM credentials."),
        new("azure", "Azure Blob", "info", "brand-azure", "Azure Storage container · SAS or shared key."),
        new("sharepoint", "SharePoint", TintPurple, "brand-office", "SharePoint Online document library."),
        new("gdrive", "Google Drive", TintPurple, "brand-google-drive", "Drive folder via service account."),
        new("sftp", "SFTP / FTP", "gray", "arrows-transfer-up", "SFTP or FTPS server · key auth or password."),
        new("email", "Email", "warn", "mail", "Attachment or signed link in an email body."),
        new("webhook", "Webhook", "warn", "webhook", "POST the binary to a custom HTTP endpoint."),
        new(Custom, "Custom", "gray", "puzzle", "External plugin · IDestination interface."),
    });
}
