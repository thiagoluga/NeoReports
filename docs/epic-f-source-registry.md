# Epic F — Source registry (named source instances + on-demand health)

> **Status: approved blueprint, not yet built.** Scope authorized by the maintainer (ADR **D42**,
> 2026-07). Packaging is settled: **MIT / OSS Core** (maintainer decision — not a Pro feature).
> Depends on nothing in Epic E; can start in parallel, but Epic E is expected to land first.
>
> This is the engine's first *instance*-level source concept: today `GET /api/capabilities`
> reports provider **types** ("sql", "inmemory"); nothing anywhere names a *particular* connection.
> The removed UI content this backs: the Sources-list registered-sources grid + health strip, the
> Dashboard "Most used" card, and the Builder's source picker
> (see `docs/ui-removed-mock-content.md`).
>
> When something here conflicts with the code you find, **stop and re-read the code first** — the
> code wins, then update this doc in the same PR.

## Locked design decisions (maintainer, 2026-07 — do not re-litigate)

1. **MIT.** Registry, health contracts, and the SQL health check ship in the OSS packages.
2. **Run-time property resolution for `Ref`**: the compiler checks existence at compile time but
   resolves the definition's properties **per run**, so editing a source (rotating a connection
   string) takes effect on the next run of every referencing report without recompiles.
3. **`PUT /api/sources/{name}` is full-replace and allowed.** Unlike report edit (punted, D33(f)),
   there is no secrets round-trip problem: GET never returns properties, so the client always
   re-sends the complete definition with `${VAR}` placeholders — nothing secret ever comes back
   down.
4. **Typed-path support is in scope**: `Source.Sql("sales-db")`-style by-name authoring for
   code-first reports (F5).

## Ground rules

Same as Epic D/E: `Abstractions` only via one additive trailing optional record parameter
(`SourceConfig.Ref`); every new interface in Core or the source packages; **GET never returns
`Properties`** — this is where the actual secrets live, the D33 property-bag rule at its most
literal; source names are filenames — validate with the shared `DynamicReportName` regex
(`^[a-zA-Z][a-zA-Z0-9_-]{0,99}$`), reject not sanitize; `${VAR}` secret handling **reuses
`ReportConfigEnvironment.Substitute`'s mechanism** (whole-value placeholders) — no second secret
story.

---

## F1 — Core: source definitions, store, registry service

### Model + store (`src/NeoReports.Core/SourceRegistry/`)

```csharp
/// <summary>A named, persisted source instance (ADR D42) — a specific connection, distinct from
/// the provider *type* capabilities report. Properties may hold ${VAR} placeholders (D33).</summary>
public sealed record SourceDefinition(
    string Name,
    string Type,
    IReadOnlyDictionary<string, object?>? Properties = null,
    string? Description = null);

public interface ISourceRegistryStore
{
    Task SaveAsync(SourceDefinition definition, CancellationToken cancellationToken);   // create or replace
    Task<SourceDefinition?> GetAsync(string name, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceDefinition>> ListAsync(CancellationToken cancellationToken);
}
```

`FileSourceRegistryStore`: one `{dir}/{name}.json` per source (default dir
`./neoreports-sources`), atomic tmp-then-move writes, name re-validated defensively — the exact
`FileReportConfigStore` pattern. Corrupt files at load: log (sanitized) and skip, never crash.

### Resolution service

`ISourceRegistry` (Core, thin over the store): `ResolveAsync(name)` → the definition with
`${VAR}` placeholders substituted **at resolve time** (locked decision 2 — substitution moves
per-run for refs; a missing env var fails the *run* with the same naming error the compile-time
path gives today). In-memory read-through cache invalidated on save/delete (single server, D2 —
no cross-process invalidation needed).

### Reference counting (computed, never tracked)

`CompiledReport` gains public `string? SourceRef`. "Used in N reports" = count of registered
reports with `SourceRef == name`. Code-first reports using F5's by-name authoring also populate
it; inline sources leave it null. No usage store exists — the number is always derivable truth.

### F1 tests

Store roundtrip/replace/delete/invalid-name/corrupt-file-skip; registry resolve substitutes
placeholders per call (change the env var between calls, see the new value); cache invalidation
on save.

---

## F2 — Compiler + dynamic path: `SourceConfig.Ref`

### Abstractions (additive)

`SourceConfig` gains a trailing optional `string? Ref`. Semantics:

