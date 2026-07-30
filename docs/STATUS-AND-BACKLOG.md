# Status & backlog — durable handoff

A version-controlled record of the enterprise-readiness audit and the work that is **still open**,
so it can be picked up in any future session or by any maintainer without relying on machine-local
notes. Update this file whenever a deferred item is done or a new one is deferred.

Last updated: **2026-07-30**.

---

## Done — enterprise-readiness audit (16 PRs, #216–#231)

A full-project audit (2026-07-30) mapped findings across security, correctness, performance,
enterprise-readiness and test coverage, and shipped everything actionable.

### Audit work packages (WP1–WP10, PRs #216–#225)
- **WP1** keyset cursor encoded type-faithfully — `Convert.ToString` corrupted `DateTime` (dropped
  sub-second → duplicate rows) and `byte[]`/`rowversion` (→ `"System.Byte[]"`) keys.
- **WP2** blocked path traversal in the Local destination via run-time `{param}` values.
- **WP3** a failed upload now fails the run instead of reporting success.
- **WP4** run-time parameters override same-named static ones; `@name` matched on an identifier boundary.
- **WP5** health/sync-run error responses scrubbed of DB host/topology (logged server-side instead).
- **WP6** opt-in per-attempt read timeout (`RetryOptions.Timeout`).
- **WP7** `ILogger` `BeginScope{JobId,ReportName}` + failure logging (abort/retry/upload).
- **WP8** multi-artifact zip downloads stream via a `0600` temp file, not a `MemoryStream`.
- **WP9** `AddNeoReportsStartupValidation()` compiles config reports at boot (fail fast).
- **WP10** behavioural tests for the Total/Rate/consecutive-reset abort thresholds.

### Maintainer-decision recommendations (#1–#6, PRs #226–#231)
- **#1 Streaming XLSX (#226)** — both writers rebuilt on `DocumentFormat.OpenXml` SAX + a
  hand-assembled `ZipArchive` (Create mode) written straight to the output; `System.IO.Packaging`
  deliberately bypassed (its `ZipPackage` buffers each part in RAM). Constant memory **proven by
  measurement** (~1.5 MB flat writing 100k→2.4M rows); a regression test enforces it. ClosedXML
  removed from both writer packages. Resolves **D14**. Only behavioural change: dropped column
  auto-fit (can't stream).
- **#2 Auth startup warning (#227)** — `MapNeoReports()` warns when neither host auth nor
  `RequireAuthorization` is configured. Default unchanged (D20: auth inherits from the host).
- **#3 Removed dead ABI exceptions (#228)** — `BatchFailedException`, `SourceFailedException`,
  `ThresholdExceededException` were never thrown; removed (maintainer decision). **Breaking, next
  major** — see `CHANGELOG.md` → Unreleased → Removed.
- **#4 Retry default (#229)** — parameterless `ReportBuilder<T>.Retry()` (3 attempts, exponential,
  jitter); default stays off; docs flag the recommendation.
- **#5 Whole-job deadline + error scrub (#230)** — `ReportBuilder<T>.Deadline(TimeSpan)` bounds the
  whole run; and the persisted run error / `RunFailed` & `Retry` events / worker catch now carry a
  driver exception's **type name** not its message (NeoReports' own curated messages are kept).
- **#6 Drain-loop caps (#231)** — the 17 HTTP-family test drain loops now fail fast past 1000 pages
  (guard against the ~22 GB-testhost runaway shape).

---

## Open backlog — deferred, with rationale

### 1. Next-major breaking cleanup (needs a 2.0 line)
- **Remove the never-thrown ABI exceptions** — already done in #228, tagged for the next major.
- **CA1068: `CancellationToken` not last** in three **public** health signatures — **done**: the
  token was moved to last (after `pingSql`/`content`, both defaulted) in `AdoSourceHealth.PingAsync`,
  `AdoSourceHealth.CheckConnectionStringAsync` and `HttpHealthProbe.SendAsync`, and all callers
  updated. Source-breaking for positional callers, so tagged **next-major** in `CHANGELOG.md`
  (Changed → breaking, public API) alongside the #228 removal.

### 2. CI hardening
- **Fail (not skip) the Testcontainers integration tests when Docker is absent in CI.** — **done**:
  the five container `ServerFixture`s now swallow a start failure only through an exception filter,
  `catch (Exception) when (DockerGate.SkipWhenUnavailable)`. `DockerGate` (a file linked into all
  five integration projects, `tests/Shared/DockerGate.cs`) skips unless `NEOREPORTS_REQUIRE_DOCKER=1`,
  which the CI/Sonar/release workflows set — so a broken image or Docker outage on the runner
  hard-fails instead of silently degrading to "all skipped", while local `dotnet test` (var unset)
  keeps skipping. Covered by `DockerGateTests`.

### 3. Pre-existing CodeQL alerts — **done**
- The ~30 open repository code-scanning alerts predating the audit were cleared: 21 fixed (auto-closed
  on the master CodeQL run) and 11 dismissed as false-positive / deliberate. Repo-level open count is
  now **0**. (Resolving a PR thread does **not** close a repo-level alert, so each was handled via
  `PATCH /repos/thiagoluga/NeoReports/code-scanning/alerts/{number}` or a real fix.)

### 4. Pro packaging (Epic Q3b/c) — blocked on the maintainer
- The three Pro packages are enforced (Q1/Q2) and an issuing tool exists (Q3a), but publishing is
  **blocked** on rotating the embedded placeholder public key: the maintainer must run the license
  tool's `keygen` **locally**, store the private half in a vault, and commit only the new public
  key. The key generated during development is compromised (it appeared in a chat transcript) and
  must never be the production signing key.

