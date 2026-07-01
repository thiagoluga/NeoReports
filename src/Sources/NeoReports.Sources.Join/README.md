# NeoReports.Sources.Join

Multi-source composition for NeoReports: **enrichment** (B2.1) and keyset **merge-join** (B2.2).

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

## Keyset merge-join

Merge two sources that are each **ordered by the join key** (same key domain). For every left row it
emits the group of right rows sharing its key; `Inner` drops unmatched left rows, `LeftOuter` keeps
them with an empty group:

```csharp
.From(Join.MergeJoin(
    left:     Source.Sql(conn, sqlCustomers).Keyset<Customer, long>(c => c.Id),
    keyLeft:  c => c.Id,
    right:    Source.Sql(conn, sqlOrders).Keyset<Order, long>(o => o.CustomerId),
    keyRight: o => o.CustomerId,
    map:      (c, orders) => new CustomerOrders(c, orders),
    kind:     JoinKind.LeftOuter))
```

Streams the merge — one right key-group buffered at a time — so memory stays constant as long as a
single key's right multiplicity is bounded. The result is an `IStreamingSource<TResult>` the pipeline
slices into batches.

