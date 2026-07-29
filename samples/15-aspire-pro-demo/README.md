# 15 — Aspire all-sources demo **with the Pro packages**

Sample [14](../14-aspire-all-sources-demo) plus the three commercial Pro packages. Aspire provisions
the same four databases (PostgreSQL, MySQL, SQL Server, MongoDB), and everything the two demos have
in common — seeding, the 51-column definitions, the named-source registrations — is **reused** from
`NeoReports.Samples.AllSourcesShared`, not copied. What this sample adds is only the Pro half.

```bash
# Pro packages require a license at run time (ADR D70) — set it first:
$env:NEOREPORTS_LICENSE_KEY = "<your key>"      # bash: export NEOREPORTS_LICENSE_KEY="<your key>"
dotnet run --project samples/15-aspire-pro-demo/AppHost
```

Then open the printed dashboard URL and click into the **web** resource's endpoint.

> **Without a valid key this sample builds but does not start** — `AddNeoReportsProLicense()` throws
> `NeoReportsLicenseException` at startup. That is deliberate (D70: hard-fail, never a silent
> degrade). If you just want the all-sources tour without a license, run sample 14. Licenses are
> issued with [`tools/NeoReports.LicenseTool`](../../tools/NeoReports.LicenseTool).

## What the Pro packages add here

| Package | Registered as | What it adds, and where you see it |
|---|---|---|
| `NeoReports.Xlsx.Pro` | `AddXlsxWorkbook(o => o.AutoFilter())` + `ToSections(XlsxWorkbook(...), …)` | One XLSX workbook with a worksheet per section, from a single source read (D27). Demonstrated by the **`wide-transactions-workbook`** report — run it and open the file: three sheets (VIP / Refunded / Gifts). The DI registration serves JSON-configured reports; note the factory is an `ISectionedWriterFactory`, so `xlsx-workbook` deliberately does **not** appear in the Builder wizard's Format step, which lists flat single-file formats only. |
| `NeoReports.Sources.Join.Pro` | `AddMergeJoinConfigSource()` + `Join.MergeJoin(...)` | A streaming keyset merge-join, including across two *different* databases — something no SQL `JOIN` can do. Demonstrated by the **`transactions-postgres-joined-mongodb`** report, which merges PostgreSQL and MongoDB in `TransactionId` order at constant memory. The config-source registration lets a JSON-configured report declare two nested child sources; those children are inline configs, not references to the named sources in the registry. |
| `NeoReports.QueryBuilder.Pro` | `AddQueryBuilder()` | The **Query builder** screen actually generates SQL — without it that screen honestly reports the capability as unavailable rather than faking it (D49). This is the one Pro package with a UI screen of its own. |

Run any report from **Reports** in the UI; the two Pro ones are listed alongside the four that
sample 14 also has. Output lands in `./out/`.

Everything that is not Pro is shared code, not duplicated code: `diff` between this sample's
`Web/Program.cs` and sample 14's returns only the Pro `using`s, the Pro block above, the two Pro
report registrations, and the header comment plus the AppHost path each file names.

## Licensing

The three Pro packages are **PolyForm Small Business 1.0.0** (free for organizations under USD 1M
annual revenue; a commercial license is required above that) — not MIT, and excluded from the OSS
NuGet release. The rest of the stack this sample uses is MIT.
