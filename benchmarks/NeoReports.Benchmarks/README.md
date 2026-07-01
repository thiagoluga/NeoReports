# NeoReports.Benchmarks

Memory benchmark that validates **CA-3 (constant memory)** from the MVP spec:
generating a 1,000,000-row report keeps allocation roughly constant — there is no
growth proportional to the total number of rows.

## Running

```bash
# All benchmarks (CSV + XLSX, 100k and 1M rows) — full statistical run, slow:
dotnet run -c Release --project benchmarks/NeoReports.Benchmarks

# Just the CSV path:
dotnet run -c Release --project benchmarks/NeoReports.Benchmarks -- --filter '*Csv*'

# Quick smoke run (few iterations):
dotnet run -c Release --project benchmarks/NeoReports.Benchmarks --no-build -- \
  --filter '*Csv*' --warmupCount 1 --iterationCount 3 --launchCount 1
```

The benchmark feeds a lazy `SyntheticSource` (generates one page at a time, never
materializing the full set) through the real `ReportRunner` pipeline to CSV/XLSX.

## How to read the result (CA-3)

`MemoryDiagnoser`'s **Allocated** column is *total* managed allocation over the run
(including memory the GC reclaims), so it naturally grows with row count. The proof
of constant memory is that **allocation per row is stable** across an order of
magnitude — nothing buffers the whole report. A representative CSV run:

| RowCount  | Allocated | Per row |
|-----------|-----------|---------|
| 100,000   | ~42.6 MB  | ~446 B  |
| 1,000,000 | ~440 MB   | ~461 B  |

~446 B/row vs ~461 B/row at 10× the volume ⇒ constant per-row cost. The Gen0/Gen1
collections during the run confirm each page's buffers are recycled rather than
accumulated. If anything materialized the full report, the per-row figure (and the
working set) would climb with `RowCount`.

The per-row allocation is the unavoidable boxing of each cell into `object?[]` at the
writer edge (4 columns × one box each), plus the row array — this is by design
(see architecture rule 3, projection only at the writer edge).

## Concurrency (many reports at once)

`ConcurrencyMemoryBenchmark` runs `Concurrency` reports (1 / 8 / 32) of 1,000,000 rows **at the same
time** over the streaming CSV path:

```bash
dotnet run -c Release --project benchmarks/NeoReports.Benchmarks -- --filter '*Concurrent*'
```

Read it the same way: **Allocated** should scale ~linearly with `Concurrency` (running many at once
adds no super-linear cost), and peak live memory stays bounded by ≈ `Concurrency × pageSize` because
each report holds only one page at a time. The deterministic correctness/isolation side of this —
independent runs, per-job temp dirs, independent cancellation, page-by-page reads — is covered by
`ConcurrencyTests` in `NeoReports.Core.UnitTests` (run in CI).

## CSV vs XLSX

- **CSV is fully streaming**: rows are formatted and flushed to the output stream
  page by page; working set is O(pageSize).
- **XLSX (ClosedXML)** builds the entire workbook in memory before saving, so its
  allocation grows with the row count by design — a conscious trade-off recorded as
  **ADR D14**. The XLSX benchmark is included for contrast; for very large reports,
  prefer CSV. (Running the XLSX 1M case needs several GB of RAM.)
