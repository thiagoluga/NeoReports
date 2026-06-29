# NeoReports.Sources.Sql

SQL Server source with keyset pagination for [NeoReports](https://github.com/thiagoluga/NeoReports).

Reads a report from SQL Server one page at a time, opening and closing a connection per page, using
an opaque `string?` keyset cursor. The query must expose a `@cursor` parameter on the key column and
order by it.

## Usage

```csharp
using NeoReports.Sources.Sql;

b.From(Source.Sql(connectionString,
        "SELECT Id, Customer, Amount, Date FROM Sales " +
        "WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id")
    .Keyset<Sale, long>(v => v.Id, pageSize: 1000));
```

Run-time report parameters are bound automatically when the query references them (e.g. `@start`).

## License

MIT © NeoReports Contributors
