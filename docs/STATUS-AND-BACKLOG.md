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
- **Fail (not skip) the Testcontainers integration tests when Docker is absent in CI.** They are
  `[SkippableFact]` + `Skip.IfNot(fixture.Available, …)` today, which is correct for local dev (a
  contributor without Docker can still `dotnet test`), but means a broken container image silently
  degrades to "all skipped" and CI stays green. Fix: gate on a CI-only env var (e.g.
  `NEOREPORTS_REQUIRE_DOCKER=1` in the workflow) so the fixture throws instead of setting
  `Available=false` when the var is set. Do **not** make it a hard fail unconditionally.

### 3. Pre-existing CodeQL alerts
- **~30 open repository code-scanning alerts** predating the audit (e.g. `cs/path-combine` in
  `Sources.Xlsx.UnitTests`, `cs/local-not-disposed` / `cs/catch-of-all-exceptions` in
  `Sources.Files.Common`, `cs/linq/missed-where` in `Core/Sources/ReflectedRowShape.cs`). Each needs
  either a real fix or an explicit dismissal via
  `PATCH /repos/thiagoluga/NeoReports/code-scanning/alerts/{number}` with a `dismissed_reason`
  (resolving a PR thread does **not** close a repo-level alert). None touch the audit's files.

### 4. Pro packaging (Epic Q3b/c) — blocked on the maintainer
- The three Pro packages are enforced (Q1/Q2) and an issuing tool exists (Q3a), but publishing is
  **blocked** on rotating the embedded placeholder public key: the maintainer must run the license
  tool's `keygen` **locally**, store the private half in a vault, and commit only the new public
  key. The key generated during development is compromised (it appeared in a chat transcript) and
  must never be the production signing key.

---

## Where the fuller context lives
Machine-local agent memory under the session's `memory/` directory holds the deeper running notes
(`enterprise-audit-2026-07.md`, `roadmap-state.md`, the SonarCloud/CI gotchas). This file is the
portable, always-available subset. Keep it in sync when the backlog changes.
