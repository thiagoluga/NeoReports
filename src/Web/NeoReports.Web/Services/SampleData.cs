// Sample data for the starter — in production this comes from a service/repository.
// English copy (en-US) — technical terms stay in English by design.

using NeoReports.Web.Models;

namespace NeoReports.Web.Services;
public static class SampleData
{
    public static readonly List<ReportSummary> Reports = new()
    {
        new("monthly-sales", "Monthly sales · retail",
            "Monthly snapshot of sales per store, grouped by category. Includes returns and manual adjustments.",
            new[] { "sales", "monthly", "BI" }, JobStatus.Running,
            "sales-db", new[] { "CSV", "XLSX" }, "SharePoint",
            "First Monday", "06:00 BRT · America/Sao_Paulo", "2 min ago", "14:32:06 · 42,184 rows",
            "teal", "table", "View job", "eye"),
        new("acme-invoice", "Monthly invoice · Acme",
            "Consolidated monthly invoice per contract. Signed PDF delivered by email.",
            new[] { "billing", "critical" }, JobStatus.Ok,
            "billing-pg", new[] { "PDF" }, "email + S3",
            "5th of month · 08:00", "next: Jun 5 · in 9 days", "14 min ago", "14:21:04 · 1m 02s · 2.1k rows",
            "coral", "file-text"),
        new("daily-audit-log", "Daily audit log",
            "Exports the previous day's audit log to cold storage and SIEM ingestion.",
            new[] { "compliance", "audit", "critical" }, JobStatus.Failed,
            "audit-mongo", new[] { "JSON" }, "Azure Blob",
            "Every day · 01:00 BRT", "next: tomorrow · in 11h", "1h ago", "13:30:00 · 15 retries exhausted",
            "purple", "braces", "Retry", "refresh"),
        new("consolidated-statement", "Consolidated statement",
            "Financial statement per entity, with accounting adjustments and automatic reconciliation.",
            new[] { "finance", "monthly" }, JobStatus.Ok,
            "finance-db", new[] { "XLSX" }, "SharePoint",
            "Every Friday · 18:00", "next: Fri May 30 · in 4 days", "2h ago", "12:46:21 · 4m 51s · 18.3k rows",
            "teal", "table"),
        new("low-stock-alert", "Low stock · alert",
            "Checks SKUs below minimum and emails the purchasing team.",
            new[] { "ops", "inventory" }, JobStatus.Paused,
            "inventory-db", new[] { "CSV" }, "email",
            "Every 6h", "paused until May 28", "3h ago", "11:30:00 · paused by operator",
            "warn", "bell", "Resume", "player-play"),
        new("regional-sales", "Regional sales",
            "Pipeline with 3 variants: full BI dataset, filtered sales cut, and leadership drop alert.",
            new[] { "sales", "BI", "pipeline" }, JobStatus.Ok,
            "sales-db", new[] { "XLSX", "CSV", "PDF" }, "3 destinations",
            "Every day · 06:00", "next: Wed 28 · in 1 day", "yesterday", "06:00:00 · 3 variants",
            "purple", "stack-2", "Open", "arrow-right", IsPipeline: true),
    };

    public static readonly List<SourceSummary> Sources = new()
    {
        new("sales-db", "sales-db", "SQL Server",
            "Server=tcp:sales.prod.acme.local,1433;Initial Catalog=sales;Encrypt=true",
            Health.Ok, "keyset · 1,000 rows/page", "86ms", 12, "2 min ago", "teal", "database"),
        new("billing-pg", "billing-pg", "PostgreSQL",
            "postgresql://billing.prod.acme.local:5432/billing?sslmode=require",
            Health.Ok, "keyset · 500 rows/page", "112ms", 4, "14 min ago", "info", "brand-postgresql"),
        new("audit-mongo", "audit-mongo", "MongoDB",
            "mongodb+srv://audit.prod.acme.local/auditdb?replicaSet=rs0&retryWrites=true",
            Health.Error, "cursor · 100 docs/batch", "— · 4 errors 1h", 2, "1h ago", "purple", "brand-mongodb"),
        new("finance-db", "finance-db", "SQL Server",
            "Server=tcp:finance.prod.acme.local,1433;Initial Catalog=finance;Encrypt=true",
            Health.Ok, "offset · 2,000 rows/page", "94ms", 8, "2h ago", "teal", "database"),
        new("inventory-db", "inventory-db", "MySQL",
            "mysql://inventory.prod.acme.local:3306/inventory?ssl=true",
            Health.Warn, "keyset · 500 rows/page", "612ms", 6, "3h ago", "warn", "brand-mysql"),
        new("product-pg", "product-pg", "PostgreSQL",
            "postgresql://product.prod.acme.local:5432/product?sslmode=require",
            Health.Ok, "keyset · 1,000 rows/page", "78ms", 3, "5h ago", "info", "brand-postgresql"),
    };

