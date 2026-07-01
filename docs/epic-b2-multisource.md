# Epic B2 — Multi-source reports (join / enrichment)

> **Status: design, not yet built.** Blueprint to approve before coding. Decisions land in
> `DECISIONS.md` (D23 expanded, D28/D29 new).

## Goal

Let one report be assembled from **several sources** — combining rows by key into one output. Two
shapes, kept as **two explicit, user-chosen strategies** (D23) because they have different memory/perf
profiles and forcing one abstraction hurts:

1. **Keyset merge-join** — two sources ordered by the same key, merged in streaming. Constant memory;
   best for joining two large ordered datasets.
2. **Enrichment / lookup** — for each page of a primary source, batch-fetch related data from a
   secondary and map it in. O(pageSize) memory; best for "for each row, get X from another source."

Both produce an `IBatchSource<TResult>` (or `IStreamingSource<TResult>`) that the **existing pipeline
consumes unchanged** — no parallel pipeline, same batch/retry/writer/destination path.

## Strategy A — keyset merge-join

A streaming merge of two ordered sources:

```csharp
var report = new ReportBuilder<CustomerOrders>("...")
    .From(Source.MergeJoin(
        left:  Source.Sql(conn, sqlCustomers).Keyset<Customer, long>(c => c.Id, pageSize: 1000),
        right: Source.Sql(conn, sqlOrders).Keyset<Order, long>(o => o.CustomerId, pageSize: 1000),
        on:    (c, o) => c.Id.CompareTo(o.CustomerId),
        map:   (c, orders) => new CustomerOrders(c, orders)))   // orders = the matched right rows
    ...
```

- Both sources **must be ordered by the join key** (the keyset key) — a documented precondition, same
  spirit as the v1 keyset requirement.
- Implemented as an `IStreamingSource<TResult>`: page each sub-source into an ordered async stream,
  advance the side with the smaller key, group equal keys, emit results. The pipeline slices the
  stream into batches (D4).
- **Constant memory** as long as the multiplicity of a single key is bounded (one key-group from each
  side held at the merge frontier). Document this; a pathological one-key-maps-to-everything join is
  the caller's responsibility.
- Join types for v1: **inner** and **left-outer** (`map` receives an empty/absent right group).

## Strategy B — enrichment / lookup

An `IBatchSource<TResult>` wrapping a primary source:

```csharp
.From(Source.Sql(conn, sqlCustomers).Keyset<Customer, long>(c => c.Id)
    .Enrich(
        key:    c => c.Id,
        lookup: async (keys, ct) => await LoadOrderCountsAsync(keys, ct),  // ONE batched call per page
        map:    (c, orderCount) => new CustomerSummary(c, orderCount)))
```

- Per page: read the primary page, collect its keys, make **one batched lookup call** for the whole
  page (never one-per-row), then map each row + its looked-up value. Cursor = the primary's cursor.
- **O(pageSize)** memory. The batched-per-page shape structurally prevents the N+1 trap.
- The lookup is a user delegate (any source: SQL `WHERE key IN (...)`, HTTP, cache, ...).

## OSS / Pro boundary (needs a maintainer decision)

Unlike B1 (a generic MIT hook + a Pro writer), B2's value **is** the join sources themselves — there
is no natural "free generic half." So this is a straight monetization call:

- **Recommended: Pro** — ship both strategies in a commercial package (e.g. `NeoReports.Sources.Join.Pro`),
  same model as `NeoReports.Xlsx.Pro` (PolyForm Small Business, `IsPackable=false`, excluded from the
  OSS release). Consistent with "advanced features are paid" (D27) and the maintainer's B1 choice.
- **Alternative: free (MIT)** in `NeoReports.Sources.Join` — maximizes adoption, forgoes B2 revenue.

They plug in through the existing extensibility (`IBatchSource`/`IStreamingSource` + the fluent
`Source.MergeJoin` / `.Enrich`), so the OSS engine is unchanged either way.

## Open sub-decisions (maintainer)

1. **Pro or free**, and if Pro, the **package name** (`NeoReports.Sources.Join.Pro`?).
2. **Join types** in v1 — inner + left-outer enough? (right/full-outer later.)
3. **Dynamic config** for multi-source — express two sources + join in JSON. Recommend **deferring**
   to a later step once the typed API settles (as B1.6 followed B1.3).
4. **Validation gate** — D23/the roadmap gate multi-source on real-user validation; confirm we build it now.

## Implementation PR breakdown (after approval)

- **B2.1 — Enrichment** (`.Enrich(...)`): the simpler `IBatchSource<TResult>` wrapper + batched lookup.
  Tests: batched-per-page (no N+1), correct mapping, missing-key handling.
- **B2.2 — Merge-join** (`Source.MergeJoin(...)`): the streaming keyset merge; inner + left-outer.
  Tests: ordered merge correctness, constant memory (bounded key group), Testcontainers E2E across two
  SQL sources.
- **B2.3 — Package & docs** (per the Pro/free decision) + a sample `07-multi-source`.
- **B2.4 — Dynamic config** for multi-source (optional, later).

Each PR small, green tests, one at a time — same workflow as Epic A/B1.
