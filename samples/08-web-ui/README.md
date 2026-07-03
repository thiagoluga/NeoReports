# Sample 08 — Web UI

Hosts the NeoReports web UI (the `NeoReports.UI` Razor Class Library) alone, in a minimal
ASP.NET Core app — no engine mounted, so every screen shows its honest empty/"engine
unreachable" state rather than fabricated data (D36 — the UI no longer ships mock content;
see `docs/ui-removed-mock-content.md`). See `docs/ui-handoff.md` for exactly what's wired vs.
still `mock/future`, and **`samples/09-web-ui-live`** for the same UI mounted together with a
live engine, so you can click through the real, end-to-end flow (register a report, run it,
download a real file).

## Run

```bash
dotnet run --project samples/08-web-ui
```

Open the printed URL — the root redirects to **`/neoreports`**.

## Mounting the UI in your own host

```csharp
builder.Services.AddNeoReportsUI();
...
app.UseNeoReportsUI("/neoreports");
```

Everything (routes, static assets, the Blazor hub) lives under the base path, so the
host's own endpoints are untouched.

## Customizing the URL

The base path is any non-root path. This sample reads it from configuration:

```bash
dotnet run --project samples/08-web-ui -- --NeoReports:UIPath=/reports-admin
```

or set `NeoReports:UIPath` in `appsettings.json`.
