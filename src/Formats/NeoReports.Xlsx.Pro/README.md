# NeoReports.Xlsx.Pro (commercial)

> **Requires a NeoReports Pro license at run time** (ADR D70). Supply it by setting the
> `NEOREPORTS_LICENSE_KEY` environment variable, or explicitly at startup:
> `services.AddNeoReportsProLicense(key)` (dependency injection) or
> `NeoReports.Licensing.ProLicenseGate.Register(key)` (code-first, no container). Without a valid key
> the package throws `NeoReportsLicenseException` the first time it is used.

Multi-sheet XLSX **workbook** writer for NeoReports: one worksheet per view, in a single `.xlsx`
file, from a single source read.

**License:** PolyForm Small Business 1.0.0 (see [`LICENSE.txt`](LICENSE.txt)) — free for organizations
under USD 1,000,000 annual revenue; a commercial license is required above that. **Not MIT**, and
excluded from the open-source NuGet release.

## Usage

```csharp
using static NeoReports.Xlsx.Pro.Format;

builder
    .From(source)
    .Column(v => v.Id, "Id")
    .ToSections(XlsxWorkbook(o => o.AutoFilter()), s => s
        .Section("Approved", v => v.Where(x => x.Amount > 0))
        .Section("Rejected", v => v
            .Where(x => x.Amount <= 0)
            .Column(x => x.Id, "Id").Column(x => x.Customer, "Customer")));
```

Produces one workbook with an **Approved** and a **Rejected** worksheet (each with its own filter and
columns), built in the same single pass over the source. Uses the exact cell semantics (native types,
number/date formats) of the MIT `NeoReports.Formats.Xlsx` writer.
