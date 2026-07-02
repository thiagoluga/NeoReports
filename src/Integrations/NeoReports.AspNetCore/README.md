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
| POST | `/reports` | register a report at runtime from a config document → `201` (`409` if the name exists, `400` if the config is invalid) |
| POST | `/reports/validate` | dry-run compile a config document → `200 { valid, error, name, columns, nameTaken }`; never registers or persists |
| DELETE | `/reports/{name}` | remove a runtime-registered report → `204` (`409` for a code-registered report, `404` if unknown) |
| GET | `/capabilities` | source/format/destination type ids the host has registered |
| GET | `/jobs/{id}` | job status + stats |
| POST | `/jobs/{id}/cancel` | request cooperative cancellation |
| GET | `/jobs/{id}/download` | download the result (multi-output → zip) |

The `POST /reports`, `POST /reports/validate`, and `DELETE /reports/{name}` endpoints require
`NeoReports.Core`'s `AddDynamicReports()` to be called (registers `IReportConfigStore` — see that
package's README); without it, those three routes fail DI resolution the same way any endpoint
does when a required service is missing.

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
