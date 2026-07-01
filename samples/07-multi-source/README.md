# 07 — multi-source (keyset merge-join)

Builds one report from **two sources** with a keyset **merge-join** (the Pro feature in
`NeoReports.Sources.Join.Pro`). Customers (left) and orders (right) are each ordered by the join key;
the join emits, for every customer, the group of its orders, and the map rolls them up into an order
count and total. `JoinKind.LeftOuter` keeps customers that have no orders.

In-memory sources, so it runs with **no database**:

```bash
dotnet run --project samples/07-multi-source
```

Writes `./out/customer-orders.csv`:

```
Customer ID;Customer;Orders;Total
1;Acme;2;R$ 150,00
2;Globex;0;R$ 0,00
3;Initech;1;R$ 200,00
```

Globex has no orders yet is present with `0` — that's the left-outer behaviour; swap to
`JoinKind.Inner` to drop it.

> **License:** `NeoReports.Sources.Join.Pro` is commercial (PolyForm Small Business 1.0.0 — free
> under USD 1M annual revenue, see the package `LICENSE.txt`). This sample references it as a project
> reference for demonstration.

## How it's wired

```csharp
.From(Join.MergeJoin(
    left: customers, keyLeft: c => c.Id,
    right: orders, keyRight: o => o.CustomerId,
    map: (c, os) => new CustomerReport(c.Id, c.Name, os.Count, os.Sum(o => o.Amount)),
    kind: JoinKind.LeftOuter))
```

The merge streams — one right key-group buffered at a time — so memory stays constant. The result is
an `IStreamingSource<CustomerReport>` the standard pipeline consumes unchanged.
