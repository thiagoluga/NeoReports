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

A third hunt covered the destination (upload) and job/scheduling layers. Two fixes shipped; the rest
need a decision.
- **Job/schedule robustness — FIXED.** (a) The worker's unfiltered `catch (OperationCanceledException)`
  recorded an `HttpClient.Timeout` (`TaskCanceledException`, foreign token) as "Cancelled." and did
  not rethrow, so a genuine failure looked operator-initiated and Hangfire saw success — now filtered
  on the run's own token. Because a **deadline** also cancels through a linked token (the run's own
  token is not cancelled either), the runner now reports it as `ReportDeadlineExceededException` — an
  `OperationCanceledException` subclass — so the worker keeps recording a deadline as `Cancelled`
  (now with a reason saying so) while everything else becomes `Failed`. Both directions are covered by
  regression tests, each verified to fail without its fix. (b) `FileScheduleOverrideStore` **and**
  `FileReportConfigStore` staged every save through a fixed `{name}.json.tmp`, so concurrent saves for
  one name collided — both now use the shared `AtomicFileWrite` (unique temp name, deleted if the save
  fails). Verified correct in the same pass: `InMemoryJobStore` thread-safety,
  `EffectiveSchedule.Resolve` (override/tombstone/fallback), `ScheduleReconciliationHostedService`
  (add/update/remove, no duplicate), `CronValidation` (UTC, Cronos, no off-by-one), and the whole
  Local/S3 upload path for stream position, disposal, failure mapping and atomicity.
- **⚠️ S3 key templating does not guard caller-controlled parameters (highest open security item).**
  `LocalDestination` passes `LocalPathSegment.EnsureSafe` to `PathTemplate.Expand` (the WP2 guard);
  `S3Destination` passes **none** — deliberately, since `/` is a legitimate key separator. But that
  reasoning covers the author's template, not `{param}` values, which come from the run request body.
  With a key template like `reports/{tenant}/{name}.{ext}`, a caller posting `tenant = "other"` (or a
  value containing `/`) steers the object into another prefix — a **cross-tenant write** where a
  shared bucket relies on prefix isolation. Not an OS traversal (S3 keys are literal, `..` is not
  collapsed) and harmless in a single-tenant bucket. The fix is a decision because the safe version
  (reject `/` in substituted **values** while keeping it in template literals) would break anyone
  intentionally passing a hierarchy fragment as a parameter. **Recommended:** adopt that guard and
  note it as breaking, or document that S3 key templates must not interpolate untrusted parameters.
- **Upload swallows `OperationCanceledException` into a `Fail` result (deferred — semantics).** Both
  destinations' `catch (Exception)` also catch a cancellation, so a deadline firing mid-upload is
  reported as a destination error rather than a cancellation. The run still ends Failed, so this is
  attribution accuracy; rethrowing would also change multi-destination behaviour (today the loop
  continues and reports per-destination results).
- **Hangfire applies its default 10-attempt `AutomaticRetry` (deferred — decision).** The invoker
  carries no `[AutomaticRetry(Attempts = 0)]` and nothing configures `GlobalJobFilters`, so a
  deterministically failing job (bad credentials, unreachable source) is re-run up to 10× — re-reading
  the whole dataset each time and flapping the stored status Failed→Running→Failed. Output integrity
  holds (temp-dir staging is idempotent), but it contradicts the "a job is atomic, one attempt"
  model (rule 6). Decide whether NeoReports should pin `Attempts = 0` or leave retries to the host.
- **`InMemoryJobScheduler.RegisterRecurringAsync` remove-then-add isn't atomic (deferred — narrow).**
  Two concurrent registrations for one report can both start a loop; the loser is overwritten in the
  dictionary without its CTS being cancelled, so it keeps firing untracked for the process lifetime.
  Reachable only by racing two schedule updates (or one against startup reconciliation); the Hangfire
  path is safe (`AddOrUpdate` is idempotent). A lock around register/remove would fix it.
