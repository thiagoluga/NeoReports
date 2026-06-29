# NeoReports.Destinations.Local

Local filesystem destination for [NeoReports](https://github.com/thiagoluga/NeoReports).

Writes the finished report file to a path resolved from a template, with an atomic publish (content
is written to a temp file in the target directory and moved into place only after a successful copy,
so a failure never leaves a partial file).

## Usage

```csharp
using NeoReports.Destinations.Local;

b.UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}"));
```

Tokens: `{name}` (report name), `{ext}` (file extension), `{date}` / `{date:format}`, and any
run-time parameter (`{paramName}`).

## License

MIT © NeoReports Contributors
