# Epic D — Live API for the UI (dynamic registration + read endpoints)

> **Status: approved blueprint, not yet built.** Scope authorized by the maintainer (ADR **D33**,
> 2026-07). One PR per task, in order — D1 is the foundation, D2 depends on D1, D3–D5 are
> independent of each other, D6–D9 wire the UI and depend on their API counterparts.
>
> This document is written to be executable by an implementation session without extra context:
> every task lists the files to touch, the exact types/endpoints to add, edge-case behavior, the
> test plan, and acceptance criteria. When something here conflicts with the code you find,
> **stop and re-read the code first** — the code wins, then update this doc in the same PR.

## Goal

Make the Blazor UI (`src/UI/NeoReports.UI`, Epic C) run on real engine data end-to-end, and — the
headline feature — make the **Builder wizard actually create runnable reports** through a new
`POST /api/reports` backed by the existing dynamic-config path (Epic A/B: `ReportConfig` +
`JsonReportConfigParser` + `ReportConfigCompiler`).

## What already exists (do not rebuild any of this)

| Capability | Where | State |
|---|---|---|
| Config model (`ReportConfig`, `SourceConfig`, `ColumnConfig`, `OutputConfig`, `SectionConfig`, `DestinationConfig`) | `src/NeoReports.Abstractions/Configuration.cs` | Done. Serializer-agnostic records. |
| JSON parser (`JsonReportConfigParser : IReportConfigParser`) | `src/NeoReports.Core/Configuration/JsonReportConfigParser.cs` | Done. Throws `ConfigurationException` on malformed input. |
| Compiler (`ReportConfigCompiler.Compile(ReportConfig, IServiceProvider)` → `CompiledReport`) | `src/NeoReports.Core/Configuration/ReportConfigCompiler.cs` | Done. Static, side-effect free; resolves `IConfigSourceProvider` / `IWriterFactory` / `IDestinationFactory` from DI by type/format id; throws `ConfigurationException` on invalid config or missing provider. |
| Registry with runtime `Register` | `src/NeoReports.Core/Registry/ReportRegistry.cs` | `Register(CompiledReport)` exists, thread-safe (`ConcurrentDictionary`), throws on duplicate name. **Missing:** `Unregister`, and neither is on `IReportRegistry`. |
| Startup registration paths | `src/NeoReports.Core/DependencyInjection/ServiceCollectionExtensions.cs` | `AddReport<TRow>` (code-first, compiles eagerly), `AddReportFromConfig` / `AddReportFromConfigFile` / `AddReportsFromConfigDirectory` (config singletons, compiled lazily when the registry is first resolved — see `GetOrAddRegistry`). |
| Job list query | `IJobStore.ListAsync(JobQuery)` in `src/NeoReports.Abstractions/Jobs.cs` | Done (filters: `Status`, `ReportName`, `Since`, `Limit`, `Offset`). **No HTTP route.** |
| Artifacts | `IReportArtifactStore` in `src/NeoReports.Core/Artifacts/` — `ReportArtifact(FileName, MimeType, Path, SizeBytes)` | Done. **No HTTP route** beyond `/download`. |
| HTTP endpoints | `src/Integrations/NeoReports.AspNetCore/NeoReportsEndpointRouteBuilderExtensions.cs` | `POST /reports/{name}/run` (async + `?mode=sync`), `GET /reports`, `GET /jobs/{id}`, `POST /jobs/{id}/cancel`, `GET /jobs/{id}/download`. |
| UI API client + fallback pattern | `src/UI/NeoReports.UI/Services/NeoReportsApiClient.cs` | `Try*` methods returning `null`/`false` on any failure; pages fall back to `SampleData`. Extend this class — keep the pattern. |
| Config sample (canonical JSON shape) | `samples/05-dynamic-config-sql/report.json` | The exact document shape `POST /api/reports` accepts. |

## Ground rules (apply to every task)

1. **`NeoReports.Abstractions` stays frozen** (CLAUDE.md rule 7). Everything new lands in
   `NeoReports.Core`, `NeoReports.AspNetCore`, or `NeoReports.UI`. If a task seems to need an
   Abstractions change, stop and record a decision first.