- **The in-memory recurring loop has no catch-all (deferred — narrow).** Any non-cancellation throw
  faults the fire-and-forget loop and the schedule silently stops for the process lifetime, unlogged.
- **`CompletedPartial` surfaces as a `Completed` job (by design, flagged).** A run that skipped
  batches maps to `ReportJobStatus.Completed`; the skip is visible only in `Stats.SkippedBatches`.
  There is no `Partial` job status. Worth confirming this is still the intent, since silent partial
  data reads as a green job.

### 6. Source-pagination and API findings (2026-08-03) — deferred, each needs a decision
Two more hunts (the nine HTTP-family source packages; the AspNetCore endpoint layer) produced these.
The unambiguous ones shipped — the engine's reserved `cursor`/`pageSize` bind names, and run-time
parameters arriving as `JsonElement`. What is left changes behaviour or semantics, so it is recorded
rather than decided:

- ~~**The page loop has no safety net.**~~ **DECIDED AND FIXED (ADR D72).** The maintainer chose a
  non-advancing-cursor guard with **no** page cap: it catches a source making no progress without
  imposing a ceiling a legitimately huge report could hit. The check runs after the batch is written,
  so the last readable page is not discarded. Enforcing it exposed that `StreamingToBatchSource`
  emitted a constant cursor — every file-backed source would have failed at page 2 — so the adapter
  now emits its page count rather than the runner special-casing a sentinel. Original description:
  no page cap, no "the cursor did not change" guard, no "zero rows but still more" guard.
- ~~**`records.Count == pageSize` ends a run early when the server caps the page.**~~ **FIXED
  (ADR D72)** — `Skip`/`Page`/`Offset` now page until a response comes back **empty**, so this class
  of truncation is structurally impossible rather than merely unlikely. Costs one extra request per
  run. Original description: OData's `Skip`
  strategy (`ODataBatchSource`) and the HTTP source's `Page`/`Offset` strategies infer "more data"
  from a full page. Services that clamp `$top`/`limit` below the engine's 1000 default (Dynamics, SAP
  Gateway, Business Central; many REST APIs silently reduce an over-max `limit`) return a short first
  page → the run stops there and reports **`Completed`** with partial data. `Skip` is opt-in and
  `NextLink` is the default, which limits blast radius. A fix means either honouring `@odata.nextLink`
  in `Skip` mode too, or paging until a page returns zero rows — both change termination semantics.
- ~~**Elasticsearch treats a partially-failed search as a short page.**~~ **FIXED (ADR D72)** — both
  fields are inspected before the hits are read, and a partial search now fails loudly. Original
  description: ES returns **HTTP 200** with
  `timed_out: true` / `_shards.failed > 0` and fewer hits; neither field is inspected, so the report
  silently ends early as `Completed`. GraphQL already fails loudly on 200-with-`errors`; the ES
  equivalent would be consistent, but it turns today's silent success into a hard failure.
