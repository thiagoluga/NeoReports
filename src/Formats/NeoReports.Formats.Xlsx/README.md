# NeoReports.Formats.Xlsx

XLSX (Excel) writer for [NeoReports](https://github.com/thiagoluga/NeoReports), backed by
[ClosedXML](https://github.com/ClosedXML/ClosedXML).

Named sheet, optional auto-filter, native cell types (numbers/dates preserved), and per-column
number/date formats.

> Note: ClosedXML builds the whole workbook in memory before saving, so this writer's memory grows
> with the row count (unlike the streaming CSV writer). For very large reports, prefer CSV.

## Usage

```csharp
using static NeoReports.Formats.Xlsx.Format;

b.To(Xlsx(o => o.SheetName("Sales").AutoFilter()));
```

To emit CSV **and** XLSX in a single source pass, add both `.To(...)` calls (import each `Format`
with `using static` to avoid the `Format` name clash):

```csharp
using static NeoReports.Formats.Csv.Format;
using static NeoReports.Formats.Xlsx.Format;
// ...
b.To(Csv(o => o.Delimiter(';')))
 .To(Xlsx(o => o.SheetName("Sales").AutoFilter()));
```

## License

MIT © NeoReports Contributors
