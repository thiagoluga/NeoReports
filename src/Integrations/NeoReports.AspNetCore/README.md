# NeoReports.AspNetCore

ASP.NET Core endpoints to trigger and manage [NeoReports](https://github.com/thiagoluga/NeoReports)
reports and jobs, via Minimal API.

## Endpoints

`MapNeoReports("/api")` maps:

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/reports/{name}/run` | async → `202 { jobId }` |
| POST | `/reports/{name}/run?mode=sync` | stream a single output (multi-output → `400`) |
| GET | `/reports` | list registered reports |
| GET | `/reports/{name}` | full report definition — columns, formats, destinations, retry/failure strategy, origin (`code`/`config`) |
| GET | `/reports/{name}/config` | the stored config document, credential-bearing values replaced by `${neoreports:redacted}` (ADR D86) → `404` for a code-registered report |
| POST | `/reports` | register a report at runtime from a config document → `201` (`409` if the name exists, `400` if the config is invalid) |
| POST | `/reports/validate` | dry-run compile a config document → `200 { valid, error, name, columns, nameTaken }`; never registers or persists |
| PUT | `/reports/{name}` | replace a runtime-registered report in one step → `200` (`400` if the config is invalid — nothing is changed, `409` for a code-registered report, `404` if unknown) |
| DELETE | `/reports/{name}` | remove a runtime-registered report → `204` (`409` for a code-registered report, `404` if unknown) |
| GET | `/capabilities` | source/format/destination type ids the host has registered |
| GET | `/jobs` | list jobs, filterable by `status`/`report`/`since`, paged (`limit` ≤ 200, `offset`) |
| GET | `/jobs/{id}` | job status + stats |
| POST | `/jobs/{id}/cancel` | request cooperative cancellation |
| GET | `/jobs/{id}/download` | download the result (multi-output → zip) |
| GET | `/jobs/{id}/artifacts` | finished output files (name/mime/size, never the on-disk path); `[]` if not completed |

The `POST /reports`, `PUT /reports/{name}`, `POST /reports/validate`, `GET /reports/{name}/config`
and `DELETE /reports/{name}` endpoints require
`NeoReports.Core`'s `AddDynamicReports()` to be called (registers `IReportConfigStore` — see that
package's README); without it, those five routes fail DI resolution the same way any endpoint
does when a required service is missing.

### Editing a report

`GET /reports/{name}/config` returns the stored document with credential-bearing values replaced
by the reserved placeholder `${neoreports:redacted}`; sending that placeholder back on
`PUT /reports/{name}` restores the stored value. An editor can therefore change a page size without
the user having to retype a connection string, and without the secret ever leaving the host. A
`${VAR}` placeholder is not a secret and comes back verbatim.

`POST /reports/validate?for={name}` resolves the placeholder the same way, so a dry run means the
same thing while editing as it does while creating. `POST /reports` rejects the placeholder outright:
there is no stored document to resolve it against.

> **`POST`/`PUT`/`DELETE /reports` are credential-use-equivalent.** Redaction protects a secret's
> *value*, not its *authority*: a caller who sends the placeholder back can point the report's query
> or URL somewhere new while reusing a connection string they were never shown. The same is already
> true of a `${VAR}` placeholder (returned unredacted by design) and of a `${VAR}`-free registered
> source referenced by name (ADR D42) — so this is the API's trust model, not a property of editing.
> Gate the write endpoints with the authorization you would give the credentials themselves.

## Usage

```csharp
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;
using NeoReports.Core.DependencyInjection;

builder.Services.AddNeoReportsArtifacts();  // retain output files for download/sync
builder.Services.AddDynamicReports();       // optional: enables the runtime report endpoints above

var app = builder.Build();
app.MapNeoReports("/api");
```

Authorization inherits from the host; pass options to apply `RequireAuthorization` to the group.

## License

MIT © NeoReports Contributors