### 5. Follow-up bug-hunt findings (2026-07-30)
A focused review of the keyset/cursor and resilience/failure paths surfaced these. One is fixed; the
rest are recorded with a concrete repro because each needs a design decision or a fix that isn't
locally verifiable.
- **Oracle temporal keyset cursor crashed on page 2 — FIXED (PR pending/merged).** The Pro
  `QueryBuilder`'s `SqlDialect.OracleCast` emitted no cast for `Date`/`DateTime`/`Timestamp` keys, so
  the ISO-8601 cursor was implicit-converted by Oracle's `NLS_DATE_FORMAT` → `ORA-01858` on the
  second page. Now casts with the codec's documented `TO_TIMESTAMP(:cursor,
  'YYYY-MM-DD"T"HH24:MI:SS.FF7')`. Unit test asserts the emitted SQL; an Oracle integration test
  (`A_timestamp_keyset_cursor_round_trips_across_pages`) empirically validates the model.
- **Postgres/Redshift `timestamptz` keyset boundary can shift under a non-UTC session.** `PostgresCast`
  casts the cursor to `::timestamp` (no zone). For a `timestamptz` key that discards the offset and
  re-interprets it in the session `TimeZone`, silently skipping or duplicating a window of rows.
  Naïvely switching to `::timestamptz` just moves the bug to plain `timestamp` keys — `ColumnType`
  doesn't distinguish the two. **Needs** the catalog to carry the with/without-time-zone distinction
  (a design change). Same class as the Oracle `TIMESTAMP WITH TIME ZONE` sub-case (the FF7 model has
  no `TZH:TZM`). Workaround today: key on a plain `timestamp`/UTC column, or run the session in UTC.
- **QueryBuilder allows a non-unique keyset key.** Single-column keyset with strict `>` requires a
  unique, monotonic key; if the user picks a non-unique column, the tail of a duplicate group that
  straddles a page boundary is dropped. Not statically detectable (no PK/unique metadata in the
  model). Consider a builder warning when the key isn't a PK, or documenting the requirement more
  loudly. Needs a product decision.
- **Multi-output batch writes are not atomic (contradicts D11 batch-atomicity).** `ReportRunner`
  writes each batch to every output in a sequential loop with no per-batch buffer/transaction; if
  output *k* throws after output *k-1* already appended, a `SkipBatchAndLog` batch is "skipped" yet
  physically present in the earlier output's file, and an abort leaves a torn batch across outputs.
  Delivered files for "the same report" can diverge and the stats won't match the bytes. A real fix
  (buffer each batch per output, commit all-or-nothing) is a write-path change that should be a
  recorded decision. Only exercised with ≥2 outputs and a real writer (the single-output
  `FakeWriterFactory` tests don't hit it).
- **`FailureRate` threshold has no minimum-sample guard.** The ratio is `totalFailures / batchesSoFar`
  with the current failing batch already counted, so an early failure degenerates: the first failing
  batch yields `1/k` and any `FailureRate` below that aborts immediately (e.g. `FailureRate: 0.5`
  aborts if either of the first two batches fails). The arithmetic matches the documented definition,
  so this is a semantics choice — consider only evaluating the ratio after N batches if the intent is
  "fraction over a large run." Needs a decision.

A second hunt over the CSV/XLSX writers (output correctness) found these. The file-breaking / total-loss
ones are **fixed** (PR pending/merged); two representation tradeoffs are recorded.
- **XLSX cell edge cases — FIXED.** In `XlsxCells.BuildCell` (shared by the MIT and Pro writers):
  (a) an XML-illegal C0 control char in a string threw and aborted the **whole** file — now stripped;
  (b) `NaN`/`Infinity` produced an invalid number cell Excel refused to open — now emitted as text;
  (c) `byte[]` stringified to `"System.Byte[]"` — now Base64 (also fixed in the CSV writer);
  (d) `TimeOnly` used the machine's current culture — now an invariant round-trip string. CSV's
  RFC-4180 escaping and invariant number/date formatting were verified **correct**.
- **XLSX loses precision for 64-bit ints / high-precision decimals (deferred — representation
  tradeoff).** `long`/`ulong`/`decimal` are funneled through `Convert.ToDouble`, so a value beyond
  2^53 (a `bigint` key) or a `decimal` past double's ~15–17 digits is silently rounded. Excel stores
  numbers as IEEE-754 doubles, so preserving the exact value **requires** writing it as text — which
  loses Excel's numeric sorting/formatting. That number-vs-text tradeoff is a product decision, so it
  is left as-is with the value rounded (today's behaviour) pending a call.
- **XLSX `DateTimeOffset` drops the offset and pre-1900 dates misrender (deferred — narrow).** `dto`
  is stored via `dto.DateTime` (offset discarded) and `DateTime.ToOADate()` can't represent dates
  before 1899-12-30. Both are inherent to the OADate/no-tz cell model; revisit only if a real report
  needs sub-day-offset fidelity or pre-1900 dates.

---

## Where the fuller context lives
Machine-local agent memory under the session's `memory/` directory holds the deeper running notes
(`enterprise-audit-2026-07.md`, `roadmap-state.md`, the SonarCloud/CI gotchas). This file is the
portable, always-available subset. Keep it in sync when the backlog changes.
