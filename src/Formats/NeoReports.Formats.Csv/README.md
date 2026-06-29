# NeoReports.Formats.Csv

Streaming CSV writer for [NeoReports](https://github.com/thiagoluga/NeoReports).

RFC 4180 escaping, configurable delimiter and encoding, a header row from each column's
`DisplayName`, and per-column culture/format. The writer is fully streaming — memory stays O(page
size) regardless of report size.

## Usage

```csharp
using System.Text;
using static NeoReports.Formats.Csv.Format;

b.To(Csv(o => o.Delimiter(';').Encoding(Encoding.UTF8)));
```

## License

MIT © NeoReports Contributors
