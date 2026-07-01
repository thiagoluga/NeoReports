# 06 — multi-sheet XLSX workbook (Pro)

One `.xlsx` with an **Approved** and a **Rejected** worksheet — each with its own filter and columns —
built from a single source read, using the commercial `NeoReports.Xlsx.Pro` package.

```bash
dotnet run --project samples/06-multi-sheet-xlsx
# writes ./out/monthly-sales-<date>.xlsx
```

- The **Approved** sheet keeps rows with `Amount > 0` and inherits the report's four columns.
- The **Rejected** sheet keeps rows with `Amount <= 0` and declares its own two columns (Id, Customer).
- The sheet (tab) name is the first argument of `.Section("name", ...)`.

Uses an in-memory source so it runs with no database.

> `NeoReports.Xlsx.Pro` is commercial (PolyForm Small Business — free under USD 1M annual revenue).
> This sample references it as a project reference for demonstration.
