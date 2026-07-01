# NeoReports.Sources.Join

Multi-source composition for NeoReports. **B2.1 — enrichment** is here; the keyset **merge-join**
(B2.2) follows.

> Packaging and license (Pro vs free) are an open Epic B2 decision (**D29**), settled in B2.3. This
> package is not auto-published yet.

## Enrichment

For each page of a primary source, one **batched** lookup resolves the page's distinct keys (never one
call per row), then each row is mapped with its looked-up value:

```csharp
.From(Source.Sql(conn, sqlCustomers).Keyset<Customer, long>(c => c.Id)
    .Enrich(
        key:    c => c.Id,
        lookup: (keys, ct) => LoadOrderCountsAsync(keys, ct),   // ONE call per page
        map:    (c, orderCount) => new CustomerSummary(c, orderCount)))
```

O(pageSize) memory; the batched-per-page shape structurally prevents the N+1 trap. The result is an
`IBatchSource<TResult>` the standard pipeline consumes unchanged.