2. **Never echo property bags.** `SourceConfig.Properties`, `OutputConfig.Properties`, and
   `DestinationConfig.Properties` can hold connection strings, paths, and bucket names. No GET
   response may ever include them — expose only type/format ids, column definitions, and scalars
   explicitly listed in this blueprint.
3. **Dynamic report names are filenames.** Validate with `^[a-zA-Z][a-zA-Z0-9_-]{0,99}$` before
   any file-system interaction; reject anything else with HTTP 400. This makes path traversal
   structurally impossible — do not "sanitize", **reject**.
4. **Sanitize user input before logging** (`Replace('\r','_').Replace('\n','_')` — same helper
   pattern as `NeoReportsApiClient.Sanitize`, added for CodeQL log-forging findings in Epic C).
5. **Auth is inherited.** All new endpoints go inside the existing route group created by
   `MapNeoReports`, so `NeoReportsEndpointOptions.RequireAuthorization` covers them automatically.
6. Conventions as usual: file-scoped namespaces, `sealed`, `record` for DTOs, async with
   `CancellationToken` last, XML docs on public surface, tests in the matching `*.UnitTests` /
   `*.IntegrationTests` project, new packages via CPM only.

---

## D1 — Core: mutable registry + persisted config store

**Goal:** the engine can accept a new report definition at runtime, keep it across restarts, and
remove it again. Pure Core work — no HTTP yet.

### D1.1 `IMutableReportRegistry`

New file `src/NeoReports.Core/Registry/IMutableReportRegistry.cs`:

```csharp
namespace NeoReports.Core.Registry;

/// <summary>A report registry that accepts changes at runtime (dynamic path, ADR D33).</summary>
public interface IMutableReportRegistry : IReportRegistry
{
    /// <summary>Registers a compiled report. Throws <see cref="ConfigurationException"/> if the name is taken.</summary>
    void Register(CompiledReport report);

    /// <summary>Removes the report registered under <paramref name="name"/>. Returns false when absent.</summary>
    bool Unregister(string name);
}
```

- `ReportRegistry` implements it. `Register` already exists; add `Unregister` =
  `_reports.TryRemove(name, out _)`.
- DI: wherever `GetOrAddRegistry` registers `IReportRegistry`, register the **same singleton
  instance** under `IMutableReportRegistry` too (one instance, two interfaces). Keep
  `IReportRegistry` as the read interface everywhere that only reads.
- Do **not** add `Register`/`Unregister` to `IReportRegistry` — additive interface, no breaking
  change for any existing implementer.
- Concurrency note (document in XML docs): a job already running when its report is unregistered
  keeps running — the worker holds a reference to the `CompiledReport`; unregistration only
  prevents *new* lookups. No cancellation on delete.

### D1.2 `IReportConfigStore` + `FileReportConfigStore`

New files under `src/NeoReports.Core/Configuration/`:

```csharp
/// <summary>Persists dynamic report config documents so runtime-registered reports survive restart.</summary>
public interface IReportConfigStore
{
    /// <summary>Saves (create or overwrite) the raw config document under the report name.</summary>
    Task SaveAsync(string name, string configDocument, CancellationToken cancellationToken);

    /// <summary>Deletes the stored document. Returns false when absent.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>True when a document is stored under the name.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken);

    /// <summary>All stored documents as (name, document) pairs.</summary>
    Task<IReadOnlyList<(string Name, string Document)>> ListAsync(CancellationToken cancellationToken);
}
```

`FileReportConfigStore` (v1 implementation, consistent with the single-server philosophy):

- Constructor takes the storage directory; creates it on first write if missing.
- File layout: `{directory}/{name}.json` — one document per report. The name was validated with
  the regex from ground rule 3 **before** reaching the store, but the store defends itself anyway:
  throw `ArgumentException` if `name` fails the same regex (single shared helper, e.g.
  `internal static class DynamicReportName { public const string Pattern = ...; public static bool IsValid(string) }`).
- `ListAsync` reads `*.json` non-recursively; the name is the filename without extension.
- Writes are atomic enough for v1: write to `{name}.json.tmp`, then `File.Move(tmp, final, overwrite: true)`.

### D1.3 Startup rehydration