- `Ref` set: the report's source is the registered definition. `Type` may be omitted (taken from
  the definition); when both present they **must match** (`ConfigurationException`). The report's
  own `Properties` **overlay** the definition's (report-local wins — the SQL query is
  per-report; the connection string comes from the source).
- `Ref` null: exactly today's behavior. **Inlining stays fully supported** — this is additive;
  every existing document compiles unchanged.

### Compiler behavior (the run-time-resolution mechanics)

- Compile time: `Ref` present ⇒ verify the definition exists (via `ISourceRegistry`) and the
  merged `Type` resolves to a registered `IConfigSourceProvider` — fail fast with clear messages
  (`ConfigurationException` ⇒ 400 through the existing endpoints). **Do not bake the definition's
  properties into the compiled report.**
- Run time: the compiled reader factory, when built from a ref, resolves the definition through
  `ISourceRegistry` **per run**, merges (definition base, report overlay, then `${VAR}`
  substitution on the merged bag), and only then calls `IConfigSourceProvider.Create` —
  **providers are untouched**; they receive an ordinary fully-merged `SourceConfig`.
- Hydration ordering: sources hydrate **before** dynamic reports (a stored report may ref a
  stored source) — sequence the hydrators explicitly in the DI wiring.

### F2 tests

Merge matrix (type from def / both matching / mismatch error / unknown ref error / overlay
precedence); env var changed between two runs of the same compiled report ⇒ second run sees the
new value (the headline test for locked decision 2); source deleted after compile ⇒ next run
fails with a clear "source no longer registered" error; existing inline-source tests untouched.

---

## F3 — AspNetCore: CRUD + health endpoints

All inside the `MapNeoReports` group. Response record (no `Properties`, ever):

```csharp
public sealed record SourceView(
    string Name, string Type, string? Description,
    int ReferencedByCount,
    string? LastHealthStatus,        // "healthy" | "unhealthy" | null (never checked)
    string? LastHealthError,
    double? LastHealthLatencyMs,
    DateTimeOffset? LastCheckedAt);
```

- `GET {prefix}/sources` → 200 list (empty ok). `GET {prefix}/sources/{name}` → 200 / 404.
- `POST {prefix}/sources` (body: name/type/properties/description) → 201 + Location; 400 invalid
  name or unknown provider type (reject, consistent with report compile); 409 duplicate.
- `PUT {prefix}/sources/{name}` → full replace (locked decision 3) → 200; 404 unknown; 400 same
  validations; name in path wins (body name mismatch ⇒ 400). Takes effect on the next run of
  every referencing report (run-time resolution) — say so in the XML docs.
- `DELETE {prefix}/sources/{name}` → **409 while any registered report references it**
  (referential integrity — a scheduled report failing days later with a mystery error is the
  alternative); else 204. 404 unknown.
