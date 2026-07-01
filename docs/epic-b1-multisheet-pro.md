# Epic B1 — Multi-sheet XLSX (first "Pro" feature)

> **Status: design, not yet built.** This is the blueprint to approve before any code or
> commercial-license file is created. Decisions land in `DECISIONS.md` (D22 updated, D27 new).

## Goal

One `.xlsx` workbook with **several named sheets**, each fed by a different **filter** over the same
report source (e.g. an "Approved" sheet and a "Rejected" sheet from one dataset). Different *sources*
per sheet is **out of B1** (that is B2 / multi-source).

This ships as the first **paid "Pro" feature**, in a **separate package** with a commercial license,
leaving the OSS core MIT and untouched in spirit.

## Why it does not fit the v1 model as-is

Today: a report has **one** schema and **one** filter set; the pipeline projects each row to
`object?[]` **once** and fans it out to N single-sheet writers (each output = one file = one sheet).

Multi-sheet inverts part of that: **one** output (a workbook) with **N sheets**, where each sheet has
its **own** filter and columns. A single projection can't produce per-sheet columns, and writers are
non-generic (`object?[]` + schema), so they can't apply a per-sheet filter on the typed row. So we
need per-sheet `(filter, columns)` captured where the typed `T` is still available (the builder), and
a writer that can target a **named sheet** within a shared workbook.

## Recommended architecture (single pass preserved)

A **multi-section output**: instead of one `(filter, columns)`, an output may carry a list of
**sections**, each `(name, optional filter, columns)`. The source is still read **once**; for each
batch, every row is offered to each section (the section filter decides inclusion, the section columns
project to that section's `object?[]` + schema), and rows are written to the matching sheet.

**OSS / Pro split (minimal OSS surface):**

- **OSS Core (MIT, additive — D25):** a small, generic hook so an output can declare multiple sections
  and the pipeline can drive per-section projection + a section-aware write. Generic, format-agnostic,
  reusable. This is the only change to the open engine.
- **`NeoReports.Xlsx.Pro` (commercial — D27):** the **XLSX workbook writer** (ClosedXML, one workbook,
  one named sheet per section, native types/auto-filter per sheet) **and** the ergonomic fluent API.
  This is the polished, supported implementation — the value customers pay for.

The open engine never references the Pro package; Pro plugs in through the existing extensibility
(`IWriterFactory` + the new multi-section hook). Anyone *could* hand-roll multi-section against the OSS
hook; the Pro package is the batteries-included, licensed, supported XLSX writer.

## API sketch (from the Pro package)

```csharp
using static NeoReports.Xlsx.Pro.Format;   // Pro entry point

builder
    .From(Source.Sql(...).Keyset<Sale, long>(v => v.Id))
    .To(XlsxWorkbook(wb => wb
        .Sheet("Approved", s => s
            .Filter(v => v.Amount > 0)
            .Column(v => v.Id, "Sale ID")
            .Column(v => v.Amount, "Amount", format: "C2", culture: "pt-BR"))
        .Sheet("Rejected", s => s
            .Filter(v => v.Amount <= 0)
            .Column(v => v.Id, "Sale ID"))));
```

- Dynamic (config) path: `"outputs": [ { "format": "xlsx-workbook", "properties": { "sheets": [ ... ] } } ]`
  — a later step once the typed API is settled.

## Resolved sub-decisions (maintainer, 2026-07-01)

1. **Package name** — `NeoReports.Xlsx.Pro`.
2. **License model** — **open-core** (forced by the already-MIT core): core stays MIT; the Pro package
   is **source-available with Option A (QuestPDF-style)**: free for companies under **USD 1M** annual
   revenue, paid above. Use **PolyForm Small Business 1.0.0** (fetch the canonical verbatim text at
   B1.2). Commercial sales terms are the maintainer's/lawyer's.
3. **License enforcement** — **none for now** (contractual/honor-system), like QuestPDF; a key gate can
   come later.
4. **OSS/Pro boundary** — the generic **multi-view hook is MIT** (each output → its own file). The Pro
   value is specifically the single-workbook writer that packs views as **sheets in one `.xlsx`**.
5. **CSV / single-table with several views in one file** — **reject with a clear error** in v1; the OSS
   path already gives one file per view.

### Reframe: "multi-view" (from the 2.5 clarification)

At save time a report can define several **views**, each with its own **filter and/or columns** over
the single source read once. **OSS/free (B1.1, done):** each view → its **own file** (separate
CSV/XLSX with distinct filter/columns from one read). **Pro/paid (B1.2):** the writer that packs
several views as **sheets in one workbook**. Per-view *columns* are supported; per-view *different
sources* is multi-source (B2), which reuses the workbook writer.

## Implementation PR breakdown (after this design is approved)

- **B1.1 — OSS multi-section hook (Core/Abstractions, MIT):** output carries optional sections; pipeline
  drives per-section projection + section-aware write; single-pass preserved. Tests with a fake
  section writer.
- **B1.2 — `NeoReports.Xlsx.Pro` package skeleton (commercial):** project, placeholder commercial
  LICENSE + metadata (not MIT, not auto-published with the OSS packages), fluent `XlsxWorkbook(...)`
  API, ClosedXML workbook writer. Golden-file test (one workbook, N sheets).
- **B1.3 — Packaging & CI:** the Pro package builds and packs but is **excluded** from the OSS NuGet
  release flow (separate/none until the maintainer decides distribution). Docs.
- **B1.4 — Sample:** `06-multi-sheet-xlsx` (typed) demonstrating Approved/Rejected sheets.
- **B1.5 — Dynamic config support** for `xlsx-workbook` (optional, after the typed API settles).

Each PR small, green tests, one at a time — same workflow as Epic A.