    public static readonly List<ColumnDef> Columns = new()
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
    };

    public static readonly List<FormatOption> Formats = new()
    {
        new("csv", "CSV", ".csv", "teal", "table", "Delimited tabular text."),
        new("xlsx", "Excel", ".xlsx", "teal", "file-spreadsheet", "Spreadsheet with native types, sheets, formulas."),
        new("pdf", "PDF", ".pdf", "coral", "file-text", "Paginated document with fixed layout."),
        new("json", "JSON", ".json", "purple", "braces", "Hierarchical structure · one line per record."),
        new("txt", "Text", ".txt", "gray", "file", "Plain text · customizable template."),
        new("xml", "XML", ".xml", "purple", "code", "Structured document · schema-validatable."),
    };

    public static readonly List<DestinationOption> Destinations = new()
    {
        new("download", "Download", "gray", "download", "Serve the file via a temporary signed URL."),
        new("s3", "AWS S3", "info", "brand-aws", "Upload to an S3 bucket with IAM credentials."),
        new("azure", "Azure Blob", "info", "brand-azure", "Azure Storage container · SAS or shared key."),
        new("sharepoint", "SharePoint", "purple", "brand-office", "SharePoint Online document library."),
        new("gdrive", "Google Drive", "purple", "brand-google-drive", "Drive folder via service account."),
        new("sftp", "SFTP / FTP", "gray", "arrows-transfer-up", "SFTP or FTPS server · key auth or password."),
        new("email", "Email", "warn", "mail", "Attachment or signed link in an email body."),
        new("webhook", "Webhook", "warn", "webhook", "POST the binary to a custom HTTP endpoint."),
        new("custom", "Custom", "gray", "puzzle", "External plugin · IDestination interface."),
    };

    public static readonly List<PluginInfo> Plugins = new()
    {
        new("sources", "NeoReports.Sources.SqlServer", "teal", "database", "built-in", "Official SQL Server driver · pooling + retry · keyset support.", "v1.2.4", 12, "ok"),
        new("sources", "NeoReports.Sources.Postgres", "info", "brand-postgresql", "built-in", "Official PostgreSQL driver · keyset/offset pagination.", "v1.2.4", 7, "ok"),
        new("sources", "NeoReports.Sources.Mongo", "purple", "brand-mongodb", "built-in", "Official MongoDB driver · cursor pagination.", "v1.1.0 → v1.3.0", 2, "update"),
        new("sources", "Acme.Sources.Kafka", "gray", "brand-kafka", "custom", "Custom source · reads topics by timestamp window.", "v0.4.1", 3, "ok"),
        new("formats", "NeoReports.Formats.Csv", "teal", "table", "built-in", "Configurable CSV · delimiters, encoding, quoting.", "v1.2.4", 28, "ok"),
        new("formats", "NeoReports.Formats.Xlsx", "teal", "file-spreadsheet", "built-in", "Excel with native types, auto-filter, freeze header.", "v1.2.4", 18, "ok"),
        new("formats", "NeoReports.Formats.Pdf", "coral", "file-text", "built-in", "Paginated PDF · headers, footers, branding.", "v1.2.4", 9, "ok"),
        new("formats", "Acme.Formats.SignedPdf", "coral", "file-certificate", "custom", "Digitally signed PDF with corporate cert.", "v0.9.2", 4, "license-error"),
        new("destinations", "NeoReports.Destinations.S3", "info", "brand-aws", "built-in", "Upload to Amazon S3 · IAM or STS.", "v1.2.4", 14, "ok"),
        new("destinations", "NeoReports.Destinations.SharePoint", "purple", "brand-office", "built-in", "SharePoint Online · Azure AD auth.", "v1.2.4", 22, "ok"),
        new("destinations", "NeoReports.Destinations.Email", "warn", "mail", "built-in", "Email via SMTP · attachment or signed link.", "v1.2.4", 18, "ok"),
        new("destinations", "NeoReports.Destinations.AzureBlob", "info", "brand-azure", "built-in", "Azure Storage · SAS or shared key.", "v1.2.4", 6, "ok"),
        new("jobs", "NeoReports.Workers.Default", "gray", "cpu", "built-in", "Default worker · in-process execution with retry.", "v1.2.4", 42, "ok"),
        new("auth", "NeoReports.Auth.HostAuth", "gray", "shield", "built-in", "Default filter inheriting HttpContext.User from the host.", "v1.2.4", 1, "ok"),
        new("auth", "Acme.Auth.JwtFilter", "purple", "key", "custom", "Custom filter for corporate JWT.", "v1.0.0", 1, "ok"),
    };
}