- `POST {prefix}/sources/{name}/health` → runs the check **now**: 200 with the result (also
  cached in memory + timestamped, which is what GET's `LastHealth*` reflects); 404 unknown
  source; 422 when no health check is registered for the type ("health check not supported for
  this source type" — itself an honest state). **On-demand only — no background poller** (a
  stale reading shown as current is the D36 fabricated-telemetry pattern; "never checked" is a
  first-class state).

Request bodies obviously carry `Properties` (that's how secrets go *in*, as `${VAR}`
placeholders — hint this in the UI); responses never do. Regression-test for no `"properties"`
key, as D4 did.

### Health contract (Core + source packages)

```csharp
public interface ISourceHealthCheck
{
    /// <summary>Provider type id this check handles (matches IConfigSourceProvider.Type).</summary>
    string Type { get; }
    Task<SourceHealthResult> CheckAsync(SourceDefinition definition, IServiceProvider services, CancellationToken cancellationToken);
}

public sealed record SourceHealthResult(bool Healthy, string? Error, TimeSpan Latency);
```

Resolved from DI by type exactly like `IConfigSourceProvider` — provider-type-extensible, nothing
SQL-specific in Core. First implementation in `NeoReports.Sources.Sql`: open connection +
`SELECT 1`, measured latency, bounded by a short timeout (e.g. 10s) so the endpoint can't hang.
The check receives the **substituted** definition (via `ISourceRegistry.ResolveAsync`).

### F3 tests

Integration: CRUD happy paths + every error case above; delete blocked while a POSTed report refs
the source, allowed after the report is deleted; health 200/404/422 (in-memory provider has no
check registered ⇒ 422; then register a fake check ⇒ 200); GET carries the cached result +
timestamp after a POST /health; no `"properties"` key anywhere (regression guard).

---

## F4 — UI: sources screens on the registry

- **SourcesList**: the registered-sources grid returns, real — name, type, description,
  referenced-by count (labeled "config reports" — code-first refs count only when authored via
  F5, whose `SourceRef` also surfaces), last health (status + latency + checked-at) and a
  **"Check now"** button per card. The health count-strip aggregates only actual results:
  "2 healthy · 1 unhealthy · 3 never checked" — never a fabricated aggregate. "Add source" form
  (name, type from capabilities, properties key/value rows with a `${VAR}` hint, description) →
  `POST /sources`; edit → full-replace `PUT` (form starts **empty of properties** — they are not
  retrievable, say so: "Properties are write-only; re-enter them to change the source."); delete
  with the two-click confirm pattern (D8), 409 shown inline naming the referencing count.
  The "Engine source types" capability section (D9) stays, above.
- **Builder step 1/2**: "Use a registered source" picker (cards from `GET /sources`) that sets
  `source.ref` in the mapped config (and hides the inline connection fields; query/key/page-size
  stay — they're report-local overlay), alongside the existing inline path. `BuilderConfigMapper`
  gains the `ref` field; `BuilderState` gains `SourceRef`.
- **Dashboard "Most used" card**: returns ranked by `ReferencedByCount`, labeled by what it is
  ("Most referenced sources"). Empty registry ⇒ card hidden or `EmptyState` (favor hiding —
  consistent with D9's sources section rendering only when data exists).
- Source **explorer** (`/sources/{name}/explore`) stays out — unchanged; schema/data
  introspection still needs its own ADR (D33/D36 stance).
- Honest states everywhere: engine unreachable ⇒ standard banner; empty registry ⇒ EmptyState
  ("No sources registered yet") + the add form.

---

## F5 — Typed path: by-name SQL source

In scope per the maintainer (locked decision 4). Constraint discovered in code: typed sources are
constructed by static entry points (`Source.Sql(connString, sql)`) inside registration lambdas
with **no `IServiceProvider`**, and `IBatchSource<T>.ReadAsync` has no DI access either — so the
by-name source must have its resolver **injected at compile time** by the Core builder, while the
actual registry lookup still happens **per run** (locked decision 2 applies to both paths).

- New entry point (unambiguous — a `(string, string)` overload of `Source.Sql` cannot distinguish
  a name from a connection string): `Source.SqlNamed("sales-db", sql).Keyset(key, pageSize)`.
  It produces a source spec that declares a **registry dependency** instead of a connection
  string.
- Core: the builder/compile step (which does have the root `IServiceProvider` —
  `GetOrAddRegistry`'s factory) wires a `Func<CancellationToken, Task<SourceDefinition>>`
  resolver (closing over `ISourceRegistry`) into the spec; the SQL source calls it at the start
  of each run to obtain the current, substituted connection string. Registering a
  `SqlNamed`-based report in a host without the source registry configured ⇒ clear
  `ConfigurationException` at registration.
- `CompiledReport.SourceRef` is populated, so typed by-name reports count in
  `ReferencedByCount` and block source deletion (F3's 409) like config reports.
- Exact spec/wiring shape is the implementer's call — the contract that matters: **per-run
  resolution, no `IServiceProvider` on the source's read path, providers/Abstractions untouched.**

### F5 tests

E2E (Testcontainers, like D13's): register a source definition pointing at the container, author
a typed report via `SqlNamed`, run — rows flow; change the definition's connection string to a
second container between runs ⇒ next run reads from the new one (run-time resolution proof);
missing registry ⇒ registration-time error.

---

## Task order

F1 → F2 → F3 → F4, then F5 (independent of F4). One PR each; F3 may split (CRUD / health) if it
grows.

## Explicitly out of scope

- Background/scheduled health polling (on-demand only — revisit needs its own decision).
- Source explorer / schema introspection (own ADR required, unchanged).
- Per-source usage *telemetry* (run counts, last-used-at) — `ReferencedByCount` is derivable
  truth; anything tracked is a new decision.
- Cross-process cache invalidation / multi-server semantics (D2: single server).
- Non-SQL health checks beyond what each source package ships (the contract is open; shipping
  more checks is per-package work, not this epic).

## Versioning

`Abstractions`: one additive trailing optional param (`SourceConfig.Ref`) ⇒ SemVer-minor (D25).
Everything else Core / AspNetCore / Sources.Sql / UI, additive ⇒ minor.
