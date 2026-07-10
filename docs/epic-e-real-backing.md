# Epic E — Real backing for the removed UI content (telemetry, scheduling, partial output)

> **Status: approved blueprint, not yet built.** Scope authorized by the maintainer (ADRs
> **D37–D41**, 2026-07). One PR per task, in order — E2 is the foundation for E3; everything else
> is independent. The source registry (the sixth candidate from
> `docs/ui-removed-mock-content.md`) is **Epic F**, with its own blueprint
> (`docs/epic-f-source-registry.md`) — it is a new domain concept and deliberately kept apart.
>
> This document is written to be executable by an implementation session without extra context.
> When something here conflicts with the code you find, **stop and re-read the code first** — the
> code wins, then update this doc in the same PR.

## Goal

Give real engine backing to five of the six mock-content items catalogued in
`docs/ui-removed-mock-content.md` (D36): abort thresholds in the dynamic path, a per-job event
log (timeline + retry detail + processing-rate history), a process-level Memory screen, partial
output for failed/cancelled jobs, and recurring scheduling. Each removed UI card comes back only
when the data behind it is real; honest empty states everywhere (D36's standing rule).

## Ground rules (apply to every task)

1. **`NeoReports.Abstractions` changes only via additive trailing optional record parameters**
   (the D34 pattern). This epic makes exactly two: `ResilienceConfig.AbortWhen` (E1) and
   `ReportConfig.Schedule` (E6). **No interface in `Abstractions` gains a member** — adding one
   breaks every external implementer. All new contracts live in Core (the D20/D33 precedent:
   `IReportArtifactStore`, `IMutableReportRegistry`).
2. **GET responses never echo property bags** (D33). The only user-adjacent text in new responses
   is exception messages inside job events — truncated (500 chars) and newline-sanitized (the
   existing log-forging helper pattern).
3. **Telemetry must never change a run's outcome.** Event appends, partial-artifact copies, and
   schedule bookkeeping are best-effort: failures are logged and swallowed.
4. **No fabricated fallback content** (D36). Every returning UI card has an explicit honest
   empty/unavailable state, specified per task below.
5. Conventions as usual: file-scoped namespaces, `sealed`, `record` DTOs, async with
   `CancellationToken` last, XML docs on public surface, new endpoints inside the existing
   `MapNeoReports` group (auth inherited), tests in the matching `*.UnitTests` /
   `*.IntegrationTests` project, new dependencies via CPM only.

---

## E1 — Abort thresholds as config (D37)

**Goal:** the dynamic path can express what `FailureStrategyBuilder.AbortIf` already does in
code, using the closed vocabulary `ThresholdContext` exposes. Per-exception-type retry filtering
is **rejected** (D37) — do not implement any part of it.

### E1.1 Abstractions (additive)

In `src/NeoReports.Abstractions/Configuration.cs`:

```csharp
/// <summary>Threshold-based escalation from skip-and-log to abort (OR semantics; at least one field set).</summary>
public sealed record AbortThresholdConfig(
    int? ConsecutiveFailures = null,
    int? TotalFailures = null,
    double? FailureRate = null);
```

`ResilienceConfig` gains a trailing optional `AbortWhen` (`AbortThresholdConfig?`, default null).

### E1.2 Core

- New data type `AbortThresholds` is **not** needed — reuse `AbortThresholdConfig` from
  Abstractions in Core signatures (it is already a plain record there).
- `FailureStrategyBuilder` gains a data-based overload `AbortIf(AbortThresholdConfig thresholds)`
  which compiles to the same internal predicate (`t => t.ConsecutiveFailures(n) || ...`) and
  **records the config** so it is introspectable. The existing `Func<ThresholdContext,bool>`
  overload stays; when it is used, introspection reports "custom".
- `CompiledReport` gains public `AbortThresholdConfig? AbortThresholds` (null = none or custom
  predicate).
- `ReportConfigCompiler` maps `Resilience.AbortWhen`:
  - Legal **only** with `OnFailure: "skip-and-log"` — with `"abort"` (or omitted `OnFailure`
    defaulting to abort) throw `ConfigurationException` ("AbortWhen requires skip-and-log").
  - Validation: at least one field set; counts >= 1; rate in (0, 1]. Violations throw
    `ConfigurationException`.
- `JsonReportConfigParser`: parse the nested object; unknown fields inside it are an error
  (consistent with the parser's existing strictness).

### E1.3 AspNetCore

`ReportDetailView` gains `int? AbortAfterConsecutiveFailures`, `int? AbortAfterTotalFailures`,
`double? AbortAtFailureRate` (all null when absent or custom). No new endpoints.

### E1.4 UI

- Builder step 2 Resilience card: the "Abort when" switches return — three toggles, each with a
  numeric input, enabled only when On failure = skip-and-log; mapped by `BuilderConfigMapper`
  into `resilience.abortWhen`. The "Retry on errors" pills do **not** return (D37 rejection).
- `ReportDetail` resilience summary (`ResilienceFormatter`): renders the thresholds when present;
  renders nothing when absent (no "abort when" row at all); code-first reports with a custom
  predicate render "Custom escalation predicate".

### E1.5 Tests

- Core: parser round-trip; compiler happy path builds a strategy that escalates at the configured
  consecutive/total/rate thresholds (reuse the existing failing-source test fixtures); rejection
  cases (with abort, empty record, zero counts, rate > 1); `CompiledReport.AbortThresholds`
  introspection for data overload vs custom predicate vs none.
- AspNetCore: `GET /reports/{name}` round-trips the thresholds; absent when unset.
- UI: `BuilderConfigMapperTests` — thresholds serialized only when skip-and-log is chosen.

**Acceptance:** a config document with `"resilience": { "onFailure": "skip-and-log",
"abortWhen": { "consecutiveFailures": 3 } }` compiles, escalates on the 3rd consecutive batch
failure in a real run, and the report detail (API + UI) shows the threshold. Document behavior
note (from D11): thresholds apply to skippable (projection/write) failures — a *read* failure
still aborts regardless, since there is no cursor to advance.

---

## E2 — Job event log: Core store + engine emission (D38)

**Goal:** the engine records discrete, structured, bounded per-job events. No HTTP yet. This is
the shared foundation for retry detail, the timeline, and the processing-rate history — one
mechanism, not three (D38).

### E2.1 Model + store (Core, new folder `src/NeoReports.Core/Events/`)

```csharp
/// <summary>One structured lifecycle event of a job run (ADR D38). Core, not Abstractions — D9
/// removed JobEvent from the frozen ABI; the concept returns as an engine/host concern (D20 pattern).</summary>
public sealed record JobEvent(
    string JobId,
    int Sequence,
    DateTimeOffset At,
    string Type,
    string? Message,
    IReadOnlyDictionary<string, string>? Data);

public interface IJobEventStore
{
    /// <summary>Appends an event. Must be cheap and must never throw into the pipeline.</summary>
    Task AppendAsync(JobEvent jobEvent, CancellationToken cancellationToken);

    /// <summary>Events for a job, ascending by sequence; optional type filter; empty when none.</summary>
    Task<IReadOnlyList<JobEvent>> ListAsync(string jobId, string? type, int limit, int offset, CancellationToken cancellationToken);

    /// <summary>Deletes all events for a job (best-effort; no error if absent).</summary>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);
}
```

**Closed event-type vocabulary** (string constants in a `JobEventTypes` static class — new types
are additive, never rename):

| Type | When | Data keys |
|---|---|---|
| `run-started` | runner entry | — |
| `run-restarted` | runner entry when events for the job already exist (crash re-execution) | — |
| `page-completed` | after a page is written to all outputs | `page`, `recordsRead`, `recordsWritten` (cumulative), `elapsedMs` |
| `retry` | Polly `OnRetry` (each retry attempt of a batch read) | `page`, `attempt`, `delayMs`, `exceptionType`, plus truncated message in `Message` |
| `batch-skipped` | skip decision after retries exhausted | `page`, `reason` |
| `outputs-finalized` | after finalize, per file | `fileName`, `sizeBytes` |
| `upload-completed` | per destination upload | `destinationType`, `fileName` |
| `run-completed` / `run-failed` / `run-cancelled` | terminal | `error` in `Message` when failed |
| `events-truncated` | cap reached | `suppressed` (count, best-effort) |

### E2.2 Options + stores + DI

```csharp
public sealed class JobEventOptions
{
    /// <summary>Hard cap of stored events per job; at the cap one events-truncated marker is appended. Default 1000.</summary>
    public int MaxEventsPerJob { get; set; } = 1000;

    /// <summary>Age after which a job's events may be pruned; null keeps them until DeleteAsync. Default null.</summary>
    public TimeSpan? Retention { get; set; }

    /// <summary>Directory for the file-backed store. Default "./neoreports-events".</summary>
    public string Directory { get; set; } = "./neoreports-events";
}
```

- `InMemoryJobEventStore` — per-job list with the cap; retention pruning on append.
- `FileJobEventStore` — one append-only JSONL file per job id (`{dir}/{jobId}.jsonl`); the job id
  is engine-generated (GUID), but defend with the same filename validation posture as
  `FileReportConfigStore` anyway. Retention: on each append, opportunistically delete `.jsonl`
  files older than `Retention` (this is also what cleans up orphan files from `?mode=sync` runs,
  which have an execution job id but no job record).
- **Opt-in registration** (D38, maintainer answer #7): `AddJobEvents(Action<JobEventOptions>? o = null)`
  in `NeoReports.Core` DI — registers options + `FileJobEventStore` singleton; an
  `AddInMemoryJobEvents()` variant for tests/dev. When not called, nothing is registered and the
  runner emits nothing.

### E2.3 Engine emission

- `ReportRunner.ExecuteAsync` resolves `IJobEventStore` via `services.GetService(...)` — the exact
  pattern already used for `IReportArtifactStore` (`ReportRunner.cs`, artifact-retention block).
  Wrap it in a small internal emitter that: assigns the per-run sequence, enforces the cap
  (asking the store for the current count once at start, then counting locally), fires and
  forgets exceptions (log at Debug, never rethrow — ground rule 3).
- Emission points map onto the existing runner structure: entry (`run-started`/`run-restarted`),
  after the write loop of each page (`page-completed`), inside both `catch` blocks
  (`batch-skipped` on skip; nothing extra on abort — `run-failed` carries it), finalize/upload
  loops, and just before each `return`/terminal path (`run-completed`/`-failed`/`-cancelled` —
  the cancelled emission happens in `ReportJobWorker`'s `OperationCanceledException` handler,
  since the runner unwinds by exception there; give the worker an optional `IJobEventStore` too).
- **Retry events**: `ResiliencePipelineFactory.Build` gains an optional
  `Action<OnRetryArguments<...>>`-shaped hook (internal signature — pass a plain delegate carrying
  attempt number, delay, exception). The runner passes a closure that also captures the current
  page number (readable from a captured local the loop updates). Polly's
  `RetryStrategyOptions.OnRetry` provides attempt/delay/outcome natively.

### E2.4 Tests (Core.UnitTests + Jobs.UnitTests)

- Stores: append/list/filter/offset/limit; cap → `events-truncated` appended exactly once and
  further appends dropped; retention prunes old jobs' events; delete; file store survives
  restart (list after new store instance on same dir).
- Runner emission (in-memory store + failing/recovering fake source, the existing resilience
  fixtures): happy run emits started/page-completed×N/finalized/upload/completed in order with
  correct cumulative counters; a retried batch emits `retry` with correct attempt/delay/page;
  skip-and-log emits `batch-skipped`; abort emits `run-failed`; cancelled run emits
  `run-cancelled`; **no store registered ⇒ run byte-identical to today** (regression: existing
  tests must not need changes).
- Concurrency: reuse the `ConcurrencyTests` pattern — 32 concurrent jobs, each job's event file
  contains only its own events, sequences monotonic.

**Acceptance:** with `AddJobEvents()` + a failing-then-recovering source, a completed job's event
file tells the full true story (started → retry → pages → finalized → completed); without the
call, zero behavioral or file-system difference. `JobStats` counters remain untouched and remain
the aggregate truth.

---

## E3 — `GET /jobs/{id}/events` + UI telemetry (depends on E2)

**Goal:** expose the event log and bring back the three removed telemetry cards, real.

### E3.1 Endpoint (AspNetCore)

- `GET {prefix}/jobs/{id}/events?type=&limit=&offset=` — own sub-resource per the D5 precedent;
  `JobView` stays untouched so status polling does no event IO.
  - Unknown job ⇒ **404**. Job exists, store absent or no events ⇒ **200 `[]`**.
  - `type`: exact match against the vocabulary (unknown value ⇒ `[]`, not 400 — the vocabulary is
    additive and a newer client may know types an older server doesn't... actually the reverse;
    still, `[]` is the forgiving, correct answer for a filter).
  - `limit` default 200, clamp 1–1000; `offset` >= 0 (clamp).
  - Response: `JobEventView(int Sequence, DateTimeOffset At, string Type, string? Message,
    IReadOnlyDictionary<string,string>? Data)` in `Contracts.cs`. No property bags by
    construction; messages already truncated/sanitized at emission (E2).
- Resolve `IJobEventStore` as optional (`GetService`) — hosts without `AddJobEvents()` return
  `[]` for every job (with the honest UI state below), mirroring how the artifact store's absence
  behaves on `/download`.

### E3.2 UI

- API client: `TryGetJobEventsAsync(id, type?, limit?)` following the `Try*` pattern.
- **Timeline card** returns on `JobRunning` / `JobCompleted` / `JobFailed`: full event list,
  ascending; while running, refreshed on the existing poll tick. Honest states: store absent /
  empty ⇒ "No events recorded — the event log is not enabled on this host."; `events-truncated`
  present ⇒ render it as a visible final row ("N further events were not recorded").
- **Retries card** returns on `JobRunning` / `JobFailed`: `?type=retry` — page, attempt, delay,
  exception type, message. Empty ⇒ "No retries so far." (a good state, not a fallback).
- **Processing-rate sparkline** returns on `JobRunning` (and `JobCompleted`): computed
  client-side from `page-completed` events — rate between consecutive points =
  Δ`recordsWritten` / Δ`At`. Fewer than 2 points ⇒ "Collecting rate data…". The existing single
  `Rate/s` number (real since the D34 follow-up) stays as the headline; peak = max of the series.
  **No second sampling mechanism** — the series is real page completions (D38).

### E3.3 Tests

Integration: 404 unknown; `[]` for running-no-events and for store-absent host; full list
ascending after a completed run; `type=retry` filters; limit/offset clamps; response JSON has no
`"properties"` key. UI verified via the preview tool per repo practice (document checks in PR).

**Acceptance:** run sample `09-web-ui-live` with `AddJobEvents()` and a source that fails twice
then recovers: the running page shows live timeline + a real retries card + a sparkline whose
points match the page cadence; the completed page keeps them; with `AddJobEvents()` removed, all
three show the honest disabled/empty state and nothing fabricated.

---

## E4 — Memory screen (D39)

**Goal:** replace the removed per-job "Memory" card (impossible to measure honestly) with a
process-level Memory screen (maintainer-locked decision, D39).

### E4.1 Endpoint (AspNetCore)

`GET {prefix}/system/memory` → **200**

```csharp
public sealed record MemoryView(
    long WorkingSetBytes,        // Environment.WorkingSet
    long GcHeapSizeBytes,        // GC.GetGCMemoryInfo().HeapSizeBytes
    long GcCommittedBytes,       // GC.GetGCMemoryInfo().TotalCommittedBytes
    DateTimeOffset MeasuredAt,
    int RunningJobs);            // IJobStore count of status Running (0 when no job stack registered)
```

`IJobStore` resolved as optional — no job stack ⇒ `RunningJobs = 0`. No time series, no counters
registry, no background collection — one reading per request (D39's deliberate narrowness next to
CLAUDE.md's "no general metrics dashboard").

### E4.2 UI

New "Memory" page (route `/system/memory`, topbar entry under the existing nav pattern): the
three metrics as metric cards + `MeasuredAt`, auto-refresh (same polling cadence as job pages),
and beneath it the running-jobs table composed client-side from the existing
`GET /jobs?status=Running` (maintainer answer #16 — no duplicated list endpoint). Page copy is
part of the feature: "Memory is measured for the whole host process (including this UI when
co-hosted). To estimate a single job's footprint, run it alone and watch this screen." Engine
unreachable ⇒ the standard banner; zero running jobs ⇒ normal empty table.

### E4.3 Tests

Integration: shape + sane values (> 0 working set); `RunningJobs` reflects a started job;
host without a job stack still 200 with 0.

**Acceptance:** screen shows live process memory alongside what's running; nothing per-job is
claimed anywhere.

---

## E5 — Partial artifacts for failed and cancelled jobs (D40)

**Goal:** when a job fails or is cancelled, the already-staged temp output is preserved in a
dedicated area — never at the real destination (protects D2/D15), never in the completed
artifacts surface.

### E5.1 Store (Core, `src/NeoReports.Core/Artifacts/`)

`IPartialArtifactStore` — same shape as `IReportArtifactStore` (`SaveAsync` / `ListAsync` /
`DeleteAsync`), deliberately a **separate interface** (not a flag) so no consumer can ever list a
partial as a real artifact by omission. `FileSystemPartialArtifactStore` with its own root
(default `./neoreports-partials/{jobId}/`) and options:

```csharp
public sealed class PartialArtifactOptions
{
    public string Directory { get; set; } = "./neoreports-partials";
    /// <summary>Partials older than this are pruned on save. Default 7 days.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}
```

Opt-in DI: `AddPartialArtifacts(Action<PartialArtifactOptions>? o = null)`.

### E5.2 Runner changes

- **Eligibility (maintainer answer #9): Failed and Cancelled runs.** `SkipBatchAndLog` runs
  ending `CompletedPartial` are completed jobs — their file legitimately publishes to the real
  destinations (D11); the partial store never engages for them.
- Failed path (runner's `status == Failed` branch, before the `finally` cleanup): if an
  `IPartialArtifactStore` is resolved, per output do best-effort `FinalizeAsync` in a `try`
  (a writer broken by the failure may refuse — skip that file), then copy each surviving file
  into the store **renamed `{name}.partial.{ext}`** (maintainer answer #10 — the label survives
  the download leaving the browser). Use `CancellationToken.None` (the terminal-state pattern
  `ReportJobWorker` already uses).
- Cancelled path: the runner unwinds via `OperationCanceledException`, so the capture hook lives
  in the `finally`-adjacent structure: catch `OperationCanceledException`, capture partials, then
  rethrow (preserving the worker's Cancelled status flow). Zero-row (header-only) files are still
  captured — honest "it died immediately".
- Capture failures are logged and swallowed (ground rule 3); temp cleanup still runs after.

### E5.3 Endpoints (AspNetCore)

- `GET {prefix}/jobs/{id}/partial-artifacts` — 404 unknown job; status not Failed/Cancelled ⇒
  **200 `[]`**; else `ArtifactView[]` (reuse the record — it already never exposes `Path`).
- `GET {prefix}/jobs/{id}/partial-artifacts/download` — single file streamed, zip for multiple
  (reuse the `/download` zip helper); 404 when nothing captured. **Completely separate routes** —
  `GET /jobs/{id}/artifacts` and the completed `/download` never learn partials exist.

### E5.4 UI

`JobFailed` gains the "Partial output" card back (the page also serves cancelled jobs — verify
routing at implementation): real file list (`.partial` names, sizes), a warning `Banner`
("Best-effort output written before the run stopped — incomplete and unverified."), per-file /
zip download. Two distinct honest empty states: store not registered ⇒ "Partial output capture is
not enabled on this host."; registered but nothing captured ⇒ "No partial output was produced
before the run stopped."

### E5.5 Tests

- Core/runner: abort mid-run (page 3 of 5) ⇒ CSV partial contains exactly the fully-written
  batches (D11's batch-atomicity), renamed with `.partial`; XLSX partial exists when ClosedXML
  finalize succeeds; cancelled run captures; CompletedPartial run does **not** capture and still
  publishes to real destinations; no store registered ⇒ behavior identical to today; retention
  prunes old jobId dirs.
- Integration: `[]` for completed/running jobs; list + download for a failed job; completed
  `/artifacts` and `/download` unchanged (regression: never include partials).

**Acceptance:** fail a multi-output job mid-run in sample 09: the failed page offers
`report.partial.csv` / `report.partial.xlsx` with the warning; the real destination directory has
nothing; the completed-artifacts endpoint returns `[]` for that job.

---

## E6 — Scheduling: recurring runs (D41, supersedes D35)

**Goal:** cron-based recurring execution for both paths, runtime-manageable for both origins,
honest "next run".

### E6.1 Abstractions (additive)

`ScheduleConfig(string Cron)` record + trailing optional `Schedule` on `ReportConfig`.
**UTC-only** (maintainer answer #4): the cron expression is evaluated in UTC; there is no
timezone field — the UI converts the computed next-run instant to the viewer's local time for
display. Document this prominently in the XML docs.

### E6.2 Core

- `ReportBuilder<T>.Schedule(string cron)`; `CompiledReport` gains `ScheduleConfig? Schedule`.
- Cron validation at compile time via **Cronos** (new CPM dependency, maintainer-approved; note
  it is already a transitive Hangfire dependency). Invalid cron ⇒ `ConfigurationException`.
- `IRecurringReportScheduler` (Core — **not** on the frozen `IReportJobScheduler`):

```csharp
public interface IRecurringReportScheduler
{
    Task RegisterRecurringAsync(string reportName, string cron, CancellationToken cancellationToken);
    Task RemoveRecurringAsync(string reportName, CancellationToken cancellationToken);
    /// <summary>Next occurrence in UTC, or null when not scheduled.</summary>
    Task<DateTimeOffset?> GetNextOccurrenceAsync(string reportName, CancellationToken cancellationToken);
}
```

- **Runtime overrides for both origins (maintainer answer #5)** — `IScheduleOverrideStore`
  (Core, file-backed `{configured dir}/schedules/{name}.json`, same atomic-write pattern as
  `FileReportConfigStore`). One uniform mechanism; the config document is **never** patched:
  - Entry shape: `{ "cron": "0 6 * * 1" }` or the tombstone `{ "cron": null }` (= explicitly
    unscheduled, even though the declaration has a schedule).
  - **Effective schedule = override entry if present (tombstone ⇒ none) else the declared
    schedule (config document or code-first builder).**
  - `PUT /schedule` writes an override; `DELETE /schedule` writes a tombstone when a declared
    schedule exists, or removes the override when it doesn't (so "delete" always means "stops
    firing" and re-registering the report re-applies only what's declared).
- **Startup reconciliation:** dynamic reports hydrate lazily (D33), so scheduling ships an
  `IHostedService` (registered by the scheduling DI wiring) that resolves the registry at startup
  (forcing hydration), computes every report's effective schedule, calls
  `RegisterRecurringAsync`/`RemoveRecurringAsync` accordingly, and removes orphaned registrations
  (Hangfire recurring ids prefixed `neoreports:` whose report no longer exists — covers "deleted
  while the server was down").

### E6.3 Jobs packages

- **Hangfire**: `HangfireJobScheduler` implements `IRecurringReportScheduler` via
  `IRecurringJobManager.AddOrUpdate` with recurring-job id `neoreports:{reportName}`. The
  recurring firing cannot reuse `ExecuteAsync(jobId, ...)` (no pre-created job) — new invoker
  entry `ExecuteRecurringAsync(reportName)`: `IJobStore.CreateAsync` first, then delegate to
  `ReportJobWorker` — so recurring runs appear in `GET /jobs` like any other job.
  `GetNextOccurrenceAsync` computes via Cronos from the stored cron (don't scrape Hangfire
  storage).
- **InMemory** (maintainer answer #2): `InMemoryJobScheduler` implements it with Cronos +
  one `PeriodicTimer` loop per registered schedule (compute next occurrence, delay, enqueue
  through the existing `EnqueueAsync`, repeat); schedules die at process exit like everything
  else in-memory. Dispose cancels the loops.
- **Overlap (maintainer answer #3): firings run concurrently** — no skip-if-running logic; the
  engine already isolates concurrent jobs (Backlog concurrency tests). Document in XML docs.

### E6.4 AspNetCore

- `POST /reports` accepts `schedule` in the document; effective at registration.
- `PUT {prefix}/reports/{name}/schedule` body `{ "cron": "..." }` → 200; invalid cron ⇒ 400;
  unknown report ⇒ 404. **Works for both origins** (code-first included — answer #5); no
  secrets round-trip because the config document is never touched (this deliberately does not
  reopen D33(f)'s punted full `PUT`).
- `DELETE {prefix}/reports/{name}/schedule` → 204 (idempotent semantics per the override rules
  above); 404 unknown report.
- When no `IRecurringReportScheduler` is registered: `PUT`/`DELETE /schedule` ⇒ **409** with a
  clear message; `POST /reports` with a `schedule` field ⇒ 400 (reject, don't silently drop —
  D36 spirit).
- `CapabilitiesResponse` gains `bool Scheduling` (AspNetCore record, additive JSON).
- `ReportDetailView` gains `string? ScheduleCron`, `DateTimeOffset? NextRunAt` (UTC, computed via
  Cronos from the effective schedule — never fabricated), `bool ScheduleOverridden` (true when an
  override/tombstone is in effect, so the UI can say "overrides the declared schedule").
- `DELETE /reports/{name}` calls `RemoveRecurringAsync` **before** `Unregister` (no new firing
  can race the delete) and removes any override entry; running jobs finish normally (D33(e)).

### E6.5 UI

Schedule card returns on `ReportDetail` and Builder step 5 — real fields only: cron input (preset
chips just fill the text box), effective-schedule display, "Next run" rendered in the **viewer's
local time** (browser conversion of the UTC `NextRunAt` — answer #4) with the UTC value in a
tooltip/mono subline, an "overridden at runtime" chip when applicable, and Set/Clear actions
calling the new endpoints. Honest states: `Scheduling=false` in capabilities ⇒ "Scheduling is not
supported by this host's job scheduler." and no controls; no schedule ⇒ "Not scheduled". No
calendar heatmap.

### E6.6 Tests

- Core: cron validation; override-store roundtrip + tombstone semantics + effective-schedule
  resolution matrix (declared×override×tombstone); reconciliation hosted service registers
  declared schedules, applies overrides, removes orphans.
- Jobs: InMemory recurring fires (short cron / fake clock or a near-term occurrence), each firing
  creates a job record; concurrent-overlap run both complete; dispose stops the loop. Hangfire
  scheduler unit-tested at the seam (recurring manager mock) as the existing Hangfire tests do.
- Integration: PUT/DELETE schedule on config-first and code-first reports; 400 invalid cron;
  409 when no recurring scheduler; detail exposes cron + plausible `NextRunAt`; capabilities flag;
  delete report removes the registration.

**Acceptance:** in sample 09, schedule a report from its detail page for "every minute", watch a
job appear in the dashboard within a minute with no manual trigger, see "Next run" tick forward
truthfully, clear the schedule, and see it stop. Restart the host: the schedule survives
(Hangfire) / dies (InMemory, documented).

---

## Explicitly out of scope for this epic (do not implement)

- Per-exception-type retry filtering — **rejected**, D37 (revisiting means revisiting D6 itself).
- Per-job memory metrics — rejected, D39 (the Memory screen is the answer).
- A background health poller, metrics time series, or any general metrics dashboard (CLAUDE.md).
- Timezone-aware cron (UTC-only in this epic; a timezone field would be additive later).
- The source registry — **Epic F**, own blueprint.
- Full report edit (`PUT /api/reports/{name}`) — still punted (D33(f)); the schedule endpoints
  are deliberately narrower.

## Versioning

`NeoReports.Abstractions` gains `AbortThresholdConfig` + two trailing optional record params ⇒
**SemVer-minor** (D25). Core/AspNetCore/Jobs/UI additive ⇒ minor. Cronos enters
`build/Directory.Packages.props` (approved). CHANGELOG entry per shipped PR as usual.
