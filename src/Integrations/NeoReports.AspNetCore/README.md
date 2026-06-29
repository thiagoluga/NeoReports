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
| GET | `/jobs/{id}` | job status + stats |
| POST | `/jobs/{id}/cancel` | request cooperative cancellation |
| GET | `/jobs/{id}/download` | download the result (multi-output → zip) |

## Usage

```csharp
using NeoReports.AspNetCore;
using NeoReports.AspNetCore.DependencyInjection;

builder.Services.AddNeoReportsArtifacts(); // retain output files for download/sync

var app = builder.Build();
app.MapNeoReports("/api");
```

Authorization inherits from the host; pass options to apply `RequireAuthorization` to the group.

## License

MIT © NeoReports Contributors
