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
}
