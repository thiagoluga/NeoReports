# Sample 08 — Web UI

Hosts the NeoReports web UI (the `NeoReports.UI` Razor Class Library) in a minimal
ASP.NET Core app. The screens run on sample data; wiring the engine's real endpoints
is tracked as Epic C step C2 (see `docs/ui-handoff.md`).

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
