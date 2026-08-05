# NeoReports.WebUi.E2ETests

End-to-end tests that **boot the real application** — the Blazor UI and the engine in one host, wired
exactly like `samples/09-web-ui-live` — and drive it with a **real Chromium** through Playwright.

## Why a browser is required

The UI is Blazor **Server**: every click, every wizard step and every job refresh travels over a
SignalR circuit. `TestServer` has no port and cannot carry one, so `WebApplicationFactory` can't be
used here — the host runs on real Kestrel (port `0`, so parallel runs can't collide) and the tests
connect to it the way a user's browser does.

This complements, rather than replaces, the two suites that already exist:

| Suite | Boots the app? | Real clicks? | Catches |
|---|---|---|---|
| `NeoReports.UI.UnitTests` (bUnit) | no | no (component-level) | component logic, in milliseconds |
| `NeoReports.AspNetCore.IntegrationTests` | yes (TestServer) | no | endpoint behaviour |
| **this suite** | **yes (Kestrel)** | **yes** | DI/hosting wiring, static assets, circuit faults, real flows |

## Running locally

The browser binaries are not part of the NuGet restore — install them once:

```bash
dotnet build tests/NeoReports.WebUi.E2ETests
pwsh tests/NeoReports.WebUi.E2ETests/bin/Debug/net8.0/playwright.ps1 install chromium
dotnet test tests/NeoReports.WebUi.E2ETests
```

Without that install the tests **skip** rather than fail, so a contributor who hasn't run it can still
run the rest of the repo's suites. CI and the release workflow install Chromium *and* set
`NEOREPORTS_REQUIRE_BROWSER=1`, which turns a browser that won't launch into a hard failure — a
successful install is not proof it runs, and without that a driver mismatch would skip most of this
suite while the build stayed green. Same stance, same reasoning, as `tests/Shared/DockerGate.cs`.

## What is covered

- **Boot** — the host listens, the root redirects into the UI, the Blazor shell is served, the UI
  package's own `_content/` assets are served, and the engine API is mounted in the same host (checked
  over plain HTTP first, so a hosting failure reports itself instead of surfacing as a browser
  timeout).
- **Every screen** — Dashboard, Reports, Jobs, Sources, Memory and the Builder load in the browser
  with their heading, and left-nav routing works client-side.
- **Generating a report** — search for a report, click **Run**, the job reaches *Completed*, the Jobs
  screen shows it in that report's own row, and the artifact endpoint serves non-empty bytes.
- **Multi-format delivery** — a report declaring `csv` + `xlsx` produces both artifacts.
- **Report detail** — clicking through shows the declared columns.
- **Builder wizard, end to end** — a report is **created through the wizard itself** (source → name and
  columns → formats → destination → Save), then verified against the engine's API and *run*, producing
  a downloadable file. Also: the source picker is populated from the live engine, and wizard state
  survives Back navigation (the D69 regression, exercised over a real circuit).
- **Report shapes** — each asserting the bytes that came out, not just that the job completed:
  every column type round-trips into the CSV; a 250-row report at 10 rows per page delivers all 250
  with no duplicates (25 real batches through the pagination loop); a zero-row report still yields a
  well-formed header-only file; an `.xlsx` opens as a valid package with a worksheet part; a report
  with no destination still produces a downloadable artifact; and a `csv`+`xlsx` report downloads as a
  zip whose two files agree on the row count.

Every test asserts Blazor's error overlay is **not** showing: a faulted circuit still returns HTTP 200
for the shell, so without that check a broken screen looks green.

## Scope note

Scenarios that only need a particular *report shape* register it through the engine's API and then
assert the delivered bytes — the UI is not the thing under test there, and driving the wizard five
steps deep for each shape would trade real coverage for a slower, more brittle suite. The wizard
itself has its own end-to-end test that creates and saves a report entirely through the browser.