- ~~**HTTP `Cursor` strategy has no non-advancing-cursor guard.**~~ **FIXED** — an unchanged token
  now throws with a message naming the configured cursor path, matching GraphQL (D63) and
  Elasticsearch. Original description: If an API echoes the requested cursor
  on the last page (Facebook Graph's `paging.cursors.after`, among others), `hasMore` stays true with
  an identical token → the same request repeats **forever**.
- ~~**`Link`-header parsing breaks on a comma inside the URL.**~~ **FIXED** — the header is now split
  by a scanner that tracks `<...>` and quoted strings, so a comma inside either no longer ends the
  link-value. Original description: `HttpBatchSource` splits the header on
  `,` unconditionally, but RFC 8288 permits commas in the target URI and in quoted parameters. A base
  URL like `?fields=id,name` echoed into the next-page link makes `rel="next"` unparseable → paging
  stops silently after page 1.
- ~~**A relative next-page URL throws.**~~ **FIXED** — a shared `HttpNextPage.Resolve` (Http.Common)
  performs RFC 3986 resolution against the URL the response came from, and both sources store the
  resolved `AbsoluteUri` in the cursor, so the same-origin guard still inspects the real target. Note
  this is deliberately *not* the concatenation `HttpHealthProbe` needs: a health path is a sub-path to
  append, a next-page link is a URI reference to resolve — the same-looking call with a different
  contract, which is why the two must not be unified. Original description: Both `HttpBatchSource` and
  `ODataBatchSource` call `new Uri(nextUrl)` (absolute-only) on a server-supplied link; RFC 8288 and
  OData both permit a relative one.
- ~~**`HttpHealthProbe.CombineUrl` still has the relative-`Uri` bug — the 5th sighting of this class.**~~
  **FIXED** — the shared helper now concatenates under the base path (absolute `http(s)` paths still
  used as given), matching the four leaf packages; covered by `HttpHealthProbeUrlTests`, verified to
  fail against the old implementation. Original description:
  `new Uri(baseUri, path)` drops the base's last path segment when it has no trailing slash, and a
  leading `/` resets to the host root. Elasticsearch (D64), HubSpot, Airtable and Salesforce each
  independently rewrote away from it — with comments naming it — but the shared helper still does it,
  and `HttpSourceHealthCheck` + `ODataSourceHealthCheck` still call it: a health check can probe the
  wrong URL and report a healthy source unhealthy (or vice-versa). **Fixing the shared helper by
  concatenation, as the four leaf packages already do, is the cheapest win in this list.**
- ~~**Google Sheets: three data-fidelity bugs.**~~ **ALL THREE FIXED.** (a) header cells are decoded
  exactly like data cells, so a numeric/boolean header indexes its column. (b) a header row that names
  no columns now throws instead of yielding N rows of type defaults reported as success. (c) an
  interior blank row (`[]`) is no longer materialized as a phantom record — and, importantly,
  `hasMore` was moved onto *rows the API returned* rather than records kept, so dropping them narrows
  D66's blank-run gap instead of widening it (a window of only blank rows would otherwise have looked
  like exhaustion). Original description: (a) header cells that aren't JSON strings are dropped,
  so a year-numbered column (`2024`) never binds and every row's value for it is null/zero — the
  requests use `UNFORMATTED_VALUE`, which returns numeric headers as JSON numbers, while the data path
  already decodes all kinds; (b) a header range that comes back without `values` caches an **empty**
  index, so a misconfigured `headerRow` produces N rows of all-nulls reported as success instead of
  failing loudly; (c) an interior blank row is returned as `[]` and materialized as a phantom
  all-default row. (a) is the clearest and most contained.
- ~~**HubSpot and Airtable default to a page size their API rejects.**~~ **DECIDED AND FIXED
  (ADR D72)** — the maintainer chose **clamping**: an author should not need to know each provider's
  ceiling. Safe because both derive `hasMore` from the server's continuation token, so clamping only
  means more requests. Original description: Both send the engine's 1000
  default as `limit`/`pageSize`, but both providers cap at 100, so a
  source built with defaults fails its very first request until the author calls `.PageSize(100)`.
- ~~**API: `POST /reports/{name}/preview` is the one data-plane endpoint that doesn't scrub driver
  exceptions.**~~ **FIXED** (routes through `SchemaProblem` like its siblings). Original description: It catches only `ConfigurationException`, so a bad filter value surfaces the raw
  `SqlException`/`PostgresException` (host, port, database) as a 500 — its siblings all route through
  `SchemaProblem`. Should be a 400 (bad filter) or the scrubbed 502 the others return.
- ~~**API: schedule/preview write paths reach a name-validating store without the guard.**~~
  **FIXED** — the preview half first (a name no config store can hold is treated as not-dynamic, so
  the endpoint returns its intended 400), and now `SetScheduleAsync`/`ClearScheduleAsync` too: both
  answer **409 Conflict** naming the pattern, matching the "this host cannot do that" response
  already next to them, instead of an `ArgumentException` 500. Original description:
  `SetScheduleAsync`, `ClearScheduleAsync` and `ReportPreviewRunner`'s config-store probe pass the
  report name straight through, so a legitimate **code-first** report whose name is outside
  `^[a-zA-Z][a-zA-Z0-9_-]{0,99}$` (e.g. `sales.daily`) gets a **500** from `ArgumentException`. The
  read paths already guard, which shows it is an oversight.
