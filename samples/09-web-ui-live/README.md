# Sample 09 — Web UI with a live engine

`samples/08-web-ui` hosts `NeoReports.UI` alone, so every screen shows its honest empty/"engine
unreachable" state (D36 — no mock data ships). This sample mounts the UI **and** the engine in
the same host — `NeoReports.Core` +
`NeoReports.AspNetCore`, with dynamic report registration enabled (`AddDynamicReports`, Epic D /
ADR D33) — so you can click through the real, end-to-end flow: register a report from the
Builder, validate it, save it, run it, download a real file, and delete it.

No external database or cloud account is needed: the only registered source (`InMemorySalesSourceProvider.cs`, id `"inmemory"`) generates synthetic rows shaped to whatever columns
you type into the Builder.

## Run

```bash
dotnet run --project samples/09-web-ui-live
```

Open the printed URL — the root redirects to **`/neoreports`**.

## Walkthrough

### 1. Confirm the engine is live

Go to **Builder** (`/neoreports/builder`). You should **not** see the "Demo mode" banner — its
absence means `GET /api/capabilities` succeeded and returned at least the `inmemory` source.

### 2. Register a report

- **Step 1 — Source**: the **Engine source type** card at the top has a real dropdown — pick
  `inmemory` (the only registered provider in this sample). The cards below it are illustrative
  only; picking one doesn't affect the report. Continue.
- **Step 2 — Configure**: fill in the **Engine configuration** card:
  - *Report name*: `my-first-report`
  - *Connection string variable*: leave empty (the in-memory source ignores it)
  - *SQL query*: anything — also ignored by the in-memory source, but required by the form
  - *Key column*: `Id`
  - *Page size*: `1000` (default is fine)
  - *Output columns*: `Id, Customer, Amount, CreatedAt`

  Click **Validate** — you should see a green "Valid" banner listing the four columns. This
  calls `POST /api/reports/validate`, which compiles the config without registering it.
- **Step 3 — Format**: CSV and XLSX are pre-selected; leave both (or pick just one).
- **Step 4 — Destination**: a real **Engine destination** card appears (only shown when the
  engine is reachable). Pick `local` and leave the default path template.
- **Step 5 — Review**: confirm the name is `my-first-report`, then:
  - **Save and schedule** → `POST /api/reports` (`201`) → navigates to `/reports/my-first-report`.
  - Or **Run now** → registers *and* immediately triggers a run → navigates to `/jobs/{id}`.

### 3. Watch it run and inspect the result

If you clicked *Run now*, the job finishes almost instantly (25 in-memory rows) and redirects to
`/jobs/completed/{id}`. The **Generated files** card lists the real file(s) — name, size, MIME
type — from `GET /api/jobs/{id}/artifacts`. Click **Download**.

Check the file landed on disk too:

```bash
ls samples/09-web-ui-live/out/
```

### 4. See it everywhere else in the UI

- **Reports** (`/reports`) — `my-first-report` is in the list.
- **Report detail** (`/reports/my-first-report`) — real columns, formats (`CSV`, `XLSX`),
  destination (`local`), retry policy, an **`origin: config`** chip, and run history. A
  **Delete report** button appears (only `config`-origin reports get one) — click it twice
  ("Delete report" → "Confirm delete") to remove it via `DELETE /api/reports/{name}`.
- **Dashboard** (`/`) — the job shows in *Recent jobs*, and the four metric cards (jobs today,
  success rate, records exported, avg duration) are computed from `GET /api/jobs`, not hardcoded.
- **Sources** (`/sources`) — an **Engine source types** card shows `inmemory`, from
  `GET /api/capabilities`.

### 5. What's not here anymore

As of D36, the UI no longer ships mock content that has no real backing — it was removed
outright rather than left as decoration. This includes Pipeline, Source explorer, the five
Settings screens, the decorative source/destination/format catalogs, and several cards embedded
in otherwise-real screens (Permissions, Recent changes, Schedule, Parameters, fake job
telemetry). See `docs/ui-removed-mock-content.md` for the full list, why each was removed, and
what a real version would need.

What's left cosmetic and *not* removed (each has a real reason, not just unfinished wiring):
- The running-job progress **percentage** stays a decorative animation by design (no
  server-side row total to compute a real one against); the counters below it are real.
- Scheduling (recurring runs) is deferred, not removed — see D35.

## Cleaning up

Registered reports persist under `./neoreports-configs/`; generated files under `./out/`.
Delete both directories to reset the sample to a clean state.