New options + DI extension in `src/NeoReports.Core/DependencyInjection/` (same class or a new
`DynamicReportsServiceCollectionExtensions.cs`):

```csharp
public sealed class DynamicReportsOptions
{
    /// <summary>Directory where dynamic report configs are persisted. Default: "./neoreports-configs".</summary>
    public string Directory { get; set; } = "./neoreports-configs";
}

public static IServiceCollection AddDynamicReports(
    this IServiceCollection services, Action<DynamicReportsOptions>? configure = null)
```

`AddDynamicReports`:

- Calls `AddNeoReports()`, registers `DynamicReportsOptions`, `IReportConfigStore` →
  `FileReportConfigStore` (singleton), and hooks rehydration into the **same lazy mechanism**
  code-first config reports already use: when the registry is first resolved
  (`GetOrAddRegistry`'s factory), after compiling the DI-registered `ReportConfig` singletons,
  it also loads every document from the store, runs it through
  `JsonReportConfigParser` → env substitution (D1.4) → `ReportConfigCompiler.Compile` →
  `registry.Register`.
- **A corrupt/incompilable stored file must not crash the host**: catch `ConfigurationException`
  (and `IOException`) per document, log an error naming the file (sanitized), skip it, continue.
- **Name collision on rehydrate** (a stored config clashes with a code-first report registered in
  the same host): log an error and skip the stored one — code-first wins, deterministically.

### D1.4 Environment-variable placeholders (secrets stay off disk)

Small pre-compile transform, new file `src/NeoReports.Core/Configuration/ReportConfigEnvironment.cs`:

```csharp
/// <summary>Resolves ${VAR} placeholders in string property values from environment variables.</summary>
public static class ReportConfigEnvironment
{
    /// <summary>Returns a copy of the config with every string property value that is exactly
    /// "${NAME}" replaced by the environment variable NAME. Missing variables throw
    /// <see cref="ConfigurationException"/> naming the variable and the property key.</summary>
    public static ReportConfig Substitute(ReportConfig config);
}
```

- Scope deliberately narrow: whole-value match only (`^\$\{[A-Za-z_][A-Za-z0-9_]*\}$`), applied to
  the string values of the three property bags (`Source.Properties`, each `Outputs[i].Properties`,
  each `Destinations[i].Properties`). No interpolation inside larger strings, no recursion, no
  defaults syntax. Keep the compiler pure — substitution is called by the **rehydration path and
  by the endpoints (D2) right before `Compile`**, never inside `ReportConfigCompiler`.
- This is what lets a user POST a config with `"connectionString": "${SALES_DB}"` so the secret
  never lands in the persisted JSON.

### D1 tests (`tests/NeoReports.Core.UnitTests`)

- Registry: `Unregister` removes and returns true; unknown name returns false; `Find` after
  unregister returns null; register→unregister→register same name succeeds.
- File store: save/list/exists/delete roundtrip; overwrite replaces content; invalid name throws;
  `ListAsync` on missing directory returns empty (no throw); tmp-file leftovers are ignored.
- Rehydration: stored config is runnable by name after provider `BuildServiceProvider()`;
  a corrupt JSON file is skipped and logged while a valid sibling still loads; collision with a
  code-first name keeps the code-first report.
- Env substitution: happy path, missing variable throws naming it, non-placeholder strings and
  non-string values untouched, `${lower_case}` accepted, `abc${X}def` NOT substituted.

**Acceptance:** all new/existing tests green; `NeoReports.Abstractions` untouched; a console
sample-style test proves save → new provider → rehydrate → run by name.

---

## D2 — AspNetCore: dynamic report endpoints

**Goal:** HTTP surface for create / validate / delete / capabilities. Depends on D1.

All endpoints join the existing group in `NeoReportsEndpointRouteBuilderExtensions.MapNeoReports`.
New response records go in `Contracts.cs`.

### D2.1 `POST {prefix}/reports` — register a dynamic report

- Body: the raw config JSON document (same shape as `samples/05-dynamic-config-sql/report.json`).
  Read it as a string (`HttpContext.Request.Body`) — do **not** bind to `ReportConfig` via ASP.NET
  model binding; parsing belongs to `IReportConfigParser` so HTTP and rehydration behave
  identically.
- Flow (exact order matters):
  1. Parse → `ConfigurationException` ⇒ **400** `{ error }`.
  2. Validate `config.Name` against the shared regex ⇒ **400** if invalid.
  3. `registry.Contains(name)` ⇒ **409** `{ error: "A report named '...' already exists." }`.
  4. `ReportConfigEnvironment.Substitute(config)` ⇒ missing env var is a `ConfigurationException`
     ⇒ **400**.
  5. `ReportConfigCompiler.Compile(substituted, services)` ⇒ `ConfigurationException` (unknown
     source type, unknown format, bad columns…) ⇒ **400**. Compile is side-effect free — nothing
     to roll back on failure.
  6. `mutableRegistry.Register(compiled)` — if this races another request and throws, return
     **409**.
  7. `configStore.SaveAsync(name, originalDocument)` — persist the **original** document (with the
     `${VAR}` placeholders, *not* the substituted secrets). If the save throws, **roll back**:
     `mutableRegistry.Unregister(name)` and return **500**.
- Success: **201** with `Location: {prefix}/reports/{name}` and body
  `ReportCreatedResponse(string Name, IReadOnlyList<string> Columns)`.

### D2.2 `POST {prefix}/reports/validate` — dry-run compile

- Same steps 1–5 as D2.1 (including the duplicate-name check as a *warning-level* result, see
  below) but **no side effects** — never registers, never saves.
- Success: **200** `ValidateReportResponse(bool Valid, string? Error, string? Name,
  IReadOnlyList<string>? Columns, bool NameTaken)`. On any `ConfigurationException`, still return
  **200** with `Valid=false` and the message — validation *outcomes* are not transport errors.
  (Reserve 400 for an unreadable/empty body.)

### D2.3 `DELETE {prefix}/reports/{name}` — remove a dynamic report

- Unknown name (neither registry nor store) ⇒ **404**.
- Name registered but **not** in the config store ⇒ it is code-first ⇒ **409**
  `{ error: "Report '...' is code-registered and cannot be deleted at runtime." }`.
- Otherwise: `configStore.DeleteAsync` first, then `mutableRegistry.Unregister` ⇒ **204**.
  (Store first: if the process dies between the two, the report stays registered until restart but
  won't rehydrate — self-healing. The opposite order resurrects a deleted report.)
- Document (XML doc + this file): running jobs of the deleted report finish normally.

### D2.4 `GET {prefix}/capabilities` — what the host can build with

- Resolve from DI: `IEnumerable<IConfigSourceProvider>` → distinct `.Type`;
  `IEnumerable<IWriterFactory>` → distinct `.Format`; `IEnumerable<IDestinationFactory>` →
  distinct `.Type`. All sorted ordinal.
- **200** `CapabilitiesResponse(IReadOnlyList<string> Sources, IReadOnlyList<string> Formats,
  IReadOnlyList<string> Destinations)`. Empty lists are valid (host registered nothing) — the UI
  uses that to fall back to demo mode.

### D2 tests (`tests/NeoReports.AspNetCore.IntegrationTests`)

Use the existing integration-test host pattern in that project. Register the in-memory config
source (see sample 04) + CSV writer + a temp-dir `FileReportConfigStore` so no external services
are needed.

- POST happy path: 201, Location header, report appears in `GET /reports`, and
  `POST /reports/{name}/run?mode=sync` streams CSV — **the end-to-end proof**.
- POST invalid JSON → 400; unknown source type → 400; duplicate name → 409; invalid name
  (`"../evil"`, `"a b"`, 101 chars) → 400 and **no file created**.
- POST with `${MISSING_VAR}` → 400; with a set env var → 201 and the stored file still contains
  the placeholder (assert on the temp dir).
- validate: valid config → 200/Valid=true + columns; broken config → 200/Valid=false + message;
  taken name → NameTaken=true.
- DELETE: dynamic → 204, gone from `GET /reports`, file gone, re-POST same name → 201;
  code-first → 409; unknown → 404.
- capabilities: reflects exactly what the test host registered.

**Acceptance:** all green; new endpoints documented in the `MapNeoReports` XML doc list and in
`src/Integrations/NeoReports.AspNetCore/README.md`; `docs/ui-handoff.md` endpoint table updated
(`POST /api/reports` moves from `future` to real).

---

## D3 — AspNetCore: `GET {prefix}/jobs` (job list)

**Goal:** expose `IJobStore.ListAsync`. Independent of D1/D2.

- Query params → `JobQuery`: `status` (enum name, case-insensitive; unknown value → 400),
  `report` (string), `since` (ISO-8601 `DateTimeOffset`), `limit` (default 50, **cap at 200** —
  clamp, don't error), `offset` (default 0, negative → 0).
- Inject `IJobStore` directly (it is what the schedulers persist to). **Verify both DI paths**
  (`NeoReports.Jobs` in-memory and `NeoReports.Jobs.Hangfire`) actually register `IJobStore`; if
  the Hangfire path doesn't, add the registration there in this PR.
- Response: **200** `JobView[]` (existing record), ordered `CreatedAt` **descending** — enforce
  the ordering in the endpoint, don't rely on store ordering.
- When no `IJobStore` is registered (host mapped endpoints without a job stack): the existing
  endpoints 500 on `GetRequiredService`; match whatever `GET /jobs/{id}` does today — do not
  invent a new behavior.

Tests (integration): empty store → `[]`; filtering by each param; limit clamp; ordering; bad
`status` → 400.

**Acceptance:** green; endpoint listed in XML docs/README; handoff table row for the dashboard
jobs strip flips to the real endpoint.

---

## D4 — AspNetCore: report detail + enriched summary

**Goal:** `GET {prefix}/reports/{name}` with the full safe definition; richer list items.

### Core prerequisite (same PR)

`CompiledReport` keeps `Outputs`/`Destinations`/`Retry`/`FailureStrategy` internal. Add **public,
read-only, computed** metadata (additive, no ctor change):

```csharp
public IReadOnlyList<string> OutputFormats { get; }      // Outputs → Factory.Format, in order
public IReadOnlyList<string> DestinationTypes { get; }   // Destinations → Factory.Type, in order
public RetryOptions RetryOptions { get; }                // expose the existing internal Retry
public string FailureStrategyName { get; }               // FailureStrategy.GetType().Name
```

(Compute the two lists once in the ctor into fields; `RetryOptions` is already a public
Abstractions type, exposing it is safe.)

### Endpoint

- `GET {prefix}/reports/{name}` → 404 unknown; else **200** `ReportDetailView`:

```csharp
public sealed record ReportColumnView(string Name, string Type, string? DisplayName, string? Format, bool Nullable);
public sealed record ReportDetailView(
    string Name,
    IReadOnlyList<ReportColumnView> Columns,
    int PageSize,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> Destinations,
    string FailureStrategy,
    int RetryMaxAttempts,            // + whichever RetryOptions fields exist — read Resilience.cs
    string Origin,                   // "code" | "config"
    bool Deletable);                 // == (Origin == "config")
```

- `Origin`: `"config"` iff `IReportConfigStore.ExistsAsync(name)` (resolve it as **optional** —
  `GetService`, not `GetRequiredService`; hosts without `AddDynamicReports` have every report as
  `"code"`).
- Column `Type` is the `ColumnType` enum name. **No property bags anywhere** (ground rule 2).
- Extend the existing `ReportSummary` (list endpoint) with `Formats` and `Destinations` — additive
  properties; keep the record's existing shape otherwise so current UI/json consumers don't break.

Tests: detail of a code-first report (origin=code, deletable=false); of a POSTed one
(origin=config, deletable=true); 404; summary now carries formats/destinations; response JSON
contains no `"properties"` key (regression guard for rule 2).

**Acceptance:** green; handoff table row for report detail flips from "find in list client-side"
to the real detail endpoint.

---

## D5 — AspNetCore: `GET {prefix}/jobs/{id}/artifacts`

**Goal:** the Job-completed screen shows real files. Kept out of `JobView` so the frequent
status poll does no file-system work.

- 404 unknown job. Job not `Completed` → **200 `[]`** (simplest for the UI; not an error).
- Else `IReportArtifactStore.ListAsync(id)` → **200**
  `ArtifactView(string FileName, string MimeType, long SizeBytes)[]` — `ReportArtifact` already
  carries `SizeBytes`; never expose `Path`.
- Resolve the artifact store the same way `/download` does today (mirror its null/absence
  behavior exactly).

Tests: completed job with 1 and with 2 outputs (names/sizes match what `/download` serves);
running job → `[]`; unknown → 404; response contains no `"path"` key.

**Acceptance:** green; endpoint in XML docs/README + handoff table.

---

## D6 — UI: Builder wired end-to-end (the headline)

**Goal:** the 5-step Builder produces a `ReportConfig`, validates it, saves it via
`POST /api/reports`, and can run it immediately. Depends on D2.

### D6.1 API client (`Services/NeoReportsApiClient.cs`)

Add, following the existing `Try*`/`Sanitize` patterns and the `ApiBase` URI logic:

```csharp
Task<ApiCapabilities?> TryGetCapabilitiesAsync(CancellationToken ct);          // GET  capabilities
Task<ApiValidationResult?> TryValidateReportAsync(string configJson, CancellationToken ct); // POST reports/validate
Task<ApiCreateResult?> TryCreateReportAsync(string configJson, CancellationToken ct);       // POST reports  (null = transport failure; result carries success/409/400 detail)
Task<bool> TryDeleteReportAsync(string name, CancellationToken ct);            // DELETE reports/{name}
```

`ApiCreateResult` must distinguish: created (201, with name), name conflict (409), invalid
(400 + error message) — the wizard shows different UI for each.

### D6.2 `BuilderState` → `ReportConfig` mapping

New pure, unit-testable class `Services/BuilderConfigMapper.cs` (static
`string ToConfigJson(BuilderState state)`), serializing with the same `JsonSerializerDefaults.Web`
options. Field mapping — **read `BuilderState.cs` first** and reconcile; the intended mapping:

| Wizard step | BuilderState | ReportConfig |
|---|---|---|
| 1 Source | selected source type id | `source.type` |
| 2 Configure | query text, key column, page size, connection string ref | `source.properties`: `sql`, `key`, `pageSize`, `connectionString` (encourage `${VAR}` in the UI hint text); top-level `pageSize` |
| 2 Configure | column definitions (name/type/display/format) | `columns[]` (order = position) |
| 3 Format | chosen format + options | `outputs[0].format` + safe `properties` |
| 4 Destination | chosen destination + path template | `destinations[0]` |
| 5 Review | report name (validated client-side with the same regex, mirrored message) | `name` |

Fields the wizard collects that have no config equivalent (schedule toggle, notification
switches…) are **not serialized** — they remain cosmetic, and the Review step labels them
"not saved (post-MVP)".

### D6.3 Wizard behavior

- **Step 1** lists source types from `TryGetCapabilitiesAsync`; fallback to `SampleData` cards
  (unchanged look) when null/empty ⇒ the whole wizard enters **demo mode** (Save disabled with an
  explanatory tooltip; everything else browsable as today).
- **Step 2** "Validate" button → mapper → `TryValidateReportAsync` → inline success (columns
  echoed) or the `Error` message in the existing error styling. `NameTaken` shows as a warning.
- **Step 5** "Save report" → `TryCreateReportAsync`; on success navigate to `/reports/{name}`;
  on 409 show the conflict inline at the name field; on 400 show the message.
  "Save & run now" → after 201, `TryRunReportAsync(name)` (exists since C2) → navigate to
  `/jobs/{jobId}`.
- All new copy en-US, sentence case, design-system components only (no new CSS unless a state has
  no existing component — prefer `Banner`).

### D6 tests

- Unit tests for `BuilderConfigMapper` (the important logic): full happy path snapshot (golden
  JSON), column ordering, omission of empty destinations, name passthrough. UI behavior verified
  via the preview tool per repo practice (document the checks performed in the PR description).

**Acceptance:** with sample `08-web-ui` + an in-memory/SQL source registered, a human can click
through Builder steps 1→5, save, land on the report detail, run it, and download the file — all
without touching code. With no engine, the Builder still demos on SampleData.

---

## D7 — UI: dashboard + run histories on real jobs

**Goal:** kill the biggest fake surfaces. Depends on D3.

- `TryListJobsAsync(status?, report?, since?, limit?)` in the API client → `ApiJobView[]`.
- **Dashboard**: recent-jobs strip = `TryListJobsAsync(limit: 8)`; metric cards computed
  client-side from `TryListJobsAsync(since: today-utc, limit: 200)`: jobs today (count), success
  rate (`Completed / terminal`), records exported (Σ `RecordsWritten`), avg duration (mean of
  `CompletedAt-StartedAt` over completed). Loading skeleton while fetching; `SampleData` fallback
  when null (engine down) — same pattern as every C2 page.
- **Report detail**: history table = `TryListJobsAsync(report: name, limit: 10)`, with
  status badge + link to the matching job screen (`/jobs/{id}` while running,
  `/jobs/completed/{id}` / `/jobs/failed/{id}` — the parameterized routes added in C2).
- Empty-state (0 jobs) uses the existing `EmptyState` component, distinct from the engine-down
  fallback.

**Acceptance:** with the sample host running and a couple of jobs triggered, dashboard numbers
and both tables visibly change; with the engine stopped, screens render SampleData exactly as
before this PR.

---

## D8 — UI: report detail, pipeline, delete, completed artifacts

**Goal:** consume D4 + D5.

- API client: `TryGetReportDetailAsync(name)`, `TryGetJobArtifactsAsync(id)`.
- **Report detail** renders real columns (name/type/display/format), formats, destinations,
  retry/failure strategy, and an **origin chip** (`code` / `config`). When `Deletable`, show a
  "Delete report" `Button` (danger variant) with the existing confirm pattern → 
  `TryDeleteReportAsync` → navigate to `/reports` on success.
- **Pipeline screen** builds its stages from the detail (source type when origin=config is not in
  the detail view — the pipeline card labels the source stage generically ("Source") unless a
  later change adds it; variant rows remain SampleData with the existing `mock` treatment).
- **Job completed** file list = real artifacts (name, human-readable size, mime icon); download
  button unchanged (`/download`); empty artifacts → keep current sample rendering only in
  fallback mode, otherwise an `EmptyState`.

**Acceptance:** create a report via the D6 Builder, run it, watch it complete, see its real file
with the real byte size on the completed screen, delete the report from its detail page, and see
it vanish from `/reports`.

---

## D9 — UI: sources page on capabilities

**Goal:** the Sources list stops being 100% fiction without inventing a source registry.

- Page shows one card per `capabilities.sources` entry (provider type), with a count of registered
  reports whose detail reports `Origin == "config"` and… **note:** per-report source type is not
  exposed (property-bag rule); v1 of this page shows provider cards + total dynamic-report count,
  no per-source health/latency (those stay mock, clearly badged as demo, or are dropped from the
  card — implementer's call, favor dropping fake numbers).
- Source **explorer** (`/sources/{name}/explore`) stays SampleData — post-MVP (schema/preview
  introspection is a security-sensitive feature, ADR required).
- Fallback: no capabilities ⇒ current SampleData page unchanged.

**Acceptance:** with the sample host, the page reflects exactly the providers the host registered;
without it, unchanged demo.

---

## Explicitly out of scope for this epic (do not implement)

- `PUT /api/reports/{name}` (edit) — needs a secrets round-trip story; post-epic ADR.
- Scheduling/recurring jobs (Hangfire `RecurringJob`) — separate ADR + epic.
- Real progress **percentage** (needs a source `COUNT`) — rejected; counters stay the truth.
- Source explorer introspection (schema browse / data preview) — separate ADR.
- Settings screens (alerts, auth chain, plugins, retention, audit) — post-MVP as per spec.
- Variants/coalescing on the pipeline screen (D23).
- Multi-worker semantics for the config store — v1 is single-server (file store is enough).

## Versioning / release notes

- `NeoReports.Core` and `NeoReports.AspNetCore`: additive public surface ⇒ **minor** bump
  (v1.2.0) when the epic ships. `NeoReports.Abstractions` unchanged.
- `CHANGELOG` entry per shipped PR as usual.