- ~~**API: `GET /jobs/{id}` returns raw destination-exception text**~~ **FIXED** — the runner now
  persists (and emits as the `UploadFailed` event) only the file name and destination type, keeping the
  destination's own wording in the log. Scrubbing at the runner rather than in each destination is what
  also covers third-party `IDestination` implementations. `GET /jobs/{id}/events` was a second route
  out for the same string and is covered too. Original description: (server paths, S3 bucket + key, AWS
  error strings). The read- and write-failure paths in the same method scrub; the **upload** path does
  not — and the sync endpoint deliberately suppresses the very same string, so one route hides what
  the other returns verbatim.
- ~~**API: sync mode's single-output guard ignores sectioned outputs.**~~ **FIXED** — `OutputCount`
  and `OutputFormats` now include sectioned outputs, so the guard rejects the mixed report it always
  claimed to and the listing reports every format. Original description: `OutputCount` counts only
  `Outputs`, so a report with one plain and one sectioned output passes the guard, the runner writes
  two artifacts, and the caller silently receives **one** — which one decided by directory-enumeration
  order. The same undercount makes `GET /reports` under-report a sectioned report's formats.
- ~~**API: `Location`/`Content-Location` headers hardcode `/api`**~~ **FIXED** — a group-level
  endpoint filter puts the mapped prefix on the request, and the three `Created`/`Accepted` sites build
  their URL from it. The test follows the returned `Location` under `MapNeoReports("/v2")` rather than
  string-matching it, so a well-formed-but-wrong header still fails. Original description: ignoring
  `MapNeoReports`'s configurable prefix — under `MapNeoReports("/v2")` the 202's `Location` is a 404
  for any client that follows it.
- ~~**Array/object run parameters still diverge by backend.**~~ **FIXED (ADR D72)** — the run endpoint
  answers 400 naming the parameter, so the documented limit is real and identical on every backend.
  Scalars are unaffected, `null` included. Not applied to source property bags: those are a provider's
  configuration surface, not a value bound into a query. Original description: sync/in-memory hand the
  source a `JsonElement` (the very thing an ADO provider can't bind) while Hangfire hands it the raw
  JSON text.
- ~~**`POST/PUT /sources` property bags are not normalized.**~~ **FIXED** — both handlers now run the
  bag through the same normalizer the run endpoint uses (renamed `NormalizeJsonValues`, since it serves
  two request shapes). Original description: `SourceRequest.Properties` is the same
  caller-supplied `object?` bag as run parameters. `FileSourceRegistryStore` launders it on write and
  read, but `InMemorySourceRegistryStore` stores it as-is — so with `AddInMemorySourceRegistry()` a
  source created over HTTP fails later with *"requires a non-empty 'connectionString' property"*
  because the value is a `JsonElement`, not a `string`.

Verified correct in the same pass (worth not re-auditing): artifact download path handling (no
caller-supplied filename reaches disk; no zip-slip; `0600` temp), job-id handling, SQL-injection
surface via run parameters, preview filter validation, list-endpoint pagination clamping, cursor
round-tripping in all nine source packages (`OpaqueCursor` is Base64-JSON, lossless), GraphQL /
HubSpot / Airtable / Salesforce termination logic, and the Elasticsearch "capture the last sort"
loop.

---

## Where the fuller context lives
Machine-local agent memory under the session's `memory/` directory holds the deeper running notes
(`enterprise-audit-2026-07.md`, `roadmap-state.md`, the SonarCloud/CI gotchas). This file is the
portable, always-available subset. Keep it in sync when the backlog changes.
