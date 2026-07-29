# NeoReports.Sources.Join.Pro (commercial)

> **Requires a NeoReports Pro license at run time** (ADR D70). Supply it by setting the
> `NEOREPORTS_LICENSE_KEY` environment variable, or explicitly at startup:
> `services.AddNeoReportsProLicense(key)` (dependency injection) or
> `NeoReports.Licensing.ProLicenseGate.Register(key)` (code-first, no container). Without a valid key
> the package throws `NeoReportsLicenseException` the first time it is used.

Multi-source composition for NeoReports: **enrichment** (B2.1) and keyset **merge-join** (B2.2).

**License:** PolyForm Small Business 1.0.0 (see [`LICENSE.txt`](LICENSE.txt)) — free for organizations
under USD 1,000,000 annual revenue; a commercial license is required above that. **Not MIT**, and
excluded from the open-source NuGet release.

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

## Dynamic config (merge-join)

The merge-join is also available on the **config-driven path** as a composite source
(`type: "merge-join"`), so a JSON report can join two sources without typed code. Register it with
`services.AddMergeJoinConfigSource()`, then:

```json
{
  "name": "customer-orders",
  "source": {
    "type": "merge-join",
    "properties": {
      "key": "customerId",
      "kind": "leftOuter",
      "left":  { "type": "sql", "properties": { "connectionString": "...", "sql": "SELECT ... ORDER BY customerId", "key": "customerId" } },
      "right": { "type": "sql", "properties": { "connectionString": "...", "sql": "SELECT ... ORDER BY customerId", "key": "customerId" } }
    }
  },
  "columns": [ { "name": "customerId", "type": "Integer" }, { "name": "customer", "type": "String" }, { "name": "total", "type": "Decimal" } ],
  "outputs": [ { "format": "csv" } ]
}
```

Both nested sources materialize against the **same report schema** — each fills the columns its query
returns and leaves the rest null — and both must be **ordered by `key`** (their keyset column). The
join then overlays the right side's non-null columns onto the matching left row; `kind` is `inner`
(default) or `leftOuter`. Config joins are expected to be **to-one** lookups (one right row per key);
with several right rows per key, the last non-null value per column wins.

