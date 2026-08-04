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
- **Builder wizard** — the source picker is populated from the live engine, steps advance, and state
  survives Back navigation (the D69 regression, exercised over a real circuit).

Every test asserts Blazor's error overlay is **not** showing: a faulted circuit still returns HTTP 200
for the shell, so without that check a broken screen looks green.

## Known gap

The wizard is driven up to the Configure step; saving a brand-new report *through the wizard* (rather
than through the API) is not covered yet — each remaining step needs source-specific input. Report
creation itself is covered by the API-seeded flows above.
